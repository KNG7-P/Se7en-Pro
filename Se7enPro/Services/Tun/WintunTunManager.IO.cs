using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Se7enPro.Services;

public sealed partial class WintunTunManager
{
    private void OpenSessionLog()
    {
        try
        {
            
            
            
            
            
            
            
            
            
            
            
            
            var file = new FileInfo(_logPath!);
            var carried = file.Exists ? file.Length : 0L;
            if (carried > LogByteCap)
            {
                try { file.Delete(); } catch { }
                carried = 0;
            }

            _logWriter = new StreamWriter(
                new FileStream(_logPath!, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            { AutoFlush = true };
            Interlocked.Exchange(ref _logBytesWritten, carried);
            Interlocked.Exchange(ref _logCapNoticeWritten, 0);
            _logWriter.WriteLine($"# {_activeTunMethod} TUN session {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Couldn't open TUN log at {Path}", _logPath);
            _logWriter = null;
        }
    }

    private void OnTunOutput(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Data)) return;
        _logger.LogInformation("[{Method}] {Line}", _activeTunMethod, e.Data);
        RememberOutputLine(e.Data);
    }

    private void RememberOutputLine(string line)
    {
        _recentOutput.Enqueue(line);
        while (_recentOutput.Count > RecentOutputMax && _recentOutput.TryDequeue(out _)) { }
        WriteLogLine(line);
    }

    private string DescribeRecentOutput()
    {
        var lines = _recentOutput.ToArray();
        return lines.Length == 0 ? "(no output)" : string.Join(" | ", lines);
    }

    private void WriteDiag(string line) => WriteLogLine($"[diag {DateTime.Now:HH:mm:ss.fff}] {line}");

        private void WriteLogLine(string line)
    {
        try
        {
            lock (_logGate)
            {
                var writer = _logWriter;
                if (writer is null) return;
                if (_logBytesWritten > LogByteCap)
                {
                    if (Interlocked.Exchange(ref _logCapNoticeWritten, 1) == 0)
                    {
                        writer.WriteLine($"[diag] log reached its {LogByteCap / (1024 * 1024)} MB cap; further lines dropped");
                    }
                    return;
                }
                Interlocked.Add(ref _logBytesWritten, line.Length + 2);
                writer.WriteLine(line);
            }
        }
        catch { }
    }

        private async Task TryRemoveStaleWintunDeviceAsync()
    {
        try
        {
            var pnpUtil = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), "pnputil.exe");
            if (!File.Exists(pnpUtil)) return;

            var psi = new ProcessStartInfo
            {
                FileName = pnpUtil,
                Arguments = "/enum-devices /class Net",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return;
            var output = await p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();

            
            
            
            
            
            
            var staleAdapterId = WintunRouteApi.FindAdapter(TunInterfaceName)?.Id;
            var ids = OwnWintunDeviceIds(output, staleAdapterId);
            if (ids.Count == 0)
            {
                WriteDiag("pnputil pre-cleanup: no stale se7en_tun devices");
                return;
            }

            foreach (var id in ids)
            {
                var rpsi = new ProcessStartInfo
                {
                    FileName = pnpUtil,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                rpsi.ArgumentList.Add("/remove-device");
                rpsi.ArgumentList.Add(id);

                using var rp = Process.Start(rpsi);
                if (rp is null) continue;
                _ = rp.StandardOutput.ReadToEndAsync();
                _ = rp.StandardError.ReadToEndAsync();

                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try
                {
                    await rp.WaitForExitAsync(timeout.Token);
                    
                    
                    
                    WriteDiag($"pnputil pre-cleanup: removed '{id}' (exit={rp.ExitCode})");
                }
                catch (OperationCanceledException)
                {
                    try { rp.Kill(entireProcessTree: true); } catch { }
                    WriteDiag($"pnputil pre-cleanup: '{id}' timed out");
                }
                catch (InvalidOperationException)
                {
                    WriteDiag($"pnputil pre-cleanup: '{id}' exited before it could be read");
                }
            }
        }
        catch (Exception ex)
        {
            WriteDiag($"pnputil pre-cleanup failed (continuing): {ex.Message}");
        }
    }

        private static List<string> OwnWintunDeviceIds(string pnputilOutput, string? staleAdapterGuid = null)
    {
        var result = new List<string>();
        var currentId = (string?)null;
        var cleanGuid = staleAdapterGuid?.Trim();

        foreach (var rawLine in pnputilOutput.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                currentId = null;
                continue;
            }

            var colon = line.IndexOf(':');
            if (colon <= 0) continue;

            var label = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();

            if (label.Equals("Instance ID", StringComparison.OrdinalIgnoreCase)
                || label.Contains("Instance", StringComparison.OrdinalIgnoreCase) && value.StartsWith(@"SWD\Wintun\", StringComparison.OrdinalIgnoreCase))
            {
                currentId = value.StartsWith(@"SWD\Wintun\", StringComparison.OrdinalIgnoreCase) ? value : null;
                if (currentId is not null && !string.IsNullOrEmpty(cleanGuid)
                    && currentId.Contains(cleanGuid, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(currentId);
                    currentId = null;
                }
                continue;
            }

            if (currentId is null) continue;

            if (label.Equals("Friendly Name", StringComparison.OrdinalIgnoreCase) ||
                label.Equals("Description", StringComparison.OrdinalIgnoreCase)
                || label.Contains("name", StringComparison.OrdinalIgnoreCase)
                || label.Contains("description", StringComparison.OrdinalIgnoreCase))
            {
                if (value.Contains("se7en", StringComparison.OrdinalIgnoreCase) ||
                    value.Contains("tun2socks", StringComparison.OrdinalIgnoreCase) ||
                    value.Contains("zeptun", StringComparison.OrdinalIgnoreCase) ||
                    value.Contains("mihomo", StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(currentId);
                    currentId = null;
                }
            }
        }

        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private async Task<bool> WaitForAdapterUpAsync(Process proc, CancellationToken ct)
    {
        var startedAt = DateTime.UtcNow;
        var deadline = startedAt + AdapterWaitTimeout;
        var triedEnable = false;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            if (proc.HasExited)
            {
                int code = -1;
                try { code = proc.ExitCode; } catch { }
                WriteDiag($"wait adapter: core exited after {(DateTime.UtcNow - startedAt).TotalMilliseconds:0} ms (code={code})");
                return false;
            }
            if (WintunRouteApi.IsAdapterUp(TunInterfaceName)) return true;

            if (!triedEnable && (DateTime.UtcNow - startedAt).TotalMilliseconds >= 2000)
            {
                var nic = WintunRouteApi.FindAdapter(TunInterfaceName);
                if (nic is not null && nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
                {
                    triedEnable = true;
                    WriteDiag($"wait adapter: '{TunInterfaceName}' exists with status={nic.OperationalStatus}; requesting admin=enabled");
                    try { await WintunDnsShell.EnableAdapterAsync(TunInterfaceName); } catch { }
                }
            }
            await Task.Delay(100, ct);
        }

        if (!ct.IsCancellationRequested)
        {
            var nic = WintunRouteApi.FindAdapter(TunInterfaceName);
            WriteDiag($"wait adapter: deadline hit; adapterFound={(nic is not null)} "
                      + $"status={(nic is null ? "-" : nic.OperationalStatus.ToString())}");
            try
            {
                var names = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                    .Select(n => $"{n.Name}|{n.OperationalStatus}")
                    .Take(20);
                WriteDiag("nics: " + string.Join(" ; ", names));
            }
            catch { }
        }
        return !ct.IsCancellationRequested && WintunRouteApi.IsAdapterUp(TunInterfaceName);
    }

        internal static async Task CleanupStaleAdapterStateAsync()
    {
        try { WintunDnsShell.DisableNrptPolicy(); } catch { }
        if (WintunRouteApi.FindAdapter(TunInterfaceName) is not null)
        {
            try
            {
                await Task.WhenAll(
                    WintunDnsShell.ClearAdapterDnsAsync(TunInterfaceName),
                    WintunDnsShell.ResetAdapterMetricAsync(TunInterfaceName));
            }
            catch { }
        }
    }

    private void PersistRouteJournal()
    {
        var realIf = _realIfIndex;
        if (realIf == 0) return;

        TunRouteJournal.Entry[] snapshot;
        lock (_routeLock)
        {
            snapshot = _appliedRoutes
                .Where(e => e.IfIndex == realIf)
                .Select(e => new TunRouteJournal.Entry(
                    e.Destination.ToString(), e.Prefix, e.NextHop.ToString(), e.Metric))
                .ToArray();
        }

        TunRouteJournal.Save(realIf, snapshot);
    }

    internal static int RecoverStaleRoutes(ILogger? logger)
    {
        return TunRouteJournal.RecoverStale(
            TunRouteJournal.AdapterAlias,
            static (ifIndex, entry) =>
            {
                try
                {
                    WintunRouteApi.DeleteRoute(new WintunRouteApi.RouteEntry(
                        ifIndex,
                        System.Net.IPAddress.Parse(entry.Destination),
                        entry.Prefix,
                        System.Net.IPAddress.Parse(entry.NextHop),
                        entry.Metric));
                    return true;
                }
                catch
                {
                    return false;
                }
            },
            logger);
    }

        private void CleanupLegacyTunWorkDirs()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var legacy in new[] { "singbox-tun", "xray-tun" })
        {
            try
            {
                var dir = Path.Combine(root, BuildIdentity.AppFolderName, legacy);
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
                dir = Path.Combine(root, "Psiphon", legacy);
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "couldn't remove legacy {Dir} work dir", legacy);
            }
        }
    }
}
