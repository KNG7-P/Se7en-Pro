using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Se7enPro.Models;
using Se7enPro.Services;

namespace Se7enPro.ViewModels;

public sealed record SplitKindOption(string Key, string Display);

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
        _systemWideEnabled = s.SystemWideTunneling;
        LoadSplitTunnelEntries();

        _settingsService.SettingsChanged += OnSettingsServiceChanged;
        _tun.StateChanged += OnTunStateChanged;
        _tunnel.StateChanged += OnTunnelStateChanged;
    }

    public ObservableCollection<SplitTunnelEntryView> SplitTunnelEntries { get; } = new();

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
        ? "Only the sites, IPs and apps listed below reach the internet through the VPN. Everything else uses your real connection."
        : "The sites, IPs and apps listed below use your real connection. Everything else is tunnelled through the VPN.";

    public string ModeSummary
    {
        get
        {
            var n = SplitTunnelEntries.Count;
            if (n == 0)
            {
                return "Right now: the list is empty, so every connection goes through the VPN. "
                     + "Add a rule below to split it.";
            }

            return IsIncludeMode
                ? $"Right now: only these {n} rule(s) go through the VPN. Everything else — the entire "
                  + "rest of the internet — uses your normal connection and is NOT protected."
                : $"Right now: these {n} rule(s) use your normal connection, with your real local IP. "
                  + "Everything else goes through the VPN.";
        }
    }

    public bool ShowIncludeWarning => SplitTunnelEnabled && IsIncludeMode;

    private void RefreshModeSurface()
    {
        OnPropertyChanged(nameof(IsIncludeMode));
        OnPropertyChanged(nameof(SplitTunnelModeHint));
        OnPropertyChanged(nameof(ModeSummary));
        OnPropertyChanged(nameof(ShowIncludeWarning));

        var include = IsIncludeMode;
        foreach (var entry in SplitTunnelEntries) entry.IncludeMode = include;
    }

    [ObservableProperty] private string _newSplitKind = "domain";

    [ObservableProperty] private string _newSplitValue = "";
    partial void OnNewSplitValueChanged(string value)
    {
        if (!string.IsNullOrEmpty(SplitTunnelError)) SplitTunnelError = "";
    }

    [ObservableProperty] private string _splitTunnelError = "";

    public bool HasSplitTunnelEntries => SplitTunnelEntries.Count > 0;
    public bool NoSplitTunnelEntries => SplitTunnelEntries.Count == 0;

    private void LoadSplitTunnelEntries()
    {
        SplitTunnelEntries.Clear();
        var stored = _settingsService.Settings.SplitTunnelEntries;
        var include = IsIncludeMode;
        if (stored is not null)
        {
            foreach (var e in stored)
            {
                if (e is null || string.IsNullOrWhiteSpace(e.Value)) continue;
                var kind = (e.Kind ?? "domain").Trim().ToLowerInvariant();
                if (kind != "ip" && kind != "app") kind = "domain";
                SplitTunnelEntries.Add(new SplitTunnelEntryView
                {
                    Kind = kind,
                    Value = e.Value.Trim(),
                    IncludeMode = include,
                });
            }
        }

        OnPropertyChanged(nameof(HasSplitTunnelEntries));
        OnPropertyChanged(nameof(NoSplitTunnelEntries));
        OnPropertyChanged(nameof(ModeSummary));
    }

    private SplitTunnelEntryView NewRow(string kind, string value) =>
        new() { Kind = kind, Value = value, IncludeMode = IsIncludeMode };

    [RelayCommand]
    private void AddSplitEntry()
    {
        var kind = (NewSplitKind ?? "domain").Trim().ToLowerInvariant();
        if (kind != "ip" && kind != "app") kind = "domain";

        var value = (NewSplitValue ?? "").Trim();
        if (value.Length == 0)
        {
            SplitTunnelError = "Enter a value first.";
            return;
        }

        var normalized = Normalize(kind, value);
        if (string.IsNullOrEmpty(normalized))
        {
            SplitTunnelError = kind switch
            {
                "ip" => "Not a valid IP or CIDR — e.g. 1.2.3.4 or 10.0.0.0/8.",
                "app" => "Pick an app with the button above, or type its name (chrome.exe).",
                _ => "Enter a valid site / domain — e.g. example.com.",
            };
            return;
        }

        if (Contains(kind, normalized!))
        {
            NewSplitValue = "";
            return;
        }

        SplitTunnelEntries.Add(NewRow(kind, normalized!));
        NewSplitValue = "";
        SplitTunnelError = "";
        PersistSplitTunnelEntries();
    }

    [RelayCommand]
    private void RemoveSplitEntry(SplitTunnelEntryView? entry)
    {
        if (entry is null) return;
        SplitTunnelEntries.Remove(entry);
        PersistSplitTunnelEntries();
    }

    [RelayCommand]
    private void ClearSplitEntries()
    {
        if (SplitTunnelEntries.Count == 0) return;

        var answer = MessageBox.Show(
            $"Remove all {SplitTunnelEntries.Count} split-tunnel entries?",
            "Clear the list",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        SplitTunnelEntries.Clear();
        PersistSplitTunnelEntries();
    }

    [RelayCommand]
    private void PickApps()
    {
        var dialog = new Views.AppPickerDialog();

        var owner = Application.Current?.MainWindow;
        if (owner is not null && owner.IsVisible && !ReferenceEquals(owner, dialog))
        {
            dialog.Owner = owner;
        }

        var accepted = dialog.ShowDialog() == true;
        if (!accepted || dialog.SelectedValues.Count == 0) return;

        var added = 0;
        foreach (var raw in dialog.SelectedValues)
        {
            var normalized = Normalize("app", raw);
            if (string.IsNullOrEmpty(normalized)) continue;
            if (Contains("app", normalized!)) continue;

            SplitTunnelEntries.Add(NewRow("app", normalized!));
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

            SplitTunnelEntries.Add(NewRow("domain", normalized!));
            added++;
        }

        SplitTunnelError = added > 0
            ? ""
            : "Those sites are already in the list.";
        if (added > 0) PersistSplitTunnelEntries();
    }

    private bool Contains(string kind, string value)
    {
        foreach (var existing in SplitTunnelEntries)
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
        var list = new List<SplitTunnelEntry>(SplitTunnelEntries.Count);
        foreach (var v in SplitTunnelEntries)
        {
            list.Add(new SplitTunnelEntry { Kind = v.Kind, Value = v.Value });
        }
        _settingsService.Settings.SplitTunnelEntries = list;
        _settingsService.Save();
        OnPropertyChanged(nameof(HasSplitTunnelEntries));
        OnPropertyChanged(nameof(NoSplitTunnelEntries));
        OnPropertyChanged(nameof(ModeSummary));
        RefreshStatus();
    }

    public bool IsAdminElevated { get; } = AdminElevation.IsAdministrator();

    [ObservableProperty] private bool _systemWideEnabled;
    partial void OnSystemWideEnabledChanged(bool value)
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
                _settingsService.Settings.SystemWideTunneling = true;
                _settingsService.Save();
                if (AdminElevation.TryRestartElevated())
                {
                    return;
                }
            }

            _suppressTunSideEffects = true;
            try { SystemWideEnabled = false; }
            finally { _suppressTunSideEffects = false; }
            return;
        }

        _settingsService.Settings.SystemWideTunneling = value;
        _settingsService.Save();
        RefreshStatus();
    }

    [RelayCommand]
    private void RestartAsAdmin()
    {
        _settingsService.Settings.SystemWideTunneling = true;
        _settingsService.Save();
        AdminElevation.TryRestartElevated();
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
                return "Run Se7en Pro as Administrator — split tunnelling needs the system-wide adapter.";

            if (!SplitTunnelEnabled)
                return "Split tunnelling is off. Everything follows the normal connection.";

            if (!SystemWideEnabled)
                return "Turn on system-wide tunnelling — these rules only apply in TUN mode.";

            if (SplitTunnelEntries.Count == 0)
                return "No rules yet. Add a site, an IP range, or pick an app below.";

            return _tun.State switch
            {
                TunState.Starting => "Starting the system-wide adapter…",
                TunState.Running =>
                    string.Equals(SplitTunnelMode, "include", StringComparison.OrdinalIgnoreCase)
                        ? $"Active — only the {SplitTunnelEntries.Count} listed rule(s) go through the VPN."
                        : $"Active — the {SplitTunnelEntries.Count} listed rule(s) bypass the VPN.",
                TunState.Stopping => "Stopping the system-wide adapter…",
                TunState.Error => _tun.LastError ?? "The system-wide adapter failed to start.",
                _ => _tunnel.State == ConnectionState.Connected
                    ? "Waiting for the system-wide adapter to come up…"
                    : "Rules are saved — they take effect once you connect.",
            };
        }
    }

    private static readonly SolidColorBrush BrushActive = MakeFrozen("#22C55E");
    private static readonly SolidColorBrush BrushPending = MakeFrozen("#F59E0B");
    private static readonly SolidColorBrush BrushError = MakeFrozen("#EF4444");
    private static readonly SolidColorBrush BrushIdle = MakeFrozen("#6B7280");

    private static SolidColorBrush MakeFrozen(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        b.Freeze();
        return b;
    }

    public Brush StatusBrush
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

    private static void Post(Action action)
    {
        if (Application.Current is { } app && !app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.BeginInvoke(action);
            return;
        }
        action();
    }
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

    public string DirectionLabel => IncludeMode ? "through VPN" : "bypasses VPN";

    public string DirectionIcon => IncludeMode ? "ShieldCheckOutline" : "ShieldOffOutline";

    public Brush DirectionBrush => IncludeMode ? DirectionVpnBrush : DirectionDirectBrush;

    private static readonly SolidColorBrush DirectionVpnBrush = Frozen("#38BDF8");
    private static readonly SolidColorBrush DirectionDirectBrush = Frozen("#F59E0B");

    private static SolidColorBrush Frozen(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        b.Freeze();
        return b;
    }

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
