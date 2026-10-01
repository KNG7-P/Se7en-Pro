using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Se7enPro.Models;
using Se7enPro.Services;

namespace Se7enPro.ViewModels;

public enum SettingsTab
{
    General,
    Network,
    Psiphon,
    Aether,
    Tor,
    Shard,
    Chained
}

public sealed record SettingOptionItem : LocalizedItem
{
    public string Key { get; init; } = "";

    public string English { get; init; } = "";

        public bool Localizable { get; init; } = true;

    public SettingOptionItem() { }

    public SettingOptionItem(string key, string english)
    {
        Key = key;
        English = english;
    }

    public string Display => Localizable ? Loc.Of(English) : English;

    public override string ToString() => Display;
}

public sealed record CloseActionOption : LocalizedItem
{
    public string Value { get; init; } = "";

    public string English { get; init; } = "";

    public string Display => Loc.Of(English);

    public override string ToString() => Display;
}

public sealed partial class SettingsViewModel : PageViewModelBase
{
    private readonly ISettingsService _settingsService;
    private readonly IThemeService _themeService;
    private readonly ITunnelCoreManager _tunnel;
    private readonly IStartupRegistration _startup;
    private readonly ICoreUpdateService _coreUpdateService;
    private readonly ShardEngine _shardEngine;
    private readonly V2RayEngine _v2rayEngine;

    private bool _suppressThemeSideEffects;

    private bool _suppressRegionSideEffects;

    public override string Title => "Settings";
    public override string Route => "settings";
    public override string Icon => "Cog";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGeneralTab))]
    [NotifyPropertyChangedFor(nameof(IsNetworkTab))]
    [NotifyPropertyChangedFor(nameof(IsPsiphonTab))]
    [NotifyPropertyChangedFor(nameof(IsAetherTab))]
    [NotifyPropertyChangedFor(nameof(IsTorTab))]
    [NotifyPropertyChangedFor(nameof(IsShardTab))]
    [NotifyPropertyChangedFor(nameof(IsChainedTab))]
    private SettingsTab _selectedTab = SettingsTab.General;

    public bool IsGeneralTab => SelectedTab == SettingsTab.General;
    public bool IsNetworkTab => SelectedTab == SettingsTab.Network;
    public bool IsPsiphonTab => SelectedTab == SettingsTab.Psiphon;
    public bool IsAetherTab => SelectedTab == SettingsTab.Aether;
    public bool IsTorTab => SelectedTab == SettingsTab.Tor;
    public bool IsShardTab => SelectedTab == SettingsTab.Shard;
    public bool IsChainedTab => SelectedTab == SettingsTab.Chained;

    [RelayCommand]
    public void SelectTab(object? parameter)
    {
        if (parameter is SettingsTab tab)
        {
            SelectedTab = tab;
        }
        else if (parameter is string str && Enum.TryParse<SettingsTab>(str, true, out var parsed))
        {
            SelectedTab = parsed;
        }
    }

    public SettingsViewModel(
        ISettingsService settingsService,
        IThemeService themeService,
        ITunnelCoreManager tunnel,
        IStartupRegistration startup,
        ICoreUpdateService coreUpdateService,
        IAppUpdateService appUpdateService,
        ShardEngine shardEngine,
        V2RayEngine v2rayEngine)
    {
        _settingsService = settingsService;
        _themeService = themeService;
        _tunnel = tunnel;
        _startup = startup;
        _coreUpdateService = coreUpdateService;
        _appUpdate = appUpdateService;
        _shardEngine = shardEngine;
        _v2rayEngine = v2rayEngine;

        
        
        
        _aetherInstalledVersion = "v1.7.0";
        _aetherLatestVersion = _aetherInstalledVersion;
        _torInstalledVersion = "v0.4.9.11";
        _torLatestVersion = _torInstalledVersion;
        _ = RefreshInstalledVersionsAsync();

        var s = _settingsService.Settings;
        _selectedTheme = s.Theme;
        
        
        _selectedLanguage = s.Language switch { "ru" => "ru", "zh" => "zh", _ => "en" };
        _appInstalledVersion = _appUpdate.InstalledVersion;
        _appLatestVersion = _appInstalledVersion;
        _selectedRegion = string.IsNullOrEmpty(s.EgressRegion) ? "auto" : s.EgressRegion;
        _setSystemProxy = s.SetSystemProxy;
        _disableTimeouts = s.DisableTimeouts;
        _socksPort = FormatListenPort(s.LocalSocksProxyPort);
        _httpPort = FormatListenPort(s.LocalHttpProxyPort);
        _useCustomProxyPorts = s.UseCustomProxyPorts;
        _lanAuthEnabled = s.LanAuthEnabled;
        _autoConnect = s.AutoConnect;
        _startWithWindows = _startup.IsEnabled();
        if (_startWithWindows != s.StartWithWindows)
        {
            s.StartWithWindows = _startWithWindows;
            _settingsService.Save();
        }
        _minimizeToTray = s.MinimizeToTray;
        _killSwitchEnabled = s.KillSwitchEnabled;
        _selectedCloseAction = ResolveCloseAction(s.OnCloseAction);
        _allowLanConnections = s.AllowLanConnections;
        _lanProxyUsername = s.LanProxyUsername;
        _lanProxyPassword = s.LanProxyPassword;
        _ = DetectLanIpAsync();

        ParseUpstreamProxy(
            s.UpstreamProxy,
            out var parsedScheme,
            out var parsedHost,
            out var parsedPort,
            out var parsedUser,
            out var parsedPass);
        _selectedProxyScheme = NormalizeScheme(
            !string.IsNullOrEmpty(s.UpstreamProxyScheme) ? s.UpstreamProxyScheme : parsedScheme);
        _proxyHost = parsedHost;
        _proxyPort = parsedPort;
        _proxyUsername = !string.IsNullOrEmpty(s.UpstreamProxyUsername)
            ? s.UpstreamProxyUsername
            : parsedUser;
        _proxyPassword = !string.IsNullOrEmpty(s.UpstreamProxyPassword)
            ? s.UpstreamProxyPassword
            : parsedPass;
        _upstreamProxyEnabled = s.UpstreamProxyEnabled;

        _selectedProtocolMode = s.ProtocolMode switch
        {
            "direct" => "direct",
            "cdn_fronting" => "cdn_fronting",
            "conduit" => "conduit",
            _ => "auto",
        };
        
        
        
        
        if (_selectedProtocolMode == "direct" && string.IsNullOrWhiteSpace(_proxyHost))
            _upstreamProxyEnabled = false;
        _conduitMode = s.ConduitMode ?? "auto";
        _conduitCompartmentId = s.ConduitCompartmentId ?? "";
        _conduitRejectCensoredCountries = s.ConduitRejectCensoredCountries;
        _beastMode = s.BeastMode;
        _cdnFrontingCustomIpList = s.CdnFrontingCustomIpList;
        _cdnFrontingCustomSni = s.CdnFrontingCustomSni;
        _autoFindIpAndSni = s.AutoFindIpAndSni;
        _saveFoundIpsAndSni = s.SaveFoundIpsAndSni;
        _cdnFrontingSkipCertVerify = s.CdnFrontingSkipCertVerify;
        _establishTunnelTimeoutSeconds = s.EstablishTunnelTimeoutSeconds ?? 300;
        InitCdnProviders(s.FrontedMeekCDNScanBuiltInSets);

        _selectedConnectionMethod = ConnectionMethodExtensions.ParseConnectionMethod(s.ConnectionMethod).ToToken();
        _selectedAetherProtocol = s.AetherProtocol switch
        {
            "wireguard" => "wireguard",
            "warp" => "warp",
            _ => "masque",
        };
        _aetherCacheEdges = s.AetherCacheEdges;
        _aetherWiwOuterPeer = s.AetherWiwOuterPeer ?? "";
        _aetherWiwInnerPeer = s.AetherWiwInnerPeer ?? "";
        _aetherMimOuterPeer = s.AetherMimOuterPeer ?? "";
        _aetherMimInnerPeer = s.AetherMimInnerPeer ?? "";
        _aetherFragmentSize = s.AetherFragmentSize ?? "";
        _aetherFragmentDelay = s.AetherFragmentDelay ?? "";
        LoadAetherProtocolProfile(_selectedAetherProtocol);
        _aetherFragment = s.AetherFragment;
        _aetherMasqueTransport = AetherEngine.NormalizeTransport(s.AetherMasqueTransport);
        
        
        _selectedTorExitCountry = string.IsNullOrWhiteSpace(s.TorExitCountry)
            ? "auto"
            : s.TorExitCountry.Trim().ToUpperInvariant();
        _torBridges = s.TorBridges ?? "";
        _selectedChainedOuterTransport = NormalizeChainedOuter(s.ChainedOuterTransport);
        _selectedChainedPsiphonOuterTransport = NormalizeChainedOuter(s.ChainedPsiphonOuterTransport);
        _selectedChainedTorOuterTransport = NormalizeChainedOuter(s.ChainedTorOuterTransport);
        _selectedChainedSubMode = string.IsNullOrEmpty(s.ChainedSubMode) ? "psiphon_warp" : s.ChainedSubMode;
        _selectedV2RayCore = s.V2RayCore == "sing_box" ? "sing_box" : "xray";
        _v2RayInboundPort = (s.V2RayInboundPort > 0 ? s.V2RayInboundPort : 10808).ToString();
        _v2RayRouteDnsThroughV2Ray = s.V2RayRouteDnsThroughV2Ray;
        _v2RayEnableMux = s.V2RayEnableMux;
        _v2RayEnableFragment = s.V2RayEnableFragment;
        LoadV2RayConfigsFromSettings();

        _shardCustomCfIp = s.ShardCustomCfIp ?? "";
        _shardSmartSplit = s.ShardSmartSplit;
        _shardRotateIp = s.ShardRotateIp;
        InitializeShardPoolStats();

        _settingsService.SettingsChanged += OnSettingsServiceChanged;
        _tunnel.StateChanged += OnTunnelStateChanged;
        _tunnel.RouteChanged += OnTunnelRouteChanged;
        _shardEngine.PoolUpdated += OnShardPoolUpdated;
        
        
        Loc.Changed += () =>
        {
            InitCdnProviders(_settingsService.Settings.FrontedMeekCDNScanBuiltInSets);
            
            
            if (!AppUpdateChecked) AppUpdateStatusText = Loc.Of("Click check to verify the latest version");
        };
        RefreshLanProxyInfo();
    }

    private void InitializeShardPoolStats()
    {
        try
        {
            var summary = _shardEngine.GetPoolSummary();
            ApplyShardPoolSummary(summary);
        }
        catch { }
    }

    private void OnShardPoolUpdated(object? sender, ShardPoolInfo summary)
    {
        Ui.Post(() => ApplyShardPoolSummary(summary));
    }

    private void ApplyShardPoolSummary(ShardPoolInfo summary)
    {
        ShardNodeCount = summary.NodeCount > 0 ? summary.NodeCount : 45;
        var custom = _settingsService.Settings.ShardCustomCfIp?.Trim() ?? "";
        var edgeCount = !string.IsNullOrEmpty(custom) ? 1 : 6;
        ShardPathCount = ShardNodeCount * edgeCount;
        if (summary.LastCheckUtc > DateTime.MinValue)
        {
            var dt = summary.LastCheckUtc.ToLocalTime();
            ShardLastCheck = $"{dt:HH:mm} ({dt.Month}/{dt.Day})";
        }
        OnPropertyChanged(nameof(ShardActiveNodesText));
        OnPropertyChanged(nameof(ShardTotalPathsText));
        OnPropertyChanged(nameof(ShardSyncSubtitle));
    }

    private async Task RefreshInstalledVersionsAsync()
    {
        try
        {
            var aetherTask = Task.Run(() => _coreUpdateService.GetInstalledVersion("aether"));
            var torTask = Task.Run(() => _coreUpdateService.GetInstalledVersion("tor"));
            await Task.WhenAll(aetherTask, torTask);

            AetherInstalledVersion = "v" + aetherTask.Result;
            AetherLatestVersion = AetherInstalledVersion;
            TorInstalledVersion = "v" + torTask.Result;
            TorLatestVersion = TorInstalledVersion;
        }
        catch
        {
            
        }
    }

    private void OnTunnelStateChanged(object? sender, ConnectionState e)
    {
        Ui.Post(() =>
        {
            RefreshLanProxyEndpoints();
            OnPropertyChanged(nameof(DisplaySocksPort));
            OnPropertyChanged(nameof(DisplayHttpPort));
            OnPropertyChanged(nameof(IsShardConnected));
            OnPropertyChanged(nameof(ShardConnectedNodeDisplay));
            OnPropertyChanged(nameof(ShardConnectedNodeDesc));
        });
    }

    private void OnTunnelRouteChanged(object? sender, EventArgs e)
    {
        Ui.Post(() => OnPropertyChanged(nameof(ShardConnectedNodeDisplay)));
    }

    [ObservableProperty] private string _lanProxyInfo = "";

    public void RefreshLanProxyInfo()
    {
        if (!AllowLanConnections)
        {
            LanProxyInfo = "";
            return;
        }

        var ips = GetLanIpv4Addresses().ToList();
        var sb = new StringBuilder();

        var socksPort = ResolveActivePort(_tunnel.SocksProxyPort, _settingsService.Settings.LocalSocksProxyPort);
        var httpPort = ResolveActivePort(_tunnel.HttpProxyPort, _settingsService.Settings.LocalHttpProxyPort);

        if (ips.Count == 0)
        {
            sb.AppendLine(Loc.Of("No LAN IPv4 addresses detected on this PC."));
        }
        else
        {
            sb.AppendLine(Loc.Of("This PC's LAN addresses — configure other devices' proxy to point here:"));
            foreach (var (ip, adapter) in ips)
            {
                sb.Append("  ");
                sb.Append(ip);
                if (!string.IsNullOrEmpty(adapter))
                {
                    sb.Append("  (");
                    sb.Append(adapter);
                    sb.Append(")");
                }
                sb.AppendLine();
            }
            sb.AppendLine();
            sb.Append("  HTTP proxy port:  ");
            sb.AppendLine(httpPort);
            sb.Append("  SOCKS proxy port: ");
            sb.AppendLine(socksPort);
        }

        LanProxyInfo = sb.ToString().TrimEnd();
    }

    private static string ResolveActivePort(int liveValue, int configuredValue)
    {
        if (liveValue > 0) return liveValue.ToString();
        if (configuredValue > 0) return configuredValue + " (configured)";
        return "auto — assigned when connected";
    }

    private static IEnumerable<(string Ip, string Adapter)> GetLanIpv4Addresses()
    {
        NetworkInterface[] nics;
        try { nics = NetworkInterface.GetAllNetworkInterfaces(); }
        catch { yield break; }

        foreach (var ni in nics)
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            switch (ni.NetworkInterfaceType)
            {
                case NetworkInterfaceType.Loopback:
                case NetworkInterfaceType.Tunnel:
                    continue;
            }
            IPInterfaceProperties props;
            try { props = ni.GetIPProperties(); }
            catch { continue; }
            foreach (var addr in props.UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var ip = addr.Address.ToString();
                if (ip.StartsWith("169.254.", StringComparison.Ordinal)) continue;
                yield return (ip, ni.Name);
            }
        }
    }

    public ObservableCollection<string> Themes { get; } = new() { "dark", "light", "system" };

    public ObservableCollection<SettingOptionItem> ThemeOptions { get; } = new()
    {
        new() { Key = "dark", English = "Midnight Dark" },
        new() { Key = "light", English = "Frost Light" },
    };

    
    
    public ObservableCollection<SettingOptionItem> LanguageOptions { get; } = new()
    {
        new() { Key = "en", English = "English", Localizable = false },
        new() { Key = "ru", English = "Русский", Localizable = false },
        new() { Key = "zh", English = "简体中文", Localizable = false },
    };

    [ObservableProperty] private string _selectedLanguage = "en";
    partial void OnSelectedLanguageChanged(string value)
    {
        _settingsService.Settings.Language = value;
        _settingsService.Save();

        
        
        Loc.Apply(value);
    }

    
    [ObservableProperty] private bool _isClearingCache;
    [ObservableProperty] private bool _isResetSettingsDialogOpen;
    [ObservableProperty] private bool _isMaintenanceToastOpen;
    [ObservableProperty] private string _maintenanceToastMessage = "";

    
    [ObservableProperty] private string _appInstalledVersion = "1.0.5";
    [ObservableProperty] private string _appLatestVersion = "1.0.5";
    [ObservableProperty] private bool _hasAppUpdate;
    [ObservableProperty] private bool _isCheckingAppUpdate;
    [ObservableProperty] private bool _appUpdateChecked;
    [ObservableProperty] private string _appUpdateStatusText = "Click check to verify the latest version";
    [ObservableProperty] private string _appUpdateDownloadUrl = "https://github.com/KNG7-P/Se7en-Pro/releases/latest";
    [ObservableProperty] private string _appUpdateStatusColor = "#666B82";

        public enum UpdatePhase { Idle, Checking, Ready, Downloading, ReadyToInstall, Installing, Current, Failed }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAppDownload))]
    [NotifyPropertyChangedFor(nameof(ShowAppInstall))]
    [NotifyPropertyChangedFor(nameof(ShowAppCancel))]
    [NotifyPropertyChangedFor(nameof(ShowAppInstalling))]
    [NotifyPropertyChangedFor(nameof(ShowAppReleases))]
    [NotifyPropertyChangedFor(nameof(ShowAppUpdateProgress))]
    [NotifyPropertyChangedFor(nameof(IsAppUpdateBusy))]
    [NotifyPropertyChangedFor(nameof(AppUpdateActionText))]
    private UpdatePhase _appUpdatePhase = UpdatePhase.Idle;

    [ObservableProperty] private int _appUpdateProgress;
    [ObservableProperty] private string _appUpdateProgressText = "";

    public bool ShowAppDownload => AppUpdatePhase == UpdatePhase.Ready;
    public bool ShowAppInstall => AppUpdatePhase == UpdatePhase.ReadyToInstall;
    public bool ShowAppCancel => AppUpdatePhase == UpdatePhase.Downloading;
    public bool ShowAppInstalling => AppUpdatePhase == UpdatePhase.Installing;
    public bool ShowAppReleases => AppUpdatePhase is UpdatePhase.Idle or UpdatePhase.Current or UpdatePhase.Failed;
    public bool ShowAppUpdateProgress => AppUpdatePhase is UpdatePhase.Downloading or UpdatePhase.ReadyToInstall;
    public bool IsAppUpdateBusy => AppUpdatePhase is UpdatePhase.Checking or UpdatePhase.Downloading or UpdatePhase.Installing;

    public string AppUpdateActionText => AppUpdatePhase switch
    {
        UpdatePhase.Ready => string.Format(Loc.Of("Download v{0}"), AppLatestVersion),
        UpdatePhase.ReadyToInstall => Loc.Of("Install"),
        UpdatePhase.Downloading => Loc.Of("Downloading..."),
        UpdatePhase.Installing => Loc.Of("Installing..."),
        _ => Loc.Of("Update Now"),
    };

    private readonly IAppUpdateService _appUpdate;
    private AppReleaseInfo? _appRelease;
    private CancellationTokenSource? _appDownloadCts;
    private string _appDownloadedFile = "";

    public ObservableCollection<Country> Regions { get; } = CountryHelper.BuildSeedRegions();

    [ObservableProperty] private string _selectedTheme = "dark";
    partial void OnSelectedThemeChanged(string value)
    {
        if (_suppressThemeSideEffects) return;
        _settingsService.Settings.Theme = value;
        _settingsService.Save();
        _themeService.ApplyTheme(value);
    }

    [ObservableProperty] private string _selectedRegion = "auto";
    partial void OnSelectedRegionChanged(string value)
    {
        if (_suppressRegionSideEffects) return;

        _settingsService.Settings.EgressRegion = value == "auto" ? "" : value;
        _settingsService.Save();

        _ = _tunnel.RestartAsync();
    }

    [ObservableProperty] private bool _setSystemProxy;
    partial void OnSetSystemProxyChanged(bool value) { _settingsService.Settings.SetSystemProxy = value; _settingsService.Save(); }

    [ObservableProperty] private bool _disableTimeouts;
    partial void OnDisableTimeoutsChanged(bool value) { _settingsService.Settings.DisableTimeouts = value; _settingsService.Save(); }

    [ObservableProperty] private bool _autoConnect;
    partial void OnAutoConnectChanged(bool value) { _settingsService.Settings.AutoConnect = value; _settingsService.Save(); }

    [ObservableProperty] private bool _startWithWindows;
    partial void OnStartWithWindowsChanged(bool value)
    {
        _settingsService.Settings.StartWithWindows = value;
        _settingsService.Save();
        _startup.SetEnabled(value);
    }

    [ObservableProperty] private bool _minimizeToTray;
    partial void OnMinimizeToTrayChanged(bool value) { _settingsService.Settings.MinimizeToTray = value; _settingsService.Save(); }

    [ObservableProperty] private bool _useCustomProxyPorts;
    partial void OnUseCustomProxyPortsChanged(bool value)
    {
        _settingsService.Settings.UseCustomProxyPorts = value;
        _settingsService.Save();
        OnPropertyChanged(nameof(HasPortCollision));
        OnPropertyChanged(nameof(DisplaySocksPort));
        OnPropertyChanged(nameof(DisplayHttpPort));
        RefreshLanProxyEndpoints();
        _ = _tunnel.RestartAsync();
    }

    [ObservableProperty] private bool _lanAuthEnabled;
    partial void OnLanAuthEnabledChanged(bool value)
    {
        _settingsService.Settings.LanAuthEnabled = value;
        _settingsService.Save();
        RefreshLanProxyEndpoints();
        _ = _tunnel.RestartAsync();
    }

    [ObservableProperty] private bool _allowLanConnections;
    partial void OnAllowLanConnectionsChanged(bool value)
    {
        _settingsService.Settings.AllowLanConnections = value;
        _settingsService.Save();
        RefreshLanProxyEndpoints();
        _ = _tunnel.RestartAsync();
    }

    [ObservableProperty] private string _lanProxyUsername = "";
    partial void OnLanProxyUsernameChanged(string value)
    {
        _settingsService.Settings.LanProxyUsername = (value ?? "").Trim();
        DebouncedSave();
        RefreshLanProxyEndpoints();
        DebouncedRestartIfActive();
    }

    [ObservableProperty] private string _lanProxyPassword = "";
    partial void OnLanProxyPasswordChanged(string value)
    {
        _settingsService.Settings.LanProxyPassword = value ?? "";
        DebouncedSave();
        RefreshLanProxyEndpoints();
        DebouncedRestartIfActive();
    }

    [ObservableProperty] private string _hostLanIp = "192.168.1.X";
    [ObservableProperty] private bool _isHostIpCopied;
    [ObservableProperty] private bool _isSocksEndpointCopied;
    [ObservableProperty] private bool _isHttpEndpointCopied;
    [ObservableProperty] private bool _isAuthCopied;

    public string LanSocksEndpoint
    {
        get
        {
            var port = _tunnel.SocksProxyPort > 0 ? _tunnel.SocksProxyPort
                       : (_settingsService.Settings.UseCustomProxyPorts && _settingsService.Settings.LocalSocksProxyPort > 0
                          ? _settingsService.Settings.LocalSocksProxyPort : 10808);
            return $"{HostLanIp}:{port}";
        }
    }

    public string LanHttpEndpoint
    {
        get
        {
            var port = _tunnel.HttpProxyPort > 0 ? _tunnel.HttpProxyPort
                       : (_settingsService.Settings.UseCustomProxyPorts && _settingsService.Settings.LocalHttpProxyPort > 0
                          ? _settingsService.Settings.LocalHttpProxyPort : 10809);
            return $"{HostLanIp}:{port}";
        }
    }

    public string LanAuthDisplay
    {
        get
        {
            var hasUser = LanAuthEnabled && !string.IsNullOrWhiteSpace(LanProxyUsername);
            var hasPass = LanAuthEnabled && !string.IsNullOrWhiteSpace(LanProxyPassword);
            if (hasUser || hasPass)
            {
                var u = hasUser ? LanProxyUsername : "none";
                var p = hasPass ? LanProxyPassword : "none";
                return $"{u} : {p}";
            }
            return Loc.Of("None (Open Access)");
        }
    }

    public bool HasLanAuthCredentials =>
        LanAuthEnabled && (!string.IsNullOrWhiteSpace(LanProxyUsername) || !string.IsNullOrWhiteSpace(LanProxyPassword));

    public void RefreshLanProxyEndpoints()
    {
        RefreshLanProxyInfo();
        OnPropertyChanged(nameof(LanSocksEndpoint));
        OnPropertyChanged(nameof(LanHttpEndpoint));
        OnPropertyChanged(nameof(LanAuthDisplay));
        OnPropertyChanged(nameof(HasLanAuthCredentials));
    }

    [RelayCommand]
    private async Task CopyHostIpAsync()
    {
        SetClipboardSafe(HostLanIp);
        IsHostIpCopied = true;
        await Task.Delay(1500);
        IsHostIpCopied = false;
    }

    [RelayCommand]
    private async Task CopySocksEndpointAsync()
    {
        SetClipboardSafe(LanSocksEndpoint);
        IsSocksEndpointCopied = true;
        await Task.Delay(1500);
        IsSocksEndpointCopied = false;
    }

    [RelayCommand]
    private async Task CopyHttpEndpointAsync()
    {
        SetClipboardSafe(LanHttpEndpoint);
        IsHttpEndpointCopied = true;
        await Task.Delay(1500);
        IsHttpEndpointCopied = false;
    }

    [RelayCommand]
    private async Task CopyAuthAsync()
    {
        var text = $"{LanProxyUsername}:{LanProxyPassword}";
        SetClipboardSafe(text);
        IsAuthCopied = true;
        await Task.Delay(1500);
        IsAuthCopied = false;
    }

    private static void SetClipboardSafe(string text) => Ui.SetClipboard(text);

    private async Task DetectLanIpAsync()
    {
        await Task.Run(() =>
        {
            var ip = DetectLocalIPv4();
            Ui.Invoke(() =>
            {
                HostLanIp = ip;
                RefreshLanProxyEndpoints();
            });
        });
    }

    public static string DetectLocalIPv4()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up ||
                    ni.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    ni.Description.Contains("Wintun", StringComparison.OrdinalIgnoreCase) ||
                    ni.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var ipProps = ni.GetIPProperties();
                foreach (var addr in ipProps.UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(addr.Address))
                    {
                        var ipStr = addr.Address.ToString();
                        if (!ipStr.StartsWith("169.254"))
                            return ipStr;
                    }
                }
            }
        }
        catch { }
        return "192.168.1.X";
    }

    public ObservableCollection<CloseActionOption> CloseActions { get; } = new()
    {
        new CloseActionOption { Value = "ask", English = "Always ask" },
        new CloseActionOption { Value = "tray", English = "Minimize to tray" },
        new CloseActionOption { Value = "exit", English = "Exit application" },
    };

    [ObservableProperty] private CloseActionOption? _selectedCloseAction;
    partial void OnSelectedCloseActionChanged(CloseActionOption? value)
    {
        if (value is null) return;
        _settingsService.Settings.OnCloseAction = value.Value;
        _settingsService.Save();
    }

    private CloseActionOption ResolveCloseAction(string? value)
    {
        var v = (value ?? "ask").ToLowerInvariant();
        if (v == "minimize") v = "tray";
        return CloseActions.FirstOrDefault(o => o.Value == v) ?? CloseActions[0];
    }

    [RelayCommand]
    private async Task ClearNetworkCacheAsync()
    {
        if (IsClearingCache) return;
        IsClearingCache = true;
        try
        {
            await Task.Run(() =>
            {
                try
                {
                    using var p = Process.Start(new ProcessStartInfo
                    {
                        FileName = "ipconfig",
                        Arguments = "/flushdns",
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        WindowStyle = ProcessWindowStyle.Hidden,
                    });
                    p?.WaitForExit(3000);
                }
                catch { }

                try
                {
                    var cacheDir = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "Se7enPro", "Cache");
                    if (System.IO.Directory.Exists(cacheDir))
                    {
                        System.IO.Directory.Delete(cacheDir, true);
                    }
                }
                catch { }
            });

            ShowMaintenanceToast(Loc.Of("Network & DNS cache cleared successfully"));
        }
        finally
        {
            IsClearingCache = false;
        }
    }

    [RelayCommand]
    private void ShowResetSettingsDialog()
    {
        IsResetSettingsDialogOpen = true;
    }

    [RelayCommand]
    private void CloseResetSettingsDialog()
    {
        IsResetSettingsDialogOpen = false;
    }

    [RelayCommand]
    private void ConfirmResetSettings()
    {
        IsResetSettingsDialogOpen = false;
        try
        {
            var def = new UserSettings();
            _settingsService.Settings.Theme = def.Theme;
            _settingsService.Settings.Language = def.Language;
            _settingsService.Settings.VisualEffects = def.VisualEffects;
            _settingsService.Settings.AutoConnect = def.AutoConnect;
            _settingsService.Settings.StartWithWindows = def.StartWithWindows;
            _settingsService.Settings.MinimizeToTray = def.MinimizeToTray;
            _settingsService.Settings.OnCloseAction = def.OnCloseAction;
            _settingsService.Settings.KillSwitchEnabled = def.KillSwitchEnabled;
            _settingsService.Settings.LocalSocksProxyPort = def.LocalSocksProxyPort;
            _settingsService.Settings.UseCustomProxyPorts = def.UseCustomProxyPorts;
            _settingsService.Settings.LanAuthEnabled = def.LanAuthEnabled;
            _settingsService.Settings.AllowLanConnections = def.AllowLanConnections;
            _settingsService.Settings.UpstreamProxy = def.UpstreamProxy;
            _settingsService.Settings.UpstreamProxyEnabled = def.UpstreamProxyEnabled;
            _settingsService.Settings.EgressRegion = def.EgressRegion;
            _settingsService.Settings.ConnectionMethod = def.ConnectionMethod;
            _settingsService.Settings.AetherProtocol = def.AetherProtocol;
            _settingsService.Settings.AetherScanModeMasque = def.AetherScanModeMasque;
            _settingsService.Settings.AetherScanModeWireguard = def.AetherScanModeWireguard;
            _settingsService.Settings.AetherScanModeWarp = def.AetherScanModeWarp;
            _settingsService.Settings.AetherNoizeMasque = def.AetherNoizeMasque;
            _settingsService.Settings.AetherNoizeMim = def.AetherNoizeMim;
            _settingsService.Settings.AetherNoizeWireguard = def.AetherNoizeWireguard;
            _settingsService.Settings.AetherNoizeWarp = def.AetherNoizeWarp;
            _settingsService.Settings.AetherIpVersionMasque = def.AetherIpVersionMasque;
            _settingsService.Settings.AetherIpVersionMim = def.AetherIpVersionMim;
            _settingsService.Settings.AetherIpVersionWireguard = def.AetherIpVersionWireguard;
            _settingsService.Settings.AetherIpVersionWarp = def.AetherIpVersionWarp;
            _settingsService.Settings.AetherEndpointMasque = def.AetherEndpointMasque;
            _settingsService.Settings.AetherEndpointMim = def.AetherEndpointMim;
            _settingsService.Settings.AetherEndpointWireguard = def.AetherEndpointWireguard;
            _settingsService.Settings.AetherEndpointWarp = def.AetherEndpointWarp;
            _settingsService.Settings.AetherWiwOuterPeer = def.AetherWiwOuterPeer;
            _settingsService.Settings.AetherWiwInnerPeer = def.AetherWiwInnerPeer;
            _settingsService.Settings.AetherMimOuterPeer = def.AetherMimOuterPeer;
            _settingsService.Settings.AetherMimInnerPeer = def.AetherMimInnerPeer;
            _settingsService.Settings.AetherExitLoc = def.AetherExitLoc;
            _settingsService.Settings.AetherExitLocMasque = def.AetherExitLocMasque;
            _settingsService.Settings.AetherExitLocMim = def.AetherExitLocMim;
            _settingsService.Settings.AetherExitLocWireguard = def.AetherExitLocWireguard;
            _settingsService.Settings.AetherExitLocWarp = def.AetherExitLocWarp;
            _settingsService.Settings.AetherCacheEdges = def.AetherCacheEdges;
            _settingsService.Settings.AetherScanMode = def.AetherScanMode;
            _settingsService.Settings.AetherScanModeMim = def.AetherScanModeMim;
            _settingsService.Settings.AetherNoize = def.AetherNoize;
            _settingsService.Settings.AetherIpVersion = def.AetherIpVersion;
            _settingsService.Settings.AetherManualPeer = def.AetherManualPeer;
            _settingsService.Settings.AetherFragment = def.AetherFragment;
            _settingsService.Settings.AetherFragmentSize = def.AetherFragmentSize;
            _settingsService.Settings.AetherFragmentDelay = def.AetherFragmentDelay;
            _settingsService.Settings.AetherMasqueTransport = def.AetherMasqueTransport;
            _settingsService.Settings.AetherMasqueQuic = def.AetherMasqueQuic;
            _settingsService.Save();

            SelectedTheme = def.Theme;
            SelectedLanguage = def.Language;
            AutoConnect = def.AutoConnect;
            StartWithWindows = def.StartWithWindows;
            MinimizeToTray = def.MinimizeToTray;
            SelectedCloseAction = ResolveCloseAction(def.OnCloseAction);
            KillSwitchEnabled = def.KillSwitchEnabled;
            UseCustomProxyPorts = def.UseCustomProxyPorts;
            LanAuthEnabled = def.LanAuthEnabled;
            SocksPort = FormatListenPort(def.LocalSocksProxyPort);
            HttpPort = FormatListenPort(def.LocalHttpProxyPort);
            AllowLanConnections = def.AllowLanConnections;
            SelectedRegion = "auto";

            _suppressAetherSideEffects = true;
            try
            {
                SelectedAetherProtocol = def.AetherProtocol;
                AetherCacheEdges = def.AetherCacheEdges;
                AetherWiwOuterPeer = def.AetherWiwOuterPeer;
                AetherWiwInnerPeer = def.AetherWiwInnerPeer;
                AetherMimOuterPeer = def.AetherMimOuterPeer;
                AetherMimInnerPeer = def.AetherMimInnerPeer;
                AetherExitLoc = def.AetherExitLoc;
                AetherFragment = def.AetherFragment;
                AetherFragmentSize = def.AetherFragmentSize;
                AetherFragmentDelay = def.AetherFragmentDelay;
                AetherMasqueTransport = def.AetherMasqueTransport;
                LoadAetherProtocolProfile(def.AetherProtocol);
            }
            finally
            {
                _suppressAetherSideEffects = false;
            }

            _settingsService.Settings.ShardCustomCfIp = def.ShardCustomCfIp;
            _settingsService.Settings.ShardSmartSplit = def.ShardSmartSplit;
            _settingsService.Settings.ShardRotateIp = def.ShardRotateIp;

            ShardCustomCfIp = def.ShardCustomCfIp;
            ShardSmartSplit = def.ShardSmartSplit;
            ShardRotateIp = def.ShardRotateIp;

            ShowMaintenanceToast(Loc.Of("Settings restored to factory defaults"));
        }
        catch (Exception ex)
        {
            ShowMaintenanceToast(string.Format(Loc.Of("Failed to reset settings: {0}"), ex.Message));
        }
    }

    private void ShowMaintenanceToast(string message)
    {
        MaintenanceToastMessage = message;
        IsMaintenanceToastOpen = true;
        _ = Task.Run(async () =>
        {
            await Task.Delay(3000);
            Ui.Invoke(() => IsMaintenanceToastOpen = false);
        });
    }

    [RelayCommand]
    private async Task CheckAppUpdateAsync()
    {
        
        if (IsAppUpdateBusy || AppUpdatePhase == UpdatePhase.ReadyToInstall) return;
        IsCheckingAppUpdate = true;
        AppUpdatePhase = UpdatePhase.Checking;
        AppUpdateStatusText = Loc.Of("Checking GitHub...");
        AppUpdateStatusColor = "#8B93A7";

        try
        {
            var release = await _appUpdate.CheckAsync();
            _appRelease = release;
            AppLatestVersion = release.Version;
            AppUpdateDownloadUrl = string.IsNullOrEmpty(release.AssetUrl) ? release.ReleasePageUrl : release.AssetUrl;
            AppUpdateChecked = true;
            HasAppUpdate = CompareVersions(AppInstalledVersion, release.Version);

            if (HasAppUpdate)
            {
                AppUpdatePhase = UpdatePhase.Ready;
                AppUpdateStatusText = string.Format(Loc.Of("New version available: v{0}"), release.Version);
                AppUpdateStatusColor = "#00D4FF";
            }
            else
            {
                AppUpdatePhase = UpdatePhase.Current;
                AppUpdateStatusText = Loc.Of("Application is up to date");
                AppUpdateStatusColor = "#10B981";
            }
        }
        catch (Exception ex)
        {
            AppUpdateChecked = true;
            AppUpdatePhase = UpdatePhase.Failed;
            AppUpdateStatusText = ex.Message;
            AppUpdateStatusColor = "#EF4444";
        }
        finally
        {
            IsCheckingAppUpdate = false;
        }
    }

    [RelayCommand]
    private async Task DownloadAppUpdateAsync()
    {
        var release = _appRelease;
        if (release is null || AppUpdatePhase != UpdatePhase.Ready) return;

        AppUpdatePhase = UpdatePhase.Downloading;
        AppUpdateProgress = 0;
        AppUpdateProgressText = "";
        AppUpdateStatusText = Loc.Of("Downloading the update...");
        AppUpdateStatusColor = "#00D4FF";

        var cts = new CancellationTokenSource();
        _appDownloadCts = cts;
        var total = release.SizeBytes;
        var progress = new Progress<double>(pct =>
        {
            AppUpdateProgress = (int)Math.Round(pct);
            AppUpdateProgressText = total > 0
                ? $"{FormatBytes(total * pct / 100)} / {FormatBytes(total)}"
                : "";
        });

        try
        {
            _appDownloadedFile = await _appUpdate.DownloadAsync(release, progress, cts.Token);
            AppUpdatePhase = UpdatePhase.ReadyToInstall;
            AppUpdateProgress = 100;
            AppUpdateStatusText = Loc.Of("Downloaded. Click Install to apply it and restart.");
            AppUpdateStatusColor = "#10B981";
        }
        catch (OperationCanceledException)
        {
            AppUpdatePhase = UpdatePhase.Ready;
            AppUpdateProgress = 0;
            AppUpdateStatusText = Loc.Of("Download cancelled.");
            AppUpdateStatusColor = "#8B93A7";
        }
        catch (Exception ex)
        {
            AppUpdatePhase = UpdatePhase.Failed;
            AppUpdateProgress = 0;
            AppUpdateStatusText = ex.Message;
            AppUpdateStatusColor = "#EF4444";
        }
        finally
        {
            cts.Dispose();
            _appDownloadCts = null;
        }
    }

    [RelayCommand]
    private void CancelAppUpdateDownload()
    {
        try { _appDownloadCts?.Cancel(); } catch { }
    }

    [RelayCommand]
    private async Task InstallAppUpdateAsync()
    {
        var release = _appRelease;
        if (release is null || AppUpdatePhase != UpdatePhase.ReadyToInstall) return;

        AppUpdatePhase = UpdatePhase.Installing;
        AppUpdateStatusText = Loc.Of("Installing... Se7en Pro will restart by itself.");
        AppUpdateStatusColor = "#F59E0B";

        try
        {
            
            
            if (_tunnel.State != ConnectionState.Disconnected) await _tunnel.StopAsync();

            await _appUpdate.InstallAsync(_appDownloadedFile, release.Version);
            await Task.Delay(600);
            ForceExitCurrentInstance();
        }
        catch (Exception ex)
        {
            AppUpdatePhase = UpdatePhase.ReadyToInstall;
            AppUpdateStatusText = ex.Message;
            AppUpdateStatusColor = "#EF4444";
        }
    }

    private static void ForceExitCurrentInstance() => Ui.ExitWithoutPrompt();

    private static string FormatBytes(double bytes)
    {
        if (bytes <= 0) return "0 MB";
        double mb = bytes / (1024 * 1024);
        return mb >= 1024 ? $"{mb / 1024:0.0} GB" : $"{mb:0.0} MB";
    }

    [RelayCommand]
    private void OpenReleasesPage()
    {
        var url = !string.IsNullOrEmpty(AppUpdateDownloadUrl)
            ? AppUpdateDownloadUrl
            : "https://github.com/KNG7-P/Se7en-Pro/releases/latest";
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { }
    }

    private static bool CompareVersions(string installed, string latest)
    {
        try
        {
            var instParts = installed.Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).ToList();
            var latParts = latest.Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).ToList();
            var len = Math.Max(instParts.Count, latParts.Count);
            while (instParts.Count < len) instParts.Add(0);
            while (latParts.Count < len) latParts.Add(0);
            for (var i = 0; i < len; i++)
            {
                if (latParts[i] > instParts[i]) return true;
                if (latParts[i] < instParts[i]) return false;
            }
        }
        catch { }
        return false;
    }

    
    
    
    public ObservableCollection<SettingOptionItem> ProxySchemes { get; } = new()
    {
        new("http", "HTTP / HTTPS Proxy"),
        new("socks5", "SOCKS5 Proxy (Recommended)"),
        new("socks5h", "SOCKS5H Proxy (Remote DNS)"),
        new("socks4a", "SOCKS4A Proxy (Legacy)"),
    };

    [ObservableProperty] private string _selectedProxyScheme = "http";
    partial void OnSelectedProxySchemeChanged(string value)
    {
        PersistProxySettings();
        OnPropertyChanged(nameof(SupportsProxyCredentials));
        OnPropertyChanged(nameof(SupportsProxyPassword));
    }

    [ObservableProperty] private string _proxyHost = "";
    partial void OnProxyHostChanged(string value)
    {
        OnPropertyChanged(nameof(HasUpstreamProxy));
        OnPropertyChanged(nameof(UpstreamProxyWarning));
        OnPropertyChanged(nameof(HasUpstreamProxyWarning));
        PersistProxySettings();
    }

    [ObservableProperty] private string _proxyPort = "";
    partial void OnProxyPortChanged(string value) => PersistProxySettings();

    [ObservableProperty] private string _proxyUsername = "";
    partial void OnProxyUsernameChanged(string value) => PersistProxySettings();

    [ObservableProperty] private string _proxyPassword = "";
    partial void OnProxyPasswordChanged(string value) => PersistProxySettings();

    private void PersistProxySettings()
    {
        var scheme = NormalizeScheme(SelectedProxyScheme);
        var user = (ProxyUsername ?? "").Trim();
        var pass = string.Equals(scheme, "socks4a", StringComparison.OrdinalIgnoreCase)
            ? ""
            : ProxyPassword ?? "";

        var combined = BuildUpstreamProxy(scheme, ProxyHost, ProxyPort, user, pass);
        _settingsService.Settings.UpstreamProxy = combined;
        _settingsService.Settings.UpstreamProxyScheme = scheme;
        _settingsService.Settings.UpstreamProxyUsername = user;
        _settingsService.Settings.UpstreamProxyPassword = pass;

        
        
        
        DebouncedSave();
    }

    
    
    
    
    
    
    
    

    private readonly Debouncer _saveDebouncer = new(TimeSpan.FromMilliseconds(400));
    private readonly Debouncer _restartDebouncer = new(TimeSpan.FromMilliseconds(700));

        private void DebouncedSave() => _saveDebouncer.Schedule(() => _settingsService.Save());

        private void DebouncedRestartIfActive()
    {
        if (_tunnel.State is not (Se7enPro.Models.ConnectionState.Connected or Se7enPro.Models.ConnectionState.Connecting))
        {
            
            return;
        }

        _restartDebouncer.Schedule(() => _ = _tunnel.RestartAsync());
    }

        [ObservableProperty] private bool _upstreamProxyEnabled = true;
    partial void OnUpstreamProxyEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(HasUpstreamProxy));
        OnPropertyChanged(nameof(UpstreamProxyWarning));
        OnPropertyChanged(nameof(HasUpstreamProxyWarning));

        
        
        if (_suppressUpstreamProxySideEffects) return;

        _settingsService.Settings.UpstreamProxyEnabled = value;
        _settingsService.Save();
        _ = _tunnel.RestartAsync();
    }

    private bool _suppressUpstreamProxySideEffects;

    [RelayCommand]
    private void ClearUpstreamProxy()
    {
        var wasEnabled = UpstreamProxyEnabled;
        _suppressUpstreamProxySideEffects = true;
        try
        {
            ProxyHost = "";
            ProxyPort = "";
            ProxyUsername = "";
            ProxyPassword = "";
            UpstreamProxyEnabled = false;
        }
        finally { _suppressUpstreamProxySideEffects = false; }

        OnPropertyChanged(nameof(HasUpstreamProxy));
        OnPropertyChanged(nameof(UpstreamProxyWarning));
        OnPropertyChanged(nameof(HasUpstreamProxyWarning));
        _settingsService.Settings.UpstreamProxyEnabled = false;
        _settingsService.Save();

        
        
        
        if (wasEnabled) _ = _tunnel.RestartAsync();
    }

    public string UpstreamProxyWarning =>
        HasUpstreamProxy
            ? Loc.Of("Every Psiphon connection is dialled through this proxy. If it is dead or "
            + "unreachable the tunnel cannot connect in any protocol mode — turn the switch "
            + "off or clear the address.")
            : "";

    public bool HasUpstreamProxyWarning => HasUpstreamProxy;

        public bool HasUpstreamProxy =>
        !IsUpstreamUnsupported && UpstreamProxyEnabled && !string.IsNullOrWhiteSpace(ProxyHost);

    public bool SupportsProxyCredentials => true;

    
    
    
    public bool SupportsProxyPassword =>
    !string.Equals(NormalizeScheme(SelectedProxyScheme), "socks4a", StringComparison.OrdinalIgnoreCase);

    [ObservableProperty] private string _socksPort = "";
    partial void OnSocksPortChanged(string value)
    {
        _settingsService.Settings.LocalSocksProxyPort = ParseListenPort(value);
        DebouncedSave();
        OnPropertyChanged(nameof(DisplaySocksPort));
        OnPropertyChanged(nameof(HasPortCollision));
        RefreshLanProxyEndpoints();
    }

    [ObservableProperty] private string _httpPort = "";
    partial void OnHttpPortChanged(string value)
    {
        _settingsService.Settings.LocalHttpProxyPort = ParseListenPort(value);
        DebouncedSave();
        OnPropertyChanged(nameof(DisplayHttpPort));
        OnPropertyChanged(nameof(HasPortCollision));
        RefreshLanProxyEndpoints();
    }

    public bool HasPortCollision
    {
        get
        {
            if (!UseCustomProxyPorts) return false;
            var socks = ParseListenPort(SocksPort);
            var http = ParseListenPort(HttpPort);
            return socks > 0 && http > 0 && socks == http;
        }
    }

    public string DisplaySocksPort
    {
        get
        {
            if (!SupportsCustomProxyPorts)
            {
                var live = _tunnel.SocksProxyPort;
                return live > 0 ? $"{live} (Auto)" : "Auto (Dynamic)";
            }
            return SocksPort;
        }
        set
        {
            if (SupportsCustomProxyPorts)
            {
                SocksPort = value;
            }
        }
    }

    public string DisplayHttpPort
    {
        get
        {
            if (!SupportsCustomProxyPorts)
            {
                var live = _tunnel.HttpProxyPort;
                return live > 0 ? $"{live} (Auto)" : "Auto (Dynamic)";
            }
            return HttpPort;
        }
        set
        {
            if (SupportsCustomProxyPorts)
            {
                HttpPort = value;
            }
        }
    }

    [ObservableProperty] private string _saveButtonText = Loc.Of("Save Settings");

    private void OnSettingsServiceChanged(object? sender, EventArgs e)
    {
        if (!Ui.CheckAccess())
        {
            Ui.Post(() => OnSettingsServiceChanged(sender, e));
            return;
        }

        var s = _settingsService.Settings;
        if (!string.Equals(SelectedTheme, s.Theme, StringComparison.Ordinal))
        {
            _suppressThemeSideEffects = true;
            try { SelectedTheme = s.Theme; }
            finally { _suppressThemeSideEffects = false; }
        }

        var externalRegion = string.IsNullOrEmpty(s.EgressRegion) ? "auto" : s.EgressRegion;
        if (!string.Equals(SelectedRegion, externalRegion, StringComparison.Ordinal))
        {
            _suppressRegionSideEffects = true;
            try { SelectedRegion = externalRegion; }
            finally { _suppressRegionSideEffects = false; }
        }

        if (!string.Equals(CdnFrontingCustomIpList, s.CdnFrontingCustomIpList ?? "", StringComparison.Ordinal))
        {
            _suppressCdnIpListSideEffects = true;
            try { CdnFrontingCustomIpList = s.CdnFrontingCustomIpList ?? ""; }
            finally { _suppressCdnIpListSideEffects = false; }
        }

        if (!string.Equals(CdnFrontingCustomSni, s.CdnFrontingCustomSni ?? "", StringComparison.Ordinal))
        {
            _suppressCdnSniSideEffects = true;
            try { CdnFrontingCustomSni = s.CdnFrontingCustomSni ?? ""; }
            finally { _suppressCdnSniSideEffects = false; }
        }

        var externalMethod = ConnectionMethodExtensions.ParseConnectionMethod(s.ConnectionMethod).ToToken();
        if (!string.Equals(SelectedConnectionMethod, externalMethod, StringComparison.Ordinal))
        {
            _suppressMethodSideEffects = true;
            try { SelectedConnectionMethod = externalMethod; }
            finally { _suppressMethodSideEffects = false; }
        }

        var externalProto = s.ProtocolMode ?? "auto";
        if (!string.Equals(SelectedProtocolMode, externalProto, StringComparison.Ordinal))
        {
            _suppressProtocolSideEffects = true;
            try { SelectedProtocolMode = externalProto; }
            finally { _suppressProtocolSideEffects = false; }
        }

        if (!string.Equals(ConduitMode, s.ConduitMode ?? "auto", StringComparison.Ordinal))
        {
            ConduitMode = s.ConduitMode ?? "auto";
        }
        if (!string.Equals(ConduitCompartmentId, s.ConduitCompartmentId ?? "", StringComparison.Ordinal))
        {
            ConduitCompartmentId = s.ConduitCompartmentId ?? "";
        }
        if (ConduitRejectCensoredCountries != s.ConduitRejectCensoredCountries)
        {
            ConduitRejectCensoredCountries = s.ConduitRejectCensoredCountries;
        }
        if (BeastMode != s.BeastMode)
        {
            BeastMode = s.BeastMode;
        }
        if (AutoFindIpAndSni != s.AutoFindIpAndSni)
        {
            AutoFindIpAndSni = s.AutoFindIpAndSni;
        }
        if (SaveFoundIpsAndSni != s.SaveFoundIpsAndSni)
        {
            SaveFoundIpsAndSni = s.SaveFoundIpsAndSni;
        }
        if (KillSwitchEnabled != s.KillSwitchEnabled)
        {
            KillSwitchEnabled = s.KillSwitchEnabled;
        }
        if (CdnFrontingSkipCertVerify != s.CdnFrontingSkipCertVerify)
        {
            CdnFrontingSkipCertVerify = s.CdnFrontingSkipCertVerify;
        }
        var timeout = s.EstablishTunnelTimeoutSeconds ?? 300;
        if (EstablishTunnelTimeoutSeconds != timeout)
        {
            EstablishTunnelTimeoutSeconds = timeout;
        }
        InitCdnProviders(s.FrontedMeekCDNScanBuiltInSets);
        OnPropertyChanged(nameof(CdnCorpusSummary));

        _suppressAetherSideEffects = true;
        try
        {
            var targetProto = s.AetherProtocol switch
            {
                "wireguard" => "wireguard",
                "warp" => "warp",
                _ => "masque",
            };
            if (SelectedAetherProtocol != targetProto)
            {
                SelectedAetherProtocol = targetProto;
            }
            LoadAetherProtocolProfile(SelectedAetherProtocol);
            AetherCacheEdges = s.AetherCacheEdges;
            AetherWiwOuterPeer = s.AetherWiwOuterPeer ?? "";
            AetherWiwInnerPeer = s.AetherWiwInnerPeer ?? "";
            AetherMimOuterPeer = s.AetherMimOuterPeer ?? "";
            AetherMimInnerPeer = s.AetherMimInnerPeer ?? "";
            AetherFragment = s.AetherFragment;
            AetherFragmentSize = s.AetherFragmentSize ?? "";
            AetherFragmentDelay = s.AetherFragmentDelay ?? "";
            AetherMasqueTransport = AetherEngine.NormalizeTransport(s.AetherMasqueTransport);
        }
        finally { _suppressAetherSideEffects = false; }

        if (UpstreamProxyEnabled != s.UpstreamProxyEnabled)
        {
            _suppressUpstreamProxySideEffects = true;
            try { UpstreamProxyEnabled = s.UpstreamProxyEnabled; }
            finally { _suppressUpstreamProxySideEffects = false; }
        }

        var externalExit = string.IsNullOrWhiteSpace(s.TorExitCountry)
            ? "auto"
            : s.TorExitCountry.Trim().ToUpperInvariant();
        _suppressTorSideEffects = true;
        try
        {
            SelectedTorExitCountry = externalExit;
            TorBridges = s.TorBridges ?? "";
        }
        finally { _suppressTorSideEffects = false; }

        if (!string.Equals(ShardCustomCfIp, s.ShardCustomCfIp ?? "", StringComparison.Ordinal))
        {
            ShardCustomCfIp = s.ShardCustomCfIp ?? "";
        }
        if (ShardSmartSplit != s.ShardSmartSplit)
        {
            ShardSmartSplit = s.ShardSmartSplit;
        }
        if (ShardRotateIp != s.ShardRotateIp)
        {
            ShardRotateIp = s.ShardRotateIp;
        }
    }

    private bool _suppressCdnIpListSideEffects;
    private bool _suppressCdnSniSideEffects;

    [RelayCommand]
    private async Task SaveAsync()
    {
        var scheme = NormalizeScheme(SelectedProxyScheme);
        var user = (ProxyUsername ?? "").Trim();

        var pass = string.Equals(scheme, "socks4a", StringComparison.OrdinalIgnoreCase)
            ? ""
            : ProxyPassword ?? "";

        var combined = BuildUpstreamProxy(scheme, ProxyHost, ProxyPort, user, pass);
        _settingsService.Settings.UpstreamProxy = combined;
        _settingsService.Settings.UpstreamProxyScheme = scheme;
        _settingsService.Settings.UpstreamProxyUsername = user;
        _settingsService.Settings.UpstreamProxyPassword = pass;

        var socks = ParseListenPort(SocksPort);
        var http = ParseListenPort(HttpPort);
        _settingsService.Settings.LocalSocksProxyPort = socks;
        _settingsService.Settings.LocalHttpProxyPort = http;

        SocksPort = FormatListenPort(socks);
        HttpPort = FormatListenPort(http);
        ProxyUsername = user;
        ProxyPassword = pass;

        _settingsService.Save();

        SaveButtonText = Loc.Of("Saved!");
        await Task.Delay(2000);
        SaveButtonText = Loc.Of("Save Settings");
    }

    private static string FormatListenPort(int port)
    => port is >= 1 and <= 65535 ? port.ToString() : "";

    private static int ParseListenPort(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        if (!int.TryParse(text.Trim(), out var p)) return 0;
        return p is >= 1 and <= 65535 ? p : 0;
    }

    public sealed record ProtocolOption(string Key, string English, string EnglishDescription) : LocalizedItem
    {
        public string Display => Loc.Of(English);
        public string Description => Loc.Of(EnglishDescription);
    }

    public ObservableCollection<ProtocolOption> ProtocolModeOptions { get; } = new()
    {
        new("auto", "Automatic Negotiation", "Let Psiphon automatically discover and select the most reliable protocol"),
        new("direct", "Direct Connection", "Force standard direct-egress protocols without domain fronting"),
        new("cdn_fronting", "CDN Domain Fronting", "Route traffic through edge CDN servers using TLS domain fronting"),
        new("conduit", "Conduit Relay", "Route encrypted traffic through decentralized WebRTC peer-to-peer relay stations"),
    };

    [ObservableProperty] private string _selectedProtocolMode = "auto";
    partial void OnSelectedProtocolModeChanged(string value)
    {
        if (_suppressProtocolSideEffects) return;
        var mode = value ?? "auto";
        _settingsService.Settings.ProtocolMode = mode;
        _settingsService.Save();

        
        
        
        
        
        
        OnPropertyChanged(nameof(IsAutoProtocolMode));
        OnPropertyChanged(nameof(IsDirectMode));
        OnPropertyChanged(nameof(IsCdnFrontingMode));
        OnPropertyChanged(nameof(IsConduitMode));
        OnPropertyChanged(nameof(IsConduitPeerMode));
        OnPropertyChanged(nameof(ShowBeastMode));
        OnPropertyChanged(nameof(CanUseAutoFind));
        OnPropertyChanged(nameof(IsUpstreamUnsupported));
        OnPropertyChanged(nameof(IsUpstreamSupported));
        OnPropertyChanged(nameof(UpstreamUnsupportedWarningText));
        OnPropertyChanged(nameof(UpstreamProxySwitchDescription));
        OnPropertyChanged(nameof(HasUpstreamProxy));
        OnPropertyChanged(nameof(UpstreamProxyWarning));
        OnPropertyChanged(nameof(HasUpstreamProxyWarning));
        OnPropertyChanged(nameof(CanEditAdvancedTunneling));

        
        
        
        
        
        if (mode == "direct" && string.IsNullOrWhiteSpace(ProxyHost) && UpstreamProxyEnabled)
        {
            _suppressUpstreamProxySideEffects = true;
            try { UpstreamProxyEnabled = false; }
            finally { _suppressUpstreamProxySideEffects = false; }

            _settingsService.Settings.UpstreamProxyEnabled = false;
            _settingsService.Save();
            OnPropertyChanged(nameof(HasUpstreamProxy));
            OnPropertyChanged(nameof(UpstreamProxyWarning));
            OnPropertyChanged(nameof(HasUpstreamProxyWarning));
        }
    }

    public bool IsAutoProtocolMode => SelectedProtocolMode == "auto";
    public bool IsDirectMode => SelectedProtocolMode == "direct";
    public bool IsCdnFrontingMode => SelectedProtocolMode == "cdn_fronting";
    public bool IsConduitMode => SelectedProtocolMode == "conduit";
    public bool ShowBeastMode => SelectedProtocolMode is "direct" or "cdn_fronting";
    public bool CanUseAutoFind => IsCdnFrontingMode;
    public bool CanEditAdvancedTunneling => !HasUpstreamProxy;

        public bool IsUpstreamUnsupported => TunnelCoreManager.BypassesUpstreamProxy(SelectedProtocolMode);
    public bool IsUpstreamSupported => !IsUpstreamUnsupported;

    public string UpstreamUnsupportedWarningText => SelectedProtocolMode switch
    {
        "auto" => Loc.Of("Upstream proxy is not used in Automatic Negotiation mode: Psiphon picks and dials its own protocols directly, so a relay would override the negotiation it is meant to perform. Switch to Direct Connection to use one."),
        "conduit" => Loc.Of("Upstream proxy is not supported in Conduit (Inproxy WebRTC) mode. Conduit requires direct peer connections."),
        "cdn_fronting" => Loc.Of("Upstream proxy is not supported in CDN Fronting mode. CDN Fronting requires direct connections to CDN edge IP addresses."),
        _ => ""
    };

    public string UpstreamProxySwitchDescription => IsUpstreamUnsupported
        ? string.Format(
            Loc.Of("Disabled for {0} (direct connection required)"),
            SelectedProtocolMode == "conduit" ? Loc.Of("Conduit") : Loc.Of("CDN Fronting"))
        : Loc.Of("Route Psiphon tunnel traffic through an existing intermediate proxy");

    public sealed record ConduitStationOption(string Key, string English) : LocalizedItem
    {
        public string Display => Loc.Of(English);
    }
    public ObservableCollection<ConduitStationOption> ConduitStationOptions { get; } = new()
    {
        new("auto", "Auto Relay"),
        new("public", "Public Network"),
        new("peer", "Personal Peering"),
    };

    [ObservableProperty] private string _conduitMode = "auto";
    partial void OnConduitModeChanged(string value)
    {
        _settingsService.Settings.ConduitMode = value ?? "auto";
        _settingsService.Save();
        OnPropertyChanged(nameof(IsConduitPeerMode));
    }

    public bool IsConduitPeerMode => ConduitMode == "peer";
    public bool IsConduitCustomMode => IsConduitPeerMode;

    [ObservableProperty] private string _conduitCompartmentId = "";
    partial void OnConduitCompartmentIdChanged(string value)
    {
        _settingsService.Settings.ConduitCompartmentId = value ?? "";
        DebouncedSave();
    }

    [ObservableProperty] private bool _conduitRejectCensoredCountries = true;
    partial void OnConduitRejectCensoredCountriesChanged(bool value)
    {
        _settingsService.Settings.ConduitRejectCensoredCountries = value;
        _settingsService.Save();
    }

    [ObservableProperty] private bool _beastMode;
    partial void OnBeastModeChanged(bool value)
    {
        _settingsService.Settings.BeastMode = value;
        _settingsService.Save();
    }

    [ObservableProperty] private bool _cdnFrontingSkipCertVerify;
    partial void OnCdnFrontingSkipCertVerifyChanged(bool value)
    {
        _settingsService.Settings.CdnFrontingSkipCertVerify = value;
        _settingsService.Save();
    }

    [ObservableProperty] private string _cdnFrontingCustomIpList = "";
    partial void OnCdnFrontingCustomIpListChanged(string value)
    {
        if (_suppressCdnIpListSideEffects) return;
        _settingsService.Settings.CdnFrontingCustomIpList = value ?? "";
        DebouncedSave();
    }

    [ObservableProperty] private string _cdnFrontingCustomSni = "";
    partial void OnCdnFrontingCustomSniChanged(string value)
    {
        if (_suppressCdnSniSideEffects) return;
        _settingsService.Settings.CdnFrontingCustomSni = value ?? "";
        DebouncedSave();
    }

    [ObservableProperty] private bool _autoFindIpAndSni;
    partial void OnAutoFindIpAndSniChanged(bool value)
    {
        _settingsService.Settings.AutoFindIpAndSni = value;
        _settingsService.Save();
        _ = _tunnel.RestartAsync();
    }

    [ObservableProperty] private bool _saveFoundIpsAndSni;
    partial void OnSaveFoundIpsAndSniChanged(bool value)
    {
        _settingsService.Settings.SaveFoundIpsAndSni = value;
        _settingsService.Save();
    }

    public sealed record TimeoutOption(int Seconds, string English) : LocalizedItem
    {
        public string Display => Loc.Of(English);
    }
    public ObservableCollection<TimeoutOption> EstablishTimeoutOptions { get; } = new()
    {
        new(0, "Forever (no timeout)"),
        new(120, "2 minutes"),
        new(300, "5 minutes (recommended)"),
        new(600, "10 minutes"),
        new(1800, "30 minutes"),
    };

    [ObservableProperty] private int _establishTunnelTimeoutSeconds = 300;
    partial void OnEstablishTunnelTimeoutSecondsChanged(int value)
    {
        _settingsService.Settings.EstablishTunnelTimeoutSeconds = value;
        _settingsService.Save();
    }

    public ObservableCollection<CdnProviderItem> CdnProviders { get; } = new();

    public string CdnCorpusSummary
    {
        get
        {
            var sets = _settingsService.Settings.FrontedMeekCDNScanBuiltInSets;
            return sets.Count > 0 ? $"{sets.Count}/10" : Loc.Of("All");
        }
    }

    public void InitCdnProviders(List<string>? selectedSets)
    {
        var selected = selectedSets ?? new();
        var defs = new (string Key, string Name, string Badge, string Color, string Icon)[]
        {
            ("cloudflare", "Cloudflare", Loc.Of("Anycast CDN"), "#F38020", "CloudQueue"),
            ("google", "Google", Loc.Of("GCP Edge"), "#4285F4", "Earth"),
            ("psiphon-akamai", Loc.Of("Psiphon (Akamai)"), Loc.Of("Akamai Edge"), "#00A2E8", "HubOutline"),
            ("fastly", "Fastly", Loc.Of("Edge Cloud"), "#FF2B2B", "LightningBolt"),
            ("psiphon-bunny", Loc.Of("Psiphon Bunny"), Loc.Of("Bunny CDN"), "#FF9500", "Speedometer"),
            ("cloudfront", "CloudFront", Loc.Of("AWS CDN"), "#FF9900", "CloudCheck"),
            ("vercel", "Vercel", Loc.Of("Global Edge"), "#94A3B8", "TriangleOutline"),
            ("github", "GitHub", Loc.Of("Pages CDN"), "#A855F7", "CodeTags"),
            ("curated-fronting", Loc.Of("Curated Fronting"), Loc.Of("Direct Fronts"), "#10B981", "CheckDecagram"),
            ("legacy-android-overrides", Loc.Of("Legacy Android"), Loc.Of("Core Fallback"), "#6366F1", "Cellphone"),
        };

        
        
        
        
        
        var wanted = defs.Select(d => d.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        
        for (int i = CdnProviders.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(CdnProviders[i].Key)) CdnProviders.RemoveAt(i);
        }

        for (int i = 0; i < defs.Length; i++)
        {
            var (key, name, badge, color, icon) = defs[i];
            var isSel = selected.Any(x => string.Equals(x, key, StringComparison.OrdinalIgnoreCase));

            var existing = CdnProviders.FirstOrDefault(c =>
                string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase));

            if (existing is null)
            {
                CdnProviders.Insert(Math.Min(i, CdnProviders.Count), new CdnProviderItem
                {
                    Key = key,
                    Name = name,
                    Badge = badge,
                    ColorHex = color,
                    IconKind = icon,
                    IsSelected = isSel,
                });
            }
            else
            {
                
                
                
                existing.Name = name;
                existing.Badge = badge;
                existing.ColorHex = color;
                existing.IconKind = icon;
                existing.IsSelected = isSel;
            }
        }

        RenumberCdnProviders();
    }

        private void RenumberCdnProviders()
    {
        var sets = _settingsService.Settings.FrontedMeekCDNScanBuiltInSets;
        foreach (var p in CdnProviders)
        {
            var idx = sets.FindIndex(x => string.Equals(x, p.Key, StringComparison.OrdinalIgnoreCase));
            p.OrderIndex = idx >= 0 ? idx + 1 : 0;
        }
    }

        [RelayCommand]
    public void SelectCdnPreset(string? keys)
    {
        var list = string.Equals(keys, "all", StringComparison.OrdinalIgnoreCase)
            ? CdnProviders.Select(p => p.Key).ToList()
            : (keys ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(w => CdnProviders.FirstOrDefault(p => string.Equals(p.Key, w, StringComparison.OrdinalIgnoreCase))?.Key)
                .Where(k => !string.IsNullOrEmpty(k))
                .Cast<string>()
                .ToList();

        _settingsService.Settings.FrontedMeekCDNScanBuiltInSets = list;
        _settingsService.Save();
        InitCdnProviders(list);
        OnPropertyChanged(nameof(CdnCorpusSummary));
    }

    [RelayCommand]
    public void ToggleCdnProvider(string key)
    {
        var sets = _settingsService.Settings.FrontedMeekCDNScanBuiltInSets;
        var idx = sets.FindIndex(x => string.Equals(x, key, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0)
        {
            sets.RemoveAt(idx);
        }
        else
        {
            sets.Add(key);
        }
        _settingsService.Save();

        var item = CdnProviders.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));
        if (item != null)
        {
            item.IsSelected = idx < 0;
        }
        RenumberCdnProviders();
        OnPropertyChanged(nameof(CdnCorpusSummary));
    }

    [RelayCommand]
    public void ClearCdnCorpus()
    {
        _settingsService.Settings.FrontedMeekCDNScanBuiltInSets.Clear();
        _settingsService.Save();
        foreach (var p in CdnProviders)
        {
            p.IsSelected = false;
        }
        RenumberCdnProviders();
        OnPropertyChanged(nameof(CdnCorpusSummary));
    }

    
    
    
    
    
    
    
    

    private bool _suppressProtocolSideEffects;
    private bool _suppressMethodSideEffects;
    private bool _suppressAetherSideEffects;
    private bool _suppressTorSideEffects;

    [ObservableProperty] private bool _killSwitchEnabled;
    partial void OnKillSwitchEnabledChanged(bool value)
    {
        _settingsService.Settings.KillSwitchEnabled = value;
        _settingsService.Save();
    }

    public bool CanChangeMethod => _tunnel.State != ConnectionState.Connecting && _tunnel.State != ConnectionState.Disconnecting;

    
    [ObservableProperty] private string _selectedConnectionMethod = "masque";
    partial void OnSelectedConnectionMethodChanged(string value)
    {
        RaiseMethodVisibility();
        if (_suppressMethodSideEffects) return;
        var token = ConnectionMethodExtensions.ParseConnectionMethod(value).ToToken();
        _settingsService.Settings.ConnectionMethod = token;
        _settingsService.Save();

        if (token is "masque" or "wireguard" or "warp_on_warp")
        {
            var targetProto = token switch
            {
                "wireguard" => "wireguard",
                "warp_on_warp" => "warp",
                _ => "masque",
            };
            if (SelectedAetherProtocol != targetProto)
            {
                SelectedAetherProtocol = targetProto;
            }
        }

        if (_tunnel.State == ConnectionState.Connected)
        {
            _ = Task.Run(async () =>
            {
                try { await _tunnel.RestartAsync(); } catch { }
            });
        }
    }

    private void RaiseMethodVisibility()
    {
        OnPropertyChanged(nameof(IsStandalonePsiphon));
        OnPropertyChanged(nameof(IsPsiphonMethod));
        OnPropertyChanged(nameof(IsAetherMethod));
        OnPropertyChanged(nameof(IsMasqueMethod));
        OnPropertyChanged(nameof(IsTorMethod));
        OnPropertyChanged(nameof(IsChainedMethod));
        OnPropertyChanged(nameof(SelectedProtocolMode));
        OnPropertyChanged(nameof(IsAutoProtocolMode));
        OnPropertyChanged(nameof(IsDirectMode));
        OnPropertyChanged(nameof(IsCdnFrontingMode));
        OnPropertyChanged(nameof(IsConduitMode));
        OnPropertyChanged(nameof(IsConduitCustomMode));
        OnPropertyChanged(nameof(ShowBeastMode));
        OnPropertyChanged(nameof(CanUseAutoFind));
        OnPropertyChanged(nameof(CanEditAdvancedTunneling));
        OnPropertyChanged(nameof(SupportsCustomProxyPorts));
        OnPropertyChanged(nameof(ProxyPortsSubtitle));
        OnPropertyChanged(nameof(DisplaySocksPort));
        OnPropertyChanged(nameof(DisplayHttpPort));
    }

    private ConnectionMethod CurrentMethod =>
        ConnectionMethodExtensions.ParseConnectionMethod(SelectedConnectionMethod);

    public bool IsStandalonePsiphon => CurrentMethod == ConnectionMethod.Psiphon;
    public bool IsPsiphonMethod => CurrentMethod is ConnectionMethod.Psiphon or ConnectionMethod.PsiphonOverWarp or ConnectionMethod.PsiphonOverV2Ray;
    public bool IsAetherMethod => CurrentMethod.IsAether() || CurrentMethod is ConnectionMethod.PsiphonOverWarp or ConnectionMethod.TorOverWarp;
    public bool IsMasqueMethod =>
        CurrentMethod == ConnectionMethod.Masque ||
        (CurrentMethod is ConnectionMethod.PsiphonOverWarp or ConnectionMethod.TorOverWarp && SelectedChainedOuterTransport is "auto" or "masque");
    public bool IsTorMethod => CurrentMethod is ConnectionMethod.Tor or ConnectionMethod.TorOverWarp or ConnectionMethod.TorOverV2Ray;
    public bool IsChainedMethod => CurrentMethod.IsChained();
    public bool SupportsCustomProxyPorts => true;

    public string ProxyPortsSubtitle => Loc.Of("Manual SOCKS5/HTTP ports (off = auto dynamic)");

    private void RestartIfActive(ConnectionMethod owner)
    {
        
        
    }

    

    public ObservableCollection<SettingOptionItem> AetherProtocols { get; } = new()
    {
        new("masque", "MASQUE"),
        new("wireguard", "WireGuard"),
        new("warp", "Warp (WARP-on-WARP)"),
    };

    [ObservableProperty] private string _selectedAetherProtocol = "masque";
    partial void OnSelectedAetherProtocolChanged(string value)
    {
        var proto = value ?? "masque";
        if (proto is "masque_in_masque" or "mim") proto = "masque";
        _settingsService.Settings.AetherProtocol = proto;

        
        var currentMethod = ConnectionMethodExtensions.ParseConnectionMethod(_settingsService.Settings.ConnectionMethod);
        if (currentMethod.IsAether())
        {
            var targetMethod = proto switch
            {
                "wireguard" => ConnectionMethod.WireGuard,
                "warp" => ConnectionMethod.WarpOnWarp,
                _ => ConnectionMethod.Masque,
            };
            if (currentMethod != targetMethod)
            {
                _suppressMethodSideEffects = true;
                try
                {
                    SelectedConnectionMethod = targetMethod.ToToken();
                    _settingsService.Settings.ConnectionMethod = targetMethod.ToToken();
                }
                finally { _suppressMethodSideEffects = false; }
            }
        }

        LoadAetherProtocolProfile(proto);
        _settingsService.Save();

        OnPropertyChanged(nameof(IsAetherMasqueSelected));
        OnPropertyChanged(nameof(IsAetherWiwSelected));
        OnPropertyChanged(nameof(IsAetherMimSelected));
        OnPropertyChanged(nameof(ManualEndpointHint));
        OnPropertyChanged(nameof(ManualEndpointDescription));
        RestartIfActive(ConnectionMethod.Masque);
    }

    public bool IsAetherMasqueSelected => SelectedAetherProtocol is "masque";
    public bool IsAetherWiwSelected => SelectedAetherProtocol is "warp" or "warp_on_warp";
    public bool IsAetherMimSelected => false;

    public string ManualEndpointHint => SelectedAetherProtocol switch
    {
        "wireguard" => "162.159.193.1:2408",
        "warp" => "162.159.192.1:2408",
        _ => "engage.cloudflareclient.com:2408",
    };

    public string ManualEndpointDescription => SelectedAetherProtocol switch
    {
        "wireguard" => Loc.Of("Custom endpoint for WireGuard protocol"),
        "warp" => Loc.Of("Custom endpoint for Warp protocol"),
        _ => Loc.Of("Custom endpoint for MASQUE protocol"),
    };

    private void LoadAetherProtocolProfile(string proto)
    {
        _suppressAetherSideEffects = true;
        try
        {
            var s = _settingsService.Settings;
            switch (proto)
            {
                case "wireguard":
                    AetherScanMode = NormalizeAetherScan(!string.IsNullOrWhiteSpace(s.AetherScanModeWireguard) ? s.AetherScanModeWireguard : s.AetherScanMode);
                    AetherNoize = NormalizeAetherNoize(!string.IsNullOrWhiteSpace(s.AetherNoizeWireguard) ? s.AetherNoizeWireguard : s.AetherNoize);
                    AetherIpVersion = NormalizeAetherIp(!string.IsNullOrWhiteSpace(s.AetherIpVersionWireguard) ? s.AetherIpVersionWireguard : s.AetherIpVersion);
                    AetherManualPeer = s.AetherEndpointWireguard ?? "";
                    AetherExitLoc = !string.IsNullOrWhiteSpace(s.AetherExitLocWireguard) ? s.AetherExitLocWireguard : s.AetherExitLoc;
                    break;
                case "warp":
                    AetherScanMode = NormalizeAetherScan(!string.IsNullOrWhiteSpace(s.AetherScanModeWarp) ? s.AetherScanModeWarp : s.AetherScanMode);
                    AetherNoize = NormalizeAetherNoize(!string.IsNullOrWhiteSpace(s.AetherNoizeWarp) ? s.AetherNoizeWarp : s.AetherNoize);
                    AetherIpVersion = NormalizeAetherIp(!string.IsNullOrWhiteSpace(s.AetherIpVersionWarp) ? s.AetherIpVersionWarp : s.AetherIpVersion);
                    AetherManualPeer = s.AetherEndpointWarp ?? "";
                    AetherExitLoc = !string.IsNullOrWhiteSpace(s.AetherExitLocWarp) ? s.AetherExitLocWarp : s.AetherExitLoc;
                    break;
                default: 
                    AetherScanMode = NormalizeAetherScan(!string.IsNullOrWhiteSpace(s.AetherScanModeMasque) ? s.AetherScanModeMasque : s.AetherScanMode);
                    AetherNoize = NormalizeAetherNoize(!string.IsNullOrWhiteSpace(s.AetherNoizeMasque) ? s.AetherNoizeMasque : s.AetherNoize);
                    AetherIpVersion = NormalizeAetherIp(!string.IsNullOrWhiteSpace(s.AetherIpVersionMasque) ? s.AetherIpVersionMasque : s.AetherIpVersion);
                    AetherManualPeer = s.AetherEndpointMasque ?? "";
                    AetherExitLoc = !string.IsNullOrWhiteSpace(s.AetherExitLocMasque) ? s.AetherExitLocMasque : s.AetherExitLoc;
                    break;
            }
        }
        finally
        {
            _suppressAetherSideEffects = false;
        }
    }

    [ObservableProperty] private string _aetherManualPeer = "";
    partial void OnAetherManualPeerChanged(string value)
    {
        if (_suppressAetherSideEffects) return;
        var peer = value?.Trim() ?? "";
        var s = _settingsService.Settings;
        switch (SelectedAetherProtocol)
        {
            case "wireguard": s.AetherEndpointWireguard = peer; break;
            case "warp": s.AetherEndpointWarp = peer; break;
            default: s.AetherEndpointMasque = peer; break;
        }
        s.AetherManualPeer = peer;
        _settingsService.Save();
        RestartIfActive(ConnectionMethod.Masque);
    }

    
    [ObservableProperty] private string _aetherWiwOuterPeer = "";
    partial void OnAetherWiwOuterPeerChanged(string value)
    {
        if (_suppressAetherSideEffects) return;
        _settingsService.Settings.AetherWiwOuterPeer = value?.Trim() ?? "";
        _settingsService.Save();
        RestartIfActive(ConnectionMethod.Masque);
    }

    [ObservableProperty] private string _aetherWiwInnerPeer = "";
    partial void OnAetherWiwInnerPeerChanged(string value)
    {
        if (_suppressAetherSideEffects) return;
        _settingsService.Settings.AetherWiwInnerPeer = value?.Trim() ?? "";
        _settingsService.Save();
        RestartIfActive(ConnectionMethod.Masque);
    }

    
    [ObservableProperty] private string _aetherMimOuterPeer = "";
    partial void OnAetherMimOuterPeerChanged(string value)
    {
        if (_suppressAetherSideEffects) return;
        _settingsService.Settings.AetherMimOuterPeer = value?.Trim() ?? "";
        _settingsService.Save();
        RestartIfActive(ConnectionMethod.Masque);
    }

    [ObservableProperty] private string _aetherMimInnerPeer = "";
    partial void OnAetherMimInnerPeerChanged(string value)
    {
        if (_suppressAetherSideEffects) return;
        _settingsService.Settings.AetherMimInnerPeer = value?.Trim() ?? "";
        _settingsService.Save();
        RestartIfActive(ConnectionMethod.Masque);
    }

    
    [ObservableProperty] private string _aetherExitLoc = "";
    partial void OnAetherExitLocChanged(string value)
    {
        if (_suppressAetherSideEffects) return;
        var loc = value?.Trim() ?? "";
        var s = _settingsService.Settings;
        
        
        
        
        switch (SelectedAetherProtocol)
        {
            case "wireguard": s.AetherExitLocWireguard = loc; break;
            case "warp": s.AetherExitLocWarp = loc; break;
            default: s.AetherExitLocMasque = loc; break;
        }
        _settingsService.Save();
        RestartIfActive(ConnectionMethod.Masque);
    }

    public ObservableCollection<SettingOptionItem> AetherScanModes { get; } = new()
    {
        new("turbo", "Turbo (Fastest Handshake)"),
        new("balanced", "Balanced (Recommended)"),
        new("thorough", "Thorough Benchmark"),
        new("verified", "Verified (Measured Edges Only)"),
        new("ironclad", "Ironclad (Ultra Resilient)"),
    };

    [ObservableProperty] private string _aetherScanMode = "balanced";
    partial void OnAetherScanModeChanged(string value)
    {
        if (_suppressAetherSideEffects) return;
        var norm = NormalizeAetherScan(value);
        var s = _settingsService.Settings;
        switch (SelectedAetherProtocol)
        {
            case "wireguard": s.AetherScanModeWireguard = norm; break;
            case "warp": s.AetherScanModeWarp = norm; break;
            default: s.AetherScanModeMasque = norm; break;
        }
        s.AetherScanMode = norm;
        _settingsService.Save();
        RestartIfActive(ConnectionMethod.Masque);
    }

    public ObservableCollection<SettingOptionItem> AetherNoizeModes { get; } = new()
    {
        new("off", "Disabled"),
        new("light", "Light (Low CPU)"),
        new("firewall", "Firewall (Stateful DPI Bypass)"),
        new("balanced", "Balanced (Recommended)"),
        new("gfw", "GFW (Great Firewall Bypass)"),
        new("aggressive", "Aggressive (Deep DPI Bypass)"),
    };

    [ObservableProperty] private string _aetherNoize = "balanced";
    partial void OnAetherNoizeChanged(string value)
    {
        if (_suppressAetherSideEffects) return;
        var norm = NormalizeAetherNoize(value);
        var s = _settingsService.Settings;
        switch (SelectedAetherProtocol)
        {
            case "wireguard": s.AetherNoizeWireguard = norm; break;
            case "warp": s.AetherNoizeWarp = norm; break;
            default: s.AetherNoizeMasque = norm; break;
        }
        s.AetherNoize = norm;
        _settingsService.Save();
        RestartIfActive(ConnectionMethod.Masque);
    }

    public ObservableCollection<SettingOptionItem> AetherIpVersions { get; } = new()
    {
        new("4", "IPv4"),
        new("6", "IPv6"),
        new("dual", "IPv4 + IPv6"),
    };

    [ObservableProperty] private string _aetherIpVersion = "4";
    partial void OnAetherIpVersionChanged(string value)
    {
        if (_suppressAetherSideEffects) return;
        var norm = NormalizeAetherIp(value);
        var s = _settingsService.Settings;
        switch (SelectedAetherProtocol)
        {
            case "wireguard": s.AetherIpVersionWireguard = norm; break;
            case "warp": s.AetherIpVersionWarp = norm; break;
            default: s.AetherIpVersionMasque = norm; break;
        }
        s.AetherIpVersion = norm;
        _settingsService.Save();
        RestartIfActive(ConnectionMethod.Masque);
    }

    [ObservableProperty] private bool _aetherCacheEdges = true;
    partial void OnAetherCacheEdgesChanged(bool value)
    {
        if (_suppressAetherSideEffects) return;
        _settingsService.Settings.AetherCacheEdges = value;
        _settingsService.Save();
        RestartIfActive(ConnectionMethod.Masque);
    }

    
    
    
    
    public ObservableCollection<SettingOptionItem> AetherMasqueTransports { get; } = new()
    {
        new("h2", "HTTP/2 (TCP fallback - Default)"),
        new("h3", "HTTP/3 (QUIC / UDP)"),
    };

    [ObservableProperty] private string _aetherMasqueTransport = "h2";
    partial void OnAetherMasqueTransportChanged(string value)
    {
        OnPropertyChanged(nameof(AetherQuicEnabled));
        OnPropertyChanged(nameof(CanUseFragment));
        OnPropertyChanged(nameof(FragmentHint));
        if (_suppressAetherSideEffects) return;
        var normalized = AetherEngine.NormalizeTransport(value);
        _settingsService.Settings.AetherMasqueTransport = normalized;
        _settingsService.Settings.AetherMasqueQuic = normalized == "h3";
        _settingsService.Save();
        RestartIfActive(ConnectionMethod.Masque);
    }

    public bool AetherQuicEnabled
    {
        get => string.Equals(AetherEngine.NormalizeTransport(AetherMasqueTransport), "h3", StringComparison.OrdinalIgnoreCase);
        set
        {
            var target = value ? "h3" : "h2";
            if (AetherMasqueTransport != target)
            {
                AetherMasqueTransport = target;
            }
        }
    }

        public bool CanUseFragment => !AetherQuicEnabled;

    public string FragmentHint => CanUseFragment
        ? Loc.Of("Split ClientHello to bypass SNI filters")
        : Loc.Of("Available on HTTP/2 only (QUIC disabled)");

    [ObservableProperty] private bool _aetherFragment;
    partial void OnAetherFragmentChanged(bool value)
    {
        if (_suppressAetherSideEffects) return;
        _settingsService.Settings.AetherFragment = value;
        _settingsService.Save();
        RestartIfActive(ConnectionMethod.Masque);
    }

    [ObservableProperty] private string _aetherFragmentSize = "";
    partial void OnAetherFragmentSizeChanged(string value)
    {
        if (_suppressAetherSideEffects) return;
        _settingsService.Settings.AetherFragmentSize = value?.Trim() ?? "";
        _settingsService.Save();
        RestartIfActive(ConnectionMethod.Masque);
    }

    [ObservableProperty] private string _aetherFragmentDelay = "";
    partial void OnAetherFragmentDelayChanged(string value)
    {
        if (_suppressAetherSideEffects) return;
        _settingsService.Settings.AetherFragmentDelay = value?.Trim() ?? "";
        _settingsService.Save();
        RestartIfActive(ConnectionMethod.Masque);
    }

    

    
    
    public ObservableCollection<Country> TorExitCountries => Regions;

    [ObservableProperty] private string _selectedTorExitCountry = "auto";
    partial void OnSelectedTorExitCountryChanged(string value)
    {
        if (_suppressTorSideEffects) return;
        _settingsService.Settings.TorExitCountry =
            (value == "auto" || string.IsNullOrWhiteSpace(value))
                ? ""
                : value.Trim().ToLowerInvariant();
        _settingsService.Save();
        RestartIfActive(ConnectionMethod.Tor);
    }

    [ObservableProperty] private string _torBridges = "";
    partial void OnTorBridgesChanged(string value)
    {
        if (_suppressTorSideEffects) return;
        _settingsService.Settings.TorBridges = value ?? "";
        _settingsService.Save();
        RestartIfActive(ConnectionMethod.Tor);
    }

    [RelayCommand]
    private void ApplyMeekCdn77() => TorBridges = Services.TorBridges.MeekCdn77;

    [RelayCommand]
    private void ApplySnowflakeCdn77() => TorBridges = Services.TorBridges.SnowflakeCdn77;

    [RelayCommand]
    private void ApplyObfs4Iat() => TorBridges = string.Join(Environment.NewLine, Services.TorBridges.Obfs4Iat);

    [RelayCommand]
    private void ApplyObfs4Public() => TorBridges = string.Join(Environment.NewLine, Services.TorBridges.Obfs4Public);

    [RelayCommand]
    private void ClearTorBridges() => TorBridges = "";

    

    [ObservableProperty] private string _shardCustomCfIp = "";
    partial void OnShardCustomCfIpChanged(string value)
    {
        _settingsService.Settings.ShardCustomCfIp = value?.Trim() ?? "";
        DebouncedSave();
        var custom = value?.Trim() ?? "";
        var edgeCount = !string.IsNullOrEmpty(custom) ? 1 : 6;
        ShardPathCount = ShardNodeCount * edgeCount;
        OnPropertyChanged(nameof(HasShardCustomIp));
        OnPropertyChanged(nameof(ShardEdgeMultiplierText));
        OnPropertyChanged(nameof(ShardEdgeMultiplierSub));
        OnPropertyChanged(nameof(ShardTotalPathsText));
    }

    [ObservableProperty] private bool _shardSmartSplit;
    partial void OnShardSmartSplitChanged(bool value)
    {
        _settingsService.Settings.ShardSmartSplit = value;
        _settingsService.Save();
        OnPropertyChanged(nameof(IsShardFullTunnelSelected));
        OnPropertyChanged(nameof(IsShardSmartSplitSelected));
    }

    [ObservableProperty] private bool _shardRotateIp = true;
    partial void OnShardRotateIpChanged(bool value)
    {
        _settingsService.Settings.ShardRotateIp = value;
        _settingsService.Save();
    }

    public bool IsShardFullTunnelSelected => !ShardSmartSplit;
    public bool IsShardSmartSplitSelected => ShardSmartSplit;

    [ObservableProperty] private int _shardNodeCount = 45;
    [ObservableProperty] private int _shardPathCount = 270;
    [ObservableProperty] private string _shardLastCheck = "";
    [ObservableProperty] private bool _isRefreshingShardPool;
    [ObservableProperty] private bool _isRotatingShardNode;

    public bool HasShardCustomIp => !string.IsNullOrWhiteSpace(ShardCustomCfIp);
    public string ShardActiveNodesText => string.Format(Loc.Of("{0} Nodes"), ShardNodeCount);
    public string ShardTotalPathsText => string.Format(Loc.Of("{0} Paths"), ShardPathCount);
    public string ShardEdgeMultiplierText => HasShardCustomIp ? Loc.Of("Custom Edge") : Loc.Of("6 Edge IPs");
    public string ShardEdgeMultiplierSub => HasShardCustomIp ? Loc.Of("Direct User Routing") : Loc.Of("CDN Edge IP Pool");
    public string ShardSyncSubtitle => !string.IsNullOrEmpty(ShardLastCheck)
        ? string.Format(Loc.Of("Sync: {0}"), ShardLastCheck)
        : Loc.Of("Built-in Seed Pool");
    public string ShardUpdateButtonText => IsRefreshingShardPool ? Loc.Of("Updating…") : Loc.Of("Update from Cloud");
    public string ShardRotateButtonText => IsRotatingShardNode ? Loc.Of("Rotating…") : Loc.Of("Rotate IP Now");

    public bool IsShardConnected =>
        _tunnel.State == ConnectionState.Connected
        && ConnectionMethodExtensions.ParseConnectionMethod(_settingsService.Settings.ConnectionMethod) == ConnectionMethod.Shard;

    public string ShardConnectedNodeDisplay =>
        IsShardConnected
            ? (!string.IsNullOrEmpty(_tunnel.CurrentRouteIp) && _tunnel.CurrentRouteIp != "—" ? $"Connected: {_tunnel.CurrentRouteIp}" : Loc.Of("Connected: Active Node"))
            : Loc.Of("Node / IP Rotation Ready");

    public string ShardConnectedNodeDesc =>
        IsShardConnected
            ? Loc.Of("Click below to instantly rotate to the next server node and get a new foreign IP.")
            : Loc.Of("Click below to advance to the next node batch for your next connection.");

    [RelayCommand]
    private async Task RefreshShardPoolAsync()
    {
        if (IsRefreshingShardPool) return;
        IsRefreshingShardPool = true;
        OnPropertyChanged(nameof(ShardUpdateButtonText));
        try
        {
            var res = await _shardEngine.RefreshSubscriptionAsync(force: true);
            ShardNodeCount = res.NodeCount;
            ShardPathCount = res.PathCount;
            if (res.LastCheckUtc > DateTime.MinValue)
            {
                var dt = res.LastCheckUtc.ToLocalTime();
                ShardLastCheck = $"{dt:HH:mm} ({dt.Month}/{dt.Day})";
            }
            OnPropertyChanged(nameof(ShardActiveNodesText));
            OnPropertyChanged(nameof(ShardTotalPathsText));
            OnPropertyChanged(nameof(ShardSyncSubtitle));
            ShowMaintenanceToast(string.Format(
                Loc.Of("SHARD pool refreshed: {0} nodes, {1} dynamic paths."), ShardNodeCount, ShardPathCount));
        }
        catch (Exception ex)
        {
            ShowMaintenanceToast(string.Format(Loc.Of("Failed to refresh SHARD pool: {0}"), ex.Message));
        }
        finally
        {
            IsRefreshingShardPool = false;
            OnPropertyChanged(nameof(ShardUpdateButtonText));
        }
    }

    [RelayCommand]
    private async Task RotateShardNodeAsync()
    {
        if (IsRotatingShardNode) return;
        IsRotatingShardNode = true;
        OnPropertyChanged(nameof(ShardRotateButtonText));
        try
        {
            await _shardEngine.RotateNodeAsync();
            OnPropertyChanged(nameof(ShardConnectedNodeDisplay));
            ShowMaintenanceToast(Loc.Of("Switched to next SHARD candidate node. New exit IP assigned."));
        }
        catch (Exception ex)
        {
            ShowMaintenanceToast(string.Format(Loc.Of("Failed to rotate SHARD node: {0}"), ex.Message));
        }
        finally
        {
            IsRotatingShardNode = false;
            OnPropertyChanged(nameof(ShardRotateButtonText));
        }
    }

    [RelayCommand]
    private void SelectShardSmartSplit(string? mode)
    {
        var isSplit = string.Equals(mode, "split", StringComparison.OrdinalIgnoreCase);
        ShardSmartSplit = isSplit;
    }

    [RelayCommand]
    private void SetShardPresetIp(string ip)
    {
        ShardCustomCfIp = ip;
    }

    [RelayCommand]
    private void ClearShardCustomCfIp()
    {
        ShardCustomCfIp = "";
    }

    

    public sealed record ChainedOuterOption(string Key, string English, string EnglishDescription) : LocalizedItem
    {
        public string Display => Loc.Of(English);
        public string Description => Loc.Of(EnglishDescription);
    }

    public ObservableCollection<ChainedOuterOption> ChainedOuterTransports { get; } = new()
    {
        new("auto", "Auto", "Try MASQUE, then WireGuard, then WoW — remembers what works"),
        new("masque", "MASQUE", "HTTP/3, falling back to HTTP/2 with TLS fragmentation"),
        new("wireguard", "WireGuard", "Single WARP tunnel; blocked on some carriers"),
        new("warp_on_warp", "WoW (WARP on WARP)", "WARP on WARP — slowest, for the most filtered networks"),
    };

    public sealed record ChainedSubModeOption(string Key, string English, string EnglishDescription) : LocalizedItem
    {
        public string Display => Loc.Of(English);
        public string Description => Loc.Of(EnglishDescription);
    }

    public ObservableCollection<ChainedSubModeOption> ChainedSubModes { get; } = new()
    {
        new("psiphon_warp", "Psiphon over WARP", "Multi-hop: Psiphon routed inside Cloudflare WARP/MASQUE tunnel"),
        new("psiphon_v2ray", "Psiphon over V2Ray", "Multi-hop: Psiphon routed inside V2Ray (Xray/Sing-box) proxy node"),
        new("tor_warp", "Tor over WARP", "Multi-hop: Tor onion routing through Cloudflare WARP"),
        new("tor_v2ray", "Tor over V2Ray", "Multi-hop: Tor onion routing through V2Ray (Xray/Sing-box) proxy node"),
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChainedPsiphonWarp))]
    [NotifyPropertyChangedFor(nameof(IsChainedTorWarp))]
    [NotifyPropertyChangedFor(nameof(IsChainedPsiphonV2Ray))]
    [NotifyPropertyChangedFor(nameof(IsChainedTorV2Ray))]
    [NotifyPropertyChangedFor(nameof(IsChainedV2Ray))]
    [NotifyPropertyChangedFor(nameof(IsChainedWarp))]
    [NotifyPropertyChangedFor(nameof(ChainedV2RayBannerTitle))]
    [NotifyPropertyChangedFor(nameof(ChainedV2RayBannerDesc))]
    private string _selectedChainedSubMode = "psiphon_warp";

    partial void OnSelectedChainedSubModeChanged(string value)
    {
        _settingsService.Settings.ChainedSubMode = value;
        _settingsService.Save();

        
        if (CurrentMethod.IsChained())
        {
            var targetMethod = value switch
            {
                "psiphon_v2ray" => ConnectionMethod.PsiphonOverV2Ray,
                "tor_warp" => ConnectionMethod.TorOverWarp,
                "tor_v2ray" => ConnectionMethod.TorOverV2Ray,
                _ => ConnectionMethod.PsiphonOverWarp,
            };
            if (CurrentMethod != targetMethod)
            {
                
                
                _settingsService.Settings.ConnectionMethod = targetMethod.ToToken();
                _settingsService.Save();

                _suppressMethodSideEffects = true;
                try { SelectedConnectionMethod = targetMethod.ToToken(); }
                finally { _suppressMethodSideEffects = false; }
            }
        }
    }

    public bool IsChainedPsiphonWarp => SelectedChainedSubMode == "psiphon_warp";
    public bool IsChainedTorWarp => SelectedChainedSubMode == "tor_warp";
    public bool IsChainedPsiphonV2Ray => SelectedChainedSubMode == "psiphon_v2ray";
    public bool IsChainedTorV2Ray => SelectedChainedSubMode == "tor_v2ray";
    public bool IsChainedV2Ray => IsChainedPsiphonV2Ray || IsChainedTorV2Ray;
    public bool IsChainedWarp => IsChainedPsiphonWarp || IsChainedTorWarp;

    public string ChainedV2RayBannerTitle => IsChainedTorV2Ray
        ? Loc.Of("Tor over V2Ray (Xray / Sing-box)")
        : Loc.Of("Psiphon over V2Ray (Xray / Sing-box)");

    public string ChainedV2RayBannerDesc => IsChainedTorV2Ray
        ? Loc.Of("Routes all Tor traffic through your upstream V2Ray proxy node for deep censorship evasion with onion privacy.")
        : Loc.Of("Routes all Psiphon traffic through your upstream V2Ray proxy node to bypass heavy firewalls with dual encryption.");

    [ObservableProperty] private string _selectedChainedOuterTransport = "auto";
    partial void OnSelectedChainedOuterTransportChanged(string value)
    {
        _settingsService.Settings.ChainedOuterTransport = NormalizeChainedOuter(value);
        _settingsService.Save();
        OnPropertyChanged(nameof(IsMasqueMethod));
        RestartIfActive(ConnectionMethod.Masque);
    }

    [ObservableProperty] private string _selectedChainedPsiphonOuterTransport = "auto";
    partial void OnSelectedChainedPsiphonOuterTransportChanged(string value)
    {
        var norm = NormalizeChainedOuter(value);
        _settingsService.Settings.ChainedPsiphonOuterTransport = norm;
        _settingsService.Save();
        RestartIfActive(ConnectionMethod.PsiphonOverWarp);
    }

    [ObservableProperty] private string _selectedChainedTorOuterTransport = "auto";
    partial void OnSelectedChainedTorOuterTransportChanged(string value)
    {
        var norm = NormalizeChainedOuter(value);
        _settingsService.Settings.ChainedTorOuterTransport = norm;
        _settingsService.Save();
        RestartIfActive(ConnectionMethod.TorOverWarp);
    }

    

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsXrayCoreSelected))]
    [NotifyPropertyChangedFor(nameof(IsSingBoxCoreSelected))]
    [NotifyPropertyChangedFor(nameof(V2RayCoreActiveBadge))]
    private string _selectedV2RayCore = "xray";

    partial void OnSelectedV2RayCoreChanged(string value)
    {
        _settingsService.Settings.V2RayCore = value == "sing_box" ? "sing_box" : "xray";
        _settingsService.Save();
        RestartIfActive(ConnectionMethod.PsiphonOverV2Ray);
        RestartIfActive(ConnectionMethod.TorOverV2Ray);
    }

    public bool IsXrayCoreSelected => SelectedV2RayCore == "xray";
    public bool IsSingBoxCoreSelected => SelectedV2RayCore == "sing_box";
    public string V2RayCoreActiveBadge => IsXrayCoreSelected ? "Xray Active" : "Sing-Box Active";

    [RelayCommand]
    private void SelectXrayCore() => SelectedV2RayCore = "xray";

    [RelayCommand]
    private void SelectSingBoxCore() => SelectedV2RayCore = "sing_box";

    

    [ObservableProperty] private string _v2RayInboundPort = "10808";
    partial void OnV2RayInboundPortChanged(string value)
    {
        if (int.TryParse(value, out var p) && p > 0 && p <= 65535)
        {
            _settingsService.Settings.V2RayInboundPort = p;
            DebouncedSave();
        }
    }

    [ObservableProperty] private bool _v2RayRouteDnsThroughV2Ray = true;
    partial void OnV2RayRouteDnsThroughV2RayChanged(bool value)
    {
        _settingsService.Settings.V2RayRouteDnsThroughV2Ray = value;
        _settingsService.Save();
    }

    [ObservableProperty] private bool _v2RayEnableMux;
    partial void OnV2RayEnableMuxChanged(bool value)
    {
        _settingsService.Settings.V2RayEnableMux = value;
        _settingsService.Save();
    }

    [ObservableProperty] private bool _v2RayEnableFragment = true;
    partial void OnV2RayEnableFragmentChanged(bool value)
    {
        _settingsService.Settings.V2RayEnableFragment = value;
        _settingsService.Save();
    }

    

    public ObservableCollection<V2RayConfigItemViewModel> V2RayConfigs { get; } = new();

    public bool HasV2RayConfigs => V2RayConfigs.Count > 0;
    public bool HasNoV2RayConfigs => V2RayConfigs.Count == 0;
    public int V2RayConfigsCount => V2RayConfigs.Count;

    [ObservableProperty] private bool _isTestingAllV2RayLatency;

    private void LoadV2RayConfigsFromSettings()
    {
        V2RayConfigs.Clear();
        var s = _settingsService.Settings;
        var activeId = s.V2RayActiveConfigId;

        foreach (var entry in s.V2RayConfigs)
        {
            if (!string.IsNullOrEmpty(activeId))
            {
                entry.IsActive = (entry.Id == activeId);
            }
            var vm = new V2RayConfigItemViewModel(entry);
            V2RayConfigs.Add(vm);
        }

        if (V2RayConfigs.Count > 0 && !V2RayConfigs.Any(c => c.IsActive))
        {
            V2RayConfigs[0].IsActive = true;
            s.V2RayActiveConfigId = V2RayConfigs[0].Id;
            s.V2RayConfigs[0].IsActive = true;
            _settingsService.Save();
        }

        OnPropertyChanged(nameof(HasV2RayConfigs));
        OnPropertyChanged(nameof(HasNoV2RayConfigs));
        OnPropertyChanged(nameof(V2RayConfigsCount));
    }

    [RelayCommand]
    private void SelectActiveV2RayConfig(V2RayConfigItemViewModel? item)
    {
        if (item is null) return;
        foreach (var c in V2RayConfigs)
        {
            c.IsActive = (c.Id == item.Id);
            c.SyncToEntry();
        }
        _settingsService.Settings.V2RayActiveConfigId = item.Id;
        _settingsService.Save();
    }

    [RelayCommand]
    private async Task TestV2RayLatencyAsync(V2RayConfigItemViewModel? item)
    {
        if (item is null || item.IsTesting) return;
        item.IsTesting = true;
        item.Latency = -2;
        try
        {
            var ms = await _v2rayEngine.MeasureRealDelayAsync(item.Entry);
            item.Latency = ms;
            item.SyncToEntry();
            _settingsService.Save();
        }
        catch
        {
            item.Latency = -3;
        }
        finally
        {
            item.IsTesting = false;
        }
    }

    [RelayCommand]
    private async Task PingAllV2RayConfigsAsync()
    {
        if (IsTestingAllV2RayLatency || V2RayConfigs.Count == 0) return;
        IsTestingAllV2RayLatency = true;

        foreach (var c in V2RayConfigs)
        {
            c.IsTesting = true;
            c.Latency = -2;
        }

        try
        {
            using var semaphore = new SemaphoreSlim(4);
            var tasks = V2RayConfigs.Select(async c =>
            {
                await semaphore.WaitAsync();
                try
                {
                    var ms = await _v2rayEngine.MeasureRealDelayAsync(c.Entry);
                    Ui.Invoke(() =>
                    {
                        c.Latency = ms;
                        c.SyncToEntry();
                    });
                }
                catch
                {
                    Ui.Invoke(() => c.Latency = -3);
                }
                finally
                {
                    Ui.Invoke(() => c.IsTesting = false);
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);
            _settingsService.Save();
        }
        finally
        {
            IsTestingAllV2RayLatency = false;
        }
    }

    [RelayCommand]
    private void DeleteV2RayConfig(V2RayConfigItemViewModel? item)
    {
        if (item is null) return;
        var wasActive = item.IsActive;
        V2RayConfigs.Remove(item);
        _settingsService.Settings.V2RayConfigs.RemoveAll(c => c.Id == item.Id);

        if (wasActive && V2RayConfigs.Count > 0)
        {
            V2RayConfigs[0].IsActive = true;
            _settingsService.Settings.V2RayActiveConfigId = V2RayConfigs[0].Id;
            V2RayConfigs[0].SyncToEntry();
        }
        else if (V2RayConfigs.Count == 0)
        {
            _settingsService.Settings.V2RayActiveConfigId = "";
        }

        _settingsService.Save();
        OnPropertyChanged(nameof(HasV2RayConfigs));
        OnPropertyChanged(nameof(HasNoV2RayConfigs));
        OnPropertyChanged(nameof(V2RayConfigsCount));
    }

    [RelayCommand]
    private void CopyV2RayShareLink(V2RayConfigItemViewModel? item)
    {
        if (item is null) return;
        Ui.SetClipboard(item.ShareLink);
    }

    

    [ObservableProperty] private bool _isClearAllConfigsDialogOpen;

    [RelayCommand]
    private void OpenClearAllConfigsDialog() => IsClearAllConfigsDialogOpen = true;

    [RelayCommand]
    private void CloseClearAllConfigsDialog() => IsClearAllConfigsDialogOpen = false;

    [RelayCommand]
    private void ConfirmClearAllConfigs()
    {
        V2RayConfigs.Clear();
        _settingsService.Settings.V2RayConfigs.Clear();
        _settingsService.Settings.V2RayActiveConfigId = "";
        _settingsService.Save();
        IsClearAllConfigsDialogOpen = false;
        OnPropertyChanged(nameof(HasV2RayConfigs));
        OnPropertyChanged(nameof(HasNoV2RayConfigs));
        OnPropertyChanged(nameof(V2RayConfigsCount));
    }

    

    [ObservableProperty] private bool _isImportDialogOpen;
    [ObservableProperty] private string _importLinkText = "";
    [ObservableProperty] private string _importErrorText = "";
    [ObservableProperty] private bool _hasImportError;

    [RelayCommand]
    private void OpenImportDialog()
    {
        ImportLinkText = "";
        ImportErrorText = "";
        HasImportError = false;
        IsImportDialogOpen = true;
    }

    [RelayCommand]
    private void CloseImportDialog()
    {
        IsImportDialogOpen = false;
    }

    [RelayCommand]
    private async Task PasteImportFromClipboard()
    {
        var text = await Ui.GetClipboardTextAsync();
        if (!string.IsNullOrWhiteSpace(text)) ImportLinkText = text.Trim();
    }

    [RelayCommand]
    private async Task ConfirmImportAsync()
    {
        if (string.IsNullOrWhiteSpace(ImportLinkText))
        {
            ImportErrorText = Loc.Of("Please enter or paste one or more links or subscription URLs.");
            HasImportError = true;
            return;
        }

        try
        {
            var outcome = await V2RayLinkParser.ParseInputAsync(ImportLinkText);
            var entries = outcome.Entries;
            if (entries.Count == 0)
            {
                ImportErrorText = outcome.Error
                    ?? Loc.Of("No valid proxy configuration found. Please check link format.");
                HasImportError = true;
                return;
            }

            foreach (var entry in entries)
            {
                _settingsService.Settings.V2RayConfigs.Add(entry);
                var vm = new V2RayConfigItemViewModel(entry);
                V2RayConfigs.Add(vm);
            }

            if (string.IsNullOrEmpty(_settingsService.Settings.V2RayActiveConfigId) && V2RayConfigs.Count > 0)
            {
                V2RayConfigs[0].IsActive = true;
                _settingsService.Settings.V2RayActiveConfigId = V2RayConfigs[0].Id;
                V2RayConfigs[0].SyncToEntry();
            }

            _settingsService.Save();

            if (outcome.Error is not null)
            {
                
                
                
                
                ImportErrorText = string.Format(
                    Loc.Of("Imported {0} node(s), but one entry failed: {1}"),
                    entries.Count,
                    outcome.Error);
                HasImportError = true;
                OnPropertyChanged(nameof(HasV2RayConfigs));
                OnPropertyChanged(nameof(HasNoV2RayConfigs));
                OnPropertyChanged(nameof(V2RayConfigsCount));
                return;
            }

            IsImportDialogOpen = false;
            OnPropertyChanged(nameof(HasV2RayConfigs));
            OnPropertyChanged(nameof(HasNoV2RayConfigs));
            OnPropertyChanged(nameof(V2RayConfigsCount));
        }
        catch (Exception ex)
        {
            ImportErrorText = string.Format(Loc.Of("Failed to parse links: {0}"), ex.Message);
            HasImportError = true;
        }
    }

    

    [ObservableProperty] private bool _isAddEditConfigDialogOpen;
    [ObservableProperty] private bool _isEditingConfig;
    [ObservableProperty] private string _addEditConfigDialogTitle = Loc.Of("Add Configuration");
    [ObservableProperty] private string _editConfigId = "";
    [ObservableProperty] private string _editConfigName = "";
    [ObservableProperty] private string _editConfigProtocol = "vless";
    [ObservableProperty] private string _editConfigAddress = "";
    [ObservableProperty] private string _editConfigPort = "443";
    [ObservableProperty] private string _editConfigUserId = "";
    [ObservableProperty] private string _editConfigSecurity = "reality";
    [ObservableProperty] private string _editConfigNetwork = "tcp";
    [ObservableProperty] private string _editConfigSni = "";
    [ObservableProperty] private string _editConfigPath = "";
    [ObservableProperty] private string _editConfigPublicKey = "";
    [ObservableProperty] private string _editConfigShortId = "";
    [ObservableProperty] private string _editConfigFormError = "";
    [ObservableProperty] private bool _hasEditConfigFormError;

    public bool IsRealitySecurity => EditConfigSecurity == "reality";
    public bool IsTlsOrRealitySecurity => EditConfigSecurity is "reality" or "tls";
    public bool IsWsOrGrpcNetwork => EditConfigNetwork is "ws" or "grpc";

    [RelayCommand]
    private void SetEditProtocol(string? proto)
    {
        if (string.IsNullOrEmpty(proto)) return;
        EditConfigProtocol = proto.ToLowerInvariant();
    }

    [RelayCommand]
    private void SetEditSecurity(string? sec)
    {
        if (string.IsNullOrEmpty(sec)) return;
        EditConfigSecurity = sec.ToLowerInvariant();
        OnPropertyChanged(nameof(IsRealitySecurity));
        OnPropertyChanged(nameof(IsTlsOrRealitySecurity));
    }

    [RelayCommand]
    private void SetEditNetwork(string? net)
    {
        if (string.IsNullOrEmpty(net)) return;
        EditConfigNetwork = net.ToLowerInvariant();
        OnPropertyChanged(nameof(IsWsOrGrpcNetwork));
    }

    [RelayCommand]
    private void OpenAddConfigDialog()
    {
        IsEditingConfig = false;
        AddEditConfigDialogTitle = Loc.Of("Add Configuration");
        EditConfigId = "cfg_" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        EditConfigName = "";
        EditConfigProtocol = "vless";
        EditConfigAddress = "";
        EditConfigPort = "443";
        EditConfigUserId = "";
        EditConfigSecurity = "reality";
        EditConfigNetwork = "tcp";
        EditConfigSni = "";
        EditConfigPath = "";
        EditConfigPublicKey = "";
        EditConfigShortId = "";
        EditConfigFormError = "";
        HasEditConfigFormError = false;
        OnPropertyChanged(nameof(IsRealitySecurity));
        OnPropertyChanged(nameof(IsTlsOrRealitySecurity));
        OnPropertyChanged(nameof(IsWsOrGrpcNetwork));
        IsAddEditConfigDialogOpen = true;
    }

    [RelayCommand]
    private void OpenEditConfigDialog(V2RayConfigItemViewModel? item)
    {
        if (item is null) return;
        IsEditingConfig = true;
        AddEditConfigDialogTitle = Loc.Of("Edit Configuration");
        EditConfigId = item.Id;
        EditConfigName = item.Name;
        EditConfigProtocol = item.Protocol;
        EditConfigAddress = item.Address;
        EditConfigPort = item.Port.ToString();
        EditConfigUserId = item.Entry.UserId;
        EditConfigSecurity = item.Security;
        EditConfigNetwork = item.Network;
        EditConfigSni = item.Entry.Sni ?? item.Entry.Host ?? "";
        EditConfigPath = item.Entry.Path ?? "";
        EditConfigPublicKey = item.Entry.PublicKey ?? "";
        EditConfigShortId = item.Entry.ShortId ?? "";
        EditConfigFormError = "";
        HasEditConfigFormError = false;
        OnPropertyChanged(nameof(IsRealitySecurity));
        OnPropertyChanged(nameof(IsTlsOrRealitySecurity));
        OnPropertyChanged(nameof(IsWsOrGrpcNetwork));
        IsAddEditConfigDialogOpen = true;
    }

    [RelayCommand]
    private void CloseAddEditConfigDialog()
    {
        IsAddEditConfigDialogOpen = false;
    }

    [RelayCommand]
    private void SaveAddEditConfig()
    {
        if (string.IsNullOrWhiteSpace(EditConfigAddress))
        {
            EditConfigFormError = Loc.Of("Server address is required.");
            HasEditConfigFormError = true;
            return;
        }

        if (!int.TryParse(EditConfigPort.Trim(), out var port) || port <= 0 || port > 65535)
        {
            EditConfigFormError = Loc.Of("Port must be a valid number between 1 and 65535.");
            HasEditConfigFormError = true;
            return;
        }

        if (string.IsNullOrWhiteSpace(EditConfigUserId))
        {
            EditConfigFormError = Loc.Of("User ID / UUID / Password is required.");
            HasEditConfigFormError = true;
            return;
        }

        var name = string.IsNullOrWhiteSpace(EditConfigName)
            ? $"Server {EditConfigAddress.Trim()}"
            : EditConfigName.Trim();

        if (IsEditingConfig)
        {
            var existing = V2RayConfigs.FirstOrDefault(c => c.Id == EditConfigId);
            if (existing != null)
            {
                existing.Name = name;
                existing.Protocol = EditConfigProtocol;
                existing.Address = EditConfigAddress.Trim();
                existing.Port = port;
                existing.Security = EditConfigSecurity;
                existing.Network = EditConfigNetwork;
                existing.Entry.UserId = EditConfigUserId.Trim();
                existing.Entry.Sni = string.IsNullOrWhiteSpace(EditConfigSni) ? null : EditConfigSni.Trim();
                existing.Entry.Host = existing.Entry.Sni;
                existing.Entry.Path = string.IsNullOrWhiteSpace(EditConfigPath) ? null : EditConfigPath.Trim();
                existing.Entry.PublicKey = string.IsNullOrWhiteSpace(EditConfigPublicKey) ? null : EditConfigPublicKey.Trim();
                existing.Entry.ShortId = string.IsNullOrWhiteSpace(EditConfigShortId) ? null : EditConfigShortId.Trim();
                existing.SyncToEntry();
            }
        }
        else
        {
            var entry = new V2RayConfigEntry
            {
                Id = EditConfigId,
                Name = name,
                Protocol = EditConfigProtocol,
                Address = EditConfigAddress.Trim(),
                Port = port,
                UserId = EditConfigUserId.Trim(),
                Security = EditConfigSecurity,
                Network = EditConfigNetwork,
                Sni = string.IsNullOrWhiteSpace(EditConfigSni) ? null : EditConfigSni.Trim(),
                Host = string.IsNullOrWhiteSpace(EditConfigSni) ? null : EditConfigSni.Trim(),
                Path = string.IsNullOrWhiteSpace(EditConfigPath) ? null : EditConfigPath.Trim(),
                PublicKey = string.IsNullOrWhiteSpace(EditConfigPublicKey) ? null : EditConfigPublicKey.Trim(),
                ShortId = string.IsNullOrWhiteSpace(EditConfigShortId) ? null : EditConfigShortId.Trim(),
                IsActive = V2RayConfigs.Count == 0,
            };

            _settingsService.Settings.V2RayConfigs.Add(entry);
            if (entry.IsActive)
            {
                _settingsService.Settings.V2RayActiveConfigId = entry.Id;
            }
            var vm = new V2RayConfigItemViewModel(entry);
            V2RayConfigs.Add(vm);
        }

        _settingsService.Save();
        IsAddEditConfigDialogOpen = false;
        OnPropertyChanged(nameof(HasV2RayConfigs));
        OnPropertyChanged(nameof(HasNoV2RayConfigs));
        OnPropertyChanged(nameof(V2RayConfigsCount));
    }

    private static string NormalizeChainedOuter(string? val) =>
        (val ?? "").Trim().ToLowerInvariant() switch
        {
            "masque" => "masque",
            "wireguard" or "wg" => "wireguard",
            "warp_on_warp" or "wow" => "warp_on_warp",
            _ => "auto",
        };

    private static string NormalizeAetherScan(string? v) =>
        (v ?? "").Trim().ToLowerInvariant() switch
        {
            "turbo" => "turbo",
            "thorough" => "thorough",
            
            "stealth" => "verified",
            "verified" => "verified",
            "ironclad" => "ironclad",
            _ => "balanced",
        };

    private static string NormalizeAetherNoize(string? v) =>
        (v ?? "").Trim().ToLowerInvariant() switch
        {
            "off" => "off",
            "light" => "light",
            "firewall" => "firewall",
            "gfw" => "gfw",
            "aggressive" => "aggressive",
            _ => "balanced",
        };

    private static string NormalizeAetherIp(string? v) =>
        (v ?? "").Trim().ToLowerInvariant() switch
        {
            "6" => "6",
            "dual" => "dual",
            _ => "4",
        };

    [RelayCommand]
    private void ResetAdvanced()
    {
        _settingsService.Settings.DisableTimeouts = false;
        _settingsService.Settings.UpstreamProxy = "";
        _settingsService.Settings.UpstreamProxyEnabled = true;
        _settingsService.Settings.UpstreamProxyScheme = "http";
        _settingsService.Settings.UpstreamProxyUsername = "";
        _settingsService.Settings.UpstreamProxyPassword = "";
        _settingsService.Settings.LocalSocksProxyPort = 0;
        _settingsService.Settings.LocalHttpProxyPort = 0;
        _settingsService.Settings.ProtocolMode = "auto";
        _settingsService.Settings.BeastMode = false;
        _settingsService.Settings.CdnFrontingCustomIpList = "";
        _settingsService.Settings.CdnFrontingCustomSni = "";
        _settingsService.Settings.CdnFrontingSkipCertVerify = false;
        _settingsService.Settings.AutoFindIpAndSni = false;
        _settingsService.Settings.SaveFoundIpsAndSni = false;
        _settingsService.Settings.FrontedMeekCDNScanBuiltInSets = new() { "psiphon-akamai", "fastly" };
        _settingsService.Settings.EstablishTunnelTimeoutSeconds = 300;
        _settingsService.Settings.ConduitMode = "auto";
        _settingsService.Settings.ConduitCompartmentId = "";
        _settingsService.Settings.ConduitRejectCensoredCountries = true;
        _settingsService.Settings.LanProxyUsername = "";
        _settingsService.Settings.LanProxyPassword = "";
        _settingsService.Settings.AetherScanMode = "balanced";
        _settingsService.Settings.AetherScanModeMim = "balanced";
        _settingsService.Settings.AetherNoize = "balanced";
        _settingsService.Settings.AetherIpVersion = "4";
        _settingsService.Settings.AetherExitLoc = "";
        _settingsService.Settings.AetherWiwOuterPeer = "";
        _settingsService.Settings.AetherWiwInnerPeer = "";
        _settingsService.Settings.AetherMimOuterPeer = "";
        _settingsService.Settings.AetherMimInnerPeer = "";
        _settingsService.Settings.AetherFragment = false;
        _settingsService.Settings.AetherFragmentSize = "";
        _settingsService.Settings.AetherFragmentDelay = "";
        _settingsService.Settings.AetherMasqueTransport = "h3";
        _settingsService.Settings.AetherMasqueQuic = true;
        _settingsService.Settings.TorExitCountry = "";
        _settingsService.Settings.TorBridges = "";
        _settingsService.Save();

        DisableTimeouts = false;
        SelectedProxyScheme = "http";
        ProxyHost = "";
        ProxyPort = "";
        ProxyUsername = "";
        ProxyPassword = "";
        SocksPort = "";
        HttpPort = "";
        SelectedProtocolMode = "auto";
        BeastMode = false;
        CdnFrontingCustomIpList = "";
        CdnFrontingCustomSni = "";
        CdnFrontingSkipCertVerify = false;
        AutoFindIpAndSni = false;
        SaveFoundIpsAndSni = false;
        EstablishTunnelTimeoutSeconds = 300;
        InitCdnProviders(_settingsService.Settings.FrontedMeekCDNScanBuiltInSets);
        OnPropertyChanged(nameof(CdnCorpusSummary));
        ConduitMode = "auto";
        ConduitCompartmentId = "";
        ConduitRejectCensoredCountries = true;
        LanProxyUsername = "";
        LanProxyPassword = "";
        AetherScanMode = "balanced";
        AetherNoize = "balanced";
        AetherIpVersion = "4";
        AetherExitLoc = "";
        AetherWiwOuterPeer = "";
        AetherWiwInnerPeer = "";
        AetherMimOuterPeer = "";
        AetherMimInnerPeer = "";
        AetherFragment = false;
        AetherFragmentSize = "";
        AetherFragmentDelay = "";
        AetherMasqueTransport = "h2";
        SelectedTorExitCountry = "auto";
        TorBridges = "";
        UpstreamProxyEnabled = true;
    }

    private static string BuildUpstreamProxy(
    string scheme, string host, string port, string user, string pass)
    {
        host = (host ?? "").Trim();
        port = (port ?? "").Trim();
        scheme = NormalizeScheme(scheme);
        if (string.IsNullOrEmpty(host)) return "";

        var creds = "";
        var trimmedUser = (user ?? "").Trim();
        if (!string.IsNullOrEmpty(trimmedUser))
        {
            creds = string.IsNullOrEmpty(pass)
                ? $"{Uri.EscapeDataString(trimmedUser)}@"
                : $"{Uri.EscapeDataString(trimmedUser)}:{Uri.EscapeDataString(pass)}@";
        }

        var hostPort = string.IsNullOrEmpty(port) ? host : $"{host}:{port}";
        return $"{scheme}://{creds}{hostPort}";
    }

    private static void ParseUpstreamProxy(
    string proxy,
    out string scheme,
    out string host,
    out string port,
    out string user,
    out string pass)
    {
        scheme = "http";
        host = "";
        port = "";
        user = "";
        pass = "";
        if (string.IsNullOrWhiteSpace(proxy)) return;

        proxy = proxy.Trim();

        var schemeEnd = proxy.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd >= 0)
        {
            scheme = NormalizeScheme(proxy[..schemeEnd]);
            proxy = proxy[(schemeEnd + 3)..];
        }

        var atIdx = proxy.LastIndexOf('@');
        if (atIdx >= 0)
        {
            var creds = proxy[..atIdx];
            proxy = proxy[(atIdx + 1)..];

            var colonIdx = creds.IndexOf(':');
            if (colonIdx >= 0)
            {
                user = Uri.UnescapeDataString(creds[..colonIdx]);
                pass = Uri.UnescapeDataString(creds[(colonIdx + 1)..]);
            }
            else
            {
                user = Uri.UnescapeDataString(creds);
            }
        }

        var slashIdx = proxy.IndexOf('/');
        if (slashIdx >= 0)
            proxy = proxy[..slashIdx];

        var lastColon = proxy.LastIndexOf(':');
        if (lastColon > 0)
        {
            host = proxy[..lastColon];
            port = proxy[(lastColon + 1)..];
        }
        else
        {
            host = proxy;
        }
    }

    
    
    

    [ObservableProperty]
    private string _aetherInstalledVersion = "v1.7.0";

    [ObservableProperty]
    private string _aetherLatestVersion = "v1.7.0";

    [ObservableProperty]
    private string _aetherStatusText = Loc.Of("Up to date");

    [ObservableProperty]
    private bool _hasAetherUpdate;

        [ObservableProperty] private string _aetherStatusColor = "#8B93A7";

    [ObservableProperty]
    private bool _isCheckingAetherUpdate;

    [ObservableProperty]
    private bool _isUpdatingAether;

    [ObservableProperty]
    private int _aetherUpdateProgress;

    [ObservableProperty]
    private string _torInstalledVersion = "v0.4.9.11";

    [ObservableProperty]
    private string _torLatestVersion = "v0.4.9.11";

    [ObservableProperty]
    private string _torStatusText = Loc.Of("Up to date");

    [ObservableProperty]
    private bool _isCheckingTorUpdate;

    [ObservableProperty] private string _torStatusColor = "#8B93A7";

    [ObservableProperty]
    private bool _isCheckingAllUpdates;

    [ObservableProperty]
    private bool _isCoreUpdateSuccessDialogOpen;

    [ObservableProperty]
    private string _coreUpdateSuccessTitle = "";

    [ObservableProperty]
    private string _coreUpdateSuccessMessage = "";

    [RelayCommand]
    private async Task CheckAllUpdatesAsync()
    {
        if (IsCheckingAllUpdates || IsCheckingAetherUpdate || IsCheckingTorUpdate || IsUpdatingAether || IsAppUpdateBusy) return;
        IsCheckingAllUpdates = true;

        try
        {
            await Task.WhenAll(CheckAppUpdateAsync(), CheckAetherUpdateAsync(), CheckTorUpdateAsync());
        }
        finally
        {
            IsCheckingAllUpdates = false;
        }
    }

    [RelayCommand]
    private async Task CheckTorUpdateAsync()
    {
        if (IsCheckingTorUpdate) return;
        IsCheckingTorUpdate = true;
        TorStatusText = Loc.Of("Checking for updates...");
        TorStatusColor = "#8B93A7";

        try
        {
            var info = await _coreUpdateService.CheckForUpdateAsync("tor");
            TorInstalledVersion = "v" + info.InstalledVersion;
            TorLatestVersion = "v" + info.LatestVersion;
            TorStatusText = Loc.Of("Tor core is up to date");
            TorStatusColor = "#10B981";
        }
        catch (Exception ex)
        {
            TorStatusText = string.Format(Loc.Of("Check failed: {0}"), ex.Message);
            TorStatusColor = "#EF4444";
        }
        finally
        {
            IsCheckingTorUpdate = false;
        }
    }

    [RelayCommand]
    private async Task CheckAetherUpdateAsync()
    {
        if (IsCheckingAetherUpdate || IsUpdatingAether) return;
        IsCheckingAetherUpdate = true;
        AetherStatusText = Loc.Of("Checking for updates...");
        AetherStatusColor = "#8B93A7";

        try
        {
            var info = await _coreUpdateService.CheckForUpdateAsync("aether");
            AetherInstalledVersion = "v" + info.InstalledVersion;
            AetherLatestVersion = "v" + info.LatestVersion;
            HasAetherUpdate = info.HasUpdate;
            if (info.HasUpdate)
            {
                AetherStatusText = string.Format(Loc.Of("Update available: v{0}"), info.LatestVersion);
                AetherStatusColor = "#00D4FF";
            }
            else
            {
                AetherStatusText = Loc.Of("Aether is up to date");
                AetherStatusColor = "#10B981";
            }
        }
        catch (Exception ex)
        {
            AetherStatusText = string.Format(Loc.Of("Check failed: {0}"), ex.Message);
            AetherStatusColor = "#EF4444";
        }
        finally
        {
            IsCheckingAetherUpdate = false;
        }
    }

    [RelayCommand]
    private async Task ReinstallAetherAsync()
    {
        if (IsUpdatingAether) return;
        await UpdateAetherAsync();
    }

    [RelayCommand]
    private async Task UpdateAetherAsync()
    {
        if (IsUpdatingAether) return;
        IsUpdatingAether = true;
        AetherUpdateProgress = 0;
        AetherStatusText = Loc.Of("Starting download...");
        AetherStatusColor = "#8B93A7";

        try
        {
            var progress = new Progress<int>(p =>
            {
                AetherUpdateProgress = p;
                AetherStatusText = p < 80
                    ? string.Format(Loc.Of("Downloading: {0}%"), p)
                    : (p < 95 ? Loc.Of("Extracting & installing...") : Loc.Of("Finalizing..."));
                AetherStatusColor = p < 95 ? "#00D4FF" : "#F59E0B";
            });

            
            
            if (_tunnel.State != ConnectionState.Disconnected) await _tunnel.StopAsync();

            var success = await _coreUpdateService.UpdateCoreAsync("aether", progress);
            if (success)
            {
                var newVer = _coreUpdateService.GetInstalledVersion("aether");
                AetherInstalledVersion = "v" + newVer;
                AetherLatestVersion = "v" + newVer;
                HasAetherUpdate = false;
                AetherStatusText = string.Format(Loc.Of("Updated to v{0}"), newVer);
                AetherStatusColor = "#10B981";

                CoreUpdateSuccessTitle = Loc.Of("Aether Core Updated Successfully");
                CoreUpdateSuccessMessage = string.Format(Loc.Of("Aether network engine has been successfully updated to v{0}."), newVer)
                    + "\n" + Loc.Of("Please restart the application to apply the updated core.")
                    + "\n" + Loc.Of("Your Se7en Pro settings were left untouched.");
                IsCoreUpdateSuccessDialogOpen = true;
            }
        }
        catch (Exception ex)
        {
            AetherStatusText = string.Format(Loc.Of("Update failed: {0}"), ex.Message);
            AetherStatusColor = "#EF4444";
        }
        finally
        {
            IsUpdatingAether = false;
        }
    }

    [RelayCommand]
    private void RestartApplication()
    {
        
        
        Ui.RequestRelaunchOnExit();
        ForceExitCurrentInstance();
    }

    [RelayCommand]
    private void CloseCoreUpdateSuccessDialog()
    {
        IsCoreUpdateSuccessDialogOpen = false;
    }

    private static string NormalizeScheme(string? scheme)
    {
        scheme = (scheme ?? "").Trim().ToLowerInvariant();
        return scheme switch
        {
            "http" or "http / https proxy" => "http",
            "socks5" or "socks5 proxy (recommended)" => "socks5",
            "socks5h" or "socks5h proxy (remote dns)" => "socks5h",
            "socks4a" or "socks4a proxy (legacy)" => "socks4a",
            _ => "http",
        };
    }
}

public sealed class CdnProviderItem : ObservableObject
{
    public string Key { get; init; } = "";

    
    
    
    
    private string _name = "";
    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    private string _badge = "";
    public string Badge
    {
        get => _badge;
        set => SetProperty(ref _badge, value);
    }

    private string _colorHex = "#4285F4";
    public string ColorHex
    {
        get => _colorHex;
        set => SetProperty(ref _colorHex, value);
    }

    private string _iconKind = "Cloud";
    public string IconKind
    {
        get => _iconKind;
        set => SetProperty(ref _iconKind, value);
    }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

        private int _orderIndex;
    public int OrderIndex
    {
        get => _orderIndex;
        set => SetProperty(ref _orderIndex, value);
    }
}
