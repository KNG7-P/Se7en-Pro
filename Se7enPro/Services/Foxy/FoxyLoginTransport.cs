using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Se7enPro.Models;
using Se7enPro.Services.Lantern;
using Se7enPro.Services.Veep;

namespace Se7enPro.Services.Foxy;

public enum FoxyLoginTransportKind
{
    None = 0,
    Lantern = 1,
    Veep = 2,
    WireGuard = 3,
    Masque = 4,
    V2Ray = 5,
}

public sealed class FoxyLoginTransportSession : IAsyncDisposable
{
    private readonly IConnectionEngine? _ownedEngine;
    private readonly bool _clearAetherMethodOverride;
    private readonly bool _clearV2RayPortOverride;
    private int _disposed;

    internal FoxyLoginTransportSession(
        FoxyLoginTransportKind kind,
        string? socksProxyEndpoint,
        string displayLabel,
        IConnectionEngine? ownedEngine = null,
        bool clearAetherMethodOverride = false,
        bool clearV2RayPortOverride = false)
    {
        Kind = kind;
        SocksProxyEndpoint = socksProxyEndpoint;
        DisplayLabel = displayLabel;
        _ownedEngine = ownedEngine;
        _clearAetherMethodOverride = clearAetherMethodOverride;
        _clearV2RayPortOverride = clearV2RayPortOverride;
    }

    public FoxyLoginTransportKind Kind { get; }
    public string? SocksProxyEndpoint { get; }
    public string DisplayLabel { get; }
    public bool UsesTemporaryTunnel => _ownedEngine is not null;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try
        {
            if (_ownedEngine is not null)
                await _ownedEngine.StopAsync().ConfigureAwait(false);
        }
        catch
        {
        }
        finally
        {
            if (_clearAetherMethodOverride)
                AetherEngine.MethodOverride = null;
            if (_clearV2RayPortOverride)
                V2RayEngine.ClearSocksPortOverride();
        }
    }
}

internal static class FoxyLoginTransportStarter
{
    public static async Task<FoxyLoginTransportSession> StartAsync(
        FoxyLoginTransportKind kind,
        ITunnelCoreManager? activeTunnel,
        LanternEngine? lanternEngine,
        VeepEngine? veepEngine,
        AetherEngine? aetherEngine,
        V2RayEngine? v2rayEngine,
        ILogger? logger,
        Action<string>? onProgress,
        CancellationToken ct)
    {
        if (kind == FoxyLoginTransportKind.None)
        {
            return new FoxyLoginTransportSession(
                FoxyLoginTransportKind.None,
                socksProxyEndpoint: null,
                displayLabel: "Direct");
        }

        var (engine, displayName, aetherOverride, readyTimeout) = kind switch
        {
            FoxyLoginTransportKind.Lantern => ((IConnectionEngine?)lanternEngine, "Lantern", (ConnectionMethod?)null, TimeSpan.FromSeconds(50)),
            FoxyLoginTransportKind.Veep => (veepEngine, "Veep", null, TimeSpan.FromSeconds(45)),
            FoxyLoginTransportKind.WireGuard => (aetherEngine, "Aether WireGuard", ConnectionMethod.WireGuard, TimeSpan.FromSeconds(120)),
            FoxyLoginTransportKind.Masque => (aetherEngine, "Aether MASQUE", ConnectionMethod.Masque, TimeSpan.FromSeconds(120)),
            FoxyLoginTransportKind.V2Ray => (v2rayEngine, "V2Ray", null, TimeSpan.FromSeconds(45)),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        if (kind == FoxyLoginTransportKind.V2Ray)
        {
            var cfg = v2rayEngine?.ResolveActiveConfig();
            var problem = V2RayEngine.ValidateOutboundNode(cfg);
            if (problem is not null)
                throw new InvalidOperationException($"Cannot start V2Ray: {problem}");
        }

        if (activeTunnel is { State: ConnectionState.Connected } && activeTunnel.SocksProxyPort > 0 &&
            MatchesMethod(activeTunnel.ActiveMethod, kind))
        {
            var ep = $"127.0.0.1:{activeTunnel.SocksProxyPort}";
            var label = kind == FoxyLoginTransportKind.V2Ray
                ? await VerifyAndFormatV2RayLabelAsync(activeTunnel.SocksProxyPort, onProgress, ct).ConfigureAwait(false)
                : displayName;
            return new FoxyLoginTransportSession(kind, ep, label);
        }

        if (engine is null)
            throw new InvalidOperationException($"{displayName} engine is not available in this session.");

        if (engine.State == ConnectionState.Connected && engine.SocksProxyPort > 0 &&
            (!aetherOverride.HasValue || engine.Method == aetherOverride.Value))
        {
            var ep = $"127.0.0.1:{engine.SocksProxyPort}";
            var label = kind == FoxyLoginTransportKind.V2Ray
                ? await VerifyAndFormatV2RayLabelAsync(engine.SocksProxyPort, onProgress, ct).ConfigureAwait(false)
                : displayName;
            return new FoxyLoginTransportSession(kind, ep, label);
        }

        if (engine.State is ConnectionState.Connected or ConnectionState.Connecting)
        {
            throw new InvalidOperationException(
                $"{displayName} core is currently busy with {engine.Method.ToDisplayName()}. Disconnect the main tunnel first or choose None to use it.");
        }

        string? lastAlert = null;
        void OnProgressChanged(object? _, EventArgs __)
        {
            var msg = engine.ConnectProgressText;
            if (!string.IsNullOrWhiteSpace(msg))
                onProgress?.Invoke(msg);
        }
        void OnNotice(object? _, Notice n)
        {
            if (n.Data.ValueKind == System.Text.Json.JsonValueKind.Object &&
                n.Data.TryGetProperty("message", out var msgEl) &&
                msgEl.ValueKind == System.Text.Json.JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(msgEl.GetString()))
            {
                lastAlert = msgEl.GetString();
            }
        }

        engine.ConnectProgressChanged += OnProgressChanged;
        engine.NoticeReceived += OnNotice;

        if (aetherOverride.HasValue)
            AetherEngine.MethodOverride = aetherOverride.Value;

        var clearV2RayPortOverride = false;
        if (kind == FoxyLoginTransportKind.V2Ray)
        {
            var freePort = AllocateFreeLoopbackPortPair();
            V2RayEngine.SetSocksPortOverride(freePort);
            clearV2RayPortOverride = true;
        }

        var ownershipTransferred = false;
        try
        {
            onProgress?.Invoke($"Starting {displayName}...");
            logger?.LogInformation("[FoxyLogin] Starting temporary {Method} proxy...", displayName);

            await engine.StartAsync().ConfigureAwait(false);

            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < readyTimeout)
            {
                ct.ThrowIfCancellationRequested();

                if (engine.State == ConnectionState.Connected && engine.SocksProxyPort > 0)
                {
                    var socksPort = engine.SocksProxyPort;
                    var ep = $"127.0.0.1:{socksPort}";
                    var label = kind == FoxyLoginTransportKind.V2Ray
                        ? await VerifyAndFormatV2RayLabelAsync(socksPort, onProgress, ct).ConfigureAwait(false)
                        : displayName;
                    logger?.LogInformation("[FoxyLogin] Temporary {Method} proxy ready on {Endpoint}.", label, ep);
                    ownershipTransferred = true;
                    return new FoxyLoginTransportSession(
                        kind,
                        ep,
                        label,
                        ownedEngine: engine,
                        clearAetherMethodOverride: aetherOverride.HasValue,
                        clearV2RayPortOverride: clearV2RayPortOverride);
                }

                if (engine.State == ConnectionState.Error || engine.State == ConnectionState.Disconnected)
                {
                    var reason = !string.IsNullOrWhiteSpace(lastAlert)
                        ? lastAlert
                        : (!string.IsNullOrWhiteSpace(engine.ConnectProgressText)
                            ? engine.ConnectProgressText
                            : $"{displayName} failed to connect.");
                    throw new InvalidOperationException(reason);
                }

                await Task.Delay(150, ct).ConfigureAwait(false);
            }

            throw new TimeoutException($"Timed out waiting for {displayName} proxy to become ready.");
        }
        finally
        {
            engine.ConnectProgressChanged -= OnProgressChanged;
            engine.NoticeReceived -= OnNotice;

            if (!ownershipTransferred)
            {
                try { await engine.StopAsync().ConfigureAwait(false); } catch { }
                if (aetherOverride.HasValue)
                    AetherEngine.MethodOverride = null;
                if (clearV2RayPortOverride)
                    V2RayEngine.ClearSocksPortOverride();
            }
        }
    }

    private static async Task<string> VerifyAndFormatV2RayLabelAsync(
        int socksPort, Action<string>? onProgress, CancellationToken ct)
    {
        onProgress?.Invoke("Verifying V2Ray connection...");
        var proof = await SocksProbe.WaitForTunnelAsync(
            socksPort, DateTime.UtcNow.AddSeconds(15), ct, TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
        if (proof is null)
        {
            throw new InvalidOperationException(
                "Selected V2Ray config could not reach the internet. Please test or switch your V2Ray config.");
        }

        EndpointLocationResult? loc = null;
        try
        {
            using var locCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            locCts.CancelAfter(TimeSpan.FromSeconds(3));
            loc = await EndpointProbeHelper.ProbeAsync(socksPort, locCts.Token).ConfigureAwait(false);
        }
        catch
        {
        }

        return loc is not null && !string.IsNullOrWhiteSpace(loc.CountryCode)
            ? $"V2Ray ({loc.CountryCode})"
            : "V2Ray (Verified)";
    }

    private static int AllocateFreeLoopbackPortPair()
    {
        for (var attempt = 0; attempt < 24; attempt++)
        {
            var basePort = Random.Shared.Next(24000, 44000);
            try
            {
                using var l1 = new TcpListener(IPAddress.Loopback, basePort);
                l1.Start();
                using var l2 = new TcpListener(IPAddress.Loopback, basePort + 1);
                l2.Start();
                return basePort;
            }
            catch
            {
            }
        }
        return 24808;
    }

    private static bool MatchesMethod(ConnectionMethod method, FoxyLoginTransportKind kind) => kind switch
    {
        FoxyLoginTransportKind.Lantern => method == ConnectionMethod.Lantern,
        FoxyLoginTransportKind.Veep => method == ConnectionMethod.Veep,
        FoxyLoginTransportKind.WireGuard => method == ConnectionMethod.WireGuard,
        FoxyLoginTransportKind.Masque => method == ConnectionMethod.Masque,
        FoxyLoginTransportKind.V2Ray => method == ConnectionMethod.PsiphonOverV2Ray,
        _ => false,
    };
}
