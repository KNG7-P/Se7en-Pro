using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Se7enPro.Services;

public sealed partial class WintunTunManager
{
        private async Task ReapplyRoutesAsync(int socksPort)
    {
        SplitRules.ClassifySplitEntries(_settings.Settings, out var domains, out _, out var procNames, out var procPaths);
        var matchSet = WidenDomainMatchSet(domains);

        // Work out which dynamically pinned routes no longer match the split rules.
        // This only touches the dynamic map: _appliedRoutes keeps the full current set
        // until the new set has actually been applied (see below).
        var doomed = new List<WintunRouteApi.RouteEntry>();
        lock (_routeLock)
        {
            foreach (var kv in _dynamicRoutes.ToList())
            {
                if (kv.Value.Domain.StartsWith("app:", StringComparison.OrdinalIgnoreCase))
                {
                    var appName = kv.Value.Domain.Substring(4);
                    if (procNames.Contains(appName, StringComparer.OrdinalIgnoreCase) ||
                        procPaths.Any(p => string.Equals(System.IO.Path.GetFileName(p), appName, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }
                }
                else if (SocksDnsForwarder.MatchDomain(kv.Value.Domain, matchSet) is not null)
                {
                    continue;
                }
                doomed.Add(kv.Value.Entry);
                _dynamicRoutes.Remove(kv.Key);
            }
        }

        var nic = WintunRouteApi.FindAdapter(TunInterfaceName);
        if (nic is null)
        {
            WriteDiag("re-apply aborted: adapter gone");
            return;
        }

        // Apply FIRST, then delete. ApplyRoutesAsync re-asserts the static set —
        // including the 0.0.0.0/1 + 128.0.0.0/1 catch-all pair that captures all
        // traffic. Deleting first (the previous order) left the machine with no route
        // into the TUN for the whole duration of the await inside ApplyRoutesAsync
        // (DNS lookups up to 3s each, a full TCP-table sweep, blocking Dns calls), so
        // every packet left via the real NIC: an unbounded full-tunnel traffic leak.
        await ApplyRoutesAsync(WintunRouteApi.GetAdapterIndex(nic), CancellationToken.None);

        // Anything still needed was re-tracked by ApplyRoutesAsync above; only the
        // genuinely stale entries are still absent from the applied set.
        List<WintunRouteApi.RouteEntry> stale;
        lock (_routeLock)
        {
            stale = doomed
                .Distinct()
                .Where(e => !_appliedRoutes.Contains(e))
                .ToList();
            foreach (var e in stale) _appliedRoutes.Remove(e);
        }

        foreach (var r in stale)
        {
            try { WintunRouteApi.DeleteRoute(r); } catch { }
        }

        if (stale.Count > 0)
        {
            WriteDiag($"re-apply: removed {stale.Count} stale split route(s); "
                      + $"{_appliedRoutes.Count} still applied");
        }

        ApplyDnsPolicyToForwarder();
        SweepProcessConnectionsNow();
        WintunRouteApi.FlushDnsCache();
    }

    /// <summary>
    /// Applies updated split tunneling policy and DNS resolver targets to the forwarder.
    /// </summary>
    private void ApplyDnsPolicyToForwarder()
    {
        var forwarder = _dnsForwarder;
        if (forwarder is null) return;

        forwarder.UpdateSplitPolicy(BuildSplitPolicy());

        var plan = new DnsResolverPolicy(_logger).Build(_settings.Settings, _v6Enabled);
        var targets = DnsResolverPolicy.RelayTargets(plan);

        if (targets.Count == 0)
        {
            WriteDiag("dns: WARNING — the new resolver list has no plain-UDP entry this "
                      + "forwarder can dial (strict mode with only DoT/DoH configured). "
                      + "The previous resolvers stay in use for this session.");
        }
        else
        {
            forwarder.UpdateRelayTargets(targets);
        }

        if (plan.Rejected.Count > 0)
        {
            WriteDiag("dns: ignored " + plan.Rejected.Count
                      + " unusable resolver entr(ies): " + string.Join("; ", plan.Rejected));
        }
        WriteDiag(plan.HasUserEntries
            ? $"dns: relay targets updated ({targets.Count}); "
              + (plan.Strict ? "strict mode, defaults suppressed" : "defaults kept as fallback")
            : $"dns: relay targets reset to built-in defaults ({targets.Count})");
    }

    private string ComputeSplitHash(int socksPort)
    {
        SplitRules.ClassifySplitEntries(
            _settings.Settings, out var domains, out var ips, out var procNames, out var procPaths);
        var s = _settings.Settings;
        var raw = string.Join("|",
            socksPort.ToString(),
            s.SystemWideTunneling ? "1" : "0",
            s.SplitTunnelEnabled ? "1" : "0",
            (s.SplitTunnelMode ?? "").Trim(),
            string.Join(",", domains),
            string.Join(",", ips),
            string.Join(",", procNames),
            string.Join(",", procPaths),
            string.Join(",", s.CustomDnsUdp ?? ""),
            string.Join(",", s.CustomDnsDot ?? ""),
            string.Join(",", s.CustomDnsDoh ?? ""),
            s.CustomDnsStrict ? "1" : "0");
        return Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(raw)));
    }

        internal static (int IfIndex, IPAddress Gateway)? FindRealDefaultRouteV4()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback
                    or NetworkInterfaceType.Tunnel) continue;
                if (WintunRouteApi.IsOwnTunAdapter(nic)) continue;

                var props = nic.GetIPProperties();
                var gw = props.GatewayAddresses
                    .Select(g => g?.Address)
                    .FirstOrDefault(a => a is not null
                                         && a.AddressFamily == AddressFamily.InterNetwork
                                         && !IPAddress.Any.Equals(a));
                if (gw is null) continue;

                var idx = props.GetIPv4Properties()?.Index;
                if (idx is not null) return (idx.Value, gw);
            }
        }
        catch { }
        return null;
    }

        internal static bool HasGlobalIPv6()
    {
        
        
        static bool IsGlobal(IPAddress a)
        {
            var b = a.GetAddressBytes();
            return b.Length == 16 && b[0] is >= 0x20 and <= 0x3F;
        }

        try
        {
            return NetworkInterface.GetAllNetworkInterfaces().Any(n =>
                n.OperationalStatus == OperationalStatus.Up
                && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel
                && !WintunRouteApi.IsOwnTunAdapter(n)
                && n.GetIPProperties().UnicastAddresses.Any(u => IsGlobal(u.Address)));
        }
        catch { return false; }
    }
}
