using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Se7enPro.Services;

public sealed partial class WintunTunManager
{
        private static readonly (string Addr, byte Prefix)[] PrivateV4Ranges =
    {
        ("10.0.0.0", 8), ("172.16.0.0", 12), ("192.168.0.0", 16),
        ("169.254.0.0", 16), ("100.64.0.0", 10), ("224.0.0.0", 4),
        ("255.255.255.255", 32),
    };

    private async Task ApplyRoutesAsync(int tunIfIndex, CancellationToken ct)
    {
        var s = _settings.Settings;
        var splitActive = s.SplitTunnelEnabled;
        var include = splitActive && string.Equals((s.SplitTunnelMode ?? "exclude").Trim(), "include", StringComparison.OrdinalIgnoreCase);

        var domains = new List<string>();
        var ipCidrs = new List<string>();
        var procNames = new List<string>();
        var procPaths = new List<string>();

        if (splitActive)
        {
            SplitRules.ClassifySplitEntries(s, out domains, out ipCidrs, out procNames, out procPaths);
        }

        if (splitActive && (procNames.Count + procPaths.Count > 0))
        {
            var ruleNames = string.Join(", ", procNames.Concat(procPaths.Select(System.IO.Path.GetFileName)));
            WriteDiag($"per-application split active: monitoring {procNames.Count + procPaths.Count} rule(s) ({ruleNames})");
        }

        var real = FindRealDefaultRouteV4()
            ?? throw new InvalidOperationException("No physical IPv4 default gateway found to anchor split routes.");
        var (realIfIndex, realGateway) = real;

        long routeGeneration;
        lock (_routeLock)
        {
            _realIfIndex = realIfIndex;
            _realGateway = realGateway;
            _realRouteKnown = true;
            _tunIfIndex = tunIfIndex;
            routeGeneration = _routeGeneration;
        }

        SweepEngineBypassConnections();
        SweepProcessConnectionsNow();

        
        
        
        var resolved = new List<(IPAddress Addr, byte Prefix)>();
        var seenIps = new HashSet<string>(StringComparer.Ordinal);
        if (splitActive)
        {
            foreach (var cidr in ipCidrs)
            {
                if (SplitRules.ParseIpCidr(cidr) is { } p && seenIps.Add(p.Addr.ToString()))
                    resolved.Add(p);
            }
        }

        
        
        if (!_v6Enabled && CanTunnelIpv6())
        {
            try
            {
                WintunRouteApi.SetAdapterIpAddress(
                    tunIfIndex, TunAddressV6, TunPrefixV6,
                    WintunRouteApi.TryGetAdapterLuid(TunInterfaceName));
                _v6Enabled = true;
                _dnsForwarder?.SetSuppressIpv6Queries(false);
                WriteDiag("ipv6: global connectivity appeared after TUN start; enabling the v6 catch-all");
            }
            catch (Exception ex)
            {
                WriteDiag($"ipv6: address setup retry failed: {ex.Message}");
            }
        }

        List<WintunRouteApi.RouteEntry> previouslyApplied;
        lock (_routeLock) previouslyApplied = _appliedRoutes.ToList();

        var newApplied = new List<WintunRouteApi.RouteEntry>();
        WintunRouteApi.RouteEntry TrackNew(Func<WintunRouteApi.RouteEntry> addRoute)
        {
            lock (_routeLock)
            {
                if (routeGeneration != _routeGeneration) throw new OperationCanceledException();
                var entry = addRoute();
                if (!newApplied.Contains(entry)) newApplied.Add(entry);
                if (!_appliedRoutes.Contains(entry)) _appliedRoutes.Add(entry);
                return entry;
            }
        }

        WintunRouteApi.RouteEntry Bypass(IPAddress addr, byte prefix) =>
            TrackNew(() => WintunRouteApi.AddRoute(realIfIndex, addr, prefix, realGateway));

        WintunRouteApi.RouteEntry ThroughTun(IPAddress addr, byte prefix) =>
            TrackNew(() => WintunRouteApi.AddRoute(tunIfIndex, addr, prefix, TunAddressV4));

        static bool TryParseLiteralIpv4(string value, out IPAddress ip)
        {
            ip = IPAddress.None;
            if (value.Contains('/') || value.Contains(' ')) return false;
            if (!IPAddress.TryParse(value, out var parsed)) return false;
            if (parsed.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(parsed)) return false;
            ip = parsed;
            return true;
        }

        static bool IsWellKnownPublicDns(IPAddress ip)
        {
            var str = ip.ToString();
            return str is "1.1.1.1" or "1.0.0.1" or "8.8.8.8" or "8.8.4.4"
                or "9.9.9.9" or "149.112.112.112"
                or "208.67.222.222" or "208.67.220.220"
                or "94.140.14.14" or "94.140.15.15";
        }

        var currentMethod = ConnectionMethodExtensions.ParseConnectionMethod(s.ConnectionMethod);
        var outerTransport = GetEffectiveChainedOuterTransport(s, currentMethod);
        var needsWarpEdges = currentMethod.UsesWarp()
            || (currentMethod.IsChained() && outerTransport is "auto" or "masque" or "wireguard" or "warp_on_warp" or "masque_in_masque");
        var needsFastlyMasque = currentMethod.IsFoxy();

        foreach (var (addr, prefix) in PrivateV4Ranges)
        {
            if (addr == "10.0.0.0" && prefix == 8)
            {
                var gwBytes = realGateway.GetAddressBytes();
                if (gwBytes.Length == 4 && gwBytes[0] != 10)
                {
                    continue;
                }
            }
            Bypass(IPAddress.Parse(addr), prefix);
        }
        if (splitActive && !include)
        {
            foreach (var dns in _underlyingDnsServers)
            {
                if (!IPAddress.TryParse(dns, out var dnsIp)) continue;
                if (IPAddress.IsLoopback(dnsIp)) continue;
                if (IsWellKnownPublicDns(dnsIp)) continue;
                Bypass(dnsIp, 32);
            }
        }

        void TrackAppliedEntry(WintunRouteApi.RouteEntry entry)
        {
            if (!newApplied.Contains(entry)) newApplied.Add(entry);
        }

        Task BypassDialTargetAsync(string? value)
            => PinEngineEndpointTargetAsync(value, realIfIndex, realGateway, routeGeneration, TrackAppliedEntry, ct);

        await BypassDialTargetAsync(_tunnel.ServerEndpointAddress);
        foreach (var endpoint in _tunnel.ServerEndpointAddresses)
        {
            await BypassDialTargetAsync(endpoint);
        }
        if (currentMethod is ConnectionMethod.Shard)
        {
            await BypassDialTargetAsync(s.ShardCustomCfIp);
        }

        if (needsWarpEdges)
        {
            Bypass(IPAddress.Parse("162.159.192.0"), 18);
            Bypass(IPAddress.Parse("188.114.96.0"), 22);

            foreach (var peer in new[] { s.AetherWiwOuterPeer, s.AetherMimOuterPeer, s.AetherManualPeer })
            {
                if (string.IsNullOrWhiteSpace(peer)) continue;
                var host = peer.Trim();
                var colon = host.IndexOf(':');
                if (colon > 0) host = host[..colon];
                if (TryParseLiteralIpv4(host, out _)) await BypassDialTargetAsync(host);
            }
        }

        if (needsFastlyMasque)
        {
            Bypass(IPAddress.Parse("23.235.42.0"), 24);
        }

        var mihomoNativeSplit = UsesMihomoNativeSplitRules();
        if (!include || mihomoNativeSplit)
        {
            if (!include)
            {
                foreach (var (addr, prefix) in resolved)
                {
                    if (addr.AddressFamily == AddressFamily.InterNetwork)
                    {
                        Bypass(addr, prefix);
                    }
                }
            }

            PersistRouteJournal();

            ThroughTun(IPAddress.Parse("0.0.0.0"), 1);
            ThroughTun(IPAddress.Parse("128.0.0.0"), 1);

            if (_v6Enabled)
            {
                TrackNew(() => WintunRouteApi.AddRoute(tunIfIndex, IPAddress.Parse("::"), 1, TunAddressV6));
                TrackNew(() => WintunRouteApi.AddRoute(tunIfIndex, IPAddress.Parse("8000::"), 1, TunAddressV6));
            }
            else
            {
                WriteDiag("ipv6: no global v6 address on the physical link; v6 stays outside the TUN");
            }
        }

        if (splitActive && !(include && mihomoNativeSplit))
        {
            foreach (var (addr, prefix) in resolved)
            {
                if (include)
                {
                    if (addr.AddressFamily == AddressFamily.InterNetwork)
                        ThroughTun(addr, prefix);
                    else if (_v6Enabled && addr.AddressFamily == AddressFamily.InterNetworkV6)
                        TrackNew(() => WintunRouteApi.AddRoute(tunIfIndex, addr, prefix, TunAddressV6));
                }
            }

            var resolveTargets = WidenDomainMatchSet(domains).Where(d => d.Contains('.')).ToList();
            if (resolveTargets.Count > 0)
            {
                using var throttler = new SemaphoreSlim(6);
                var tasks = resolveTargets.Select(async d =>
                {
                    if (ct.IsCancellationRequested) return (d, Array.Empty<IPAddress>());
                    await throttler.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        var domainIps = new List<IPAddress>();
                        if (_dnsForwarder is not null)
                        {
                            try
                            {
                                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
                                var policyIps = await _dnsForwarder.ResolveForPolicyAsync(d, linked.Token).ConfigureAwait(false);
                                if (policyIps is { Length: > 0 })
                                {
                                    domainIps.AddRange(policyIps);
                                }
                            }
                            catch { }
                        }

                        if (domainIps.Count == 0)
                        {
                            try
                            {
                                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
                                var addrs = await Dns.GetHostAddressesAsync(d, linked.Token).ConfigureAwait(false);
                                domainIps.AddRange(addrs);
                            }
                            catch { }
                        }

                        return (d, domainIps.ToArray());
                    }
                    catch
                    {
                        return (d, Array.Empty<IPAddress>());
                    }
                    finally
                    {
                        throttler.Release();
                    }
                });

                var allResults = await Task.WhenAll(tasks).ConfigureAwait(false);
                foreach (var (d, ips) in allResults)
                {
                    foreach (var ip in ips)
                    {
                        if (ip.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ip)) continue;
                        var key = ip.ToString();
                        if (!seenIps.Add(key)) continue;
                        bool isEngineRoute;
                        lock (_routeLock)
                        {
                            isEngineRoute = _dynamicRoutes.TryGetValue(key, out var existingDyn)
                                && existingDyn.Domain.StartsWith("engine:", StringComparison.OrdinalIgnoreCase);
                        }
                        if (isEngineRoute) continue;

                        var entry = include ? ThroughTun(ip, 32) : Bypass(ip, 32);
                        lock (_routeLock)
                        {
                            _dynamicRoutes[key] = (entry, d);
                        }
                    }
                }
            }
        }

        lock (_routeLock)
        {
            foreach (var dyn in _dynamicRoutes.Values)
            {
                if (!newApplied.Contains(dyn.Entry))
                    newApplied.Add(dyn.Entry);
            }
            _appliedRoutes.Clear();
            _appliedRoutes.AddRange(newApplied);
        }

        if (previouslyApplied.Count > 0)
        {
            List<WintunRouteApi.RouteEntry> staleStatics;
            lock (_routeLock)
            {
                staleStatics = previouslyApplied
                    .Distinct()
                    .Where(e => !_appliedRoutes.Contains(e))
                    .ToList();
                foreach (var e in staleStatics) _appliedRoutes.Remove(e);
            }

            foreach (var r in staleStatics)
            {
                try { WintunRouteApi.DeleteRoute(r); }
                catch { }
            }

            if (staleStatics.Count > 0)
            {
                WriteDiag($"routes applied: retired {staleStatics.Count} stale route(s)");
            }
        }

        WriteDiag($"routes applied: {_appliedRoutes.Count} entries "
                  + $"(mode={(include ? "include" : splitActive ? "exclude" : "full")}, "
                  + $"realGw={realGateway})");

        PersistRouteJournal();
    }
}
