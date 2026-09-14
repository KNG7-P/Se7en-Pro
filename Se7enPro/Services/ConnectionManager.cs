using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
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
    private readonly V2RayEngine _v2ray;
    private readonly ShardEngine _shard;
    private ChainedEngine? _psiphonOverWarp;
    private ChainedEngine? _torOverWarp;
    private ChainedEngine? _psiphonOverV2Ray;
    private ChainedEngine? _torOverV2Ray;

    private readonly object _sync = new();
    private readonly List<string> _recentLog = new();

    private IConnectionEngine _active;

    public ConnectionManager(
        ILogger<ConnectionManager> logger,
        ILoggerFactory loggerFactory,
        ISettingsService settings,
        ISystemProxyService systemProxy,
        TunnelCoreManager psiphon,
        AetherEngine aether,
        TorEngine tor,
        V2RayEngine v2ray,
        ShardEngine shard)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _settings = settings;
        _systemProxy = systemProxy;
        _psiphon = psiphon;
        _aether = aether;
        _tor = tor;
        _v2ray = v2ray;
        _shard = shard;

        _active = SelectEngineForCurrentSettings();
        Attach(_active);
        _settings.SettingsChanged += OnSettingsChanged;
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        if (State == ConnectionState.Connected)
        {
            ApplySystemProxy(State);
        }
        else if (State == ConnectionState.Disconnected)
        {
            var desired = SelectEngineForCurrentSettings();
            if (!ReferenceEquals(_active, desired))
            {
                SwitchActiveTo(desired);
            }
        }
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
    private long _lastNicScanTick;

    public long BytesSent => Math.Max(_cachedBytesSent, _active.BytesSent);
    public long BytesReceived => Math.Max(_cachedBytesReceived, _active.BytesReceived);

    public double DownSpeedBytesPerSec { get; private set; }
    public double UpSpeedBytesPerSec { get; private set; }
    private long _lastSpeedRx;
    private long _lastSpeedTx;
    private DateTime _lastSpeedCalcUtc = DateTime.UtcNow;
    private DateTime _lastActiveSpeedUtc = DateTime.UtcNow;
    private bool _hadSpeed;

    private void StartStatsMonitor()
    {
        StopStatsMonitor();
        _lastSpeedCalcUtc = DateTime.UtcNow;
        _lastActiveSpeedUtc = DateTime.UtcNow;
        _cachedBytesSent = BytesSent;
        _cachedBytesReceived = BytesReceived;
        _lastSpeedRx = BytesReceived;
        _lastSpeedTx = BytesSent;
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

                try { await Task.Delay(500, ct); }
                catch (OperationCanceledException) { break; }
            }
        }, ct);
    }

    private long _lastEventTick;

    private void StopStatsMonitor()
    {
        _statsCts?.Cancel();
        _statsCts = null;
        _cachedTunNic = null;
        _lastNicScanTick = 0;
        _lastEventTick = 0;
        _cachedBytesSent = 0;
        _cachedBytesReceived = 0;
        _lastSpeedRx = 0;
        _lastSpeedTx = 0;
        DownSpeedBytesPerSec = 0;
        UpSpeedBytesPerSec = 0;
        _hadSpeed = false;
    }

    private void UpdateInterfaceStats()
    {
        try
        {
            NetworkInterface? nic = null;
            var isTunActive = _settings.Settings.SystemWideTunneling && AdminElevation.IsAdministrator();
            if (isTunActive)
            {
                nic = _cachedTunNic;
                if (nic is null || (nic.OperationalStatus != OperationalStatus.Up && nic.OperationalStatus != OperationalStatus.Unknown))
                {
                    var nowTick = Environment.TickCount64;

                    if (nowTick - _lastNicScanTick >= 10000)
                    {
                        _lastNicScanTick = nowTick;
                        _cachedTunNic = NetworkInterface.GetAllNetworkInterfaces()
                            .Where(n => n.OperationalStatus is OperationalStatus.Up or OperationalStatus.Unknown)
                            .FirstOrDefault(WintunRouteApi.IsOwnTunAdapter)
                            ?? NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(WintunRouteApi.IsOwnTunAdapter);
                        nic = _cachedTunNic;
                    }
                }
            }

            long rx = 0;
            long tx = 0;
            if (nic is not null)
            {
                try
                {
                    var stats = nic.GetIPStatistics();
                    rx = stats.BytesReceived;
                    tx = stats.BytesSent;
                }
                catch { }
            }

            var activeSent = _active.BytesSent;
            var activeRecv = _active.BytesReceived;

            var bestRx = Math.Max(rx, activeRecv);
            var bestTx = Math.Max(tx, activeSent);

            var changed = false;
            if (bestRx > _cachedBytesReceived)
            {
                _cachedBytesReceived = bestRx;
                changed = true;
            }
            if (bestTx > _cachedBytesSent)
            {
                _cachedBytesSent = bestTx;
                changed = true;
            }

            var now = DateTime.UtcNow;
            var dt = (now - _lastSpeedCalcUtc).TotalSeconds;
            if (dt >= 0.50)
            {
                var dRx = Math.Max(0, bestRx - _lastSpeedRx);
                var dTx = Math.Max(0, bestTx - _lastSpeedTx);
                var instDown = dRx / dt;
                var instUp = dTx / dt;

                if (dRx > 0)
                {
                    DownSpeedBytesPerSec = (DownSpeedBytesPerSec <= 0) ? instDown : (DownSpeedBytesPerSec * 0.35 + instDown * 0.65);
                    _lastActiveSpeedUtc = now;
                }
                else
                {
                    var silence = (now - _lastActiveSpeedUtc).TotalSeconds;
                    if (silence > 1.2)
                    {
                        DownSpeedBytesPerSec *= 0.65;
                        if (DownSpeedBytesPerSec < 64) DownSpeedBytesPerSec = 0;
                    }
                }

                if (dTx > 0)
                {
                    UpSpeedBytesPerSec = (UpSpeedBytesPerSec <= 0) ? instUp : (UpSpeedBytesPerSec * 0.35 + instUp * 0.65);
                    _lastActiveSpeedUtc = now;
                }
                else
                {
                    var silence = (now - _lastActiveSpeedUtc).TotalSeconds;
                    if (silence > 1.2)
                    {
                        UpSpeedBytesPerSec *= 0.65;
                        if (UpSpeedBytesPerSec < 64) UpSpeedBytesPerSec = 0;
                    }
                }

                _lastSpeedRx = bestRx;
                _lastSpeedTx = bestTx;
                _lastSpeedCalcUtc = now;
            }

            var speedActive = DownSpeedBytesPerSec > 0 || UpSpeedBytesPerSec > 0;
            var nowTick64 = Environment.TickCount64;
            if ((changed || speedActive || _hadSpeed) && (nowTick64 - _lastEventTick >= 500))
            {
                _lastEventTick = nowTick64;
                _hadSpeed = speedActive;
                BytesTransferredChanged?.Invoke(this, EventArgs.Empty);
            }
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
    private CancellationTokenSource? _inFlightStartCts;

    public void CancelInFlightConnection()
    {
        lock (_sync)
        {
            _inFlightStartCts?.Cancel();
        }
        try { _active.CancelConnecting(); } catch { }
    }

    public async Task StartAsync()
    {
        CancellationTokenSource inFlightCts;
        lock (_sync)
        {
            _inFlightStartCts?.Cancel();
            _inFlightStartCts = new CancellationTokenSource();
            inFlightCts = _inFlightStartCts;
        }

        await _lifecycleGate.WaitAsync();
        try
        {
            if (inFlightCts.IsCancellationRequested) return;

            var desired = SelectEngineForCurrentSettings();

            if (!ReferenceEquals(_active, desired))
            {

                await SafeStopAsync(_active);
                SwitchActiveTo(desired);
            }

            if (inFlightCts.IsCancellationRequested) return;

            OnEngineLogLineAppended(_active, $"[Core] Connecting to {_active.Method.ToDisplayName()}...");
            StateChanged?.Invoke(this, ConnectionState.Connecting);

            try
            {
                await _active.StartAsync();
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Startup cancelled for {Method}", _active.Method);
                ClearSystemProxyIfApplied();
                StateChanged?.Invoke(this, ConnectionState.Disconnected);
            }
            catch (Exception ex)
            {
                if (inFlightCts.IsCancellationRequested)
                {
                    _logger.LogInformation("Startup cancelled with exception for {Method}", _active.Method);
                    ClearSystemProxyIfApplied();
                    StateChanged?.Invoke(this, ConnectionState.Disconnected);
                    return;
                }
                _logger.LogError(ex, "Failed to start active engine {Method}", _active.Method);
                OnEngineLogLineAppended(_active, $"[Core] Startup error: {ex.Message}");
                ClearSystemProxyIfApplied();
                StateChanged?.Invoke(this, ConnectionState.Error);
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopAsync()
    {
        CancelInFlightConnection();

        await _lifecycleGate.WaitAsync();
        try
        {
            await StopAllEnginesAsync();
            ClearSystemProxyIfApplied();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task RestartAsync()
    {
        var wasActiveOrConnecting = _active.State is ConnectionState.Connecting or ConnectionState.Connected or ConnectionState.Error;
        CancelInFlightConnection();

        await _lifecycleGate.WaitAsync();
        try
        {
            var desired = SelectEngineForCurrentSettings();
            var running = wasActiveOrConnecting || _active.State is ConnectionState.Connecting or ConnectionState.Connected or ConnectionState.Error;

            if (!running)
            {
                if (!ReferenceEquals(_active, desired)) SwitchActiveTo(desired);
                return;
            }

            _logger.LogInformation("Switching engine {From} -> {To}",
                _active.Method, desired.Method);

            await StopAllEnginesAsync();
            ClearSystemProxyIfApplied();
            await Task.Delay(350);

            SwitchActiveTo(desired);
            try
            {
                await _active.StartAsync();
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Restart cancelled for {Method}", _active.Method);
                ClearSystemProxyIfApplied();
                StateChanged?.Invoke(this, ConnectionState.Disconnected);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Restart failed starting {Method}", _active.Method);
                OnEngineLogLineAppended(_active, $"[Core] Restart error: {ex.Message}");
                ClearSystemProxyIfApplied();
                StateChanged?.Invoke(this, ConnectionState.Error);
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task StopAllEnginesAsync()
    {
        CancelInFlightConnection();
        try { _psiphon.CancelConnecting(); } catch { }
        try { _aether.CancelConnecting(); } catch { }
        try { _tor.CancelConnecting(); } catch { }
        try { _v2ray.CancelConnecting(); } catch { }
        try { _shard.CancelConnecting(); } catch { }
        try { _psiphonOverWarp?.CancelConnecting(); } catch { }
        try { _torOverWarp?.CancelConnecting(); } catch { }
        try { _psiphonOverV2Ray?.CancelConnecting(); } catch { }
        try { _torOverV2Ray?.CancelConnecting(); } catch { }

        TunnelCoreManager.ClearUpstreamProxyUrlOverride();
        TorEngine.ClearSocks5ProxyOverride();

        AetherEngine.ClearOverrides();

        var tasks = new List<Task>
        {
            SafeStopAsync(_psiphon),
            SafeStopAsync(_aether),
            SafeStopAsync(_tor),
            SafeStopAsync(_v2ray),
            SafeStopAsync(_shard)
        };
        if (_psiphonOverWarp is not null) tasks.Add(SafeStopAsync(_psiphonOverWarp));
        if (_torOverWarp is not null) tasks.Add(SafeStopAsync(_torOverWarp));
        if (_psiphonOverV2Ray is not null) tasks.Add(SafeStopAsync(_psiphonOverV2Ray));
        if (_torOverV2Ray is not null) tasks.Add(SafeStopAsync(_torOverV2Ray));

        await Task.WhenAll(tasks);
    }

    private static async Task SafeStopAsync(IConnectionEngine engine)
    {
        try { await engine.StopAsync(); } catch {  }
    }

    private int _currentlyAppliedProxyPort;

    private void ApplySystemProxy(ConnectionState state)
    {
        try
        {
            var isTunActive = _settings.Settings.SystemWideTunneling && AdminElevation.IsAdministrator();
            if (!_settings.Settings.SetSystemProxy || isTunActive) { ClearSystemProxyIfApplied(); return; }
            if (state == ConnectionState.Connected)
            {
                var port = _active.HttpProxyPort > 0 ? _active.HttpProxyPort : _active.SocksProxyPort;
                if (port <= 0) { _logger.LogWarning("{Method} exposes no proxy port; system proxy not set", _active.Method); return; }
                if (_currentlyAppliedProxyPort != port)
                {
                    _systemProxy.Set(port);
                    _currentlyAppliedProxyPort = port;
                    OnEngineLogLineAppended(_active, $"System proxy → 127.0.0.1:{port}");
                }
            }
            else
            {
                ClearSystemProxyIfApplied();
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to update the Windows system proxy"); }
    }

    private void ClearSystemProxyIfApplied()
    {
        try
        {
            if (_currentlyAppliedProxyPort != 0)
            {
                _systemProxy.Clear();
                _currentlyAppliedProxyPort = 0;
            }
            else
            {

                _systemProxy.RestoreIfCrashed();
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to clear the Windows system proxy"); }
    }

    private IConnectionEngine SelectEngineForCurrentSettings()
    {
        var method = ConnectionMethodExtensions.ParseConnectionMethod(_settings.Settings.ConnectionMethod);
        return method switch
        {
            ConnectionMethod.Tor => _tor,
            ConnectionMethod.Masque or ConnectionMethod.WireGuard or ConnectionMethod.WarpOnWarp or ConnectionMethod.MasqueOnMasque => _aether,
            ConnectionMethod.PsiphonOverWarp => _psiphonOverWarp ??= new ChainedEngine(
                _loggerFactory.CreateLogger<ChainedEngine>(), _settings, _aether, _psiphon, _tor, _v2ray, ConnectionMethod.PsiphonOverWarp),
            ConnectionMethod.TorOverWarp => _torOverWarp ??= new ChainedEngine(
                _loggerFactory.CreateLogger<ChainedEngine>(), _settings, _aether, _psiphon, _tor, _v2ray, ConnectionMethod.TorOverWarp),
            ConnectionMethod.PsiphonOverV2Ray => _psiphonOverV2Ray ??= new ChainedEngine(
                _loggerFactory.CreateLogger<ChainedEngine>(), _settings, _aether, _psiphon, _tor, _v2ray, ConnectionMethod.PsiphonOverV2Ray),
            ConnectionMethod.TorOverV2Ray => _torOverV2Ray ??= new ChainedEngine(
                _loggerFactory.CreateLogger<ChainedEngine>(), _settings, _aether, _psiphon, _tor, _v2ray, ConnectionMethod.TorOverV2Ray),
            ConnectionMethod.Shard => _shard,
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

        var switchMsg = $"[Core] Switched active protocol to {engine.Method.ToDisplayName()}";
        lock (_sync)
        {
            _recentLog.Clear();
            _recentLog.Add($"{DateTime.Now:HH:mm:ss} {switchMsg}");
        }
        LogCleared?.Invoke(this, EventArgs.Empty);
        LogLineAppended?.Invoke(this, switchMsg);

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
        UpdateInterfaceStats();
    }

    private void OnEngineRouteChanged(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, _active)) return;
        RouteChanged?.Invoke(this, EventArgs.Empty);
    }
}
