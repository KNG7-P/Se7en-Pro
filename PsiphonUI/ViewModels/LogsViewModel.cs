using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PsiphonUI.Services;

namespace PsiphonUI.ViewModels;

public sealed partial class LogsViewModel : PageViewModelBase
{
    private readonly ITunnelCoreManager _tunnel;
    private const int MaxDisplayedLines = 5000;

    private readonly ConcurrentQueue<string> _pending = new();
    private readonly DispatcherTimer _flushTimer;

    public override string Title => "Logs";
    public override string Route => "logs";
    public override string Icon => "TextBoxOutline";

    public LogsViewModel(ITunnelCoreManager tunnel)
    {
        _tunnel = tunnel;
        Lines = new ObservableCollection<string>(tunnel.RecentLog);

        _tunnel.LogLineAppended += OnLogLineAppended;
        _tunnel.LogCleared += OnLogCleared;

        _flushTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _flushTimer.Tick += FlushPending;
    }

    public ObservableCollection<string> Lines { get; }

    [ObservableProperty]
    private string _filter = "";

    public System.Collections.Generic.IEnumerable<string> FilteredLines =>
        string.IsNullOrWhiteSpace(Filter)
            ? Lines
            : Lines.Where(l => l.Contains(Filter, StringComparison.OrdinalIgnoreCase));

    partial void OnFilterChanged(string value) => OnPropertyChanged(nameof(FilteredLines));

    private void OnLogLineAppended(object? sender, string line)
    {
        _pending.Enqueue($"{DateTime.Now:HH:mm:ss} {line}");

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        if (dispatcher.CheckAccess())
        {
            if (!_flushTimer.IsEnabled) _flushTimer.Start();
        }
        else
        {
            dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_flushTimer.IsEnabled) _flushTimer.Start();
            }));
        }
    }

    private void OnLogCleared(object? sender, EventArgs e)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            while (_pending.TryDequeue(out _)) { }
            Lines.Clear();
            OnPropertyChanged(nameof(FilteredLines));
        });
    }

    private void FlushPending(object? sender, EventArgs e)
    {
        if (_pending.IsEmpty)
        {

            _flushTimer.Stop();
            return;
        }

        const int maxPerTick = 1000;
        var promoted = 0;
        while (promoted < maxPerTick && _pending.TryDequeue(out var line))
        {
            Lines.Add(line);
            promoted++;
        }

        while (Lines.Count > MaxDisplayedLines)
        {
            Lines.RemoveAt(0);
        }

        if (promoted > 0 && !string.IsNullOrWhiteSpace(Filter))
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
