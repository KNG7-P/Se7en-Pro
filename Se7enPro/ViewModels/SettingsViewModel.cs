using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Se7enPro.Models;
using Se7enPro.Services;

namespace Se7enPro.ViewModels;

public sealed class CloseActionOption
{
    public string Value { get; init; } = "";
    public string Display { get; init; } = "";
    public override string ToString() => Display;
}

public sealed partial class SettingsViewModel : PageViewModelBase
{
    private readonly ISettingsService _settingsService;
    private readonly IThemeService _themeService;
    private readonly ITunnelCoreManager _tunnel;
    private readonly IStartupRegistration _startup;
    private readonly ICoreUpdateService _coreUpdateService;

    private bool _suppressThemeSideEffects;

    private bool _suppressRegionSideEffects;

    public override string Title => "Settings";
    public override string Route => "settings";
    public override string Icon => "Cog";

    public SettingsViewModel(
        ISettingsService settingsService,
        IThemeService themeService,
        ITunnelCoreManager tunnel,
        IStartupRegistration startup,
        ICoreUpdateService coreUpdateService)
    {
        _settingsService = settingsService;
        _themeService = themeService;
        _tunnel = tunnel;
        _startup = startup;
        _coreUpdateService = coreUpdateService;

        _aetherInstalledVersion = "v" + _coreUpdateService.GetInstalledVersion("aether");
        _aetherLatestVersion = _aetherInstalledVersion;
        _torInstalledVersion = "v" + _coreUpdateService.GetInstalledVersion("tor");
        _torLatestVersion = _torInstalledVersion;

        var s = _settingsService.Settings;
        _selectedTheme = s.Theme;
        _selectedRegion = string.IsNullOrEmpty(s.EgressRegion) ? "auto" : s.EgressRegion;
        _setSystemProxy = s.SetSystemProxy;
        _disableTimeouts = s.DisableTimeouts;
        _socksPort = FormatListenPort(s.LocalSocksProxyPort);
        _httpPort = FormatListenPort(s.LocalHttpProxyPort);
        _autoConnect = s.AutoConnect;
        _startWithWindows = _startup.IsEnabled();
        if (_startWithWindows != s.StartWithWindows)
        {
            s.StartWithWindows = _startWithWindows;
            _settingsService.Save();
        }
        _minimizeToTray = s.MinimizeToTray;
        _ipScannerEnabled = s.IpScannerEnabled;
        _killSwitchEnabled = s.KillSwitchEnabled;
        _selectedCloseAction = ResolveCloseAction(s.OnCloseAction);
        _allowLanConnections = s.AllowLanConnections;
        _lanProxyUsername = s.LanProxyUsername;
        _lanProxyPassword = s.LanProxyPassword;

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
        _conduitMode = s.ConduitMode ?? "auto";
        _conduitCompartmentId = s.ConduitCompartmentId ?? "";
        _conduitRejectCensoredCountries = s.ConduitRejectCensoredCountries;
        _beastMode = s.BeastMode;
        _cdnFrontingCustomIpList = s.CdnFrontingCustomIpList;
        _cdnFrontingCustomSni = s.CdnFrontingCustomSni;
        _autoFindIpAndSni = s.AutoFindIpAndSni;
        _saveFoundIpsAndSni = s.SaveFoundIpsAndSni;

        _selectedConnectionMethod = ConnectionMethodExtensions.ParseConnectionMethod(s.ConnectionMethod).ToToken();
        _aetherManualPeer = s.AetherManualPeer ?? "";
        _aetherScanMode = NormalizeAetherScan(s.AetherScanMode);
        _aetherNoize = NormalizeAetherNoize(s.AetherNoize);
        _aetherIpVersion = NormalizeAetherIp(s.AetherIpVersion);
        _aetherFragment = s.AetherFragment;
        _aetherMasqueTransport = AetherEngine.NormalizeTransport(s.AetherMasqueTransport);

        _selectedTorExitCountry = string.IsNullOrWhiteSpace(s.TorExitCountry)
            ? "auto"
            : s.TorExitCountry.Trim().ToUpperInvariant();
        _torBridges = s.TorBridges ?? "";
        _selectedChainedOuterTransport = NormalizeChainedOuter(s.ChainedOuterTransport);

        _settingsService.SettingsChanged += OnSettingsServiceChanged;
        _tunnel.StateChanged += OnTunnelStateChanged;
        RefreshLanProxyInfo();
    }

    private void OnTunnelStateChanged(object? sender, ConnectionState e)
    {
        if (System.Windows.Application.Current is { } app)
        {
            app.Dispatcher.BeginInvoke(new Action(() =>
            {
                RefreshLanProxyInfo();
                OnPropertyChanged(nameof(DisplaySocksPort));
                OnPropertyChanged(nameof(DisplayHttpPort));
            }));
        }
        else
        {
            RefreshLanProxyInfo();
            OnPropertyChanged(nameof(DisplaySocksPort));
            OnPropertyChanged(nameof(DisplayHttpPort));
        }
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
            sb.AppendLine("No LAN IPv4 addresses detected on this PC.");
        }
        else
        {
            sb.AppendLine("This PC's LAN addresses — configure other devices' proxy to point here:");
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

    [ObservableProperty] private bool _ipScannerEnabled;
    partial void OnIpScannerEnabledChanged(bool value)
    {
        _settingsService.Settings.IpScannerEnabled = value;
        _settingsService.Save();
    }

    [ObservableProperty] private bool _startWithWindows;
    partial void OnStartWithWindowsChanged(bool value)
    {
        _settingsService.Settings.StartWithWindows = value;
        _settingsService.Save();
        _startup.SetEnabled(value);
    }

    [ObservableProperty] private bool _minimizeToTray;
    partial void OnMinimizeToTrayChanged(bool value) { _settingsService.Settings.MinimizeToTray = value; _settingsService.Save(); }

    [ObservableProperty] private bool _allowLanConnections;
    partial void OnAllowLanConnectionsChanged(bool value)
    {
        _settingsService.Settings.AllowLanConnections = value;
        _settingsService.Save();
        RefreshLanProxyInfo();
        _ = _tunnel.RestartAsync();
    }

    [ObservableProperty] private string _lanProxyUsername = "";
    partial void OnLanProxyUsernameChanged(string value)
    {
        _settingsService.Settings.LanProxyUsername = (value ?? "").Trim();
        _settingsService.Save();
        _ = _tunnel.RestartAsync();
    }

    [ObservableProperty] private string _lanProxyPassword = "";
    partial void OnLanProxyPasswordChanged(string value)
    {
        _settingsService.Settings.LanProxyPassword = value ?? "";
        _settingsService.Save();
        _ = _tunnel.RestartAsync();
    }

    public ObservableCollection<CloseActionOption> CloseActions { get; } = new()
    {
        new CloseActionOption { Value = "ask", Display = "Always ask" },
        new CloseActionOption { Value = "minimize", Display = "Minimize to system tray" },
        new CloseActionOption { Value = "exit", Display = "Close completely" },
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
        return CloseActions.FirstOrDefault(o => o.Value == v) ?? CloseActions[0];
    }

    public ObservableCollection<string> ProxySchemes { get; } = new()
    {
        "http", "socks5", "socks5h", "socks4a",
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
        var hasProxy = !string.IsNullOrWhiteSpace(value);
        OnPropertyChanged(nameof(HasUpstreamProxy));
        OnPropertyChanged(nameof(IsCdnFrontingMode));
        OnPropertyChanged(nameof(CanUseAutoFind));
        OnPropertyChanged(nameof(CanEditAdvancedTunneling));
        OnPropertyChanged(nameof(UpstreamProxyWarning));
        OnPropertyChanged(nameof(HasUpstreamProxyWarning));
        if (hasProxy && !_suppressProxyExclusion)
        {
            if (!string.Equals(SelectedProtocolMode, "direct", StringComparison.Ordinal))
            {
                SelectedProtocolMode = "direct";
            }
            if (BeastMode) BeastMode = false;
        }
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
        _settingsService.Save();
    }

    [ObservableProperty] private bool _upstreamProxyEnabled = true;
    partial void OnUpstreamProxyEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(HasUpstreamProxy));
        OnPropertyChanged(nameof(IsDirectMode));
        OnPropertyChanged(nameof(IsCdnFrontingMode));
        OnPropertyChanged(nameof(ShowBeastMode));
        OnPropertyChanged(nameof(CanUseAutoFind));
        OnPropertyChanged(nameof(CanEditAdvancedTunneling));
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
        _suppressProxyExclusion = true;
        try
        {
            ProxyHost = "";
            ProxyPort = "";
            ProxyUsername = "";
            ProxyPassword = "";
        }
        finally { _suppressProxyExclusion = false; }

        if (UpstreamProxyEnabled) _ = _tunnel.RestartAsync();
        else UpstreamProxyEnabled = true;
    }

    public string UpstreamProxyWarning =>
        HasUpstreamProxy
            ? "Every Psiphon connection is dialled through this proxy. If it is dead or "
            + "unreachable the tunnel cannot connect in any protocol mode — turn the switch "
            + "off or clear the address."
            : "";

    public bool HasUpstreamProxyWarning => HasUpstreamProxy;

    public bool HasUpstreamProxy =>
        UpstreamProxyEnabled && !string.IsNullOrWhiteSpace(ProxyHost);

    private bool _suppressProxyExclusion;

    public bool SupportsProxyCredentials => true;

    public bool SupportsProxyPassword =>
    !string.Equals(SelectedProxyScheme, "socks4a", StringComparison.OrdinalIgnoreCase);

    [ObservableProperty] private string _socksPort = "";
    partial void OnSocksPortChanged(string value)
    {
        _settingsService.Settings.LocalSocksProxyPort = ParseListenPort(value);
        _settingsService.Save();
        OnPropertyChanged(nameof(DisplaySocksPort));
        RefreshLanProxyInfo();
    }

    [ObservableProperty] private string _httpPort = "";
    partial void OnHttpPortChanged(string value)
    {
        _settingsService.Settings.LocalHttpProxyPort = ParseListenPort(value);
        _settingsService.Save();
        OnPropertyChanged(nameof(DisplayHttpPort));
        RefreshLanProxyInfo();
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

    [ObservableProperty] private string _saveButtonText = "Save Settings";

    private void OnSettingsServiceChanged(object? sender, EventArgs e)
    {
        if (System.Windows.Application.Current is { } app && !app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.BeginInvoke(new Action(() => OnSettingsServiceChanged(sender, e)));
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

        _suppressAetherSideEffects = true;
        try
        {
            AetherScanMode = NormalizeAetherScan(s.AetherScanMode);
            AetherNoize = NormalizeAetherNoize(s.AetherNoize);
            AetherIpVersion = NormalizeAetherIp(s.AetherIpVersion);
            AetherFragment = s.AetherFragment;
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

        SaveButtonText = "Saved!";
        await Task.Delay(2000);
        SaveButtonText = "Save Settings";
    }

    private static string FormatListenPort(int port)
    => port is >= 1 and <= 65535 ? port.ToString() : "";

    private static int ParseListenPort(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        if (!int.TryParse(text.Trim(), out var p)) return 0;
        return p is >= 1 and <= 65535 ? p : 0;
    }

    public sealed record ProtocolOption(string Key, string Display);

    public ObservableCollection<ProtocolOption> ProtocolModeOptions { get; } = new()
    {
        new("auto", "Auto"),
        new("direct", "Direct"),
        new("cdn_fronting", "CDN Fronting"),
        new("conduit", "Conduit"),
    };

    [ObservableProperty] private string _selectedProtocolMode = "auto";
    partial void OnSelectedProtocolModeChanged(string value)
    {
        if (_suppressProtocolSideEffects) return;
        _settingsService.Settings.ProtocolMode = value ?? "auto";
        _settingsService.Save();
        OnPropertyChanged(nameof(IsAutoProtocolMode));
        OnPropertyChanged(nameof(IsDirectMode));
        OnPropertyChanged(nameof(IsCdnFrontingMode));
        OnPropertyChanged(nameof(IsConduitMode));
        OnPropertyChanged(nameof(IsConduitCustomMode));
        OnPropertyChanged(nameof(ShowBeastMode));
        OnPropertyChanged(nameof(CanUseAutoFind));

        if (value == "cdn_fronting" && HasUpstreamProxy)
        {
            _suppressProxyExclusion = true;
            try
            {
                ProxyHost = "";
                ProxyPort = "";
                ProxyUsername = "";
                ProxyPassword = "";
            }
            finally { _suppressProxyExclusion = false; }
        }
    }

    public bool IsAutoProtocolMode => SelectedProtocolMode == "auto";
    public bool IsDirectMode => SelectedProtocolMode == "direct" && !HasUpstreamProxy;
    public bool IsCdnFrontingMode => SelectedProtocolMode == "cdn_fronting" && !HasUpstreamProxy;
    public bool IsConduitMode => SelectedProtocolMode == "conduit" && !HasUpstreamProxy;
    public bool ShowBeastMode => (SelectedProtocolMode == "direct" || SelectedProtocolMode == "cdn_fronting") && !HasUpstreamProxy;

    [ObservableProperty] private string _conduitMode = "auto";
    partial void OnConduitModeChanged(string value)
    {
        _settingsService.Settings.ConduitMode = value ?? "auto";
        _settingsService.Save();
        OnPropertyChanged(nameof(IsConduitCustomMode));
    }

    public bool IsConduitCustomMode => ConduitMode == "custom";

    [ObservableProperty] private string _conduitCompartmentId = "";
    partial void OnConduitCompartmentIdChanged(string value)
    {
        _settingsService.Settings.ConduitCompartmentId = value ?? "";
        _settingsService.Save();
    }

    [ObservableProperty] private bool _conduitRejectCensoredCountries = true;
    partial void OnConduitRejectCensoredCountriesChanged(bool value)
    {
        _settingsService.Settings.ConduitRejectCensoredCountries = value;
        _settingsService.Save();
    }

    public bool CanUseAutoFind => IsCdnFrontingMode;

    public bool CanEditAdvancedTunneling => !HasUpstreamProxy;

    [ObservableProperty] private bool _beastMode;
    partial void OnBeastModeChanged(bool value) { _settingsService.Settings.BeastMode = value; _settingsService.Save(); }

    [ObservableProperty] private string _cdnFrontingCustomIpList = "";
    partial void OnCdnFrontingCustomIpListChanged(string value)
    {
        if (_suppressCdnIpListSideEffects) return;
        _settingsService.Settings.CdnFrontingCustomIpList = value ?? "";
        _settingsService.Save();
    }

    [ObservableProperty] private string _cdnFrontingCustomSni = "";
    partial void OnCdnFrontingCustomSniChanged(string value)
    {
        if (_suppressCdnSniSideEffects) return;
        _settingsService.Settings.CdnFrontingCustomSni = value ?? "";
        _settingsService.Save();
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

    private bool _suppressProtocolSideEffects;
    private bool _suppressMethodSideEffects;
    private bool _suppressAetherSideEffects;
    private bool _suppressTorSideEffects;

    public IReadOnlyList<ConnectionMethodOption> ConnectionMethods => ConnectionMethodExtensions.AllOptions;

    [ObservableProperty] private bool _killSwitchEnabled;
    partial void OnKillSwitchEnabledChanged(bool value)
    {
        _settingsService.Settings.KillSwitchEnabled = value;
        _settingsService.Save();
    }

    public bool CanChangeMethod => _tunnel.State != ConnectionState.Connecting && _tunnel.State != ConnectionState.Disconnecting;

    [ObservableProperty] private string _selectedConnectionMethod = "psiphon";
    partial void OnSelectedConnectionMethodChanged(string value)
    {
        RaiseMethodVisibility();
        if (_suppressMethodSideEffects) return;
        var token = ConnectionMethodExtensions.ParseConnectionMethod(value).ToToken();
        _settingsService.Settings.ConnectionMethod = token;
        _settingsService.Save();

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
        OnPropertyChanged(nameof(ActiveMethodDescription));
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
    public bool IsPsiphonMethod => CurrentMethod is ConnectionMethod.Psiphon or ConnectionMethod.PsiphonOverWarp;
    public bool IsAetherMethod => CurrentMethod.IsAether() || CurrentMethod.IsChained();
    public bool IsMasqueMethod =>
        CurrentMethod == ConnectionMethod.Masque ||
        (CurrentMethod.IsChained() && SelectedChainedOuterTransport is "auto" or "masque");
    public bool IsTorMethod => CurrentMethod is ConnectionMethod.Tor or ConnectionMethod.TorOverWarp;
    public bool IsChainedMethod => CurrentMethod.IsChained();
    public bool SupportsCustomProxyPorts =>
        CurrentMethod is ConnectionMethod.Psiphon
                      or ConnectionMethod.Tor
                      or ConnectionMethod.PsiphonOverWarp
                      or ConnectionMethod.TorOverWarp;

    public string ProxyPortsSubtitle => SupportsCustomProxyPorts
        ? "Blank = auto-pick random open ports on next connect."
        : "Ports are assigned dynamically by the core in this mode.";

    public string ActiveMethodDescription =>
        ConnectionMethods.FirstOrDefault(m => m.Key == SelectedConnectionMethod)?.Description ?? "";

    private void RestartIfActive(ConnectionMethod owner)
    {
        if (CurrentMethod == owner ||
            (owner.IsAether() && (CurrentMethod.IsAether() || CurrentMethod.IsChained())))
        {
            _ = _tunnel.RestartAsync();
        }
    }

    [ObservableProperty] private string _aetherManualPeer = "";
    partial void OnAetherManualPeerChanged(string value)
    {
        if (_suppressAetherSideEffects) return;
        _settingsService.Settings.AetherManualPeer = value?.Trim() ?? "";
        _settingsService.Save();
        RestartIfActive(ConnectionMethod.Masque);
    }

    public sealed record LabeledOption(string Key, string Display);

    public ObservableCollection<LabeledOption> AetherScanModes { get; } = new()
    {
        new("turbo", "Turbo — fastest, fewest endpoints"),
        new("balanced", "Balanced (recommended)"),
        new("thorough", "Thorough — scan more endpoints"),
        new("stealth", "Stealth — quieter, slower scanning"),
        new("ironclad", "Ironclad — most resilient, slowest"),
    };

    [ObservableProperty] private string _aetherScanMode = "balanced";
    partial void OnAetherScanModeChanged(string value)
    {
        if (_suppressAetherSideEffects) return;
        _settingsService.Settings.AetherScanMode = NormalizeAetherScan(value);
        _settingsService.Save();
        RestartIfActive(ConnectionMethod.Masque);
    }

    public ObservableCollection<LabeledOption> AetherNoizeModes { get; } = new()
    {
        new("off", "Off — no obfuscation"),
        new("light", "Light — for simple firewalls"),
        new("balanced", "Balanced — for GFW-class filtering (recommended)"),
        new("aggressive", "Aggressive — maximum obfuscation"),
    };

    [ObservableProperty] private string _aetherNoize = "balanced";
    partial void OnAetherNoizeChanged(string value)
    {
        if (_suppressAetherSideEffects) return;
        _settingsService.Settings.AetherNoize = NormalizeAetherNoize(value);
        _settingsService.Save();
        RestartIfActive(ConnectionMethod.Masque);
    }

    public ObservableCollection<LabeledOption> AetherIpVersions { get; } = new()
    {
        new("4", "IPv4 only"),
        new("6", "IPv6 only"),
        new("dual", "Dual stack (IPv4 + IPv6)"),
    };

    [ObservableProperty] private string _aetherIpVersion = "4";
    partial void OnAetherIpVersionChanged(string value)
    {
        if (_suppressAetherSideEffects) return;
        _settingsService.Settings.AetherIpVersion = NormalizeAetherIp(value);
        _settingsService.Save();
        RestartIfActive(ConnectionMethod.Masque);
    }

    public ObservableCollection<LabeledOption> AetherMasqueTransports { get; } = new()
    {
        new("h3", "HTTP/3 over QUIC — fastest (recommended)"),
        new("h2", "HTTP/2 over TCP — for networks that block UDP"),
    };

    [ObservableProperty] private string _aetherMasqueTransport = "h3";
    partial void OnAetherMasqueTransportChanged(string value)
    {
        OnPropertyChanged(nameof(CanUseFragment));
        OnPropertyChanged(nameof(FragmentHint));
        if (_suppressAetherSideEffects) return;
        _settingsService.Settings.AetherMasqueTransport = AetherEngine.NormalizeTransport(value);
        _settingsService.Save();
        RestartIfActive(ConnectionMethod.Masque);
    }

    public bool CanUseFragment =>
        AetherEngine.NormalizeTransport(AetherMasqueTransport) == "h2";

    public string FragmentHint => CanUseFragment
        ? "Splits the TLS ClientHello across packets so SNI-based filters cannot match it."
        : "Only available on the HTTP/2 (TCP) transport.";

    [ObservableProperty] private bool _aetherFragment;
    partial void OnAetherFragmentChanged(bool value)
    {
        if (_suppressAetherSideEffects) return;
        _settingsService.Settings.AetherFragment = value;
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

    public sealed record ChainedOuterOption(string Key, string Display, string Description);

    public ObservableCollection<ChainedOuterOption> ChainedOuterTransports { get; } = new()
    {
        new("auto", "Auto", "Try MASQUE, then WireGuard, then WoW — remembers what works"),
        new("masque", "MASQUE", "HTTP/3, falling back to HTTP/2 with TLS fragmentation"),
        new("wireguard", "WireGuard", "Single WARP tunnel; blocked on some carriers"),
        new("warp_on_warp", "WoW (WARP on WARP)", "WARP on WARP — slowest, for the most filtered networks"),
    };

    [ObservableProperty] private string _selectedChainedOuterTransport = "auto";
    partial void OnSelectedChainedOuterTransportChanged(string value)
    {
        _settingsService.Settings.ChainedOuterTransport = NormalizeChainedOuter(value);
        _settingsService.Save();
        OnPropertyChanged(nameof(IsMasqueMethod));
        RestartIfActive(ConnectionMethod.Masque);
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
            "stealth" => "stealth",
            "ironclad" => "ironclad",
            _ => "balanced",
        };

    private static string NormalizeAetherNoize(string? v) =>
        (v ?? "").Trim().ToLowerInvariant() switch
        {
            "off" => "off",
            "light" or "firewall" => "light",
            "aggressive" or "gfw" => "aggressive",
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
        _settingsService.Settings.AutoFindIpAndSni = false;
        _settingsService.Settings.SaveFoundIpsAndSni = false;
        _settingsService.Settings.ConduitMode = "auto";
        _settingsService.Settings.ConduitCompartmentId = "";
        _settingsService.Settings.ConduitRejectCensoredCountries = true;
        _settingsService.Settings.LanProxyUsername = "";
        _settingsService.Settings.LanProxyPassword = "";
        _settingsService.Settings.AetherScanMode = "balanced";
        _settingsService.Settings.AetherNoize = "balanced";
        _settingsService.Settings.AetherIpVersion = "4";
        _settingsService.Settings.AetherFragment = false;
        _settingsService.Settings.AetherMasqueTransport = "h3";
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
        AutoFindIpAndSni = false;
        SaveFoundIpsAndSni = false;
        ConduitMode = "auto";
        ConduitCompartmentId = "";
        ConduitRejectCensoredCountries = true;
        LanProxyUsername = "";
        LanProxyPassword = "";
        AetherScanMode = "balanced";
        AetherNoize = "balanced";
        AetherIpVersion = "4";
        AetherFragment = false;
        AetherMasqueTransport = "h3";
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
    private string _aetherStatusText = "Up to date";

    [ObservableProperty]
    private bool _hasAetherUpdate;

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
    private string _torStatusText = "Up to date";

    [ObservableProperty]
    private bool _isCheckingTorUpdate;

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
        if (IsCheckingAllUpdates || IsCheckingAetherUpdate || IsCheckingTorUpdate || IsUpdatingAether) return;
        IsCheckingAllUpdates = true;

        try
        {
            await Task.WhenAll(CheckAetherUpdateAsync(), CheckTorUpdateAsync());
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
        TorStatusText = "Checking for updates...";

        try
        {
            var info = await _coreUpdateService.CheckForUpdateAsync("tor");
            TorInstalledVersion = "v" + info.InstalledVersion;
            TorLatestVersion = "v" + info.LatestVersion;
            TorStatusText = "Tor core is up to date";
        }
        catch (Exception ex)
        {
            TorStatusText = "Check failed: " + ex.Message;
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
        AetherStatusText = "Checking for updates...";

        try
        {
            var info = await _coreUpdateService.CheckForUpdateAsync("aether");
            AetherInstalledVersion = "v" + info.InstalledVersion;
            AetherLatestVersion = "v" + info.LatestVersion;
            HasAetherUpdate = info.HasUpdate;
            AetherStatusText = info.HasUpdate
                ? $"Update available: v{info.LatestVersion}"
                : "Aether is up to date";
        }
        catch (Exception ex)
        {
            AetherStatusText = "Check failed: " + ex.Message;
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
        AetherStatusText = "Starting download...";

        try
        {
            var progress = new Progress<int>(p =>
            {
                AetherUpdateProgress = p;
                AetherStatusText = p < 80
                    ? $"Downloading: {p}%"
                    : (p < 95 ? "Extracting & Installing..." : "Finalizing...");
            });

            var success = await _coreUpdateService.UpdateCoreAsync("aether", progress);
            if (success)
            {
                var newVer = _coreUpdateService.GetInstalledVersion("aether");
                AetherInstalledVersion = "v" + newVer;
                AetherLatestVersion = "v" + newVer;
                HasAetherUpdate = false;
                AetherStatusText = $"Updated to v{newVer}";

                CoreUpdateSuccessTitle = "Aether Core Updated Successfully";
                CoreUpdateSuccessMessage = $"Aether network engine has been successfully updated to v{newVer}.\nPlease restart the application to apply the updated core.";
                IsCoreUpdateSuccessDialogOpen = true;
            }
        }
        catch (Exception ex)
        {
            AetherStatusText = "Update failed: " + ex.Message;
        }
        finally
        {
            IsUpdatingAether = false;
        }
    }

    [RelayCommand]
    private void RestartApplication()
    {
        try
        {
            var exe = Process.GetCurrentProcess().MainModule?.FileName ?? Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            }
        }
        catch { }

        try
        {
            Application.Current.Shutdown();
        }
        catch
        {
            Environment.Exit(0);
        }
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
            "http" or "socks5" or "socks5h" or "socks4a" => scheme,
            _ => "http",
        };
    }
}
