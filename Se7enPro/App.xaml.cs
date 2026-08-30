using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Se7enPro.Services;
using Se7enPro.ViewModels;
using Se7enPro.Views;

namespace Se7enPro;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    private const string SingleInstanceMutexName = "Global\\Se7enPro_SingleInstance";
    private const string ShowWindowEventName = "Global\\Se7enPro_ShowWindowEvent";

    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _showWindowEvent;
    private Thread? _showWindowListener;
    private volatile bool _shuttingDown;

    static App()
    {

        System.Windows.Media.Animation.Timeline.DesiredFrameRateProperty.OverrideMetadata(
            typeof(System.Windows.Media.Animation.Timeline),
            new FrameworkPropertyMetadata(60));
    }

    protected override void OnStartup(StartupEventArgs e)
    {

        bool isNew;
        try
        {
            _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out isNew);
        }
        catch (AbandonedMutexException)
        {

            isNew = true;
        }
        catch (Exception)
        {

            isNew = true;
        }

        if (!isNew)
        {
            try
            {
                if (EventWaitHandle.TryOpenExisting(ShowWindowEventName, out var existing))
                {
                    existing.Set();
                    existing.Dispose();
                }
            }
            catch
            {
            }
            Shutdown(0);
            return;
        }

        _showWindowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEventName);
        _showWindowListener = new Thread(ShowWindowListenerLoop)
        {
            IsBackground = true,
            Name = "ShowWindowSignalListener",
        };
        _showWindowListener.Start();

        base.OnStartup(e);

        var services = new ServiceCollection();
        ConfigureServices(services);
        Services = services.BuildServiceProvider();

        AdminElevation.ReleaseMutexAction = ReleaseSingleInstanceMutex;
        AdminElevation.ReacquireMutexAction = ReacquireSingleInstanceMutex;
        AdminElevation.ShutdownAppAction = () =>
        {
            Current?.Dispatcher.BeginInvoke(() => Current?.Shutdown(0));
        };

        var settings = Services.GetRequiredService<ISettingsService>();
        settings.Load();
        Services.GetRequiredService<IThemeService>().ApplyTheme(settings.Settings.Theme);
        ApplyLanguage(settings.Settings.Language);

        Services.GetRequiredService<IChildProcessGuard>();

        _ = System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                Services.GetRequiredService<IStartupReaper>().ReapStaleProcesses();
                Services.GetRequiredService<ISystemProxyService>().RestoreIfCrashed();
                Services.GetRequiredService<IStartupRegistration>().SyncFromSetting(settings.Settings.StartWithWindows);
            }
            catch { }
        });

        if (settings.Settings.AutoConnect)
        {
            _ = StartAutoConnectAsync();
        }

        EventManager.RegisterClassHandler(
            typeof(ComboBox),
            UIElement.PreviewMouseWheelEvent,
            new MouseWheelEventHandler(OnComboBoxPreviewMouseWheel));

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;

        SessionEnding += OnSessionEnding;

        int renderTier = RenderCapability.Tier >> 16;
        var logger = Services.GetService<ILogger<App>>();
        logger?.LogInformation("UI Engine initialized. RenderTier: {Tier} (Tier 2 = Full Hardware GPU Acceleration), GC Memory: {Memory:N0} KB",
            renderTier, GC.GetTotalMemory(false) / 1024);

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;

        bool startMinimized = e.Args.Any(a =>
            a.Equals("--autostart", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("--minimized", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("/minimized", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("-minimized", StringComparison.OrdinalIgnoreCase));

        if (startMinimized)
        {
            var tray = Services.GetRequiredService<ITrayIconService>();
            tray.Initialize();
            tray.HideToTray();
        }
        else
        {
            mainWindow.Show();
            mainWindow.Activate();
        }
    }

    public static void ReleaseSingleInstanceMutex()
    {
        if (Current is App app)
        {
            try { app._singleInstanceMutex?.ReleaseMutex(); } catch { }
            try { app._singleInstanceMutex?.Dispose(); } catch { }
            app._singleInstanceMutex = null;
        }
    }

    public static void ReacquireSingleInstanceMutex()
    {
        if (Current is App app && app._singleInstanceMutex is null)
        {
            try
            {
                app._singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out _);
            }
            catch { }
        }
    }

    private void ShowWindowListenerLoop()
    {
        var handle = _showWindowEvent;
        if (handle is null) return;

        while (!_shuttingDown)
        {
            bool signaled;
            try
            {
                signaled = handle.WaitOne();
            }
            catch
            {
                return;
            }

            if (!signaled || _shuttingDown) return;

            try
            {
                Dispatcher.Invoke(() =>
                {
                    var tray = Services?.GetService<ITrayIconService>();
                    if (tray is not null)
                    {
                        tray.ShowWindow();
                        return;
                    }

                    var win = Current?.MainWindow;
                    if (win is null) return;
                    if (!win.IsVisible) win.Show();
                    if (win.WindowState == WindowState.Minimized) win.WindowState = WindowState.Normal;
                    win.ShowInTaskbar = true;
                    win.Activate();
                    win.Topmost = true;
                    win.Topmost = false;
                    win.Focus();
                });
            }
            catch
            {
            }
        }
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddLogging(b =>
        {
            b.AddDebug();
            b.SetMinimumLevel(LogLevel.Information);
        });

        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IThemeService, ThemeService>();
        services.AddSingleton<ITrayIconService, TrayIconService>();
        services.AddSingleton<IStartupRegistration, StartupRegistration>();
        services.AddSingleton<ISystemProxyService, SystemProxyService>();
        services.AddSingleton<IChildProcessGuard, ChildProcessGuard>();
        services.AddSingleton<IStartupReaper, StartupReaper>();

        services.AddSingleton<TunnelCoreManager>();
        services.AddSingleton<AetherEngine>();
        services.AddSingleton<TorEngine>();
        services.AddSingleton<ITunnelCoreManager, ConnectionManager>();
        services.AddSingleton<ITunManager, WintunTunManager>();
        services.AddSingleton<IKillSwitchService, KillSwitchService>();
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<IIpHealthChecker, IpHealthChecker>();
        services.AddSingleton<ICoreUpdateService, CoreUpdateService>();

        services.AddSingleton<MainViewModel>();
        services.AddSingleton<HomeViewModel>();
        services.AddSingleton<SplitTunnelViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<IpScannerViewModel>();

        services.AddSingleton<Func<IpScannerViewModel>>(sp => () => sp.GetRequiredService<IpScannerViewModel>());
        services.AddSingleton<LogsViewModel>();
        services.AddSingleton<AboutViewModel>();
    }

    private static async System.Threading.Tasks.Task StartAutoConnectAsync()
    {
        try
        {
            await System.Threading.Tasks.Task.Delay(300);
            var tunnel = Services?.GetService<ITunnelCoreManager>();
            if (tunnel is not null)
            {
                await tunnel.StartAsync();
            }
        }
        catch
        {
        }
    }

    private void ApplyLanguage(string lang)
    {
        var culture = lang switch
        {
            "fa" => new CultureInfo("fa-IR"),
            _ => new CultureInfo("en-US"),
        };
        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
        FrameworkElement.LanguageProperty.OverrideMetadata(
            typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(culture.IetfLanguageTag)));
    }

    private int _cleanupRan;

    private void RunCleanup()
    {
        if (Interlocked.Exchange(ref _cleanupRan, 1) != 0) return;

        try
        {

            var tun = Services?.GetService<ITunManager>();
            if (tun is not null)
            {
                tun.DisposeAsync().AsTask()
                   .WaitAsync(TimeSpan.FromSeconds(6)).GetAwaiter().GetResult();
            }

            Services?.GetService<ITunnelCoreManager>()?.StopAsync()
                     .WaitAsync(TimeSpan.FromSeconds(6)).GetAwaiter().GetResult();

            Services?.GetService<ISystemProxyService>()?.Clear();
        }
        catch
        {

        }

        try { Services?.GetService<ITrayIconService>()?.Dispose(); }
        catch { }

        try { Services?.GetService<IKillSwitchService>()?.Disarm(); }
        catch { }

        try { (Services?.GetService<IChildProcessGuard>() as IDisposable)?.Dispose(); }
        catch { }
    }

    private void OnSessionEnding(object sender, SessionEndingCancelEventArgs e)
    {
        RunCleanup();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _shuttingDown = true;
        RunCleanup();

        try { _showWindowEvent?.Set(); } catch { }
        try { _showWindowEvent?.Dispose(); } catch { }
        _showWindowEvent = null;

        try { _singleInstanceMutex?.ReleaseMutex(); } catch { }
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(
        object sender,
        System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        ShowFatal(e.Exception);
        e.Handled = true;
    }

    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            ShowFatal(ex);
        }
    }

    private static void OnComboBoxPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ComboBox cb || cb.IsDropDownOpen) return;
        e.Handled = true;
        var ancestor = FindAncestorScrollViewer(cb);
        if (ancestor is null) return;
        var args = new MouseWheelEventArgs(e.MouseDevice!, e.Timestamp, e.Delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
            Source = cb,
        };
        ancestor.RaiseEvent(args);
    }

    private static ScrollViewer? FindAncestorScrollViewer(DependencyObject? from)
    {
        for (var node = from is null ? null : VisualTreeHelper.GetParent(from);
             node is not null;
             node = VisualTreeHelper.GetParent(node))
        {
            if (node is ScrollViewer sv) return sv;
        }
        return null;
    }

    private static void ShowFatal(Exception ex)
    {
        try
        {
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Se7en",
                "logs");
            Directory.CreateDirectory(logDir);
            File.AppendAllText(
                Path.Combine(logDir, "fatal.log"),
                $"{DateTime.Now:O} {ex}\n");
        }
        catch
        {

        }

        MessageBox.Show(
            $"An unexpected error occurred:\n\n{ex.Message}",
            "Se7en Pro",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }
}
