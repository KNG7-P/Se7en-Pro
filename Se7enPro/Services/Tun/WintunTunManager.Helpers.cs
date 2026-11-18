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
        var s = _settings.Settings;
        var splitActive = s.SplitTunnelEnabled;
        var include = splitActive && string.Equals((s.SplitTunnelMode ?? "exclude").Trim(), "include", StringComparison.OrdinalIgnoreCase);
        var expectedIfIndex = include ? _tunIfIndex : _realIfIndex;

        SplitRules.ClassifySplitEntries(s, out var domains, out _, out var procNames, out var procPaths);
        var matchSet = WidenDomainMatchSet(domains);

        ApplyDnsPolicyToForwarder();
        WintunRouteApi.FlushDnsCache();

        var doomed = new List<WintunRouteApi.RouteEntry>();
        lock (_routeLock)
        {
            foreach (var kv in _dynamicRoutes.ToList())
            {
                if (kv.Value.Domain.StartsWith("engine:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var wrongInterface = expectedIfIndex > 0 && kv.Value.Entry.IfIndex != expectedIfIndex;
                if (splitActive && !wrongInterface)
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

        var tunIdx = WintunRouteApi.TryResolveAdapterIdentity(TunInterfaceName, out var resolvedIdx, out _) && resolvedIdx > 0
            ? (int)resolvedIdx
            : (_tunIfIndex > 0 ? _tunIfIndex : WintunRouteApi.GetAdapterIndex(nic));

        await ApplyRoutesAsync(tunIdx, CancellationToken.None);

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

        SweepProcessConnectionsNow();
        WintunRouteApi.FlushDnsCache();
        WintunRouteApi.ResetActiveNonEngineWebConnections();
    }

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

    private string ComputeEndpointHash()
    {
        var primary = _tunnel.ServerEndpointAddress ?? "";
        var all = _tunnel.ServerEndpointAddresses;
        var joined = all is { Count: > 0 }
            ? string.Join(",", all.OrderBy(static x => x, StringComparer.OrdinalIgnoreCase))
            : "";
        return Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes($"{primary}|{joined}")));
    }

    private async Task RefreshEndpointBypassRoutesAsync()
    {
        int realIfIndex;
        IPAddress? realGateway;
        bool realKnown;
        long routeGeneration;
        lock (_routeLock)
        {
            realIfIndex = _realIfIndex;
            realGateway = _realGateway;
            realKnown = _realRouteKnown;
            routeGeneration = _routeGeneration;
        }

        if (!realKnown || realGateway is null)
        {
            var real = FindRealDefaultRouteV4();
            if (real is null) return;
            (realIfIndex, realGateway) = real.Value;
        }

        SweepEngineBypassConnections();
        SweepProcessConnectionsNow();

        var targets = new List<string?> { _tunnel.ServerEndpointAddress };
        if (_tunnel.ServerEndpointAddresses is { Count: > 0 } extras)
        {
            targets.AddRange(extras);
        }

        foreach (var raw in targets)
        {
            await PinEngineEndpointTargetAsync(raw, realIfIndex, realGateway, routeGeneration);
        }

        PersistRouteJournal();
    }

    private async Task PinEngineEndpointTargetAsync(
        string? value, int realIfIndex, IPAddress realGateway, long routeGeneration,
        Action<WintunRouteApi.RouteEntry>? onAdded = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var target = value.Trim();

        if (target.StartsWith("[", StringComparison.Ordinal))
        {
            var close = target.IndexOf(']');
            if (close > 1) target = target.Substring(1, close - 1);
        }
        else
        {
            var colon = target.LastIndexOf(':');
            if (colon > 0 && target.IndexOf(':') == colon)
                target = target.Substring(0, colon);
        }

        void PinIp(IPAddress ip)
        {
            lock (_routeLock)
            {
                if (routeGeneration != _routeGeneration) throw new OperationCanceledException();
                var entry = WintunRouteApi.AddRoute(realIfIndex, ip, 32, realGateway);
                onAdded?.Invoke(entry);
                if (!_appliedRoutes.Contains(entry)) _appliedRoutes.Add(entry);
                _dynamicRoutes[ip.ToString()] = (entry, "engine:endpoint");
            }
        }

        if (IPAddress.TryParse(target, out var literal) && literal.AddressFamily == AddressFamily.InterNetwork)
        {
            PinIp(literal);
            return;
        }
        if (target.Contains('/') || target.Contains(' ')) return;
        try
        {
            using var dnsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            dnsCts.CancelAfter(TimeSpan.FromMilliseconds(2500));
            var addrs = await Dns.GetHostAddressesAsync(target, dnsCts.Token).ConfigureAwait(false);
            foreach (var a in addrs.Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a)))
            {
                PinIp(a);
            }
        }
        catch { }
    }

    internal string ComputeMihomoSplitHash()
    {
        var s = _settings.Settings;
        SplitRules.ClassifySplitEntries(s, out var domains, out var ips, out var procNames, out var procPaths);
        domains.Sort(StringComparer.OrdinalIgnoreCase);
        ips.Sort(StringComparer.OrdinalIgnoreCase);
        procNames.Sort(StringComparer.OrdinalIgnoreCase);
        procPaths.Sort(StringComparer.OrdinalIgnoreCase);

        var raw = string.Join("|",
            s.SplitTunnelEnabled ? "1" : "0",
            (s.SplitTunnelMode ?? "").Trim().ToLowerInvariant(),
            string.Join(",", domains),
            string.Join(",", ips),
            string.Join(",", procNames),
            string.Join(",", procPaths));
        return Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(raw)));
    }

    private static (NetworkInterface Nic, int IfIndex, IPAddress Gateway)? SelectRealDefaultRouteV4()
    {
        static bool IsVirtual(NetworkInterface nic)
        {
            var text = nic.Name + " " + nic.Description;
            return text.Contains("vEthernet", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)
                || text.Contains("WSL", StringComparison.OrdinalIgnoreCase)
                || text.Contains("VMware", StringComparison.OrdinalIgnoreCase)
                || text.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Loopback", StringComparison.OrdinalIgnoreCase);
        }

        static (NetworkInterface Nic, int IfIndex, IPAddress Gateway)? Pick(NetworkInterface[] nics, bool allowVirtual)
        {
            foreach (var nic in nics)
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback
                    or NetworkInterfaceType.Tunnel) continue;
                if (WintunRouteApi.IsOwnTunAdapter(nic)) continue;
                if (!allowVirtual && IsVirtual(nic)) continue;

                var props = nic.GetIPProperties();
                var gw = props.GatewayAddresses
                    .Select(g => g?.Address)
                    .FirstOrDefault(a => a is not null
                                         && a.AddressFamily == AddressFamily.InterNetwork
                                         && !IPAddress.Any.Equals(a));
                if (gw is null) continue;

                var idx = props.GetIPv4Properties()?.Index;
                if (idx is not null) return (nic, idx.Value, gw);
            }
            return null;
        }

        try
        {
            var nics = NetworkInterface.GetAllNetworkInterfaces();
            return Pick(nics, allowVirtual: false) ?? Pick(nics, allowVirtual: true);
        }
        catch { }
        return null;
    }

    internal static (int IfIndex, IPAddress Gateway)? FindRealDefaultRouteV4()
    {
        var hit = SelectRealDefaultRouteV4();
        return hit is { } r ? (r.IfIndex, r.Gateway) : null;
    }

    internal static string? FindRealDefaultInterfaceName()
    {
        var hit = SelectRealDefaultRouteV4();
        return hit?.Nic.Name;
    }

    internal bool UsesMihomoNativeSplitRules() =>
        string.Equals(_activeTunMethod, "mihomo", StringComparison.Ordinal)
        && !string.IsNullOrEmpty(FindRealDefaultInterfaceName());

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
