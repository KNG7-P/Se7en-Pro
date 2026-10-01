using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Se7enPro.ViewModels;

namespace Se7enPro.Views;

public partial class LogsPage : UserControl
{
    private ScrollViewer? _scrollViewer;

    
    
    
    
    
    
    
    
    
    
    
    private bool _follow = true;
    private bool _scrollQueued;

    public LogsPage()
    {
        InitializeComponent();
        var vm = App.Services.GetRequiredService<LogsViewModel>();
        DataContext = vm;
        _follow = vm.AutoScroll;

        
        
        
        
        
        LogListBox.ApplyTemplate();
        _scrollViewer = LogListBox.Template?.FindName("ScrollViewer", LogListBox) as ScrollViewer
                        ?? FindVisualChild<ScrollViewer>(LogListBox);

        if (_scrollViewer is not null)
        {
            _scrollViewer.ScrollChanged += OnLogScrollChanged;
        }

        
        
        vm.RequestScrollToEnd += QueueScrollToTail;

        vm.PropertyChanged += OnViewModelPropertyChanged;

        Loaded += (_, _) =>
        {
            _scrollViewer ??= FindVisualChild<ScrollViewer>(LogListBox);
            if (_scrollViewer is not null)
            {
                _scrollViewer.ScrollChanged -= OnLogScrollChanged;
                _scrollViewer.ScrollChanged += OnLogScrollChanged;
            }
            if (_follow) QueueScrollToTail();
        };
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(LogsViewModel.AutoScroll)) return;
        _follow = sender is LogsViewModel v && v.AutoScroll;
        if (_follow) QueueScrollToTail();
    }

        private void QueueScrollToTail()
    {
        if (_scrollQueued) return;
        _scrollQueued = true;

        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _scrollQueued = false;
            if (!_follow) return;
            _scrollViewer?.ScrollToBottom();
        });
    }

    private void OnLogScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        
        
        
        if (e.ExtentHeightChange > 0 && _follow)
        {
            _scrollViewer?.ScrollToBottom();
            return;
        }

        
        
        
        if (e.ViewportHeightChange != 0.0 || e.VerticalChange == 0) return;

        var sv = _scrollViewer;
        if (sv is null) return;

        var follow = sv.ScrollableHeight <= 0
                  || sv.ScrollableHeight - sv.VerticalOffset <= 1.0;
        if (follow == _follow) return;
        _follow = follow;

        
        
        
        
        
        
        if (DataContext is LogsViewModel vm && vm.AutoScroll != follow)
        {
            vm.AutoScroll = follow;
        }
    }

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
