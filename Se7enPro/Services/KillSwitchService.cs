using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Se7enPro.Models;

namespace Se7enPro.Services;

public sealed class KillSwitchService : IKillSwitchService, IDisposable
{
    private const string RuleName = "Se7enPro_KillSwitch_Block";
    private const string RuleNameV6 = "Se7enPro_KillSwitch_Block_v6";

    private const string BlockedRemoteV4 =
        "0.0.0.0-9.255.255.255,11.0.0.0-126.255.255.255,128.0.0.0-172.15.255.255,"
        + "172.32.0.0-192.167.255.255,192.169.0.0-255.255.255.255";

    private const string BlockedRemoteV6 = "2000::/3";

    private readonly ILogger<KillSwitchService> _logger;
    private readonly ISettingsService _settings;
    private readonly ITunnelCoreManager _tunnel;
    private volatile bool _isBlocked;

    public KillSwitchService(
        ILogger<KillSwitchService> logger,
        ISettingsService settings,
        ITunnelCoreManager tunnel)
    {
        _logger = logger;
        _settings = settings;
        _tunnel = tunnel;

        _settings.SettingsChanged += (_, _) => Reconcile();
        _tunnel.StateChanged += (_, _) => Reconcile();

        Enqueue(RemoveBlockRules);
    }

    public bool IsActive => _isBlocked;

    public void Arm() => Reconcile();

    public void Disarm() => Enqueue(RemoveBlockRules);

    public void Reconcile()
    {
        var enabled = _settings.Settings.KillSwitchEnabled;
        var state = _tunnel.State;

        if (!enabled)
        {
            if (_isBlocked) Disarm();
            return;
        }

        switch (state)
        {
            case ConnectionState.Error:
                Enqueue(ApplyBlockRules);
                break;

            case ConnectionState.Connecting:
            case ConnectionState.Connected:
            case ConnectionState.Disconnected:
            case ConnectionState.Disconnecting:
                if (_isBlocked) Disarm();
                break;
        }
    }

    private void Enqueue(Func<Task> op)
    {
        lock (_chainLock)
        {
            _chain = _chain.ContinueWith(
                async _ => { try { await op(); } catch { } },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default).Unwrap();
        }
    }

    private readonly object _chainLock = new();
    private Task _chain = Task.CompletedTask;

    private async Task ApplyBlockRules()
    {
        if (_isBlocked) return;

        _logger.LogWarning("KillSwitch: blocking outbound internet traffic to prevent an IP leak.");

        var v4 = await RunNetshAsync(
            "advfirewall", "firewall", "add", "rule",
            $"name={RuleName}", "dir=out", "action=block",
            $"remoteip={BlockedRemoteV4}", "profile=any", "enable=yes");

        var v6 = await RunNetshAsync(
            "advfirewall", "firewall", "add", "rule",
            $"name={RuleNameV6}", "dir=out", "action=block",
            $"remoteip={BlockedRemoteV6}", "profile=any", "enable=yes");

        if (v4 || v6) _isBlocked = true;

        if (!v4)
        {
            _logger.LogError("KillSwitch: the IPv4 block rule could not be installed; "
                             + "traffic is NOT being blocked.");
        }
        if (!v6)
        {

            _logger.LogError("KillSwitch: the IPv6 block rule could not be installed; "
                             + "IPv6 traffic may still bypass the tunnel.");
        }
    }

    private async Task RemoveBlockRules()
    {
        await RunNetshAsync("advfirewall", "firewall", "delete", "rule", $"name={RuleName}");
        await RunNetshAsync("advfirewall", "firewall", "delete", "rule", $"name={RuleNameV6}");
        _isBlocked = false;
    }

    private static async Task<bool> RunNetshAsync(params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "netsh.exe",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        try
        {
            using var p = Process.Start(psi);
            if (p is null) return false;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await p.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return false;
            }
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    public void Dispose()
    {
        Disarm();
        Task pending;
        lock (_chainLock) pending = _chain;
        try { pending.Wait(TimeSpan.FromSeconds(10)); } catch { }
    }
}
