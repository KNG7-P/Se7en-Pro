using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Se7enPro.Models;
using Se7enPro.Services;
using Se7enPro.ViewModels;

namespace Se7enPro.Views;

public partial class MainWindow : Window
{
    private readonly ISettingsService _settings;
    private readonly ITrayIconService _tray;
    private readonly ITunnelCoreManager _tunnel;
    private readonly Dictionary<object, FrameworkElement> _pageViews = new();
    private bool _forceExit;
    private bool _closeInFlight;
    private bool _askDialogInFlight;
    private bool _windowShapeQueued;

    public MainWindow()
    {
        InitializeComponent();

        Opacity = 1d;
        ContentRendered += OnFirstFrameRendered;
        var vm = App.Services.GetRequiredService<MainViewModel>();
        DataContext = vm;
        vm.PropertyChanged += OnMainViewModelPropertyChanged;

        var settingsVm = App.Services.GetRequiredService<SettingsViewModel>();
        ConfigModals.DataContext = settingsVm;

        SizeChanged += OnMainWindowSizeChanged;

        _settings = App.Services.GetRequiredService<ISettingsService>();
        _tray = App.Services.GetRequiredService<ITrayIconService>();
        _tunnel = App.Services.GetRequiredService<ITunnelCoreManager>();

        _tunnel.StateChanged += OnTunnelStateChanged;
        Closing += OnMainWindowClosing;

        
        
        
        
        _tray.RequestShow -= OnTrayRequestShow;
        _tray.RequestShow += OnTrayRequestShow;
        _tray.RequestExit -= OnTrayRequestExit;
        _tray.RequestExit += OnTrayRequestExit;
        _tray.RequestToggleConnection -= OnTrayRequestToggleConnection;
        _tray.RequestToggleConnection += OnTrayRequestToggleConnection;

        SizeChanged += (_, _) => QueueWindowShape();
        StateChanged += (_, _) =>
        {
            QueueWindowShape();
            if (WindowState == WindowState.Minimized)
            {
                ScheduleIdleTrim();
                if (!_forceExit && _settings.Settings.MinimizeToTray)
                {
                    Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                    {
                        if (WindowState == WindowState.Minimized && !_forceExit)
                            _tray.HideToTray();
                    }));
                }
            }
            else CancelIdleTrim();
        };

        
        
        
        
        
        
        
        Loaded += (_, _) =>
        {
            EnsureVisibleOnSomeScreen();
            QueueWindowShape();

            if (!_firstPageAttached && vm.CurrentPage is not null)
            {
                _firstPageAttached = true;
                ShowPage(vm.CurrentPage);
            }

            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                _tray.Initialize();
                _tray.UpdateConnectionState(_tunnel.State);

                try
                {
                    var settingsVm = App.Services.GetService<SettingsViewModel>();
                    if (settingsVm is not null)
                    {
                        _ = GetOrCreatePage(settingsVm);
                    }
                }
                catch { }
            }));
        };
    }

    private void EnsureVisibleOnSomeScreen()
    {
        if (WindowState != WindowState.Normal) return;

        var workWidth = SystemParameters.WorkArea.Width;
        var workHeight = SystemParameters.WorkArea.Height;
        if (workWidth > 0 && Width > workWidth)
        {
            Width = Math.Max(MinWidth, workWidth);
        }
        if (workHeight > 0 && Height > workHeight)
        {
            Height = Math.Max(MinHeight, workHeight);
        }

        var deskLeft = SystemParameters.VirtualScreenLeft;
        var deskTop = SystemParameters.VirtualScreenTop;
        var deskWidth = SystemParameters.VirtualScreenWidth;
        var deskHeight = SystemParameters.VirtualScreenHeight;
        if (deskWidth <= 0 || deskHeight <= 0) return;

        var curWidth = ActualWidth > 0 ? Math.Min(ActualWidth, Width) : (Width > 0 ? Width : 1000);
        var curHeight = ActualHeight > 0 ? Math.Min(ActualHeight, Height) : (Height > 0 ? Height : 680);

        const double keepVisible = 120;
        var right = Left + curWidth;
        var bottom = Top + curHeight;

        var offScreen =
            right < deskLeft + keepVisible ||
            Left > deskLeft + deskWidth - keepVisible ||
            bottom < deskTop + keepVisible ||
            Top > deskTop + deskHeight - keepVisible ||
            Top < SystemParameters.WorkArea.Top ||
            bottom > SystemParameters.WorkArea.Bottom;

        if (!offScreen) return;

        var workLeft = SystemParameters.WorkArea.Left;
        var workTop = SystemParameters.WorkArea.Top;
        Left = workLeft + Math.Max(0, (workWidth - curWidth) / 2);
        Top = workTop + Math.Max(0, (workHeight - curHeight) / 2);
    }

    
    
    
    
    
    

    private bool _firstPageAttached;

    private void RevealWithFirstPage()
    {
        ContentRendered -= OnFirstFrameRendered;

        if (!_firstPageAttached)
        {
            _firstPageAttached = true;
            if (DataContext is MainViewModel mvm) ShowPage(mvm.CurrentPage);
        }

        Reveal();
    }

    public void Reveal()
    {
        if (_closeInFlight) return;
        if (!_firstPageAttached && DataContext is MainViewModel mvm)
        {
            _firstPageAttached = true;
            ShowPage(mvm.CurrentPage);
        }
        Opacity = 1d;
    }

    private void OnFirstFrameRendered(object? sender, EventArgs e)
    {
        ContentRendered -= OnFirstFrameRendered;
        Opacity = 1d;
        if (!_firstPageAttached && DataContext is MainViewModel mvm)
        {
            _firstPageAttached = true;
            ShowPage(mvm.CurrentPage);
        }
    }

    private void OnMainViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.CurrentPage) && sender is MainViewModel mvm)
        {
            ShowPage(mvm.CurrentPage);
        }
    }

    private void ShowPage(PageViewModelBase? vm)
    {
        if (vm is null)
        {
            PageHost.Content = null;
            return;
        }

        var view = GetOrCreatePage(vm);
        view.Opacity = 1d;
        view.RenderTransform = null;
        PageHost.Content = view;
    }

    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Handled || e.Key != System.Windows.Input.Key.Escape) return;
        var vm = ConfigModals.DataContext as SettingsViewModel
                 ?? (PageHost.Content as FrameworkElement)?.DataContext as SettingsViewModel;
        if (vm is null) return;

        if (vm.IsAddEditConfigDialogOpen) vm.CloseAddEditConfigDialogCommand.Execute(null);
        else if (vm.IsImportDialogOpen) vm.CloseImportDialogCommand.Execute(null);
        else if (vm.IsClearAllConfigsDialogOpen) vm.CloseClearAllConfigsDialogCommand.Execute(null);
        else if (vm.IsResetSettingsDialogOpen) vm.CloseResetSettingsDialogCommand.Execute(null);
        else return;
        e.Handled = true;
    }

    private FrameworkElement GetOrCreatePage(PageViewModelBase vm)
    {        if (_pageViews.TryGetValue(vm, out var cached))
            return cached;

        FrameworkElement view = vm switch
        {
            HomeViewModel => new HomePage(),
            SplitTunnelViewModel => new SplitTunnelPage(),
            SettingsViewModel => new SettingsPage(),
            LogsViewModel => new LogsPage(),
            AboutViewModel => new AboutPage(),
            _ => throw new InvalidOperationException($"No view mapped for {vm.GetType().Name}"),
        };

        view.DataContext = vm;
        _pageViews[vm] = view;

        return view;
    }

    private void QueueWindowShape()
    {
        if (_windowShapeQueued)
            return;

        _windowShapeQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            _windowShapeQueued = false;
            ApplyWindowShape();
        }));
    }

    private void ApplyWindowShape()
    {
    }

    private void OnTunnelStateChanged(object? sender, ConnectionState e)
    {
        Dispatcher.BeginInvoke(new Action(() => _tray.UpdateConnectionState(e)));
    }

    private int _trayToggleInFlight;

    private async void OnTrayRequestToggleConnection(object? sender, EventArgs e)
    {
        
        
        if (Interlocked.Exchange(ref _trayToggleInFlight, 1) == 1) return;
        try
        {
            switch (_tunnel.State)
            {
                case ConnectionState.Disconnected:
                case ConnectionState.Error:
                    
                    
                    
                    await Task.Run(() => _tunnel.StartAsync());
                    break;
                case ConnectionState.Connected:
                case ConnectionState.Connecting:
                    await Task.Run(() => _tunnel.StopAsync());
                    break;
            }
        }
        catch
        {
        }
        finally
        {
            Interlocked.Exchange(ref _trayToggleInFlight, 0);
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        var source = HwndSource.FromHwnd(handle);
        source?.AddHook(WindowProc);
    }

    private static IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_GETMINMAXINFO = 0x0024;
        if (msg == WM_GETMINMAXINFO)
        {
            WmGetMinMaxInfo(hwnd, lParam);
            handled = true;
        }
        return IntPtr.Zero;
    }

    private static void WmGetMinMaxInfo(IntPtr hwnd, IntPtr lParam)
    {
        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        var monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (monitor != IntPtr.Zero)
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
            if (NativeMethods.GetMonitorInfo(monitor, ref info))
            {
                var rcWorkArea = info.rcWork;
                var rcMonitorArea = info.rcMonitor;
                mmi.ptMaxPosition.x = Math.Abs(rcWorkArea.Left - rcMonitorArea.Left);
                mmi.ptMaxPosition.y = Math.Abs(rcWorkArea.Top - rcMonitorArea.Top);
                mmi.ptMaxSize.x = Math.Abs(rcWorkArea.Right - rcWorkArea.Left);
                mmi.ptMaxSize.y = Math.Abs(rcWorkArea.Bottom - rcWorkArea.Top);
            }
        }

        var source = HwndSource.FromHwnd(hwnd);
        var dpiX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        var dpiY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

        var minWidth = 1000.0;
        var minHeight = 680.0;
        if (source?.RootVisual is Window window)
        {
            if (window.MinWidth > 0) minWidth = window.MinWidth;
            if (window.MinHeight > 0) minHeight = window.MinHeight;
        }

        mmi.ptMinTrackSize.x = (int)Math.Ceiling(minWidth * dpiX);
        mmi.ptMinTrackSize.y = (int)Math.Ceiling(minHeight * dpiY);

        Marshal.StructureToPtr(mmi, lParam, true);
    }

    private void OnTrayRequestShow(object? sender, EventArgs e)
    {
        
        
        
        Dispatcher.BeginInvoke(new Action(() => _tray.ShowWindow()));
    }

    private void OnTrayRequestExit(object? sender, EventArgs e)
    {
        
        
        
        Dispatcher.BeginInvoke(new Action(ExitApplication));
    }

        public void RequestClose()
    {
        if (_closeInFlight) return;
        _closeInFlight = true;
        try
        {
            Close();
        }
        finally
        {
            _closeInFlight = false;
        }
    }

    private void OnMainWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_forceExit) return;

        var action = (_settings.Settings.OnCloseAction ?? "ask").ToLowerInvariant();

        switch (action)
        {
            case "exit":
                return;

            case "minimize":
            case "tray":
                e.Cancel = true;
                _tray.HideToTray();
                return;

            default:
                e.Cancel = true;
                if (_askDialogInFlight) return;
                _askDialogInFlight = true;
                Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(HandleAskAction));
                return;
        }
    }

    private void HandleAskAction()
    {
        try
        {
            var dialog = new CloseConfirmationDialog
            {
                Owner = this,
            };

            var ok = dialog.ShowDialog() == true;
            if (!ok || dialog.Result == CloseAction.Cancel)
            {
                return;
            }

            if (dialog.RememberChoice)
            {
                _settings.Settings.OnCloseAction = dialog.Result == CloseAction.Minimize ? "minimize" : "exit";
                _settings.Save();
            }

            if (dialog.Result == CloseAction.Minimize)
            {
                _tray.HideToTray();
            }
            else
            {
                ExitApplication();
            }
        }
        finally
        {
            _askDialogInFlight = false;
        }
    }

    public static readonly DependencyProperty IsSidebarCompactProperty = DependencyProperty.Register(
        nameof(IsSidebarCompact), typeof(bool), typeof(MainWindow), new PropertyMetadata(false));

    public bool IsSidebarCompact
    {
        get => (bool)GetValue(IsSidebarCompactProperty);
        set => SetValue(IsSidebarCompactProperty, value);
    }

    private bool _manualSidebarOverride;

    private void OnToggleSidebarClicked(object sender, RoutedEventArgs e)
    {
        _manualSidebarOverride = true;
        ApplySidebarCompactState(!IsSidebarCompact);
    }

    private void OnMainWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_manualSidebarOverride) return;
        ApplySidebarCompactState(e.NewSize.Width < 860);
    }

    private void ApplySidebarCompactState(bool compact)
    {
        if (IsSidebarCompact == compact) return;
        IsSidebarCompact = compact;
        var width = new GridLength(compact ? 72 : 210);
        SidebarColumn.Width = width;
        var textVis = compact ? Visibility.Collapsed : Visibility.Visible;
        TelegramTextPanel.Visibility = textVis;
        TelegramExternalIcon.Visibility = textVis;
        SidebarVersionBorder.Visibility = textVis;
    }

    public void ExitApplication()
    {
        _forceExit = true;
        Application.Current?.Shutdown();
    }

    private int _backdropDepth;

    public void ShowBackdrop()
    {
        _backdropDepth++;
        if (ModalBackdropOverlay != null)
        {
            ModalBackdropOverlay.Visibility = Visibility.Visible;
        }
    }

    public void HideBackdrop()
    {
        if (_backdropDepth > 0) _backdropDepth--;
        if (_backdropDepth == 0 && ModalBackdropOverlay != null)
        {
            ModalBackdropOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void OnModalBackdropMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        foreach (Window owned in OwnedWindows)
        {
            if (owned.IsVisible)
            {
                owned.Activate();
                break;
            }
        }
    }

    private static DispatcherTimer? _idleTrimTimer;
    private static readonly TimeSpan IdleTrimDelay = TimeSpan.FromSeconds(90);

        public static void ScheduleIdleTrim()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;

        dispatcher.BeginInvoke(new Action(() =>
        {
            CancelIdleTrim();
            var timer = new DispatcherTimer { Interval = IdleTrimDelay };
            timer.Tick += (_, _) =>
            {
                CancelIdleTrim();
                TrimWorkingSet();
            };
            timer.Start();
            _idleTrimTimer = timer;
        }));
    }

        public static void CancelIdleTrim()
    {
        _idleTrimTimer?.Stop();
        _idleTrimTimer = null;
    }

    public static void TrimWorkingSet()
    {
        try
        {
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: false, compacting: true);
            
            
            
            
            
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
                
                
                using var self = Process.GetCurrentProcess();
                NativeMethods.SetProcessWorkingSetSize(self.Handle, (IntPtr)(-1), (IntPtr)(-1));
            }
        }
        catch
        {
        }
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct POINT
{
    public int x;
    public int y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RECT
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MINMAXINFO
{
    public POINT ptReserved;
    public POINT ptMaxSize;
    public POINT ptMaxPosition;
    public POINT ptMinTrackSize;
    public POINT ptMaxTrackSize;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
internal struct MONITORINFO
{
    public int cbSize;
    public RECT rcMonitor;
    public RECT rcWork;
    public uint dwFlags;
}

internal static class NativeMethods
{
    public const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr handle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr minimumWorkingSetSize, IntPtr maximumWorkingSetSize);
}
