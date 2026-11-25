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
        var plan = BuildSplitPlan();
        var splitActive = plan.Enabled;

        // Only Bypass domains may be resolved and pinned to the physical gateway. Through
        // Tunnel domains are owned by the engine rules and Block domains are rejected
        // before dialling; pinning either of those direct would silently defeat the mode.
        var domains = plan.DirectDomains;
        var ipCidrs = plan.DirectIpCidrs;

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
            if (plan.HasAppRules || resolved.Count > 0)
            {
                WriteDiag($"split profile '{plan.ProfileName}': default={plan.DefaultCategory}, "
                          + $"{plan.ProcessNames.Count + plan.ProcessPaths.Count} app rule(s), "
                          + $"{plan.BypassDomains.Count} bypass domain(s), {resolved.Count} bypass ip rule(s)");
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

        var usesMihomoNative = UsesMihomoNativeSplitRules();
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
        if (splitActive)
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

        // The tunnel always owns the default route. Split traffic is decided per app by
        // WFP, by mihomo's own process rules, or by the route monitor below; IP/DNS rules
        // are pinned afterwards. Nothing here inverts the catch-all.
        if (splitActive)
        {
            foreach (var (addr, prefix) in resolved)
            {
                if (addr.AddressFamily == AddressFamily.InterNetwork)
                {
                    Bypass(addr, prefix);
                }
            }

            // A Block IP rule has to reach the tunnel so the engine can reject it. On the
            // backends that have no rule engine an explicit host route into the TUN is
            // what keeps that range off the physical link; without it the catch-all split
            // would carry the range to the proxy and the rule would silently do nothing.
            foreach (var cidr in plan.BlockedIpCidrs)
            {
                if (SplitRules.ParseIpCidr(cidr) is not { } blockedRange) continue;
                if (blockedRange.Addr.AddressFamily != AddressFamily.InterNetwork) continue;
                if (IPAddress.IsLoopback(blockedRange.Addr)) continue;

                TrackNew(() => WintunRouteApi.AddRoute(
                    tunIfIndex,
                    blockedRange.Addr,
                    blockedRange.Prefix == 0 ? (byte)1 : blockedRange.Prefix,
                    TunAddressV4,
                    metric: 1));
            }

            if (plan.BlockedIpCidrs.Count > 0)
            {
                WriteDiag($"split: {plan.BlockedIpCidrs.Count} blocked range(s) routed into the tunnel");
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

        _ = tunIfIndex;

        if (splitActive && usesMihomoNative)
        {
            // mihomo resolves and routes these itself from its own rule list.
            domains = new List<string>();
        }

        if (splitActive && domains.Count > 0)
        {
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

                        var entry = Bypass(ip, 32);
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
                  + $"(mode={(splitActive ? "split:" + plan.ProfileName : "full")}, "
                  + $"realGw={realGateway})");

        PersistRouteJournal();
    }
}
