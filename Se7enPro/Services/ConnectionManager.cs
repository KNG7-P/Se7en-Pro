using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Se7enPro.Models;
using Se7enPro.Services.Foxy;
using Se7enPro.Services.Lantern;
using Se7enPro.Services.Veep;

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
    private readonly FoxyEngine _foxy;
    private readonly LanternEngine _lantern;
    private readonly VeepEngine _veep;
    private readonly IdentityProvisioner _identity;
    private ChainedEngine? _psiphonOverWarp;
    private ChainedEngine? _torOverWarp;
    private ChainedEngine? _psiphonOverV2Ray;
    private ChainedEngine? _torOverV2Ray;
    private ChainedEngine? _warpOverV2Ray;
    private ChainedEngine? _foxyOverWarp;
    private ChainedEngine? _foxyOverV2Ray;
    private ChainedEngine? _psiphonOverFoxy;
    private ChainedEngine? _psiphonOverLantern;
    private ChainedEngine? _lanternOverWarp;
    private ChainedEngine? _veepOverWarp;

    private readonly object _sync = new();
    private readonly List<string> _recentLog = new();

    private IConnectionEngine _active;
    private bool _systemProxyApplied;
    private string _appliedProxyPlan = "";

    // Proxy mode has no TUN adapter, so the split rules cannot ride on the catch-all
    // route. Block is still enforced with WFP (the same router the TUN path uses);
    // Direct is enforced by keeping the app out of the system proxy. This field holds
    // that router for the proxy path only.
    private WfpSplitRouter? _proxyModeSplitRouter;
    private string _proxyModeSplitSignature = "";

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
        FoxyEngine foxy,
        LanternEngine lantern,
        VeepEngine veep,
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
        _foxy = foxy;
        _lantern = lantern;
        _veep = veep;
        _identity = identityProvisioner;

        _active = SelectEngineForCurrentSettings();
        Attach(_active);

        _settings.SettingsChanged += (_, _) =>
        {
            if (_lifecycleGate.Wait(0))
            {
                try
                {
                    if (State is ConnectionState.Disconnected or ConnectionState.Error)
                    {
                        var desired = SelectEngineForCurrentSettings();
                        if (!ReferenceEquals(_active, desired))
                        {
                            SwitchActiveTo(desired);
                        }
                    }
                }
                finally
                {
                    _lifecycleGate.Release();
                }
            }

            ApplySystemProxy(State);
        };

        try
        {
            Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
        }
        catch { }
    }

    private void OnPowerModeChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode != Microsoft.Win32.PowerModes.Resume || State != ConnectionState.Connected) return;

        var cts = Volatile.Read(ref _statsCts);
        if (cts is null) return;
        var ct = cts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1500, ct);
                if (State != ConnectionState.Connected || ct.IsCancellationRequested) return;
                var socksPort = _active.SocksProxyPort;
                if (socksPort <= 0) return;

                if (!await CheckTunnelHealthAsync(socksPort, ct) && !ct.IsCancellationRequested &&
                    State == ConnectionState.Connected &&
                    Interlocked.CompareExchange(ref _isHandlingDrop, 1, 0) == 0)
                {
                    _logger.LogWarning("Tunnel unhealthy after system resume; triggering drop handler");
                    _ = Task.Run(HandleConnectionDropAsync);
                }
            }
            catch { }
        }, ct);
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
    public string ServerEndpointAddress => _active.ServerEndpointAddress;
    public IReadOnlyList<string> ServerEndpointAddresses => _active.ServerEndpointAddresses;
    private long _cachedBytesSent;
    private long _cachedBytesReceived;
    private CancellationTokenSource? _statsCts;
    private NetworkInterface? _cachedTunNic;
    private int _isHandlingDrop;
    private int _reconnectInProgress;
    private int _identityRecoveryStarted;
    private int _everConnected;




    private bool _userWantsConnection;

    public long BytesSent => Math.Max(_cachedBytesSent, _active.BytesSent);
    public long BytesReceived => Math.Max(_cachedBytesReceived, _active.BytesReceived);

    private void StartStatsMonitor()
    {
        StopStatsMonitor();
        var cts = new CancellationTokenSource();
        var ct = cts.Token;
        var old = Interlocked.Exchange(ref _statsCts, cts);
        try { old?.Cancel(); } catch { }
        try { old?.Dispose(); } catch { }

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
                        else if (DateTime.UtcNow - lastTrafficActivityUtc >= TimeSpan.FromSeconds(45))
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
                                        _logger.LogWarning("Tunnel health probe failed ({Count}/3)", consecutiveProbeFailures);
                                        if (consecutiveProbeFailures >= 3 &&
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

    private async Task<bool> CheckTunnelHealthAsync(int socksPort, CancellationToken ct)
    {
        if (socksPort <= 0) return false;
        var s = _settings.Settings;
        var needAuth = LanPolicy.CanExpose(s) && ActiveMethod is ConnectionMethod.Psiphon or ConnectionMethod.Shard;
        var user = needAuth ? s.LanProxyUsername : null;
        var pass = needAuth ? s.LanProxyPassword : null;

        try
        {
            if (await SocksProbe.ProbeConnectAsync(socksPort, "cp.cloudflare.com", 80, ct, TimeSpan.FromSeconds(3), user, pass))
            {
                return true;
            }
        }
        catch { }

        if (ct.IsCancellationRequested) return false;

        try
        {
            if (await SocksProbe.ProbeConnectAsync(socksPort, "www.google.com", 80, ct, TimeSpan.FromSeconds(3), user, pass))
            {
                return true;
            }
        }
        catch { }

        if (ct.IsCancellationRequested) return false;

        try
        {
            if (await SocksProbe.ProbeHttpsAsync(
                    socksPort, "https://cp.cloudflare.com/generate_204", ct, user, pass))
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
            if (!_settings.Settings.AutoReconnect)
            {
                _logger.LogInformation("Connection drop detected; auto reconnect is off");
                OnEngineLogLineAppended(_active, "[Health] Connection dropped. Auto reconnect is off.");
                Interlocked.Exchange(ref _reconnectInProgress, 1);
                try
                {
                    await StopAllEnginesAsync();
                }
                finally
                {
                    Interlocked.Exchange(ref _reconnectInProgress, 0);
                }
                ClearSystemProxyIfApplied();
                lock (_sync) _stateOverride = ConnectionState.Error;
                StateChanged?.Invoke(this, ConnectionState.Error);
                return;
            }

            _logger.LogWarning("Connection drop detected; switching state to reconnecting...");
            OnEngineLogLineAppended(_active, "[Health] Connection drop detected. Reconnecting...");

            Interlocked.Exchange(ref _reconnectInProgress, 1);
            lock (_sync) _stateOverride = ConnectionState.Connecting;
            StateChanged?.Invoke(this, ConnectionState.Connecting);

            await StopAllEnginesAsync();
            ClearSystemProxyIfApplied();

            bool reconnected = false;
            var attemptBudget = ReconnectAttemptBudget(ResolveMethod());
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                if (!IsUserStillWantsConnection())
                {
                    lock (_sync) _stateOverride = null;
                    Interlocked.Exchange(ref _reconnectInProgress, 0);
                    Interlocked.Exchange(ref _isHandlingDrop, 0);
                    StateChanged?.Invoke(this, ConnectionState.Disconnected);
                    return;
                }

                try
                {
                    OnEngineLogLineAppended(_active, $"[Health] Reconnect attempt {attempt}/2...");

                    var desired = SelectEngineForCurrentSettings();
                    SwitchActiveTo(desired);

                    var outcome = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    EventHandler<ConnectionState> handler = (sender, state) =>
                    {
                        if (!ReferenceEquals(sender, _active)) return;
                        if (state == ConnectionState.Connected) outcome.TrySetResult(true);
                        else if (state is ConnectionState.Error or ConnectionState.Disconnected) outcome.TrySetResult(false);
                    };
                    _active.StateChanged += handler;
                    try
                    {
                        await _active.StartAsync();
                        if (_active.State == ConnectionState.Connected) outcome.TrySetResult(true);

                        var settled = await Task.WhenAny(outcome.Task, Task.Delay(attemptBudget));
                        if (settled == outcome.Task && outcome.Task.Result)
                        {
                            reconnected = true;
                            lock (_sync) _stateOverride = null;
                            Interlocked.Exchange(ref _reconnectInProgress, 0);
                            Interlocked.Exchange(ref _isHandlingDrop, 0);
                            OnEngineLogLineAppended(_active, "[Health] Successfully reconnected.");
                            break;
                        }
                    }
                    finally
                    {
                        _active.StateChanged -= handler;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Health] Reconnect attempt {Attempt} failed", attempt);
                }

                await StopAllEnginesAsync();

                if (!IsUserStillWantsConnection())
                {
                    lock (_sync) _stateOverride = null;
                    Interlocked.Exchange(ref _reconnectInProgress, 0);
                    Interlocked.Exchange(ref _isHandlingDrop, 0);
                    StateChanged?.Invoke(this, ConnectionState.Disconnected);
                    return;
                }
                await Task.Delay(2000);
            }

            if (!reconnected)
            {
                await StopAllEnginesAsync();
                lock (_sync) _stateOverride = ConnectionState.Error;
                ClearSystemProxyIfApplied();
                OnEngineLogLineAppended(_active, "[Health] Reconnection failed. Tunnel disconnected with error.");
                Interlocked.Exchange(ref _reconnectInProgress, 0);
                Interlocked.Exchange(ref _isHandlingDrop, 0);
                StateChanged?.Invoke(this, ConnectionState.Error);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Health] Error handling connection drop");
            lock (_sync) _stateOverride = ConnectionState.Error;
            ClearSystemProxyIfApplied();
            Interlocked.Exchange(ref _reconnectInProgress, 0);
            Interlocked.Exchange(ref _isHandlingDrop, 0);
            StateChanged?.Invoke(this, ConnectionState.Error);
        }
        finally
        {
            Interlocked.Exchange(ref _reconnectInProgress, 0);
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

    private static TimeSpan ReconnectAttemptBudget(ConnectionMethod method) =>
        method.IsChained() ? TimeSpan.FromSeconds(300)
        : method.IsAether() ? TimeSpan.FromSeconds(180)
        : TimeSpan.FromSeconds(90);

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
        Volatile.Write(ref _identityRecoveryStarted, 0);
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
        Volatile.Write(ref _everConnected, 0);
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
            var currentState = State;
            var running = currentState is ConnectionState.Connecting or ConnectionState.Connected;

            if (!running)
            {
                if (!ReferenceEquals(_active, desired)) SwitchActiveTo(desired);
                return;
            }

            _logger.LogInformation("Switching engine {From} -> {To}",
                _active.Method, desired.Method);

            Interlocked.Exchange(ref _reconnectInProgress, 1);
            lock (_sync) _stateOverride = ConnectionState.Connecting;
            StateChanged?.Invoke(this, ConnectionState.Connecting);
            try
            {
                await StopAllEnginesAsync();
                desired = SelectEngineForCurrentSettings();
                SwitchActiveTo(desired);
            }
            finally
            {
                lock (_sync) _stateOverride = null;
                Interlocked.Exchange(ref _reconnectInProgress, 0);
            }

            await _active.StartAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Restart failed");
            ClearSystemProxyIfApplied();
            lock (_sync) _stateOverride = ConnectionState.Error;
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
            SafeStopAsync(_shard),
            SafeStopAsync(_foxy),
            SafeStopAsync(_lantern),
            SafeStopAsync(_veep)
        };
        if (_psiphonOverWarp is not null) tasks.Add(SafeStopAsync(_psiphonOverWarp));
        if (_torOverWarp is not null) tasks.Add(SafeStopAsync(_torOverWarp));
        if (_psiphonOverV2Ray is not null) tasks.Add(SafeStopAsync(_psiphonOverV2Ray));
        if (_torOverV2Ray is not null) tasks.Add(SafeStopAsync(_torOverV2Ray));
        if (_warpOverV2Ray is not null) tasks.Add(SafeStopAsync(_warpOverV2Ray));
        if (_foxyOverWarp is not null) tasks.Add(SafeStopAsync(_foxyOverWarp));
        if (_foxyOverV2Ray is not null) tasks.Add(SafeStopAsync(_foxyOverV2Ray));
        if (_psiphonOverFoxy is not null) tasks.Add(SafeStopAsync(_psiphonOverFoxy));
        if (_psiphonOverLantern is not null) tasks.Add(SafeStopAsync(_psiphonOverLantern));
        if (_lanternOverWarp is not null) tasks.Add(SafeStopAsync(_lanternOverWarp));
        if (_veepOverWarp is not null) tasks.Add(SafeStopAsync(_veepOverWarp));

        await Task.WhenAll(tasks);




        TunnelCoreManager.UpstreamProxyUrlOverride = null;
        TorEngine.Socks5ProxyOverride = null;
        AetherEngine.SocksPortOverride = null;
        AetherEngine.MethodOverride = null;
        AetherEngine.UpstreamProxyOverride = null;
        V2RayEngine.ClearSocksPortOverride();
        V2RayEngine.ClearUserUpstreamOverride();
        FoxyEngine.UpstreamProxyOverride = null;
        FoxyEngine.PreferDualSessionPool = false;
        LanternEngine.UpstreamProxyOverride = null;
        VeepEngine.UpstreamProxyOverride = null;
    }

    private static async Task SafeStopAsync(IConnectionEngine engine)
    {
        try { await engine.StopAsync(); } catch { }
    }











    private void ApplySystemProxy(ConnectionState state)
    {
        try
        {
            var s = _settings.Settings;
            var isTunActive = s.SystemWideTunneling && AdminElevation.IsAdministrator();
            var wantProxy = state == ConnectionState.Connected && s.SetSystemProxy && !isTunActive;

            if (wantProxy)
            {
                var port = _active.HttpProxyPort;
                if (port <= 0)
                {
                    _logger.LogWarning(
                        "{Method} exposes no HTTP proxy port; system proxy not set",
                        _active.Method);
                    return;
                }

                List<string>? bypassList = null;
                if (s.SplitTunnelEnabled)
                {
                    // System-proxy bypass is the Bypass mode, nothing else: a Through Tunnel
                    // or Block domain must not be excluded from the proxy here.
                    var splitPlan = SplitRoutingPlanner.Build(s);

                    // --- Proxy mode split enforcement -------------------------------
                    // Without a TUN adapter the catch-all cannot decide anything, so:
                    //   Block  -> WFP filters (same router as the TUN path)
                    //   Direct -> kept out of the system proxy below
                    //   Proxy  -> the system proxy itself
                    ApplyProxyModeSplitEnforcement(splitPlan);

                    var domains = splitPlan.BypassDomains;
                    var ipCidrs = splitPlan.DirectIpCidrs;
                    bypassList = new List<string>();
                    foreach (var d in domains)
                    {
                        if (d.Contains('.'))
                        {
                            var clean = d.TrimStart('*', '.');
                            bypassList.Add($"*.{clean}");
                            bypassList.Add(clean);
                        }
                        else
                        {
                            bypassList.Add($"*.{d}");
                        }
                    }
                    foreach (var ip in ipCidrs)
                    {
                        var slash = ip.IndexOf('/');
                        bypassList.Add(slash > 0 ? ip[..slash] : ip);
                    }
                }

                var splitSig = bypassList is not null ? string.Join(",", bypassList) : "";
                var plan = $"set:{port}:{splitSig}";
                if (plan == _appliedProxyPlan) return;
                _appliedProxyPlan = plan;

                _systemProxy.Set(port, bypassList);
                _systemProxyApplied = true;
                WintunRouteApi.ResetStaleLoopbackConnections(port, _active.SocksProxyPort, 1819, 1820, 1821, 1824, 1825);
                OnEngineLogLineAppended(_active,
                    $"System proxy pointed at 127.0.0.1:{port} — apps that honor it now go "
                  + "through the tunnel.");
                return;
            }

            if (state is ConnectionState.Disconnected or ConnectionState.Error
                || !s.SetSystemProxy || isTunActive)
            {
                // Leaving proxy mode must remove the WFP filters that belonged to it,
                // otherwise a Block rule would survive a disconnect.
                DisarmProxyModeSplitEnforcement();
                ClearSystemProxyIfApplied();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update the Windows system proxy");
        }
    }

    // ------------------------------------------------------------------ proxy-mode split
    //
    // When system-wide tunnelling is off, the app's traffic reaches the proxy through the
    // Windows system proxy setting instead of a TUN adapter. Split Tunnel still has to
    // work there, so this applies the same three modes by the only means available:
    //
    //   Block  -> WFP BLOCK filters on ALE_AUTH_CONNECT and DATAGRAM_DATA, exactly the
    //             filters the TUN path installs. A blocked app cannot reach the proxy or
    //             the network directly.
    //   Direct -> the app's domains/CIDRs are added to the proxy bypass list, so it
    //             resolves and connects on the physical interface. Its own image is also
    //             blocked at ALE_CONNECT_REDIRECT so a proxy-aware app cannot opt in.
    //   Proxy  -> nothing; the system proxy carries it.
    //
    // Re-applied only when the effective filter set changes.
    private void ApplyProxyModeSplitEnforcement(SplitRoutingPlan plan)
    {
        try
        {
            if (!plan.Enabled)
            {
                DisarmProxyModeSplitEnforcement();
                return;
            }

            var blocked = SplitRuleResolver.ResolvePaths(plan, SplitRuleCategory.Block, ResolveProcessPathByName);
            var direct = SplitRuleResolver.ResolvePaths(plan, SplitRuleCategory.Direct, ResolveProcessPathByName);

            // Proxy mode has no engine rule list either, so default = Block needs the same
            // WFP catch-all the TUN path uses: allowed apps keep their filters and
            // everything else is denied.
            IReadOnlyList<string>? allowedWhenDefaultBlock = null;
            if (plan.DefaultCategory == SplitRuleCategory.Block)
            {
                allowedWhenDefaultBlock = direct
                    .Concat(SplitRuleResolver.ResolvePaths(plan, SplitRuleCategory.Proxy, ResolveProcessPathByName))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            else if (blocked.Count == 0 && direct.Count == 0)
            {
                DisarmProxyModeSplitEnforcement();
                return;
            }

            var blockedCidrs = plan.BlockedIpCidrs
                .OrderBy(static c => c, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var signature = string.Join("|",
                plan.ProfileId,
                allowedWhenDefaultBlock is null ? "-" : "defaultblock",
                string.Join(";", blocked.OrderBy(static p => p, StringComparer.OrdinalIgnoreCase)),
                string.Join(";", direct.OrderBy(static p => p, StringComparer.OrdinalIgnoreCase)),
                string.Join(";", blockedCidrs));

            if (string.Equals(signature, _proxyModeSplitSignature, StringComparison.Ordinal))
            {
                return;
            }

            _proxyModeSplitRouter ??= new WfpSplitRouter();
            if (_proxyModeSplitRouter.Apply(blocked, direct, signature, allowedWhenDefaultBlock, blockedCidrs))
            {
                _proxyModeSplitSignature = signature;
                OnEngineLogLineAppended(_active,
                    $"Split Tunnel (proxy mode): {blocked.Count} blocked, {direct.Count} direct. "
                  + "WFP filters are in force; Direct apps are kept out of the system proxy.");
            }
            else
            {
                _proxyModeSplitSignature = "";
                OnEngineLogLineAppended(_active,
                    $"Split Tunnel (proxy mode): WFP filters not installed — {_proxyModeSplitRouter.LastError}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Proxy-mode split enforcement failed");
        }
    }

    private void DisarmProxyModeSplitEnforcement()
    {
        if (_proxyModeSplitRouter is null) return;
        try { _proxyModeSplitRouter.Disarm(); } catch { }
        _proxyModeSplitSignature = "";
    }

    private string? ResolveProcessPathByName(string processName)
    {
        if (string.IsNullOrEmpty(processName)) return null;

        try
        {
            var stem = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? processName[..^4]
                : processName;

            foreach (var running in System.Diagnostics.Process.GetProcessesByName(stem))
            {
                using (running)
                {
                    try
                    {
                        var path = running.MainModule?.FileName;
                        if (!string.IsNullOrEmpty(path)) return path;
                    }
                    catch
                    {
                        // Protected process; try the next one.
                    }
                }
            }
        }
        catch
        {
            // Process enumeration can fail while the session is changing; a null here
            // just means this name cannot be pinned this round.
        }

        return null;
    }

    private void ClearSystemProxyIfApplied()
    {
        if (!_systemProxyApplied && !_systemProxy.IsApplied) return;
        _systemProxyApplied = false;
        _appliedProxyPlan = "";
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
                _loggerFactory.CreateLogger<ChainedEngine>(), _settings, _aether, _psiphon, _tor, _v2ray, _foxy, _shard, _lantern, _veep, ConnectionMethod.PsiphonOverWarp),
            ConnectionMethod.TorOverWarp => _torOverWarp ??= new ChainedEngine(
                _loggerFactory.CreateLogger<ChainedEngine>(), _settings, _aether, _psiphon, _tor, _v2ray, _foxy, _shard, _lantern, _veep, ConnectionMethod.TorOverWarp),
            ConnectionMethod.PsiphonOverV2Ray => _psiphonOverV2Ray ??= new ChainedEngine(
                _loggerFactory.CreateLogger<ChainedEngine>(), _settings, _aether, _psiphon, _tor, _v2ray, _foxy, _shard, _lantern, _veep, ConnectionMethod.PsiphonOverV2Ray),
            ConnectionMethod.TorOverV2Ray => _torOverV2Ray ??= new ChainedEngine(
                _loggerFactory.CreateLogger<ChainedEngine>(), _settings, _aether, _psiphon, _tor, _v2ray, _foxy, _shard, _lantern, _veep, ConnectionMethod.TorOverV2Ray),
            ConnectionMethod.WarpOverV2Ray => _warpOverV2Ray ??= new ChainedEngine(
                _loggerFactory.CreateLogger<ChainedEngine>(), _settings, _aether, _psiphon, _tor, _v2ray, _foxy, _shard, _lantern, _veep, ConnectionMethod.WarpOverV2Ray),
            ConnectionMethod.Foxy => _foxy,
            ConnectionMethod.Lantern => _lantern,
            ConnectionMethod.LanternOverWarp => _lanternOverWarp ??= new ChainedEngine(
                _loggerFactory.CreateLogger<ChainedEngine>(), _settings, _aether, _psiphon, _tor, _v2ray, _foxy, _shard, _lantern, _veep, ConnectionMethod.LanternOverWarp),
            ConnectionMethod.Veep => _veep,
            ConnectionMethod.VeepOverWarp => _veepOverWarp ??= new ChainedEngine(
                _loggerFactory.CreateLogger<ChainedEngine>(), _settings, _aether, _psiphon, _tor, _v2ray, _foxy, _shard, _lantern, _veep, ConnectionMethod.VeepOverWarp),
            ConnectionMethod.FoxyOverWarp => _foxyOverWarp ??= new ChainedEngine(
                _loggerFactory.CreateLogger<ChainedEngine>(), _settings, _aether, _psiphon, _tor, _v2ray, _foxy, _shard, _lantern, _veep, ConnectionMethod.FoxyOverWarp),
            ConnectionMethod.FoxyOverV2Ray => _foxyOverV2Ray ??= new ChainedEngine(
                _loggerFactory.CreateLogger<ChainedEngine>(), _settings, _aether, _psiphon, _tor, _v2ray, _foxy, _shard, _lantern, _veep, ConnectionMethod.FoxyOverV2Ray),
            ConnectionMethod.Shard => _shard,
            ConnectionMethod.PsiphonOverFoxy => _psiphonOverFoxy ??= new ChainedEngine(
                _loggerFactory.CreateLogger<ChainedEngine>(), _settings, _aether, _psiphon, _tor, _v2ray, _foxy, _shard, _lantern, _veep, ConnectionMethod.PsiphonOverFoxy),
            ConnectionMethod.PsiphonOverLantern => _psiphonOverLantern ??= new ChainedEngine(
                _loggerFactory.CreateLogger<ChainedEngine>(), _settings, _aether, _psiphon, _tor, _v2ray, _foxy, _shard, _lantern, _veep, ConnectionMethod.PsiphonOverLantern),
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


        if (Volatile.Read(ref _reconnectInProgress) == 0)
        {
            StateChanged?.Invoke(this, _active.State);
        }
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

        if (Volatile.Read(ref _reconnectInProgress) != 0)
        {
            if (state is ConnectionState.Disconnected or ConnectionState.Disconnecting or ConnectionState.Error)
            {
                return;
            }
            if (state != ConnectionState.Connected)
            {
                return;
            }
        }

        lock (_sync) _stateOverride = null;
        if (!IsUserStillWantsConnection() && (state == ConnectionState.Connecting || state == ConnectionState.Connected))
        {
            _logger.LogInformation("Ignoring engine state {State} because user has disconnected", state);
            return;
        }
        if (state == ConnectionState.Connected)
        {
            Volatile.Write(ref _everConnected, 1);
            StartStatsMonitor();
            TopUpIdentityPoolInBackground();
        }
        else
        {
            StopStatsMonitor();
        }
        ApplySystemProxy(state);
        StateChanged?.Invoke(this, state);

        if (state == ConnectionState.Error)
        {
            if (Volatile.Read(ref _everConnected) != 0)
            {
                if (_settings.Settings.AutoReconnect
                    && IsUserStillWantsConnection()
                    && Interlocked.CompareExchange(ref _isHandlingDrop, 1, 0) == 0)
                {
                    _ = Task.Run(HandleConnectionDropAsync);
                }
            }
            else
            {
                _ = TryRecoverIdentityAfterErrorAsync();
            }
        }
    }

    private async Task TryRecoverIdentityAfterErrorAsync()
    {
        if (!IsUserStillWantsConnection()) return;
        if (Interlocked.Exchange(ref _identityRecoveryStarted, 1) != 0) return;

        var engine = _active;
        CancellationToken? attemptToken;
        lock (_sync) attemptToken = _inFlightStartCts?.Token;
        if (attemptToken is null)
        {
            Volatile.Write(ref _identityRecoveryStarted, 0);
            return;
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(attemptToken.Value);
            cts.CancelAfter(TimeSpan.FromMinutes(3));
            if (!await engine.RecoverFromMissingPrerequisitesAsync(cts.Token))
            {
                Volatile.Write(ref _identityRecoveryStarted, 0);
                return;
            }

            _logger.LogInformation(
                "Second-chance prerequisites acquired after {Method} failed; retrying the engine", engine.Method);

            await _lifecycleGate.WaitAsync(cts.Token);
            try
            {
                if (cts.IsCancellationRequested || !IsUserStillWantsConnection()
                    || !ReferenceEquals(_active, engine) || engine.State != ConnectionState.Error) return;

                await SafeStopAsync(engine);

                if (cts.IsCancellationRequested || !IsUserStillWantsConnection()
                    || !ReferenceEquals(_active, engine)) return;

                await engine.StartAsync();
                AetherExtras.ResetEchFallback();
            }
            finally
            {
                _lifecycleGate.Release();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Volatile.Write(ref _identityRecoveryStarted, 0);
            _logger.LogError(ex, "Second-chance retry failed for {Method}", engine.Method);
        }
    }

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
