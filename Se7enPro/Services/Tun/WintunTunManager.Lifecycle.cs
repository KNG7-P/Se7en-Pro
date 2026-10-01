using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Se7enPro.Models;

namespace Se7enPro.Services;

public sealed partial class WintunTunManager
{
    private async Task StopSupervisorAsync()
    {
        CancellationTokenSource? cts;
        Task? task;
        lock (_lock)
        {
            cts = _supervisorCts;
            task = _supervisorTask;
            _supervisorCts = null;
            _supervisorTask = null;
        }

        SetState(TunState.Stopping, error: null);
        var startedAt = DateTime.UtcNow;

        try { cts?.Cancel(); } catch { }

        if (task is not null)
        {
            try { await task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (TimeoutException) { WriteDiag("supervisor did not exit within 5s after cancel"); }
            catch { }
        }
        try { cts?.Dispose(); } catch { }

        await KillTunAsync().ConfigureAwait(false);
        _activeSocksPort = 0;
        _activeSplitHash = "";
        WriteDiag($"stop completed in {(DateTime.UtcNow - startedAt).TotalMilliseconds:0} ms");
        SetState(TunState.Off, error: null);
    }

    private async Task WaitForProcessExitAsync(CancellationToken ct)
    {
        Process? proc;
        lock (_lock) proc = _process;
        if (proc is null) return;

        try
        {
            await proc.WaitForExitAsync(ct);
            WriteDiag($"tun core exited (code={proc.ExitCode}) while supervisor was watching");
        }
        catch (OperationCanceledException) { }
    }

        private async Task KillTunAsync()
    {
        Process? proc;
        StreamWriter? writer;
        lock (_lock)
        {
            proc = _process;
            writer = _logWriter;
            _process = null;
            _logWriter = null;
        }

        WintunRouteApi.RouteEntry[] routes;
        SocksDnsForwarder? forwarder;
        bool dnsSet;
        CancellationTokenSource? refresherCts;
        Task? refresherTask;
        CancellationTokenSource? processSplitCts;
        Task? processSplitTask;
        lock (_routeLock)
        {
            routes = _appliedRoutes.ToArray();
            _appliedRoutes.Clear();
            _dynamicRoutes.Clear();
            forwarder = _dnsForwarder;
            _dnsForwarder = null;
            dnsSet = _adapterDnsSet;
            _adapterDnsSet = false;
            refresherCts = _refresherCts;
            refresherTask = _refresherTask;
            _refresherCts = null;
            _refresherTask = null;
            processSplitCts = _processSplitCts;
            processSplitTask = _processSplitTask;
            _processSplitCts = null;
            _processSplitTask = null;
        }
        _realRouteKnown = false;
        
        
        
        
        
        _realIfIndex = 0;
        _realGateway = IPAddress.Any;
        _tunIfIndex = 0;

        try { refresherCts?.Cancel(); } catch { }
        try { processSplitCts?.Cancel(); } catch { }
        if (refresherTask is not null)
        {
            try { await refresherTask.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch { }
        }
        if (processSplitTask is not null)
        {
            try { await processSplitTask.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch { }
        }
        
        
        try { refresherCts?.Dispose(); } catch { }
        try { processSplitCts?.Dispose(); } catch { }

        foreach (var r in routes)
        {
            try { WintunRouteApi.DeleteRoute(r); }
            catch {  }
        }

        try { forwarder?.Stop(); } catch { }
        try { forwarder?.Dispose(); } catch { }
        if (dnsSet)
        {
            try { await WintunDnsShell.ClearAdapterDnsAsync(TunInterfaceName); } catch { }
        }
        WintunRouteApi.FlushDnsCache();

        if (proc is not null)
        {
            try
            {
                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    try { await proc.WaitForExitAsync(timeout.Token); } catch { }
                }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "tun core kill failed"); }
            finally { proc.Dispose(); }
        }

        
        
        for (var i = 0; i < (int)(AdapterDownWait.TotalSeconds * 10); i++)
        {
            if (!WintunRouteApi.IsAdapterUp(TunInterfaceName)) break;
            await Task.Delay(100);
        }

        try { writer?.Dispose(); } catch { }

        RestoreSystemProxyAfterTun();
    }

        private void SuppressSystemProxy()
    {
        if (!_settings.Settings.SetSystemProxy) return;
        try
        {
            _systemProxy.Clear();
            if (!_proxySuppressedByTun)
            {
                _proxySuppressedByTun = true;
                WriteDiag("system proxy suppressed while the TUN owns system-wide traffic");
            }
        }
        catch (Exception ex)
        {
            WriteDiag($"proxy suppress failed: {ex.Message}");
        }
    }

        private void RestoreSystemProxyAfterTun()
    {
        if (!_proxySuppressedByTun) return;
        _proxySuppressedByTun = false;
        try
        {
            if (_settings.Settings.SetSystemProxy
                && _tunnel.State == ConnectionState.Connected
                && _tunnel.HttpProxyPort > 0)
            {
                _systemProxy.Set(_tunnel.HttpProxyPort);
                WriteDiag("system proxy restored — the tunnel still holds the connection");
            }
        }
        catch (Exception ex)
        {
            WriteDiag($"proxy restore failed: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _tunnel.StateChanged -= OnTunnelStateChanged;
        _settings.SettingsChanged -= OnSettingsChanged;

        CancellationTokenSource? cts;
        Task? task;
        lock (_lock)
        {
            cts = _supervisorCts;
            task = _supervisorTask;
            _supervisorCts = null;
            _supervisorTask = null;
        }

        try { cts?.Cancel(); } catch { }
        if (task is not null)
        {
            
            
            
            
            
            
            
            
            try { await task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch { }
        }
        try { await KillTunAsync().ConfigureAwait(false); } catch { }
        cts?.Dispose();

        
        
        
    }

    private void SetState(TunState s, string? error)
    {
        if (State == s && LastError == error) return;
        State = s;
        LastError = error;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetError(string message)
    {
        WriteDiag("ERROR: " + message);
        _logger.LogWarning("WintunTunManager: {Message}", message);
        SetState(TunState.Error, message);
    }
}
