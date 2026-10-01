using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Microsoft.Extensions.Logging;
using Se7enPro.Models;

namespace Se7enPro.Services;

public sealed class KillSwitchService : IKillSwitchService, IDisposable
{
    private const string RuleNameV4 = "Se7enPro_KillSwitch_BlockV4";
    private const string RuleNameV6 = "Se7enPro_KillSwitch_BlockV6";
    private const string RuleNameDnsV4 = "Se7enPro_KillSwitch_BlockDnsV4";
    private const string RuleNameDnsV6 = "Se7enPro_KillSwitch_BlockDnsV6";

    private readonly ILogger<KillSwitchService> _logger;
    private readonly ISettingsService _settings;
    private readonly ITunnelCoreManager _tunnel;

    
    
    
    
    private readonly object _ruleLock = new();

    
    private volatile bool _isBlocked;

    
    
    
    private volatile bool _userWantsConnection;

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
        Subscribe();
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

    private void OnTunnelStateChanged(object? sender, ConnectionState state) => Reconcile();

    private void OnConnectionIntentChanged(object? sender, bool wantsConnection)
    {
        _userWantsConnection = wantsConnection;
        Reconcile();
    }

    public bool IsActive => _isBlocked;

        public void Arm()
    {
        _userWantsConnection = true;
        Reconcile();
    }

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

        
        
        
        var shouldBlock = enabled && _userWantsConnection && !connected;

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

        _isBlocked = true;
        _logger.LogWarning("KillSwitch: outbound IPv4/IPv6 internet traffic blocked (no live tunnel).");
    }

    private void RemoveBlockRules()
    {
        var wasBlocked = _isBlocked;
        _isBlocked = false;
        foreach (var rule in new[] { RuleNameV4, RuleNameV6, RuleNameDnsV4, RuleNameDnsV6 })
        {
            RunNetsh($"advfirewall firewall delete rule name=\"{rule}\"");
        }
        if (wasBlocked)
        {
            _logger.LogInformation("KillSwitch: outbound traffic released.");
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

            
            
            
            if (!p.WaitForExit(5000))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                _logger.LogWarning("KillSwitch: netsh timed out for \"{Args}\"", args);
                return false;
            }

            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();

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
