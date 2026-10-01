using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Se7enPro.Models;
using Se7enPro.Services;

namespace Se7enPro.ViewModels;

public sealed record SplitKindOption(string Key, string English) : LocalizedItem
{
    public string Display => Loc.Of(English);
}

public sealed partial class SplitTunnelViewModel : PageViewModelBase
{
    private readonly ISettingsService _settingsService;
    private readonly ITunManager _tun;
    private readonly ITunnelCoreManager _tunnel;

    private bool _suppressPersist;
    private bool _suppressTunSideEffects;

    public override string Title => "Split Tunnel";
    public override string Route => "split";
    public override string Icon => "CallSplit";

    public SplitTunnelViewModel(
        ISettingsService settingsService,
        ITunManager tun,
        ITunnelCoreManager tunnel)
    {
        _settingsService = settingsService;
        _tun = tun;
        _tunnel = tunnel;

        var s = _settingsService.Settings;
        _splitTunnelEnabled = s.SplitTunnelEnabled;
        _splitTunnelMode =
            string.Equals(s.SplitTunnelMode, "include", StringComparison.OrdinalIgnoreCase)
                ? "include"
                : "exclude";
        _systemWideEnabled = AdminElevation.IsAdministrator() && s.SystemWideTunneling;
        LoadSplitTunnelEntries();

        _settingsService.SettingsChanged += OnSettingsServiceChanged;
        _tun.StateChanged += OnTunStateChanged;
        _tunnel.StateChanged += OnTunnelStateChanged;

        
        
        Loc.Changed += () => Post(() =>
        {
            foreach (var entry in DomainEntries) entry.RaiseDirection();
            foreach (var entry in AppEntries) entry.RaiseDirection();
        });
    }

    
    
    
    
    

    [ObservableProperty] private int _activeTab = 0; 

    public bool IsWebsitesTab => ActiveTab == 0;
    public bool IsAppsTab => ActiveTab == 1;

    [RelayCommand]
    private void SelectWebsitesTab() => SetTab(0);

    [RelayCommand]
    private void SelectAppsTab() => SetTab(1);

    public void SetTab(int tab)
    {
        ActiveTab = tab;
        OnPropertyChanged(nameof(IsWebsitesTab));
        OnPropertyChanged(nameof(IsAppsTab));
    }

    public ObservableCollection<SplitTunnelEntryView> SplitTunnelEntries { get; } = new();
    public ObservableCollection<SplitTunnelEntryView> DomainEntries { get; } = new();
    public ObservableCollection<SplitTunnelEntryView> AppEntries { get; } = new();

    public int DomainCount => DomainEntries.Count;
    public int AppCount => AppEntries.Count;

    public bool HasDomainEntries => DomainEntries.Count > 0;
    public bool NoDomainEntries => DomainEntries.Count == 0;

    public bool HasAppEntries => AppEntries.Count > 0;
    public bool NoAppEntries => AppEntries.Count == 0;

    public string DomainRulesCountText => string.Format(Loc.Of("{0} configured"), DomainEntries.Count);

    public string AppRulesSubtitle => IsIncludeMode
        ? Loc.Of("Only the applications listed below are routed through the secure tunnel.")
        : Loc.Of("Selected applications bypass the tunnel and connect directly to the internet.");

    public ObservableCollection<SplitKindOption> SplitTunnelKinds { get; } = new()
    {
        new("domain", "Site / domain"),
        new("ip", "IP or CIDR"),
    };

    [ObservableProperty] private bool _splitTunnelEnabled;
    partial void OnSplitTunnelEnabledChanged(bool value)
    {
        if (_suppressPersist) return;
        _settingsService.Settings.SplitTunnelEnabled = value;
        _settingsService.Save();
        RefreshStatus();
    }

    [ObservableProperty] private string _splitTunnelMode = "exclude";
    partial void OnSplitTunnelModeChanged(string value)
    {
        RefreshModeSurface();
        if (_suppressPersist) return;

        var v = string.Equals(value, "include", StringComparison.OrdinalIgnoreCase)
            ? "include"
            : "exclude";
        _settingsService.Settings.SplitTunnelMode = v;
        _settingsService.Save();
    }

    public bool IsIncludeMode =>
        string.Equals(SplitTunnelMode, "include", StringComparison.OrdinalIgnoreCase);

    public string SplitTunnelModeHint => IsIncludeMode
        ? Loc.Of("Only the sites, IPs and apps listed below reach the internet through the VPN. Everything else uses your real connection.")
        : Loc.Of("The sites, IPs and apps listed below use your real connection. Everything else is tunnelled through the VPN.");

    public string ModeSummary
    {
        get
        {
            var n = DomainEntries.Count + AppEntries.Count;
            if (n == 0)
            {
                return Loc.Of("Right now: the list is empty, so every connection goes through the VPN. Add a rule below to split it.");
            }

            return IsIncludeMode
                ? string.Format(Loc.Of("Right now: only these {0} rule(s) go through the VPN. Everything else uses your normal connection and is NOT protected."), n)
                : string.Format(Loc.Of("Right now: these {0} rule(s) use your normal connection with your real local IP. Everything else goes through the VPN."), n);
        }
    }

    public bool ShowIncludeWarning => SplitTunnelEnabled && IsIncludeMode;

    private void RefreshModeSurface()
    {
        OnPropertyChanged(nameof(IsIncludeMode));
        OnPropertyChanged(nameof(SplitTunnelModeHint));
        OnPropertyChanged(nameof(ModeSummary));
        OnPropertyChanged(nameof(ShowIncludeWarning));
        OnPropertyChanged(nameof(AppRulesSubtitle));

        var include = IsIncludeMode;
        foreach (var entry in DomainEntries) entry.IncludeMode = include;
        foreach (var entry in AppEntries) entry.IncludeMode = include;
        foreach (var entry in SplitTunnelEntries) entry.IncludeMode = include;
    }

    [ObservableProperty] private string _newSplitKind = "domain";

    [ObservableProperty] private string _newSplitValue = "";
    partial void OnNewSplitValueChanged(string value)
    {
        if (!string.IsNullOrEmpty(SplitTunnelError)) SplitTunnelError = "";
    }

    [ObservableProperty] private string _splitTunnelError = "";

    public bool HasSplitTunnelEntries => (DomainEntries.Count + AppEntries.Count) > 0;
    public bool NoSplitTunnelEntries => (DomainEntries.Count + AppEntries.Count) == 0;

    private void LoadSplitTunnelEntries()
    {
        SplitTunnelEntries.Clear();
        DomainEntries.Clear();
        AppEntries.Clear();

        var stored = _settingsService.Settings.SplitTunnelEntries;
        var include = IsIncludeMode;
        if (stored is not null)
        {
            foreach (var e in stored)
            {
                if (e is null || string.IsNullOrWhiteSpace(e.Value)) continue;
                var kind = (e.Kind ?? "domain").Trim().ToLowerInvariant();
                if (kind != "ip" && kind != "app") kind = "domain";
                var row = new SplitTunnelEntryView
                {
                    Kind = kind,
                    Value = e.Value.Trim(),
                    IncludeMode = include,
                };
                SplitTunnelEntries.Add(row);
                if (kind == "app")
                    AppEntries.Add(row);
                else
                    DomainEntries.Add(row);
            }
        }

        NotifyEntriesChanged();
    }

    private void NotifyEntriesChanged()
    {
        OnPropertyChanged(nameof(HasSplitTunnelEntries));
        OnPropertyChanged(nameof(NoSplitTunnelEntries));
        OnPropertyChanged(nameof(DomainCount));
        OnPropertyChanged(nameof(AppCount));
        OnPropertyChanged(nameof(HasDomainEntries));
        OnPropertyChanged(nameof(NoDomainEntries));
        OnPropertyChanged(nameof(HasAppEntries));
        OnPropertyChanged(nameof(NoAppEntries));
        OnPropertyChanged(nameof(DomainRulesCountText));
        OnPropertyChanged(nameof(AppRulesSubtitle));
        OnPropertyChanged(nameof(ModeSummary));
    }

    private SplitTunnelEntryView NewRow(string kind, string value) =>
        new() { Kind = kind, Value = value, IncludeMode = IsIncludeMode };

    [RelayCommand]
    private async Task OpenAddSiteDialog()
    {
        var rule = await Ui.AskRuleAsync();
        if (rule is not null) AddRule(rule.Value.Kind, rule.Value.Value);
    }

    [RelayCommand]
    private void AddPreset(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;
        var p = raw.Trim();
        var kind = (p.Contains('/') || char.IsDigit(p[0])) ? "ip" : "domain";
        AddRule(kind, p);
    }

    public bool AddRule(string kind, string rawValue)
    {
        kind = (kind ?? "domain").Trim().ToLowerInvariant();
        if (kind != "ip" && kind != "app") kind = "domain";

        var normalized = Normalize(kind, rawValue);
        if (string.IsNullOrEmpty(normalized))
        {
            SplitTunnelError = Loc.Of("Invalid target format.");
            return false;
        }

        if (Contains(kind, normalized!))
        {
            return false;
        }

        var row = NewRow(kind, normalized!);
        if (kind == "app")
            AppEntries.Add(row);
        else
            DomainEntries.Add(row);

        SplitTunnelError = "";
        PersistSplitTunnelEntries();
        return true;
    }

    [RelayCommand]
    private void AddSplitEntry()
    {
        var kind = (NewSplitKind ?? "domain").Trim().ToLowerInvariant();
        if (kind != "ip" && kind != "app") kind = "domain";

        var value = (NewSplitValue ?? "").Trim();
        if (value.Length == 0)
        {
            SplitTunnelError = Loc.Of("Enter a value first.");
            return;
        }

        if (AddRule(kind, value))
        {
            NewSplitValue = "";
        }
    }

    [RelayCommand]
    private void RemoveSplitEntry(SplitTunnelEntryView? entry)
    {
        if (entry is null) return;
        DomainEntries.Remove(entry);
        AppEntries.Remove(entry);
        SplitTunnelEntries.Remove(entry);
        PersistSplitTunnelEntries();
    }

    [RelayCommand]
    private async Task ClearDomainEntries()
    {
        if (DomainEntries.Count == 0) return;
        if (!await Ui.ConfirmAsync(
                Loc.Of("Clear Websites & IPs"),
                string.Format(Loc.Of("Remove all {0} website and IP rules?"), DomainEntries.Count)))
            return;

        DomainEntries.Clear();
        PersistSplitTunnelEntries();
    }

    [RelayCommand]
    private async Task ClearAppEntries()
    {
        if (AppEntries.Count == 0) return;
        if (!await Ui.ConfirmAsync(
                Loc.Of("Clear Applications"),
                string.Format(Loc.Of("Remove all {0} application rules?"), AppEntries.Count)))
            return;

        AppEntries.Clear();
        PersistSplitTunnelEntries();
    }

    [RelayCommand]
    private async Task ClearSplitEntries()
    {
        if (DomainEntries.Count == 0 && AppEntries.Count == 0) return;

        if (!await Ui.ConfirmAsync(
                Loc.Of("Clear all rules"),
                string.Format(Loc.Of("Remove all {0} split-tunnel entries?"),
                    DomainEntries.Count + AppEntries.Count)))
            return;

        DomainEntries.Clear();
        AppEntries.Clear();
        PersistSplitTunnelEntries();
    }

    [RelayCommand]
    private async Task PickApps()
    {
        var chosen = await Ui.PickApplicationsAsync();
        if (chosen.Count == 0) return;

        var added = 0;
        foreach (var raw in chosen)
        {
            var normalized = Normalize("app", raw);
            if (string.IsNullOrEmpty(normalized)) continue;
            if (Contains("app", normalized!)) continue;

            AppEntries.Add(NewRow("app", normalized!));
            added++;
        }

        if (added > 0)
        {
            SplitTunnelError = "";
            PersistSplitTunnelEntries();
        }
    }

        private static readonly string[] IranBypassDomains =
    {
        "ir",                 
        "digikala.com",
        "aparat.com",
        "filimo.com",
        "varzesh3.com",
        "telewebion.com",
        "sheypoor.com",
        "zarinpal.com",
    };

    [RelayCommand]
    private void ApplyIranBypassPreset()
    {
        SplitTunnelMode = "exclude";
        if (!SplitTunnelEnabled) SplitTunnelEnabled = true;

        var added = 0;
        foreach (var domain in IranBypassDomains)
        {
            var normalized = SplitRules.NormalizeSplitDomain(domain);
            if (string.IsNullOrEmpty(normalized)) continue;
            if (Contains("domain", normalized!)) continue;

            DomainEntries.Add(NewRow("domain", normalized!));
            added++;
        }

        SplitTunnelError = added > 0
            ? ""
            : Loc.Of("Those sites are already in the list.");
        if (added > 0) PersistSplitTunnelEntries();
    }

    private bool Contains(string kind, string value)
    {
        var list = kind == "app" ? (IEnumerable<SplitTunnelEntryView>)AppEntries : DomainEntries;
        foreach (var existing in list)
        {
            if (existing.Kind == kind &&
                string.Equals(existing.Value, value, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static string? Normalize(string kind, string value) => kind switch
    {
        "ip" => SplitRules.NormalizeSplitIpCidr(value),
        "app" => NormalizeSplitApp(value),
        _ => SplitRules.NormalizeSplitDomain(value),
    };

    private static string? NormalizeSplitApp(string raw)
    {
        var s = (raw ?? "").Trim().Trim('"');
        if (s.Length == 0) return null;
        return SplitRules.LooksLikeAppPath(s)
            ? s
            : SplitRules.NormalizeProcessName(s);
    }

    private void PersistSplitTunnelEntries()
    {
        var list = new List<SplitTunnelEntry>(DomainEntries.Count + AppEntries.Count);
        foreach (var v in DomainEntries)
        {
            list.Add(new SplitTunnelEntry { Kind = v.Kind, Value = v.Value });
        }
        foreach (var v in AppEntries)
        {
            list.Add(new SplitTunnelEntry { Kind = v.Kind, Value = v.Value });
        }
        _settingsService.Settings.SplitTunnelEntries = list;
        _settingsService.Save();

        SplitTunnelEntries.Clear();
        foreach (var item in DomainEntries) SplitTunnelEntries.Add(item);
        foreach (var item in AppEntries) SplitTunnelEntries.Add(item);

        NotifyEntriesChanged();
        RefreshStatus();
    }

    
    
    

    public bool IsAdminElevated { get; } = AdminElevation.IsAdministrator();

    [ObservableProperty] private bool _systemWideEnabled;
    partial void OnSystemWideEnabledChanged(bool value)
    {
        if (_suppressTunSideEffects) return;

        if (value && !IsAdminElevated)
        {
            _ = AskForElevationAsync();
            return;
        }

        _settingsService.Settings.SystemWideTunneling = value;
        _settingsService.Save();
        RefreshStatus();
    }

    [RelayCommand]
    private void RestartAsAdmin() => _ = TryRestartAsAdmin();

        private async Task AskForElevationAsync()
    {
        if (await Ui.ConfirmElevationAsync() && TryRestartAsAdmin()) return;

        _suppressTunSideEffects = true;
        try { SystemWideEnabled = false; }
        finally { _suppressTunSideEffects = false; }

        RefreshStatus();
    }

        private bool TryRestartAsAdmin()
    {
        _settingsService.Settings.SystemWideTunneling = true;
        _settingsService.Save();

        if (AdminElevation.TryRestartElevated()) return true;

        _settingsService.Settings.SystemWideTunneling = false;
        _settingsService.Save();
        return false;
    }

        [RelayCommand]
    private void EnableSystemWide() => SystemWideEnabled = true;

        public bool IsSplitActive =>
        SplitTunnelEnabled
        && SplitTunnelEntries.Count > 0
        && SystemWideEnabled
        && _tun.State == TunState.Running;

        public bool ShowSystemWideWarning => SplitTunnelEnabled && !SystemWideEnabled;

    public string StatusText
    {
        get
        {
            if (!IsAdminElevated)
                return Loc.Of("Run Se7en Pro as Administrator — split tunnelling needs the system-wide adapter.");

            if (!SplitTunnelEnabled)
                return Loc.Of("Split tunnelling is off. Everything follows the normal connection.");

            if (!SystemWideEnabled)
                return Loc.Of("Turn on system-wide tunnelling — these rules only apply in TUN mode.");

            if (SplitTunnelEntries.Count == 0)
                return Loc.Of("No rules yet. Add a site, an IP range, or pick an app below.");

            return _tun.State switch
            {
                TunState.Starting => Loc.Of("Starting the system-wide adapter…"),
                TunState.Running =>
                    string.Equals(SplitTunnelMode, "include", StringComparison.OrdinalIgnoreCase)
                        ? string.Format(Loc.Of("Active — only the {0} listed rule(s) go through the VPN."), SplitTunnelEntries.Count)
                        : string.Format(Loc.Of("Active — the {0} listed rule(s) bypass the VPN."), SplitTunnelEntries.Count),
                TunState.Stopping => Loc.Of("Stopping the system-wide adapter…"),
                TunState.Error => _tun.LastError ?? Loc.Of("The system-wide adapter failed to start."),
                _ => _tunnel.State == ConnectionState.Connected
                    ? Loc.Of("Waiting for the system-wide adapter to come up…")
                    : Loc.Of("Rules are saved — they take effect once you connect."),
            };
        }
    }

    
    
    private static readonly object BrushActive = Ui.FrozenBrush("#22C55E");
    private static readonly object BrushPending = Ui.FrozenBrush("#F59E0B");
    private static readonly object BrushError = Ui.FrozenBrush("#EF4444");
    private static readonly object BrushIdle = Ui.FrozenBrush("#6B7280");

    public object StatusBrush
    {
        get
        {
            if (!IsAdminElevated) return BrushError;
            if (!SplitTunnelEnabled) return BrushIdle;
            if (_tun.State == TunState.Error) return BrushError;
            if (IsSplitActive) return BrushActive;
            return BrushPending;
        }
    }

    private void RefreshStatus()
    {
        OnPropertyChanged(nameof(IsSplitActive));
        OnPropertyChanged(nameof(ShowSystemWideWarning));
        OnPropertyChanged(nameof(ShowIncludeWarning));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusBrush));
    }

    
    
    

    private void OnTunStateChanged(object? sender, EventArgs e) => Post(RefreshStatus);

    private void OnTunnelStateChanged(object? sender, ConnectionState e) => Post(RefreshStatus);

    private void OnSettingsServiceChanged(object? sender, EventArgs e) => Post(() =>
    {
        var s = _settingsService.Settings;

        
        if (s.SystemWideTunneling != SystemWideEnabled)
        {
            _suppressTunSideEffects = true;
            try { SystemWideEnabled = s.SystemWideTunneling; }
            finally { _suppressTunSideEffects = false; }
        }

        if (s.SplitTunnelEnabled != SplitTunnelEnabled)
        {
            _suppressPersist = true;
            try { SplitTunnelEnabled = s.SplitTunnelEnabled; }
            finally { _suppressPersist = false; }
        }

        var mode = string.Equals(s.SplitTunnelMode, "include", StringComparison.OrdinalIgnoreCase)
            ? "include"
            : "exclude";
        if (!string.Equals(mode, SplitTunnelMode, StringComparison.Ordinal))
        {
            _suppressPersist = true;
            try { SplitTunnelMode = mode; }
            finally { _suppressPersist = false; }
        }

        RefreshStatus();
    });

    private static void Post(Action action) => Ui.Post(action);
}

public sealed partial class SplitTunnelEntryView : ObservableObject
{
    public string Kind { get; init; } = "domain";
    public string Value { get; init; } = "";

        [ObservableProperty] private bool _includeMode;

    partial void OnIncludeModeChanged(bool value)
    {
        OnPropertyChanged(nameof(DirectionLabel));
        OnPropertyChanged(nameof(DirectionIcon));
        OnPropertyChanged(nameof(DirectionBrush));
    }

    public string DirectionLabel => Loc.Of(IncludeMode ? "through VPN" : "bypasses VPN");

        public void RaiseDirection()
    {
        OnPropertyChanged(nameof(DirectionLabel));
        OnPropertyChanged(nameof(DirectionIcon));
        OnPropertyChanged(nameof(DirectionBrush));
    }

    public string DirectionIcon => IncludeMode ? "ShieldCheckOutline" : "ShieldOffOutline";

    public object DirectionBrush => IncludeMode ? DirectionVpnBrush : DirectionDirectBrush;

    private static readonly object DirectionVpnBrush = Ui.FrozenBrush("#38BDF8");
    private static readonly object DirectionDirectBrush = Ui.FrozenBrush("#F59E0B");

    public string KindLabel => Kind switch
    {
        "ip" => "IP / CIDR",
        "app" => "App",
        _ => "Site",
    };

    public string Icon => Kind switch
    {
        "ip" => "IpNetwork",
        "app" => "Application",
        _ => "Web",
    };
}
