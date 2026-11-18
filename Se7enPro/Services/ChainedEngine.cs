using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Se7enPro.Models;
using Se7enPro.Services.Foxy;
using Se7enPro.Services.Lantern;
using Se7enPro.Services.Veep;

namespace Se7enPro.Services;

public sealed class ChainedEngine : IConnectionEngine, IDisposable
{
    private readonly ILogger<ChainedEngine> _logger;
    private readonly ISettingsService _settings;
    private readonly AetherEngine _outer;
    private readonly TunnelCoreManager _psiphon;
    private readonly TorEngine _tor;
    private readonly V2RayEngine _v2ray;
    private readonly FoxyEngine _foxy;
    private readonly ShardEngine _shard;
    private readonly LanternEngine _lantern;
    private readonly VeepEngine _veep;
    private readonly ConnectionMethod _method;

    private int _outerSocksPort;
    private int _outerV2RaySocksPort;

    
    
    
    private const int ChainedTorInnerBudgetSec = 200;
    private const int ChainedPsiphonInnerBudgetSec = 320;
    private const int ChainedFoxyInnerBudgetSec = 150;
    private const int ChainedLanternInnerBudgetSec = 240;
    private const int ChainedVeepInnerBudgetSec = 60;
    private const int ChainedShardOuterBudgetSec = 180;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _staticLock = new();
    private CancellationTokenSource? _connectCts;
    private Task? _chainTask;
    private readonly List<Action> _eventDetachActions = new();
    private volatile bool _isStopping;
    private volatile bool _hasBeenConnected;
    private volatile bool _stepOneComplete;

    
    
    
    
    private static readonly object _outerOverridesLock = new();
    private static int? _outerSocksPortOverride;
    private static ConnectionMethod? _outerMethodOverride;
    private static string? _outerSocks5ProxyOverride;
    private static string? _outerUpstreamProxyUrlOverride;
    

    public ChainedEngine(
        ILogger<ChainedEngine> logger,
        ISettingsService settings,
        AetherEngine outer,
        TunnelCoreManager psiphon,
        TorEngine tor,
        V2RayEngine v2ray,
        FoxyEngine foxy,
        ShardEngine shard,
        LanternEngine lantern,
        VeepEngine veep,
        ConnectionMethod method)
    {
        _logger = logger;
        _settings = settings;
        _outer = outer;
        _psiphon = psiphon;
        _tor = tor;
        _v2ray = v2ray;
        _foxy = foxy;
        _shard = shard;
        _lantern = lantern;
        _veep = veep;
        _method = method;

        AttachEvents(OuterEngine, isOuter: true);
        AttachEvents(InnerEngine, isOuter: false);
    }

    public ConnectionMethod Method => _method;
    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;

    private IConnectionEngine InnerEngine =>
        _method switch
        {
            ConnectionMethod.TorOverWarp or ConnectionMethod.TorOverV2Ray => _tor,
            ConnectionMethod.WarpOverV2Ray => _outer,
            ConnectionMethod.FoxyOverWarp or ConnectionMethod.FoxyOverV2Ray => _foxy,
            ConnectionMethod.LanternOverWarp => _lantern,
            ConnectionMethod.VeepOverWarp => _veep,
            _ => _psiphon,
        };

        private IConnectionEngine OuterEngine =>
        _method switch
        {
            ConnectionMethod.PsiphonOverV2Ray or ConnectionMethod.TorOverV2Ray
                            or ConnectionMethod.WarpOverV2Ray
                            or ConnectionMethod.FoxyOverV2Ray => _v2ray,
            ConnectionMethod.PsiphonOverFoxy => _foxy,
            ConnectionMethod.PsiphonOverLantern => _lantern,
            _ => _outer,
        };

    private bool IsV2RayOuter =>
        _method is ConnectionMethod.PsiphonOverV2Ray or ConnectionMethod.TorOverV2Ray
                            or ConnectionMethod.WarpOverV2Ray
                            or ConnectionMethod.FoxyOverV2Ray;

    private bool IsWarpOverV2Ray => _method == ConnectionMethod.WarpOverV2Ray;

    private string OuterLegName =>
        IsV2RayOuter ? "V2Ray"
        : _method == ConnectionMethod.PsiphonOverFoxy ? "Foxy"
        : _method == ConnectionMethod.PsiphonOverLantern ? "Lantern"
        : "WARP";

    public int SocksProxyPort => InnerEngine.SocksProxyPort;
    public int HttpProxyPort => InnerEngine.HttpProxyPort;
    public string ClientRegion => InnerEngine.ClientRegion;
    public string ConnectedServerRegion => InnerEngine.ConnectedServerRegion;
    public string CurrentRouteIp
    {
        get
        {
            var inner = InnerEngine.CurrentRouteIp;
            if (!string.IsNullOrEmpty(inner) && !inner.StartsWith("127."))
                return inner;
            var outer = OuterEngine.CurrentRouteIp;
            return !string.IsNullOrEmpty(outer) ? outer : inner;
        }
    }
    public string CurrentRouteSni => InnerEngine.CurrentRouteSni;

    public string ServerEndpointAddress => OuterEngine.ServerEndpointAddress;

    public IReadOnlyList<string> ServerEndpointAddresses => OuterEngine.ServerEndpointAddresses;

    public IReadOnlyList<string> AvailableEgressRegions => InnerEngine.AvailableEgressRegions;
    public long BytesSent => InnerEngine.BytesSent;
    public long BytesReceived => InnerEngine.BytesReceived;

    public int ConnectProgressPercent { get; private set; }
    public string ConnectProgressText { get; private set; } = "";

    public IReadOnlyList<string> CoreProcessNames => EngineProcessNames.All;

    public event EventHandler<ConnectionState>? StateChanged;
    public event EventHandler<Notice>? NoticeReceived;
    public event EventHandler<string>? LogLineAppended;
    public event EventHandler? BytesTransferredChanged;
    public event EventHandler? RouteChanged;
    public event EventHandler? ConnectProgressChanged;

    private void SetState(ConnectionState s)
    {
        if (State == s) return;
        State = s;
        if (s == ConnectionState.Connected) _hasBeenConnected = true;
        StateChanged?.Invoke(this, s);
    }

    private void SetProgress(int percent, string text)
    {
        ConnectProgressPercent = Math.Clamp(percent, 0, 100);
        ConnectProgressText = text;
        try { ConnectProgressChanged?.Invoke(this, EventArgs.Empty); } catch { }
    }

    public Task StartAsync()
    {
        if (State is ConnectionState.Connected or ConnectionState.Connecting)
        {
            Log("Chained session already active; ignoring duplicate connect.");
            return Task.CompletedTask;
        }

        _isStopping = false;
        _connectCts?.Cancel();
        _connectCts = new CancellationTokenSource();
        var ct = _connectCts.Token;

        SetState(ConnectionState.Connecting);
        _chainTask = Task.Run(() => RunChainAsync(ct), ct);
        return Task.CompletedTask;
    }

    private async Task RunChainAsync(CancellationToken ct)
    {
        _hasBeenConnected = false;
        _stepOneComplete = false;
        try
        {
            await _gate.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            if (ct.IsCancellationRequested) return;

            if (_method == ConnectionMethod.PsiphonOverFoxy)
            {
                await RunPsiphonOverHttpEngineAsync(
                    _foxy,
                    "Foxy",
                    "foxy",
                    ChainedFoxyInnerBudgetSec,
                    resetOuterOverrides: () =>
                    {
                        FoxyEngine.UpstreamProxyOverride = null;
                        FoxyEngine.PreferDualSessionPool = true;
                    },
                    ct);
                return;
            }

            if (_method == ConnectionMethod.PsiphonOverLantern)
            {
                await RunPsiphonOverHttpEngineAsync(
                    _lantern,
                    "Lantern",
                    "lantern",
                    ChainedLanternInnerBudgetSec,
                    resetOuterOverrides: () => LanternEngine.UpstreamProxyOverride = null,
                    ct);
                return;
            }

            
            
            
            
            

            if (_method is ConnectionMethod.PsiphonOverV2Ray or ConnectionMethod.TorOverV2Ray
                        or ConnectionMethod.WarpOverV2Ray or ConnectionMethod.FoxyOverV2Ray)
            {
                var v2rayInnerName = _method switch
                {
                    ConnectionMethod.TorOverV2Ray => "Tor",
                    ConnectionMethod.WarpOverV2Ray => "WARP",
                    ConnectionMethod.FoxyOverV2Ray => "Foxy",
                    _ => "Psiphon"
                };

                
                var activeConfig = _v2ray.ResolveActiveConfig();
                if (activeConfig == null)
                {
                    Log($"Cannot start chained {v2rayInnerName} over V2Ray: No active V2Ray/Xray configuration is selected.");
                    await StopInternalAsync();
                    SetState(ConnectionState.Error);
                    return;
                }

                
                
                
                
                if (string.IsNullOrWhiteSpace(activeConfig.Address))
                {
                    Log($"Cannot start chained {v2rayInnerName} over V2Ray: the selected node"
                      + $" \"{NodeLabel(activeConfig)}\" has no server address, so it cannot carry any traffic."
                      + " Edit it in the V2Ray page or pick a node that has one.");
                    await StopInternalAsync();
                    SetState(ConnectionState.Error);
                    return;
                }

                var nodeName = NodeLabel(activeConfig);
                var hopTransport = HopTransport(activeConfig);
                Log($"Starting multi-hop chained session ({v2rayInnerName} over V2Ray), outer hop {hopTransport}...");

                
                
                
                
                
                
                if (_method == ConnectionMethod.TorOverV2Ray && IsPlaintextHop(activeConfig))
                {
                    Log($"\"{nodeName}\" carries the chain unencrypted ({hopTransport}), so Tor's relay"
                      + " handshakes cross the network in the open and anything that blocks them there"
                      + " (your provider, or the network the node runs on) can drop them while normal"
                      + " traffic still passes. Pick a TLS or Reality node (security=tls in the V2Ray"
                      + " page), or run Tor on its own with bridges.");
                }
                SetProgress(10, $"{OuterLegName} [1/2]: Starting {nodeName}...");

                _outerV2RaySocksPort = FindFreeTcpPortPair();
                V2RayEngine.SetSocksPortOverride(_outerV2RaySocksPort);
                await _v2ray.StartAsync();

                var v2rayConnected = false;
                for (var i = 0; i < 60 && !ct.IsCancellationRequested; i++)
                {
                    if (_v2ray.State == ConnectionState.Connected)
                    {
                        v2rayConnected = true;
                        break;
                    }
                    if (_v2ray.State is ConnectionState.Error or ConnectionState.Disconnected)
                    {
                        break;
                    }
                    await Task.Delay(500, ct);
                }

                if (!v2rayConnected)
                {
                    Log("V2Ray outer leg failed to start; aborting chained session.");
                    await StopInternalAsync();
                    SetState(ConnectionState.Error);
                    return;
                }

                var socksPort = _v2ray.SocksProxyPort;
                
                if (!await ProbeOuterSocksReadyAsync(socksPort, ct))
                {
                    Log("V2Ray SOCKS did not answer after reporting Connected; aborting chain.");
                    await StopInternalAsync();
                    SetState(ConnectionState.Error);
                    return;
                }

                
                
                
                
                
                
                
                
                var v2rayOk = await VerifyOuterEgressAsync(socksPort, nodeName, hopTransport, ct);
                if (!v2rayOk)
                {
                    Log($"V2Ray outer hop \"{nodeName}\" did not carry internet traffic; aborting chain.");
                    await StopInternalAsync();
                    SetState(ConnectionState.Error);
                    return;
                }
                _stepOneComplete = true;

                Log($"V2Ray inbound ready on 127.0.0.1:{socksPort}. Starting {v2rayInnerName} through V2Ray tunnel...");
                SetProgress(50, $"{OuterLegName} connected (1/2) -> Connecting to {v2rayInnerName} (2/2)...");

                var v2rayHttpPort = _v2ray.HttpProxyPort > 0 ? _v2ray.HttpProxyPort : socksPort + 1;
                lock (_outerOverridesLock)
                {
                    if (_method == ConnectionMethod.TorOverV2Ray)
                        _outerSocks5ProxyOverride = $"127.0.0.1:{socksPort}";
                    else if (!IsWarpOverV2Ray)
                        _outerUpstreamProxyUrlOverride = _method == ConnectionMethod.PsiphonOverV2Ray
                            ? $"http://127.0.0.1:{v2rayHttpPort}"
                            : $"socks5://127.0.0.1:{socksPort}";
                }

                if (_method == ConnectionMethod.TorOverV2Ray)
                {
                    TorEngine.Socks5ProxyOverride = $"127.0.0.1:{socksPort}";
                    _tor.ReadyTimeoutOverride = TimeSpan.FromSeconds(ChainedTorInnerBudgetSec + 10);
                    await _tor.StartAsync();
                }
                else if (IsWarpOverV2Ray)
                {
                    AetherEngine.UpstreamProxyOverride = $"socks5://127.0.0.1:{socksPort}";
                    await _outer.StartAsync();
                }
                else if (_method == ConnectionMethod.FoxyOverV2Ray)
                {
                    FoxyEngine.UpstreamProxyOverride = $"127.0.0.1:{socksPort}";
                    await _foxy.StartAsync();
                }
                else
                {
                    TunnelCoreManager.UpstreamProxyUrlOverride = $"http://127.0.0.1:{v2rayHttpPort}";
                    await StartInnerPsiphonAsync();
                }

                var v2rayInnerConnected = false;
                var v2rayInnerBudget = _method switch
                {
                    ConnectionMethod.TorOverV2Ray => ChainedTorInnerBudgetSec,
                    ConnectionMethod.WarpOverV2Ray => GetAetherOuterTimeoutSeconds(),
                    ConnectionMethod.FoxyOverV2Ray => ChainedFoxyInnerBudgetSec,
                    _ => ChainedPsiphonInnerBudgetSec,
                };
                var v2rayInnerMax = (int)Math.Ceiling(v2rayInnerBudget * 1000.0 / 500.0);
                for (var i = 0; i < v2rayInnerMax && !ct.IsCancellationRequested; i++)
                {
                    if (_v2ray.State is ConnectionState.Error or ConnectionState.Disconnected)
                    {
                        Log("V2Ray outer leg dropped while inner was connecting; aborting chain.");
                        break;
                    }
                    if (InnerEngine.State == ConnectionState.Connected)
                    {
                        var innerPort = InnerEngine.SocksProxyPort;
                        if (innerPort > 0 && await ProbeOuterSocksReadyAsync(innerPort, ct))
                        {
                            v2rayInnerConnected = true;
                            break;
                        }
                    }
                    if (InnerEngine.State is ConnectionState.Error or ConnectionState.Disconnected)
                    {
                        break;
                    }
                    await Task.Delay(500, ct);
                }

                if (v2rayInnerConnected && !ct.IsCancellationRequested)
                {
                    SetProgress(100, $"Connected ({v2rayInnerName} over V2Ray)");
                    SetState(ConnectionState.Connected);
                    Log($"Multi-hop {v2rayInnerName} over V2Ray successfully connected!");
                    TriggerChainedExitProbe(InnerEngine, ct);
                }
                else
                {
                    if (ct.IsCancellationRequested)
                    {
                        await StopInternalAsync();
                        SetState(ConnectionState.Disconnected);
                        return;
                    }
                    Log($"{v2rayInnerName} failed to connect through V2Ray; stopping."
                      + InnerStallHint(v2rayInnerName, nodeName, hopTransport, IsPlaintextHop(activeConfig)));
                    await StopInternalAsync();
                    SetState(ConnectionState.Error);
                }
                return;
            }

            var isTor = _method == ConnectionMethod.TorOverWarp;
            var isFoxy = _method == ConnectionMethod.FoxyOverWarp;
            var isLantern = _method == ConnectionMethod.LanternOverWarp;
            var isVeep = _method == ConnectionMethod.VeepOverWarp;
            var label = isTor ? "Tor over WARP"
                : isFoxy ? "Foxy over WARP"
                : isLantern ? "Lantern over WARP"
                : isVeep ? "Veep over WARP"
                : "Psiphon over WARP";
            var innerName = isTor ? "Tor" : isFoxy ? "Foxy" : isLantern ? "Lantern" : isVeep ? "Veep" : "Psiphon";

            Log($"Starting multi-hop chained session ({label})...");
            SetProgress(10, $"{OuterLegName} [1/2]: Connecting...");

            
            var outerTransport = isTor
                ? (!string.IsNullOrWhiteSpace(_settings.Settings.ChainedTorOuterTransport)
                    ? _settings.Settings.ChainedTorOuterTransport
                    : (_settings.Settings.ChainedOuterTransport ?? "auto")).Trim().ToLowerInvariant()
                : isFoxy
                    ? (!string.IsNullOrWhiteSpace(_settings.Settings.ChainedFoxyOuterTransport)
                        ? _settings.Settings.ChainedFoxyOuterTransport
                        : (_settings.Settings.ChainedOuterTransport ?? "auto")).Trim().ToLowerInvariant()
                    : isLantern
                    ? (!string.IsNullOrWhiteSpace(_settings.Settings.ChainedLanternOuterTransport)
                        ? _settings.Settings.ChainedLanternOuterTransport
                        : (_settings.Settings.ChainedOuterTransport ?? "auto")).Trim().ToLowerInvariant()
                    : isVeep
                    ? (!string.IsNullOrWhiteSpace(_settings.Settings.ChainedVeepOuterTransport)
                        ? _settings.Settings.ChainedVeepOuterTransport
                        : (_settings.Settings.ChainedOuterTransport ?? "auto")).Trim().ToLowerInvariant()
                    : (!string.IsNullOrWhiteSpace(_settings.Settings.ChainedPsiphonOuterTransport)
                        ? _settings.Settings.ChainedPsiphonOuterTransport
                        : (_settings.Settings.ChainedOuterTransport ?? "auto")).Trim().ToLowerInvariant();

            var preferredAether = (_settings.Settings.AetherProtocol ?? "masque").Trim().ToLowerInvariant();
            var autoTarget = preferredAether switch
            {
                "wireguard" or "wg" => ConnectionMethod.WireGuard,
                "warp_on_warp" or "wow" or "warp" => ConnectionMethod.WarpOnWarp,
                "mim" or "masque_in_masque" => ConnectionMethod.MasqueInMasque,
                _ => ConnectionMethod.Masque,
            };

            var targetMethod = outerTransport switch
            {
                "wireguard" or "wg" => ConnectionMethod.WireGuard,
                "warp_on_warp" or "wow" or "warp" => ConnectionMethod.WarpOnWarp,
                "mim" or "masque_in_masque" => ConnectionMethod.MasqueInMasque,
                "masque" => ConnectionMethod.Masque,
                _ => autoTarget,
            };

            _outerSocksPort = FindFreeTcpPort();
            lock (_outerOverridesLock)
            {
                _outerSocksPortOverride = _outerSocksPort;
                _outerMethodOverride = targetMethod;
            }
            AetherEngine.SocksPortOverride = _outerSocksPort;
            AetherEngine.MethodOverride = targetMethod;
            
            
            
            
            
            AetherEngine.UpstreamProxyOverride = "";
            Log($"Starting outer WARP transport ({targetMethod.ToDisplayName()})...");
            await _outer.StartAsync();

            
            
            
            
            
            
            var outerBudgetSec = GetAetherOuterTimeoutSeconds();
            var maxWait = (int)Math.Ceiling(outerBudgetSec * 1000.0 / 500.0);
            var outerConnected = false;
            for (var i = 0; i < maxWait && !ct.IsCancellationRequested; i++)
            {
                if (_outer.State == ConnectionState.Connected)
                {
                    
                    
                    
                    if (await ProbeOuterSocksReadyAsync(_outerSocksPort, ct))
                    {
                        
                        
                        
                        
                        
                        
                        var verified = await VerifyOuterEgressAsync(_outerSocksPort, OuterLegName, targetMethod.ToDisplayName(), ct);
                        if (verified && _outer.State == ConnectionState.Connected)
                        {
                            outerConnected = true;
                            break;
                        }
                    }
                }
                if (_outer.State is ConnectionState.Error or ConnectionState.Disconnected)
                {
                    
                    
                    var graceUntil = DateTime.UtcNow.AddSeconds(6);
                    while (DateTime.UtcNow < graceUntil && !ct.IsCancellationRequested)
                    {
                        if (_outer.State == ConnectionState.Connected
                            || _outer.State == ConnectionState.Connecting)
                            break;
                        await Task.Delay(500, ct);
                    }
                    if (_outer.State is ConnectionState.Error or ConnectionState.Disconnected)
                        break;
                }
                await Task.Delay(500, ct);
            }

            
            if (!outerConnected && outerTransport == "auto" && !ct.IsCancellationRequested)
            {
                var fallbackMethod = targetMethod == ConnectionMethod.WireGuard
                    ? ConnectionMethod.Masque
                    : ConnectionMethod.WireGuard;
                Log($"Outer {targetMethod.ToDisplayName()} transport did not connect; attempting fallback to {fallbackMethod.ToDisplayName()}...");
                SetProgress(25, $"{OuterLegName} [1/2]: Fallback to {fallbackMethod.ToDisplayName()}...");
                try { await _outer.StopAsync(); } catch { }

                lock (_outerOverridesLock) _outerMethodOverride = fallbackMethod;
                AetherEngine.SocksPortOverride = _outerSocksPort;
                AetherEngine.MethodOverride = fallbackMethod;
                await _outer.StartAsync();

                for (var i = 0; i < 160 && !ct.IsCancellationRequested; i++)
                {
                    if (_outer.State == ConnectionState.Connected
                        && await ProbeOuterSocksReadyAsync(_outerSocksPort, ct))
                    {
                        var verifiedFallback = await VerifyOuterEgressAsync(_outerSocksPort, OuterLegName, fallbackMethod.ToDisplayName(), ct);
                        if (verifiedFallback && _outer.State == ConnectionState.Connected)
                        {
                            outerConnected = true;
                            break;
                        }
                    }
                    if (_outer.State is ConnectionState.Error or ConnectionState.Disconnected)
                    {
                        var graceUntil = DateTime.UtcNow.AddSeconds(5);
                        while (DateTime.UtcNow < graceUntil && !ct.IsCancellationRequested)
                        {
                            if (_outer.State is ConnectionState.Connected or ConnectionState.Connecting) break;
                            await Task.Delay(500, ct);
                        }
                        if (_outer.State is ConnectionState.Error or ConnectionState.Disconnected) break;
                    }
                    await Task.Delay(500, ct);
                }
            }

            if (!outerConnected)
            {
                if (ct.IsCancellationRequested)
                {
                    await StopInternalAsync();
                    SetState(ConnectionState.Disconnected);
                    return;
                }
                Log("Outer WARP leg failed to establish; aborting chained session.");
                await StopInternalAsync();
                SetState(ConnectionState.Error);
                return;
            }

            _stepOneComplete = true;
            Log("Outer WARP tunnel established and verified. Starting inner leg through WARP...");
            SetProgress(50, $"{OuterLegName} connected (1/2) -> Connecting to {innerName} (2/2)...");

            
            var outerHttpPort = _outer.HttpProxyPort;
            lock (_outerOverridesLock)
            {
                if (isTor) _outerSocks5ProxyOverride = $"127.0.0.1:{_outerSocksPort}";
                else if (!isFoxy && !isLantern && !isVeep) _outerUpstreamProxyUrlOverride = $"http://127.0.0.1:{outerHttpPort}";
            }
            if (isTor)
            {
                TorEngine.Socks5ProxyOverride = $"127.0.0.1:{_outerSocksPort}";
                _tor.ReadyTimeoutOverride = TimeSpan.FromSeconds(ChainedTorInnerBudgetSec + 10);
                await _tor.StartAsync();
            }
            else if (isFoxy)
            {
                FoxyEngine.UpstreamProxyOverride = $"127.0.0.1:{_outerSocksPort}";
                await _foxy.StartAsync();
            }
            else if (isLantern)
            {
                LanternEngine.UpstreamProxyOverride = $"socks5://127.0.0.1:{_outerSocksPort}";
                await _lantern.StartAsync();
            }
            else if (isVeep)
            {
                VeepEngine.UpstreamProxyOverride = $"socks5://127.0.0.1:{_outerSocksPort}";
                await _veep.StartAsync();
            }
            else
            {
                if (outerHttpPort <= 0)
                {
                    Log("Outer WARP leg did not expose an HTTP proxy port for Psiphon; aborting chain.");
                    await StopInternalAsync();
                    SetState(ConnectionState.Error);
                    return;
                }
                TunnelCoreManager.UpstreamProxyUrlOverride = $"http://127.0.0.1:{outerHttpPort}";
                await StartInnerPsiphonAsync();
            }

            
            
            var innerBudgetSec = isTor
                ? ChainedTorInnerBudgetSec
                : isFoxy ? ChainedFoxyInnerBudgetSec
                : isLantern ? ChainedLanternInnerBudgetSec
                : isVeep ? ChainedVeepInnerBudgetSec
                : ChainedPsiphonInnerBudgetSec;
            var innerMaxWait = (int)Math.Ceiling(innerBudgetSec * 1000.0 / 500.0);
            
            var innerConnected = false;
            for (var i = 0; i < innerMaxWait && !ct.IsCancellationRequested; i++)
            {
                if (_outer.State is ConnectionState.Error or ConnectionState.Disconnected)
                {
                    Log("Outer WARP leg dropped while inner was connecting; aborting chain.");
                    break;
                }
                if (InnerEngine.State == ConnectionState.Connected)
                {
                    
                    var innerPort = InnerEngine.SocksProxyPort;
                    if (innerPort > 0 && await ProbeOuterSocksReadyAsync(innerPort, ct))
                    {
                        innerConnected = true;
                        break;
                    }
                }
                if (InnerEngine.State is ConnectionState.Error or ConnectionState.Disconnected)
                {
                    break;
                }
                await Task.Delay(500, ct);
            }

            if (innerConnected && !ct.IsCancellationRequested)
            {
                SetProgress(100, $"Connected ({label})");
                SetState(ConnectionState.Connected);
                Log($"Multi-hop {label} successfully connected!");
                TriggerChainedExitProbe(InnerEngine, ct);
            }
            else
            {
                if (ct.IsCancellationRequested)
                {
                    await StopInternalAsync();
                    SetState(ConnectionState.Disconnected);
                    return;
                }
                Log("Inner leg failed to establish; stopping chained session."
                  + InnerStallHint(innerName, "WARP", "wireguard", unencryptedHop: false));
                await StopInternalAsync();
                SetState(ConnectionState.Error);
            }
        }
        catch (OperationCanceledException)
        {
            Log("Chained connection cancelled.");
            await StopInternalAsync();
            SetState(ConnectionState.Disconnected);
        }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested)
            {
                await StopInternalAsync();
                SetState(ConnectionState.Disconnected);
                return;
            }
            _logger.LogError(ex, "Chained engine startup error");
            await StopInternalAsync();
            SetState(ConnectionState.Error);
        }
        finally
        {
            
            
            
            
            
            
            
            try { _gate.Release(); } catch { }
        }
    }

        private async Task StartInnerPsiphonAsync()
    {
        if (_psiphon.State is ConnectionState.Connected or ConnectionState.Connecting)
            await _psiphon.RestartAsync();
        else
            await _psiphon.StartAsync();
    }

    private async Task RunPsiphonOverHttpEngineAsync(
        IConnectionEngine outerEngine,
        string outerDisplayName,
        string outerProtocolKey,
        int outerBudgetSec,
        Action? resetOuterOverrides,
        CancellationToken ct)
    {
        Log($"Starting multi-hop chained session (Psiphon over {outerDisplayName})...");
        SetProgress(10, $"{OuterLegName} [1/2]: Connecting...");

        TunnelCoreManager.UpstreamProxyUrlOverride = null;
        resetOuterOverrides?.Invoke();
        if (outerEngine.State is ConnectionState.Connected or ConnectionState.Connecting)
        {
            try { await outerEngine.StopAsync(); } catch { }
        }
        await outerEngine.StartAsync();

        var maxWait = (int)Math.Ceiling(outerBudgetSec * 1000.0 / 500.0);
        var outerConnected = false;
        for (var i = 0; i < maxWait && !ct.IsCancellationRequested; i++)
        {
            if (outerEngine.State == ConnectionState.Connected)
            {
                var port = outerEngine.SocksProxyPort;
                if (port > 0 && await ProbeOuterSocksReadyAsync(port, ct))
                {
                    outerConnected = true;
                    break;
                }
            }
            if (outerEngine.State is ConnectionState.Error or ConnectionState.Disconnected) break;
            await Task.Delay(500, ct);
        }

        if (!outerConnected)
        {
            if (ct.IsCancellationRequested)
            {
                await StopInternalAsync();
                SetState(ConnectionState.Disconnected);
                return;
            }
            Log($"{outerDisplayName} outer leg failed to establish; aborting chained session.");
            await StopInternalAsync();
            SetState(ConnectionState.Error);
            return;
        }

        var socksPort = outerEngine.SocksProxyPort;
        var httpPort = outerEngine.HttpProxyPort;
        var outerOk = await VerifyOuterEgressAsync(socksPort, outerDisplayName, outerProtocolKey, ct);
        if (!outerOk || httpPort <= 0)
        {
            Log(httpPort <= 0
                ? $"{outerDisplayName} outer hop did not expose an HTTP proxy port for Psiphon; aborting chain."
                : $"{outerDisplayName} outer hop did not carry internet traffic; aborting chain.");
            await StopInternalAsync();
            SetState(ConnectionState.Error);
            return;
        }
        _stepOneComplete = true;
        Log($"{outerDisplayName} HTTP inbound ready on 127.0.0.1:{httpPort}. Starting Psiphon through {outerDisplayName} tunnel...");
        SetProgress(50, $"{OuterLegName} connected (1/2) -> Connecting to Psiphon (2/2)...");

        lock (_outerOverridesLock)
            _outerUpstreamProxyUrlOverride = $"http://127.0.0.1:{httpPort}";
        TunnelCoreManager.UpstreamProxyUrlOverride = $"http://127.0.0.1:{httpPort}";
        await StartInnerPsiphonAsync();

        var innerMaxWait = (int)Math.Ceiling(ChainedPsiphonInnerBudgetSec * 1000.0 / 500.0);
        var innerConnected = false;
        for (var i = 0; i < innerMaxWait && !ct.IsCancellationRequested; i++)
        {
            if (outerEngine.State is ConnectionState.Error or ConnectionState.Disconnected)
            {
                Log($"{outerDisplayName} outer leg dropped while Psiphon was connecting; aborting chain.");
                break;
            }
            if (_psiphon.State == ConnectionState.Connected)
            {
                var innerPort = _psiphon.SocksProxyPort;
                if (innerPort > 0 && await ProbeOuterSocksReadyAsync(innerPort, ct))
                {
                    innerConnected = true;
                    break;
                }
            }
            if (_psiphon.State is ConnectionState.Error or ConnectionState.Disconnected) break;
            await Task.Delay(500, ct);
        }

        if (innerConnected && !ct.IsCancellationRequested)
        {
            SetProgress(100, $"Connected (Psiphon over {outerDisplayName})");
            SetState(ConnectionState.Connected);
            Log($"Multi-hop Psiphon over {outerDisplayName} successfully connected!");
            TriggerChainedExitProbe(InnerEngine, ct);
            _ = Task.Run(() => RunChainedPsiphonWatchdogAsync(outerEngine, outerDisplayName, ct), ct);
        }
        else
        {
            if (ct.IsCancellationRequested)
            {
                await StopInternalAsync();
                SetState(ConnectionState.Disconnected);
                return;
            }
            Log($"Psiphon failed to connect through {outerDisplayName}; stopping chained session.");
            await StopInternalAsync();
            SetState(ConnectionState.Error);
        }
    }

    private async Task RunChainedPsiphonWatchdogAsync(
        IConnectionEngine outerEngine,
        string outerDisplayName,
        CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && !_isStopping)
            {
                await Task.Delay(TimeSpan.FromSeconds(45), ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested || _isStopping || State != ConnectionState.Connected)
                    return;

                var innerPort = _psiphon.SocksProxyPort;
                if (innerPort <= 0)
                    continue;

                var proof = await SocksProbe.WaitForTunnelAsync(
                    innerPort, DateTime.UtcNow.AddSeconds(8), ct, TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
                if (proof != null || ct.IsCancellationRequested || _isStopping)
                    continue;

                await Task.Delay(TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested || _isStopping || State != ConnectionState.Connected)
                    return;

                innerPort = _psiphon.SocksProxyPort;
                proof = innerPort > 0
                    ? await SocksProbe.WaitForTunnelAsync(
                        innerPort, DateTime.UtcNow.AddSeconds(8), ct, TimeSpan.FromMilliseconds(500)).ConfigureAwait(false)
                    : null;
                if (proof != null || ct.IsCancellationRequested || _isStopping)
                    continue;

                Log($"Chained Psiphon leg over {outerDisplayName} stalled (no probe traffic); checking {outerDisplayName} outer hop...");

                var outerHealthy = outerEngine.State == ConnectionState.Connected
                    && outerEngine.SocksProxyPort > 0
                    && outerEngine.HttpProxyPort > 0
                    && await SocksProbe.WaitForTunnelAsync(
                        outerEngine.SocksProxyPort, DateTime.UtcNow.AddSeconds(8), ct, TimeSpan.FromMilliseconds(500)).ConfigureAwait(false) != null;

                if (!outerHealthy)
                {
                    Log($"{outerDisplayName} outer hop stopped carrying traffic; triggering chain recovery.");
                    SetState(ConnectionState.Error);
                    return;
                }

                Log($"{outerDisplayName} outer hop is healthy; restarting Psiphon leg in-place...");
                try { await _psiphon.StopAsync().ConfigureAwait(false); } catch { }
                if (ct.IsCancellationRequested || _isStopping)
                    return;

                var outerHttp = outerEngine.HttpProxyPort;
                lock (_outerOverridesLock)
                    _outerUpstreamProxyUrlOverride = $"http://127.0.0.1:{outerHttp}";
                TunnelCoreManager.UpstreamProxyUrlOverride = $"http://127.0.0.1:{outerHttp}";
                await StartInnerPsiphonAsync().ConfigureAwait(false);

                var maxWait = (int)Math.Ceiling(ChainedPsiphonInnerBudgetSec * 1000.0 / 500.0);
                var recovered = false;
                for (var i = 0; i < maxWait && !ct.IsCancellationRequested && !_isStopping; i++)
                {
                    if (outerEngine.State is ConnectionState.Error or ConnectionState.Disconnected)
                        break;
                    if (_psiphon.State == ConnectionState.Connected
                        && _psiphon.SocksProxyPort > 0
                        && await ProbeOuterSocksReadyAsync(_psiphon.SocksProxyPort, ct).ConfigureAwait(false))
                    {
                        recovered = true;
                        break;
                    }
                    if (_psiphon.State is ConnectionState.Error or ConnectionState.Disconnected)
                        break;
                    await Task.Delay(500, ct).ConfigureAwait(false);
                }

                if (recovered)
                {
                    Log($"Psiphon over {outerDisplayName} recovered in-place.");
                }
                else if (!ct.IsCancellationRequested && !_isStopping)
                {
                    Log($"Psiphon failed to recover over {outerDisplayName}; triggering chain recovery.");
                    SetState(ConnectionState.Error);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Chained Psiphon watchdog exited: {Message}", ex.Message);
        }
    }

    public void CancelConnecting()
    {
        _isStopping = true;
        try { _connectCts?.Cancel(); } catch { }
        if (InnerEngine is TunnelCoreManager p) try { p.CancelConnecting(); } catch { }
        if (InnerEngine is TorEngine t) try { t.CancelConnecting(); } catch { }
        if (InnerEngine is FoxyEngine f) try { f.CancelConnecting(); } catch { }
        if (InnerEngine is LanternEngine l) try { l.CancelConnecting(); } catch { }
        if (InnerEngine is VeepEngine v) try { v.CancelConnecting(); } catch { }
        try { _outer.CancelConnecting(); } catch { }
        try { _v2ray.CancelConnecting(); } catch { }
        try { _shard.CancelConnecting(); } catch { }
    }

    public async Task StopAsync()
    {
        _isStopping = true;
        try { _connectCts?.Cancel(); } catch { }
        var runningChain = _chainTask;
        if (runningChain != null)
        {
            try { await runningChain.ConfigureAwait(false); } catch { }
        }
        SetState(ConnectionState.Disconnecting);
        try
        {
            await _gate.WaitAsync();
            try
            {
                await StopInternalAsync();
                SetProgress(0, "");
                SetState(ConnectionState.Disconnected);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch
        {
            await StopInternalAsync();
            SetState(ConnectionState.Disconnected);
        }
        finally
        {
            _isStopping = false;
        }
    }

    private async Task StopInternalAsync()
    {
        try
        {
            await Task.WhenAll(
                Task.Run(async () =>
                {
                    try { await InnerEngine.StopAsync(); } catch { }
                }),
                Task.Run(async () =>
                {
                    try { await _outer.StopAsync(); } catch { }
                }),
                Task.Run(async () =>
                {
                    try { await _v2ray.StopAsync(); } catch { }
                }),
                Task.Run(async () =>
                {
                    try { await _foxy.StopAsync(); } catch { }
                }),
                Task.Run(async () =>
                {
                    try { await _shard.StopAsync(); } catch { }
                }),
                Task.Run(async () =>
                {
                    try { await _lantern.StopAsync(); } catch { }
                }),
                Task.Run(async () =>
                {
                    try { await _veep.StopAsync(); } catch { }
                }));
        }
        catch { }
        finally
        {
            lock (_outerOverridesLock)
            {
                _outerUpstreamProxyUrlOverride = null;
                _outerSocks5ProxyOverride = null;
                _outerSocksPortOverride = null;
                _outerMethodOverride = null;
            }
            TunnelCoreManager.UpstreamProxyUrlOverride = null;
            TorEngine.Socks5ProxyOverride = null;
            
            
            _tor.ReadyTimeoutOverride = null;
            AetherEngine.SocksPortOverride = null;
            AetherEngine.MethodOverride = null;
            AetherEngine.UpstreamProxyOverride = null;
            V2RayEngine.ClearSocksPortOverride();
            V2RayEngine.ClearUserUpstreamOverride();
            FoxyEngine.UpstreamProxyOverride = null;
            FoxyEngine.PreferDualSessionPool = false;
            LanternEngine.UpstreamProxyOverride = null;
            VeepEngine.UpstreamProxyOverride = null;
            _stepOneComplete = false;
        }
    }

    private void AttachEvents(IConnectionEngine engine, bool isOuter)
    {
        EventHandler<ConnectionState> stateHandler = (_, state) =>
        {
            if (!_isStopping && _connectCts?.IsCancellationRequested == false && State is ConnectionState.Connected)
            {
                if (state is ConnectionState.Error or ConnectionState.Disconnected)
                {
                    Log($"Chained leg ({engine.Method}) dropped into {state}. Triggering recovery.");
                    SetState(ConnectionState.Error);
                }
                else if (state == ConnectionState.Connecting)
                {
                    SetProgress(60, $"A chain hop ({engine.Method.ToDisplayName()}) is re-establishing...");
                }
            }
            else if (!_isStopping && state == ConnectionState.Connected &&
                     State is ConnectionState.Connecting or ConnectionState.Error)
            {
                if (OuterEngine.State == ConnectionState.Connected && InnerEngine.State == ConnectionState.Connected)
                {
                    if (_hasBeenConnected) Log("All chain hops are up again — session restored.");
                    SetProgress(100, "Connected");
                    SetState(ConnectionState.Connected);
                }
            }
        };
        engine.StateChanged += stateHandler;
        _eventDetachActions.Add(() => engine.StateChanged -= stateHandler);

        EventHandler<string> logHandler = (_, line) =>
        {
            if (State is ConnectionState.Connecting or ConnectionState.Connected)
            {
                var innerTag = _method switch
                {
                    ConnectionMethod.TorOverWarp or ConnectionMethod.TorOverV2Ray => "Tor",
                    ConnectionMethod.FoxyOverWarp or ConnectionMethod.FoxyOverV2Ray => "Foxy",
                    ConnectionMethod.LanternOverWarp => "Lantern",
                    ConnectionMethod.VeepOverWarp => "Veep",
                    _ => "Psiphon"
                };
                var tag = IsWarpOverV2Ray
                    ? (ReferenceEquals(engine, _v2ray) ? "V2Ray" : "WARP")
                    : isOuter ? OuterLegName : innerTag;
                if ((_method == ConnectionMethod.LanternOverWarp && ReferenceEquals(engine, _lantern))
                    || (_method == ConnectionMethod.VeepOverWarp && ReferenceEquals(engine, _veep)))
                    LogLineAppended?.Invoke(this, line);
                else
                    LogLineAppended?.Invoke(this, LogTags.Tag(tag, line));
            }
        };
        engine.LogLineAppended += logHandler;
        _eventDetachActions.Add(() => engine.LogLineAppended -= logHandler);

        if (isOuter)
        {
            EventHandler routeHandler = (_, _) =>
            {
                if (State is ConnectionState.Connecting or ConnectionState.Connected)
                    RouteChanged?.Invoke(this, EventArgs.Empty);
            };
            engine.RouteChanged += routeHandler;
            _eventDetachActions.Add(() => engine.RouteChanged -= routeHandler);

            if (IsWarpOverV2Ray && ReferenceEquals(engine, _outer))
            {
                EventHandler<Notice> noticeHandler = (_, notice) =>
                {
                    if (State is ConnectionState.Connecting or ConnectionState.Connected)
                        NoticeReceived?.Invoke(this, notice);
                };
                EventHandler bytesHandler = (_, _) =>
                {
                    if (State is ConnectionState.Connecting or ConnectionState.Connected)
                        BytesTransferredChanged?.Invoke(this, EventArgs.Empty);
                };
                engine.NoticeReceived += noticeHandler;
                engine.BytesTransferredChanged += bytesHandler;
                _eventDetachActions.Add(() => engine.NoticeReceived -= noticeHandler);
                _eventDetachActions.Add(() => engine.BytesTransferredChanged -= bytesHandler);
            }

            EventHandler progressHandler = (_, _) =>
            {
                if (!ReferenceEquals(engine, OuterEngine)) return;
                if (_stepOneComplete) return;
                if (State == ConnectionState.Connecting && engine.State != ConnectionState.Connected)
                {
                    var pct = Math.Clamp(engine.ConnectProgressPercent / 2, 5, 50);
                    var text = engine.ConnectProgressText;
                    if (string.IsNullOrWhiteSpace(text)) text = "Connecting...";
                    if (text.StartsWith(OuterLegName, StringComparison.OrdinalIgnoreCase))
                        SetProgress(pct, $"[1/2] {text}");
                    else
                        SetProgress(pct, $"{OuterLegName} [1/2]: {text}");
                }
            };
            engine.ConnectProgressChanged += progressHandler;
            _eventDetachActions.Add(() => engine.ConnectProgressChanged -= progressHandler);
        }
        else
        {
            EventHandler<Notice> noticeHandler = (_, notice) =>
            {
                if (State is ConnectionState.Connecting or ConnectionState.Connected)
                    NoticeReceived?.Invoke(this, notice);
            };
            EventHandler bytesHandler = (_, _) =>
            {
                if (State is ConnectionState.Connecting or ConnectionState.Connected)
                    BytesTransferredChanged?.Invoke(this, EventArgs.Empty);
            };
            EventHandler routeHandler = (_, _) =>
            {
                if (State is ConnectionState.Connecting or ConnectionState.Connected)
                    RouteChanged?.Invoke(this, EventArgs.Empty);
            };
            EventHandler progressHandler = (_, _) =>
            {
                if (State == ConnectionState.Connecting && OuterEngine.State == ConnectionState.Connected)
                {
                    var innerTag = _method switch
                    {
                        ConnectionMethod.TorOverWarp or ConnectionMethod.TorOverV2Ray => "Tor",
                        ConnectionMethod.FoxyOverWarp or ConnectionMethod.FoxyOverV2Ray => "Foxy",
                        ConnectionMethod.LanternOverWarp => "Lantern",
                        ConnectionMethod.VeepOverWarp => "Veep",
                        ConnectionMethod.WarpOverV2Ray => "WARP",
                        _ => "Psiphon"
                    };
                    var pct = 50 + Math.Clamp(InnerEngine.ConnectProgressPercent / 2, 0, 50);
                    var text = InnerEngine.ConnectProgressText;
                    if (string.IsNullOrWhiteSpace(text)) text = "Connecting...";
                    if (text.StartsWith(innerTag, StringComparison.OrdinalIgnoreCase))
                        SetProgress(pct, $"[2/2] {text}");
                    else
                        SetProgress(pct, $"{innerTag} [2/2]: {text}");
                }
            };
            engine.NoticeReceived += noticeHandler;
            engine.BytesTransferredChanged += bytesHandler;
            engine.RouteChanged += routeHandler;
            engine.ConnectProgressChanged += progressHandler;
            _eventDetachActions.Add(() => engine.NoticeReceived -= noticeHandler);
            _eventDetachActions.Add(() => engine.BytesTransferredChanged -= bytesHandler);
            _eventDetachActions.Add(() => engine.RouteChanged -= routeHandler);
            _eventDetachActions.Add(() => engine.ConnectProgressChanged -= progressHandler);
        }
    }

    

    private double GetAetherOuterTimeoutSeconds()
    {
        var s = _settings.Settings;
        var scanRaw = !string.IsNullOrWhiteSpace(s.AetherScanModeMasque) ? s.AetherScanModeMasque : s.AetherScanMode;
        var scan = (scanRaw ?? "balanced").Trim().ToLowerInvariant();
        double scanSec = scan switch
        {
            "turbo" => 45,
            "thorough" => 300 + 30,
            "verified" => 240 + 30,
            "stealth" => 180 + 25,
            "ironclad" => 180 + 15,
            _ => 120 + 20, 
        };
        return scanSec + 30 + 20;
    }

        private static string NodeLabel(V2RayConfigEntry c) =>
        string.IsNullOrWhiteSpace(c.Name) ? $"{c.Protocol} port {c.Port}" : c.Name.Trim();

        private static string HopTransport(V2RayConfigEntry c)
    {
        var security = (c.Security ?? string.Empty).Trim().ToLowerInvariant();
        if (security.Length == 0) security = "none";
        var network = string.IsNullOrWhiteSpace(c.Network) ? "tcp" : c.Network.Trim().ToLowerInvariant();
        return $"{(c.Protocol ?? "vless").ToLowerInvariant()}/{network}/{security}";
    }

        private static bool IsPlaintextHop(V2RayConfigEntry c)
    {
        var protocol = (c.Protocol ?? string.Empty).ToLowerInvariant();
        if (protocol is not ("vless" or "trojan")) return false;
        if (!string.IsNullOrWhiteSpace(c.PublicKey)) return false;
        var security = (c.Security ?? string.Empty).Trim().ToLowerInvariant();
        return security is "" or "none" or "plain";
    }

        private async Task<bool> VerifyOuterEgressAsync(int socksPort, string nodeName, string transport, CancellationToken ct)
    {
        SetProgress(42, $"Checking that {nodeName} can reach the internet...");
        var proof = await SocksProbe.WaitForTunnelAsync(
            socksPort, DateTime.UtcNow.AddSeconds(12), ct, TimeSpan.FromMilliseconds(700));
        if (ct.IsCancellationRequested) return false;

        if (proof != null)
        {
            Log($"{nodeName} outer hop carries traffic ({transport}, verified through {proof}).");
            return true;
        }

        Log($"{nodeName} outer hop on 127.0.0.1:{socksPort} did not answer HTTP reachability check ({transport}); outer proxy cannot reach the internet.");
        return false;
    }

        private string InnerStallHint(string innerName, string outerLabel, string transport, bool unencryptedHop)
    {
        if (InnerEngine is not TorEngine) return "";

        if (unencryptedHop)
        {
            return $" The {outerLabel} hop is {transport}: it carries the chain without encrypting it,"
              + " so Tor's relay handshakes are readable to anything between you and the relay and get"
              + " dropped there while ordinary traffic passes. A node with security=tls or"
              + " security=reality is what fixes this - another attempt on this node will stall the"
              + " same way.";
        }

        var stage = InnerEngine.CurrentRouteSni;
        if (string.IsNullOrWhiteSpace(stage)) return "";

        return $" Last {innerName} progress through the {outerLabel} hop ({transport}) was \"{stage}\":"
          + " the hop answers connections but does not finish carrying them."
          + " Pick a different node, or switch the V2Ray core (xray / sing-box) in Settings.";
    }

    private static int FindFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static int FindFreeTcpPortPair()
    {
        for (var attempt = 0; attempt < 25; attempt++)
        {
            var p = FindFreeTcpPort();
            try
            {
                using var probe = new TcpListener(IPAddress.Loopback, p + 1);
                probe.Start();
                probe.Stop();
                return p;
            }
            catch
            {
            }
        }
        return FindFreeTcpPort();
    }

    private static async Task<bool> ProbeOuterSocksReadyAsync(int socksPort, CancellationToken ct)
    {
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(TimeSpan.FromSeconds(3));
            using var client = new System.Net.Sockets.TcpClient();
            await client.ConnectAsync(System.Net.IPAddress.Loopback, socksPort, linked.Token);
            await using var stream = client.GetStream();
            await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, linked.Token);
            var resp = new byte[2];
            var read = 0;
            while (read < 2)
            {
                var n = await stream.ReadAsync(resp.AsMemory(read), linked.Token);
                if (n <= 0) return false;
                read += n;
            }
            return resp[0] == 0x05 && resp[1] == 0x00;
        }
        catch { return false; }
    }

    private void TriggerChainedExitProbe(IConnectionEngine inner, CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1200, ct);
                var innerPort = inner.SocksProxyPort;
                if (innerPort <= 0) return;

                var result = await EndpointProbeHelper.ProbeAsync(innerPort, ct);
                if (result != null && !string.IsNullOrEmpty(result.Ip))
                {
                    inner.UpdateExitLocation(result.Ip, result.CountryCode ?? "", result.CityOrColo);
                    RouteChanged?.Invoke(this, EventArgs.Empty);
                }

                await Task.Delay(3500, ct);
                if (State == ConnectionState.Connected && !ct.IsCancellationRequested)
                {
                    var verify = await EndpointProbeHelper.ProbeAsync(innerPort, ct);
                    if (verify != null && !string.IsNullOrEmpty(verify.Ip) &&
                        (verify.Ip != inner.CurrentRouteIp || verify.CountryCode != inner.ConnectedServerRegion))
                    {
                        inner.UpdateExitLocation(verify.Ip, verify.CountryCode ?? "", verify.CityOrColo);
                        RouteChanged?.Invoke(this, EventArgs.Empty);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Chained exit probe failed");
            }
        }, ct);
    }

    private void Log(string line) =>
        LogLineAppended?.Invoke(this, LogTags.Tag(LogTags.Chain, line));

    public void Dispose()
    {
        foreach (var detach in _eventDetachActions)
        {
            try { detach(); } catch { }
        }
        _eventDetachActions.Clear();
        _connectCts?.Cancel();
        _connectCts?.Dispose();
        _gate.Dispose();
    }
}
