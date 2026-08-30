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

        var doomed = new List<WintunRouteApi.RouteEntry>();
        lock (_routeLock)
        {
            var survivors = new HashSet<WintunRouteApi.RouteEntry>();
            foreach (var kv in _dynamicRoutes.ToList())
            {
                if (kv.Value.Domain.StartsWith("app:", StringComparison.OrdinalIgnoreCase))
                {
                    var appName = kv.Value.Domain.Substring(4);
                    if (procNames.Contains(appName, StringComparer.OrdinalIgnoreCase) ||
                        procPaths.Any(p => string.Equals(System.IO.Path.GetFileName(p), appName, StringComparison.OrdinalIgnoreCase)))
                    {
                        survivors.Add(kv.Value.Entry);
                        continue;
                    }
                }
                else if (SocksDnsForwarder.MatchDomain(kv.Value.Domain, matchSet) is not null)
                {
                    survivors.Add(kv.Value.Entry);
                    continue;
                }
                _appliedRoutes.Remove(kv.Value.Entry);
                doomed.Add(kv.Value.Entry);
                _dynamicRoutes.Remove(kv.Key);
            }

            foreach (var e in _appliedRoutes)
            {
                if (!survivors.Contains(e)) doomed.Add(e);
            }
            _appliedRoutes.RemoveAll(e => !survivors.Contains(e));
        }

        foreach (var r in doomed.Distinct())
        {
            try { WintunRouteApi.DeleteRoute(r); } catch { }
        }

        var nic = WintunRouteApi.FindAdapter(TunInterfaceName);
        if (nic is null)
        {
            WriteDiag("re-apply aborted: adapter gone");
            return;
        }
        await ApplyRoutesAsync(WintunRouteApi.GetAdapterIndex(nic), CancellationToken.None);
        _dnsForwarder?.UpdateSplitPolicy(BuildSplitPolicy());
        SweepProcessConnectionsNow();
        WintunRouteApi.FlushDnsCache();
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
            string.Join(",", procPaths));
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
