using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Se7enPro.Services;

public sealed partial class WintunTunManager
{
        private bool IsLocalProxyPort(int port)
    {
        if (port is 10808 or 10809 or 1819 or 1820 or 1821 or 1824 or 1825) return true;
        if (port > 0 && (port == _activeSocksPort || port == _tunnel.SocksProxyPort || port == _tunnel.HttpProxyPort)) return true;
        var s = _settings.Settings;
        if (s.UseCustomProxyPorts && (port == s.LocalSocksProxyPort || port == s.LocalHttpProxyPort)) return true;
        return false;
    }

    private readonly object _pidCacheLock = new();
    private readonly Dictionary<int, (string? Path, string? Name)> _sharedPidCache = new();
    private DateTime _pidCacheLastClearedUtc = DateTime.UtcNow;

    internal static string GetEffectiveChainedOuterTransport(Models.UserSettings s, ConnectionMethod method)
    {
        var specific = method switch
        {
            ConnectionMethod.PsiphonOverWarp => s.ChainedPsiphonOuterTransport,
            ConnectionMethod.TorOverWarp => s.ChainedTorOuterTransport,
            ConnectionMethod.FoxyOverWarp => s.ChainedFoxyOuterTransport,
            ConnectionMethod.LanternOverWarp => s.ChainedLanternOuterTransport,
            ConnectionMethod.VeepOverWarp => s.ChainedVeepOuterTransport,
            _ => s.ChainedOuterTransport
        };
        var normalized = (specific ?? "").Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(normalized)) return normalized;
        return (s.ChainedOuterTransport ?? "auto").Trim().ToLowerInvariant();
    }

    private (string? Path, string? Name) LookupProcessInfoCached(int pid)
    {
        lock (_pidCacheLock)
        {
            if ((DateTime.UtcNow - _pidCacheLastClearedUtc).TotalSeconds >= 30 || _sharedPidCache.Count > 2048)
            {
                _sharedPidCache.Clear();
                _pidCacheLastClearedUtc = DateTime.UtcNow;
            }
            if (_sharedPidCache.TryGetValue(pid, out var cached))
            {
                return cached;
            }
        }

        var fullPath = WintunRouteApi.TryGetProcessPath(pid);
        var fileName = string.IsNullOrEmpty(fullPath) ? null : Path.GetFileName(fullPath);
        var info = (fullPath, fileName);

        lock (_pidCacheLock)
        {
            _sharedPidCache[pid] = info;
        }
        return info;
    }

    private bool IsProtectedEngineIp(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        var s = _settings.Settings;
        var currentMethod = ConnectionMethodExtensions.ParseConnectionMethod(s.ConnectionMethod);
        var outerTransport = GetEffectiveChainedOuterTransport(s, currentMethod);
        var needsWarp = currentMethod.UsesWarp()
            || (currentMethod.IsChained() && outerTransport is "auto" or "masque" or "wireguard" or "warp_on_warp" or "masque_in_masque");
        if (needsWarp)
        {
            if (b[0] == 162 && b[1] == 159 && (b[2] & 0xC0) == 192) return true;
            if (b[0] == 188 && b[1] == 114 && (b[2] & 0xFC) == 96) return true;
        }
        if (currentMethod.IsFoxy())
        {
            if (b[0] == 23 && b[1] == 235 && b[2] == 42) return true;
        }
        return false;
    }      

    
    
    
    
    
    private static readonly (byte[] Bytes, byte Prefix)[] NonRoutableV4Ranges =
    {
        (IPAddress.Parse("0.0.0.0").GetAddressBytes(), 8),
        (IPAddress.Parse("10.0.0.0").GetAddressBytes(), 8),
        (IPAddress.Parse("127.0.0.0").GetAddressBytes(), 8),
        (IPAddress.Parse("169.254.0.0").GetAddressBytes(), 16),
        (IPAddress.Parse("172.16.0.0").GetAddressBytes(), 12),
        (IPAddress.Parse("192.168.0.0").GetAddressBytes(), 16),
        (IPAddress.Parse("198.18.0.0").GetAddressBytes(), 15),   
        (IPAddress.Parse("100.64.0.0").GetAddressBytes(), 10),
        (IPAddress.Parse("224.0.0.0").GetAddressBytes(), 4),     
        (IPAddress.Parse("255.255.255.255").GetAddressBytes(), 32),
    };

    private static bool IsValidPublicInternetIpv4(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var bytes = ip.GetAddressBytes();

        foreach (var (rangeBytes, prefix) in NonRoutableV4Ranges)
        {
            var fullBytes = prefix / 8;
            var remBits = prefix % 8;
            var match = true;

            for (var i = 0; i < fullBytes; i++)
            {
                if (bytes[i] != rangeBytes[i]) { match = false; break; }
            }
            if (match && remBits > 0)
            {
                var mask = (byte)(0xFF << (8 - remBits));
                if ((bytes[fullBytes] & mask) != (rangeBytes[fullBytes] & mask))
                {
                    match = false;
                }
            }
            if (match) return false;
        }

        return true;
    }

        private void SweepProcessConnectionsNow()
    {
        var s = _settings.Settings;
        if (!s.SplitTunnelEnabled) return;

        SplitRules.ClassifySplitEntries(s, out _, out _, out var procNames, out var procPaths);
        if (procNames.Count == 0 && procPaths.Count == 0) return;
        if (UsesMihomoNativeSplitRules()) return;

        var include = string.Equals((s.SplitTunnelMode ?? "exclude").Trim(), "include", StringComparison.OrdinalIgnoreCase);
        ScanAndPinProcessConnections(procNames, procPaths, include);
    }

    private void SweepEngineBypassConnections()
    {
        long routeGeneration;
        int realIfIndex;
        IPAddress realGateway;
        lock (_routeLock)
        {
            if (!_realRouteKnown) return;
            routeGeneration = _routeGeneration;
            realIfIndex = _realIfIndex;
            realGateway = _realGateway;
        }

        void PinBypass(IPAddress ip, string source)
        {
            if (IPAddress.IsLoopback(ip) || !IsValidPublicInternetIpv4(ip)) return;

            var ipKey = ip.ToString();
            try
            {
                lock (_routeLock)
                {
                    if (routeGeneration != _routeGeneration || !_realRouteKnown) return;
                    if (_dynamicRoutes.ContainsKey(ipKey)) return;

                    var entry = WintunRouteApi.AddRoute(realIfIndex, ip, 32, realGateway);
                    _appliedRoutes.Add(entry);
                    _dynamicRoutes[ipKey] = (entry, source);
                }

                WriteDiag($"engine bypass: {source} → {ip}; host route pinned direct (real gw)");
                PersistRouteJournal();
            }
            catch (Exception ex)
            {
                WriteDiag($"engine bypass: route for {ip} ({source}) not pinned: {ex.Message}");
            }
        }

        var endpoint = _tunnel.ServerEndpointAddress?.Trim() ?? "";
        if (endpoint.Length > 0)
        {
            if (IPAddress.TryParse(endpoint, out var endpointIp))
            {
                PinBypass(endpointIp, "engine:endpoint");
            }
            else
            {
                try
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            foreach (var a in (await Dns.GetHostAddressesAsync(endpoint).ConfigureAwait(false))
                                         .Where(a => a.AddressFamily == AddressFamily.InterNetwork))
                            {
                                PinBypass(a, "engine:endpoint");
                            }
                        }
                        catch { }
                    });
                }
                catch { }
            }
        }

        var connections = WintunRouteApi.GetActiveTcpConnections();
        if (connections.Count == 0) return;

        foreach (var conn in connections)
        {
            var pid = conn.Pid;
            var procInfo = LookupProcessInfoCached(pid);

            if (string.IsNullOrEmpty(procInfo.Name)) continue;
            if (!EngineProcessNames.All.Contains(procInfo.Name, StringComparer.OrdinalIgnoreCase)) continue;

            if (string.Equals(procInfo.Name, EngineProcessNames.Shard, StringComparison.OrdinalIgnoreCase)
                || string.Equals(procInfo.Name, "shard.exe", StringComparison.OrdinalIgnoreCase))
            {
                var ep = _tunnel.ServerEndpointAddress?.Trim() ?? "";
                if (ep.Length > 0 && IPAddress.TryParse(ep, out var epIp) && !conn.RemoteIp.Equals(epIp))
                {
                    continue;
                }
            }

            PinBypass(conn.RemoteIp, $"engine:{procInfo.Name} (pid {pid})");
        }
    }

        private async Task RunProcessSplitMonitorAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var hasAppRules = false;
            try
            {
                SweepEngineBypassConnections();

                var s = _settings.Settings;
                if (s.SplitTunnelEnabled)
                {
                    SplitRules.ClassifySplitEntries(s, out _, out _, out var procNames, out var procPaths);
                    var hasProcessMonitorWork = (procNames.Count > 0 || procPaths.Count > 0)
                        && !UsesMihomoNativeSplitRules();
                    hasAppRules = hasProcessMonitorWork;
                    if (hasProcessMonitorWork)
                    {
                        var include = string.Equals((s.SplitTunnelMode ?? "exclude").Trim(), "include", StringComparison.OrdinalIgnoreCase);
                        ScanAndPinProcessConnections(procNames, procPaths, include);
                    }
                }
            }
            catch (Exception ex)
            {
                WriteDiag($"process split monitor error: {ex.Message}");
            }

            try
            {
                await Task.Delay(hasAppRules ? 50 : 250, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void ScanAndPinProcessConnections(
        List<string> procNames,
        List<string> procPaths,
        bool include)
    {
        if (!include && !_realRouteKnown) return;
        if (include && _tunIfIndex == 0) return;

        var connections = WintunRouteApi.GetActiveTcpConnections();
        if (connections.Count == 0) return;

        foreach (var conn in connections)
        {
            var pid = conn.Pid;
            var procInfo = LookupProcessInfoCached(pid);

            if (string.IsNullOrEmpty(procInfo.Name)) continue;

            var matchedName = MatchProcess(procInfo.Path, procInfo.Name, procNames, procPaths);
            if (matchedName is null) continue;

            var remoteIp = conn.RemoteIp;

            
            
            
            
            
            
            
            
            
            
            if (!include && IPAddress.IsLoopback(remoteIp))
            {
                var remotePort = (ushort)IPAddress.NetworkToHostOrder((short)conn.Raw.RemotePort);
                if (IsLocalProxyPort(remotePort) &&
                    !EngineProcessNames.All.Contains(procInfo.Name, StringComparer.OrdinalIgnoreCase))
                {
                    WintunRouteApi.TryCloseTcpConnection(conn.Raw);
                }
                continue;
            }

            if (!IsValidPublicInternetIpv4(remoteIp)) continue;
            if (include && IsProtectedEngineIp(remoteIp)) continue;

            var ipKey = remoteIp.ToString();
            var needsAdd = false;
            lock (_routeLock)
            {
                if (!_dynamicRoutes.ContainsKey(ipKey))
                {
                    needsAdd = true;
                }
            }

            if (!needsAdd) continue;

            try
            {
                var entry = include
                    ? WintunRouteApi.AddRoute(_tunIfIndex, remoteIp, 32, TunAddressV4)
                    : WintunRouteApi.AddRoute(_realIfIndex, remoteIp, 32, _realGateway);

                var added = false;
                lock (_routeLock)
                {
                    if (!_dynamicRoutes.ContainsKey(ipKey))
                    {
                        _appliedRoutes.Add(entry);
                        _dynamicRoutes[ipKey] = (entry, $"app:{matchedName}");
                        added = true;
                    }
                }

                if (!added)
                {
                    try { WintunRouteApi.DeleteRoute(entry); } catch { }
                }
                else
                {
                    
                    
                    
                    
                    
                    
                    
                    
                    var isCoreProcess = EngineProcessNames.All.Contains(
                        procInfo.Name, StringComparer.OrdinalIgnoreCase);
                    if (!include || !isCoreProcess)
                    {
                        WintunRouteApi.TryCloseTcpConnection(conn.Raw);
                    }

                    WriteDiag($"split app: '{matchedName}' (pid {pid}) → {remoteIp}; host route pinned "
                              + $"{(include ? "via tunnel" : "direct (real gw)")}");
                    PersistRouteJournal();
                }
            }
            catch (Exception ex)
            {
                WriteDiag($"split app: route for {remoteIp} ({matchedName}) not pinned: {ex.Message}");
            }
        }
    }

    private static string? MatchProcess(
        string? fullPath,
        string fileName,
        List<string> procNames,
        List<string> procPaths)
    {
        foreach (var name in procNames)
        {
            if (string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase))
                return name;
        }

        if (!string.IsNullOrEmpty(fullPath))
        {
            foreach (var path in procPaths)
            {
                if (string.Equals(path, fullPath, StringComparison.OrdinalIgnoreCase))
                    return Path.GetFileName(path);

                if (string.Equals(Path.GetFileName(path), fileName, StringComparison.OrdinalIgnoreCase))
                    return fileName;
            }
        }

        return null;
    }
}
