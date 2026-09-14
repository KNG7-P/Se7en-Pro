using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Se7enPro.Models;

namespace Se7enPro.Services;

public sealed class ChainedEngine : IConnectionEngine, IDisposable
{
    private readonly ILogger<ChainedEngine> _logger;
    private readonly ISettingsService _settings;
    private readonly AetherEngine _outer;
    private readonly TunnelCoreManager _psiphon;
    private readonly TorEngine _tor;
    private readonly V2RayEngine _v2ray;
    private readonly ConnectionMethod _method;

    private const int ChainOuterSocksPort = 1820;
    private const int ChainOuterV2RaySocksPort = 1832;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _staticLock = new();
    private CancellationTokenSource? _connectCts;
    private volatile bool _isStopping;

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
        ConnectionMethod method)
    {
        _logger = logger;
        _settings = settings;
        _outer = outer;
        _psiphon = psiphon;
        _tor = tor;
        _v2ray = v2ray;
        _method = method;

        AttachEvents(_outer, isOuter: true);
        AttachEvents(_psiphon, isOuter: false);
        AttachEvents(_tor, isOuter: false);
        AttachEvents(_v2ray, isOuter: true);
    }

    public ConnectionMethod Method => _method;
    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;

    private IConnectionEngine InnerEngine =>
        _method switch
        {
            ConnectionMethod.TorOverWarp or ConnectionMethod.TorOverV2Ray => _tor,
            _ => _psiphon,
        };

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
            var isV2Ray = _method is ConnectionMethod.PsiphonOverV2Ray or ConnectionMethod.TorOverV2Ray;
            var outer = isV2Ray ? _v2ray.CurrentRouteIp : _outer.CurrentRouteIp;
            return !string.IsNullOrEmpty(outer) ? outer : inner;
        }
    }
    public string CurrentRouteSni => InnerEngine.CurrentRouteSni;
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
        _connectCts?.Cancel();
        _connectCts = new CancellationTokenSource();
        var ct = _connectCts.Token;

        SetState(ConnectionState.Connecting);
        return RunChainAsync(ct);
    }

    private async Task RunChainAsync(CancellationToken ct)
    {
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

            var userUpstream = (_settings.Settings.UpstreamProxy ?? "").Trim();
            if (_settings.Settings.UpstreamProxyEnabled && !string.IsNullOrEmpty(userUpstream))
            {
                Log("Chained mode cannot be used while 'Use upstream proxy' is enabled — disable it in Settings first.");
                SetState(ConnectionState.Error);
                return;
            }

            if (_method is ConnectionMethod.PsiphonOverV2Ray or ConnectionMethod.TorOverV2Ray)
            {
                var v2rayInnerName = _method switch
                {
                    ConnectionMethod.TorOverV2Ray => "Tor",
                    _ => "Psiphon"
                };
                Log($"Starting multi-hop chained session ({v2rayInnerName} over V2Ray)...");
                SetProgress(10, "Starting V2Ray/Xray/Sing-box outer transport...");

                V2RayEngine.SetSocksPortOverride(ChainOuterV2RaySocksPort);
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

                Log($"V2Ray inbound ready on 127.0.0.1:{socksPort}. Verifying outbound node connectivity before starting {v2rayInnerName}...");
                SetProgress(30, "Verifying V2Ray outbound connectivity...");

                var outboundReady = false;
                var outboundDeadline = DateTime.UtcNow.AddSeconds(25);
                while (DateTime.UtcNow < outboundDeadline && !ct.IsCancellationRequested)
                {
                    if (_v2ray.State is ConnectionState.Error or ConnectionState.Disconnected)
                    {
                        Log("V2Ray process exited unexpectedly while testing outbound connectivity.");
                        break;
                    }
                    if (await ProbeProxyCanConnectInternetAsync(socksPort, ct))
                    {
                        outboundReady = true;
                        break;
                    }
                    await Task.Delay(600, ct);
                }

                if (!outboundReady)
                {
                    Log("V2Ray outbound connectivity test failed (node is unreachable or unresponsive); aborting chained session.");
                    await StopInternalAsync();
                    SetState(ConnectionState.Error);
                    return;
                }

                Log("V2Ray outbound connection confirmed live. Stabilizing node tunnel...");
                SetProgress(45, "Stabilizing V2Ray connection...");
                try { await Task.Delay(1200, ct); } catch { }

                Log($"Starting {v2rayInnerName} through verified V2Ray tunnel...");
                SetProgress(50, $"Tunnelling {v2rayInnerName} through V2Ray node...");

                lock (_outerOverridesLock)
                {
                    if (_method == ConnectionMethod.TorOverV2Ray)
                        _outerSocks5ProxyOverride = $"127.0.0.1:{socksPort}";
                    else
                        _outerUpstreamProxyUrlOverride = $"socks5://127.0.0.1:{socksPort}";
                }

                if (_method == ConnectionMethod.TorOverV2Ray)
                {
                    TorEngine.SetSocks5ProxyOverride($"127.0.0.1:{socksPort}");
                    await _tor.StartAsync();
                }
                else
                {
                    TunnelCoreManager.SetUpstreamProxyUrlOverride($"socks5://127.0.0.1:{socksPort}");
                    await _psiphon.StartAsync();
                }

                var v2rayInnerConnected = false;
                var v2rayInnerBudget = _method == ConnectionMethod.TorOverV2Ray ? 190 : 320;
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
                }
                else
                {
                    if (ct.IsCancellationRequested)
                    {
                        await StopInternalAsync();
                        SetState(ConnectionState.Disconnected);
                        return;
                    }
                    Log($"{v2rayInnerName} failed to connect through V2Ray; stopping.");
                    await StopInternalAsync();
                    SetState(ConnectionState.Error);
                }
                return;
            }

            var isTor = _method == ConnectionMethod.TorOverWarp;
            var label = isTor ? "Tor over WARP" : "Psiphon over WARP";
            var innerName = isTor ? "Tor" : "Psiphon";

            Log($"Starting multi-hop chained session ({label})...");
            SetProgress(10, "Connecting to Cloudflare WARP (outer leg)...");

            var outerTransport = isTor
                ? (_settings.Settings.ChainedTorOuterTransport ?? _settings.Settings.ChainedOuterTransport ?? "auto").Trim().ToLowerInvariant()
                : (_settings.Settings.ChainedPsiphonOuterTransport ?? _settings.Settings.ChainedOuterTransport ?? "auto").Trim().ToLowerInvariant();

            var preferredAether = (_settings.Settings.AetherProtocol ?? "masque").Trim().ToLowerInvariant();
            var autoTarget = preferredAether switch
            {
                "wireguard" or "wg" => ConnectionMethod.WireGuard,
                "warp_on_warp" or "wow" or "warp" => ConnectionMethod.WarpOnWarp,
                "masque_on_masque" or "mim" or "mom" => ConnectionMethod.MasqueOnMasque,
                _ => ConnectionMethod.Masque,
            };

            var targetMethod = outerTransport switch
            {
                "wireguard" or "wg" => ConnectionMethod.WireGuard,
                "warp_on_warp" or "wow" or "warp" => ConnectionMethod.WarpOnWarp,
                "masque_on_masque" or "mim" or "mom" => ConnectionMethod.MasqueOnMasque,
                "masque" => ConnectionMethod.Masque,
                _ => autoTarget,
            };

            lock (_outerOverridesLock)
            {
                _outerSocksPortOverride = ChainOuterSocksPort;
                _outerMethodOverride = targetMethod;
            }
            AetherEngine.SetOverrides(ChainOuterSocksPort, targetMethod);
            Log($"Starting outer WARP transport ({targetMethod.ToDisplayName()})...");
            await _outer.StartAsync();

            var outerBudgetSec = GetAetherOuterTimeoutSeconds();
            var maxWait = (int)Math.Ceiling(outerBudgetSec * 1000.0 / 500.0);
            var outerConnected = false;
            for (var i = 0; i < maxWait && !ct.IsCancellationRequested; i++)
            {
                if (_outer.State == ConnectionState.Connected)
                {

                    if (await ProbeOuterSocksReadyAsync(ChainOuterSocksPort, ct))
                    {

                        try { await Task.Delay(1200, ct); } catch { }
                        if (_outer.State == ConnectionState.Connected
                            && await ProbeOuterSocksReadyAsync(ChainOuterSocksPort, ct))
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
                SetProgress(25, $"WARP fallback -> Connecting via {fallbackMethod.ToDisplayName()} (outer leg)...");
                try { await _outer.StopAsync(); } catch { }

                lock (_outerOverridesLock) _outerMethodOverride = fallbackMethod;
                AetherEngine.SetOverrides(ChainOuterSocksPort, fallbackMethod);
                await _outer.StartAsync();

                for (var i = 0; i < 160 && !ct.IsCancellationRequested; i++)
                {
                    if (_outer.State == ConnectionState.Connected
                        && await ProbeOuterSocksReadyAsync(ChainOuterSocksPort, ct))
                    {
                        try { await Task.Delay(1000, ct); } catch { }
                        if (_outer.State == ConnectionState.Connected
                            && await ProbeOuterSocksReadyAsync(ChainOuterSocksPort, ct))
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
                if (!ct.IsCancellationRequested)
                {
                    Log("Outer WARP leg failed to establish; aborting chained session.");
                    await StopInternalAsync();
                    SetState(ConnectionState.Error);
                }
                return;
            }

            Log("Outer WARP tunnel established. Starting inner leg through WARP...");
            SetProgress(55, $"Tunnelling {innerName} through WARP (inner leg)...");

            lock (_outerOverridesLock)
            {
                if (isTor) _outerSocks5ProxyOverride = $"127.0.0.1:{ChainOuterSocksPort}";
                else _outerUpstreamProxyUrlOverride = $"socks5://127.0.0.1:{ChainOuterSocksPort}";
            }
            if (isTor)
            {
                TorEngine.SetSocks5ProxyOverride($"127.0.0.1:{ChainOuterSocksPort}");
                await _tor.StartAsync();
            }
            else
            {
                TunnelCoreManager.SetUpstreamProxyUrlOverride($"socks5://127.0.0.1:{ChainOuterSocksPort}");
                await _psiphon.StartAsync();
            }

            var innerBudgetSec = isTor ? 190 : 320;
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
            }
            else
            {
                if (ct.IsCancellationRequested)
                {
                    await StopInternalAsync();
                    SetState(ConnectionState.Disconnected);
                    return;
                }
                Log("Inner leg failed to establish; stopping chained session.");
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
            _isStopping = false;
            try { _gate.Release(); } catch { }
        }
    }

    public void CancelConnecting()
    {
        _isStopping = true;
        try { _connectCts?.Cancel(); } catch { }
        try { InnerEngine.CancelConnecting(); } catch { }
        try { _outer.CancelConnecting(); } catch { }
        try { _v2ray.CancelConnecting(); } catch { }
    }

    public async Task StopAsync()
    {
        _isStopping = true;
        try { _connectCts?.Cancel(); } catch { }
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
            TunnelCoreManager.ClearUpstreamProxyUrlOverride();
            TorEngine.ClearSocks5ProxyOverride();
            V2RayEngine.ClearSocksPortOverride();
            AetherEngine.ClearOverrides();
        }
    }

    private void AttachEvents(IConnectionEngine engine, bool isOuter)
    {
        engine.StateChanged += (_, state) =>
        {
            if (!_isStopping && _connectCts?.IsCancellationRequested == false && State is ConnectionState.Connected)
            {
                if (state is ConnectionState.Error or ConnectionState.Disconnected)
                {
                    Log($"Chained leg ({engine.Method}) dropped into {state}. Triggering recovery.");
                    SetState(ConnectionState.Error);
                }
            }
        };

        engine.LogLineAppended += (_, line) =>
        {
            if (State is ConnectionState.Connecting or ConnectionState.Connected)
            {
                var isV2RayOuter = _method is ConnectionMethod.PsiphonOverV2Ray or ConnectionMethod.TorOverV2Ray;
                var innerTag = _method switch
                {
                    ConnectionMethod.TorOverWarp or ConnectionMethod.TorOverV2Ray => "Tor",
                    _ => "Psiphon"
                };
                var tag = isOuter ? (isV2RayOuter ? "V2Ray" : "WARP") : innerTag;
                LogLineAppended?.Invoke(this, $"[{tag}] {line}");
            }
        };

        if (isOuter)
        {
            engine.RouteChanged += (_, _) => RouteChanged?.Invoke(this, EventArgs.Empty);
            engine.ConnectProgressChanged += (_, _) =>
            {
                if (State == ConnectionState.Connecting && _outer.State != ConnectionState.Connected)
                {
                    var pct = Math.Clamp(_outer.ConnectProgressPercent / 2, 5, 50);
                    SetProgress(pct, $"WARP outer: {_outer.ConnectProgressText}");
                }
            };
        }
        else
        {
            engine.NoticeReceived += (_, notice) => NoticeReceived?.Invoke(this, notice);
            engine.BytesTransferredChanged += (_, _) => BytesTransferredChanged?.Invoke(this, EventArgs.Empty);
            engine.RouteChanged += (_, _) => RouteChanged?.Invoke(this, EventArgs.Empty);
            engine.ConnectProgressChanged += (_, _) =>
            {
                if (State == ConnectionState.Connecting && _outer.State == ConnectionState.Connected)
                {
                    var pct = 50 + Math.Clamp(InnerEngine.ConnectProgressPercent / 2, 0, 50);
                    SetProgress(pct, InnerEngine.ConnectProgressText);
                }
            };
        }
    }

    private double GetAetherOuterTimeoutSeconds()
    {
        var scan = (_settings.Settings.AetherScanMode ?? "balanced").Trim().ToLowerInvariant();
        double scanSec = scan switch
        {
            "turbo" => 45,
            "thorough" => 300 + 30,
            "stealth" => 180 + 25,
            "ironclad" => 180 + 15,
            _ => 120 + 20,
        };

        return scanSec + 30 + 20;
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

    private static async Task<bool> ProbeProxyCanConnectInternetAsync(int socksPort, CancellationToken ct)
    {
        var targetUrls = new[]
        {
            "http://cp.cloudflare.com/generate_204",
            "http://connectivitycheck.gstatic.com/generate_204",
            "http://1.1.1.1/cdn-cgi/trace"
        };

        foreach (var url in targetUrls)
        {
            if (ct.IsCancellationRequested) return false;
            try
            {
                using var handler = new SocketsHttpHandler
                {
                    Proxy = new WebProxy($"socks5://127.0.0.1:{socksPort}"),
                    UseProxy = true,
                    ConnectTimeout = TimeSpan.FromSeconds(2.5),
                    PooledConnectionLifetime = TimeSpan.FromSeconds(1)
                };
                using var client = new HttpClient(handler)
                {
                    Timeout = TimeSpan.FromSeconds(2.5)
                };
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                linkedCts.CancelAfter(TimeSpan.FromSeconds(2.5));

                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
                var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token);
                if (resp.IsSuccessStatusCode || (int)resp.StatusCode == 204)
                {
                    return true;
                }
            }
            catch
            {

            }
        }
        return false;
    }

    private void Log(string line) =>
        LogLineAppended?.Invoke(this, $"{DateTime.Now:HH:mm:ss} {line}");

    public void Dispose()
    {
        _connectCts?.Cancel();
        _connectCts?.Dispose();
        _gate.Dispose();
    }
}
