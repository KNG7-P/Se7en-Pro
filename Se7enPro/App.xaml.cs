using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Se7enPro.Services;

namespace Se7enPro;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    private const string SingleInstanceMutexName = "Global\\Se7enPro_SingleInstance";
    private const string ShowWindowEventName = "Global\\Se7enPro_ShowWindowEvent";

    private const string DaemonMutexName = "Global\\Se7enPro_DaemonInstance";

    private Mutex? _singleInstanceMutex;
    private Mutex? _daemonMutex;
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
        var diagPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Se7en", "startup_diag.log");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(diagPath)!);
            File.AppendAllText(diagPath, $"[{DateTime.Now:O}] Entered OnStartup with args: '{string.Join(" ", e.Args)}'\n");
        }
        catch { }

        try
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

        bool isDaemon = e.Args.Any(a =>
            a.Equals("--daemon", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("--headless", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("-daemon", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("/daemon", StringComparison.OrdinalIgnoreCase));

        if (!isDaemon)
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
            catch (UnauthorizedAccessException)
            {

                isNew = false;
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
        }
        else
        {

            bool isNewDaemon;
            try
            {
                _daemonMutex = new Mutex(true, DaemonMutexName, out isNewDaemon);
            }
            catch (AbandonedMutexException)
            {
                isNewDaemon = true;
            }
            catch (UnauthorizedAccessException)
            {

                isNewDaemon = false;
            }
            catch (Exception)
            {
                isNewDaemon = true;
            }

            if (!isNewDaemon)
            {
                try { File.AppendAllText(diagPath, $"[{DateTime.Now:O}] Another daemon instance is running; exiting new one.\n"); } catch { }
                Shutdown(0);
                return;
            }

            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                try { _daemonMutex?.ReleaseMutex(); } catch { }
                try { _daemonMutex?.Dispose(); } catch { }
            };

            AdminElevation.ReleaseDaemonMutexAction = ReleaseDaemonMutex;
            AdminElevation.ReacquireDaemonMutexAction = ReacquireDaemonMutex;
        }

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

        Services.GetRequiredService<IChildProcessGuard>();

        _ = System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                Services.GetRequiredService<IStartupReaper>().ReapStaleProcesses();
                Services.GetRequiredService<ISystemProxyService>().RestoreIfCrashed();
                Services.GetRequiredService<IStartupRegistration>().SyncFromSetting(settings.Settings.StartWithWindows);
                EnsureDefenderExclusion(AppDomain.CurrentDomain.BaseDirectory);
            }
            catch { }
        });

        if (settings.Settings.AutoConnect)
        {
            _ = StartAutoConnectAsync();
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;

        SessionEnding += OnSessionEnding;

        int renderTier = RenderCapability.Tier >> 16;
        var logger = Services.GetService<ILogger<App>>();
        logger?.LogInformation("UI Engine initialized. RenderTier: {Tier} (Tier 2 = Full Hardware GPU Acceleration), GC Memory: {Memory:N0} KB",
            renderTier, GC.GetTotalMemory(false) / 1024);

        var ipc = Services.GetRequiredService<IpcDaemonService>();
        ipc.Start();

        string? flutterExe = null;
        var appDir = AppDomain.CurrentDomain.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(appDir, "Se7enPro.exe"),
            Path.Combine(appDir, "se7en.exe"),
            Path.GetFullPath(Path.Combine(appDir, @"..\..\..\..\..\Se7enFlutter\build\windows\x64\runner\Release\Se7enPro.exe")),
            Path.GetFullPath(Path.Combine(appDir, @"..\..\..\..\..\Se7enFlutter\build\windows\x64\runner\Release\se7en.exe")),
        };

        foreach (var cand in candidates)
        {
            if (File.Exists(cand))
            {

                var isOwnExe = string.Equals(cand, Process.GetCurrentProcess().MainModule?.FileName, StringComparison.OrdinalIgnoreCase);
                if (!isOwnExe)
                {
                    flutterExe = cand;
                    break;
                }
            }
        }

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        logger?.LogInformation("Running in Headless Backend Mode for Flutter UI.");

        var flutterProcName = Path.GetFileNameWithoutExtension(flutterExe ?? "Se7enPro");
        if (flutterExe != null && Process.GetProcessesByName(flutterProcName).Length == 0)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = flutterExe,
                    WorkingDirectory = Path.GetDirectoryName(flutterExe)!,
                    UseShellExecute = true,
                };
                var flutterProc = Process.Start(psi);
                if (flutterProc != null)
                {
                    flutterProc.EnableRaisingEvents = true;
                    flutterProc.Exited += (_, _) =>
                    {
                        logger?.LogInformation("Flutter frontend closed; shutting down backend.");
                        Current?.Dispatcher.BeginInvoke(() => Current?.Shutdown(0));
                    };
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed to launch Flutter frontend {Exe}", flutterExe);
            }
        }

        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        return;
        }
        catch (Exception ex)
        {
            try { File.AppendAllText(diagPath, $"[{DateTime.Now:O}] FATAL in OnStartup: {ex}\n"); } catch { }
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

    public static void ReleaseDaemonMutex()
    {
        if (Current is App app)
        {
            try { app._daemonMutex?.ReleaseMutex(); } catch { }
            try { app._daemonMutex?.Dispose(); } catch { }
            app._daemonMutex = null;
        }
    }

    public static void ReacquireDaemonMutex()
    {
        if (Current is App app && app._daemonMutex is null)
        {
            try
            {
                app._daemonMutex = new Mutex(true, DaemonMutexName, out _);
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
        services.AddSingleton<IStartupRegistration, StartupRegistration>();
        services.AddSingleton<ISystemProxyService, SystemProxyService>();
        services.AddSingleton<IChildProcessGuard, ChildProcessGuard>();
        services.AddSingleton<IStartupReaper, StartupReaper>();

        services.AddSingleton<TunnelCoreManager>();
        services.AddSingleton<AetherEngine>();
        services.AddSingleton<TorEngine>();
        services.AddSingleton<V2RayEngine>();
        services.AddSingleton<ShardEngine>();
        services.AddSingleton<ITunnelCoreManager, ConnectionManager>();
        services.AddSingleton<ITunManager, WintunTunManager>();
        services.AddSingleton<IKillSwitchService, KillSwitchService>();
        services.AddSingleton<IIpHealthChecker, IpHealthChecker>();
        services.AddSingleton<ICoreUpdateService, CoreUpdateService>();
        services.AddSingleton<IpcDaemonService>();
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

        try { Services?.GetService<IKillSwitchService>()?.Disarm(); }
        catch { }

        try { (Services?.GetService<IChildProcessGuard>() as IDisposable)?.Dispose(); }
        catch { }

        try { (Services?.GetService<IpcDaemonService>() as IAsyncDisposable)?.DisposeAsync().AsTask().Wait(1000); }
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

        try { _daemonMutex?.ReleaseMutex(); } catch { }
        _daemonMutex?.Dispose();
        _daemonMutex = null;
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

    private static void EnsureDefenderExclusion(string appDir)
    {
        try
        {
            if (!AdminElevation.IsAdministrator()) return;
            if (string.IsNullOrWhiteSpace(appDir) || !Directory.Exists(appDir)) return;

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"Add-MpPreference -ExclusionPath '{appDir.TrimEnd('\\')}' -ErrorAction SilentlyContinue\"",
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                UseShellExecute = false,
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(3000);
        }
        catch
        {
        }
    }
}
