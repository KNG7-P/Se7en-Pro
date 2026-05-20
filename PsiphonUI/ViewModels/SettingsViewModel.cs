using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PsiphonUI.Models;
using PsiphonUI.Services;

namespace PsiphonUI.ViewModels;

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

    private bool _suppressThemeSideEffects;

    private bool _suppressRegionSideEffects;

    public override string Title => "Settings";
    public override string Route => "settings";
    public override string Icon => "Cog";

    public SettingsViewModel(
        ISettingsService settingsService,
        IThemeService themeService,
        ITunnelCoreManager tunnel,
        IStartupRegistration startup)
    {
        _settingsService = settingsService;
        _themeService = themeService;
        _tunnel = tunnel;
        _startup = startup;

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
        _selectedCloseAction = ResolveCloseAction(s.OnCloseAction);
        _allowLanConnections = s.AllowLanConnections;

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

        _selectedProtocolMode = s.ProtocolMode switch
        {
            "direct" => "direct",
            "cdn_fronting" => "cdn_fronting",
            _ => "auto",
        };
        _beastMode = s.BeastMode;
        _cdnFrontingCustomIpList = s.CdnFrontingCustomIpList;
        _cdnFrontingCustomSni = s.CdnFrontingCustomSni;

        _settingsService.SettingsChanged += OnSettingsServiceChanged;
        _tunnel.StateChanged += OnTunnelStateChanged;
        RefreshLanProxyInfo();
    }

    private void OnTunnelStateChanged(object? sender, ConnectionState e)
    {
        if (System.Windows.Application.Current is { } app)
        {
            app.Dispatcher.BeginInvoke(new Action(RefreshLanProxyInfo));
        }
        else
        {
            RefreshLanProxyInfo();
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
        _settingsService.Settings.UpstreamProxyScheme = NormalizeScheme(value);
        _settingsService.Save();
        OnPropertyChanged(nameof(SupportsProxyCredentials));
        OnPropertyChanged(nameof(SupportsProxyPassword));
    }

    [ObservableProperty] private string _proxyHost = "";
    partial void OnProxyHostChanged(string value)
    {

        var hasProxy = !string.IsNullOrWhiteSpace(value);
        OnPropertyChanged(nameof(HasUpstreamProxy));
        OnPropertyChanged(nameof(IsCdnFrontingMode));
        if (hasProxy && !_suppressProxyExclusion)
        {
            if (!string.Equals(SelectedProtocolMode, "direct", StringComparison.Ordinal))
            {
                SelectedProtocolMode = "direct";
            }
            if (BeastMode) BeastMode = false;
        }
    }

    [ObservableProperty] private string _proxyPort = "";
    [ObservableProperty] private string _proxyUsername = "";
    [ObservableProperty] private string _proxyPassword = "";

    public bool HasUpstreamProxy => !string.IsNullOrWhiteSpace(ProxyHost);

    private bool _suppressProxyExclusion;

    public bool SupportsProxyCredentials => true;

    public bool SupportsProxyPassword =>
    !string.Equals(SelectedProxyScheme, "socks4a", StringComparison.OrdinalIgnoreCase);

    [ObservableProperty] private string _socksPort = "";
    partial void OnSocksPortChanged(string value) => RefreshLanProxyInfo();

    [ObservableProperty] private string _httpPort = "";
    partial void OnHttpPortChanged(string value) => RefreshLanProxyInfo();

    [ObservableProperty] private string _saveButtonText = "Save Settings";

    private void OnSettingsServiceChanged(object? sender, EventArgs e)
    {
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
    }

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
    };

    [ObservableProperty] private string _selectedProtocolMode = "auto";
    partial void OnSelectedProtocolModeChanged(string value)
    {
        _settingsService.Settings.ProtocolMode = value ?? "auto";
        _settingsService.Save();
        OnPropertyChanged(nameof(IsCdnFrontingMode));

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

    public bool IsCdnFrontingMode =>
    SelectedProtocolMode == "cdn_fronting" && !HasUpstreamProxy;

    public bool CanEditAdvancedTunneling => !HasUpstreamProxy;

    [ObservableProperty] private bool _beastMode;
    partial void OnBeastModeChanged(bool value) { _settingsService.Settings.BeastMode = value; _settingsService.Save(); }

    [ObservableProperty] private string _cdnFrontingCustomIpList = "";
    partial void OnCdnFrontingCustomIpListChanged(string value)
    {
        _settingsService.Settings.CdnFrontingCustomIpList = value ?? "";
        _settingsService.Save();
    }

    [ObservableProperty] private string _cdnFrontingCustomSni = "";
    partial void OnCdnFrontingCustomSniChanged(string value)
    {
        _settingsService.Settings.CdnFrontingCustomSni = value ?? "";
        _settingsService.Save();
    }

    [RelayCommand]
    private void ResetAdvanced()
    {
        _settingsService.Settings.DisableTimeouts = false;
        _settingsService.Settings.UpstreamProxy = "";
        _settingsService.Settings.UpstreamProxyScheme = "http";
        _settingsService.Settings.UpstreamProxyUsername = "";
        _settingsService.Settings.UpstreamProxyPassword = "";
        _settingsService.Settings.LocalSocksProxyPort = 0;
        _settingsService.Settings.LocalHttpProxyPort = 0;
        _settingsService.Settings.ProtocolMode = "auto";
        _settingsService.Settings.BeastMode = false;
        _settingsService.Settings.CdnFrontingCustomIpList = "";
        _settingsService.Settings.CdnFrontingCustomSni = "";
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
