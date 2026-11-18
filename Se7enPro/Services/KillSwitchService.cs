using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Se7enPro.Models;

namespace Se7enPro.Services;

public sealed class KillSwitchService : IKillSwitchService, IDisposable
{
    private static readonly string[] LegacyRuleNames =
    {
        "Se7enPro_KillSwitch_BlockV4",
        "Se7enPro_KillSwitch_BlockV6",
        "Se7enPro_KillSwitch_BlockDnsV4",
        "Se7enPro_KillSwitch_BlockDnsV6",
        "Se7enPro_KillSwitch_AllowEngine",
    };

    private readonly ILogger<KillSwitchService> _logger;
    private readonly ISettingsService _settings;
    private readonly ITunnelCoreManager _tunnel;
    private readonly ITunManager _tun;

    private readonly WfpKillSwitch _wfp = new();
    private readonly object _sync = new();
    private readonly AutoResetEvent _wake = new(false);

    private Thread? _worker;
    private volatile bool _desiredBlock;

    private volatile bool _userWantsConnection;
    private volatile bool _everConnected;

    private bool _subscribed;
    private volatile bool _disposed;

    public KillSwitchService(
        ILogger<KillSwitchService> logger,
        ISettingsService settings,
        ITunnelCoreManager tunnel,
        ITunManager tun)
    {
        _logger = logger;
        _settings = settings;
        _tunnel = tunnel;
        _tun = tun;

        _userWantsConnection = tunnel.State is not ConnectionState.Disconnected;
        _everConnected = tunnel.State == ConnectionState.Connected;
        Subscribe();

        _ = Task.Run(PurgeLegacyFirewallRules);
        Reconcile();
    }

    public bool IsActive => _wfp.IsArmed;
    public string? LastError { get; private set; }

    private void Subscribe()
    {
        if (_subscribed) return;
        _subscribed = true;
        _settings.SettingsChanged += OnSettingsChanged;
        _tunnel.StateChanged += OnTunnelStateChanged;
        _tunnel.ConnectionIntentChanged += OnConnectionIntentChanged;
        _tun.StateChanged += OnTunStateChanged;
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => Reconcile();
    private void OnTunStateChanged(object? sender, EventArgs e) => Reconcile();

    private void OnTunnelStateChanged(object? sender, ConnectionState state)
    {
        if (state == ConnectionState.Connected)
        {
            _everConnected = true;
        }
        Reconcile();
    }

    private void OnConnectionIntentChanged(object? sender, bool wantsConnection)
    {
        _userWantsConnection = wantsConnection;
        Reconcile();
    }

    public void Arm()
    {
        _userWantsConnection = true;
        _everConnected = true;
        _desiredBlock = true;
        EnsureWorker();
        _wake.Set();
        Reconcile();
    }

    public void Disarm()
    {
        _userWantsConnection = false;
        _desiredBlock = false;
        _wake.Set();
    }

    public void Reconcile()
    {
        if (_disposed) return;

        var s = _settings.Settings;
        var enabled = s.KillSwitchEnabled;
        var systemWide = s.SystemWideTunneling && AdminElevation.IsAdministrator();
        var tunFailed = systemWide && _tun.State == TunState.Error;
        var connected = _tunnel.State == ConnectionState.Connected && !tunFailed;

        var releasing = !_userWantsConnection || _tunnel.State == ConnectionState.Disconnecting;
        _desiredBlock = enabled
                        && _userWantsConnection
                        && _everConnected
                        && !connected
                        && !releasing;

        EnsureWorker();
        try { _wake.Set(); } catch (ObjectDisposedException) { }
    }

    private void EnsureWorker()
    {
        lock (_sync)
        {
            if (_disposed) return;
            if (_worker is { IsAlive: true }) return;
            _worker = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = "Se7enKillSwitch",
            };
            _worker.Start();
        }
    }

    private void WorkerLoop()
    {
        while (!_disposed)
        {
            try
            {
                _wake.WaitOne(TimeSpan.FromSeconds(30));
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            if (_disposed) break;

            while (!_disposed)
            {
                var desired = _desiredBlock;
                if (desired == _wfp.IsArmed) break;

                if (desired)
                {
                    if (_wfp.Arm(EngineExecutablePaths()))
                    {
                        LastError = null;
                        _logger.LogWarning(
                            "KillSwitch: outbound IPv4/IPv6 internet traffic blocked (no live tunnel).");
                    }
                    else
                    {
                        LastError = _wfp.LastError;
                        _logger.LogError(
                            "KillSwitch: could not install the WFP block filters, so traffic is NOT blocked. " +
                            "Run Se7en Pro as Administrator. Details: {Error}", LastError);
                        try
                        {
                            _wake.WaitOne(TimeSpan.FromSeconds(3));
                        }
                        catch (ObjectDisposedException)
                        {
                            return;
                        }
                        break;
                    }
                }
                else
                {
                    _wfp.Disarm();
                    _logger.LogInformation("KillSwitch: outbound traffic released.");
                }
            }
        }
    }

    internal static IReadOnlyList<string> EngineExecutablePaths()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in EngineProcessNames.All)
        {
            names.Add(name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe");
        }
        names.Add("psiphon-tunnel-core.exe");
        names.Add("veep-core.exe");
        names.Add("tun2socks.exe");
        names.Add("Se7enPro.Tun.exe");
        names.Add("zeptun.exe");
        names.Add("Se7enPro.Zeptun.exe");
        names.Add("mihomo.exe");
        names.Add("Se7enPro.Mihomo.exe");

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(Environment.ProcessPath))
        {
            paths.Add(Environment.ProcessPath);
        }
        foreach (var root in new[]
                 {
                     Path.Combine(
                         Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         "Se7en"),
                     Path.Combine(AppContext.BaseDirectory, "Resources"),
                 })
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                foreach (var file in Directory.EnumerateFiles(root, "*.exe", SearchOption.AllDirectories))
                {
                    var leaf = Path.GetFileName(file);
                    if (leaf.Length > 0 && names.Contains(leaf)) paths.Add(file);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[killswitch] could not enumerate {root}: {ex.Message}");
            }
        }

        return paths.ToList();
    }

    private void PurgeLegacyFirewallRules()
    {
        try
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var markerDir = Path.Combine(localAppData, "Se7en");
            var marker = Path.Combine(markerDir, ".legacy_fw_purged");
            if (File.Exists(marker)) return;

            var netsh = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), "netsh.exe");

            foreach (var rule in LegacyRuleNames)
            {
                var psi = new ProcessStartInfo
                {
                    FileName = netsh,
                    Arguments = $"advfirewall firewall delete rule name=\"{rule}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };

                using var p = Process.Start(psi);
                if (p is null) continue;
                p.StandardOutput.ReadToEndAsync();
                p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(3000))
                {
                    try { p.Kill(entireProcessTree: true); } catch { }
                }
            }

            try
            {
                Directory.CreateDirectory(markerDir);
                File.WriteAllText(marker, "ok");
            }
            catch { }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[killswitch] legacy rule cleanup failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_subscribed)
        {
            _settings.SettingsChanged -= OnSettingsChanged;
            _tunnel.StateChanged -= OnTunnelStateChanged;
            _tunnel.ConnectionIntentChanged -= OnConnectionIntentChanged;
            _tun.StateChanged -= OnTunStateChanged;
            _subscribed = false;
        }

        _desiredBlock = false;
        try { _wake.Set(); } catch (ObjectDisposedException) { }
        try { _worker?.Join(TimeSpan.FromSeconds(3)); } catch { }

        _wfp.Dispose();
        try { _wake.Dispose(); } catch { }
    }
}
