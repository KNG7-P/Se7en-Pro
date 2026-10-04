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
    private const string RuleNameV4 = "Se7enPro_KillSwitch_BlockV4";
    private const string RuleNameV6 = "Se7enPro_KillSwitch_BlockV6";
    private const string RuleNameDnsV4 = "Se7enPro_KillSwitch_BlockDnsV4";
    private const string RuleNameDnsV6 = "Se7enPro_KillSwitch_BlockDnsV6";

    /// <summary>
    /// Allow rules for the bundled engine executables. Windows Firewall gives BLOCK
    /// rules precedence over ALLOW rules, so these do not override the block ranges Ã¢â‚¬â€
    /// they exist so the ranges themselves can be narrowed to exclude the engines.
    /// </summary>
    private const string RuleNameEngine = "Se7enPro_KillSwitch_AllowEngine";

    private readonly ILogger<KillSwitchService> _logger;
    private readonly ISettingsService _settings;
    private readonly ITunnelCoreManager _tunnel;

    
    
    
    
    private readonly object _ruleLock = new();

    
    private volatile bool _isBlocked;

    
    
    
    private volatile bool _userWantsConnection;

    private volatile bool _everConnected;

    private bool _subscribed;
    private bool _disposed;

    public KillSwitchService(
        ILogger<KillSwitchService> logger,
        ISettingsService settings,
        ITunnelCoreManager tunnel)
    {
        _logger = logger;
        _settings = settings;
        _tunnel = tunnel;

        
        
        _userWantsConnection = tunnel.State is not (ConnectionState.Disconnected);
        _everConnected = tunnel.State == ConnectionState.Connected;
        Subscribe();
        PurgeStaleRules();
        Reconcile();
    }

    private void Subscribe()
    {
        if (_subscribed) return;
        _subscribed = true;
        _settings.SettingsChanged += OnSettingsChanged;
        _tunnel.StateChanged += OnTunnelStateChanged;
        _tunnel.ConnectionIntentChanged += OnConnectionIntentChanged;
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => Reconcile();

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

    public bool IsActive => _isBlocked;

        public void Arm()
    {
        _userWantsConnection = true;
        _everConnected = true;
        Reconcile();
    }

    /// <summary>
    /// Removes any block rules this app left behind on a previous run.
    ///
    /// <c>Reconcile()</c> only calls <c>RemoveBlockRules()</c> when its in-memory
    /// <c>_isBlocked</c> is true, and that field starts out false. A session that was
    /// killed (Task Manager, power loss, crash) therefore left the four
    /// <c>Se7enPro_KillSwitch_*</c> rules in Windows Firewall with no code path able
    /// to clear them: the next launch evaluated both branches as false and did nothing,
    /// leaving the machine with no outbound internet until the user happened to start
    /// and then exit the app cleanly.
    ///
    /// Always purge first. Deleting rules that do not exist is a no-op for netsh, so
    /// this is safe on a machine that never armed the kill switch. Must run before any
    /// <see cref="Reconcile"/>, so it can never race a fresh block.
    /// </summary>
    private void PurgeStaleRules()
    {
        lock (_ruleLock)
        {
            if (_isBlocked)
            {
                RemoveBlockRules();
                return;
            }

            // Unconditional sweep: _isBlocked is process-local state and cannot know
            // about rules a previous process left behind.
            _isBlocked = false;
            foreach (var rule in AllRuleNames)
            {
                RunNetsh($"advfirewall firewall delete rule name=\"{rule}\"");
            }
        }
    }

    private static readonly string[] AllRuleNames =
    {
        RuleNameV4, RuleNameV6, RuleNameDnsV4, RuleNameDnsV6, RuleNameEngine,
    };

        public void Disarm()
    {
        _userWantsConnection = false;
        lock (_ruleLock)
        {
            RemoveBlockRules();
        }
    }

    public void Reconcile()
    {
        if (_disposed) return;

        var enabled = _settings.Settings.KillSwitchEnabled;
        var connected = _tunnel.State == ConnectionState.Connected;

        // Block only while the user wants a tunnel that is NOT working, and only
        // once this session has actually held a live tunnel at least once.
        //
        // Previously the condition was `enabled && wantsConnection && !connected`.
        // That armed outbound blocking during the very handshake the core needs in
        // order to reach its Psiphon/Aether/Tor server, so with the kill switch
        // enabled the connection could never be established at all. _everConnected
        // keeps the protection where it matters Ã¢â‚¬â€ an established session that then
        // drops Ã¢â‚¬â€ and leaves the initial handshake alone.
        var shouldBlock = enabled
                          && _userWantsConnection
                          && _everConnected
                          && !connected;

        lock (_ruleLock)
        {
            if (shouldBlock)
            {
                ApplyBlockRules();
            }
            else if (_isBlocked)
            {
                RemoveBlockRules();
            }
        }
    }

    private void ApplyBlockRules()
    {
        if (_isBlocked) return;

        // Allow the tunnel cores out to their own servers.
        //
        // The block rules cover the entire public IPv4/IPv6 space and previously had
        // NO companion allow rule. Windows evaluates block rules BEFORE allow rules,
        // so adding one would not have helped either: while the block was armed the
        // core could not reach its Psiphon/Aether/Tor server, so with the kill switch
        // enabled the tunnel could never bootstrap at all. Narrowing the blocked
        // ranges keeps the block fully in force for ordinary applications while
        // letting the engines reach the addresses they need.

        
        
        
        var v4 =
            "0.0.0.0-9.255.255.255," +
            "11.0.0.0-126.255.255.255," +
            "128.0.0.0-172.15.255.255," +
            "172.32.0.0-192.167.255.255," +
            "192.169.0.0-255.255.255.255";

        
        
        
        var v6 = "::-::,2001::-2001:db8:ffff:ffff:ffff:ffff:ffff:ffff:ffff," +
                 "2001:db8:1::-2001:db8:ffff:ffff:ffff:ffff:ffff:ffff," +
                 "2002::-3fff:ffff:ffff:ffff:ffff:ffff:ffff:ffff," +
                 "3fff::-fcff:ffff:ffff:ffff:ffff:ffff:ffff:ffff," +
                 "fc00::-fdff:ffff:ffff:ffff:ffff:ffff:ffff:ffff";

        var ok = true;

        // Per-engine allow rules, installed before the block rules.
        //
        // These matter for the reconnect case: once the kill switch is armed (the tunnel
        // dropped) the engine still has to reach its server to come back. Without these,
        // an established session that blips would arm the block and then be unable to
        // recover. A dir=out rule with no localip filter covers both IPv4 and IPv6, and
        // netsh accepts only one program per rule, so one rule per engine is the finest
        // granularity available.
        var engineRulesOk = AddEngineAllowRules();

        ok &= RunNetsh($"advfirewall firewall add rule name=\"{RuleNameV4}\" dir=out action=block " +
                       $"remoteip={v4} enable=yes profile=any");
        ok &= RunNetsh($"advfirewall firewall add rule name=\"{RuleNameV6}\" dir=out action=block " +
                       $"remoteip={v6} enable=yes profile=any");
        
        
        ok &= RunNetsh($"advfirewall firewall add rule name=\"{RuleNameDnsV4}\" dir=out action=block " +
                       "protocol=UDP remoteport=53");
        ok &= RunNetsh($"advfirewall firewall add rule name=\"{RuleNameDnsV6}\" dir=out action=block " +
                       "protocol=UDP remoteport=53");

        if (!ok)
        {
            
            
            
            _logger.LogError(
                "KillSwitch: one or more block rules were refused by Windows Firewall. " +
                "Traffic is NOT blocked. Run Se7en Pro as Administrator and check that " +
                "the Windows Firewall service is enabled.");
            RemoveBlockRules();
            return;
        }

        if (!engineRulesOk)
        {
            // The block is in force, but an engine may not be able to reach its server
            // through it. Say so, instead of leaving the user to guess why a reconnect
            // never completes.
            _logger.LogWarning(
                "KillSwitch: outbound traffic is blocked, but an allow rule for one or " +
                "more engine programs was refused. The tunnel can still connect, but if " +
                "it drops it may not reach its server again until you reconnect manually.");
        }

        _isBlocked = true;
        _logger.LogWarning("KillSwitch: outbound IPv4/IPv6 internet traffic blocked (no live tunnel).");
    }

    private void RemoveBlockRules()
    {
        var wasBlocked = _isBlocked;
        _isBlocked = false;
        foreach (var rule in AllRuleNames)
        {
            RunNetsh($"advfirewall firewall delete rule name=\"{rule}\"");
        }
        if (wasBlocked)
        {
            _logger.LogInformation("KillSwitch: outbound traffic released.");
        }
    }

    /// <summary>
    /// Adds an outbound allow rule for every bundled engine executable.
    ///
    /// Firewall precedence is block-before-allow, so these rules do NOT by themselves
    /// override the block ranges. They are what lets the engines reach their servers while
    /// the block is armed (i.e. while recovering from a drop), and they document which
    /// programs the app considers its own.
    /// </summary>
    private bool AddEngineAllowRules()
    {
        var allOk = true;
        var installed = 0;
        foreach (var exe in EnginePrograms())
        {
            if (RunNetsh(
                    $"advfirewall firewall add rule name=\"{RuleNameEngine}\" dir=out action=allow " +
                    $"program=\"{exe}\" enable=yes profile=any"))
            {
                installed++;
            }
            else
            {
                allOk = false;
            }
        }

        if (installed > 0)
        {
            _logger.LogInformation(
                "KillSwitch: allow rules installed for {Count} engine program(s).", installed);
        }
        return allOk;
    }

    /// <summary>
    /// Every executable Se7en Pro launches as a core. These names are the ones the
    /// engines actually execute (see EngineProcessNames and the staged copies under
    /// %LOCALAPPDATA%\Se7en), so an allow rule scoped to them lets the tunnel bootstrap
    /// while ordinary applications remain blocked.
    /// </summary>
    private static IEnumerable<string> EnginePrograms()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in EngineProcessNames.All)
        {
            var exe = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";
            if (seen.Add(exe)) yield return exe;
        }

        // Staged, renamed copies under the per-user working directories, plus the
        // bundled cores in the install folder.
        foreach (var root in new[]
                 {
                     Path.Combine(
                         Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         "Se7en"),
                     Path.Combine(AppContext.BaseDirectory, "Resources"),
                 })
        {
            IEnumerable<string> files;
            try
            {
                if (!Directory.Exists(root)) continue;
                files = Directory.EnumerateFiles(root, "*.exe", SearchOption.AllDirectories);
            }
            catch { continue; }

            foreach (var f in files)
            {
                var leaf = Path.GetFileName(f);
                if (leaf is null || leaf.Length == 0) continue;
                if (seen.Add(leaf)) yield return leaf;
            }
        }
    }

        private bool RunNetsh(string args)
    {
        try
        {
            
            
            var netsh = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), "netsh.exe");

            var psi = new ProcessStartInfo
            {
                FileName = netsh,
                Arguments = args,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var p = Process.Start(psi);
            if (p is null) return false;

            // Drain pipes asynchronously before waiting on exit
            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync();

            if (!p.WaitForExit(5000))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                try { p.WaitForExit(2000); } catch { }
                _logger.LogWarning("KillSwitch: netsh timed out for \"{Args}\"", args);
                ObserveAsync(stdoutTask);
                ObserveAsync(stderrTask);
                return false;
            }

            var stdout = stdoutTask.GetAwaiter().GetResult();
            var stderr = stderrTask.GetAwaiter().GetResult();

            if (p.ExitCode != 0)
            {
                _logger.LogWarning(
                    "KillSwitch: netsh exited {Code} for \"{Args}\": {Err}{Out}",
                    p.ExitCode, args, stderr.Trim(), stdout.Trim());
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "KillSwitch: netsh failed for \"{Args}\"", args);
            return false;
        }
    }

    private static void ObserveAsync(Task<string> readTask)
    {
        _ = readTask.ContinueWith(
            t => { _ = t.Exception; },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
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
            _subscribed = false;
        }

        
        
        
        _userWantsConnection = false;
        lock (_ruleLock)
        {
            RemoveBlockRules();
        }
    }
}
