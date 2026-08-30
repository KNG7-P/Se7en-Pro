using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Se7enPro.Services;

public sealed partial class WintunTunManager
{
    private async Task<bool> StartTunAndWaitForReadyAsync(int socksPort, CancellationToken ct)
    {
        _recentOutput.Clear();
        var startedAt = DateTime.UtcNow;
        try
        {
            _workDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Se7en", "tun2socks");
            Directory.CreateDirectory(_workDir);
            CleanupLegacyTunWorkDirs();

            var diagDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Se7en", "logs");
            Directory.CreateDirectory(diagDir);
            _logPath = Path.Combine(diagDir, "tun2socks.log");
            OpenSessionLog();

            var sourceDir = Path.Combine(AppContext.BaseDirectory, "Resources", "tun2socks");
            if (!Directory.Exists(sourceDir))
            {
                SetError("Bundled tun2socks resources not found next to the app.");
                return false;
            }

            foreach (var (src, dst) in new[] { ("tun2socks.exe", CachedTunExeName), ("wintun.dll", "wintun.dll") })
            {
                var from = Path.Combine(sourceDir, src);
                if (!File.Exists(from))
                {
                    SetError($"Bundled tun2socks resource missing: {src}");
                    return false;
                }
                var to = Path.Combine(_workDir, dst);
                if (!FileCacheHelper.IsCachedCopyUpToDate(from, to))
                {
                    try { File.Copy(from, to, overwrite: true); }
                    catch (IOException) when (File.Exists(to)) { }
                }
            }

            if (!WintunRouteApi.IsAdapterUp(TunInterfaceName))
            {
                await TryRemoveStaleWintunDeviceAsync();
            }

            var psi = new ProcessStartInfo
            {
                FileName = Path.Combine(_workDir, CachedTunExeName),
                WorkingDirectory = _workDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-device");
            psi.ArgumentList.Add(TunInterfaceName);
            psi.ArgumentList.Add("-proxy");
            psi.ArgumentList.Add($"socks5://127.0.0.1:{socksPort}");
            psi.ArgumentList.Add("-mtu");
            psi.ArgumentList.Add(TunMtu.ToString());
            psi.ArgumentList.Add("-tcp-auto-tuning");
            psi.ArgumentList.Add("-loglevel");
            psi.ArgumentList.Add("info");

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.OutputDataReceived += OnTunOutput;
            proc.ErrorDataReceived += OnTunOutput;
            try { proc.Start(); }
            catch (Exception ex)
            {
                SetError($"Failed to start {CachedTunExeName}: {ex.Message}");
                return false;
            }

            _childGuard.Adopt(proc);
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            lock (_lock) _process = proc;

            WriteDiag($"tun core spawned (pid {proc.Id}); waiting for adapter '{TunInterfaceName}'");
            if (!await WaitForAdapterUpAsync(proc, ct))
            {
                if (ct.IsCancellationRequested) return false;
                SetError(proc.HasExited
                    ? $"{CachedTunExeName} exited (code={proc.ExitCode}) before the adapter came up. "
                      + $"Last output: {DescribeRecentOutput()}. Full log: {_logPath}"
                    : $"Adapter '{TunInterfaceName}' never appeared within {AdapterWaitTimeout.TotalSeconds:0}s. "
                      + $"Last output: {DescribeRecentOutput()}. Full log: {_logPath}");
                return false;
            }

            var nic = WintunRouteApi.FindAdapter(TunInterfaceName)!;
            var ifIndex = WintunRouteApi.GetAdapterIndex(nic);
            WintunRouteApi.SetAdapterIpAddress(ifIndex, TunAddressV4, TunPrefixV4);

            _v6Enabled = HasGlobalIPv6();
            if (_v6Enabled)
            {
                WintunRouteApi.SetAdapterIpAddress(ifIndex, TunAddressV6, TunPrefixV6);
            }

            _dnsForwarder = new SocksDnsForwarder(socksPort);
            _dnsForwarder.Diag = WriteDiag;
            _underlyingDnsServers = DetectUnderlyingDnsServersV4();
            _dnsForwarder.UpdateSplitPolicy(BuildSplitPolicy());
            _dnsForwarder.Start();

            await WintunDnsShell.SetAdapterDnsAsync(TunInterfaceName, "127.0.0.1");
            _adapterDnsSet = true;

            WintunRouteApi.FlushDnsCache();

            await ApplyRoutesAsync(ifIndex, ct);

            _refresherCts = new CancellationTokenSource();
            _refresherTask = RunSplitDnsRefresherAsync(_refresherCts.Token);

            _processSplitCts = new CancellationTokenSource();
            _processSplitTask = RunProcessSplitMonitorAsync(_processSplitCts.Token);
            SweepProcessConnectionsNow();

            SuppressSystemProxy();

            WintunRouteApi.FlushDnsCache();
            WriteDiag($"tun ready in {(DateTime.UtcNow - startedAt).TotalMilliseconds:0} ms "
                      + $"(ifIndex={ifIndex}, v6CatchAll={_v6Enabled})");
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            SetError($"TUN startup failed: {ex.Message}");
            return false;
        }
    }
}
