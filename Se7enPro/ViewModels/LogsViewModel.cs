using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Se7enPro.Services;

namespace Se7enPro.ViewModels;

public sealed partial class LogsViewModel : PageViewModelBase
{
    private readonly ITunnelCoreManager _tunnel;
    private const int MaxDisplayedLines = 2500;

    private readonly ConcurrentQueue<string> _pending = new();
    private readonly DispatcherTimer _flushTimer;

    public override string Title => "Logs";
    public override string Route => "logs";
    public override string Icon => "TextBoxOutline";

    public LogsViewModel(ITunnelCoreManager tunnel)
    {
        _tunnel = tunnel;
        Lines = new BulkObservableCollection<string>();
        if (tunnel.RecentLog is { } recent)
        {
            Lines.AddRange(recent);
        }

        _tunnel.LogLineAppended += OnLogLineAppended;
        _tunnel.LogCleared += OnLogCleared;

        _flushTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(200),
        };
        _flushTimer.Tick += FlushPending;
    }

    public BulkObservableCollection<string> Lines { get; }

    [ObservableProperty]
    private string _filter = "";

    public IEnumerable<string> FilteredLines =>
        string.IsNullOrWhiteSpace(Filter)
            ? Lines
            : Lines.Where(l => l.Contains(Filter, StringComparison.OrdinalIgnoreCase));

    private DispatcherTimer? _filterDebounce;

    partial void OnFilterChanged(string value)
    {
        if (_filterDebounce is null)
        {
            _filterDebounce = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(200),
            };
            _filterDebounce.Tick += (_, _) =>
            {
                _filterDebounce!.Stop();
                OnPropertyChanged(nameof(FilteredLines));
            };
        }
        _filterDebounce.Stop();
        _filterDebounce.Start();
    }

    private int _flushTimerStartQueued;

    private void OnLogLineAppended(object? sender, string line)
    {
        _pending.Enqueue($"{DateTime.Now:HH:mm:ss} {line}");

        if (_flushTimer.IsEnabled) return;
        if (Interlocked.CompareExchange(ref _flushTimerStartQueued, 1, 0) != 0) return;

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;

        if (dispatcher.CheckAccess())
        {
            Interlocked.Exchange(ref _flushTimerStartQueued, 0);
            if (!_flushTimer.IsEnabled && !_pending.IsEmpty) _flushTimer.Start();
        }
        else
        {
            dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                Interlocked.Exchange(ref _flushTimerStartQueued, 0);
                if (!_flushTimer.IsEnabled && !_pending.IsEmpty)
                {
                    _flushTimer.Start();
                }
            }));
        }
    }

    private void OnLogCleared(object? sender, EventArgs e)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        dispatcher.BeginInvoke(new Action(() =>
        {
            while (_pending.TryDequeue(out _)) { }
            Lines.Clear();
            OnPropertyChanged(nameof(FilteredLines));
        }));
    }

    private void FlushPending(object? sender, EventArgs e)
    {
        if (_pending.IsEmpty)
        {
            _flushTimer.Stop();
            return;
        }

        var batch = new List<string>(capacity: 128);
        const int maxPerTick = 500;
        while (batch.Count < maxPerTick && _pending.TryDequeue(out var line))
        {
            batch.Add(line);
        }

        if (batch.Count == 0) return;

        if (Lines.Count + batch.Count > MaxDisplayedLines)
        {
            var totalCount = Lines.Count + batch.Count;
            var skipCount = totalCount - MaxDisplayedLines;
            var combined = new List<string>(MaxDisplayedLines);
            for (int i = skipCount; i < Lines.Count; i++)
            {
                combined.Add(Lines[i]);
            }
            combined.AddRange(batch);
            Lines.ResetWith(combined);
        }
        else
        {
            Lines.AddRange(batch);
        }

        if (!string.IsNullOrWhiteSpace(Filter))
        {
            OnPropertyChanged(nameof(FilteredLines));
        }
    }

    [RelayCommand]
    private void Clear()
    {
        while (_pending.TryDequeue(out _)) { }
        Lines.Clear();
        OnPropertyChanged(nameof(FilteredLines));
    }

    [RelayCommand]
    private void Copy()
    {
        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, Lines));
        }
        catch
        {
        }
    }
}
