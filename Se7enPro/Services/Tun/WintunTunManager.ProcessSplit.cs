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
        private static bool IsLocalProxyPort(int port) => port is
        10808 or 10809 or   
        1819 or 1820 or 1821 or 1824 or 1825 or   
        1080 or 8118;      

    
    
    
    
    
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

        var include = string.Equals((s.SplitTunnelMode ?? "exclude").Trim(), "include", StringComparison.OrdinalIgnoreCase);
        var pidCache = new Dictionary<int, (string? Path, string? Name)>();
        ScanAndPinProcessConnections(procNames, procPaths, include, pidCache);
    }

        private async Task RunProcessSplitMonitorAsync(CancellationToken ct)
    {
        var pidCache = new Dictionary<int, (string? Path, string? Name)>();
        var sweepCount = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var s = _settings.Settings;
                if (s.SplitTunnelEnabled)
                {
                    SplitRules.ClassifySplitEntries(s, out _, out _, out var procNames, out var procPaths);
                    if (procNames.Count > 0 || procPaths.Count > 0)
                    {
                        var include = string.Equals((s.SplitTunnelMode ?? "exclude").Trim(), "include", StringComparison.OrdinalIgnoreCase);
                        ScanAndPinProcessConnections(procNames, procPaths, include, pidCache);
                    }
                }

                sweepCount++;
                if (sweepCount >= 100)
                {
                    
                    pidCache.Clear();
                    sweepCount = 0;
                }
            }
            catch (Exception ex)
            {
                WriteDiag($"process split monitor error: {ex.Message}");
            }

            try
            {
                var s = _settings.Settings;
                var hasActiveRules = s.SplitTunnelEnabled;
                await Task.Delay(hasActiveRules ? 1000 : 5000, ct);
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
        bool include,
        Dictionary<int, (string? Path, string? Name)> pidCache)
    {
        if (!include && !_realRouteKnown) return;
        if (include && _tunIfIndex == 0) return;

        var connections = WintunRouteApi.GetActiveTcpConnections();
        if (connections.Count == 0) return;

        foreach (var conn in connections)
        {
            var pid = conn.Pid;
            if (!pidCache.TryGetValue(pid, out var procInfo))
            {
                var fullPath = WintunRouteApi.TryGetProcessPath(pid);
                var fileName = string.IsNullOrEmpty(fullPath) ? null : Path.GetFileName(fullPath);
                procInfo = (fullPath, fileName);
                pidCache[pid] = procInfo;
            }

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

                if (added)
                {
                    
                    
                    
                    
                    
                    
                    
                    
                    var isCoreProcess = EngineProcessNames.All.Contains(
                        procInfo.Name, StringComparer.OrdinalIgnoreCase);
                    if (!include || !isCoreProcess)
                    {
                        WintunRouteApi.TryCloseTcpConnection(conn.Raw);
                    }

                    WriteDiag($"split app: '{matchedName}' (pid {pid}) → {remoteIp}; host route pinned "
                              + $"{(include ? "via tunnel" : "direct (real gw)")}");
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
