using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Se7enPro.Models;

namespace Se7enPro.Services;

public sealed class ConnectionManager : ITunnelCoreManager
{
    private const int MaxLogLines = 5000;

    private readonly ILogger<ConnectionManager> _logger;
    private readonly ISettingsService _settings;
    private readonly ISystemProxyService _systemProxy;
    private readonly ILoggerFactory _loggerFactory;

    private readonly TunnelCoreManager _psiphon;
    private readonly AetherEngine _aether;
    private readonly TorEngine _tor;
    private ChainedEngine? _psiphonOverWarp;
    private ChainedEngine? _torOverWarp;

    private readonly object _sync = new();
    private readonly List<string> _recentLog = new();

    private IConnectionEngine _active;
    private bool _systemProxyApplied;

    public ConnectionManager(
        ILogger<ConnectionManager> logger,
        ILoggerFactory loggerFactory,
        ISettingsService settings,
        ISystemProxyService systemProxy,
        TunnelCoreManager psiphon,
        AetherEngine aether,
        TorEngine tor)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _settings = settings;
        _systemProxy = systemProxy;
        _psiphon = psiphon;
        _aether = aether;
        _tor = tor;

        _active = SelectEngineForCurrentSettings();
        Attach(_active);
    }

    public ConnectionState State => _active.State;
    public int SocksProxyPort => _active.SocksProxyPort;
    public int HttpProxyPort => _active.HttpProxyPort;
    public string ClientRegion => _active.ClientRegion;
    public string ConnectedServerRegion => _active.ConnectedServerRegion;
    public string CurrentRouteIp => _active.CurrentRouteIp;
    public string CurrentRouteSni => _active.CurrentRouteSni;
    private long _cachedBytesSent;
    private long _cachedBytesReceived;
    private CancellationTokenSource? _statsCts;
    private NetworkInterface? _cachedTunNic;

    public long BytesSent => _cachedBytesSent > 0 ? _cachedBytesSent : _active.BytesSent;
    public long BytesReceived => _cachedBytesReceived > 0 ? _cachedBytesReceived : _active.BytesReceived;

    private void StartStatsMonitor()
    {
        StopStatsMonitor();
        _statsCts = new CancellationTokenSource();
        var ct = _statsCts.Token;

        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (State == ConnectionState.Connected)
                    {
                        UpdateInterfaceStats();
                    }
                }
                catch { }

                try { await Task.Delay(1000, ct); }
                catch (OperationCanceledException) { break; }
            }
        }, ct);
    }

    private void StopStatsMonitor()
    {
        _statsCts?.Cancel();
        _statsCts = null;
        _cachedTunNic = null;
        _cachedBytesSent = 0;
        _cachedBytesReceived = 0;
    }

    private void UpdateInterfaceStats()
    {
        try
        {
            var nic = _cachedTunNic;
            if (nic is null || nic.OperationalStatus != OperationalStatus.Up)
            {
                _cachedTunNic = NetworkInterface.GetAllNetworkInterfaces()
                    .FirstOrDefault(n => n.OperationalStatus == OperationalStatus.Up &&
                        (n.Name == "se7en_tun" || (n.Description != null && n.Description.Contains("Wintun", StringComparison.OrdinalIgnoreCase))));
                nic = _cachedTunNic;
            }

            if (nic is not null)
            {
                var stats = nic.GetIPStatistics();
                var rx = stats.BytesReceived;
                var tx = stats.BytesSent;
                if (rx > 0 || tx > 0)
                {
                    _cachedBytesReceived = rx;
                    _cachedBytesSent = tx;
                    BytesTransferredChanged?.Invoke(this, EventArgs.Empty);
                    return;
                }
            }

            _cachedBytesSent = _active.BytesSent;
            _cachedBytesReceived = _active.BytesReceived;
            BytesTransferredChanged?.Invoke(this, EventArgs.Empty);
        }
        catch { }
    }

    public IReadOnlyList<string> AvailableEgressRegions => _active.AvailableEgressRegions;
    public int ConnectProgressPercent => _active.ConnectProgressPercent;
    public string ConnectProgressText => _active.ConnectProgressText;

    public ConnectionMethod ActiveMethod => _active.Method;

    public IReadOnlyList<string> RecentLog
    {
        get { lock (_sync) return _recentLog.ToArray(); }
    }

    public event EventHandler<ConnectionState>? StateChanged;
    public event EventHandler<Notice>? NoticeReceived;
    public event EventHandler<string>? LogLineAppended;
    public event EventHandler? BytesTransferredChanged;
    public event EventHandler? LogCleared;
    public event EventHandler? RouteChanged;
    public event EventHandler? ConnectProgressChanged;

    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);

    public async Task StartAsync()
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            var desired = SelectEngineForCurrentSettings();

            if (!ReferenceEquals(_active, desired))
            {

                await SafeStopAsync(_active);
                SwitchActiveTo(desired);
            }

            await _active.StartAsync();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            await SafeStopAsync(_active);
            ClearSystemProxyIfApplied();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task RestartAsync()
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            var desired = SelectEngineForCurrentSettings();
            var running = _active.State is ConnectionState.Connecting or ConnectionState.Connected;

            if (!running)
            {
                if (!ReferenceEquals(_active, desired)) SwitchActiveTo(desired);
                return;
            }

            if (!ReferenceEquals(_active, desired))
            {
                _logger.LogInformation("Switching engine {From} -> {To}",
                    _active.Method, desired.Method);
                await SafeStopAsync(_active);
                SwitchActiveTo(desired);
                await _active.StartAsync();
            }
            else
            {
                await SafeStopAsync(_active);
                await _active.StartAsync();
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private static async Task SafeStopAsync(IConnectionEngine engine)
    {
        try { await engine.StopAsync(); } catch {  }
    }

    private void ApplySystemProxy(ConnectionState state)
    {
        try
        {
            if (!_settings.Settings.SetSystemProxy || _settings.Settings.SystemWideTunneling)
            {
                ClearSystemProxyIfApplied();
                return;
            }

            if (state == ConnectionState.Connected)
            {
                var port = _active.HttpProxyPort;
                if (port <= 0)
                {
                    _logger.LogWarning(
                        "{Method} exposes no HTTP proxy port; system proxy not set",
                        _active.Method);
                    return;
                }
                _systemProxy.Set(port);
                _systemProxyApplied = true;
                WintunRouteApi.ResetLocalLoopbackConnections(port, _active.SocksProxyPort, 1819, 1820, 1821);
                OnEngineLogLineAppended(_active,
                    $"System proxy pointed at 127.0.0.1:{port} — apps that honor it now go "
                  + "through the tunnel.");
            }
            else if (state is ConnectionState.Disconnected or ConnectionState.Error)
            {
                ClearSystemProxyIfApplied();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update the Windows system proxy");
        }
    }

    private void ClearSystemProxyIfApplied()
    {
        if (!_systemProxyApplied) return;
        _systemProxyApplied = false;
        try { _systemProxy.Clear(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to clear the Windows system proxy"); }
    }

    private IConnectionEngine SelectEngineForCurrentSettings()
    {
        var method = ConnectionMethodExtensions.ParseConnectionMethod(_settings.Settings.ConnectionMethod);
        return method switch
        {
            ConnectionMethod.Tor => _tor,
            ConnectionMethod.Masque or ConnectionMethod.WireGuard or ConnectionMethod.WarpOnWarp => _aether,
            ConnectionMethod.PsiphonOverWarp => _psiphonOverWarp ??= new ChainedEngine(
                _loggerFactory.CreateLogger<ChainedEngine>(), _settings, _aether, _psiphon, _tor, ConnectionMethod.PsiphonOverWarp),
            ConnectionMethod.TorOverWarp => _torOverWarp ??= new ChainedEngine(
                _loggerFactory.CreateLogger<ChainedEngine>(), _settings, _aether, _psiphon, _tor, ConnectionMethod.TorOverWarp),
            _ => _psiphon,
        };
    }

    private void SwitchActiveTo(IConnectionEngine engine)
    {
        if (ReferenceEquals(_active, engine)) return;

        ClearSystemProxyIfApplied();

        Detach(_active);
        _active = engine;
        Attach(_active);

        lock (_sync) _recentLog.Clear();
        LogCleared?.Invoke(this, EventArgs.Empty);

        StateChanged?.Invoke(this, _active.State);
        BytesTransferredChanged?.Invoke(this, EventArgs.Empty);
        RouteChanged?.Invoke(this, EventArgs.Empty);
        ConnectProgressChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Attach(IConnectionEngine engine)
    {
        engine.StateChanged += OnEngineStateChanged;
        engine.NoticeReceived += OnEngineNoticeReceived;
        engine.LogLineAppended += OnEngineLogLineAppended;
        engine.BytesTransferredChanged += OnEngineBytesChanged;
        engine.RouteChanged += OnEngineRouteChanged;
        engine.ConnectProgressChanged += OnEngineConnectProgressChanged;
    }

    private void Detach(IConnectionEngine engine)
    {
        engine.StateChanged -= OnEngineStateChanged;
        engine.NoticeReceived -= OnEngineNoticeReceived;
        engine.LogLineAppended -= OnEngineLogLineAppended;
        engine.BytesTransferredChanged -= OnEngineBytesChanged;
        engine.RouteChanged -= OnEngineRouteChanged;
        engine.ConnectProgressChanged -= OnEngineConnectProgressChanged;
    }

    private void OnEngineConnectProgressChanged(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, _active)) return;
        ConnectProgressChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnEngineStateChanged(object? sender, ConnectionState state)
    {
        if (!ReferenceEquals(sender, _active)) return;
        if (state == ConnectionState.Connected)
        {
            StartStatsMonitor();
        }
        else
        {
            StopStatsMonitor();
        }
        ApplySystemProxy(state);
        StateChanged?.Invoke(this, state);
    }

    private void OnEngineNoticeReceived(object? sender, Notice notice)
    {
        if (!ReferenceEquals(sender, _active)) return;
        NoticeReceived?.Invoke(this, notice);
    }

    private void OnEngineLogLineAppended(object? sender, string line)
    {
        if (!ReferenceEquals(sender, _active)) return;
        lock (_sync)
        {
            _recentLog.Add($"{DateTime.Now:HH:mm:ss} {line}");
            if (_recentLog.Count > MaxLogLines)
            {
                _recentLog.RemoveRange(0, _recentLog.Count - MaxLogLines);
            }
        }
        LogLineAppended?.Invoke(this, line);
    }

    private void OnEngineBytesChanged(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, _active)) return;
        BytesTransferredChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnEngineRouteChanged(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, _active)) return;
        RouteChanged?.Invoke(this, EventArgs.Empty);
    }
}
