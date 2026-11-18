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
        var s = _settings.Settings;
        if (!s.SplitTunnelEnabled)
        {
            return new SocksDnsForwarder.SplitPolicy
            {
                ExcludeMode = true,
                Domains = new List<string>(),
                LocalDnsIp = null,
                AddressSeen = null,
            };
        }

        SplitRules.ClassifySplitEntries(s, out var domains, out _, out _, out _);
        var include = string.Equals((s.SplitTunnelMode ?? "exclude").Trim(), "include", StringComparison.OrdinalIgnoreCase);
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

        if (domains.Count > 0 && localDns is null)
        {
            WriteDiag("NOTE: no underlying DNS server detected — bypass domains will resolve "
                      + "through the tunnel and may still surface the VPN IP.");
        }

        return new SocksDnsForwarder.SplitPolicy
        {
            ExcludeMode = !include,
            Domains = WidenDomainMatchSet(domains),
            LocalDnsIp = localDns,
            AddressSeen = OnSplitAddressSeen,
        };
    }

        private void OnSplitAddressSeen(IPAddress ip, string queriedName, bool viaTunnel)
    {
        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return;

        var s = _settings.Settings;
        if (!s.SplitTunnelEnabled) return;

        var include = string.Equals((s.SplitTunnelMode ?? "exclude").Trim(), "include", StringComparison.OrdinalIgnoreCase);
        if (viaTunnel != include) return;
        if (include && UsesMihomoNativeSplitRules()) return;
        if (!include && !_realRouteKnown) return;
        if (include && IsProtectedEngineIp(ip)) return;

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

            var entry = include
                ? WintunRouteApi.AddRoute(_tunIfIndex, ip, 32, TunAddressV4)
                : WintunRouteApi.AddRoute(_realIfIndex, ip, 32, _realGateway);

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
                WriteDiag($"split dns: '{queriedName}' → {ip}; host route pinned "
                          + $"{(include ? "via tunnel" : "direct (real gw)")}");
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
                var s = _settings.Settings;
                if (s.SplitTunnelEnabled && _dnsForwarder is not null)
                {
                    SplitRules.ClassifySplitEntries(s, out var domains, out _, out _, out _);
                    foreach (var d in WidenDomainMatchSet(domains))
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
