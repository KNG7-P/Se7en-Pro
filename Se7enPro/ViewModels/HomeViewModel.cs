using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
    private readonly INavigationService _navigation;
    private readonly UiTimer _uptimeTimer;
    private DateTime? _connectedAt;

    private long _lastSentSample;
    private long _lastReceivedSample;
    private DateTime? _lastSampleAt;
    private double _downBytesPerSec;
    private double _upBytesPerSec;

    private bool _suppressRegionSideEffects;
    private readonly Debouncer _regionRestartDebouncer = new(TimeSpan.FromSeconds(1));
    private int _bytesUpdateQueued;
    private int _progressUpdateQueued;
    private int _routeUpdateQueued;

    private EventHandler<ConnectionState>? _stateHandler;
    private EventHandler? _progressHandler;
    private EventHandler? _bytesHandler;
    private EventHandler? _routeHandler;
    private EventHandler<Notice>? _noticeHandler;
    private EventHandler? _settingsHandler;

    public override string Title => "Home";
    public override string Route => "home";
    public override string Icon => "Home";

    public HomeViewModel(
        ITunnelCoreManager tunnel,
        ISettingsService settings,
        ITunManager tun,
        INavigationService navigation)
    {
        _tunnel = tunnel;
        _settings = settings;
        _tun = tun;
        _navigation = navigation;

        _tunModeEnabled = AdminElevation.IsAdministrator()
            && _settings.Settings.SystemWideTunneling;

        _systemProxyEnabled = _settings.Settings.SetSystemProxy;

        _selectedEgressRegion = RegionInCurrentList(ReadRegionFromSettings());

        
        
        
        
        
        
        
        _stateHandler = (_, s) => Post(() => ApplyState(s));
        _tunnel.StateChanged += _stateHandler;
        _progressHandler = (_, _) =>
        {
            if (Interlocked.Exchange(ref _progressUpdateQueued, 1) == 1) return;
            Post(() =>
            {
                Interlocked.Exchange(ref _progressUpdateQueued, 0);
                OnPropertyChanged(nameof(ConnectingProgress));
                OnPropertyChanged(nameof(ConnectingProgressText));
                OnPropertyChanged(nameof(StatusSubtext));
            });
        };
        _tunnel.ConnectProgressChanged += _progressHandler;
        _bytesHandler = (_, _) =>
        {
            if (Interlocked.Exchange(ref _bytesUpdateQueued, 1) == 1) return;
            Post(() =>
            {
                Interlocked.Exchange(ref _bytesUpdateQueued, 0);
                ApplyTotalCounters();
            });
        };
        _tunnel.BytesTransferredChanged += _bytesHandler;
        _routeHandler = (_, _) =>
        {
            if (Interlocked.Exchange(ref _routeUpdateQueued, 1) == 1) return;
            Post(() =>
            {
                Interlocked.Exchange(ref _routeUpdateQueued, 0);
                OnPropertyChanged(nameof(CurrentRouteIp));
                OnPropertyChanged(nameof(CurrentRouteSni));
                OnPropertyChanged(nameof(HasCurrentRoute));
                OnPropertyChanged(nameof(HasRouteIp));
                OnPropertyChanged(nameof(HasRouteSni));
                OnPropertyChanged(nameof(ServerRegionCode));
                OnPropertyChanged(nameof(ServerRegionName));
                OnPropertyChanged(nameof(HasRegion));
                OnPropertyChanged(nameof(HasServerRegion));
                OnPropertyChanged(nameof(ShowRegionBox));
                OnPropertyChanged(nameof(EndpointDisplay));
                OnPropertyChanged(nameof(CipherDisplay));
                OnPropertyChanged(nameof(DetectedLocationTooltip));
                CopyCurrentIpCommand.NotifyCanExecuteChanged();
                CopyCurrentSniCommand.NotifyCanExecuteChanged();
            });
        };
        _tunnel.RouteChanged += _routeHandler;
        _noticeHandler = (_, n) =>
        {
            if (n.NoticeType != "ClientRegion" && n.NoticeType != "ConnectedServerRegion")
                return;
            Post(() =>
            {
                OnPropertyChanged(nameof(ServerRegionCode));
                OnPropertyChanged(nameof(ServerRegionName));
                OnPropertyChanged(nameof(HasRegion));
                OnPropertyChanged(nameof(HasServerRegion));
                OnPropertyChanged(nameof(ShowRegionBox));
                OnPropertyChanged(nameof(EndpointDisplay));
                OnPropertyChanged(nameof(DetectedLocationTooltip));
            });
        };
        _tunnel.NoticeReceived += _noticeHandler;

        _tun.StateChanged += (_, _) => Post(() =>
        {
            OnPropertyChanged(nameof(TunStatusText));
            OnPropertyChanged(nameof(TunStatusBrush));
            OnPropertyChanged(nameof(TunHasMessage));
        });

        _settingsHandler = (_, _) => Post(() =>
        {
            var externalMethod = ConnectionMethodExtensions.ParseConnectionMethod(_settings.Settings.ConnectionMethod).ToToken();
            if (!string.Equals(SelectedConnectionMethod, externalMethod, StringComparison.Ordinal))
            {
                _suppressMethodSideEffects = true;
                try
                {
                    SelectedMethodOption = OptionForKey(ConnectionMethods, externalMethod);
                    SelectedConnectionMethod = externalMethod;
                }
                finally { _suppressMethodSideEffects = false; }
            }

            OnPropertyChanged(nameof(ActiveMethodName));
            OnPropertyChanged(nameof(MethodDisplayName));
            OnPropertyChanged(nameof(ConfigureMethodLabel));
            RaiseMethodDependentProperties();

            var expectedTun = AdminElevation.IsAdministrator() && _settings.Settings.SystemWideTunneling;
            if (expectedTun != TunModeEnabled)
            {
                _suppressTunSideEffects = true;
                try { TunModeEnabled = expectedTun; }
                finally { _suppressTunSideEffects = false; }
            }
            OnPropertyChanged(nameof(RoutingEngineDisplay));

            var expectedProxy = _settings.Settings.SetSystemProxy;
            if (expectedProxy != SystemProxyEnabled)
            {
                _suppressProxySideEffects = true;
                try { SystemProxyEnabled = expectedProxy; }
                finally { _suppressProxySideEffects = false; }
            }

            var externalRegion = ReadRegionFromSettings();
            if (!string.Equals(SelectedEgressRegion, externalRegion, StringComparison.Ordinal))
            {
                _suppressRegionSideEffects = true;
                try { SelectedEgressRegion = externalRegion; }
                finally { _suppressRegionSideEffects = false; }
            }
        });
        _settings.SettingsChanged += _settingsHandler;

        _uptimeTimer = new UiTimer(TimeSpan.FromSeconds(1), () =>
        {
            ApplySpeedTick();
            OnPropertyChanged(nameof(UptimeText));
        });

        var initialMethodToken = ConnectionMethodExtensions.ParseConnectionMethod(_settings.Settings.ConnectionMethod).ToToken();
        _selectedConnectionMethod = initialMethodToken;
        ConnectionMethodsView = Ui.CreateGroupedOptionsView(
            _connectionMethods, nameof(ConnectionMethodOption.GroupKey));
        _selectedMethodOption = OptionForKey(_connectionMethods, initialMethodToken) ?? _connectionMethods.FirstOrDefault();
        ApplyState(_tunnel.State);
        RestoreEgressRegionSelection();
        Post(RestoreEgressRegionSelection);
        if (System.Windows.Application.Current?.Dispatcher is { } dispatcher && !dispatcher.HasShutdownStarted)
        {
            dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(RestoreEgressRegionSelection));
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        if (_stateHandler is not null) _tunnel.StateChanged -= _stateHandler;
        if (_progressHandler is not null) _tunnel.ConnectProgressChanged -= _progressHandler;
        if (_bytesHandler is not null) _tunnel.BytesTransferredChanged -= _bytesHandler;
        if (_routeHandler is not null) _tunnel.RouteChanged -= _routeHandler;
        if (_noticeHandler is not null) _tunnel.NoticeReceived -= _noticeHandler;
        if (_settingsHandler is not null) _settings.SettingsChanged -= _settingsHandler;
        try { _uptimeTimer?.Stop(); } catch { }
        try { _regionRestartDebouncer?.Dispose(); } catch { }
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

    [ObservableProperty]
    private bool _systemProxyEnabled = true;

    private bool _suppressProxySideEffects;

    partial void OnSystemProxyEnabledChanged(bool value)
    {
        if (_suppressProxySideEffects) return;
        _settings.Settings.SetSystemProxy = value;
        _settings.Save();
    }

    [RelayCommand]
    private void NavigateToSettings()
    {
        var targetTab = CurrentMethod switch
        {
            ConnectionMethod.Psiphon => SettingsTab.Psiphon,
            ConnectionMethod.Masque or ConnectionMethod.WireGuard or ConnectionMethod.WarpOnWarp or ConnectionMethod.MasqueInMasque => SettingsTab.Aether,
            ConnectionMethod.Tor => SettingsTab.Tor,
            ConnectionMethod.Shard => SettingsTab.Shard,
            ConnectionMethod.Foxy => SettingsTab.Foxy,
            ConnectionMethod.Lantern => SettingsTab.General,
            ConnectionMethod.Veep => SettingsTab.General,
            ConnectionMethod.LanternOverWarp or ConnectionMethod.VeepOverWarp => SettingsTab.Chained,
            ConnectionMethod.PsiphonOverWarp or ConnectionMethod.TorOverWarp or
            ConnectionMethod.PsiphonOverV2Ray or ConnectionMethod.TorOverV2Ray or
            ConnectionMethod.FoxyOverWarp or ConnectionMethod.FoxyOverV2Ray or
            ConnectionMethod.WarpOverV2Ray or ConnectionMethod.PsiphonOverFoxy or
            ConnectionMethod.PsiphonOverLantern => SettingsTab.Chained,
            _ => SettingsTab.General
        };
        if (App.Services != null)
        {
            var settingsVm = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetService<SettingsViewModel>(App.Services);
            if (settingsVm != null)
            {
                settingsVm.SelectedTab = targetTab;
                if (CurrentMethod == ConnectionMethod.WireGuard) settingsVm.SelectedAetherProtocol = "wireguard";
                else if (CurrentMethod == ConnectionMethod.WarpOnWarp) settingsVm.SelectedAetherProtocol = "warp";
                else if (CurrentMethod == ConnectionMethod.MasqueInMasque) settingsVm.SelectedAetherProtocol = "mim";
                else if (CurrentMethod == ConnectionMethod.Masque) settingsVm.SelectedAetherProtocol = "masque";
                else if (CurrentMethod == ConnectionMethod.PsiphonOverWarp) settingsVm.SelectedChainedSubMode = "psiphon_warp";
                else if (CurrentMethod == ConnectionMethod.TorOverWarp) settingsVm.SelectedChainedSubMode = "tor_warp";
                else if (CurrentMethod == ConnectionMethod.PsiphonOverV2Ray) settingsVm.SelectedChainedSubMode = "psiphon_v2ray";
                else if (CurrentMethod == ConnectionMethod.PsiphonOverFoxy) settingsVm.SelectedChainedSubMode = "psiphon_foxy";
                else if (CurrentMethod == ConnectionMethod.PsiphonOverLantern) settingsVm.SelectedChainedSubMode = "psiphon_lantern";
                else if (CurrentMethod == ConnectionMethod.TorOverV2Ray) settingsVm.SelectedChainedSubMode = "tor_v2ray";
                else if (CurrentMethod == ConnectionMethod.FoxyOverWarp) settingsVm.SelectedChainedSubMode = "foxy_warp";
                else if (CurrentMethod == ConnectionMethod.FoxyOverV2Ray) settingsVm.SelectedChainedSubMode = "foxy_v2ray";
                else if (CurrentMethod == ConnectionMethod.WarpOverV2Ray) settingsVm.SelectedChainedSubMode = "warp_v2ray";
                else if (CurrentMethod == ConnectionMethod.LanternOverWarp) settingsVm.SelectedChainedSubMode = "lantern_warp";
                else if (CurrentMethod == ConnectionMethod.VeepOverWarp) settingsVm.SelectedChainedSubMode = "veep_warp";
            }
        }
        _navigation.NavigateTo("settings");
    }

    public bool IsConnected => State == ConnectionState.Connected;
    public bool IsConnecting => State == ConnectionState.Connecting;
    public bool IsDisconnecting => State == ConnectionState.Disconnecting;
    public bool IsDisconnected => State == ConnectionState.Disconnected || State == ConnectionState.Error;

    public string StatusHeadline => State switch
    {
        ConnectionState.Connected => Loc.T("statusConnected", "CONNECTED"),
        ConnectionState.Connecting => Loc.T("statusConnecting", "CONNECTING..."),
        ConnectionState.Disconnecting => Loc.T("statusStopping", "STOPPING"),
        ConnectionState.Error => Loc.T("statusConnectionError", "CONNECTION ERROR"),
        _ => Loc.T("statusDisconnected", "DISCONNECTED"),
    };

    public string StatusSubtext => State switch
    {
        ConnectionState.Connected => Loc.T("statusProtected", "Protected & Encrypted"),
        ConnectionState.Connecting => ConnectingProgressText,
        ConnectionState.Disconnecting => Loc.T("statusCleaningUp", "Cleaning up session…"),
        ConnectionState.Error => Loc.T("statusFailed", "Connection failed. Check logs."),
        _ => Loc.T("statusReady", "Ready to connect"),
    };

    public string MethodDisplayName => CurrentMethod.ToDisplayName();
    public string ConfigureMethodLabel => string.Format(Loc.Of("Configure {0} Settings"), MethodDisplayName);

    public string EndpointDisplay => !string.IsNullOrEmpty(_tunnel.CurrentRouteIp) && _tunnel.CurrentRouteIp != "—"
        ? _tunnel.CurrentRouteIp
        : (IsConnected ? Loc.Of("Detecting…") : "—");

    public bool ShowRegionBox => ShowRegionPicker;

    public string DetectedLocationTooltip
    {
        get
        {
            if (HasRegion && HasRouteIp)
            {
                return $"{ServerRegionName} ({ServerRegionCode}) • {CurrentRouteIp}";
            }
            if (HasRegion) return ServerRegionName;
            if (HasRouteIp) return CurrentRouteIp;
            return Loc.T("finalEndpointLocation", "Final Endpoint & Location");
        }
    }

    public string CipherDisplay => !string.IsNullOrEmpty(_tunnel.CurrentRouteSni) && _tunnel.CurrentRouteSni != "—"
        ? _tunnel.CurrentRouteSni
        : (IsConnected ? MethodDisplayName : Loc.Of("Cipher Ready"));

    private double _peakBytesPerSec;
    public string PeakSpeedText => State == ConnectionState.Connected && _peakBytesPerSec > 0
        ? FormatSpeed(_peakBytesPerSec)
        : "0.0 KB/s";

    public string TotalTrafficText => State == ConnectionState.Connected
        ? FormatBytes(_tunnel.BytesReceived + _tunnel.BytesSent)
        : "0 B";

    public ObservableCollection<Country> EgressRegions { get; } =
    CountryHelper.BuildSeedRegions();

    public ObservableCollection<Country> TorRegions { get; } =
    CountryHelper.BuildTorExitRegions();

    public ObservableCollection<Country> FoxyRegions { get; } =
    CountryHelper.BuildFoxyRegions();

    public ObservableCollection<Country> LanternRegions { get; } =
    CountryHelper.BuildLanternRegions();

    public ObservableCollection<Country> VeepRegions { get; } =
    CountryHelper.BuildVeepRegions();

    public ObservableCollection<Country> ActiveRegions => CurrentMethod switch
    {
        ConnectionMethod.Tor or ConnectionMethod.TorOverWarp or ConnectionMethod.TorOverV2Ray => TorRegions,
        ConnectionMethod.Foxy or ConnectionMethod.FoxyOverWarp or ConnectionMethod.FoxyOverV2Ray => FoxyRegions,
        ConnectionMethod.Lantern or ConnectionMethod.LanternOverWarp => LanternRegions,
        ConnectionMethod.Veep or ConnectionMethod.VeepOverWarp => VeepRegions,
        _ => EgressRegions,
    };

    [ObservableProperty]
    private Country? _selectedRegionItem;

    [ObservableProperty]
    private string _selectedEgressRegion = "auto";

    private readonly List<ConnectionMethodOption> _connectionMethods = ConnectionMethodExtensions.CreateOptions();

    public IReadOnlyList<ConnectionMethodOption> ConnectionMethods => _connectionMethods;

        public object ConnectionMethodsView { get; }

    private static ConnectionMethodOption? OptionForKey(IReadOnlyList<ConnectionMethodOption> options, string? key) =>
        options.FirstOrDefault(m => string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase));

    [ObservableProperty]
    private ConnectionMethodOption? _selectedMethodOption;

    [ObservableProperty]
    private string _selectedConnectionMethod = "masque";

    private bool _suppressMethodSideEffects;

    public bool CanChangeMethod => State is ConnectionState.Disconnected or ConnectionState.Error;

    partial void OnSelectedMethodOptionChanged(ConnectionMethodOption? value)
    {
        if (value is null) return;
        var token = ConnectionMethodExtensions.ParseConnectionMethod(value.Key).ToToken();
        if (string.Equals(_selectedConnectionMethod, token, StringComparison.OrdinalIgnoreCase)) return;

        _selectedConnectionMethod = token;
        OnPropertyChanged(nameof(SelectedConnectionMethod));
        ApplyConnectionMethodChanges(token);
    }

    partial void OnSelectedConnectionMethodChanged(string value)
    {
        var token = ConnectionMethodExtensions.ParseConnectionMethod(value).ToToken();
        if (!string.Equals(_selectedMethodOption?.Key, token, StringComparison.OrdinalIgnoreCase))
        {
            var match = OptionForKey(ConnectionMethods, token) ?? ConnectionMethods.FirstOrDefault();
            if (match is not null && match != _selectedMethodOption)
            {
                _selectedMethodOption = match;
                OnPropertyChanged(nameof(SelectedMethodOption));
            }
        }
        ApplyConnectionMethodChanges(token);
    }

    private void ApplyConnectionMethodChanges(string token)
    {
        if (_suppressMethodSideEffects) return;
        _settings.Settings.ConnectionMethod = token;

        
        
        
        if (token is "masque" or "wireguard" or "warp_on_warp" or "masque_in_masque")
        {
            _settings.Settings.AetherProtocol = token switch
            {
                "wireguard" => "wireguard",
                "warp_on_warp" => "warp",
                "masque_in_masque" => "mim",
                _ => "masque",
            };
        }
        else if (token is "psiphon_over_warp" or "tor_over_warp" or "foxy_over_warp"
                 or "psiphon_over_v2ray" or "tor_over_v2ray" or "foxy_over_v2ray" or "warp_over_v2ray"
                 or "psiphon_over_foxy" or "psiphon_over_lantern" or "lantern_over_warp" or "veep_over_warp")
        {
            _settings.Settings.ChainedSubMode = token switch
            {
                "psiphon_over_v2ray" => "psiphon_v2ray",
                "psiphon_over_foxy" => "psiphon_foxy",
                "psiphon_over_lantern" => "psiphon_lantern",
                "tor_over_warp" => "tor_warp",
                "tor_over_v2ray" => "tor_v2ray",
                "foxy_over_warp" => "foxy_warp",
                "foxy_over_v2ray" => "foxy_v2ray",
                "warp_over_v2ray" => "warp_v2ray",
                "lantern_over_warp" => "lantern_warp",
                "veep_over_warp" => "veep_warp",
                _ => "psiphon_warp",
            };
        }

        _settings.Save();
        OnPropertyChanged(nameof(ActiveMethodName));
        OnPropertyChanged(nameof(MethodDisplayName));
        OnPropertyChanged(nameof(ConfigureMethodLabel));
        RaiseMethodDependentProperties();
    }

        public ConnectionMethod CurrentMethod =>
        ConnectionMethodExtensions.ParseConnectionMethod(_settings.Settings.ConnectionMethod);

        private string ReadRegionFromSettings()
    {
        var s = _settings.Settings;
        var code = CurrentMethod switch
        {
            ConnectionMethod.Tor or ConnectionMethod.TorOverWarp or ConnectionMethod.TorOverV2Ray => s.TorExitCountry,
            ConnectionMethod.Psiphon or ConnectionMethod.PsiphonOverWarp or ConnectionMethod.PsiphonOverV2Ray or ConnectionMethod.PsiphonOverFoxy or ConnectionMethod.PsiphonOverLantern => s.EgressRegion,
            ConnectionMethod.Foxy or ConnectionMethod.FoxyOverWarp or ConnectionMethod.FoxyOverV2Ray => s.FirefoxServerCountry,
            ConnectionMethod.Lantern or ConnectionMethod.LanternOverWarp => s.LanternRegion,
            ConnectionMethod.Veep or ConnectionMethod.VeepOverWarp => s.VeepRegion,
            _ => "auto",
        };
        if (string.IsNullOrWhiteSpace(code)
            || string.Equals(code, "auto", StringComparison.OrdinalIgnoreCase)
            || string.Equals(code, "rec", StringComparison.OrdinalIgnoreCase))
            return "auto";
        if (CurrentMethod is ConnectionMethod.Veep or ConnectionMethod.VeepOverWarp)
            return code.Trim().ToLowerInvariant();
        return code.Trim().ToUpperInvariant();
    }

    partial void OnSelectedRegionItemChanged(Country? value)
    {
        if (_suppressRegionSideEffects) return;
        if (value is null)
        {
            if (System.Windows.Application.Current?.Dispatcher is { } dispatcher && !dispatcher.HasShutdownStarted)
            {
                dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(RestoreEgressRegionSelection));
            }
            else
            {
                Post(RestoreEgressRegionSelection);
            }
            return;
        }

        var code = value.Code;
        var previousCode = _selectedEgressRegion;
        _selectedEgressRegion = code;
        OnPropertyChanged(nameof(SelectedEgressRegion));

        var isAuto = string.Equals(code, "auto", StringComparison.OrdinalIgnoreCase);
        var s = _settings.Settings;
        switch (CurrentMethod)
        {
            case ConnectionMethod.Tor or ConnectionMethod.TorOverWarp or ConnectionMethod.TorOverV2Ray:
                s.TorExitCountry = isAuto ? "" : code.ToLowerInvariant();
                break;
            case ConnectionMethod.Psiphon or ConnectionMethod.PsiphonOverWarp or ConnectionMethod.PsiphonOverV2Ray or ConnectionMethod.PsiphonOverFoxy or ConnectionMethod.PsiphonOverLantern:
                s.EgressRegion = isAuto ? "" : code.ToUpperInvariant();
                break;
            case ConnectionMethod.Foxy or ConnectionMethod.FoxyOverWarp or ConnectionMethod.FoxyOverV2Ray:
                s.FirefoxServerCountry = isAuto ? "REC" : code.ToUpperInvariant();
                s.FirefoxServerCity = "";
                break;
            case ConnectionMethod.Lantern or ConnectionMethod.LanternOverWarp:
                s.LanternRegion = isAuto ? "" : code.ToUpperInvariant();
                break;
            case ConnectionMethod.Veep or ConnectionMethod.VeepOverWarp:
                s.VeepRegion = isAuto ? "auto" : code.ToLowerInvariant();
                break;
            default:
                return;
        }

        _settings.Save();

        if (State == ConnectionState.Connected &&
            _tunnel.ActiveMethod == CurrentMethod &&
            !string.Equals(previousCode, code, StringComparison.OrdinalIgnoreCase))
        {
            _regionRestartDebouncer.Schedule(() =>
            {
                if (State == ConnectionState.Connected && _tunnel.ActiveMethod == CurrentMethod)
                {
                    _ = Task.Run(() => _tunnel.RestartAsync());
                }
            });
        }
    }

    partial void OnSelectedEgressRegionChanged(string value)
    {
        if (_suppressRegionSideEffects) return;

        if (string.IsNullOrWhiteSpace(value))
        {
            Post(RestoreEgressRegionSelection);
            return;
        }

        var regions = ActiveRegions;
        if (regions != null && (_selectedRegionItem == null || !string.Equals(_selectedRegionItem.Code, value, StringComparison.OrdinalIgnoreCase)))
        {
            var match = regions.FirstOrDefault(r => string.Equals(r.Code, value, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                _suppressRegionSideEffects = true;
                try
                {
                    SelectedRegionItem = match;
                    OnPropertyChanged(nameof(SelectedRegionItem));
                }
                finally { _suppressRegionSideEffects = false; }
            }
        }
    }

    private void RestoreEgressRegionSelection()
    {
        var regions = ActiveRegions;
        if (regions == null || regions.Count == 0) return;

        var targetCode = ReadRegionFromSettings();
        var match = regions.FirstOrDefault(r => string.Equals(r.Code, targetCode, StringComparison.OrdinalIgnoreCase))
                    ?? regions.FirstOrDefault(r => string.Equals(r.Code, "auto", StringComparison.OrdinalIgnoreCase))
                    ?? regions[0];

        _suppressRegionSideEffects = true;
        try
        {
            SelectedRegionItem = match;
            SelectedEgressRegion = match.Code;
            OnPropertyChanged(nameof(SelectedRegionItem));
            OnPropertyChanged(nameof(SelectedEgressRegion));
        }
        finally { _suppressRegionSideEffects = false; }
    }

    private string RegionInCurrentList(string code)
    {
        var regions = ActiveRegions;
        if (regions == null || regions.Count == 0) return "auto";
        if (string.IsNullOrWhiteSpace(code)
            || string.Equals(code, "auto", StringComparison.OrdinalIgnoreCase)
            || string.Equals(code, "rec", StringComparison.OrdinalIgnoreCase))
        {
            return regions[0].Code;
        }
        var match = regions.FirstOrDefault(r => string.Equals(r.Code, code, StringComparison.OrdinalIgnoreCase));
        return match != null ? match.Code : regions[0].Code;
    }

        public bool ShowRegionPicker =>
        CurrentMethod is ConnectionMethod.Psiphon
                      or ConnectionMethod.Tor
                      or ConnectionMethod.Foxy
                      or ConnectionMethod.Lantern
                      or ConnectionMethod.Veep
                      or ConnectionMethod.PsiphonOverWarp
                      or ConnectionMethod.TorOverWarp
                      or ConnectionMethod.FoxyOverWarp
                      or ConnectionMethod.LanternOverWarp
                      or ConnectionMethod.VeepOverWarp
                      or ConnectionMethod.PsiphonOverV2Ray
                      or ConnectionMethod.TorOverV2Ray
                      or ConnectionMethod.FoxyOverV2Ray
                      or ConnectionMethod.PsiphonOverFoxy
                      or ConnectionMethod.PsiphonOverLantern;

    public string RegionPickerTitle => CurrentMethod switch
    {
        ConnectionMethod.Tor or ConnectionMethod.TorOverWarp or ConnectionMethod.TorOverV2Ray => Loc.Of("Exit country"),
        ConnectionMethod.Foxy or ConnectionMethod.FoxyOverWarp or ConnectionMethod.FoxyOverV2Ray => Loc.Of("Edge location"),
        ConnectionMethod.Lantern or ConnectionMethod.LanternOverWarp => Loc.Of("Region"),
        ConnectionMethod.Veep or ConnectionMethod.VeepOverWarp => Loc.Of("Location"),
        _ => Loc.Of("Region"),
    };

    public string RegionPickerHint => CurrentMethod switch
    {
        ConnectionMethod.Tor or ConnectionMethod.TorOverWarp or ConnectionMethod.TorOverV2Ray
            => Loc.Of("Tor exit relay country. Fewer relays in a country means slower circuits."),
        ConnectionMethod.Foxy or ConnectionMethod.FoxyOverWarp or ConnectionMethod.FoxyOverV2Ray
            => Loc.Of("Mozilla's edge location your traffic leaves from. Recommended picks the closest edge."),
        ConnectionMethod.Lantern or ConnectionMethod.LanternOverWarp
            => Loc.Of("Lantern server location. Auto selects the lowest latency node optimized for Iran."),
        ConnectionMethod.Veep or ConnectionMethod.VeepOverWarp
            => Loc.Of("Veep server location. Auto selects the lowest latency proxy node."),
        _ => Loc.Of("Where your traffic exits. Auto picks the fastest server."),
    };

        public bool ShowTrafficStats => true;

    public bool HasRouteIp => !string.IsNullOrEmpty(_tunnel.CurrentRouteIp) && _tunnel.CurrentRouteIp != "—";

    public bool HasRouteSni => !string.IsNullOrEmpty(_tunnel.CurrentRouteSni);

    public bool HasRouteIpAndSni => HasRouteIp && HasRouteSni;

    public bool HasRouteSniOnly => !HasRouteIp && HasRouteSni;

    public string RouteIpLabel => CurrentMethod switch
    {
        ConnectionMethod.Psiphon or ConnectionMethod.PsiphonOverWarp or ConnectionMethod.PsiphonOverV2Ray or ConnectionMethod.PsiphonOverFoxy or ConnectionMethod.PsiphonOverLantern => "IP",
        _ => "EDGE",
    };

    public string RouteIpTooltip => CurrentMethod switch
    {
        ConnectionMethod.Psiphon or ConnectionMethod.PsiphonOverWarp or ConnectionMethod.PsiphonOverV2Ray or ConnectionMethod.PsiphonOverFoxy or ConnectionMethod.PsiphonOverLantern
            => Loc.T("homeEdgeIpHint", "Edge IP tunnel-core is currently routing through"),
        _ => Loc.T("homeCloudflareEdgeHint", "Cloudflare edge address this session is bound to"),
    };

    public string RouteSniLabel => CurrentMethod switch
    {
        ConnectionMethod.Psiphon or ConnectionMethod.PsiphonOverWarp or ConnectionMethod.PsiphonOverV2Ray or ConnectionMethod.PsiphonOverFoxy or ConnectionMethod.PsiphonOverLantern => "SNI",
        ConnectionMethod.Tor or ConnectionMethod.TorOverWarp or ConnectionMethod.TorOverV2Ray => "STATUS",
        ConnectionMethod.Shard => "HOST",
        _ => "MODE",
    };

    public string RouteSniTooltip => CurrentMethod switch
    {
        ConnectionMethod.Psiphon or ConnectionMethod.PsiphonOverWarp or ConnectionMethod.PsiphonOverV2Ray or ConnectionMethod.PsiphonOverFoxy or ConnectionMethod.PsiphonOverLantern
            => Loc.T("homeSniHint", "SNI hostname tunnel-core is currently presenting to the CDN"),
        ConnectionMethod.Tor or ConnectionMethod.TorOverWarp or ConnectionMethod.TorOverV2Ray
            => Loc.T("homeTorBootstrapHint", "Tor bootstrap progress"),
        ConnectionMethod.Shard
            => Loc.T("homeShardSniHint", "Host / SNI header presented by SHARD to Cloudflare edge"),
        _ => Loc.T("homeTransportHint", "Transport and obfuscation profile in use"),
    };

    private void RaiseMethodDependentProperties()
    {
        OnPropertyChanged(nameof(CurrentMethod));
        OnPropertyChanged(nameof(ShowRegionPicker));
        OnPropertyChanged(nameof(ShowRegionBox));
        _suppressRegionSideEffects = true;
        try
        {
            OnPropertyChanged(nameof(ActiveRegions));
        }
        finally
        {
            _suppressRegionSideEffects = false;
        }
        RestoreEgressRegionSelection();
        if (System.Windows.Application.Current?.Dispatcher is { } dispatcher && !dispatcher.HasShutdownStarted)
        {
            dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(RestoreEgressRegionSelection));
        }
        OnPropertyChanged(nameof(DetectedLocationTooltip));
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
        OnPropertyChanged(nameof(MethodDisplayName));
        OnPropertyChanged(nameof(ConfigureMethodLabel));
        OnPropertyChanged(nameof(EndpointDisplay));
        OnPropertyChanged(nameof(CipherDisplay));
        CopyCurrentIpCommand.NotifyCanExecuteChanged();
        CopyCurrentSniCommand.NotifyCanExecuteChanged();
    }

    public bool IsAdminElevated { get; } = AdminElevation.IsAdministrator();

    private bool _suppressTunSideEffects;

    [RelayCommand]
    private async Task RestartAsAdmin()
    {
        if (!AdminElevation.IsTrustedInstallPath())
        {
            await ShowPortableElevationBlockedAsync();
            return;
        }
        _ = TryRestartAsAdmin();
    }

    private static Task ShowPortableElevationBlockedAsync() =>
        Ui.ConfirmAsync(
            Loc.T("ElevatePortableBlockedTitle", "Elevation unavailable"),
            string.Format(
                Loc.T("ElevatePortableBlocked",
                    "Se7en Pro is running from a folder you can write to:\n\n{0}\n\n" +
                    "Elevating this copy would let another program running as you replace " +
                    "Se7enPro.exe and gain Administrator rights.\n\n" +
                    "Install Se7en Pro under Program Files and start the installed copy to use " +
                    "system-wide tunneling."),
                AppContext.BaseDirectory.TrimEnd('\\')));

    private bool TryRestartAsAdmin()
    {
        _settings.Settings.SystemWideTunneling = true;
        _settings.Save();

        if (AdminElevation.TryRestartElevated()) return true;

        _settings.Settings.SystemWideTunneling = false;
        _settings.Save();
        return false;
    }

    partial void OnTunModeEnabledChanged(bool value)
    {
        if (_suppressTunSideEffects) return;

        if (value && !IsAdminElevated)
        {
            _ = AskForElevationAsync();
            return;
        }

        _settings.Settings.SystemWideTunneling = value;
        _settings.Save();
    }

    private async Task AskForElevationAsync()
    {
        if (await Ui.ConfirmElevationAsync())
        {
            if (!AdminElevation.IsTrustedInstallPath())
            {
                await ShowPortableElevationBlockedAsync();
            }
            else if (TryRestartAsAdmin())
            {
                return;
            }
        }

        _suppressTunSideEffects = true;
        try { TunModeEnabled = false; }
        finally { _suppressTunSideEffects = false; }
    }

    public string TunStatusText
    {
        get
        {

            if (!IsAdminElevated)
                return Loc.T("tunAdminRequired", "Run Se7en Pro as Administrator to enable system-wide tunneling.");

            return _tun.State switch
            {
                TunState.Starting => Loc.T("tunStarting", "Starting TUN…"),
                TunState.Running => Loc.T("tunAllRouted", "All traffic is routed through Se7en Pro."),
                TunState.Stopping => Loc.T("tunStopping", "Stopping TUN…"),
                TunState.Error => _tun.LastError ?? Loc.T("tunFailed", "TUN failed to start."),
                _ => TunModeEnabled
                    ? Loc.T("tunWillStart", "Will start automatically when Se7en Pro connects.")
                    : Loc.T("tunProxyOnly", "Only apps that honor the system proxy will use Se7en Pro."),
            };
        }
    }

    
    
    private static readonly object TunBrushRunning = Ui.FrozenBrush("#22C55E");
    private static readonly object TunBrushTransition = Ui.FrozenBrush("#F59E0B");
    private static readonly object TunBrushError = Ui.FrozenBrush("#EF4444");
    private static readonly object TunBrushIdle = Ui.FrozenBrush("#6B7280");

    public object TunStatusBrush => _tun.State switch
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
        (State is ConnectionState.Connected or ConnectionState.Connecting
            ? _tunnel.ActiveMethod
            : ConnectionMethodExtensions.ParseConnectionMethod(_settings.Settings.ConnectionMethod))
        .ToDisplayName();

    
    
    
    

    public bool IsUsingUpstreamProxy
    {
        get
        {
            var s = _settings.Settings;
            if (CurrentMethod == ConnectionMethod.Psiphon)
            {
                return !TunnelCoreManager.BypassesUpstreamProxy(s.ProtocolMode)
                    && s.UpstreamProxyEnabled
                    && !string.IsNullOrWhiteSpace(s.UpstreamProxy);
            }
            return false;
        }
    }

    public bool ShowProxyBadge =>
        IsUsingUpstreamProxy && State == ConnectionState.Connected;

    public string ProxyDisplay
    {
        get
        {
            var s = _settings.Settings;
            return BuildProxyDisplay(s.UpstreamProxy, s.UpstreamProxyScheme);
        }
    }

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
    public double DownSpeed => State == ConnectionState.Connected ? _downBytesPerSec : 0.0;
    public double UpSpeed => State == ConnectionState.Connected ? _upBytesPerSec : 0.0;
    public double PeakSpeed => State == ConnectionState.Connected ? _peakBytesPerSec : 0.0;

    public double DownRatio
    {
        get
        {
            if (State != ConnectionState.Connected) return 50.0;
            var total = _tunnel.BytesReceived + _tunnel.BytesSent;
            if (total <= 0) return 50.0;
            return Math.Clamp((double)_tunnel.BytesReceived / total * 100.0, 5.0, 95.0);
        }
        set { }
    }

    public double UpRatio
    {
        get => 100.0 - DownRatio;
        set { }
    }

    public string DownPercentText
    {
        get => $"{DownRatio:F0}%";
        set { }
    }

    public string UpPercentText
    {
        get => $"{UpRatio:F0}%";
        set { }
    }

    public string RoutingEngineDisplay => TunModeEnabled
        ? (WintunTunManager.NormalizeTunMethod(_settings.Settings.TunMethod) switch
        {
            "zeptun" => Loc.Of("Zeptun L3 Adapter"),
            "mihomo" => Loc.Of("Mihomo L3 Adapter"),
            _ => Loc.Of("WinTUN L3 Adapter"),
        })
        : (SystemProxyEnabled ? Loc.Of("System Web Proxy") : Loc.Of("Local Inbound Proxy"));

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
            if (!_uptimeTimer.IsRunning) _uptimeTimer.Start();
        }
        else
        {
            _connectedAt = null;
            if (_uptimeTimer.IsRunning) _uptimeTimer.Stop();

            _lastSampleAt = null;
            _lastSentSample = 0;
            _lastReceivedSample = 0;
            _downBytesPerSec = 0;
            _upBytesPerSec = 0;
            _peakBytesPerSec = 0;
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
        OnPropertyChanged(nameof(DownSpeed));
        OnPropertyChanged(nameof(UpSpeed));
        OnPropertyChanged(nameof(PeakSpeed));
        OnPropertyChanged(nameof(PeakSpeedText));
        OnPropertyChanged(nameof(DownRatio));
        OnPropertyChanged(nameof(UpRatio));
        OnPropertyChanged(nameof(DownPercentText));
        OnPropertyChanged(nameof(UpPercentText));
        OnPropertyChanged(nameof(RoutingEngineDisplay));
        OnPropertyChanged(nameof(TotalTrafficText));
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(IsConnecting));
        OnPropertyChanged(nameof(IsDisconnecting));
        OnPropertyChanged(nameof(IsDisconnected));
        OnPropertyChanged(nameof(StatusHeadline));
        OnPropertyChanged(nameof(StatusSubtext));
        OnPropertyChanged(nameof(ServerRegionCode));
        OnPropertyChanged(nameof(ServerRegionName));
        OnPropertyChanged(nameof(HasRegion));
        OnPropertyChanged(nameof(HasServerRegion));
        OnPropertyChanged(nameof(ShowConnectingProgress));
        OnPropertyChanged(nameof(ConnectingProgress));
        OnPropertyChanged(nameof(ConnectingProgressText));
        RaiseMethodDependentProperties();

        if (s is ConnectionState.Connected or ConnectionState.Error or ConnectionState.Disconnected)
        {
            Interlocked.Exchange(ref _isToggling, 0);
        }

        ToggleConnectionCommand.NotifyCanExecuteChanged();
    }

    public int ConnectingProgress
    {
        get => _tunnel.ConnectProgressPercent;
        set { }
    }

    public string ConnectingProgressText => string.IsNullOrWhiteSpace(_tunnel.ConnectProgressText)
        ? Loc.Of("Scanning candidate edges and establishing connection...")
        : Loc.Of(_tunnel.ConnectProgressText);

    public bool ShowConnectingProgress => State == ConnectionState.Connecting;

    private void ApplyTotalCounters()
    {
        OnPropertyChanged(nameof(TotalDownText));
        OnPropertyChanged(nameof(TotalUpText));
        OnPropertyChanged(nameof(TotalTrafficText));
        OnPropertyChanged(nameof(DownRatio));
        OnPropertyChanged(nameof(UpRatio));
        OnPropertyChanged(nameof(DownPercentText));
        OnPropertyChanged(nameof(UpPercentText));
    }

    private void ApplySpeedTick()
    {
        var now = DateTime.UtcNow;
        var sent = _tunnel.BytesSent;
        var received = _tunnel.BytesReceived;

        if (_lastSampleAt is { } prev && State == ConnectionState.Connected)
        {
            var dt = (now - prev).TotalSeconds;
            if (dt >= 0.5)
            {
                var dSent = Math.Max(0, sent - _lastSentSample);
                var dRecv = Math.Max(0, received - _lastReceivedSample);
                _upBytesPerSec = dSent / dt;
                _downBytesPerSec = dRecv / dt;

                _lastSentSample = sent;
                _lastReceivedSample = received;
                _lastSampleAt = now;
            }
        }
        else
        {
            _lastSentSample = sent;
            _lastReceivedSample = received;
            _lastSampleAt = now;
            if (State != ConnectionState.Connected)
            {
                _downBytesPerSec = 0;
                _upBytesPerSec = 0;
            }
        }

        _peakBytesPerSec = Math.Max(_peakBytesPerSec, Math.Max(_downBytesPerSec, _upBytesPerSec));

        OnPropertyChanged(nameof(DownSpeedText));
        OnPropertyChanged(nameof(UpSpeedText));
        OnPropertyChanged(nameof(DownSpeed));
        OnPropertyChanged(nameof(UpSpeed));
        OnPropertyChanged(nameof(PeakSpeed));
        OnPropertyChanged(nameof(PeakSpeedText));
        ApplyTotalCounters();
    }

    
    private static void Post(Action action) => Ui.Post(action);

    private int _isToggling;

    [RelayCommand(AllowConcurrentExecutions = true, CanExecute = nameof(CanToggle))]
    private async System.Threading.Tasks.Task ToggleConnection()
    {
        if (State == ConnectionState.Connecting)
        {
            _tunnel.CancelInFlightConnection();
            try
            {
                await _tunnel.StopAsync();
            }
            catch { }
            finally
            {
                Interlocked.Exchange(ref _isToggling, 0);
                ToggleConnectionCommand.NotifyCanExecuteChanged();
            }
            return;
        }

        if (Interlocked.Exchange(ref _isToggling, 1) == 1) return;
        ToggleConnectionCommand.NotifyCanExecuteChanged();
        try
        {
            if (State == ConnectionState.Connected)
            {
                await _tunnel.StopAsync();
            }
            else if (State == ConnectionState.Disconnected || State == ConnectionState.Error)
            {
                if (CurrentMethod is ConnectionMethod.Foxy or ConnectionMethod.FoxyOverWarp
                    or ConnectionMethod.FoxyOverV2Ray or ConnectionMethod.PsiphonOverFoxy)
                {
                    var accounts = _settings.Settings.FirefoxAccounts;
                    if (accounts == null || accounts.Count == 0)
                    {
                        System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                        {
                            if (Views.FoxyAccountRequiredDialog.Show() && App.Services != null)
                            {
                                var settingsVm = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetService<SettingsViewModel>(App.Services);
                                if (settingsVm != null)
                                {
                                    settingsVm.SelectedTab = SettingsTab.Foxy;
                                }
                                _navigation.NavigateTo("settings");
                            }
                        });
                        return;
                    }
                }
                await Task.Run(() => _tunnel.StartAsync());
            }
        }
        catch
        {
            
        }
        finally
        {
            Interlocked.Exchange(ref _isToggling, 0);
            ToggleConnectionCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanToggle() =>
        (_isToggling == 0 || State == ConnectionState.Connecting) && State is not ConnectionState.Disconnecting;

    [ObservableProperty]
    private bool _isHttpCopied;

    [ObservableProperty]
    private bool _isSocksCopied;

    [ObservableProperty]
    private bool _isEndpointCopied;

    [ObservableProperty]
    private bool _isCipherCopied;

    private int _httpCopyGen;
    private int _socksCopyGen;
    private int _endpointCopyGen;
    private int _cipherCopyGen;

    [RelayCommand]
    private void CopyHttpProxy()
    {
        if (TryCopy(HttpProxyEndpoint))
        {
            IsHttpCopied = true;
            var gen = Interlocked.Increment(ref _httpCopyGen);
            System.Threading.Tasks.Task.Delay(1500).ContinueWith(_ => Post(() =>
            {
                if (Volatile.Read(ref _httpCopyGen) == gen) IsHttpCopied = false;
            }));
        }
    }

    [RelayCommand]
    private void CopySocksProxy()
    {
        if (TryCopy(SocksProxyEndpoint))
        {
            IsSocksCopied = true;
            var gen = Interlocked.Increment(ref _socksCopyGen);
            System.Threading.Tasks.Task.Delay(1500).ContinueWith(_ => Post(() =>
            {
                if (Volatile.Read(ref _socksCopyGen) == gen) IsSocksCopied = false;
            }));
        }
    }

    [RelayCommand]
    private void CopyCurrentIp()
    {
        if (TryCopy(EndpointDisplay))
        {
            IsEndpointCopied = true;
            var gen = Interlocked.Increment(ref _endpointCopyGen);
            System.Threading.Tasks.Task.Delay(1500).ContinueWith(_ => Post(() =>
            {
                if (Volatile.Read(ref _endpointCopyGen) == gen) IsEndpointCopied = false;
            }));
        }
    }

    [RelayCommand]
    private void CopyCurrentSni()
    {
        if (TryCopy(CipherDisplay))
        {
            IsCipherCopied = true;
            var gen = Interlocked.Increment(ref _cipherCopyGen);
            System.Threading.Tasks.Task.Delay(1500).ContinueWith(_ => Post(() =>
            {
                if (Volatile.Read(ref _cipherCopyGen) == gen) IsCipherCopied = false;
            }));
        }
    }

    private static bool TryCopy(string text)
    {
        if (string.IsNullOrEmpty(text) || text == "—") return false;
        return Ui.SetClipboard(text);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        if (bytes < 1024) return $"{bytes} B";
        double v = bytes;
        int i = -1;
        do { v /= 1024.0; i++; } while (v >= 1024.0 && i < ByteUnits.Length - 1);
        return v >= 100 ? $"{v:0} {ByteUnits[i]}" : $"{v:0.0} {ByteUnits[i]}";
    }

    
    
    
    private static readonly string[] ByteUnits = { "KB", "MB", "GB", "TB" };
    private static readonly string[] SpeedUnits = { "KB/s", "MB/s", "GB/s" };

    private static string FormatSpeed(double bytesPerSec)
    {
        if (bytesPerSec <= 0) return "0 B/s";
        if (bytesPerSec < 1024) return $"{bytesPerSec:0} B/s";
        double v = bytesPerSec;
        int i = -1;
        do { v /= 1024.0; i++; } while (v >= 1024.0 && i < SpeedUnits.Length - 1);
        return v >= 100 ? $"{v:0} {SpeedUnits[i]}" : $"{v:0.0} {SpeedUnits[i]}";
    }
}
