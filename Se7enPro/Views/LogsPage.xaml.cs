using System;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Se7enPro.ViewModels;

namespace Se7enPro.Views;

public partial class LogsPage : UserControl
{
    private LogsViewModel? _viewModel;
    private ScrollViewer? _scrollViewer;
    private bool _follow = true;
    private bool _scrollQueued;
    private long _suppressScrollStateUntil;

    public LogsPage()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void AttachViewModel(LogsViewModel? vm)
    {
        if (ReferenceEquals(_viewModel, vm)) return;

        if (_viewModel is not null)
        {
            ((INotifyCollectionChanged)_viewModel.FilteredLines).CollectionChanged -= OnLinesChanged;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = vm;

        if (_viewModel is not null)
        {
            _follow = _viewModel.AutoScroll;
            ((INotifyCollectionChanged)_viewModel.FilteredLines).CollectionChanged += OnLinesChanged;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsLoaded)
        {
            AttachViewModel(e.NewValue as LogsViewModel);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var vm = DataContext as LogsViewModel ?? App.Services?.GetService<LogsViewModel>();
        if (DataContext is null && vm is not null) DataContext = vm;
        AttachViewModel(vm);

        var viewer = FindVisualChild<ScrollViewer>(LogListBox);
        if (!ReferenceEquals(viewer, _scrollViewer))
        {
            if (_scrollViewer is not null) _scrollViewer.ScrollChanged -= OnLogScrollChanged;
            _scrollViewer = viewer;
            if (_scrollViewer is not null) _scrollViewer.ScrollChanged += OnLogScrollChanged;
        }

        if (_follow) QueueScrollToEnd();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        AttachViewModel(null);
        if (_scrollViewer is not null)
        {
            _scrollViewer.ScrollChanged -= OnLogScrollChanged;
            _scrollViewer = null;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(LogsViewModel.AutoScroll) || _viewModel is null) return;
        _follow = _viewModel.AutoScroll;
        if (_follow) QueueScrollToEnd();
    }

    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action is NotifyCollectionChangedAction.Reset or NotifyCollectionChangedAction.Remove)
        {
            _suppressScrollStateUntil = Environment.TickCount64 + 300;
        }

        if (_follow && e.Action is NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Reset)
        {
            QueueScrollToEnd();
        }
    }

    private void QueueScrollToEnd()
    {
        if (_scrollQueued) return;
        _scrollQueued = true;

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            _scrollQueued = false;
            if (!_follow || !IsVisible) return;
            _scrollViewer ??= FindVisualChild<ScrollViewer>(LogListBox);
            if (_scrollViewer is not null)
            {
                _suppressScrollStateUntil = Environment.TickCount64 + 150;
                _scrollViewer.ScrollToEnd();
            }
        });
    }

    private void OnLogScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        var viewer = _scrollViewer;
        if (viewer is null) return;

        var suppressState = Environment.TickCount64 < _suppressScrollStateUntil;

        if (e.VerticalChange < -0.1 && !suppressState)
        {
            if (_follow)
            {
                _follow = false;
                if (_viewModel is { AutoScroll: true }) _viewModel.AutoScroll = false;
            }
            return;
        }

        if (e.VerticalChange > 0.1 && !suppressState)
        {
            if (!_follow && IsAtBottom(viewer))
            {
                _follow = true;
                if (_viewModel is { AutoScroll: false }) _viewModel.AutoScroll = true;
            }
            return;
        }

        if (e.ExtentHeightChange != 0 && _follow)
        {
            QueueScrollToEnd();
        }
    }

    private static bool IsAtBottom(ScrollViewer viewer) =>
        viewer.ScrollableHeight <= 0 || viewer.ScrollableHeight - viewer.VerticalOffset <= 2.0;

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) return typed;
            var sub = FindVisualChild<T>(child);
            if (sub is not null) return sub;
        }
        return null;
    }
}
