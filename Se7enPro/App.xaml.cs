using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Se7enPro.Services;
using Se7enPro.Services.Foxy;
using Se7enPro.Services.Lantern;
using Se7enPro.Services.Veep;
using Se7enPro.ViewModels;
using Se7enPro.Views;

namespace Se7enPro;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    
    
    
    
    
    
    // The mutex and the wake-up event are the process identity of this build. An isolated
    // debug build uses its own names so it can run side by side with an installed copy
    // instead of being folded into it (or taking its window over).
    private const string SingleInstanceMutexName =
#if ISOLATED_DEBUG
        @"Local\Se7enPro_SplitDebug_SingleInstance";
#else
        @"Local\Se7enPro_SingleInstance";
#endif

    private const string ShowWindowEventName =
#if ISOLATED_DEBUG
        @"Local\Se7enPro_SplitDebug_ShowWindowEvent";
#else
        BuildIdentity.ShowWindowEventName;
#endif

    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _showWindowEvent;
    private Thread? _showWindowListener;
    private volatile bool _shuttingDown;

    static App()
    {
        
        System.Windows.Media.Animation.Timeline.DesiredFrameRateProperty.OverrideMetadata(
            typeof(System.Windows.Media.Animation.Timeline),
            new FrameworkPropertyMetadata(30));
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        
        
        Se7enPro.Services.Ui.Adapter = new Se7enPro.Services.WpfUiAdapter();

        if (e.Args.Any(a => a.Equals("--cleanup", StringComparison.OrdinalIgnoreCase)))
        {
            RunUninstallCleanup();
            Shutdown(0);
            return;
        }

        
        
        
        LoadAppFonts();

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
            
            
            if (TrySignalRunningInstance())
            {
                Shutdown(0);
                return;
            }

            
            
            
        }

        try
        {
            _showWindowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEventName);
        }
        catch (Exception ex)
        {
            
            
            
            _showWindowEvent = null;
            System.Diagnostics.Debug.WriteLine($"Show-window event unavailable: {ex.Message}");
        }

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

        try
        {
            var removed = WintunTunManager.RecoverStaleRoutes(Services.GetService<ILogger<App>>());
            if (removed > 0)
                Debug.WriteLine($"[startup] removed {removed} leftover TUN route(s) from a crashed session");

            WintunDnsShell.DisableNrptPolicy();
            _ = Task.Run(async () =>
            {
                try { await WintunTunManager.CleanupStaleAdapterStateAsync(); } catch { }
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[startup] stale TUN route/DNS cleanup failed: {ex.Message}");
        }

        Services.GetRequiredService<IChildProcessGuard>();

        
        
        
        
        Services.GetRequiredService<IKillSwitchService>();

        
        
        void RunDeferredStartup()
        {
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    var tunnel = Services.GetRequiredService<ITunnelCoreManager>();
                    if (tunnel.State == Se7enPro.Models.ConnectionState.Disconnected)
                    {
                        Services.GetRequiredService<IStartupReaper>().ReapStaleProcesses();
                        Services.GetRequiredService<ISystemProxyService>().RestoreIfCrashed();
                    }
                    Services.GetRequiredService<IStartupRegistration>().SyncFromSetting(settings.Settings.StartWithWindows);
                    if (settings.Settings.AutoConnect && tunnel.State == Se7enPro.Models.ConnectionState.Disconnected)
                    {
                        _ = StartAutoConnectAsync();
                    }
                }
                catch { }
            });
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
            RunDeferredStartup();
        }
        else
        {
            
            
            
            
            bool startupWorkQueued = false;
            void OnFirstFrame(object? sender, EventArgs args)
            {
                if (startupWorkQueued) return;
                startupWorkQueued = true;
                mainWindow.ContentRendered -= OnFirstFrame;
                RunDeferredStartup();
            }
            mainWindow.ContentRendered += OnFirstFrame;

            mainWindow.Show();
            mainWindow.Activate();
        }
    }

    public static void ReleaseSingleInstanceMutex()
    {
        if (Current is App app)
        {
            void ReleaseCore()
            {
                try { app._singleInstanceMutex?.ReleaseMutex(); } catch { }
                try { app._singleInstanceMutex?.Dispose(); } catch { }
                app._singleInstanceMutex = null;
            }

            if (app.Dispatcher.CheckAccess())
                ReleaseCore();
            else
                try { app.Dispatcher.Invoke(ReleaseCore); } catch { ReleaseCore(); }
        }
    }

    public static void ReacquireSingleInstanceMutex()
    {
        if (Current is App app && app._singleInstanceMutex is null)
        {
            void ReacquireCore()
            {
                if (app._singleInstanceMutex is not null) return;
                try
                {
                    var m = new Mutex(true, SingleInstanceMutexName, out var createdNew);
                    if (createdNew)
                    {
                        app._singleInstanceMutex = m;
                    }
                    else
                    {
                        try { m.Dispose(); } catch { }
                    }
                }
                catch { }
            }

            if (app.Dispatcher.CheckAccess())
                ReacquireCore();
            else
                try { app.Dispatcher.Invoke(ReacquireCore); } catch { ReacquireCore(); }
        }
    }

        private static bool TrySignalRunningInstance()
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                if (EventWaitHandle.TryOpenExisting(ShowWindowEventName, out var existing))
                {
                    using (existing)
                    {
                        existing.Set();
                    }
                    return true;
                }
            }
            catch (Exception ex)
            {
                
                
                System.Diagnostics.Debug.WriteLine($"Show-window signal failed: {ex.Message}");
                return true;
            }

            Thread.Sleep(120);
        }

        return false;
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
        services.AddSingleton<IdentityPool>();
        services.AddSingleton<IdentityProvisioner>();
        services.AddSingleton<AetherEngine>();
        services.AddSingleton<TorEngine>();
        services.AddSingleton<ShardEngine>();
        services.AddSingleton<V2RayEngine>();
        services.AddSingleton<FoxyEngine>();
        services.AddSingleton<FoxyAccountService>();
        services.AddSingleton<LanternEngine>();
        services.AddSingleton<VeepEngine>();
        services.AddSingleton<ITunnelCoreManager, ConnectionManager>();
        services.AddSingleton<ITunManager, WintunTunManager>();
        services.AddSingleton<IKillSwitchService, KillSwitchService>();
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<ICoreUpdateService, CoreUpdateService>();
        services.AddSingleton<IAppUpdateService, AppUpdateService>();

        services.AddSingleton<MainViewModel>();
        services.AddSingleton<HomeViewModel>();
        services.AddSingleton<SplitTunnelViewModel>();
        services.AddSingleton<SettingsViewModel>();
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
                
                
                
                await System.Threading.Tasks.Task.Run(() => tunnel.StartAsync());
            }
        }
        catch (Exception ex)
        {
            Services?.GetService<ILogger<App>>()?.LogWarning(ex, "Auto-connect at startup failed");
            try
            {
                var home = Services?.GetService<HomeViewModel>();
                if (home is not null)
                {
                    home.StatusText = string.Format(
                        Loc.T("AutoConnectFailed", "Auto-connect failed: {0}"),
                        ex.Message);
                }
            }
            catch { }
        }
    }

    private void ApplyLanguage(string lang) => Loc.Apply(lang);

    private int _cleanupRan;

        private static void LoadAppFonts()
    {
        try
        {
            var fontsDir = Path.Combine(AppContext.BaseDirectory, "Fonts");
            if (!Directory.Exists(fontsDir))
            {
                return;
            }

            var inter = new FontFamily(Path.Combine(fontsDir, "#Inter"));
            var mono = new FontFamily(Path.Combine(fontsDir, "#JetBrains Mono"));

            var app = Application.Current;
            if (app is null)
            {
                return;
            }

            app.Resources["UI.FontFamily"] = inter;
            app.Resources["BrandFont"] = inter;
            app.Resources["UI.MonoFontFamily"] = mono;
        }
        catch
        {
            
            
        }
    }

    private void RunCleanup()
    {
        if (Interlocked.Exchange(ref _cleanupRan, 1) != 0) return;

        
        
        
        
        
        
        
        
        TryCleanup("pending settings save", () =>
        {
            var settingsVm = Services?.GetService<SettingsViewModel>();
            settingsVm?.FlushPendingWrites();
            settingsVm?.Foxy.FlushPendingSave();
        });

        TryCleanup("kill switch", () =>
        {
            Services?.GetService<IKillSwitchService>()?.Disarm();
        });

        TryCleanup("network teardown", () =>
        {
            var tasks = new List<Task>();
            var tun = Services?.GetService<ITunManager>();
            if (tun is not null) tasks.Add(Task.Run(() => tun.DisposeAsync().AsTask()));
            var tunnel = Services?.GetService<ITunnelCoreManager>();
            if (tunnel is not null) tasks.Add(Task.Run(() => tunnel.StopAsync()));
            if (tasks.Count > 0)
            {
                Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            }
        });

        
        
        
        TryCleanup("system proxy", () =>
        {
            var proxy = Services?.GetService<ISystemProxyService>();
            if (proxy?.IsApplied == true) proxy.Clear();
        });

        try { Services?.GetService<ITrayIconService>()?.Dispose(); }
        catch { }

        try { (Services?.GetService<IChildProcessGuard>() as IDisposable)?.Dispose(); }
        catch { }
    }

        private static void TryCleanup(string what, Action step)
    {
        try
        {
            step();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[shutdown] {what} failed: {ex.Message}");
        }
    }

    private static void RunUninstallCleanup()
    {
        ServiceProvider? provider = null;
        try
        {
            var services = new ServiceCollection();
            ConfigureServices(services);
            provider = services.BuildServiceProvider();

            TryCleanup("stale engines", () =>
                provider.GetRequiredService<IStartupReaper>().ReapStaleProcesses());
            TryCleanup("system proxy", () =>
                provider.GetRequiredService<ISystemProxyService>().RestoreIfCrashed());
            TryCleanup("TUN routes", () =>
                WintunTunManager.RecoverStaleRoutes(provider.GetService<ILogger<App>>()));
            TryCleanup("NRPT policy & DNS", () =>
            {
                WintunDnsShell.DisableNrptPolicy();
                WintunTunManager.CleanupStaleAdapterStateAsync().GetAwaiter().GetResult();
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[cleanup] {ex.Message}");
        }
        finally
        {
            provider?.Dispose();
        }
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

        
        
        if (_relaunchOnExit)
        {
            try
            {
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe))
                {
                    Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
                }
            }
            catch { }
        }

        base.OnExit(e);
    }

    private bool _relaunchOnExit;

        public static void RequestRelaunchOnExit()
    {
        if (Current is App app) app._relaunchOnExit = true;
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

    private static int _fatalDialogsShown;

    private static void ShowFatal(Exception ex)
    {
        try
        {
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                BuildIdentity.AppFolderName,
                "logs");
            Directory.CreateDirectory(logDir);
            File.AppendAllText(
                Path.Combine(logDir, "fatal.log"),
                $"{DateTime.Now:O} {ex}\n");
        }
        catch
        {

        }

        
        
        
        
        
        if (Interlocked.Increment(ref _fatalDialogsShown) > 1) return;

        MessageBox.Show(
            $"An unexpected error occurred:\n\n{ex.Message}",
            "Se7en Pro",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }
}
