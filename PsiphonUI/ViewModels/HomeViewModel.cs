using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PsiphonUI.Models;
using PsiphonUI.Services;

namespace PsiphonUI.ViewModels;

public sealed partial class HomeViewModel : PageViewModelBase
{
    private readonly ITunnelCoreManager _tunnel;
    private readonly ISettingsService _settings;
    private readonly IXrayTunManager _xray;
    private readonly DispatcherTimer _uptimeTimer;
    private DateTime? _connectedAt;

    private long _lastSentSample;
    private long _lastReceivedSample;
    private DateTime? _lastSampleAt;
    private double _downBytesPerSec;
    private double _upBytesPerSec;

    private bool _suppressRegionSideEffects;

    public override string Title => "Home";
    public override string Route => "home";
    public override string Icon => "Home";

    public HomeViewModel(
        ITunnelCoreManager tunnel,
        ISettingsService settings,
        IXrayTunManager xray)
    {
        _tunnel = tunnel;
        _settings = settings;
        _xray = xray;

        _tunModeEnabled = AdminElevation.IsAdministrator()
            && _settings.Settings.SystemWideTunneling;

        _selectedEgressRegion = string.IsNullOrEmpty(_settings.Settings.EgressRegion)
            ? "auto"
            : _settings.Settings.EgressRegion;

        _tunnel.StateChanged += (_, s) => Application.Current.Dispatcher.Invoke(() => ApplyState(s));
        _tunnel.BytesTransferredChanged += (_, _) => Application.Current.Dispatcher.Invoke(ApplyBytes);
        _tunnel.NoticeReceived += (_, n) => Application.Current.Dispatcher.Invoke(() =>
        {

            if (n.NoticeType == "ClientRegion" || n.NoticeType == "ConnectedServerRegion")
            {
                OnPropertyChanged(nameof(ServerRegionCode));
                OnPropertyChanged(nameof(ServerRegionName));
                OnPropertyChanged(nameof(HasRegion));
            }
        });

        _xray.StateChanged += (_, _) => Application.Current.Dispatcher.Invoke(() =>
        {
            OnPropertyChanged(nameof(TunStatusText));
            OnPropertyChanged(nameof(TunStatusBrush));
            OnPropertyChanged(nameof(TunHasMessage));
        });

        _settings.SettingsChanged += (_, _) => Application.Current.Dispatcher.Invoke(() =>
        {
            if (_settings.Settings.SystemWideTunneling != TunModeEnabled)
            {
                _suppressTunSideEffects = true;
                try { TunModeEnabled = _settings.Settings.SystemWideTunneling; }
                finally { _suppressTunSideEffects = false; }
            }

            var externalRegion = string.IsNullOrEmpty(_settings.Settings.EgressRegion)
                ? "auto"
                : _settings.Settings.EgressRegion;
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
            OnPropertyChanged(nameof(UptimeText));
            OnPropertyChanged(nameof(DownSpeedText));
            OnPropertyChanged(nameof(UpSpeedText));
        };

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

    partial void OnSelectedEgressRegionChanged(string value)
    {
        if (_suppressRegionSideEffects) return;

        _settings.Settings.EgressRegion = value == "auto" ? "" : value;
        _settings.Save();

        _ = _tunnel.RestartAsync();
    }

    public bool IsAdminElevated { get; } = AdminElevation.IsAdministrator();

    private bool _suppressTunSideEffects;

    partial void OnTunModeEnabledChanged(bool value)
    {
        if (_suppressTunSideEffects) return;

        if (value && !IsAdminElevated)
        {
            MessageBox.Show(
                "System-wide tunneling needs Administrator privileges to install "
              + "the virtual network adapter (WinTUN).\n\n"
              + "Close PsiphonUI and re-launch it by right-clicking → \"Run as administrator\", "
              + "then try again.",
                "Administrator privileges required",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

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
                return "Run PsiphonUI as Administrator to enable system-wide tunneling.";

            return _xray.State switch
            {
                XrayTunState.Starting => "Starting TUN…",
                XrayTunState.Running => "All traffic is routed through PsiphonUI.",
                XrayTunState.Stopping => "Stopping TUN…",
                XrayTunState.Error => _xray.LastError ?? "TUN failed to start.",
                _ => TunModeEnabled
                    ? "Will start automatically when PsiphonUI connects."
                    : "Only apps that honor the system proxy will use PsiphonUI.",
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

    public Brush TunStatusBrush => _xray.State switch
    {
        XrayTunState.Running => TunBrushRunning,
        XrayTunState.Starting or XrayTunState.Stopping => TunBrushTransition,
        XrayTunState.Error => TunBrushError,
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

    public string HttpProxyEndpoint =>
    _tunnel.HttpProxyPort > 0 ? $"127.0.0.1:{_tunnel.HttpProxyPort}" : "—";

    public string SocksProxyEndpoint =>
        _tunnel.SocksProxyPort > 0 ? $"127.0.0.1:{_tunnel.SocksProxyPort}" : "—";

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

    public string TotalDownText => FormatBytes(_tunnel.BytesReceived);
    public string TotalUpText => FormatBytes(_tunnel.BytesSent);
    public string DownSpeedText => State == ConnectionState.Connected ? FormatSpeed(_downBytesPerSec) : "—";
    public string UpSpeedText => State == ConnectionState.Connected ? FormatSpeed(_upBytesPerSec) : "—";

    private void ApplyState(ConnectionState s)
    {
        State = s;
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
        OnPropertyChanged(nameof(UptimeText));
        OnPropertyChanged(nameof(TotalDownText));
        OnPropertyChanged(nameof(TotalUpText));
        OnPropertyChanged(nameof(DownSpeedText));
        OnPropertyChanged(nameof(UpSpeedText));
        OnPropertyChanged(nameof(ServerRegionCode));
        OnPropertyChanged(nameof(ServerRegionName));
        OnPropertyChanged(nameof(HasRegion));
        ToggleConnectionCommand.NotifyCanExecuteChanged();
    }

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
