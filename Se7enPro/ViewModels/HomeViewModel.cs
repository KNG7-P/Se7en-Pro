using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Se7enPro.Models;
using Se7enPro.Services;

namespace Se7enPro.ViewModels;

public sealed partial class HomeViewModel : PageViewModelBase
{
    private readonly ITunnelCoreManager _tunnel;
    private readonly ISettingsService _settings;
    private readonly ITunManager _tun;
    private readonly DispatcherTimer _uptimeTimer;
    private DateTime? _connectedAt;

    private long _lastSentSample;
    private long _lastReceivedSample;
    private DateTime? _lastSampleAt;
    private double _downBytesPerSec;
    private double _upBytesPerSec;

    private bool _suppressRegionSideEffects;
    private int _bytesUpdateQueued;

    public override string Title => "Home";
    public override string Route => "home";
    public override string Icon => "Home";

    public HomeViewModel(
        ITunnelCoreManager tunnel,
        ISettingsService settings,
        ITunManager tun)
    {
        _tunnel = tunnel;
        _settings = settings;
        _tun = tun;

        _tunModeEnabled = AdminElevation.IsAdministrator()
            && _settings.Settings.SystemWideTunneling;

        _selectedEgressRegion = ReadRegionFromSettings();

        _tunnel.StateChanged += (_, s) => Post(() => ApplyState(s));
        _tunnel.ConnectProgressChanged += (_, _) => Post(() =>
        {
            OnPropertyChanged(nameof(ConnectingProgress));
            OnPropertyChanged(nameof(ConnectingProgressText));
        });
        _tunnel.BytesTransferredChanged += (_, _) =>
        {

            if (Interlocked.Exchange(ref _bytesUpdateQueued, 1) == 1) return;
            Post(() =>
            {
                Interlocked.Exchange(ref _bytesUpdateQueued, 0);
                ApplyBytes();
            });
        };
        _tunnel.RouteChanged += (_, _) => Post(() =>
        {
            OnPropertyChanged(nameof(CurrentRouteIp));
            OnPropertyChanged(nameof(CurrentRouteSni));
            OnPropertyChanged(nameof(HasCurrentRoute));
            OnPropertyChanged(nameof(HasRouteIp));
            OnPropertyChanged(nameof(HasRouteSni));
            OnPropertyChanged(nameof(ServerRegionCode));
            OnPropertyChanged(nameof(ServerRegionName));
            OnPropertyChanged(nameof(HasRegion));
            OnPropertyChanged(nameof(HasServerRegion));
            CopyCurrentIpCommand.NotifyCanExecuteChanged();
            CopyCurrentSniCommand.NotifyCanExecuteChanged();
        });
        _tunnel.NoticeReceived += (_, n) =>
        {

            if (n.NoticeType != "ClientRegion" && n.NoticeType != "ConnectedServerRegion")
                return;
            Post(() =>
            {
                OnPropertyChanged(nameof(ServerRegionCode));
                OnPropertyChanged(nameof(ServerRegionName));
                OnPropertyChanged(nameof(HasRegion));
                OnPropertyChanged(nameof(HasServerRegion));
            });
        };

        _tun.StateChanged += (_, _) => Post(() =>
        {
            OnPropertyChanged(nameof(TunStatusText));
            OnPropertyChanged(nameof(TunStatusBrush));
            OnPropertyChanged(nameof(TunHasMessage));
        });

        _settings.SettingsChanged += (_, _) => Post(() =>
        {
            var externalMethod = ConnectionMethodExtensions.ParseConnectionMethod(_settings.Settings.ConnectionMethod).ToToken();
            if (!string.Equals(SelectedConnectionMethod, externalMethod, StringComparison.Ordinal))
            {
                _suppressMethodSideEffects = true;
                try { SelectedConnectionMethod = externalMethod; }
                finally { _suppressMethodSideEffects = false; }
            }

            OnPropertyChanged(nameof(ActiveMethodName));
            RaiseMethodDependentProperties();

            if (_settings.Settings.SystemWideTunneling != TunModeEnabled)
            {
                _suppressTunSideEffects = true;
                try { TunModeEnabled = _settings.Settings.SystemWideTunneling; }
                finally { _suppressTunSideEffects = false; }
            }

            var externalRegion = ReadRegionFromSettings();
            if (!string.Equals(SelectedEgressRegion, externalRegion, StringComparison.Ordinal))
            {
                _suppressRegionSideEffects = true;
                try { SelectedEgressRegion = externalRegion; }
                finally { _suppressRegionSideEffects = false; }
            }
        });

        _uptimeTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _uptimeTimer.Tick += (_, _) =>
        {
            ApplyBytes();
            OnPropertyChanged(nameof(UptimeText));
        };

        _selectedConnectionMethod = ConnectionMethodExtensions.ParseConnectionMethod(_settings.Settings.ConnectionMethod).ToToken();
        ApplyState(_tunnel.State);
    }

    [ObservableProperty]
    private ConnectionState _state = ConnectionState.Disconnected;

    [ObservableProperty]
    private string _statusText = "Disconnected";

    [ObservableProperty]
    private string _statusDetail = "Tap the button to connect";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _tunModeEnabled;

    public ObservableCollection<Country> EgressRegions { get; } =
    CountryHelper.BuildSeedRegions();

    [ObservableProperty]
    private string _selectedEgressRegion = "auto";

    public IReadOnlyList<ConnectionMethodOption> ConnectionMethods => ConnectionMethodExtensions.AllOptions;

    [ObservableProperty]
    private string _selectedConnectionMethod = "psiphon";

    private bool _suppressMethodSideEffects;

    public bool CanChangeMethod => State != ConnectionState.Connecting && State != ConnectionState.Disconnecting;

    partial void OnSelectedConnectionMethodChanged(string value)
    {
        if (_suppressMethodSideEffects) return;
        var token = ConnectionMethodExtensions.ParseConnectionMethod(value).ToToken();
        _settings.Settings.ConnectionMethod = token;
        _settings.Save();
        OnPropertyChanged(nameof(ActiveMethodName));
        RaiseMethodDependentProperties();

        if (State == ConnectionState.Connected)
        {
            _ = Task.Run(async () =>
            {
                try { await _tunnel.RestartAsync(); } catch { }
            });
        }
    }

    public ConnectionMethod CurrentMethod =>
        ConnectionMethodExtensions.ParseConnectionMethod(_settings.Settings.ConnectionMethod);

    private string ReadRegionFromSettings()
    {
        var s = _settings.Settings;
        var code = CurrentMethod switch
        {
            ConnectionMethod.Tor or ConnectionMethod.TorOverWarp => s.TorExitCountry,
            ConnectionMethod.Psiphon or ConnectionMethod.PsiphonOverWarp => s.EgressRegion,
            _ => "auto",
        };
        if (string.IsNullOrWhiteSpace(code) || string.Equals(code, "auto", StringComparison.OrdinalIgnoreCase))
            return "auto";
        return code.Trim().ToUpperInvariant();
    }

    partial void OnSelectedEgressRegionChanged(string value)
    {
        if (_suppressRegionSideEffects) return;

        var isAuto = string.IsNullOrWhiteSpace(value) || string.Equals(value, "auto", StringComparison.OrdinalIgnoreCase);
        var s = _settings.Settings;
        switch (CurrentMethod)
        {
            case ConnectionMethod.Tor or ConnectionMethod.TorOverWarp:
                s.TorExitCountry = isAuto ? "" : value.ToLowerInvariant();
                break;
            case ConnectionMethod.Psiphon or ConnectionMethod.PsiphonOverWarp:
                s.EgressRegion = isAuto ? "" : value.ToUpperInvariant();
                break;
            default:
                return;
        }

        _settings.Save();
        _ = _tunnel.RestartAsync();
    }

    public bool ShowRegionPicker =>
        CurrentMethod is ConnectionMethod.Psiphon
                      or ConnectionMethod.Tor
                      or ConnectionMethod.PsiphonOverWarp
                      or ConnectionMethod.TorOverWarp;

    public string RegionPickerTitle =>
        CurrentMethod is ConnectionMethod.Tor or ConnectionMethod.TorOverWarp ? "Exit country" : "Region";

    public string RegionPickerHint => CurrentMethod is ConnectionMethod.Tor or ConnectionMethod.TorOverWarp
        ? "Tor exit relay country. Fewer relays in a country means slower circuits."
        : "Where your traffic exits. Auto picks the fastest server.";

    public bool ShowTrafficStats => true;

    public bool HasRouteIp =>
        CurrentMethod is not (ConnectionMethod.Tor or ConnectionMethod.TorOverWarp) && !string.IsNullOrEmpty(_tunnel.CurrentRouteIp);

    public bool HasRouteSni => !string.IsNullOrEmpty(_tunnel.CurrentRouteSni);

    public bool HasRouteIpAndSni => HasRouteIp && HasRouteSni;

    public bool HasRouteSniOnly => !HasRouteIp && HasRouteSni;

    public string RouteIpLabel => CurrentMethod switch
    {
        ConnectionMethod.Psiphon or ConnectionMethod.PsiphonOverWarp => "IP",
        _ => "EDGE",
    };

    public string RouteIpTooltip => CurrentMethod switch
    {
        ConnectionMethod.Psiphon or ConnectionMethod.PsiphonOverWarp => "Edge IP tunnel-core is currently routing through",
        _ => "Cloudflare edge address this session is bound to",
    };

    public string RouteSniLabel => CurrentMethod switch
    {
        ConnectionMethod.Psiphon or ConnectionMethod.PsiphonOverWarp => "SNI",
        ConnectionMethod.Tor or ConnectionMethod.TorOverWarp => "STATUS",
        _ => "MODE",
    };

    public string RouteSniTooltip => CurrentMethod switch
    {
        ConnectionMethod.Psiphon or ConnectionMethod.PsiphonOverWarp => "SNI hostname tunnel-core is currently presenting to the CDN",
        ConnectionMethod.Tor or ConnectionMethod.TorOverWarp => "Tor bootstrap progress",
        _ => "Transport and obfuscation profile in use",
    };

    private void RaiseMethodDependentProperties()
    {
        OnPropertyChanged(nameof(CurrentMethod));
        OnPropertyChanged(nameof(ShowRegionPicker));
        OnPropertyChanged(nameof(RegionPickerTitle));
        OnPropertyChanged(nameof(RegionPickerHint));
        OnPropertyChanged(nameof(ShowTrafficStats));
        OnPropertyChanged(nameof(HasRouteIp));
        OnPropertyChanged(nameof(HasRouteSni));
        OnPropertyChanged(nameof(HasRouteIpAndSni));
        OnPropertyChanged(nameof(HasRouteSniOnly));
        OnPropertyChanged(nameof(RouteIpLabel));
        OnPropertyChanged(nameof(RouteIpTooltip));
        OnPropertyChanged(nameof(RouteSniLabel));
        OnPropertyChanged(nameof(RouteSniTooltip));
        OnPropertyChanged(nameof(IsUsingUpstreamProxy));
        OnPropertyChanged(nameof(ShowProxyBadge));
        OnPropertyChanged(nameof(ProxyDisplay));
        CopyCurrentIpCommand.NotifyCanExecuteChanged();
        CopyCurrentSniCommand.NotifyCanExecuteChanged();
    }

    public bool IsAdminElevated { get; } = AdminElevation.IsAdministrator();

    private bool _suppressTunSideEffects;

    [RelayCommand]
    private void RestartAsAdmin()
    {
        _settings.Settings.SystemWideTunneling = true;
        _settings.Save();
        AdminElevation.TryRestartElevated();
    }

    partial void OnTunModeEnabledChanged(bool value)
    {
        if (_suppressTunSideEffects) return;

        if (value && !IsAdminElevated)
        {
            var res = MessageBox.Show(
                "System-wide tunneling needs Administrator privileges to install and configure "
              + "the virtual network adapter (WinTUN).\n\n"
              + "Would you like to restart Se7en Pro as Administrator now?",
                "Administrator privileges required",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (res == MessageBoxResult.Yes)
            {
                _settings.Settings.SystemWideTunneling = true;
                _settings.Save();
                if (AdminElevation.TryRestartElevated())
                {
                    return;
                }
            }

            _suppressTunSideEffects = true;
            try { TunModeEnabled = false; }
            finally { _suppressTunSideEffects = false; }
            return;
        }

        _settings.Settings.SystemWideTunneling = value;
        _settings.Save();
    }

    public string TunStatusText
    {
        get
        {

            if (!IsAdminElevated)
                return "Run Se7en Pro as Administrator to enable system-wide tunneling.";

            return _tun.State switch
            {
                TunState.Starting => "Starting TUN…",
                TunState.Running => "All traffic is routed through Se7en Pro.",
                TunState.Stopping => "Stopping TUN…",
                TunState.Error => _tun.LastError ?? "TUN failed to start.",
                _ => TunModeEnabled
                    ? "Will start automatically when Se7en Pro connects."
                    : "Only apps that honor the system proxy will use Se7en Pro.",
            };
        }
    }

    private static readonly SolidColorBrush TunBrushRunning = MakeFrozen("#22C55E");
    private static readonly SolidColorBrush TunBrushTransition = MakeFrozen("#F59E0B");
    private static readonly SolidColorBrush TunBrushError = MakeFrozen("#EF4444");
    private static readonly SolidColorBrush TunBrushIdle = MakeFrozen("#6B7280");

    private static SolidColorBrush MakeFrozen(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        b.Freeze();
        return b;
    }

    public Brush TunStatusBrush => _tun.State switch
    {
        TunState.Running => TunBrushRunning,
        TunState.Starting or TunState.Stopping => TunBrushTransition,
        TunState.Error => TunBrushError,
        _ => TunBrushIdle,
    };

    public bool TunHasMessage => true;

    public int HttpProxyPort => _tunnel.HttpProxyPort;
    public int SocksProxyPort => _tunnel.SocksProxyPort;

    public string ServerRegionCode => _tunnel.ConnectedServerRegion;

    public string ServerRegionName =>
    string.IsNullOrEmpty(_tunnel.ConnectedServerRegion)
        ? "—"
        : CountryHelper.FullName(_tunnel.ConnectedServerRegion);

    public bool HasRegion =>
    !string.IsNullOrEmpty(_tunnel.ConnectedServerRegion)
    && CountryHelper.HasFlag(_tunnel.ConnectedServerRegion);

    public bool HasServerRegion => !string.IsNullOrEmpty(_tunnel.ConnectedServerRegion);

    public string ActiveMethodName =>
        ConnectionMethodExtensions
            .ParseConnectionMethod(_settings.Settings.ConnectionMethod)
            .ToDisplayName();

    public bool IsUsingUpstreamProxy =>
        CurrentMethod == ConnectionMethod.Psiphon
        && _settings.Settings.UpstreamProxyEnabled
        && !string.IsNullOrWhiteSpace(_settings.Settings.UpstreamProxy);

    public bool ShowProxyBadge =>
        IsUsingUpstreamProxy && State == ConnectionState.Connected;

    public string ProxyDisplay => BuildProxyDisplay(
        _settings.Settings.UpstreamProxy, _settings.Settings.UpstreamProxyScheme);

    private static string BuildProxyDisplay(string? raw, string? scheme)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var body = raw.Trim();

        var si = body.IndexOf("://", StringComparison.Ordinal);
        if (si >= 0)
        {
            if (string.IsNullOrWhiteSpace(scheme)) scheme = body[..si];
            body = body[(si + 3)..];
        }

        var at = body.LastIndexOf('@');
        if (at >= 0) body = body[(at + 1)..];

        var slash = body.IndexOf('/');
        if (slash >= 0) body = body[..slash];

        scheme = string.IsNullOrWhiteSpace(scheme) ? "proxy" : scheme.Trim().ToLowerInvariant();
        return string.IsNullOrEmpty(body) ? "" : $"{scheme}://{body}";
    }

    public string HttpProxyEndpoint =>
    _tunnel.HttpProxyPort > 0 ? $"127.0.0.1:{_tunnel.HttpProxyPort}" : "—";

    public string SocksProxyEndpoint =>
        _tunnel.SocksProxyPort > 0 ? $"127.0.0.1:{_tunnel.SocksProxyPort}" : "—";

    public string CurrentRouteIp =>
        string.IsNullOrEmpty(_tunnel.CurrentRouteIp) ? "—" : _tunnel.CurrentRouteIp;

    public string CurrentRouteSni =>
        string.IsNullOrEmpty(_tunnel.CurrentRouteSni) ? "—" : _tunnel.CurrentRouteSni;

    public bool HasCurrentRoute =>
        !string.IsNullOrEmpty(_tunnel.CurrentRouteIp) || !string.IsNullOrEmpty(_tunnel.CurrentRouteSni);

    public string UptimeText
    {
        get
        {
            if (_connectedAt is null) return "—";
            var span = DateTime.UtcNow - _connectedAt.Value;
            if (span.TotalHours >= 1)
                return $"{(int)span.TotalHours}:{span.Minutes:D2}:{span.Seconds:D2}";
            return $"{span.Minutes:D2}:{span.Seconds:D2}";
        }
    }

    public string TotalDownText => State == ConnectionState.Connected ? FormatBytes(_tunnel.BytesReceived) : "0 B";
    public string TotalUpText => State == ConnectionState.Connected ? FormatBytes(_tunnel.BytesSent) : "0 B";
    public string DownSpeedText => State == ConnectionState.Connected ? FormatSpeed(_downBytesPerSec) : "—";
    public string UpSpeedText => State == ConnectionState.Connected ? FormatSpeed(_upBytesPerSec) : "—";

    private void ApplyState(ConnectionState s)
    {
        State = s;
        OnPropertyChanged(nameof(CanChangeMethod));
        (StatusText, StatusDetail, IsBusy) = s switch
        {
            ConnectionState.Connected => ("Connected", $"HTTP: 127.0.0.1:{_tunnel.HttpProxyPort}  •  SOCKS: 127.0.0.1:{_tunnel.SocksProxyPort}", false),
            ConnectionState.Connecting => ("Connecting…", "Establishing tunnel", true),
            ConnectionState.Disconnecting => ("Disconnecting…", "Cleaning up", true),
            ConnectionState.Error => ("Connection error", "See logs for details", false),
            _ => ("Disconnected", "Tap the button to connect", false),
        };

        if (s == ConnectionState.Connected)
        {

            _connectedAt ??= DateTime.UtcNow;
            if (!_uptimeTimer.IsEnabled) _uptimeTimer.Start();
        }
        else
        {
            _connectedAt = null;
            if (_uptimeTimer.IsEnabled) _uptimeTimer.Stop();

            _lastSampleAt = null;
            _lastSentSample = 0;
            _lastReceivedSample = 0;
            _downBytesPerSec = 0;
            _upBytesPerSec = 0;
        }

        OnPropertyChanged(nameof(HttpProxyPort));
        OnPropertyChanged(nameof(SocksProxyPort));
        OnPropertyChanged(nameof(HttpProxyEndpoint));
        OnPropertyChanged(nameof(SocksProxyEndpoint));
        OnPropertyChanged(nameof(CurrentRouteIp));
        OnPropertyChanged(nameof(CurrentRouteSni));
        OnPropertyChanged(nameof(HasCurrentRoute));
        OnPropertyChanged(nameof(UptimeText));
        OnPropertyChanged(nameof(TotalDownText));
        OnPropertyChanged(nameof(TotalUpText));
        OnPropertyChanged(nameof(DownSpeedText));
        OnPropertyChanged(nameof(UpSpeedText));
        OnPropertyChanged(nameof(ServerRegionCode));
        OnPropertyChanged(nameof(ServerRegionName));
        OnPropertyChanged(nameof(HasRegion));
        OnPropertyChanged(nameof(HasServerRegion));
        OnPropertyChanged(nameof(ShowConnectingProgress));
        OnPropertyChanged(nameof(ConnectingProgress));
        OnPropertyChanged(nameof(ConnectingProgressText));
        RaiseMethodDependentProperties();
        ToggleConnectionCommand.NotifyCanExecuteChanged();
    }

    public int ConnectingProgress
    {
        get => _tunnel.ConnectProgressPercent;
        set { }
    }

    public string ConnectingProgressText => string.IsNullOrWhiteSpace(_tunnel.ConnectProgressText)
        ? "Scanning candidate edges and establishing connection..."
        : _tunnel.ConnectProgressText;

    public bool ShowConnectingProgress => State == ConnectionState.Connecting;

    private void ApplyBytes()
    {
        var now = DateTime.UtcNow;
        var sent = _tunnel.BytesSent;
        var received = _tunnel.BytesReceived;

        if (_lastSampleAt is { } prev)
        {
            var dt = (now - prev).TotalSeconds;
            if (dt > 0.0)
            {

                var dSent = Math.Max(0, sent - _lastSentSample);
                var dRecv = Math.Max(0, received - _lastReceivedSample);
                _upBytesPerSec = dSent / dt;
                _downBytesPerSec = dRecv / dt;
            }
        }

        _lastSentSample = sent;
        _lastReceivedSample = received;
        _lastSampleAt = now;

        OnPropertyChanged(nameof(TotalDownText));
        OnPropertyChanged(nameof(TotalUpText));
        OnPropertyChanged(nameof(DownSpeedText));
        OnPropertyChanged(nameof(UpSpeedText));
    }

    private static void Post(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        if (dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }

    private bool _isToggling;

    [RelayCommand(CanExecute = nameof(CanToggle))]
    private async System.Threading.Tasks.Task ToggleConnection()
    {
        if (_isToggling) return;
        _isToggling = true;
        try
        {

            var entry = State;
            ToggleConnectionCommand.NotifyCanExecuteChanged();

            if (entry == ConnectionState.Disconnected || entry == ConnectionState.Error)
            {
                await _tunnel.StartAsync();
            }
            else if (entry == ConnectionState.Connected || entry == ConnectionState.Connecting)
            {
                await _tunnel.StopAsync();
            }
        }
        finally
        {
            _isToggling = false;
            ToggleConnectionCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanToggle() =>
        !_isToggling && State is not ConnectionState.Disconnecting;

    [RelayCommand]
    private void CopyHttpProxy() => TryCopy(HttpProxyEndpoint);

    [RelayCommand]
    private void CopySocksProxy() => TryCopy(SocksProxyEndpoint);

    [RelayCommand(CanExecute = nameof(HasRouteIp))]
    private void CopyCurrentIp() => TryCopy(CurrentRouteIp);

    [RelayCommand(CanExecute = nameof(HasRouteSni))]
    private void CopyCurrentSni() => TryCopy(CurrentRouteSni);

    private static void TryCopy(string text)
    {
        if (string.IsNullOrEmpty(text) || text == "—") return;
        try
        {
            Clipboard.SetText(text);
        }
        catch
        {

        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        if (bytes < 1024) return $"{bytes} B";
        double v = bytes;
        string[] units = { "KB", "MB", "GB", "TB" };
        int i = -1;
        do { v /= 1024.0; i++; } while (v >= 1024.0 && i < units.Length - 1);
        return v >= 100 ? $"{v:0} {units[i]}" : $"{v:0.0} {units[i]}";
    }

    private static string FormatSpeed(double bytesPerSec)
    {
        if (bytesPerSec <= 0) return "0 B/s";
        if (bytesPerSec < 1024) return $"{bytesPerSec:0} B/s";
        double v = bytesPerSec;
        string[] units = { "KB/s", "MB/s", "GB/s" };
        int i = -1;
        do { v /= 1024.0; i++; } while (v >= 1024.0 && i < units.Length - 1);
        return v >= 100 ? $"{v:0} {units[i]}" : $"{v:0.0} {units[i]}";
    }
}
