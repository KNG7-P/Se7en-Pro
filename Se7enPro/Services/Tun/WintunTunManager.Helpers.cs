using System;
using System.Collections.Generic;
using System.IO;
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
        ApplyDnsPolicyToForwarder();
        WintunRouteApi.FlushDnsCache();

        _ = socksPort;
        await ApplyRoutesAsync(_tunIfIndex > 0 ? _tunIfIndex : 0, CancellationToken.None);

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

    // Applies the profile's Block and Bypass lists to WFP. Through-Tunnel needs nothing:
    // the TUN catch-all owns it. Called whenever the plan, the TUN method or the adapter
    // state changes, and a no-op when the resulting filter set would be identical.
    private void ApplySplitEnforcement(SplitRoutingPlan plan)
    {
        if (!plan.Enabled || UsesMihomoNativeSplitRules())
        {
            _splitRouter.Disarm();
            return;
        }

        if (!plan.HasAppRules
            && plan.BlockedIpCidrs.Count == 0
            && plan.DefaultCategory != SplitRuleCategory.Block)
        {
            _splitRouter.Disarm();
            return;
        }

        var blocked = ResolveExecutablePaths(plan, SplitRuleCategory.Block);
        var bypass = ResolveExecutablePaths(plan, SplitRuleCategory.Direct);

        // With default = Block the engine's MATCH,REJECT is not available on the
        // wintun/zeptun backends, so the deny has to come from WFP as well: the apps that
        // are explicitly allowed (Direct or Proxy) keep their filters, and everything
        // else is denied by a catch-all behind them.
        IReadOnlyList<string>? allowedWhenDefaultBlock = null;
        if (plan.DefaultCategory == SplitRuleCategory.Block)
        {
            allowedWhenDefaultBlock = ResolveExecutablePaths(plan, SplitRuleCategory.Direct)
                .Concat(ResolveExecutablePaths(plan, SplitRuleCategory.Proxy))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        else if (blocked.Count == 0 && bypass.Count == 0 && plan.BlockedIpCidrs.Count == 0)
        {
            _splitRouter.Disarm();
            return;
        }

        // Block IP ranges are address-scoped, so they go to WFP directly instead of
        // needing an app. Without this a Block CIDR would only work with mihomo.
        var blockedCidrs = plan.BlockedIpCidrs
            .OrderBy(static c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var signature = string.Join("|",
            plan.ProfileId,
            allowedWhenDefaultBlock is null ? "-" : "defaultblock",
            string.Join(";", blocked.OrderBy(static p => p, StringComparer.OrdinalIgnoreCase)),
            string.Join(";", bypass.OrderBy(static p => p, StringComparer.OrdinalIgnoreCase)),
            string.Join(";", blockedCidrs));

        if (!_splitRouter.Apply(blocked, bypass, signature, allowedWhenDefaultBlock, blockedCidrs))
        {
            WriteDiag($"split enforcement: WFP filters not installed: {_splitRouter.LastError}");
            return;
        }

        WriteDiag($"split enforcement: {(blocked.Count > 0 ? blocked.Count + " blocked" : "0 blocked")}, "
                  + $"{(bypass.Count > 0 ? bypass.Count + " bypassed" : "0 bypassed")}"
                  + (_splitRouter.BypassNeedsRouteFallback ? " (bypass via route fallback)" : ""));
        _lastAppliedSplitSignature = signature;
    }

    private List<string> ResolveExecutablePaths(SplitRoutingPlan plan, SplitRuleCategory category)
    {
        var paths = new List<string>();

        foreach (var rule in plan.Rules)
        {
            if (rule.Category != category) continue;

            if (rule.Target == SplitRuleTarget.ProcessPath)
            {
                if (!paths.Contains(rule.Value, StringComparer.OrdinalIgnoreCase)) paths.Add(rule.Value);
                continue;
            }

            if (rule.Target != SplitRuleTarget.ProcessName) continue;

            var known = ResolveProcessPathByName(rule.Value);
            if (known is not null && !paths.Contains(known, StringComparer.OrdinalIgnoreCase))
            {
                paths.Add(known);
            }
        }

        return paths;
    }

    private string? ResolveProcessPathByName(string processName)
    {
        if (string.IsNullOrEmpty(processName)) return null;

        if (_processPathByName.TryGetValue(processName, out var cached)) return cached;

        string? found = null;
        try
        {
            foreach (var running in System.Diagnostics.Process.GetProcessesByName(
                         Path.GetFileNameWithoutExtension(processName)))
            {
                using (running)
                {
                    try
                    {
                        var path = running.MainModule?.FileName;
                        if (!string.IsNullOrEmpty(path)) { found = path; break; }
                    }
                    catch { }
                }
            }
        }
        catch { }

        if (found is null)
        {
            try
            {
                found = InstalledAppsProvider.GetCachedPathForFileName(processName);
            }
            catch { }
        }

        _processPathByName[processName] = found;
        return found;
    }

    private string _splitPlanCacheKey = "";
    private SplitRoutingPlan? _splitPlanCachePlan;

    // Rebuilding the plan must never be on a hot path: the process monitor wakes every
    // 250 ms and the DNS policy is refreshed on every change. The plan is therefore
    // memoised on a cheap fingerprint — split-tunnel store revision plus the two
    // top-level switches — so equal settings reuse the same instance.
    internal SplitRoutingPlan BuildSplitPlan()
    {
        var s = _settings.Settings;
        var key = string.Concat(
            SplitTunnelProfileStore.Revision.ToString(),
            "|", s.SplitTunnelEnabled ? "1" : "0",
            "|", s.ActiveSplitTunnelProfileId ?? "",
            "|", s.ActiveSplitTunnelProfile ?? "");

        if (_splitPlanCachePlan is not null && string.Equals(key, _splitPlanCacheKey, StringComparison.Ordinal))
        {
            return _splitPlanCachePlan;
        }

        var plan = SplitRoutingPlanner.Build(s);
        _splitPlanCachePlan = plan;
        _splitPlanCacheKey = key;
        return plan;
    }

    private string ComputeSplitHash(int socksPort, SplitRoutingPlan plan)
    {
        var s = _settings.Settings;
        var raw = string.Join("|",
            socksPort.ToString(),
            s.SystemWideTunneling ? "1" : "0",
            plan.Hash,
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
