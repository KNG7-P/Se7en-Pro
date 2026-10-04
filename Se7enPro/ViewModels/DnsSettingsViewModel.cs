using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Se7enPro.Models;
using Se7enPro.Services;

namespace Se7enPro.ViewModels;

/// <summary>
/// ViewModel for custom DNS settings configuration.
/// </summary>
public sealed partial class DnsSettingsViewModel : ObservableObject
{
    private static readonly TimeSpan SaveDebounce = TimeSpan.FromMilliseconds(650);

    /// <summary>How long the "Saved" confirmation stays up.</summary>
    private static readonly TimeSpan SaveFlashDuration = TimeSpan.FromSeconds(2.5);

    private readonly ISettingsService _settings;

    /// <summary>Set while loading, so a programmatic update is not treated as an edit.</summary>
    private bool _loading;

    private CancellationTokenSource? _saveDebounce;
    private CancellationTokenSource? _flashCancel;

    public DnsSettingsViewModel(ISettingsService settings)
    {
        _settings = settings;

        // One template drives all three, so the transports cannot drift apart visually.
        Rows = new List<DnsFieldRow>
        {
            new(DnsTransport.Udp, Labels.UdpLabel, Labels.UdpHint, Labels.UdpPlaceholder,
                OnRowEdited),
            new(DnsTransport.Dot, Labels.DotLabel, Labels.DotHint, Labels.DotPlaceholder,
                OnRowEdited),
            new(DnsTransport.Doh, Labels.DohLabel, Labels.DohHint, Labels.DohPlaceholder,
                OnRowEdited),
        };

        Load();
    }

    // ---------------------------------------------------------------- state

    /// <summary>The three resolver lists, in display order.</summary>
    public IReadOnlyList<DnsFieldRow> Rows { get; }

    public DnsFieldRow UdpRow => Rows[0];
    public DnsFieldRow DotRow => Rows[1];
    public DnsFieldRow DohRow => Rows[2];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTunnelWarning))]
    private bool _strictMode;

    /// <summary>Compact count for the header badge, e.g. "2 UDP, 1 DoH".</summary>
    [ObservableProperty] private string _summary = "";

    /// <summary>True while the confirmation is on screen.</summary>
    [ObservableProperty] private bool _isSavedFlash;

    [ObservableProperty] private bool _isTesting;
    [ObservableProperty] private string _testResult = "";
    [ObservableProperty] private bool _hasTestResult;
    [ObservableProperty] private string _validationMessage = "";
    [ObservableProperty] private bool _hasValidationError;

    /// <summary>Translated UI strings for this card.</summary>
    public DnsLabels Labels { get; } = new();

    public bool HasAnyResolver => UdpRow.HasEntries || DotRow.HasEntries || DohRow.HasEntries;
    public bool CanTest => !IsTesting;
    public bool HasValidationMessage => ValidationMessage.Length > 0;
    public bool HasTestResultToShow => HasTestResult && TestResult.Length > 0;

    /// <summary>
    /// Indicates whether strict mode is enabled without plain UDP resolvers configured.
    /// </summary>
    public bool HasTunnelWarning =>
        StrictMode
        && !UdpRow.HasEntries
        && (DotRow.HasEntries || DohRow.HasEntries);

    // ---------------------------------------------------------------- commands

    /// <summary>
    /// Probes configured resolvers and reports connectivity status.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanTest))]
    private async Task TestResolversAsync()
    {
        if (IsTesting) return;

        foreach (var row in Rows)
        {
            if (row.HasProblem)
            {
                ValidationMessage = $"{row.Label}: {row.Problem}";
                HasValidationError = true;
                return;
            }
        }

        var targets = Rows.SelectMany(r => r.Entries).ToList();
        if (targets.Count == 0)
        {
            ValidationMessage = Labels.EnterOneFirst;
            HasValidationError = true;
            return;
        }

        IsTesting = true;
        TestResult = Labels.Testing;
        HasTestResult = true;
        TestResolversCommand.NotifyCanExecuteChanged();

        try
        {
            using var cts = new CancellationTokenSource(
                TimeSpan.FromSeconds(ProbeTimeoutSeconds * targets.Count + 5));
            var prober = new DnsProber();

            var results = new List<DnsProbeResult>(targets.Count);
            foreach (var t in targets)
            {
                if (cts.IsCancellationRequested) break;
                results.Add(await prober.ProbeAsync(t, cts.Token));
            }

            var ok = results.Count(r => r.Reachable);
            var detail = string.Join("  ·  ", results.Select(r => r.ToString()));

            TestResult = results.Count == 0
                ? Labels.EnterOneFirst
                : $"{ok} of {results.Count} answered";
            if (detail.Length > 0) TestResult += " — " + detail;

            ValidationMessage = results.Count > 0 && ok == 0 ? Labels.NoneAnswered : "";
            HasValidationError = ValidationMessage.Length > 0;
        }
        finally
        {
            IsTesting = false;
            TestResolversCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>
    /// Clears all resolver fields and resets strict mode.
    /// </summary>
    [RelayCommand]
    private void Reset()
    {
        foreach (var row in Rows) row.Text = "";
        StrictMode = false;
        Commit(immediate: true);
    }

    private const int ProbeTimeoutSeconds = 8;

    // ---------------------------------------------------------------- persistence

    private void OnRowEdited(DnsFieldRow row) => ScheduleSave();

    private void ScheduleSave()
    {
        if (_loading) return;

        _saveDebounce?.Cancel();
        _saveDebounce?.Dispose();
        _saveDebounce = new CancellationTokenSource();

        var token = _saveDebounce.Token;
        _ = SaveAfterDelayAsync(token);
    }

    private async Task SaveAfterDelayAsync(CancellationToken token)
    {
        try { await Task.Delay(SaveDebounce, token); }
        catch (OperationCanceledException) { return; }

        if (token.IsCancellationRequested) return;

        Commit(immediate: true);
    }

    /// <summary>
    /// Persists configuration if changes are detected.
    /// </summary>
    private void Commit(bool immediate)
    {
        var s = _settings.Settings;
        var udp = UdpRow.Text.Trim();
        var dot = DotRow.Text.Trim();
        var doh = DohRow.Text.Trim();

        var unchanged =
            string.Equals(s.CustomDnsUdp ?? "", udp, StringComparison.Ordinal)
            && string.Equals(s.CustomDnsDot ?? "", dot, StringComparison.Ordinal)
            && string.Equals(s.CustomDnsDoh ?? "", doh, StringComparison.Ordinal)
            && s.CustomDnsStrict == StrictMode;

        RefreshSummary();
        if (unchanged)
        {
            if (immediate) IsSavedFlash = false;
            return;
        }

        s.CustomDnsUdp = udp;
        s.CustomDnsDot = dot;
        s.CustomDnsDoh = doh;
        s.CustomDnsStrict = StrictMode;
        _settings.Save();

        FlashSaved();
    }

    private void FlashSaved()
    {
        _flashCancel?.Cancel();
        _flashCancel?.Dispose();
        _flashCancel = new CancellationTokenSource();

        IsSavedFlash = true;

        var token = _flashCancel.Token;
        _ = ClearFlashAsync(token);

        async Task ClearFlashAsync(CancellationToken t)
        {
            try { await Task.Delay(SaveFlashDuration, t); }
            catch (OperationCanceledException) { return; }
            if (!t.IsCancellationRequested) IsSavedFlash = false;
        }
    }

    /// <summary>Re-reads from settings. Used at startup and after a factory reset.</summary>
    public void Load()
    {
        _loading = true;
        try
        {
            var s = _settings.Settings;
            UdpRow.Text = s.CustomDnsUdp ?? "";
            DotRow.Text = s.CustomDnsDot ?? "";
            DohRow.Text = s.CustomDnsDoh ?? "";
            StrictMode = s.CustomDnsStrict;
        }
        finally
        {
            _loading = false;
        }

        _saveDebounce?.Cancel();
        IsSavedFlash = false;

        foreach (var row in Rows) row.Revalidate();
        RefreshSummary();
    }

    private void RefreshSummary()
    {
        Summary = DnsSettings.Summarise(UdpRow.Text, DotRow.Text, DohRow.Text);
        if (Summary.Length == 0) Summary = Labels.Automatic;
        OnPropertyChanged(nameof(HasAnyResolver));
    }

    partial void OnStrictModeChanged(bool value)
    {
        OnPropertyChanged(nameof(HasTunnelWarning));
        if (!_loading) Commit(immediate: true);
    }

    partial void OnValidationMessageChanged(string value) =>
        OnPropertyChanged(nameof(HasValidationMessage));

    partial void OnTestResultChanged(string value) =>
        OnPropertyChanged(nameof(HasTestResultToShow));

    partial void OnIsTestingChanged(bool value) => OnPropertyChanged(nameof(CanTest));
}