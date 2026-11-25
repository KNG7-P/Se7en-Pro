using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Se7enPro.Services;

public sealed partial class WintunTunManager
{
        private bool IsLocalProxyPort(int port)
    {
        if (port is 10808 or 10809 or 1819 or 1820 or 1821 or 1824 or 1825) return true;
        if (port > 0 && (port == _activeSocksPort || port == _tunnel.SocksProxyPort || port == _tunnel.HttpProxyPort)) return true;
        var s = _settings.Settings;
        if (s.UseCustomProxyPorts && (port == s.LocalSocksProxyPort || port == s.LocalHttpProxyPort)) return true;
        return false;
    }

    private readonly object _pidCacheLock = new();
    private readonly Dictionary<int, (string? Path, string? Name)> _sharedPidCache = new();
    private DateTime _pidCacheLastClearedUtc = DateTime.UtcNow;

    internal static string GetEffectiveChainedOuterTransport(Models.UserSettings s, ConnectionMethod method)
    {
        var specific = method switch
        {
            ConnectionMethod.PsiphonOverWarp => s.ChainedPsiphonOuterTransport,
            ConnectionMethod.TorOverWarp => s.ChainedTorOuterTransport,
            ConnectionMethod.FoxyOverWarp => s.ChainedFoxyOuterTransport,
            ConnectionMethod.LanternOverWarp => s.ChainedLanternOuterTransport,
            ConnectionMethod.VeepOverWarp => s.ChainedVeepOuterTransport,
            _ => s.ChainedOuterTransport
        };
        var normalized = (specific ?? "").Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(normalized)) return normalized;
        return (s.ChainedOuterTransport ?? "auto").Trim().ToLowerInvariant();
    }

    private (string? Path, string? Name) LookupProcessInfoCached(int pid)
    {
        lock (_pidCacheLock)
        {
            if ((DateTime.UtcNow - _pidCacheLastClearedUtc).TotalSeconds >= 30 || _sharedPidCache.Count > 2048)
            {
                _sharedPidCache.Clear();
                _pidCacheLastClearedUtc = DateTime.UtcNow;
            }
            if (_sharedPidCache.TryGetValue(pid, out var cached))
            {
                return cached;
            }
        }

        var fullPath = WintunRouteApi.TryGetProcessPath(pid);
        var fileName = string.IsNullOrEmpty(fullPath) ? null : Path.GetFileName(fullPath);
        var info = (fullPath, fileName);

        lock (_pidCacheLock)
        {
            _sharedPidCache[pid] = info;
        }
        return info;
    }

    private bool IsProtectedEngineIp(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        var s = _settings.Settings;
        var currentMethod = ConnectionMethodExtensions.ParseConnectionMethod(s.ConnectionMethod);
        var outerTransport = GetEffectiveChainedOuterTransport(s, currentMethod);
        var needsWarp = currentMethod.UsesWarp()
            || (currentMethod.IsChained() && outerTransport is "auto" or "masque" or "wireguard" or "warp_on_warp" or "masque_in_masque");
        if (needsWarp)
        {
            if (b[0] == 162 && b[1] == 159 && (b[2] & 0xC0) == 192) return true;
            if (b[0] == 188 && b[1] == 114 && (b[2] & 0xFC) == 96) return true;
        }
        if (currentMethod.IsFoxy())
        {
            if (b[0] == 23 && b[1] == 235 && b[2] == 42) return true;
        }
        return false;
    }

    private static readonly (byte[] Bytes, byte Prefix)[] NonRoutableV4Ranges =
    {
        (IPAddress.Parse("0.0.0.0").GetAddressBytes(), 8),
        (IPAddress.Parse("10.0.0.0").GetAddressBytes(), 8),
        (IPAddress.Parse("127.0.0.0").GetAddressBytes(), 8),
        (IPAddress.Parse("169.254.0.0").GetAddressBytes(), 16),
        (IPAddress.Parse("172.16.0.0").GetAddressBytes(), 12),
        (IPAddress.Parse("192.168.0.0").GetAddressBytes(), 16),
        (IPAddress.Parse("198.18.0.0").GetAddressBytes(), 15),   
        (IPAddress.Parse("100.64.0.0").GetAddressBytes(), 10),
        (IPAddress.Parse("224.0.0.0").GetAddressBytes(), 4),     
        (IPAddress.Parse("255.255.255.255").GetAddressBytes(), 32),
    };

    private static bool IsValidPublicInternetIpv4(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var bytes = ip.GetAddressBytes();

        foreach (var (rangeBytes, prefix) in NonRoutableV4Ranges)
        {
            var fullBytes = prefix / 8;
            var remBits = prefix % 8;
            var match = true;

            for (var i = 0; i < fullBytes; i++)
            {
                if (bytes[i] != rangeBytes[i]) { match = false; break; }
            }
            if (match && remBits > 0)
            {
                var mask = (byte)(0xFF << (8 - remBits));
                if ((bytes[fullBytes] & mask) != (rangeBytes[fullBytes] & mask))
                {
                    match = false;
                }
            }
            if (match) return false;
        }

        return true;
    }

    private void SweepProcessConnectionsNow()
    {
        var plan = BuildSplitPlan();
        if (!plan.Enabled) return;

        ScanAndPinProcessConnections(plan);
    }

    private void SweepEngineBypassConnections()
    {
        long routeGeneration;
        int realIfIndex;
        IPAddress realGateway;
        lock (_routeLock)
        {
            if (!_realRouteKnown) return;
            routeGeneration = _routeGeneration;
            realIfIndex = _realIfIndex;
            realGateway = _realGateway;
        }

        void PinBypass(IPAddress ip, string source)
        {
            if (IPAddress.IsLoopback(ip) || !IsValidPublicInternetIpv4(ip)) return;

            var ipKey = ip.ToString();
            try
            {
                lock (_routeLock)
                {
                    if (routeGeneration != _routeGeneration || !_realRouteKnown) return;
                    if (_dynamicRoutes.ContainsKey(ipKey)) return;

                    var entry = WintunRouteApi.AddRoute(realIfIndex, ip, 32, realGateway);
                    _appliedRoutes.Add(entry);
                    _dynamicRoutes[ipKey] = (entry, source);
                }

                WriteDiag($"engine bypass: {source} → {ip}; host route pinned direct (real gw)");
                PersistRouteJournal();
            }
            catch (Exception ex)
            {
                WriteDiag($"engine bypass: route for {ip} ({source}) not pinned: {ex.Message}");
            }
        }

        var endpoint = _tunnel.ServerEndpointAddress?.Trim() ?? "";
        if (endpoint.Length > 0)
        {
            if (IPAddress.TryParse(endpoint, out var endpointIp))
            {
                PinBypass(endpointIp, "engine:endpoint");
            }
            else
            {
                try
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            foreach (var a in (await Dns.GetHostAddressesAsync(endpoint).ConfigureAwait(false))
                                         .Where(a => a.AddressFamily == AddressFamily.InterNetwork))
                            {
                                PinBypass(a, "engine:endpoint");
                            }
                        }
                        catch { }
                    });
                }
                catch { }
            }
        }

        var connections = WintunRouteApi.GetActiveTcpConnections();
        if (connections.Count == 0) return;

        foreach (var conn in connections)
        {
            var pid = conn.Pid;
            var procInfo = LookupProcessInfoCached(pid);

            if (string.IsNullOrEmpty(procInfo.Name)) continue;
            if (!EngineProcessNames.All.Contains(procInfo.Name, StringComparer.OrdinalIgnoreCase)) continue;

            if (string.Equals(procInfo.Name, EngineProcessNames.Shard, StringComparison.OrdinalIgnoreCase)
                || string.Equals(procInfo.Name, "shard.exe", StringComparison.OrdinalIgnoreCase))
            {
                var ep = _tunnel.ServerEndpointAddress?.Trim() ?? "";
                if (ep.Length > 0 && IPAddress.TryParse(ep, out var epIp) && !conn.RemoteIp.Equals(epIp))
                {
                    continue;
                }
            }

            PinBypass(conn.RemoteIp, $"engine:{procInfo.Name} (pid {pid})");
        }
    }

    // The monitor only exists for what WFP cannot decide statically:
    //  - Blocked applications that already hold connections (closed immediately);
    //  - Bypass applications when the redirect layer refused our filters, or when the
    //    active TUN core is not covered by WFP at all (route pinning fallback);
    //  - name-only rules (an .exe without a known absolute path) resolved per process.
    // Through-Tunnel applications are handled by the TUN catch-all and need no sweep.
    private async Task RunProcessSplitMonitorAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var hasWork = false;
            try
            {
                SweepEngineBypassConnections();

                var plan = BuildSplitPlan();
                if (plan.Enabled && (plan.BlockValues.Count > 0 || plan.DirectValues.Count > 0))
                {
                    hasWork = true;
                    ScanAndPinProcessConnections(plan);
                }
            }
            catch (Exception ex)
            {
                WriteDiag($"process split monitor error: {ex.Message}");
            }

            try
            {
                await Task.Delay(hasWork ? 250 : 1000, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void ScanAndPinProcessConnections(SplitRoutingPlan plan)
    {
        if (!_realRouteKnown) return;

        var connections = WintunRouteApi.GetActiveTcpConnections();
        if (connections.Count == 0) return;

        var needDirectPinning = _splitRouter is null
            || !_splitRouter.IsArmed
            || _splitRouter.BypassNeedsRouteFallback;

        foreach (var conn in connections)
        {
            var pid = conn.Pid;
            var procInfo = LookupProcessInfoCached(pid);

            if (string.IsNullOrEmpty(procInfo.Name)) continue;

            var category = SplitRoutingPlan.ResolveCategory(
                procInfo.Name, procInfo.Path, plan.DefaultCategory, plan.Rules);

            // The default category must NOT be skipped: when the default is Direct,
            // every unlisted app belongs to Direct and has to be pinned to the physical
            // gateway exactly like an explicitly-listed Direct app. Skipping it (the old
            // behaviour) left the whole unlisted majority riding the TUN and being
            // redirected back out by the engine instead of going direct.
            //
            // Only the Proxy default needs no work here: the TUN catch-all already owns
            // that traffic, which is what the early return was originally written for.
            if (category == SplitRuleCategory.Proxy
                && plan.DefaultCategory == SplitRuleCategory.Proxy)
            {
                continue;
            }

            var remoteIp = conn.RemoteIp;

            if (category == SplitRuleCategory.Block)
            {
                if (IPAddress.IsLoopback(remoteIp)) continue;
                try { WintunRouteApi.TryCloseTcpConnection(conn.Raw); } catch { }
                continue;
            }

            if (category != SplitRuleCategory.Direct) continue;
            if (!needDirectPinning) continue;
            if (IPAddress.IsLoopback(remoteIp)) continue;
            if (!IsValidPublicInternetIpv4(remoteIp)) continue;

            var ipKey = remoteIp.ToString();
            var needsAdd = false;
            lock (_routeLock)
            {
                if (!_dynamicRoutes.ContainsKey(ipKey))
                {
                    needsAdd = true;
                }
            }

            if (!needsAdd) continue;

            try
            {
                var entry = WintunRouteApi.AddRoute(_realIfIndex, remoteIp, 32, _realGateway);

                var added = false;
                lock (_routeLock)
                {
                    if (!_dynamicRoutes.ContainsKey(ipKey))
                    {
                        _appliedRoutes.Add(entry);
                        _dynamicRoutes[ipKey] = (entry, $"app:{procInfo.Name}");
                        added = true;
                    }
                }

                if (!added)
                {
                    try { WintunRouteApi.DeleteRoute(entry); } catch { }
                }
                else
                {
                    var isCoreProcess = EngineProcessNames.All.Contains(
                        procInfo.Name, StringComparer.OrdinalIgnoreCase);
                    if (!isCoreProcess)
                    {
                        WintunRouteApi.TryCloseTcpConnection(conn.Raw);
                    }

                    WriteDiag($"split bypass: '{procInfo.Name}' (pid {pid}) → {remoteIp}; host route pinned direct (real gw)");
                    PersistRouteJournal();
                }
            }
            catch (Exception ex)
            {
                WriteDiag($"split bypass: route for {remoteIp} ({procInfo.Name}) not pinned: {ex.Message}");
            }
        }
    }
}
