using System.Windows.Threading;
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
using Se7enPro.Services;

namespace Se7enPro.ViewModels;

public sealed partial class IpScannerViewModel : PageViewModelBase
{
    private readonly IIpHealthChecker _healthChecker;
    private readonly ISettingsService _settingsService;
    private readonly INavigationService _navigationService;

    private readonly IServiceProvider _serviceProvider;

    private CancellationTokenSource? _runCts;

    private readonly ConcurrentQueue<(int ScanId, IpRow Row, IpHealthResult Result)> _resultQueue = new();
    private readonly DispatcherTimer _flushTimer;
    private int _scanGenerationId;

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

        _flushTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(120),
        };
        _flushTimer.Tick += (_, _) => FlushResultQueue();
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

    [ObservableProperty] private long _targetCount = 5000;
    [ObservableProperty] private bool _isGenerating;
    [ObservableProperty] private bool _isSettingsOpen;

    private const int ChunkSize = 1_000_000;

    private const long GenerateAbsoluteCap = 100_000_000L;

    private IpRangeParser.StreamSource? _streamSource;

    [ObservableProperty] private long _streamingTotalIps;
    partial void OnStreamingTotalIpsChanged(long value) => OnPropertyChanged(nameof(IsStreamingMode));

    [ObservableProperty] private int _streamingChunkIndex;
    [ObservableProperty] private int _streamingChunkCount;
    [ObservableProperty] private long _overallScannedCount;

    public bool IsStreamingMode => _streamSource != null && StreamingTotalIps > 0;

    [ObservableProperty] private bool _isCustomListLoaded;
    partial void OnIsCustomListLoadedChanged(bool value)
    {
        OnPropertyChanged(nameof(CanGenerateFromRanges));
        OnPropertyChanged(nameof(CanUseRanges));
    }

    partial void OnIsGeneratingChanged(bool value)
        => OnPropertyChanged(nameof(CanGenerateFromRanges));

    public bool CanGenerateFromRanges => !IsGenerating && !IsCustomListLoaded;

    public bool CanUseRanges => !IsCustomListLoaded;

    public long SelectedRangeIpTotal
    {
        get
        {
            var cidrs = AvailableRanges.Where(r => r.IsSelected).Select(r => r.Cidr);
            IpRangeParser.ParseSegments(cidrs, out var total);
            return total > long.MaxValue ? long.MaxValue : (long)total;
        }
    }

    public string SelectedRangeIpTotalDisplay
    {
        get
        {
            var t = SelectedRangeIpTotal;
            if (t <= 0) return "no ranges ticked";
            return $"{t:N0} IPs in {AvailableRanges.Count(r => r.IsSelected)} ticked range(s)";
        }
    }

    public long TargetCountSafeCap => GenerateAbsoluteCap;

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
        OnPropertyChanged(nameof(SelectedRangeIpTotal));
        OnPropertyChanged(nameof(SelectedRangeIpTotalDisplay));
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
    {
        OnPropertyChanged(nameof(AvailableRangesSummary));
        OnPropertyChanged(nameof(SelectedRangeIpTotal));
        OnPropertyChanged(nameof(SelectedRangeIpTotalDisplay));
    }

    [RelayCommand]
    private void OpenSettings() => IsSettingsOpen = true;

    [RelayCommand]
    private void CloseSettings() => IsSettingsOpen = false;

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {

        var hasText = !string.IsNullOrWhiteSpace(CustomInput);
        var hasRanges = AvailableRanges.Any(r => r.IsSelected);
        var shouldQueue = !IsRunning && !IsGenerating && (hasText || hasRanges);

        if (shouldQueue)
        {
            await ImportSelectedRangesAsync(null).ConfigureAwait(true);

            if (!IsSettingsOpen) return;
        }

        var method = SelectedCheckMethod ?? "Ping";
        var timeout = Math.Clamp(TimeoutMs, 500, 30_000);
        var conc = Math.Clamp(Concurrency, 1, 200);
        var ticked = AvailableRanges.Count(r => r.IsSelected);
        StatusText = $"Settings saved · {method} · {timeout} ms · concurrency {conc} · {ticked:N0} range(s) ticked.";
        IsSettingsOpen = false;
    }

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
    private async Task ImportSelectedRangesAsync(object? parameter)
    {
        if (IsGenerating) return;
        if (IsCustomListLoaded)
        {
            StatusText = "A file-loaded IP list is in use. Press Clear to re-enable ranges.";
            return;
        }
        if (parameter is not null)
        {
            var s = parameter.ToString();
            if (!string.IsNullOrWhiteSpace(s))
            {
                var cleaned = s.Replace(",", "").Replace("_", "").Trim();
                if (long.TryParse(cleaned, out var override_))
                {
                    TargetCount = override_;
                }
            }
        }
        var picked = AvailableRanges.Where(r => r.IsSelected).Select(r => r.Cidr).ToList();
        var hasTextInput = !string.IsNullOrWhiteSpace(CustomInput);
        if (picked.Count == 0 && !hasTextInput)
        {
            StatusText = "Tick at least one range or paste IPs, then press Generate.";
            return;
        }

        var cap = TargetCountSafeCap;
        var target = TargetCount <= 0 ? cap : Math.Min(TargetCount, cap);

        IsGenerating = true;
        StatusText = "Generating IPs…";

        try
        {

            var streamInputBuilder = new StringBuilder();
            foreach (var cidr in picked)
            {
                streamInputBuilder.Append(cidr);
                streamInputBuilder.Append('\n');
            }
            if (hasTextInput)
            {
                streamInputBuilder.Append(CustomInput);
            }
            var src = await Task.Run(() => IpRangeParser.BuildStream(streamInputBuilder.ToString())).ConfigureAwait(true);

            if (src.TotalHosts == 0)
            {
                var warn = src.Warnings.Count > 0
                    ? $" Warnings: {string.Join(" | ", src.Warnings.Take(3))}"
                    : string.Empty;
                StatusText = $"Nothing valid to scan.{warn}";
                return;
            }

            var effective = TargetCount <= 0
                ? src.TotalHosts
                : Math.Min((ulong)Math.Min(target, GenerateAbsoluteCap), src.TotalHosts);

            var warnSuffix = src.Warnings.Count > 0
                ? $" · {src.Warnings.Count} warning(s)"
                : string.Empty;

            Interlocked.Increment(ref _scanGenerationId);
            while (_resultQueue.TryDequeue(out _)) { }

            if (effective <= (ulong)ChunkSize)
            {

                var smallTarget = (long)effective;
                var sampled = picked.Count > 0
                    ? await Task.Run(() => IpRangeParser.GenerateFromCidrs(
                        picked, smallTarget, smallTarget)).ConfigureAwait(true)
                    : new IpRangeParser.SamplingResult(Array.Empty<string>(), 0, Truncated: false);

                IReadOnlyList<string> textIps = Array.Empty<string>();
                IpRangeParser.ExpansionResult? textResult = null;
                if (hasTextInput)
                {
                    textResult = await Task.Run(() =>
                        IpRangeParser.ExpandWithDiagnostics(CustomInput)).ConfigureAwait(true);
                    textIps = textResult.Ips;
                }

                var rows = await Task.Run(() =>
                {
                    var totalCount = sampled.Ips.Count + textIps.Count;
                    var list = new List<IpRow>(totalCount);
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var ip in sampled.Ips)
                        if (seen.Add(ip)) list.Add(new IpRow { Ip = ip, Status = IpRowStatus.Pending });
                    foreach (var ip in textIps)
                        if (seen.Add(ip)) list.Add(new IpRow { Ip = ip, Status = IpRowStatus.Pending });
                    return list;
                }).ConfigureAwait(true);

                Healthy.ResetWith(System.Array.Empty<IpRow>());
                ResetCounters();
                Candidates.ResetWith(rows);
                TotalCount = Candidates.Count;
                ResetStreamingMode();

                var sb = new StringBuilder();
                sb.Append($"Queued {rows.Count:N0} IPs");
                if (picked.Count > 0) sb.Append($" from {picked.Count} range(s) (pool: {sampled.TotalAvailable:N0})");
                if (textIps.Count > 0) sb.Append($" + {textIps.Count:N0} from text");
                sb.Append(". Press Start to scan.");
                if (textResult is not null) sb.Append(' ').Append(DiagnosticSuffix(textResult));
                StatusText = sb.ToString().TrimEnd();
            }
            else
            {

                var chunkCount = (int)Math.Min((ulong)int.MaxValue, (effective + (ulong)ChunkSize - 1) / (ulong)ChunkSize);
                var previewSize = (int)Math.Min(effective, (ulong)ChunkSize);

                var previewRows = await Task.Run(() =>
                {
                    var list = new List<IpRow>(previewSize);
                    using var enumerator = src.Enumerate(CancellationToken.None).GetEnumerator();
                    for (int i = 0; i < previewSize; i++)
                    {
                        if (!enumerator.MoveNext()) break;
                        list.Add(new IpRow { Ip = enumerator.Current, Status = IpRowStatus.Pending });
                    }
                    return list;
                }).ConfigureAwait(true);

                _streamSource = src;
                StreamingTotalIps = (long)Math.Min(effective, (ulong)long.MaxValue);
                StreamingChunkCount = chunkCount;
                StreamingChunkIndex = 0;
                OverallScannedCount = 0;

                Healthy.Clear();
                ResetCounters();
                Candidates.ResetWith(previewRows);
                TotalCount = (int)Math.Min(effective, (ulong)int.MaxValue);
                StatusText = chunkCount > 1
                    ? $"Queued {effective:N0} IPs across {chunkCount:N0} chunk(s) of {ChunkSize:N0}. Showing first {previewRows.Count:N0} in queue · press Start to scan.{warnSuffix}"
                    : $"Queued {effective:N0} IPs. Press Start to scan.{warnSuffix}";
            }

            IsSettingsOpen = false;
        }
        catch (OperationCanceledException)
        {
            StatusText = "Generation cancelled.";
        }
        catch (Exception ex)
        {
            StatusText = $"Generate failed: {ex.Message}";
        }
        finally
        {
            IsGenerating = false;
        }
    }

    [RelayCommand]
    private void ClearCustomInput()
    {
        CustomInput = "";
        Candidates.Clear();
        Healthy.Clear();
        ResetCounters();
        TotalCount = 0;
        IsCustomListLoaded = false;
        ResetStreamingMode();
        StatusText = "Cleared. Tick ranges and press Generate, or paste IPs and press Start.";
    }

    private void ResetStreamingMode()
    {
        _streamSource = null;
        StreamingTotalIps = 0;
        StreamingChunkIndex = 0;
        StreamingChunkCount = 0;
        OverallScannedCount = 0;
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

        Interlocked.Increment(ref _scanGenerationId);
        while (_resultQueue.TryDequeue(out _)) { }

        Healthy.ResetWith(System.Array.Empty<IpRow>());
        ResetCounters();
        Candidates.ResetWith(rows);
        TotalCount = Candidates.Count;
        ResetStreamingMode();
        StatusText = $"Generated {TotalCount:N0} IPs. Press Start to scan. {DiagnosticSuffix(r)}".TrimEnd();
    }

    [RelayCommand]
    private async Task LoadFromTextFileAsync() => await LoadFromFileAsync().ConfigureAwait(true);

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
            StatusText = $"Loaded {text.Length:N0} chars. Counting...";

            var src = await Task.Run(() => IpRangeParser.BuildStream(text)).ConfigureAwait(true);

            CustomInput = text;

            if (src.TotalHosts == 0)
            {
                var warn = src.Warnings.Count > 0
                    ? $" Warnings: {string.Join(" | ", src.Warnings.Take(3))}"
                    : string.Empty;
                StatusText = $"Loaded {Path.GetFileName(dialog.FileName)} into the IP-input box, but no parseable IPs / ranges found.{warn}";
                return;
            }

            var warnSuffix = src.Warnings.Count > 0
                ? $" · {src.Warnings.Count} warning(s)"
                : string.Empty;
            StatusText = $"Loaded {Path.GetFileName(dialog.FileName)} into the IP-input box ({src.TotalHosts:N0} IPs). Edit if you like, then press Save to queue or Scan All for chunked scan.{warnSuffix}";
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to read file: {ex.Message}";
        }
    }

    private async Task RunStreamingScanAsync(IpRangeParser.StreamSource source)
    {
        var myScanId = Interlocked.Increment(ref _scanGenerationId);

        while (_resultQueue.TryDequeue(out _)) { }

        IsRunning = true;
        IsPaused = false;
        _pauseGate.Set();
        StatusText = "Streaming scan starting…";
        ResetCounters();
        Healthy.Clear();

        StreamingChunkIndex = 0;
        StreamingChunkCount = (int)Math.Ceiling((double)StreamingTotalIps / ChunkSize);
        OverallScannedCount = 0;

        var method = SelectedCheckMethod switch
        {
            "TLS + SNI" => IpHealthCheckMethod.TlsSni,
            _ => IpHealthCheckMethod.Ping,
        };
        var timeout = TimeSpan.FromMilliseconds(Math.Clamp(TimeoutMs, 500, 30_000));
        var conc = Math.Clamp(Concurrency, 1, 200);
        var sni = (SniHost ?? "").Trim();
        long remaining = StreamingTotalIps > 0
            ? StreamingTotalIps
            : (long)Math.Min(source.TotalHosts, (ulong)long.MaxValue);
        long total = remaining;

        _runCts = new CancellationTokenSource();
        var ct = _runCts.Token;
        var sem = new SemaphoreSlim(conc, conc);
        var dispatcher = Application.Current?.Dispatcher;

        _flushTimer.Start();

        try
        {
            await Task.Run(async () =>
            {
                using var enumerator = source.Enumerate(ct).GetEnumerator();
                while (!ct.IsCancellationRequested && remaining > 0)
                {
                    var thisChunkSize = (int)Math.Min(remaining, ChunkSize);
                    var chunk = new List<IpRow>(thisChunkSize);
                    for (int i = 0; i < thisChunkSize; i++)
                    {
                        if (ct.IsCancellationRequested) break;
                        if (!enumerator.MoveNext()) break;
                        chunk.Add(new IpRow { Ip = enumerator.Current, Status = IpRowStatus.Pending });
                    }

                    if (chunk.Count == 0) break;

                    var chunkIdx = ++StreamingChunkIndex;
                    await UiInvokeAsync(dispatcher, () =>
                    {
                        Candidates.ResetWith(chunk);
                        TotalCount = chunk.Count;
                        ScannedCount = 0;
                        FailedCount = 0;
                        StatusText = $"Chunk {chunkIdx}/{StreamingChunkCount} · scanning {chunk.Count:N0} IPs · {OverallScannedCount:N0}/{total:N0} overall · {HealthyCount} healthy";
                    }).ConfigureAwait(false);

                    var tasks = new List<Task>(chunk.Count);
                    foreach (var row in chunk)
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
                                _resultQueue.Enqueue((myScanId, row, result));
                            }
                            finally
                            {
                                sem.Release();
                            }
                        }, ct));
                    }

                    try { await Task.WhenAll(tasks).ConfigureAwait(false); }
                    catch (OperationCanceledException) { }

                    await UiInvokeAsync(dispatcher, () =>
                    {
                        FlushResultQueue();
                        OverallScannedCount += chunk.Count;
                        var failedOverall = Math.Max(0, OverallScannedCount - HealthyCount);
                        StatusText = $"Chunk {chunkIdx}/{StreamingChunkCount} done · {OverallScannedCount:N0}/{total:N0} overall · {HealthyCount} healthy / {failedOverall:N0} failed";
                    }).ConfigureAwait(false);

                    remaining -= chunk.Count;
                    if (ct.IsCancellationRequested) break;
                }
            }, ct).ConfigureAwait(false);

            await UiInvokeAsync(dispatcher, () =>
            {
                _flushTimer.Stop();
                FlushResultQueue();
                StatusText = ct.IsCancellationRequested
                    ? $"Stopped at chunk {StreamingChunkIndex}/{StreamingChunkCount} · {OverallScannedCount:N0}/{total:N0} ({HealthyCount} healthy)"
                    : $"Done. Streaming scan complete · {OverallScannedCount:N0} IPs · {HealthyCount} healthy";
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await UiInvokeAsync(dispatcher, () =>
            {
                _flushTimer.Stop();
                FlushResultQueue();
                StatusText = $"Stopped at chunk {StreamingChunkIndex}/{StreamingChunkCount} · {OverallScannedCount:N0}/{total:N0} ({HealthyCount} healthy)";
            }).ConfigureAwait(false);
        }
        finally
        {
            await UiInvokeAsync(dispatcher, () =>
            {
                _flushTimer.Stop();
                IsRunning = false;
                IsPaused = false;
            }).ConfigureAwait(false);
            _pauseGate.Set();
            _runCts?.Dispose();
            _runCts = null;
        }
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        if (IsRunning) return;

        if (_streamSource != null && _streamSource.TotalHosts > 0)
        {
            await RunStreamingScanAsync(_streamSource).ConfigureAwait(true);
            return;
        }

        if (Candidates.Count == 0) await GenerateIpsAsync().ConfigureAwait(true);
        if (Candidates.Count == 0)
        {
            StatusText = "Nothing to scan. Import a preset range or paste IPs first.";
            return;
        }

        var myScanId = Interlocked.Increment(ref _scanGenerationId);

        while (_resultQueue.TryDequeue(out _)) { }

        IsRunning = true;
        IsPaused = false;
        _pauseGate.Set();
        StatusText = "Scanning…";
        ResetCounters();
        Healthy.Clear();

        foreach (var row in Candidates)
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
        _flushTimer.Start();

        _ = Task.Run(async () =>
        {
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
                            _resultQueue.Enqueue((myScanId, row, result));
                        }
                        finally
                        {
                            sem.Release();
                        }
                    }, ct));
                }

                await Task.WhenAll(tasks).ConfigureAwait(false);

                await UiInvokeAsync(dispatcher, () =>
                {
                    _flushTimer.Stop();
                    FlushResultQueue();
                    StatusText = ct.IsCancellationRequested
                        ? $"Stopped at {ScannedCount}/{TotalCount} — {HealthyCount} healthy."
                        : $"Done. {HealthyCount} healthy / {FailedCount} failed.";
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await UiInvokeAsync(dispatcher, () =>
                {
                    _flushTimer.Stop();
                    FlushResultQueue();
                    StatusText = $"Stopped at {ScannedCount}/{TotalCount} — {HealthyCount} healthy.";
                }).ConfigureAwait(false);
            }
            finally
            {
                await UiInvokeAsync(dispatcher, () =>
                {
                    _flushTimer.Stop();
                    IsRunning = false;
                    IsPaused = false;
                }).ConfigureAwait(false);
                _pauseGate.Set();
                _runCts?.Dispose();
                _runCts = null;
            }
        }, ct);
    }

    private static Task UiInvokeAsync(System.Windows.Threading.Dispatcher? dispatcher, Action action)
    {
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }
        return dispatcher.InvokeAsync(action).Task;
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

    private void FlushResultQueue()
    {
        try
        {
            if (_resultQueue.IsEmpty) return;

            var currentScanId = Volatile.Read(ref _scanGenerationId);
            var newlyHealthy = new List<IpRow>(capacity: 32);
            var newlyFailed = 0;
            var scannedInBatch = 0;

            while (_resultQueue.TryDequeue(out var item))
            {
                if (item.ScanId != currentScanId) continue;

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
                scannedInBatch++;
            }

            if (newlyHealthy.Count > 0)
            {
                HealthyCount += newlyHealthy.Count;
                newlyHealthy.Sort(static (a, b) =>
                    (a.LatencyMs ?? int.MaxValue).CompareTo(b.LatencyMs ?? int.MaxValue));

                Candidates.RemoveRange(newlyHealthy);

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

            ScannedCount += scannedInBatch;
            FailedCount += newlyFailed;
            StatusText = $"Scanned {ScannedCount:N0} / {TotalCount:N0} — {HealthyCount:N0} healthy, {FailedCount:N0} failed";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"FlushResultQueue error: {ex.Message}");
        }
    }

    [RelayCommand]
    private void Stop()
    {
        try { _runCts?.Cancel(); }
        catch { }

        _pauseGate.Set();
        _flushTimer.Stop();
        FlushResultQueue();
    }
    [RelayCommand]
    private void Clear()
    {
        if (IsRunning) return;
        Candidates.Clear();
        Healthy.Clear();
        ResetCounters();
        IsCustomListLoaded = false;
        ResetStreamingMode();
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

        var scanned = Healthy.Select(r => r.Ip);
        var settingsVm = _serviceProvider.GetService(typeof(SettingsViewModel)) as SettingsViewModel;

        var existing = settingsVm is not null
            ? settingsVm.CdnFrontingCustomIpList
            : _settingsService.Settings.CdnFrontingCustomIpList;

        var (mergedText, addedCount, totalCount) = MergeCdnIpList(existing, scanned);

        if (settingsVm is not null)
        {
            settingsVm.SelectedProtocolMode = "cdn_fronting";
            settingsVm.CdnFrontingCustomIpList = mergedText;
        }
        else
        {
            _settingsService.Settings.CdnFrontingCustomIpList = mergedText;
            _settingsService.Settings.ProtocolMode = "cdn_fronting";
            _settingsService.Save();
        }

        StatusText = addedCount > 0
            ? $"Added {addedCount} new IPs (total {totalCount}) → opening Settings → CDN Fronting…"
            : $"All scanned IPs are already in the list (total {totalCount}) → opening Settings → CDN Fronting…";
        _navigationService.NavigateTo("settings");
    }

    private static (string Text, int AddedCount, int TotalCount) MergeCdnIpList(
        string? existingList, IEnumerable<string> scannedIps)
    {
        var max = CdnFrontingBuilder.MaxCustomCdnFrontingIpAddresses;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merged = new List<string>();

        var existingSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in (existingList ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var ip = rawLine.Trim();
            if (ip.Length > 0) existingSet.Add(ip);
        }

        var added = 0;
        foreach (var raw in scannedIps)
        {
            var ip = (raw ?? "").Trim();
            if (ip.Length == 0) continue;
            if (!seen.Add(ip)) continue;
            merged.Add(ip);
            if (!existingSet.Contains(ip)) added++;
            if (merged.Count >= max) break;
        }

        if (merged.Count < max)
        {
            foreach (var rawLine in (existingList ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var ip = rawLine.Trim();
                if (ip.Length == 0) continue;
                if (!seen.Add(ip)) continue;
                merged.Add(ip);
                if (merged.Count >= max) break;
            }
        }

        return (string.Join('\n', merged), added, merged.Count);
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
