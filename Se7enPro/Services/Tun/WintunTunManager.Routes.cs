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

    private static readonly (string Addr, byte Prefix)[] PrivateV6Ranges =
    {
        ("fe80::", 10),
        ("fc00::", 7),
        ("ff00::", 8),
    };

    private async Task ApplyRoutesAsync(int tunIfIndex, CancellationToken ct)
    {
        void Track(WintunRouteApi.RouteEntry? e) { if (e is not null) lock (_routeLock) _appliedRoutes.Add(e); }
        void TrackCatchAll(WintunRouteApi.RouteEntry? e)
        {
            if (e is not null)
            {
                lock (_routeLock)
                {
                    _appliedRoutes.Add(e);
                    _catchAllRoutes.Add(e);
                }
            }
        }
        void TrackIfAdded(WintunRouteApi.RouteEntry? e) { if (e is not null) Track(e); }

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

        var realV6 = FindRealDefaultRouteV6();
        if (realV6 is { } v6)
        {
            _realIfIndexV6 = v6.IfIndex;
            _realGatewayV6 = v6.Gateway;
            _realRouteV6Known = true;
        }
        else
        {
            _realIfIndexV6 = 0;
            _realGatewayV6 = IPAddress.IPv6Any;
            _realRouteV6Known = false;
        }

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

            var acceptV6 = include ? _v6Enabled : _realRouteV6Known;

            foreach (var d in WidenDomainMatchSet(domains))
            {
                if (ct.IsCancellationRequested) return;
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    foreach (var ip in await Dns.GetHostAddressesAsync(d, timeout.Token))
                    {
                        var isV6 = ip.AddressFamily == AddressFamily.InterNetworkV6;
                        if (isV6 && !acceptV6) continue;
                        if (!isV6 && ip.AddressFamily != AddressFamily.InterNetwork) continue;
                        if (seenIps.Add(ip.ToString())) resolved.Add((ip, isV6 ? (byte)128 : (byte)32));
                    }
                }
                catch {  }
            }
        }

        if (!include)
        {

            foreach (var (addr, prefix) in PrivateV4Ranges)
            {
                var route = WintunRouteApi.AddRoute(realIfIndex, IPAddress.Parse(addr), prefix, realGateway);
                if (route != null) Track(route);
            }

            if (_v6Enabled && _realRouteV6Known)
            {
                foreach (var (addr, prefix) in PrivateV6Ranges)
                {
                    try { Track(WintunRouteApi.AddRoute(_realIfIndexV6, IPAddress.Parse(addr), prefix, _realGatewayV6)); }
                    catch (Exception ex) { WriteDiag($"protected v6 range {addr}/{prefix} not pinned: {ex.Message}"); }
                }
            }

            if (splitActive)
            {
                foreach (var dns in _underlyingDnsServers)
                {
                    if (!IPAddress.TryParse(dns, out var dnsIp)) continue;
                    if (IPAddress.IsLoopback(dnsIp)) continue;
                    var dnsRoute = WintunRouteApi.AddRoute(realIfIndex, dnsIp, 32, realGateway);
                    if (dnsRoute != null) Track(dnsRoute);
                }

                if (_v6Enabled && _realRouteV6Known)
                {
                    foreach (var dns in DetectUnderlyingDnsServersV6())
                    {
                        if (!IPAddress.TryParse(dns, out var dnsIp)) continue;
                        if (IPAddress.IsLoopback(dnsIp)) continue;
                        try { Track(WintunRouteApi.AddRoute(_realIfIndexV6, dnsIp, 128, _realGatewayV6)); }
                        catch (Exception ex) { WriteDiag($"underlying v6 resolver {dnsIp} not pinned: {ex.Message}"); }
                    }
                }
            }

            foreach (var (addr, prefix) in resolved)
            {
                var isV6 = addr.AddressFamily == AddressFamily.InterNetworkV6;
                if (isV6 && !_realRouteV6Known) continue;
                try
                {
                    Track(isV6
                        ? WintunRouteApi.AddRoute(_realIfIndexV6, addr, prefix, _realGatewayV6)
                        : WintunRouteApi.AddRoute(realIfIndex, addr, prefix, realGateway));
                }
                catch (Exception ex) { WriteDiag($"bypass route {addr}/{prefix} not applied: {ex.Message}"); }
            }

            var currentMethod = ConnectionMethodExtensions.ParseConnectionMethod(s.ConnectionMethod);
            var needsAetherBypass = currentMethod.IsAether() || currentMethod.IsChained();
            if (needsAetherBypass)
            {
                Track(WintunRouteApi.AddRoute(realIfIndex, IPAddress.Parse("162.159.192.0"), 20, realGateway));
                Track(WintunRouteApi.AddRoute(realIfIndex, IPAddress.Parse("188.114.96.0"), 20, realGateway));
                Track(WintunRouteApi.AddRoute(realIfIndex, IPAddress.Parse("162.159.36.0"), 22, realGateway));
                Track(WintunRouteApi.AddRoute(realIfIndex, IPAddress.Parse("162.159.46.0"), 22, realGateway));

                if (!string.IsNullOrWhiteSpace(s.AetherManualPeer))
                    PinHostOrIpToRealGateway(s.AetherManualPeer, realIfIndex, realGateway);
                if (!string.IsNullOrWhiteSpace(s.AetherEndpointMasque))
                    PinHostOrIpToRealGateway(s.AetherEndpointMasque, realIfIndex, realGateway);
                if (!string.IsNullOrWhiteSpace(s.AetherEndpointWireguard))
                    PinHostOrIpToRealGateway(s.AetherEndpointWireguard, realIfIndex, realGateway);
                if (!string.IsNullOrWhiteSpace(s.AetherEndpointWarp))
                    PinHostOrIpToRealGateway(s.AetherEndpointWarp, realIfIndex, realGateway);
                if (!string.IsNullOrWhiteSpace(s.AetherEndpointMasqueOnMasque))
                    PinHostOrIpToRealGateway(s.AetherEndpointMasqueOnMasque, realIfIndex, realGateway);
                if (!string.IsNullOrWhiteSpace(s.AetherMimOuter))
                    PinHostOrIpToRealGateway(s.AetherMimOuter, realIfIndex, realGateway);
            }

            if (s.UpstreamProxyEnabled && !string.IsNullOrWhiteSpace(s.UpstreamProxy))
            {
                PinHostOrIpToRealGateway(s.UpstreamProxy, realIfIndex, realGateway);
            }

            if (!string.IsNullOrWhiteSpace(TunnelCoreManager.UpstreamProxyUrlOverride))
            {
                PinHostOrIpToRealGateway(TunnelCoreManager.UpstreamProxyUrlOverride, realIfIndex, realGateway);
            }

            if (s.V2RayConfigs is { Count: > 0 })
            {
                var activeId = s.V2RayActiveConfigId;
                var activeCfg = s.V2RayConfigs.FirstOrDefault(c => c.Id == activeId)
                             ?? s.V2RayConfigs.FirstOrDefault(c => c.IsActive)
                             ?? s.V2RayConfigs.FirstOrDefault();
                if (activeCfg != null && !string.IsNullOrWhiteSpace(activeCfg.Address))
                {
                    PinHostOrIpToRealGateway(activeCfg.Address, realIfIndex, realGateway);
                }

                foreach (var cfg in s.V2RayConfigs)
                {
                    if (cfg.IsActive && !string.IsNullOrWhiteSpace(cfg.Address))
                    {
                        PinHostOrIpToRealGateway(cfg.Address, realIfIndex, realGateway);
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(_tunnel.CurrentRouteIp))
            {
                PinHostOrIpToRealGateway(_tunnel.CurrentRouteIp, realIfIndex, realGateway);
                var cleanServerIp = CleanIpOrHost(_tunnel.CurrentRouteIp);
                if (IPAddress.TryParse(cleanServerIp, out var routeIp) && !IPAddress.IsLoopback(routeIp))
                {
                    if (routeIp.AddressFamily == AddressFamily.InterNetwork)
                    {
                        Track(WintunRouteApi.AddRoute(realIfIndex, routeIp, 32, realGateway));
                    }
                    else if (routeIp.AddressFamily == AddressFamily.InterNetworkV6 && _realRouteV6Known)
                    {
                        try { Track(WintunRouteApi.AddRoute(_realIfIndexV6, routeIp, 128, _realGatewayV6)); }
                        catch (Exception ex) { WriteDiag($"tunnel server route {routeIp} not pinned: {ex.Message}"); }
                    }
                }
            }
        }

        if (include)
        {

            foreach (var (addr, prefix) in resolved)
            {
                var isV6 = addr.AddressFamily == AddressFamily.InterNetworkV6;
                if (isV6 && !_v6Enabled) continue;
                TrackIfAdded(WintunRouteApi.AddRoute(
                    tunIfIndex, addr, prefix, isV6 ? TunAddressV6 : TunAddressV4));
            }
        }

        if (!include)
        {

            ScanAndPinEngineConnections(realIfIndex, realGateway);

            lock (_routeLock)
            {

                _catchAllRoutes.Clear();
                _catchAllSuspended = false;
            }

            TrackCatchAll(WintunRouteApi.AddRoute(tunIfIndex, IPAddress.Parse("0.0.0.0"), 1, TunAddressV4));
            TrackCatchAll(WintunRouteApi.AddRoute(tunIfIndex, IPAddress.Parse("128.0.0.0"), 1, TunAddressV4));

            if (_v6Enabled)
            {
                TrackCatchAll(WintunRouteApi.AddRoute(tunIfIndex, IPAddress.Parse("::"), 1, TunAddressV6));
                TrackCatchAll(WintunRouteApi.AddRoute(tunIfIndex, IPAddress.Parse("8000::"), 1, TunAddressV6));
            }
        }

        WriteDiag($"routes applied: {_appliedRoutes.Count} entries "
                  + $"(mode={(include ? "include" : splitActive ? "exclude" : "full")}, "
                  + $"realGw={realGateway})");
    }

    private void SuspendCatchAllRoutes()
    {
        WintunRouteApi.RouteEntry[] routes;
        lock (_routeLock)
        {
            if (_catchAllSuspended || _catchAllRoutes.Count == 0) return;
            routes = _catchAllRoutes.ToArray();
            _catchAllSuspended = true;
        }

        foreach (var r in routes)
        {
            try { WintunRouteApi.DeleteRoute(r); }
            catch (Exception ex) { WriteDiag($"catch-all suspend: {r.Destination}/{r.Prefix} not lifted: {ex.Message}"); }
        }

        WintunRouteApi.FlushDnsCache();
        WriteDiag($"tunnel re-dialling: {routes.Length} catch-all route(s) lifted; adapter and core kept");
        ArmCatchAllGraceTimer();
    }

    private void ArmCatchAllGraceTimer()
    {
        var fresh = new CancellationTokenSource();
        CancellationTokenSource? previous;
        lock (_routeLock)
        {
            previous = _catchAllGraceCts;
            _catchAllGraceCts = fresh;
        }
        try { previous?.Cancel(); } catch { }
        try { previous?.Dispose(); } catch { }

        var token = fresh.Token;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(CatchAllSuspendGrace, token); }
            catch (OperationCanceledException) { return; }

            lock (_routeLock)
            {
                if (!_catchAllSuspended) return;
            }

            _catchAllGraceExpired = true;
            WriteDiag($"tunnel still re-dialling after {CatchAllSuspendGrace.TotalSeconds:0}s; stopping the tun session");
            try { await ReconcileAsync(); }
            catch (Exception ex) { WriteDiag($"grace-expiry reconcile failed: {ex.Message}"); }
        }, CancellationToken.None);
    }

    private void CancelCatchAllGraceTimer()
    {
        CancellationTokenSource? cts;
        lock (_routeLock)
        {
            cts = _catchAllGraceCts;
            _catchAllGraceCts = null;
        }
        try { cts?.Cancel(); } catch { }
        try { cts?.Dispose(); } catch { }
        _catchAllGraceExpired = false;
    }

    private bool ResumeCatchAllRoutes()
    {
        WintunRouteApi.RouteEntry[] routes;
        lock (_routeLock)
        {
            if (!_catchAllSuspended) return true;
            routes = _catchAllRoutes.ToArray();
            _catchAllSuspended = false;
        }

        CancelCatchAllGraceTimer();

        var failed = 0;
        foreach (var r in routes)
        {
            try { WintunRouteApi.AddRoute(r.IfIndex, r.Destination, r.Prefix, r.NextHop, r.Metric); }
            catch (Exception ex)
            {
                failed++;
                WriteDiag($"catch-all resume: {r.Destination}/{r.Prefix} not restored: {ex.Message}");
            }
        }

        WintunRouteApi.FlushDnsCache();
        if (failed > 0) return false;

        WriteDiag($"tunnel back up: {routes.Length} catch-all route(s) restored");
        return true;
    }

    private void PinHostOrIpToRealGateway(string rawHostOrUrl, int realIfIndex, IPAddress realGateway)
    {
        if (string.IsNullOrWhiteSpace(rawHostOrUrl)) return;

        try
        {
            var raw = rawHostOrUrl.Trim();
            var schemeIdx = raw.IndexOf("://", StringComparison.Ordinal);
            if (schemeIdx >= 0) raw = raw.Substring(schemeIdx + 3);

            var atIdx = raw.LastIndexOf('@');
            if (atIdx >= 0) raw = raw.Substring(atIdx + 1);

            var slashIdx = raw.IndexOf('/');
            if (slashIdx >= 0) raw = raw.Substring(0, slashIdx);

            string host;
            if (raw.Contains(':'))
            {
                var colonIdx = raw.LastIndexOf(':');
                host = raw.Substring(0, colonIdx).Trim();
            }
            else
            {
                var parts = raw.Split(new[] { ' ', '\t', '-' }, StringSplitOptions.RemoveEmptyEntries);
                host = parts.Length > 0 ? parts[0].Trim() : raw;
            }

            if (string.IsNullOrWhiteSpace(host)) return;

            if (IPAddress.TryParse(host, out var ip))
            {
                if (!IPAddress.IsLoopback(ip) && ip.AddressFamily == AddressFamily.InterNetwork)
                {
                    TrackRoute(WintunRouteApi.AddRoute(realIfIndex, ip, 32, realGateway));
                    WriteDiag($"pinned upstream proxy IP {ip} to real gateway {realGateway}");
                }
            }
            else
            {
                try
                {
                    var addrs = Dns.GetHostAddresses(host);
                    foreach (var addr in addrs)
                    {
                        if (!IPAddress.IsLoopback(addr) && addr.AddressFamily == AddressFamily.InterNetwork)
                        {
                            TrackRoute(WintunRouteApi.AddRoute(realIfIndex, addr, 32, realGateway));
                            WriteDiag($"pinned upstream proxy host {host} ({addr}) to real gateway {realGateway}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    WriteDiag($"could not resolve upstream host {host} for route pinning: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            WriteDiag($"failed to pin upstream target {rawHostOrUrl}: {ex.Message}");
        }
    }

    private void TrackRoute(WintunRouteApi.RouteEntry? e)
    {
        if (e is not null) lock (_routeLock) _appliedRoutes.Add(e);
    }
}
