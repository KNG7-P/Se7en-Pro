using System;
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
            _logWriter = new StreamWriter(
                new FileStream(_logPath!, FileMode.Create, FileAccess.Write, FileShare.Read))
            { AutoFlush = true };
            Interlocked.Exchange(ref _logBytesWritten, 0);
            Interlocked.Exchange(ref _logCapNoticeWritten, 0);
            _logWriter.WriteLine($"# tun2socks TUN session {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Couldn't open tun2socks log at {Path}", _logPath);
            _logWriter = null;
        }
    }

    private void OnTunOutput(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Data)) return;
        _logger.LogInformation("[tun2socks] {Line}", e.Data);
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
            using var p = Process.Start(psi)!;
            var output = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();

            var matches = System.Text.RegularExpressions.Regex.Matches(
                output, @"SWD\\Wintun\\\{[0-9a-fA-F\-]+\}");
            var ids = matches.Select(m => m.Value).Distinct().ToList();
            if (ids.Count == 0)
            {
                WriteDiag("pnputil pre-cleanup: no stale Wintun devices");
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
                using var rp = Process.Start(rpsi)!;
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try { await rp.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException)
                {
                    try { rp.Kill(entireProcessTree: true); } catch { }
                }
                WriteDiag($"pnputil pre-cleanup: removed '{id}' (exit={rp.ExitCode})");
            }
        }
        catch (Exception ex)
        {
            WriteDiag($"pnputil pre-cleanup failed (continuing): {ex.Message}");
        }
    }

    private async Task<bool> WaitForAdapterUpAsync(Process proc, CancellationToken ct)
    {
        var startedAt = DateTime.UtcNow;
        var deadline = startedAt + AdapterWaitTimeout;
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

    private void CleanupLegacyTunWorkDirs()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var legacy in new[] { "singbox-tun", "xray-tun" })
        {
            try
            {
                var dir = Path.Combine(root, "Se7en", legacy);
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
