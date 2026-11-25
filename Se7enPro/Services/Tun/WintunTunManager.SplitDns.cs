using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Se7enPro.Services;

public sealed partial class WintunTunManager
{
        internal static List<string> WidenDomainMatchSet(IEnumerable<string> domains)
    {
        var set = new List<string>();
        foreach (var d in domains)
        {
            if (d.Length == 0) continue;
            if (!set.Contains(d)) set.Add(d);
            if (d.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
            {
                var apex = d[4..];
                if (apex.Length > 0 && !set.Contains(apex)) set.Add(apex);
            }
        }
        return set;
    }

    private SocksDnsForwarder.SplitPolicy BuildSplitPolicy()
    {
        var plan = BuildSplitPlan();

        // The local resolver is used for Bypass domains only: those answers are then pinned
        // to the real gateway. Through Tunnel domains must resolve through the tunnel so
        // the destination IP follows the proxy path. Block domains are different again:
        // they are intercepted so the query can be answered with an empty result instead
        // of leaking a resolution the app should not have been able to make.
        var hasBlockDomains = plan.Enabled && plan.BlockedDomains.Count > 0;

        if (!plan.Enabled || (plan.BypassDomains.Count == 0 && !hasBlockDomains))
        {
            return new SocksDnsForwarder.SplitPolicy
            {
                ExcludeMode = true,
                Domains = new List<string>(),
                BlockedDomains = WidenDomainMatchSet(plan.BlockedDomains),
                LocalDnsIp = null,
                AddressSeen = null,
            };
        }

        static bool IsPublicTunnelDns(string ip) =>
            ip is "1.1.1.1" or "1.0.0.1" or "8.8.8.8" or "8.8.4.4"
               or "9.9.9.9" or "149.112.112.112"
               or "208.67.222.222" or "208.67.220.220"
               or "94.140.14.14" or "94.140.15.15";
        var localDns = _underlyingDnsServers.FirstOrDefault();
        if (localDns is not null && IsPublicTunnelDns(localDns))
        {
            localDns = _underlyingDnsServers.FirstOrDefault(d => !IsPublicTunnelDns(d))
                ?? (_realRouteKnown && !_realGateway.Equals(IPAddress.Any) ? _realGateway.ToString() : null);
        }

        return new SocksDnsForwarder.SplitPolicy
        {
            ExcludeMode = true,
            Domains = WidenDomainMatchSet(plan.BypassDomains),
            BlockedDomains = WidenDomainMatchSet(plan.BlockedDomains),
            LocalDnsIp = localDns,
            AddressSeen = OnSplitAddressSeen,
        };
    }

    // A domain rule resolves through the local resolver and the answer is pinned direct,
    // so a bypassed domain never even reveals the tunnel IP. Domain rules are "bypass
    // domains" by design; a blocked domain is rejected by WFP/mihomo before it is dialled.
    private void OnSplitAddressSeen(IPAddress ip, string queriedName, bool viaTunnel)
    {
        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return;

        var plan = BuildSplitPlan();
        if (!plan.Enabled || plan.BypassDomains.Count == 0) return;
        if (plan.BlockedDomains.Contains(queriedName)) return;
        if (!_realRouteKnown) return;

        try
        {
            var key = ip.ToString();
            lock (_routeLock)
            {
                if (_dynamicRoutes.TryGetValue(key, out var existingEngine)
                    && existingEngine.Domain.StartsWith("engine:", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            var entry = WintunRouteApi.AddRoute(_realIfIndex, ip, 32, _realGateway);

            var added = false;
            WintunRouteApi.RouteEntry? replaced = null;
            lock (_routeLock)
            {
                if (_dynamicRoutes.TryGetValue(key, out var existing))
                {
                    if (existing.Domain.StartsWith("engine:", StringComparison.OrdinalIgnoreCase))
                    {
                        replaced = entry;
                    }
                    else if (existing.Entry != entry)
                    {
                        replaced = existing.Entry;
                        _appliedRoutes.Remove(existing.Entry);
                        if (!_appliedRoutes.Contains(entry)) _appliedRoutes.Add(entry);
                        _dynamicRoutes[key] = (entry, queriedName);
                        added = true;
                    }
                }
                else
                {
                    if (!_appliedRoutes.Contains(entry)) _appliedRoutes.Add(entry);
                    _dynamicRoutes[key] = (entry, queriedName);
                    added = true;
                }
            }
            if (replaced is not null)
            {
                try { WintunRouteApi.DeleteRoute(replaced); } catch { }
            }
            if (added)
            {
                WriteDiag($"split dns: '{queriedName}' → {ip}; host route pinned direct (real gw)");
                PersistRouteJournal();
                WintunRouteApi.TryCloseTcpConnectionsTo(ip);
            }
        }
        catch (Exception ex)
        {
            WriteDiag($"split dns: route for {ip} not pinned: {ex.Message}");
        }
    }

        internal static IReadOnlyList<string> DetectUnderlyingDnsServersV4()
    {
        if (UnderlyingDnsServersOverride is not null)
        {
            return UnderlyingDnsServersOverride();
        }

        var preferred = new List<string>();
        var fallback = new List<string>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback
                    or NetworkInterfaceType.Tunnel) continue;
                if (WintunRouteApi.IsOwnTunAdapter(nic)) continue;

                var props = nic.GetIPProperties();
                var hasGateway = props.GatewayAddresses.Any(g => g?.Address is { } a
                    && a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                    && !IPAddress.Any.Equals(a));

                foreach (var dns in props.DnsAddresses)
                {
                    if (dns.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(dns)) continue;
                    if (dns.Equals(IPAddress.Any) || dns.Equals(IPAddress.Broadcast)) continue;
                    var text = dns.ToString();
                    if (text.StartsWith("169.254.", StringComparison.Ordinal)) continue;
                    if (preferred.Contains(text) || fallback.Contains(text)) continue;
                    (hasGateway ? preferred : fallback).Add(text);
                }
            }
        }
        catch { }
        return preferred.Concat(fallback).ToList();
    }

        private async Task RunSplitDnsRefresherAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(10), ct); }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var plan = BuildSplitPlan();
                if (plan.Enabled && _dnsForwarder is not null && plan.BypassDomains.Count > 0)
                {
                    foreach (var d in WidenDomainMatchSet(plan.BypassDomains))
                    {
                        if (ct.IsCancellationRequested) return;
                        if (!d.Contains('.')) continue;
                        try
                        {
                            var ips = await _dnsForwarder.ResolveForPolicyAsync(d, ct);
                            if (ips is { Length: > 0 })
                            {
                                WriteDiag($"split dns refresh: {d} → {string.Join(", ", ips.Select(i => i.ToString()))}");
                            }
                        }
                        catch (Exception ex)
                        {
                            WriteDiag($"split dns refresh: '{d}' failed: {ex.Message}");
                        }
                    }
                }
            }
            catch { }

            try { await Task.Delay(TimeSpan.FromSeconds(45), ct); }
            catch (OperationCanceledException) { return; }
        }
    }
}
