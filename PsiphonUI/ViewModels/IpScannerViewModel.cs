using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PsiphonUI.Services;

namespace PsiphonUI.ViewModels;

public sealed partial class IpScannerViewModel : PageViewModelBase
{
    private readonly IIpHealthChecker _healthChecker;
    private readonly ISettingsService _settingsService;
    private readonly INavigationService _navigationService;

    private readonly IServiceProvider _serviceProvider;

    private CancellationTokenSource? _runCts;

    private readonly ConcurrentQueue<(IpRow Row, IpHealthResult Result)> _resultQueue = new();
    private int _flushScheduled;
    private const int FlushIntervalMs = 80;

    private readonly ManualResetEventSlim _pauseGate = new(initialState: true);

    public override string Title => "IP Scanner";
    public override string Route => "ipscanner";
    public override string Icon => "Radar";

    public IpScannerViewModel(
        IIpHealthChecker healthChecker,
        ISettingsService settingsService,
        INavigationService navigationService,
        IServiceProvider serviceProvider)
    {
        _healthChecker = healthChecker;
        _settingsService = settingsService;
        _navigationService = navigationService;
        _serviceProvider = serviceProvider;

        Presets = new ObservableCollection<IpScannerPresets.Preset>(IpScannerPresets.All);
        _selectedPreset = Presets[0];

        AvailableRanges = new ObservableCollection<RangeRow>();
        AvailableSnis = new ObservableCollection<SniRow>();
        RefreshAvailableRanges(_selectedPreset);
        RefreshAvailableSnis(_selectedPreset);

        var rangesView = CollectionViewSource.GetDefaultView(AvailableRanges);
        if (rangesView != null) rangesView.Filter = RangePassesFilter;

        CheckMethods = new ObservableCollection<string> { "Ping (ICMP)", "TLS + SNI" };
        _selectedCheckMethod = "Ping (ICMP)";

        if (_selectedPreset.Snis.Count > 0)
            _sniHost = _selectedPreset.Snis[0];

        Candidates = new BulkObservableCollection<IpRow>();
        Healthy = new BulkObservableCollection<IpRow>();

        WeakRangeRowEvents.Changed += row =>
        {
            if (AvailableRanges.Contains(row)) NotifyRangeSelectionChanged();
        };
        WeakSniRowEvents.Changed += row =>
        {
            if (AvailableSnis.Contains(row)) NotifySniSelectionChanged(row);
        };
    }

    public ObservableCollection<IpScannerPresets.Preset> Presets { get; }
    public ObservableCollection<string> CheckMethods { get; }

    [ObservableProperty] private IpScannerPresets.Preset _selectedPreset;
    partial void OnSelectedPresetChanged(IpScannerPresets.Preset value)
    {
        if (value is null) return;
        RefreshAvailableRanges(value);
        RefreshAvailableSnis(value);

        SniHost = value.Snis.Count > 0 ? value.Snis[0] : "";
    }

    [ObservableProperty] private string _selectedCheckMethod = "Ping (ICMP)";
    partial void OnSelectedCheckMethodChanged(string value)
    {
        OnPropertyChanged(nameof(IsTlsMode));

        if (IsTlsMode && string.IsNullOrWhiteSpace(SniHost) && _selectedPreset.Snis.Count > 0)
        {
            SniHost = _selectedPreset.Snis[0];
        }
    }

    public bool IsTlsMode => SelectedCheckMethod == "TLS + SNI";

    [ObservableProperty] private string _sniHost = "";

    [ObservableProperty] private int _concurrency = 25;
    [ObservableProperty] private int _timeoutMs = 2500;

    [ObservableProperty] private string _customInput = "";

    public ObservableCollection<RangeRow> AvailableRanges { get; }

    [ObservableProperty] private string _rangesFilter = "";

    partial void OnRangesFilterChanged(string value)
    {

        var view = CollectionViewSource.GetDefaultView(AvailableRanges);
        if (view != null) view.Refresh();
    }

    private bool RangePassesFilter(object item)
    {
        if (string.IsNullOrWhiteSpace(RangesFilter)) return true;
        if (item is not RangeRow row) return false;
        var needle = RangesFilter.Trim();
        return row.Cidr.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || row.Label.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    public ObservableCollection<SniRow> AvailableSnis { get; }

    private void RefreshAvailableRanges(IpScannerPresets.Preset preset)
    {
        AvailableRanges.Clear();
        foreach (var r in preset.Ranges)
        {
            AvailableRanges.Add(new RangeRow
            {
                Cidr = r.Cidr,
                Label = r.Label,

                IsSelected = r.DefaultSelected,
            });
        }
        OnPropertyChanged(nameof(AvailableRangesSummary));
    }

    private void RefreshAvailableSnis(IpScannerPresets.Preset preset)
    {
        AvailableSnis.Clear();
        for (var i = 0; i < preset.Snis.Count; i++)
        {
            AvailableSnis.Add(new SniRow
            {
                Hostname = preset.Snis[i],
                IsSelected = i == 0,
            });
        }
    }

    public string AvailableRangesSummary
    {
        get
        {
            var sel = AvailableRanges.Count(r => r.IsSelected);
            return $"{sel} / {AvailableRanges.Count} ranges selected";
        }
    }

    [RelayCommand]
    private void SelectAllRanges()
    {

        foreach (var r in VisibleRanges()) r.IsSelected = true;
        OnPropertyChanged(nameof(AvailableRangesSummary));
    }

    [RelayCommand]
    private void ClearRangeSelection()
    {

        foreach (var r in VisibleRanges()) r.IsSelected = false;
        OnPropertyChanged(nameof(AvailableRangesSummary));
    }

    private IEnumerable<RangeRow> VisibleRanges()
    {
        foreach (var r in AvailableRanges)
            if (RangePassesFilter(r)) yield return r;
    }

    internal void NotifyRangeSelectionChanged()
    => OnPropertyChanged(nameof(AvailableRangesSummary));

    internal void NotifySniSelectionChanged(SniRow changed)
    {
        if (!changed.IsSelected) return;
        SniHost = changed.Hostname;
        foreach (var s in AvailableSnis)
        {
            if (!ReferenceEquals(s, changed) && s.IsSelected) s.IsSelected = false;
        }
    }

    public BulkObservableCollection<IpRow> Candidates { get; }
    public BulkObservableCollection<IpRow> Healthy { get; }

    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _scannedCount;
    [ObservableProperty] private int _healthyCount;
    [ObservableProperty] private int _failedCount;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private string _statusText = "Idle";

    public bool IsRunningAndNotPaused => IsRunning && !IsPaused;

    partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(IsRunningAndNotPaused));
    partial void OnIsPausedChanged(bool value) => OnPropertyChanged(nameof(IsRunningAndNotPaused));

    [RelayCommand]
    private async Task ImportSelectedRangesAsync()
    {
        var picked = AvailableRanges.Where(r => r.IsSelected).Select(r => r.Cidr).ToList();
        var hasTextInput = !string.IsNullOrWhiteSpace(CustomInput);
        if (picked.Count == 0 && !hasTextInput)
        {
            StatusText = "Tick at least one range or paste IPs, then press Generate.";
            return;
        }

        var sb = new StringBuilder();
        foreach (var p in picked) { sb.Append(p); sb.Append('\n'); }
        if (hasTextInput) sb.Append(CustomInput);
        var combined = sb.ToString();

        StatusText = "Expanding ranges...";
        var r = await Task.Run(() => IpRangeParser.ExpandWithDiagnostics(combined)).ConfigureAwait(true);
        if (r.Ips.Count == 0)
        {
            StatusText = "Nothing valid to scan. " + DiagnosticSuffix(r);
            return;
        }

        var rows = await Task.Run(() =>
        {
            var list = new List<IpRow>(r.Ips.Count);
            foreach (var ip in r.Ips) list.Add(new IpRow { Ip = ip, Status = IpRowStatus.Pending });
            return list;
        }).ConfigureAwait(true);

        if (r.Ips.Count <= LargeListThreshold)
        {
            CustomInput = string.Join('\n', r.Ips);
        }
        Healthy.ResetWith(System.Array.Empty<IpRow>());
        ResetCounters();
        Candidates.ResetWith(rows);
        TotalCount = Candidates.Count;
        StatusText = $"Generated {Candidates.Count:N0} IPs from {picked.Count} range(s) + textbox. Press Start to scan. {DiagnosticSuffix(r)}".TrimEnd();
    }

    [RelayCommand]
    private void ClearCustomInput()
    {
        CustomInput = "";
        Candidates.Clear();
        Healthy.Clear();
        ResetCounters();
        TotalCount = 0;
        StatusText = "Cleared. Tick ranges and press Generate, or paste IPs and press Start.";
    }

    private const int LargeListThreshold = 5000;

    [RelayCommand]
    private async Task GenerateIpsAsync()
    {
        var input = CustomInput ?? "";
        StatusText = "Expanding ranges...";
        var r = await Task.Run(() => IpRangeParser.ExpandWithDiagnostics(input)).ConfigureAwait(true);
        if (r.Ips.Count == 0)
        {
            StatusText = "No valid IPs / ranges in input. " + DiagnosticSuffix(r);
            return;
        }

        var rows = await Task.Run(() =>
        {
            var list = new List<IpRow>(r.Ips.Count);
            foreach (var ip in r.Ips) list.Add(new IpRow { Ip = ip, Status = IpRowStatus.Pending });
            return list;
        }).ConfigureAwait(true);

        Healthy.ResetWith(System.Array.Empty<IpRow>());
        ResetCounters();
        Candidates.ResetWith(rows);
        TotalCount = Candidates.Count;
        StatusText = $"Generated {TotalCount:N0} IPs. Press Start to scan. {DiagnosticSuffix(r)}".TrimEnd();
    }

    private static string DiagnosticSuffix(IpRangeParser.ExpansionResult r)
    {
        if (r.Warnings.Count == 0 && !r.HitCap) return "";
        var sb = new StringBuilder();
        if (r.HitCap) sb.Append($"Hit the {IpRangeParser.MaxEntries:N0} cap. ");
        if (r.Warnings.Count > 0)
        {
            sb.Append("Warnings: ");
            sb.Append(string.Join(" | ",
                r.Warnings.Take(3)));
            if (r.Warnings.Count > 3) sb.Append($" (+{r.Warnings.Count - 3} more)");
        }
        return sb.ToString();
    }

    [RelayCommand]
    private async Task LoadFromFileAsync()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
            Title = "Select IP list file",
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var text = await File.ReadAllTextAsync(dialog.FileName).ConfigureAwait(true);
            StatusText = $"Loaded {text.Length:N0} chars. Expanding...";
            var r = await Task.Run(() => IpRangeParser.ExpandWithDiagnostics(text)).ConfigureAwait(true);
            if (r.Ips.Count == 0)
            {
                StatusText = "No valid IPs / ranges in file. " + DiagnosticSuffix(r);
                return;
            }

            var rows = await Task.Run(() =>
            {
                var list = new List<IpRow>(r.Ips.Count);
                foreach (var ip in r.Ips) list.Add(new IpRow { Ip = ip, Status = IpRowStatus.Pending });
                return list;
            }).ConfigureAwait(true);

            CustomInput = r.Ips.Count <= LargeListThreshold ? string.Join('\n', r.Ips) : text;
            Healthy.ResetWith(System.Array.Empty<IpRow>());
            ResetCounters();
            Candidates.ResetWith(rows);
            TotalCount = Candidates.Count;
            StatusText = $"Loaded {TotalCount:N0} IPs from {Path.GetFileName(dialog.FileName)}. Press Start to scan. {DiagnosticSuffix(r)}".TrimEnd();
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to read file: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        if (IsRunning) return;
        if (Candidates.Count == 0) await GenerateIpsAsync().ConfigureAwait(true);
        if (Candidates.Count == 0)
        {
            StatusText = "Nothing to scan. Import a preset range or paste IPs first.";
            return;
        }

        IsRunning = true;
        IsPaused = false;
        _pauseGate.Set();
        StatusText = "Scanning…";
        ResetCounters();
        Healthy.Clear();

        foreach (var row in Candidates.ToList())
        {
            row.Status = IpRowStatus.Pending;
            row.LatencyMs = null;
            row.Ttl = null;
            row.Message = "";
        }
        TotalCount = Candidates.Count;

        var method = SelectedCheckMethod switch
        {
            "TLS + SNI" => IpHealthCheckMethod.TlsSni,
            _ => IpHealthCheckMethod.Ping,
        };
        var timeout = TimeSpan.FromMilliseconds(Math.Clamp(TimeoutMs, 500, 30_000));
        var conc = Math.Clamp(Concurrency, 1, 200);
        var sni = (SniHost ?? "").Trim();

        _runCts = new CancellationTokenSource();
        var ct = _runCts.Token;
        var sem = new SemaphoreSlim(conc, conc);
        var dispatcher = Application.Current?.Dispatcher;

        var work = Candidates.ToList();

        try
        {
            var tasks = new List<Task>(work.Count);
            foreach (var row in work)
            {
                if (ct.IsCancellationRequested) break;

                await WaitWhilePausedAsync(ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested) break;

                await sem.WaitAsync(ct).ConfigureAwait(false);
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        var result = await _healthChecker.CheckAsync(row.Ip, method, timeout, sni, ct).ConfigureAwait(false);
                        _resultQueue.Enqueue((row, result));
                        ScheduleResultFlush(dispatcher);
                    }
                    finally
                    {
                        sem.Release();
                    }
                }, ct));
            }

            await Task.WhenAll(tasks).ConfigureAwait(true);

            FlushResultQueue();
            StatusText = ct.IsCancellationRequested
                ? $"Stopped at {ScannedCount}/{TotalCount} — {HealthyCount} healthy."
                : $"Done. {HealthyCount} healthy / {FailedCount} failed.";
        }
        catch (OperationCanceledException)
        {
            StatusText = $"Stopped at {ScannedCount}/{TotalCount} — {HealthyCount} healthy.";
        }
        finally
        {
            IsRunning = false;
            IsPaused = false;
            _pauseGate.Set();
            _runCts?.Dispose();
            _runCts = null;
        }
    }

    private async Task WaitWhilePausedAsync(CancellationToken ct)
    {
        while (!_pauseGate.IsSet)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await Task.Delay(250, ct).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                throw new OperationCanceledException(ct);
            }
        }
    }

    [RelayCommand]
    private void Pause()
    {
        if (!IsRunning || IsPaused) return;
        _pauseGate.Reset();
        IsPaused = true;
        StatusText = $"Paused at {ScannedCount}/{TotalCount} — {HealthyCount} healthy, {FailedCount} failed. Press Resume to continue.";
    }

    [RelayCommand]
    private void Resume()
    {
        if (!IsRunning || !IsPaused) return;
        IsPaused = false;
        _pauseGate.Set();
        StatusText = $"Resumed at {ScannedCount}/{TotalCount}…";
    }

    private void ScheduleResultFlush(System.Windows.Threading.Dispatcher? dispatcher)
    {
        if (Interlocked.CompareExchange(ref _flushScheduled, 1, 0) != 0)
        {
            return;
        }
        if (dispatcher is null)
        {

            FlushResultQueue();
            return;
        }

        _ = dispatcher.InvokeAsync(async () =>
        {
            try
            {
                await Task.Delay(FlushIntervalMs).ConfigureAwait(true);
                FlushResultQueue();
            }
            catch
            {

            }
        });
    }

    private void FlushResultQueue()
    {
        try
        {
            if (_resultQueue.IsEmpty) return;

            var newlyHealthy = new List<IpRow>(capacity: 16);
            var newlyFailed = 0;

            while (_resultQueue.TryDequeue(out var item))
            {
                var row = item.Row;
                var result = item.Result;
                row.LatencyMs = result.LatencyMs;
                row.Ttl = result.Ttl;
                row.Message = result.Message;
                if (result.Ok)
                {
                    row.Status = IpRowStatus.Healthy;
                    newlyHealthy.Add(row);
                }
                else
                {
                    row.Status = IpRowStatus.Failed;
                    newlyFailed++;
                }
                ScannedCount++;
            }

            if (newlyHealthy.Count > 0)
            {
                HealthyCount += newlyHealthy.Count;
                newlyHealthy.Sort(static (a, b) =>
                    (a.LatencyMs ?? int.MaxValue).CompareTo(b.LatencyMs ?? int.MaxValue));

                foreach (var row in newlyHealthy)
                {
                    Candidates.Remove(row);
                }

                var merged = new List<IpRow>(Healthy.Count + newlyHealthy.Count);
                int i = 0, j = 0;
                while (i < Healthy.Count && j < newlyHealthy.Count)
                {
                    var hl = Healthy[i].LatencyMs ?? int.MaxValue;
                    var nl = newlyHealthy[j].LatencyMs ?? int.MaxValue;
                    if (hl <= nl) merged.Add(Healthy[i++]);
                    else merged.Add(newlyHealthy[j++]);
                }
                while (i < Healthy.Count) merged.Add(Healthy[i++]);
                while (j < newlyHealthy.Count) merged.Add(newlyHealthy[j++]);

                Healthy.ResetWith(merged);
            }

            FailedCount += newlyFailed;
            StatusText = $"Scanned {ScannedCount} / {TotalCount} — {HealthyCount} healthy, {FailedCount} failed";
        }
        finally
        {

            Interlocked.Exchange(ref _flushScheduled, 0);
            if (!_resultQueue.IsEmpty)
            {
                ScheduleResultFlush(Application.Current?.Dispatcher);
            }
        }
    }

    [RelayCommand]
    private void Stop()
    {
        try { _runCts?.Cancel(); }
        catch { }

        _pauseGate.Set();
    }

    [RelayCommand]
    private void Clear()
    {
        if (IsRunning) return;
        Candidates.Clear();
        Healthy.Clear();
        ResetCounters();
        StatusText = "Cleared.";
    }

    [RelayCommand]
    private void CopyHealthyNewline()
    {
        if (Healthy.Count == 0) return;
        var sb = new StringBuilder();
        foreach (var r in Healthy) sb.AppendLine(r.Ip);
        TrySetClipboard(sb.ToString());
        StatusText = $"Copied {Healthy.Count} IPs.";
    }

    [RelayCommand]
    private void CopyHealthyCsv()
    {
        if (Healthy.Count == 0) return;
        var sb = new StringBuilder();
        sb.AppendLine("ip,latency_ms,ttl,message");
        foreach (var r in Healthy)
        {
            sb.AppendLine($"{r.Ip},{r.LatencyMs ?? 0},{r.Ttl?.ToString() ?? ""},{Csv(r.Message)}");
        }
        TrySetClipboard(sb.ToString());
        StatusText = $"Copied {Healthy.Count} IPs as CSV.";
    }

    [RelayCommand]
    private void ExportHealthy()
    {
        if (Healthy.Count == 0) return;
        var dialog = new SaveFileDialog
        {
            Filter = "Text files (*.txt)|*.txt|CSV files (*.csv)|*.csv",
            FileName = "healthy_ips.txt",
            Title = "Save healthy IPs",
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            using var sw = new StreamWriter(dialog.FileName);
            var isCsv = dialog.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);
            if (isCsv) sw.WriteLine("ip,latency_ms,ttl,message");
            foreach (var r in Healthy)
            {
                sw.WriteLine(isCsv
                    ? $"{r.Ip},{r.LatencyMs ?? 0},{r.Ttl?.ToString() ?? ""},{Csv(r.Message)}"
                    : r.Ip);
            }
            StatusText = $"Saved {Healthy.Count} IPs.";
        }
        catch (Exception ex)
        {
            StatusText = $"Save failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ApplyToCdnFronting()
    {
        if (Healthy.Count == 0)
        {
            StatusText = "No healthy IPs to apply.";
            return;
        }

        var top = Healthy.Take(CdnFrontingBuilder.MaxCustomCdnFrontingIpAddresses).Select(r => r.Ip).ToList();
        var text = string.Join('\n', top);

        var settingsVm = _serviceProvider.GetService(typeof(SettingsViewModel)) as SettingsViewModel;
        if (settingsVm is not null)
        {
            settingsVm.SelectedProtocolMode = "cdn_fronting";
            settingsVm.CdnFrontingCustomIpList = text;
        }
        else
        {

            _settingsService.Settings.CdnFrontingCustomIpList = text;
            _settingsService.Settings.ProtocolMode = "cdn_fronting";
            _settingsService.Save();
        }

        StatusText = $"Applied {top.Count} healthy IPs → opening Settings → CDN Fronting…";
        _navigationService.NavigateTo("settings");
    }

    [RelayCommand]
    private void CopyIp(IpRow? row)
    {
        if (row is null) return;
        TrySetClipboard(row.Ip);
        StatusText = $"Copied {row.Ip}.";
    }

    private void ResetCounters()
    {
        ScannedCount = 0;
        HealthyCount = 0;
        FailedCount = 0;
        TotalCount = Candidates.Count;
    }

    private static void TrySetClipboard(string text)
    {
        try { Clipboard.SetText(text); } catch { }
    }

    private static string Csv(string s) => s.Contains(',') ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
}

public enum IpRowStatus { Pending, Healthy, Failed }

public sealed partial class IpRow : ObservableObject
{
    [ObservableProperty] private string _ip = "";
    [ObservableProperty] private IpRowStatus _status = IpRowStatus.Pending;
    [ObservableProperty] private int? _latencyMs;
    [ObservableProperty] private int? _ttl;
    [ObservableProperty] private string _message = "";
}

public sealed partial class RangeRow : ObservableObject
{
    [ObservableProperty] private string _cidr = "";
    [ObservableProperty] private string _label = "";
    [ObservableProperty] private bool _isSelected;

    partial void OnIsSelectedChanged(bool value)
    {

        WeakRangeRowEvents.RaiseChanged(this);
    }
}

public sealed partial class SniRow : ObservableObject
{
    [ObservableProperty] private string _hostname = "";
    [ObservableProperty] private bool _isSelected;

    partial void OnIsSelectedChanged(bool value)
    {
        WeakSniRowEvents.RaiseChanged(this);
    }
}

internal static class WeakRangeRowEvents
{
    public static event Action<RangeRow>? Changed;
    public static void RaiseChanged(RangeRow row) => Changed?.Invoke(row);
}

internal static class WeakSniRowEvents
{
    public static event Action<SniRow>? Changed;
    public static void RaiseChanged(SniRow row) => Changed?.Invoke(row);
}
