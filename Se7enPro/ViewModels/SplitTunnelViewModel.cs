using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Se7enPro.Models;
using Se7enPro.Services;

namespace Se7enPro.ViewModels;

public sealed partial class SplitTunnelViewModel : PageViewModelBase
{
    private readonly ISettingsService _settingsService;
    private readonly ITunManager _tun;
    private readonly ITunnelCoreManager _tunnel;

    private bool _suppressPersist;
    private bool _suppressTunSideEffects;
    private bool _loading;

    public override string Title => Loc.T("splitTunnelTitle", "Split Tunnel");
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
        _systemWideEnabled = AdminElevation.IsAdministrator() && s.SystemWideTunneling;

        LoadProfiles();

        _settingsService.SettingsChanged += OnSettingsServiceChanged;
        _tun.StateChanged += OnTunStateChanged;
        _tunnel.StateChanged += OnTunnelStateChanged;
    }

    // ----------------------------------------------------------------- profiles

    public ObservableCollection<SplitProfileItem> Profiles { get; } = new();

    [ObservableProperty] private SplitProfileItem? _selectedProfile;

    // Switching the active profile changes which apps go where. If a tunnel is already
    // up, the new routing is only guaranteed once the connection is re-established, so
    // ask first and reconnect on yes. When nothing is connected the switch is silent.
    partial void OnSelectedProfileChanged(SplitProfileItem? value)
    {
        if (_loading || value is null) return;

        if (IsConnected)
        {
            _ = SwitchProfileAsync(value);
            return;
        }

        ApplyProfileSelection(value);
    }

    private bool IsConnected =>
        _tunnel.State is ConnectionState.Connected or ConnectionState.Connecting;

    private async Task SwitchProfileAsync(SplitProfileItem target)
    {
        var name = target.Name;

        if (await Ui.ConfirmAsync(
                Loc.Of("Apply new routing?"),
                string.Format(
                    Loc.Of("Switch to \u201c{0}\u201d and reconnect so the new routing takes effect?\n\nThe tunnel will drop for a moment."),
                    name)))
        {
            ApplyProfileSelection(target);
            try
            {
                await _tunnel.RestartAsync();
            }
            catch
            {
                // The routing is already saved; a failed restart is reported by the
                // tunnel itself, so there is nothing extra to surface here.
            }

            return;
        }

        // Declined: put the picker back where it was without touching the settings.
        _loading = true;
        try
        {
            SelectedProfile = Profiles.FirstOrDefault(p => string.Equals(p.Id, _appliedProfileId, StringComparison.Ordinal))
                              ?? Profiles.FirstOrDefault();
        }
        finally
        {
            _loading = false;
        }
    }

    // The profile that is actually in force, so a declined switch can restore the picker.
    private string _appliedProfileId = "";

    private void ApplyProfileSelection(SplitProfileItem target)
    {
        _suppressPersist = true;
        try
        {
            SplitTunnelProfileStore.SetActive(_settingsService.Settings, target.Id);
            _appliedProfileId = target.Id;
        }
        finally
        {
            _suppressPersist = false;
        }

        _settingsService.Save();
        ReloadActiveProfile();
        RefreshStatus();
    }

    public bool HasProfiles => Profiles.Count > 0;
    public bool HasNoProfiles => Profiles.Count == 0;
    public SplitTunnelProfile? ActiveProfile =>
        SelectedProfile is null ? null : FindProfile(SelectedProfile.Id);

    [ObservableProperty] private string _newProfileName = "";

    [ObservableProperty] private string _profileError = "";

    [RelayCommand]
    private void CreateProfile()
    {
        var name = (NewProfileName ?? "").Trim();
        if (name.Length == 0)
        {
            // Previously an empty box silently created a profile called "Profile",
            // which is the bug where a blank Enter kept adding profiles.
            ProfileError = Loc.Of("Enter a profile name first.");
            return;
        }

        if (_settingsService.Settings.SplitTunnelProfiles.Count >= SplitTunnelProfileStore.MaxProfiles)
        {
            ProfileError = string.Format(
                Loc.Of("Profile limit reached ({0})."),
                SplitTunnelProfileStore.MaxProfiles);
            return;
        }

        var created = SplitTunnelProfileStore.CreateProfile(_settingsService.Settings, name);
        if (created is null)
        {
            ProfileError = Loc.Of("Enter a profile name first.");
            return;
        }

        ProfileError = "";
        _settingsService.Save();
        NewProfileName = "";
        LoadProfiles(created.Id);
        RefreshStatus();
    }

    partial void OnNewProfileNameChanged(string value)
    {
        if (ProfileError.Length > 0) ProfileError = "";
    }

    [RelayCommand]
    private async Task DeleteProfile()
    {
        var current = ActiveProfile;
        if (current is null) return;

        if (!await Ui.ConfirmAsync(
                Loc.Of("Delete profile"),
                string.Format(Loc.Of("Delete “{0}” and its routing rules?"), current.Name)))
        {
            return;
        }

        SplitTunnelProfileStore.DeleteProfile(_settingsService.Settings, current.Id);
        _settingsService.Save();
        LoadProfiles();
        RefreshStatus();
    }

    [ObservableProperty] private bool _isRenamingProfile;
    [ObservableProperty] private string _renameText = "";

    [RelayCommand]
    private void BeginRename()
    {
        var current = ActiveProfile;
        if (current is null) return;
        RenameText = current.Name;
        IsRenamingProfile = true;
    }

    [RelayCommand]
    private void CommitRename()
    {
        var current = ActiveProfile;
        IsRenamingProfile = false;
        if (current is null) return;

        // A blank rename keeps the old name rather than renaming to "Profile".
        if ((RenameText ?? "").Trim().Length == 0)
        {
            RenameText = current.Name;
            return;
        }

        if (!SplitTunnelProfileStore.Rename(_settingsService.Settings, current, RenameText)) return;

        _settingsService.Save();
        LoadProfiles(current.Id);
    }

    [RelayCommand]
    private void CancelRename() => IsRenamingProfile = false;

    // ----------------------------------------------------------------- default outbound

    // Order requested for the profile's default outbound: Proxy, Direct, Block.
    public IReadOnlyList<SplitOutboundOption> DefaultOutbounds { get; } = new[]
    {
        new SplitOutboundOption(SplitOutbound.Proxy, "Proxy", "ShieldCheckOutline"),
        new SplitOutboundOption(SplitOutbound.Direct, "Direct", "Earth"),
        new SplitOutboundOption(SplitOutbound.Block, "Block", "BlockHelper"),
    };

    public string DefaultOutbound
    {
        get => ActiveProfile?.DefaultOutbound ?? SplitOutbound.Proxy;
        set
        {
            var current = ActiveProfile;
            if (current is null) return;
            var normalized = SplitOutbound.Normalize(value);
            if (string.Equals(current.DefaultOutbound, normalized, StringComparison.Ordinal)) return;

            current.DefaultOutbound = normalized;
            SplitTunnelProfileStore.Touch();
            Persist();
            OnPropertyChanged();
            OnPropertyChanged(nameof(DefaultOutboundLabel));
            OnPropertyChanged(nameof(DefaultOutboundText));
        }
    }

    public string DefaultOutboundLabel => DefaultOutbound switch
    {
        SplitOutbound.Direct => Loc.Of("Direct"),
        SplitOutbound.Block => Loc.Of("Block"),
        _ => Loc.Of("Proxy"),
    };

    // Kept short on purpose: the caption used to be "Everything else: Proxy" and wrapped
    // onto two lines at the window's minimum width.
    public string DefaultOutboundText =>
        string.Format(Loc.Of("Rest: {0}"), DefaultOutboundLabel);

    [RelayCommand]
    private void SetDefaultOutbound(string? key) => DefaultOutbound = key ?? SplitOutbound.Proxy;

    // ----------------------------------------------------------------- mode columns

    [ObservableProperty] private string _activeMode = SplitRouteAction.Bypass;

    public bool IsBypassMode => ActiveMode == SplitRouteAction.Bypass;
    public bool IsTunnelMode => ActiveMode == SplitRouteAction.Tunnel;
    public bool IsBlockMode => ActiveMode == SplitRouteAction.Block;

    [RelayCommand]
    private void SelectMode(string? mode)
    {
        ActiveMode = SplitTunnelProfileStore.NormalizeAction(mode);
        OnPropertyChanged(nameof(IsBypassMode));
        OnPropertyChanged(nameof(IsTunnelMode));
        OnPropertyChanged(nameof(IsBlockMode));
        RefreshColumns();
        OnPropertyChanged(nameof(ActiveModeEmpty));
    }

    public ObservableCollection<SplitRuleRow> BypassRows { get; } = new();
    public ObservableCollection<SplitRuleRow> TunnelRows { get; } = new();
    public ObservableCollection<SplitRuleRow> BlockRows { get; } = new();

    public int BypassCount => BypassRows.Count;
    public int TunnelCount => TunnelRows.Count;
    public int BlockCount => BlockRows.Count;

    public string BypassCountText => BypassRows.Count.ToString();
    public string TunnelCountText => TunnelRows.Count.ToString();
    public string BlockCountText => BlockRows.Count.ToString();

    public bool HasBypass => BypassRows.Count > 0;
    public bool HasTunnel => TunnelRows.Count > 0;
    public bool HasBlock => BlockRows.Count > 0;

    public bool ActiveModeEmpty => ActiveMode switch
    {
        SplitRouteAction.Bypass => !HasBypass,
        SplitRouteAction.Block => !HasBlock,
        _ => !HasTunnel,
    };

    public string ActiveModeHeader => ActiveMode switch
    {
        SplitRouteAction.Bypass => Loc.Of("Direct"),
        SplitRouteAction.Block => Loc.Of("Block"),
        _ => Loc.Of("Proxy"),
    };

    public string ActiveModeCaption => ActiveMode switch
    {
        SplitRouteAction.Bypass => Loc.Of("No tunnel"),
        SplitRouteAction.Block => Loc.Of("No network"),
        _ => Loc.Of("Via proxy"),
    };

    private void RefreshColumns()
    {
        OnPropertyChanged(nameof(BypassCount));
        OnPropertyChanged(nameof(TunnelCount));
        OnPropertyChanged(nameof(BlockCount));
        OnPropertyChanged(nameof(BypassCountText));
        OnPropertyChanged(nameof(TunnelCountText));
        OnPropertyChanged(nameof(BlockCountText));
        OnPropertyChanged(nameof(HasBypass));
        OnPropertyChanged(nameof(HasTunnel));
        OnPropertyChanged(nameof(HasBlock));
        OnPropertyChanged(nameof(ActiveModeEmpty));
        OnPropertyChanged(nameof(ActiveModeHeader));
        OnPropertyChanged(nameof(ActiveModeCaption));
        OnPropertyChanged(nameof(RuleCountText));
        OnPropertyChanged(nameof(StatusText));
    }

    public string RuleCountText
    {
        get
        {
            var total = BypassRows.Count + TunnelRows.Count + BlockRows.Count;
            return string.Format(Loc.Of("{0} rules"), total);
        }
    }

    // ----------------------------------------------------------------- applications

    private List<SplitAppOption> _allApps = new();
    private bool _appsLoaded;

    public ObservableCollection<SplitAppOption> FilteredApps { get; } = new();

    [ObservableProperty] private string _appSearch = "";

    partial void OnAppSearchChanged(string value) => ApplyAppFilter();

    [ObservableProperty] private bool _isScanningApps;

    [RelayCommand]
    private async Task RefreshApps()
    {
        InstalledAppsProvider.Invalidate();
        _appsLoaded = false;
        _allApps = new List<SplitAppOption>();
        await EnsureAppsLoadedAsync();
        ApplyAppFilter();
    }

    private async Task EnsureAppsLoadedAsync()
    {
        if (_appsLoaded) return;

        IsScanningApps = true;
        try
        {
            // Reuse the last scan while it is still fresh: opening the page twice must not
            // walk Program Files twice.
            var cached = InstalledAppsProvider.CachedApps;
            var installed = cached.Count > 0
                ? cached.ToList()
                : await InstalledAppsProvider.LoadAsync();
            var existing = ActiveProfile;
            var assigned = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (existing is not null)
            {
                foreach (var row in BuildRows(existing.Block, SplitRouteAction.Block)) assigned[row.Key] = SplitRouteAction.Block;
                foreach (var row in BuildRows(existing.Tunnel, SplitRouteAction.Tunnel)) assigned[row.Key] = SplitRouteAction.Tunnel;
                foreach (var row in BuildRows(existing.Bypass, SplitRouteAction.Bypass)) assigned[row.Key] = SplitRouteAction.Bypass;
            }

            _allApps = installed
                .Select(app => new SplitAppOption(app.Name, app.ExePath, app.FileName, app.Icon, app.IsRunning))
                .ToList();

            foreach (var app in _allApps)
            {
                if (assigned.TryGetValue("app\u0001" + app.ExePath, out var action)
                    || assigned.TryGetValue("process\u0001" + app.FileName, out action))
                {
                    app.Mode = action;
                }
            }

            _appsLoaded = true;
        }
        finally
        {
            IsScanningApps = false;
        }
    }

    private void ApplyAppFilter()
    {
        var needle = (AppSearch ?? "").Trim();
        var profile = ActiveProfile;
        var ownership = BuildOwnershipMap(profile);

        FilteredApps.Clear();

        if (needle.Length > 0)
        {
            // Rank matches so the obvious one is first: exact file name, then name
            // prefix, then substring. Someone typing "chrome" should see chrome.exe
            // at the top even on a machine with a hundred apps.
            var scored = new List<(SplitAppOption App, int Rank)>();
            foreach (var app in _allApps)
            {
                var rank = app.MatchRank(needle);
                if (rank < 0) continue;
                scored.Add((app, rank));
            }

            scored.Sort(static (a, b) =>
            {
                var c = a.Rank.CompareTo(b.Rank);
                return c != 0 ? c : string.Compare(a.App.Name, b.App.Name, StringComparison.CurrentCultureIgnoreCase);
            });

            foreach (var (app, _) in scored)
            {
                app.Mode = ownership.TryGetValue(app.ExePath, out var action) ? action : "";
                FilteredApps.Add(app);
                if (FilteredApps.Count >= 200) break;
            }
        }
        else
        {
            foreach (var app in _allApps)
            {
                app.Mode = ownership.TryGetValue(app.ExePath, out var action) ? action : "";
                FilteredApps.Add(app);
                if (FilteredApps.Count >= 200) break;
            }
        }

        OnPropertyChanged(nameof(NoFilteredApps));
        OnPropertyChanged(nameof(CanAddTypedApp));
        OnPropertyChanged(nameof(TypedAppPreview));
    }

    public bool NoFilteredApps => FilteredApps.Count == 0;

    // Adding an app the scan never saw (a portable exe, a game, a renamed binary) must
    // be possible without knowing the path: typing the name and confirming is enough.
    public bool CanAddTypedApp
    {
        get
        {
            var typed = NormalizeTypedApp();
            if (typed.Length == 0) return false;
            return !_allApps.Any(a =>
                string.Equals(a.FileName, typed, StringComparison.OrdinalIgnoreCase)
                || string.Equals(a.Name, typed.TrimEnd('.'), StringComparison.OrdinalIgnoreCase));
        }
    }

    public string TypedAppPreview
    {
        get
        {
            var typed = NormalizeTypedApp();
            return typed.Length == 0
                ? ""
                : string.Format(Loc.Of("Add “{0}” to {1}"), typed, ActiveModeHeader);
        }
    }

    private string NormalizeTypedApp()
    {
        var raw = (AppSearch ?? "").Trim().Trim('"');
        if (raw.Length < 2) return "";
        if (raw.Contains('\\') || raw.Contains('/')) return raw;
        return raw.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? raw : raw + ".exe";
    }

    [RelayCommand]
    private void AddTypedApp()
    {
        var profile = ActiveProfile;
        if (profile is null) return;

        var typed = NormalizeTypedApp();
        if (typed.Length == 0) return;

        // A typed path becomes a path rule (stable across renames); a typed bare name
        // becomes a name rule.
        var kind = typed.Contains('\\') || typed.Contains('/')
            ? SplitTargetKind.ProcessPath
            : SplitTargetKind.Process;

        SplitTunnelProfileStore.SetApplication(profile, kind, typed, ActiveMode, typed);
        Persist();
        AppSearch = "";
        ReloadActiveProfile();
        ApplyAppFilter();
    }

    private static Dictionary<string, string> BuildOwnershipMap(SplitTunnelProfile? profile)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (profile is null) return map;

        foreach (var rule in profile.Block)
        {
            if (rule.Kind == SplitTargetKind.App) map[rule.Value] = SplitRouteAction.Block;
        }
        foreach (var rule in profile.Tunnel)
        {
            if (rule.Kind == SplitTargetKind.App) map[rule.Value] = SplitRouteAction.Tunnel;
        }
        foreach (var rule in profile.Bypass)
        {
            if (rule.Kind == SplitTargetKind.App) map[rule.Value] = SplitRouteAction.Bypass;
        }
        return map;
    }

    [RelayCommand]
    private void AssignApp(SplitAppOption? app) => AssignAppToMode(app, ActiveMode);

    // Removal targets the app, not a rule row: the store finds it in whichever of the
    // three lists owns it, so no mode is ever assumed.
    // Removal targets the app, not a rule row: the store finds it in whichever of the
    // three lists owns it. The old code only ever asked the Bypass list, so an app in
    // Through Tunnel or Block could not be removed from the Applications pane at all.
    [RelayCommand]
    private void RemoveAppAssignment(SplitAppOption? app)
    {
        var profile = ActiveProfile;
        if (app is null || profile is null) return;

        var removed = SplitTunnelProfileStore.RemoveValue(profile, SplitTargetKind.App, app.ExePath)
                      | SplitTunnelProfileStore.RemoveValue(profile, SplitTargetKind.ProcessPath, app.ExePath)
                      | SplitTunnelProfileStore.RemoveValue(profile, SplitTargetKind.Process, app.FileName);

        if (!removed) return;

        app.Mode = "";
        Persist();
        ReloadActiveProfile();
        ApplyAppFilter();
    }

    [RelayCommand]
    private void AssignAppBypass(SplitAppOption? app) => AssignAppToMode(app, SplitRouteAction.Bypass);

    [RelayCommand]
    private void AssignAppTunnel(SplitAppOption? app) => AssignAppToMode(app, SplitRouteAction.Tunnel);

    [RelayCommand]
    private void AssignAppBlock(SplitAppOption? app) => AssignAppToMode(app, SplitRouteAction.Block);

    private void AssignAppToMode(SplitAppOption? app, string mode)
    {
        var profile = ActiveProfile;
        if (profile is null || app is null) return;

        // Path rules are stable across renames and survive a name-only match. Selecting an
        // app can only ever touch that one target: it is removed from whichever of the
        // three lists owns it and added to exactly one list.
        SplitTunnelProfileStore.SetApplication(profile, SplitTargetKind.App, app.ExePath, mode, app.Name);
        Persist();

        app.Mode = mode;
        ReloadActiveProfile();
        ApplyAppFilter();
    }

    // ----------------------------------------------------------------- custom rules

    [ObservableProperty] private bool _isCustomRuleOpen;
    [ObservableProperty] private string _customRuleValue = "";
    [ObservableProperty] private string _customRuleKind = SplitTargetKind.Process;
    [ObservableProperty] private string _customRuleError = "";

    [RelayCommand]
    private void OpenCustomRule()
    {
        CustomRuleValue = "";
        CustomRuleError = "";
        IsCustomRuleOpen = true;
    }

    [RelayCommand]
    private void CloseCustomRule() => IsCustomRuleOpen = false;

    [RelayCommand]
    private void AddCustomRule()
    {
        var profile = ActiveProfile;
        if (profile is null) return;

        var raw = (CustomRuleValue ?? "").Trim();
        if (raw.Length == 0)
        {
            CustomRuleError = Loc.Of("Enter a value first.");
            return;
        }

        // The selected matcher is used verbatim. Guessing between path and name here is
        // what silently turned a typed path into "path.exe".
        var kind = SplitTargetKind.Normalize(CustomRuleKind);
        if (kind == SplitTargetKind.App) kind = SplitTargetKind.Process;

        var normalizedValue = SplitTunnelProfileStore.NormalizeValue(kind, raw);
        if (normalizedValue.Length == 0)
        {
            CustomRuleError = CustomRuleKind switch
            {
                SplitTargetKind.Regex => Loc.Of("That regular expression is not valid."),
                SplitTargetKind.Ip => Loc.Of("Enter an address like 10.0.0.0/8."),
                SplitTargetKind.Domain => Loc.Of("Enter a domain like example.com."),
                SplitTargetKind.ProcessPath => Loc.Of("Enter a full image path."),
                _ => Loc.Of("That value is not valid."),
            };
            return;
        }

        var result = SplitTunnelProfileStore.SetApplication(profile, kind, normalizedValue, ActiveMode);
        Persist();
        CustomRuleError = result switch
        {
            SplitMoveResult.AlreadyThere => Loc.Of("Already in this mode."),
            SplitMoveResult.Full => Loc.Of("This profile is full."),
            _ => "",
        };
        if (CustomRuleError.Length == 0)
        {
            CustomRuleValue = "";
            IsCustomRuleOpen = false;
        }

        ReloadActiveProfile();
    }

    // One entry per matcher, in the order the UI shows them. Each carries its own
    // example so the placeholder always teaches the format for the selected kind.
    public IReadOnlyList<SplitRuleKindOption> RuleKinds { get; } = new[]
    {
        new SplitRuleKindOption(SplitTargetKind.ProcessPath, "Path", "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe"),
        new SplitRuleKindOption(SplitTargetKind.Process, "Process", "chrome.exe"),
        new SplitRuleKindOption(SplitTargetKind.Ip, "IP", "10.0.0.0/8"),
        new SplitRuleKindOption(SplitTargetKind.Keyword, "Keyword", "chrom"),
        new SplitRuleKindOption(SplitTargetKind.Regex, "Regex", "^chrome.*\\.exe$"),
        new SplitRuleKindOption(SplitTargetKind.Suffix, "Suffix", "rome"),
        new SplitRuleKindOption(SplitTargetKind.Domain, "Domain", "example.com"),
    };

    [RelayCommand]
    private void SelectRuleKind(string? kind)
    {
        CustomRuleKind = SplitTargetKind.Normalize(kind);
        if (CustomRuleKind == SplitTargetKind.App) CustomRuleKind = SplitTargetKind.Process;
        CustomRuleError = "";
        OnPropertyChanged(nameof(CustomRuleKindLabel));
        OnPropertyChanged(nameof(CustomRuleHint));
        OnPropertyChanged(nameof(CustomRuleHelp));
        OnPropertyChanged(nameof(IsProcessKind));
    }

    public bool IsProcessKind => SplitTargetKind.IsProcessKind(CustomRuleKind);

    public string CustomRuleKindLabel => RuleKinds
        .FirstOrDefault(k => k.Kind == CustomRuleKind)?.Label ?? Loc.Of("Process");

    // Placeholder: an example of the exact thing to type.
    public string CustomRuleHint => CustomRuleKind switch
    {
        SplitTargetKind.ProcessPath => "C:\\Program Files\\App\\app.exe",
        SplitTargetKind.Process => "chrome.exe",
        SplitTargetKind.Ip => "10.0.0.0/8",
        SplitTargetKind.Keyword => "chrom",
        SplitTargetKind.Regex => "^chrome.*\\.exe$",
        SplitTargetKind.Suffix => "rome",
        SplitTargetKind.Domain => "example.com",
        _ => "app.exe",
    };

    // One short line that says what the selected matcher does — no paragraphs.
    public string CustomRuleHelp => CustomRuleKind switch
    {
        SplitTargetKind.ProcessPath => Loc.Of("Exact image path"),
        SplitTargetKind.Process => Loc.Of("Image file name"),
        SplitTargetKind.Ip => Loc.Of("Address or range"),
        SplitTargetKind.Keyword => Loc.Of("Anywhere in the name"),
        SplitTargetKind.Regex => Loc.Of("Regular expression"),
        SplitTargetKind.Suffix => Loc.Of("Ends with"),
        SplitTargetKind.Domain => Loc.Of("Domain and subdomains"),
        _ => "",
    };

    // ----------------------------------------------------------------- rule rows

    [RelayCommand]
    private void RemoveRule(SplitRuleRow? row)
    {
        var profile = ActiveProfile;
        if (profile is null || row is null) return;

        // The row no longer has to be in the mode the UI happens to be showing.
        if (!SplitTunnelProfileStore.RemoveValue(profile, row.Kind, row.Value)) return;

        Persist();
        ReloadActiveProfile();
        ApplyAppFilter();
    }

    [RelayCommand]
    private void MoveRule(SplitRuleRow? row)
    {
        var profile = ActiveProfile;
        if (profile is null || row is null) return;

        var next = row.Action switch
        {
            SplitRouteAction.Bypass => SplitRouteAction.Tunnel,
            SplitRouteAction.Tunnel => SplitRouteAction.Block,
            _ => SplitRouteAction.Bypass,
        };

        var result = SplitTunnelProfileStore.SetApplication(profile, row.Kind, row.Value, next, row.DisplayName);
        if (result != SplitMoveResult.AlreadyThere)
        {
            Persist();
            ReloadActiveProfile();
        }
    }

    [RelayCommand]
    private async Task ClearMode()
    {
        var profile = ActiveProfile;
        if (profile is null) return;

        var rows = ActiveMode switch
        {
            SplitRouteAction.Bypass => profile.Bypass,
            SplitRouteAction.Block => profile.Block,
            _ => profile.Tunnel,
        };
        if (rows.Count == 0) return;

        if (!await Ui.ConfirmAsync(
                Loc.Of("Clear rules"),
                string.Format(Loc.Of("Remove all {0} rules from this mode?"), rows.Count)))
        {
            return;
        }

        // Only the active mode is cleared; the other two lists are untouched.
        SplitTunnelProfileStore.Clear(profile, ActiveMode);
        Persist();
        ReloadActiveProfile();
    }

    // ----------------------------------------------------------------- enable / system-wide

    [ObservableProperty] private bool _splitTunnelEnabled;
    partial void OnSplitTunnelEnabledChanged(bool value)
    {
        if (_suppressPersist) return;
        _settingsService.Settings.SplitTunnelEnabled = value;

        if (value && IsAdminElevated && !SystemWideEnabled)
        {
            _suppressTunSideEffects = true;
            try { SystemWideEnabled = true; }
            finally { _suppressTunSideEffects = false; }
            _settingsService.Settings.SystemWideTunneling = true;
        }

        _settingsService.Save();
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

    // ----------------------------------------------------------------- status

    public bool IsSplitActive =>
        SplitTunnelEnabled
        && SelectedProfile is not null
        && (BypassRows.Count + TunnelRows.Count + BlockRows.Count) > 0
        && SystemWideEnabled
        && _tun.State == TunState.Running;

    public bool ShowSystemWideWarning => SplitTunnelEnabled && !IsAdminElevated;

    public string StatusText
    {
        get
        {
            if (!IsAdminElevated)
                return Loc.Of("Administrator rights are required for system-wide split tunnelling.");

            if (!SplitTunnelEnabled)
                return Loc.Of("Split tunnelling is off.");

            if (SelectedProfile is null)
                return Loc.Of("Create a profile to start routing applications.");

            if (!SystemWideEnabled)
                return Loc.Of("Turn on system-wide tunnelling to apply these rules.");

            if ((BypassRows.Count + TunnelRows.Count + BlockRows.Count) == 0)
                return Loc.Of("No rules yet. Add an application to this profile.");

            return _tun.State switch
            {
                TunState.Starting => Loc.Of("Starting the adapter…"),
                TunState.Running => string.Format(Loc.Of("Active — profile “{0}”"), SelectedProfile.Name),
                TunState.Stopping => Loc.Of("Stopping the adapter…"),
                TunState.Error => _tun.LastError ?? Loc.Of("The adapter failed to start."),
                _ => _tunnel.State == ConnectionState.Connected
                    ? Loc.Of("Waiting for the adapter…")
                    : Loc.Of("Rules are saved — they apply once you connect."),
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
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusBrush));
        OnPropertyChanged(nameof(HasProfiles));
        OnPropertyChanged(nameof(HasNoProfiles));
        OnPropertyChanged(nameof(DefaultOutbound));
        OnPropertyChanged(nameof(DefaultOutboundLabel));
        OnPropertyChanged(nameof(DefaultOutboundText));
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

        var activeId = s.ActiveSplitTunnelProfileId;
        if (!string.Equals(SelectedProfile?.Id, activeId, StringComparison.Ordinal))
        {
            LoadProfiles(activeId);
        }

        RefreshStatus();
    });

    // ----------------------------------------------------------------- plumbing

    private void Persist()
    {
        _settingsService.Save();
        RefreshStatus();
    }

    private SplitTunnelProfile? FindProfile(string id)
    {
        foreach (var p in _settingsService.Settings.SplitTunnelProfiles)
        {
            if (p is not null && string.Equals(p.Id, id, StringComparison.Ordinal)) return p;
        }
        return null;
    }

    private void LoadProfiles(string? selectId = null)
    {
        _loading = true;
        try
        {
            var settings = _settingsService.Settings;
            Profiles.Clear();

            foreach (var profile in settings.SplitTunnelProfiles)
            {
                if (profile is null) continue;
                Profiles.Add(new SplitProfileItem(
                    profile.Id,
                    profile.Name,
                    profile.DefaultOutbound,
                    profile.RuleCount));
            }

            var targetId = selectId
                ?? settings.ActiveSplitTunnelProfileId
                ?? "";
            var selected = Profiles.FirstOrDefault(p => string.Equals(p.Id, targetId, StringComparison.Ordinal))
                           ?? Profiles.FirstOrDefault();

            SelectedProfile = selected;
            _appliedProfileId = selected?.Id ?? "";
            if (selected is not null
                && !string.Equals(settings.ActiveSplitTunnelProfileId, selected.Id, StringComparison.Ordinal))
            {
                SplitTunnelProfileStore.SetActive(settings, selected.Id);
            }
        }
        finally
        {
            _loading = false;
        }

        ReloadActiveProfile();
        RefreshStatus();
    }

    private void ReloadActiveProfile()
    {
        var profile = ActiveProfile;

        BypassRows.Clear();
        BlockRows.Clear();
        TunnelRows.Clear();

        if (profile is not null)
        {
            foreach (var row in BuildRows(profile.Block, SplitRouteAction.Block)) BlockRows.Add(row);
            foreach (var row in BuildRows(profile.Tunnel, SplitRouteAction.Tunnel)) TunnelRows.Add(row);
            foreach (var row in BuildRows(profile.Bypass, SplitRouteAction.Bypass)) BypassRows.Add(row);

            if (SelectedProfile is not null)
            {
                SelectedProfile.DefaultOutbound = profile.DefaultOutbound;
                SelectedProfile.RuleCount = profile.RuleCount;
                SelectedProfile.Name = profile.Name;
            }
        }

        OnPropertyChanged(nameof(DefaultOutbound));
        OnPropertyChanged(nameof(DefaultOutboundLabel));
        OnPropertyChanged(nameof(DefaultOutboundText));
        RefreshColumns();
    }

    private static IEnumerable<SplitRuleRow> BuildRows(
        IEnumerable<SplitTunnelRule> rules,
        string action)
    {
        foreach (var rule in rules)
        {
            if (rule is null) continue;
            yield return new SplitRuleRow(
                action,
                rule.Kind,
                rule.Value,
                string.IsNullOrWhiteSpace(rule.DisplayName)
                    ? DefaultLabel(rule.Kind, rule.Value)
                    : rule.DisplayName.Trim());
        }
    }

    private static string DefaultLabel(string kind, string value) => kind switch
    {
        SplitTargetKind.Process or SplitTargetKind.App => value,
        _ => value,
    };

    private static void Post(Action action) => Ui.Post(action);
}

public sealed partial class SplitProfileItem : ObservableObject
{
    public SplitProfileItem(string id, string name, string defaultOutbound, int ruleCount)
    {
        Id = id;
        _name = name;
        _defaultOutbound = defaultOutbound;
        _ruleCount = ruleCount;
    }

    public string Id { get; }

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _defaultOutbound;
    [ObservableProperty] private int _ruleCount;

    public string OutboundIcon => DefaultOutbound switch
    {
        SplitOutbound.Direct => "Earth",
        SplitOutbound.Block => "BlockHelper",
        _ => "ShieldCheckOutline",
    };

    public string OutboundLabel => DefaultOutbound switch
    {
        SplitOutbound.Direct => Loc.Of("Direct"),
        SplitOutbound.Block => Loc.Of("Block"),
        _ => Loc.Of("Proxy"),
    };

    partial void OnDefaultOutboundChanged(string value)
    {
        OnPropertyChanged(nameof(OutboundIcon));
        OnPropertyChanged(nameof(OutboundLabel));
    }

    public string CountLabel => RuleCount.ToString();
}

public sealed partial class SplitAppOption : ObservableObject
{
    public SplitAppOption(string name, string exePath, string fileName, object? icon, bool isRunning)
    {
        Name = name;
        ExePath = exePath;
        FileName = fileName;
        Icon = icon;
        IsRunning = isRunning;
    }

    public string Name { get; }
    public string ExePath { get; }
    public string FileName { get; }
    public object? Icon { get; }
    public bool IsRunning { get; }

    public string SearchText => $"{Name}\n{FileName}\n{ExePath}";

    // Lower is better; -1 means "no match". Exact file name wins, then the file name
    // starts with the text, then the display name starts with it, then anywhere.
    public int MatchRank(string needle)
    {
        if (string.IsNullOrEmpty(needle)) return 0;

        var withExe = needle.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? needle
            : needle + ".exe";

        if (string.Equals(FileName, withExe, StringComparison.OrdinalIgnoreCase)
            || string.Equals(FileName, needle, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (FileName?.StartsWith(needle, StringComparison.OrdinalIgnoreCase) == true) return 1;
        if (Name?.StartsWith(needle, StringComparison.OrdinalIgnoreCase) == true) return 2;

        if (SearchText.IndexOf(needle, StringComparison.CurrentCultureIgnoreCase) >= 0) return 3;
        return -1;
    }

    [ObservableProperty] private string _mode = "";

    partial void OnModeChanged(string value)
    {
        OnPropertyChanged(nameof(Assigned));
        OnPropertyChanged(nameof(ModeLabel));
        OnPropertyChanged(nameof(ModeBrush));
    }

    public bool Assigned => Mode.Length > 0;

    public string ModeLabel => Mode switch
    {
        SplitRouteAction.Bypass => Loc.Of("Direct"),
        SplitRouteAction.Block => Loc.Of("Block"),
        SplitRouteAction.Tunnel => Loc.Of("Proxy"),
        _ => Loc.Of("Add"),
    };

    public object ModeBrush => Mode switch
    {
        SplitRouteAction.Bypass => BypassBrush,
        SplitRouteAction.Block => BlockBrush,
        SplitRouteAction.Tunnel => TunnelBrush,
        _ => IdleBrush,
    };

    private static readonly object BypassBrush = Services.Ui.FrozenBrush("#F59E0B");
    private static readonly object BlockBrush = Services.Ui.FrozenBrush("#EF4444");
    private static readonly object TunnelBrush = Services.Ui.FrozenBrush("#00D4FF");
    private static readonly object IdleBrush = Services.Ui.FrozenBrush("#6B7280");
}

public sealed record SplitRuleRow(
    string Action,
    string Kind,
    string Value,
    string Label)
{
    public string Key => $"{Kind}\u0001{Value}";

    public string DisplayName => Label;

    public string Icon => Kind switch
    {
        SplitTargetKind.Domain => "Web",
        SplitTargetKind.Ip => "IpNetwork",
        _ => "Application",
    };

    public object ActionBrush => Action switch
    {
        SplitRouteAction.Bypass => BypassBrush,
        SplitRouteAction.Block => BlockBrush,
        _ => TunnelBrush,
    };

    private static readonly object BypassBrush = Services.Ui.FrozenBrush("#F59E0B");
    private static readonly object BlockBrush = Services.Ui.FrozenBrush("#EF4444");
    private static readonly object TunnelBrush = Services.Ui.FrozenBrush("#00D4FF");
}

public sealed record SplitOutboundOption(string Key, string English, string Icon) : LocalizedItem
{
    public string Label => Loc.Of(English);
}

public sealed record SplitRuleKindOption(string Kind, string Label, string Example)
{
    public string LocalLabel => Loc.Of(Label);
}
