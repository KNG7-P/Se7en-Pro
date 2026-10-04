using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    private readonly V2RayEngine _v2ray;
    private readonly ShardEngine _shard;
    private readonly IdentityProvisioner _identity;
    private ChainedEngine? _psiphonOverWarp;
    private ChainedEngine? _torOverWarp;
    private ChainedEngine? _psiphonOverV2Ray;
    private ChainedEngine? _torOverV2Ray;

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
        TorEngine tor,
        V2RayEngine v2ray,
        ShardEngine shard,
        IdentityProvisioner identityProvisioner)
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
        _identity = identityProvisioner;

        _active = SelectEngineForCurrentSettings();
        Attach(_active);

        _settings.SettingsChanged += (_, _) =>
        {
            if (State is ConnectionState.Disconnected or ConnectionState.Error)
            {
                var desired = SelectEngineForCurrentSettings();
                if (!ReferenceEquals(_active, desired))
                {
                    SwitchActiveTo(desired);
                }
            }
        };
    }

    

    private ConnectionState? _stateOverride;

    
    
    
    
    public ConnectionState State
    {
        get { lock (_sync) return _stateOverride ?? _active.State; }
    }
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
    private int _isHandlingDrop;

    
    
    
    private bool _userWantsConnection;

    public long BytesSent => Math.Max(_cachedBytesSent, _active.BytesSent);
    public long BytesReceived => Math.Max(_cachedBytesReceived, _active.BytesReceived);

    private void StartStatsMonitor()
    {
        StopStatsMonitor();
        _statsCts = new CancellationTokenSource();
        var ct = _statsCts.Token;

        _ = Task.Run(async () =>
        {
            var lastTrafficActivityUtc = DateTime.UtcNow;
            var lastProbeUtc = DateTime.MinValue;
            long lastKnownBytes = BytesSent + BytesReceived;
            int consecutiveProbeFailures = 0;

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (State == ConnectionState.Connected)
                    {
                        UpdateInterfaceStats();

                        var currentBytes = BytesSent + BytesReceived;
                        if (currentBytes > lastKnownBytes)
                        {
                            lastKnownBytes = currentBytes;
                            lastTrafficActivityUtc = DateTime.UtcNow;
                            consecutiveProbeFailures = 0;
                        }
                        else if (DateTime.UtcNow - lastTrafficActivityUtc >= TimeSpan.FromSeconds(5))
                        {
                            if (DateTime.UtcNow - lastProbeUtc >= TimeSpan.FromSeconds(4))
                            {
                                lastProbeUtc = DateTime.UtcNow;
                                var socksPort = _active.SocksProxyPort;
                                if (socksPort > 0)
                                {
                                    bool healthy = await CheckTunnelHealthAsync(socksPort, ct);
                                    if (healthy)
                                    {
                                        consecutiveProbeFailures = 0;
                                        lastTrafficActivityUtc = DateTime.UtcNow;
                                    }
                                    else
                                    {
                                        consecutiveProbeFailures++;
                                        _logger.LogWarning("Tunnel health probe failed ({Count}/2)", consecutiveProbeFailures);
                                        if (consecutiveProbeFailures >= 2 &&
                                            Interlocked.CompareExchange(ref _isHandlingDrop, 1, 0) == 0)
                                        {
                                            _ = Task.Run(HandleConnectionDropAsync);
                                            break;
                                        }
                                    }
                                }
                            }
                        }
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
        
        
        
        
        var old = Interlocked.Exchange(ref _statsCts, null);
        try { old?.Cancel(); } catch { }
        try { old?.Dispose(); } catch { }
        _cachedTunNic = null;
        _cachedBytesSent = 0;
        _cachedBytesReceived = 0;
        _statsSource = "";
        _statsFailureLogged = false;
        Interlocked.Exchange(ref _isHandlingDrop, 0);
    }

    private void UpdateInterfaceStats()
    {
        try
        {
            
            
            
            
            
            
            var tunOwned = AdminElevation.IsAdministrator() && _settings.Settings.SystemWideTunneling;
            var nic = _cachedTunNic;
            if (!tunOwned)
            {
                if (nic is not null) _cachedTunNic = null;
            }
            else if (nic is null || nic.OperationalStatus != OperationalStatus.Up)
            {
                
                
                
                _cachedTunNic = NetworkInterface.GetAllNetworkInterfaces()
                    .FirstOrDefault(n => n.OperationalStatus == OperationalStatus.Up &&
                        n.Name == "se7en_tun");
                nic = _cachedTunNic;
            }

            if (tunOwned && nic is not null)
            {
                var stats = nic.GetIPStatistics();
                var rx = stats.BytesReceived;
                var tx = stats.BytesSent;
                ResetStatsCacheIfSourceChanged("nic");

                
                
                
                
                
                
                var moved = false;
                if (rx > _cachedBytesReceived) { _cachedBytesReceived = rx; moved = true; }
                if (tx > _cachedBytesSent) { _cachedBytesSent = tx; moved = true; }
                if (moved) BytesTransferredChanged?.Invoke(this, EventArgs.Empty);
                return;
            }

            var activeSent = _active.BytesSent;
            var activeRecv = _active.BytesReceived;
            ResetStatsCacheIfSourceChanged("engine");
            var engineMoved = false;
            if (activeSent > _cachedBytesSent) { _cachedBytesSent = activeSent; engineMoved = true; }
            if (activeRecv > _cachedBytesReceived) { _cachedBytesReceived = activeRecv; engineMoved = true; }
            if (engineMoved) BytesTransferredChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            
            
            if (!_statsFailureLogged)
            {
                _statsFailureLogged = true;
                _logger.LogWarning(ex, "Traffic counters stopped updating");
            }
        }
    }

    private string _statsSource = "";

    
    
    
    private void ResetStatsCacheIfSourceChanged(string source)
    {
        if (_statsSource == source) return;
        _statsSource = source;
        _cachedBytesSent = 0;
        _cachedBytesReceived = 0;
    }

    private static async Task<bool> CheckTunnelHealthAsync(int socksPort, CancellationToken ct)
    {
        if (socksPort <= 0) return false;
        try
        {
            if (await SocksProbe.ProbeConnectAsync(socksPort, "cp.cloudflare.com", 80, ct, TimeSpan.FromSeconds(3)))
            {
                return true;
            }
        }
        catch { }

        if (ct.IsCancellationRequested) return false;

        try
        {
            if (await SocksProbe.ProbeConnectAsync(socksPort, "www.google.com", 80, ct, TimeSpan.FromSeconds(3)))
            {
                return true;
            }
        }
        catch { }

        return false;
    }

    private async Task HandleConnectionDropAsync()
    {
        
        
        
        
        await _lifecycleGate.WaitAsync();
        try
        {
            _logger.LogWarning("Connection drop detected; switching state to reconnecting...");
            OnEngineLogLineAppended(_active, "[Health] Connection drop detected. Reconnecting...");

            _stateOverride = ConnectionState.Connecting;
            StateChanged?.Invoke(this, ConnectionState.Connecting);

            await StopAllEnginesAsync();
            ClearSystemProxyIfApplied();

            bool reconnected = false;
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                
                
                if (!IsUserStillWantsConnection())
                {
                    _stateOverride = null;
                    StateChanged?.Invoke(this, ConnectionState.Disconnected);
                    return;
                }

                try
                {
                    OnEngineLogLineAppended(_active, $"[Health] Reconnect attempt {attempt}/2...");

                    var desired = SelectEngineForCurrentSettings();
                    SwitchActiveTo(desired);
                    await _active.StartAsync();

                    if (_active.State == ConnectionState.Connected)
                    {
                        reconnected = true;
                        _stateOverride = null;
                        StateChanged?.Invoke(this, ConnectionState.Connected);
                        OnEngineLogLineAppended(_active, "[Health] Successfully reconnected.");
                        break;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Health] Reconnect attempt {Attempt} failed", attempt);
                }

                if (!IsUserStillWantsConnection())
                {
                    _stateOverride = null;
                    StateChanged?.Invoke(this, ConnectionState.Disconnected);
                    return;
                }
                await Task.Delay(2000);
            }

            if (!reconnected)
            {
                _stateOverride = ConnectionState.Error;
                ClearSystemProxyIfApplied();
                OnEngineLogLineAppended(_active, "[Health] Reconnection failed. Tunnel disconnected with error.");
                StateChanged?.Invoke(this, ConnectionState.Error);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Health] Error handling connection drop");
            _stateOverride = ConnectionState.Error;
            ClearSystemProxyIfApplied();
            StateChanged?.Invoke(this, ConnectionState.Error);
        }
        finally
        {
            _stateOverride = null;
            Interlocked.Exchange(ref _isHandlingDrop, 0);
            _lifecycleGate.Release();
        }
    }

        private bool IsUserStillWantsConnection()
    {
        lock (_sync)
        {
            return _userWantsConnection;
        }
    }

    private bool _statsFailureLogged;

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
    public event EventHandler<bool>? ConnectionIntentChanged;
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
        _stateOverride = null;
        RaiseConnectionIntent(wantsConnection: true);
        CancellationTokenSource inFlightCts;
        CancellationTokenSource? previousCts;
        lock (_sync)
        {
            previousCts = _inFlightStartCts;
            _inFlightStartCts = new CancellationTokenSource();
            inFlightCts = _inFlightStartCts;
        }
        
        
        try { previousCts?.Cancel(); } catch { }
        try { previousCts?.Dispose(); } catch { }

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

            await _active.StartAsync();
        }
        catch (OperationCanceledException)
        {
            ClearSystemProxyIfApplied();
            StateChanged?.Invoke(this, ConnectionState.Disconnected);
        }
        catch (Exception ex)
        {
            if (inFlightCts.IsCancellationRequested)
            {
                ClearSystemProxyIfApplied();
                StateChanged?.Invoke(this, ConnectionState.Disconnected);
                return;
            }

            if (await _active.RecoverFromMissingPrerequisitesAsync(inFlightCts.Token))
            {
                try
                {
                    await SafeStopAsync(_active);
                    await _active.StartAsync();
                    AetherExtras.ResetEchFallback();
                    return;
                }
                catch (OperationCanceledException) when (inFlightCts.IsCancellationRequested)
                {
                    ClearSystemProxyIfApplied();
                    StateChanged?.Invoke(this, ConnectionState.Disconnected);
                    return;
                }
                catch (Exception retryEx)
                {
                    _logger.LogError(retryEx,
                        "Retry after the identity fallback failed for {Method}", _active.Method);
                    ex = retryEx;
                }
            }

            _logger.LogError(ex, "Failed to start active engine {Method}", _active.Method);
            OnEngineLogLineAppended(_active, $"[Core] Startup error: {ex.Message}");
            ClearSystemProxyIfApplied();
            StateChanged?.Invoke(this, ConnectionState.Error);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopAsync()
    {
        _stateOverride = null;
        StopStatsMonitor();
        RaiseConnectionIntent(wantsConnection: false);
        CancelInFlightConnection();

        await _lifecycleGate.WaitAsync();
        try
        {
            await StopAllEnginesAsync();
            ClearSystemProxyIfApplied();
        }
        finally
        {
            _stateOverride = null;
            _lifecycleGate.Release();
        }
        StateChanged?.Invoke(this, ConnectionState.Disconnected);
    }

    private void RaiseConnectionIntent(bool wantsConnection)
    {
        lock (_sync)
        {
            if (_userWantsConnection == wantsConnection) return;
            _userWantsConnection = wantsConnection;
        }
        try
        {
            ConnectionIntentChanged?.Invoke(this, wantsConnection);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ConnectionIntentChanged handler failed");
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

            _logger.LogInformation("Switching engine {From} -> {To}",
                _active.Method, desired.Method);

            await StopAllEnginesAsync();
            
            
            desired = SelectEngineForCurrentSettings();
            SwitchActiveTo(desired);
            await _active.StartAsync();
        }
        catch (Exception ex)
        {
            
            
            
            _logger.LogError(ex, "Restart failed");
            ClearSystemProxyIfApplied();
            StateChanged?.Invoke(this, ConnectionState.Error);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task StopAllEnginesAsync()
    {
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

        
        
        
        TunnelCoreManager.UpstreamProxyUrlOverride = null;
        TorEngine.Socks5ProxyOverride = null;
        AetherEngine.SocksPortOverride = null;
        AetherEngine.MethodOverride = null;
        V2RayEngine.ClearSocksPortOverride();
        V2RayEngine.ClearUserUpstreamOverride();
    }

    private static async Task SafeStopAsync(IConnectionEngine engine)
    {
        try { await engine.StopAsync(); } catch {  }
    }

    
    
    
    
    
    
    
    
    

    private void ApplySystemProxy(ConnectionState state)
    {
        try
        {
            var isTunActive = _settings.Settings.SystemWideTunneling && AdminElevation.IsAdministrator();
            if (!_settings.Settings.SetSystemProxy || isTunActive)
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
                WintunRouteApi.ResetLocalLoopbackConnections(port, _active.SocksProxyPort, 1819, 1820, 1821, 1824, 1825);
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

    

        private ConnectionMethod ResolveMethod()
    {
        AetherEngine.MethodOverride = null;
        return ConnectionMethodExtensions.ParseConnectionMethod(_settings.Settings.ConnectionMethod);
    }

    private IConnectionEngine SelectEngineForCurrentSettings()
    {
        var method = ResolveMethod();
        return method switch
        {
            ConnectionMethod.Tor => _tor,
            var m when m.IsAether() => _aether,
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
        _stateOverride = null;
        if (!_userWantsConnection && (state == ConnectionState.Connecting || state == ConnectionState.Connected))
        {
            _logger.LogInformation("Ignoring engine state {State} because user has disconnected", state);
            return;
        }
        if (state == ConnectionState.Connected)
        {
            StartStatsMonitor();
            TopUpIdentityPoolInBackground();
        }
        else
        {
            StopStatsMonitor();
        }
        ApplySystemProxy(state);
        StateChanged?.Invoke(this, state);
    }

    /// <summary>
    /// Replenishes the Cloudflare identity pool in the background when connected.
    /// </summary>
    private void TopUpIdentityPoolInBackground()
    {
        var method = ConnectionMethodExtensions.ParseConnectionMethod(_settings.Settings.ConnectionMethod);

        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                await _identity.RefillPoolAsync(method, ct: cts.Token);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Identity pool top-up skipped");
            }
        });
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
