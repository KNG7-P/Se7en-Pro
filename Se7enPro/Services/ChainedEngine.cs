using System;
using System.Collections.Generic;
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
    private readonly ConnectionMethod _method;

    private const int ChainOuterSocksPort = 1820;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _connectCts;

    public ChainedEngine(
        ILogger<ChainedEngine> logger,
        ISettingsService settings,
        AetherEngine outer,
        TunnelCoreManager psiphon,
        TorEngine tor,
        ConnectionMethod method)
    {
        _logger = logger;
        _settings = settings;
        _outer = outer;
        _psiphon = psiphon;
        _tor = tor;
        _method = method;

        AttachEvents(_outer, isOuter: true);
        AttachEvents(_psiphon, isOuter: false);
        AttachEvents(_tor, isOuter: false);
    }

    public ConnectionMethod Method => _method;
    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;

    private IConnectionEngine InnerEngine =>
        _method == ConnectionMethod.TorOverWarp ? _tor : _psiphon;

    public int SocksProxyPort => InnerEngine.SocksProxyPort;
    public int HttpProxyPort => InnerEngine.HttpProxyPort;
    public string ClientRegion => InnerEngine.ClientRegion;
    public string ConnectedServerRegion => InnerEngine.ConnectedServerRegion;
    public string CurrentRouteIp => InnerEngine.CurrentRouteIp;
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
        _ = Task.Run(() => RunChainAsync(ct), ct);
        return Task.CompletedTask;
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
            var isTor = _method == ConnectionMethod.TorOverWarp;
            var label = isTor ? "Tor over WARP" : "Psiphon over WARP";

            Log($"Starting multi-hop chained session ({label})...");
            SetProgress(10, "Connecting to Cloudflare WARP (outer leg)...");

            var outerTransport = (_settings.Settings.ChainedOuterTransport ?? "auto").Trim().ToLowerInvariant();
            var targetMethod = outerTransport switch
            {
                "wireguard" or "wg" => ConnectionMethod.WireGuard,
                "warp_on_warp" or "wow" => ConnectionMethod.WarpOnWarp,
                "masque" => ConnectionMethod.Masque,
                _ => ConnectionMethod.Masque,
            };

            AetherEngine.SocksPortOverride = ChainOuterSocksPort;
            AetherEngine.MethodOverride = targetMethod;
            Log($"Starting outer WARP transport ({targetMethod.ToDisplayName()})...");
            await _outer.StartAsync();

            var outerConnected = false;
            var maxWait = outerTransport == "auto" ? 80 : 200;
            for (var i = 0; i < maxWait && !ct.IsCancellationRequested; i++)
            {
                if (_outer.State == ConnectionState.Connected)
                {
                    outerConnected = true;
                    break;
                }
                if (_outer.State is ConnectionState.Error or ConnectionState.Disconnected)
                {
                    break;
                }
                await Task.Delay(500, ct);
            }

            if (!outerConnected && outerTransport == "auto" && !ct.IsCancellationRequested)
            {
                Log("Outer MASQUE transport did not connect; attempting fallback to WireGuard...");
                SetProgress(25, "MASQUE fallback -> Connecting via WireGuard (outer leg)...");
                try { await _outer.StopAsync(); } catch { }

                AetherEngine.MethodOverride = ConnectionMethod.WireGuard;
                await _outer.StartAsync();

                for (var i = 0; i < 120 && !ct.IsCancellationRequested; i++)
                {
                    if (_outer.State == ConnectionState.Connected)
                    {
                        outerConnected = true;
                        break;
                    }
                    if (_outer.State is ConnectionState.Error or ConnectionState.Disconnected)
                    {
                        break;
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
            SetProgress(55, $"Tunnelling {(isTor ? "Tor" : "Psiphon")} through WARP (inner leg)...");

            if (isTor)
            {
                TorEngine.Socks5ProxyOverride = $"127.0.0.1:{ChainOuterSocksPort}";
                await _tor.StartAsync();
            }
            else
            {
                TunnelCoreManager.UpstreamProxyUrlOverride = $"socks5://127.0.0.1:{ChainOuterSocksPort}";
                await _psiphon.StartAsync();
            }

            var innerConnected = false;
            for (var i = 0; i < 180 && !ct.IsCancellationRequested; i++)
            {
                if (InnerEngine.State == ConnectionState.Connected)
                {
                    innerConnected = true;
                    break;
                }
                if (InnerEngine.State is ConnectionState.Error or ConnectionState.Disconnected)
                {
                    break;
                }
                await Task.Delay(500, ct);
            }

            if (ct.IsCancellationRequested) return;

            if (innerConnected)
            {
                SetProgress(100, $"Connected ({label})");
                SetState(ConnectionState.Connected);
                Log($"Multi-hop {label} successfully connected!");
            }
            else
            {
                Log("Inner leg failed to establish; stopping chained session.");
                await StopInternalAsync();
                SetState(ConnectionState.Error);
            }
        }
        catch (OperationCanceledException)
        {
            await StopInternalAsync();
            SetState(ConnectionState.Disconnected);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Chained engine startup error");
            await StopInternalAsync();
            SetState(ConnectionState.Error);
        }
        finally
        {
            try { _gate.Release(); } catch { }
        }
    }

    public async Task StopAsync()
    {
        _connectCts?.Cancel();
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
                }));
        }
        catch { }
        finally
        {
            TunnelCoreManager.UpstreamProxyUrlOverride = null;
            TorEngine.Socks5ProxyOverride = null;
            AetherEngine.SocksPortOverride = null;
            AetherEngine.MethodOverride = null;
        }
    }

    private void AttachEvents(IConnectionEngine engine, bool isOuter)
    {
        engine.LogLineAppended += (_, line) =>
        {
            if (State is ConnectionState.Connecting or ConnectionState.Connected)
            {
                LogLineAppended?.Invoke(this, $"[{(isOuter ? "WARP" : "Inner")}] {line}");
            }
        };

        if (isOuter)
        {
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

    private void Log(string line) =>
        LogLineAppended?.Invoke(this, $"{DateTime.Now:HH:mm:ss} {line}");

    public void Dispose()
    {
        _connectCts?.Cancel();
        _connectCts?.Dispose();
        _gate.Dispose();
    }
}
