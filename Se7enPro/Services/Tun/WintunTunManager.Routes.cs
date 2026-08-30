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

        _realIfIndex = realIfIndex;
        _realGateway = realGateway;
        _realRouteKnown = true;
        _tunIfIndex = tunIfIndex;

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
            foreach (var d in WidenDomainMatchSet(domains))
            {
                if (ct.IsCancellationRequested) return;
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    foreach (var ip in await Dns.GetHostAddressesAsync(d, timeout.Token))
                    {
                        if (ip.AddressFamily != AddressFamily.InterNetwork) continue;
                        if (seenIps.Add(ip.ToString())) resolved.Add((ip, 32));
                    }
                }
                catch {  }
            }
        }

        void Track(WintunRouteApi.RouteEntry e) { lock (_routeLock) _appliedRoutes.Add(e); }

        if (!include)
        {

            foreach (var (addr, prefix) in PrivateV4Ranges)
            {
                Track(WintunRouteApi.AddRoute(realIfIndex, IPAddress.Parse(addr), prefix, realGateway));
            }

            foreach (var dns in _underlyingDnsServers)
            {
                if (!IPAddress.TryParse(dns, out var dnsIp)) continue;
                if (IPAddress.IsLoopback(dnsIp)) continue;
                Track(WintunRouteApi.AddRoute(realIfIndex, dnsIp, 32, realGateway));
            }
            foreach (var (addr, prefix) in resolved.Where(r => r.Addr.AddressFamily == AddressFamily.InterNetwork))
            {
                Track(WintunRouteApi.AddRoute(realIfIndex, addr, prefix, realGateway));
            }

            var currentMethod = ConnectionMethodExtensions.ParseConnectionMethod(s.ConnectionMethod);
            if (currentMethod.IsAether() || currentMethod.IsChained())
            {
                Track(WintunRouteApi.AddRoute(realIfIndex, IPAddress.Parse("162.159.192.0"), 20, realGateway));
                Track(WintunRouteApi.AddRoute(realIfIndex, IPAddress.Parse("188.114.96.0"), 20, realGateway));
                Track(WintunRouteApi.AddRoute(realIfIndex, IPAddress.Parse("162.159.36.0"), 22, realGateway));
                Track(WintunRouteApi.AddRoute(realIfIndex, IPAddress.Parse("162.159.46.0"), 22, realGateway));
            }
            if (IPAddress.TryParse(_tunnel.CurrentRouteIp, out var routeIp) &&
                routeIp.AddressFamily == AddressFamily.InterNetwork &&
                !IPAddress.IsLoopback(routeIp))
            {
                Track(WintunRouteApi.AddRoute(realIfIndex, routeIp, 32, realGateway));
            }
        }

        foreach (var (addr, prefix) in resolved.Where(r => r.Addr.AddressFamily == AddressFamily.InterNetwork))
        {
            if (include)
            {

                Track(WintunRouteApi.AddRoute(tunIfIndex, addr, prefix, TunAddressV4));
            }
        }

        if (!include)
        {
            Track(WintunRouteApi.AddRoute(tunIfIndex, IPAddress.Parse("0.0.0.0"), 1, TunAddressV4));
            Track(WintunRouteApi.AddRoute(tunIfIndex, IPAddress.Parse("128.0.0.0"), 1, TunAddressV4));

            if (_v6Enabled)
            {
                Track(WintunRouteApi.AddRoute(tunIfIndex, IPAddress.Parse("::"), 1, TunAddressV6));
                Track(WintunRouteApi.AddRoute(tunIfIndex, IPAddress.Parse("8000::"), 1, TunAddressV6));
            }
        }

        WriteDiag($"routes applied: {_appliedRoutes.Count} entries "
                  + $"(mode={(include ? "include" : splitActive ? "exclude" : "full")}, "
                  + $"realGw={realGateway})");
    }
}
