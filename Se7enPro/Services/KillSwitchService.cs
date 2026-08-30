using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Se7enPro.Models;

namespace Se7enPro.Services;

public sealed class KillSwitchService : IKillSwitchService, IDisposable
{
    private const string RuleName = "Se7enPro_KillSwitch_Block";
    private readonly ILogger<KillSwitchService> _logger;
    private readonly ISettingsService _settings;
    private readonly ITunnelCoreManager _tunnel;
    private bool _isBlocked;

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

        Task.Run(() => RemoveBlockRuleQuietly());
    }

    public bool IsActive => _isBlocked;

    public void Arm() => Reconcile();

    public void Disarm()
    {
        RemoveBlockRuleQuietly();
        _isBlocked = false;
    }

    public void Reconcile()
    {
        var enabled = _settings.Settings.KillSwitchEnabled;
        var state = _tunnel.State;

        if (!enabled)
        {
            if (_isBlocked)
            {
                Disarm();
            }
            return;
        }

        if (state is ConnectionState.Error)
        {
            ApplyBlockRule();
        }
        else if (state is ConnectionState.Connected or ConnectionState.Disconnected)
        {
            if (_isBlocked)
            {
                Disarm();
            }
        }
    }

    private void ApplyBlockRule()
    {
        if (_isBlocked) return;
        _isBlocked = true;

        Task.Run(() =>
        {
            try
            {
                _logger.LogWarning("KillSwitch: Blocking outbound internet traffic to prevent IP leak.");

                RunNetsh($"advfirewall firewall add rule name=\"{RuleName}\" dir=out action=block remoteip=0.0.0.0-9.255.255.255,11.0.0.0-126.255.255.255,128.0.0.0-172.15.255.255,172.32.0.0-192.167.255.255,192.169.0.0-255.255.255.255");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to apply KillSwitch firewall rule");
            }
        });
    }

    private void RemoveBlockRuleQuietly()
    {
        Task.Run(() =>
        {
            try
            {
                RunNetsh($"advfirewall firewall delete rule name=\"{RuleName}\"");
            }
            catch { }
        });
    }

    private static void RunNetsh(string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "netsh",
            Arguments = args,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi);
        p?.WaitForExit(3000);
    }

    public void Dispose()
    {
        Disarm();
    }
}
