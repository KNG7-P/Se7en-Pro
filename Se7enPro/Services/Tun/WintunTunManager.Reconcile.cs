using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Se7enPro.Models;

namespace Se7enPro.Services;

public sealed partial class WintunTunManager
{
    private async Task ReconcileAsync()
    {
        await _reconcileGate.WaitAsync();
        try
        {
            var systemWide = _settings.Settings.SystemWideTunneling && AdminElevation.IsAdministrator();
            var tunnelState = _tunnel.State;
            var socksPort = _tunnel.SocksProxyPort;
            var have = State is TunState.Starting or TunState.Running;

            var wantStart = systemWide
                && tunnelState == ConnectionState.Connected
                && socksPort > 0;

            var wantKeep = wantStart
                || (systemWide
                    && have
                    && socksPort > 0
                    && !_catchAllGraceExpired
                    && tunnelState == ConnectionState.Connecting);

            var splitHash = ComputeSplitHash(socksPort);
            WriteDiag($"reconcile: systemWide={systemWide} tunnel={tunnelState} socks={socksPort} "
                      + $"wantStart={wantStart} wantKeep={wantKeep} have={have} state={State}");

            if (wantStart && !have)
            {
                StartSupervisor(socksPort);
                _activeSplitHash = splitHash;
            }
            else if (wantStart && have && _activeSocksPort != socksPort)
            {

                WriteDiag($"socks port changed ({_activeSocksPort}→{socksPort}); restarting tun core");
                await StopSupervisorAsync();
                StartSupervisor(socksPort);
                _activeSplitHash = splitHash;
            }
            else if (wantStart && have
                     && _activeSocksPort == socksPort
                     && _activeSplitHash != splitHash)
            {

                WriteDiag("split rules changed; re-applying routes live");
                await ReapplyRoutesAsync(socksPort);
                _activeSplitHash = splitHash;
            }
            else if (!wantKeep && have)
            {
                await StopSupervisorAsync();
            }
            else if (!wantKeep && State == TunState.Error)
            {
                SetState(TunState.Off, error: null);
            }

            var isChainedConnecting = IsChainedMethod(_settings.Settings.ConnectionMethod)
                && tunnelState == ConnectionState.Connecting
                && !wantStart;

            if (State is TunState.Starting or TunState.Running)
            {
                if (wantStart)
                {
                    if (!ResumeCatchAllRoutes())
                    {
                        WriteDiag("catch-all could not be fully restored; rebuilding the tun session");
                        await StopSupervisorAsync();
                        StartSupervisor(socksPort);
                        _activeSplitHash = splitHash;
                    }
                }
                else if (wantKeep && !isChainedConnecting)
                {
                    if (!_settings.Settings.KillSwitchEnabled)
                    {
                        SuspendCatchAllRoutes();
                    }
                    else
                    {
                        WriteDiag("kill switch enabled: keeping catch-all routes intact during re-dial to prevent leak");
                    }
                }
                else if (wantKeep && isChainedConnecting)
                {
                    WriteDiag("chained session still establishing outer leg; TUN reconcile deferred until fully connected");
                }

                SuppressSystemProxy();
            }
            else if (!have && isChainedConnecting)
            {
                WriteDiag("chained outer still scanning; TUN bring-up deferred");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WintunTunManager.ReconcileAsync failed");
            SetError(ex.Message);
        }
        finally
        {
            _reconcileGate.Release();
        }
    }

    private void StartSupervisor(int socksPort)
    {
        if (!AdminElevation.IsAdministrator())
        {
            SetError("System-wide tunneling needs Administrator privileges. "
                     + "Restart Se7en Pro as Administrator and try again.");
            return;
        }

        var cts = new CancellationTokenSource();
        lock (_lock)
        {
            _supervisorCts?.Cancel();
            _supervisorCts = cts;
        }

        _activeSocksPort = socksPort;
        SetState(TunState.Starting, error: null);
        _supervisorTask = Task.Run(() => SuperviseAsync(socksPort, cts.Token));
    }

    private static bool IsChainedMethod(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        var t = token.Trim().ToLowerInvariant();
        return t is "psiphon_over_warp" or "pow" or "chain"
            or "tor_over_warp" or "tow"
            or "psiphon_over_v2ray" or "pov" or "tor_over_v2ray" or "tov";
    }

    private async Task SuperviseAsync(int socksPort, CancellationToken ct)
    {
        var restarts = 0;
        var windowStart = DateTime.UtcNow;

        while (!ct.IsCancellationRequested)
        {
            if (DateTime.UtcNow - windowStart > SupervisorRestartWindow)
            {
                restarts = 0;
                windowStart = DateTime.UtcNow;
            }

            if (restarts >= SupervisorMaxRestartsInWindow)
            {
                SetError($"The TUN core keeps exiting (≥{SupervisorMaxRestartsInWindow} times in "
                         + $"{SupervisorRestartWindow.TotalMinutes:0} min). Last output: "
                         + $"{DescribeRecentOutput()}. Full log: {_logPath}");
                return;
            }

            var success = await StartTunAndWaitForReadyAsync(socksPort, ct);
            if (ct.IsCancellationRequested) return;

            if (success)
            {
                SetState(TunState.Running, error: null);
                var startedAt = DateTime.UtcNow;
                await WaitForProcessExitAsync(ct);
                if (ct.IsCancellationRequested) return;

                if (DateTime.UtcNow - startedAt > TimeSpan.FromMinutes(1))
                {
                    restarts = 0;
                    windowStart = DateTime.UtcNow;
                }
            }

            await KillTunAsync();
            if (ct.IsCancellationRequested) return;

            restarts++;
            var backoff = TimeSpan.FromSeconds(
                Math.Min(Math.Pow(2, restarts - 1), SupervisorMaxBackoff.TotalSeconds));
            WriteDiag($"tun core exited (restart #{restarts}); backing off {backoff.TotalSeconds:0}s");
            SetState(TunState.Starting, error: null);

            try { await Task.Delay(backoff, ct); }
            catch (OperationCanceledException) { return; }
        }
    }
}
