using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Se7enPro.Services;

namespace Se7enPro.ViewModels;

public enum LogEngineFilter
{
    All,
    Errors,
    Psiphon,
    V2Ray,
    WARP,
    Tor,
    Tunnel,
    SHARD,
    System,
}

public sealed record LogEngineFilterItem(LogEngineFilter Filter, string English, string Icon, string Channel)
    : LocalizedItem
{
    public string Label => Loc.Of(English);
}

public enum LogSeverity
{
    Plain,
    Info,
    Success,
    Warning,
    Error,
}

public sealed record LogLine(string Time, string Channel, LogSeverity Severity, string Message)
{
        public string ChannelLabel => Channel.ToUpperInvariant();

        public string Raw => string.IsNullOrEmpty(Time)
        ? $"[{Channel}] {Message}"
        : $"{Time} [{Channel}] {Message}";
}

public sealed partial class LogsViewModel : PageViewModelBase
{
    private readonly ITunnelCoreManager _tunnel;
    private readonly ISettingsService _settings;
    private readonly INavigationService _navigation;
    private const int MaxDisplayedLines = 1000;
    private const int PruneChunk = 150;

    private readonly ConcurrentQueue<string> _pending = new();
    private readonly UiTimer _flushTimer;
    private readonly List<LogLine> _allLines = new();
    private bool _isViewActive;
    private bool _needsRefreshOnActivate;

    public override string Title => "Logs";
    public override string Route => "logs";
    public override string Icon => "TextBoxOutline";

    public event Action? RequestScrollToEnd;

    public IReadOnlyList<LogEngineFilterItem> EngineFilterOptions { get; } = new List<LogEngineFilterItem>
    {
        new(LogEngineFilter.All, "All Logs", "FormatListBulleted", ""),
        new(LogEngineFilter.Errors, "Errors & Warnings", "AlertOutline", ""),
        new(LogEngineFilter.Psiphon, "Psiphon Core", "ShieldOutline", LogTags.Psiphon),
        new(LogEngineFilter.WARP, "Cloudflare WARP", "LightningBoltOutline", LogTags.Aether),
        new(LogEngineFilter.Tor, "Tor Network", "Incognito", LogTags.Tor),
        new(LogEngineFilter.SHARD, "SHARD", "Grain", LogTags.Shard),
        new(LogEngineFilter.V2Ray, "V2Ray / Xray", "HubOutline", LogTags.V2Ray),
        new(LogEngineFilter.Tunnel, "TUN & Routing", "Routes", LogTags.Tun),
        new(LogEngineFilter.System, "App & Lifecycle", "ApplicationBracketsOutline", LogTags.App),
    };

    [ObservableProperty]
    private LogEngineFilterItem _selectedEngineFilterItem;

    [ObservableProperty]
    private bool _autoScroll = true;

    [ObservableProperty]
    private string _filter = "";

    public BulkObservableCollection<LogLine> FilteredLines { get; } = new();

    public bool IsDarkTheme => _settings.Settings.Theme != "light";

    public bool HasFilteredLines => FilteredLines.Count > 0;

    public string SearchPlaceholder =>
        string.Format(Loc.Of("Search notices and network logs… ({0} items)"), FilteredLines.Count);

    public bool IsEngineFiltered => SelectedEngineFilterItem.Filter != LogEngineFilter.All;

    private UiTimer? _filterDebounce;
    private int _flushTimerStartQueued;

    public LogsViewModel(ITunnelCoreManager tunnel, ISettingsService settings, INavigationService navigation)
    {
        _tunnel = tunnel;
        _settings = settings;
        _navigation = navigation;
        _settings.SettingsChanged += OnSettingsChanged;

        _navigation.Navigated += (_, page) =>
        {
            var wasActive = _isViewActive;
            _isViewActive = page is LogsViewModel || page?.Route == "logs";
            if (_isViewActive && (!wasActive || _needsRefreshOnActivate))
            {
                _needsRefreshOnActivate = false;
                ReapplyFilter();
            }
        };
        _isViewActive = navigation.Current is LogsViewModel || navigation.Current?.Route == "logs";

        _selectedEngineFilterItem = EngineFilterOptions[0];

        if (tunnel.RecentLog is { } recent)
        {
            _allLines.AddRange(recent.Select(Parse));
        }

        _tunnel.LogLineAppended += OnLogLineAppended;
        _tunnel.LogCleared += OnLogCleared;

        _flushTimer = new UiTimer(TimeSpan.FromMilliseconds(250), FlushPending);

        ReapplyFilter();
    }

    partial void OnFilterChanged(string value)
    {
        DebounceFilter();
    }

    partial void OnSelectedEngineFilterItemChanged(LogEngineFilterItem value)
    {
        OnPropertyChanged(nameof(IsEngineFiltered));
        ReapplyFilter();
    }

    [RelayCommand]
    private void ToggleAutoScroll()
    {
        AutoScroll = !AutoScroll;
        if (AutoScroll)
        {
            RequestScrollToEnd?.Invoke();
        }
    }

    [RelayCommand]
    private void Clear()
    {
        while (_pending.TryDequeue(out _)) { }
        _allLines.Clear();
        FilteredLines.Clear();
        OnPropertyChanged(nameof(HasFilteredLines));
        OnPropertyChanged(nameof(SearchPlaceholder));
    }

    [RelayCommand]
    private void Copy()
    {
        if (FilteredLines.Count > 0)
        {
            Ui.SetClipboard(string.Join(Environment.NewLine, FilteredLines.Select(l => l.Raw)));
        }
    }

    private void DebounceFilter()
    {
        if (_filterDebounce is null)
        {
            _filterDebounce = new UiTimer(TimeSpan.FromMilliseconds(150), () =>
            {
                _filterDebounce!.Stop();
                ReapplyFilter();
            });
        }
        _filterDebounce.Stop();
        _filterDebounce.Start();
    }

    private void ReapplyFilter()
    {
        var f = Filter?.Trim();
        var eng = SelectedEngineFilterItem;
        var hasText = !string.IsNullOrEmpty(f);

        var filtered = _allLines.Where(l =>
            Matches(eng, l) &&
            (!hasText || l.Message.Contains(f!, StringComparison.OrdinalIgnoreCase))
        ).ToList();

        FilteredLines.ResetWith(filtered);
        OnPropertyChanged(nameof(HasFilteredLines));
        OnPropertyChanged(nameof(SearchPlaceholder));

        if (AutoScroll)
        {
            RequestScrollToEnd?.Invoke();
        }
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        Ui.Post(() => OnPropertyChanged(nameof(IsDarkTheme)));
    }

    private void OnLogLineAppended(object? sender, string line)
    {
        _pending.Enqueue($"{DateTime.Now:HH:mm:ss} {line}");

        if (_flushTimer.IsRunning) return;
        if (Interlocked.CompareExchange(ref _flushTimerStartQueued, 1, 0) != 0) return;

        Ui.Post(() =>
        {
            Interlocked.Exchange(ref _flushTimerStartQueued, 0);
            if (!_flushTimer.IsRunning && !_pending.IsEmpty)
            {
                _flushTimer.Start();
            }
        });
    }

    private void OnLogCleared(object? sender, EventArgs e)
    {
        Ui.Post(() =>
        {
            while (_pending.TryDequeue(out _)) { }
            _allLines.Clear();
            FilteredLines.Clear();
            OnPropertyChanged(nameof(HasFilteredLines));
            OnPropertyChanged(nameof(SearchPlaceholder));
        });
    }

    private void FlushPending()
    {
        if (_pending.IsEmpty)
        {
            _flushTimer.Stop();
            return;
        }

        var batch = new List<LogLine>(capacity: 128);
        const int maxPerTick = 500;
        while (batch.Count < maxPerTick && _pending.TryDequeue(out var line))
        {
            batch.Add(Parse(line));
        }

        if (batch.Count == 0) return;

        _allLines.AddRange(batch);
        if (_allLines.Count > MaxDisplayedLines + PruneChunk)
        {
            _allLines.RemoveRange(0, _allLines.Count - MaxDisplayedLines);
        }

        
        if (!_isViewActive)
        {
            _needsRefreshOnActivate = true;
            return;
        }

        var f = Filter?.Trim();
        var eng = SelectedEngineFilterItem;
        var hasText = !string.IsNullOrEmpty(f);

        var matchingFromBatch = batch.Where(l =>
            Matches(eng, l) &&
            (!hasText || l.Message.Contains(f!, StringComparison.OrdinalIgnoreCase))
        ).ToList();

        if (matchingFromBatch.Count > 0)
        {
            if (FilteredLines.Count + matchingFromBatch.Count > MaxDisplayedLines + PruneChunk)
            {
                ReapplyFilter();
            }
            else
            {
                
                
                
                
                FilteredLines.AddRange(matchingFromBatch);
                OnPropertyChanged(nameof(HasFilteredLines));
                OnPropertyChanged(nameof(SearchPlaceholder));
            }

            if (AutoScroll)
            {
                RequestScrollToEnd?.Invoke();
            }
        }
    }

    private static bool Matches(LogEngineFilterItem item, LogLine line)
    {
        return item.Filter switch
        {
            LogEngineFilter.All => true,
            LogEngineFilter.Errors => line.Severity is LogSeverity.Error or LogSeverity.Warning,
            _ => line.Channel == item.Channel,
        };
    }

    private static readonly string[] ErrorWords =
        { "error", "fail", "fatal", "panic", "exception", "aborted", "denied", "refused", "unreachable" };
    private static readonly string[] WarningWords =
        { "warn", "blocked", "bypassed", "retry", "timeout", "expired", "fallback" };
    private static readonly string[] SuccessWords =
        { "connected", "established", "listening", "ready", "success", "restored", "up again" };
    private static readonly string[] InfoWords =
        { "notice", "info", "route:", "saved route", "using upstream" };

        private static LogLine Parse(string raw)
    {
        var text = raw ?? "";
        var time = "";
        if (text.Length > 9 && char.IsDigit(text[0]) && char.IsDigit(text[1])
            && text[2] == ':' && text[5] == ':' && text[8] == ' ')
        {
            time = text[..8];
            text = text[9..];
        }

        var channel = LogTags.Of(text) ?? LogTags.App;
        var message = LogTags.Has(text) ? text[(text.IndexOf(']') + 1)..].TrimStart() : text;

        return new LogLine(time, channel, Classify(message), message);
    }

    private static LogSeverity Classify(string message)
    {
        var lower = message.ToLowerInvariant();
        if (Contains(lower, ErrorWords)) return LogSeverity.Error;
        if (Contains(lower, WarningWords)) return LogSeverity.Warning;
        if (Contains(lower, SuccessWords)) return LogSeverity.Success;
        if (Contains(lower, InfoWords)) return LogSeverity.Info;
        return LogSeverity.Plain;
    }

    private static bool Contains(string haystack, string[] needles)
    {
        foreach (var n in needles)
        {
            if (haystack.Contains(n, StringComparison.Ordinal)) return true;
        }
        return false;
    }
}
