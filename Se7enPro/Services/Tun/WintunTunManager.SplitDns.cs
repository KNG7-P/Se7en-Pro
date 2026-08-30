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
        var localDns = _underlyingDnsServers.FirstOrDefault();

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

    private void OnSplitAddressSeen(IPAddress ip, string queriedName)
    {
        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return;

        var s = _settings.Settings;
        if (!s.SplitTunnelEnabled) return;

        var include = string.Equals((s.SplitTunnelMode ?? "exclude").Trim(), "include", StringComparison.OrdinalIgnoreCase);
        if (!include && !_realRouteKnown) return;

        try
        {
            var entry = include
                ? WintunRouteApi.AddRoute(_tunIfIndex, ip, 32, TunAddressV4)
                : WintunRouteApi.AddRoute(_realIfIndex, ip, 32, _realGateway);

            var key = ip.ToString();
            var added = false;
            lock (_routeLock)
            {
                if (!_dynamicRoutes.ContainsKey(key))
                {
                    _appliedRoutes.Add(entry);
                    _dynamicRoutes[key] = (entry, queriedName);
                    added = true;
                }
            }
            if (added)
            {
                WriteDiag($"split dns: '{queriedName}' → {ip}; host route pinned "
                          + $"{(include ? "via tunnel" : "direct (real gw)")}");
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
