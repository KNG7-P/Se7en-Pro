using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
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

    public MainWindow()
    {
        InitializeComponent();
        var vm = App.Services.GetRequiredService<MainViewModel>();
        DataContext = vm;
        vm.PropertyChanged += OnMainViewModelPropertyChanged;

        _settings = App.Services.GetRequiredService<ISettingsService>();
        _tray = App.Services.GetRequiredService<ITrayIconService>();
        _tunnel = App.Services.GetRequiredService<ITunnelCoreManager>();

        _tunnel.StateChanged += OnTunnelStateChanged;
        Closing += OnMainWindowClosing;

        ShowPage(vm.CurrentPage);

        Loaded += (_, _) =>
        {
            _tray.Initialize();
            _tray.UpdateConnectionState(_tunnel.State);
            _tray.RequestShow += OnTrayRequestShow;
            _tray.RequestExit += OnTrayRequestExit;
            _tray.RequestToggleConnection += OnTrayRequestToggleConnection;
        };
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

        if (!_pageViews.TryGetValue(vm, out var view))
        {
            view = vm switch
            {
                HomeViewModel => new HomePage(),
                SplitTunnelViewModel => new SplitTunnelPage(),
                SettingsViewModel => new SettingsPage(),
                IpScannerViewModel => new IpScannerPage(),
                LogsViewModel => new LogsPage(),
                AboutViewModel => new AboutPage(),
                _ => throw new InvalidOperationException($"No view mapped for {vm.GetType().Name}"),
            };
            view.DataContext = vm;
            _pageViews[vm] = view;
        }

        PageHost.Content = view;
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
                    await _tunnel.StartAsync();
                    break;
                case ConnectionState.Connected:
                case ConnectionState.Connecting:
                    await _tunnel.StopAsync();
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

        var minWidth = 940.0;
        var minHeight = 770.0;
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
        Dispatcher.Invoke(() => _tray.ShowWindow());
    }

    private void OnTrayRequestExit(object? sender, EventArgs e)
    {

        Dispatcher.BeginInvoke(new Action(ExitApplication));
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
                e.Cancel = true;
                _tray.HideToTray();
                return;

            default:
                e.Cancel = true;
                HandleAskAction();
                return;
        }
    }

    private void HandleAskAction()
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

    private void ExitApplication()
    {
        _forceExit = true;
        Application.Current?.Shutdown();
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
}
