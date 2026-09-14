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
                FileCacheHelper.StageFileSafe(from, to, _logger, new[] { "tun2socks", "wintun" });
            }

            KillOrphanTunCores();

            for (var i = 0; i < 20 && WintunRouteApi.IsAdapterUp(TunInterfaceName); i++)
            {

                await Task.Delay(100, ct);
            }

            if (WintunRouteApi.IsAdapterUp(TunInterfaceName))
            {
                WriteDiag($"'{TunInterfaceName}' is still up with no core of ours behind it; "
                          + "removing the device before bring-up");
            }

            await TryRemoveStaleWintunDeviceAsync();

            var psi = new ProcessStartInfo
            {
                FileName = Path.Combine(_workDir, CachedTunExeName),
                WorkingDirectory = _workDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var arg in BuildCoreArguments(socksPort))
            {
                psi.ArgumentList.Add(arg);
            }

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

            await WintunDnsShell.DisableDuplicateAddressDetectionAsync(TunInterfaceName);

            WintunRouteApi.SetAdapterIpAddress(ifIndex, TunAddressV4, TunPrefixV4);

            try
            {
                WintunRouteApi.SetAdapterIpAddress(ifIndex, TunDnsAddressV4, TunDnsPrefixV4);
            }
            catch (Exception ex)
            {
                WriteDiag($"could not assign {TunDnsAddressV4}/{TunDnsPrefixV4} to the adapter: {ex.Message}");
            }

            var currentMethod = ConnectionMethodExtensions.ParseConnectionMethod(_settings.Settings.ConnectionMethod);
            var engineSupportsV6 = currentMethod is not (ConnectionMethod.Tor or ConnectionMethod.TorOverWarp or ConnectionMethod.TorOverV2Ray);
            _v6Enabled = HasGlobalIPv6() && engineSupportsV6;
            if (_v6Enabled)
            {
                WintunRouteApi.SetAdapterIpAddress(ifIndex, TunAddressV6, TunPrefixV6);

                try
                {
                    WintunRouteApi.SetAdapterIpAddress(ifIndex, TunDnsAddressV6, TunDnsPrefixV6);
                }
                catch (Exception ex)
                {
                    WriteDiag($"could not assign {TunDnsAddressV6}/{TunDnsPrefixV6} to the adapter: {ex.Message}");
                }
            }

            _dnsForwarder = new SocksDnsForwarder(socksPort);
            _dnsForwarder.Diag = WriteDiag;
            _underlyingDnsServers = DetectUnderlyingDnsServersV4();

            var realV6Probe = FindRealDefaultRouteV6();
            _realIfIndexV6 = realV6Probe?.IfIndex ?? 0;
            _realGatewayV6 = realV6Probe?.Gateway ?? System.Net.IPAddress.IPv6Any;
            _realRouteV6Known = realV6Probe is not null;

            _dnsForwarder.UpdateSplitPolicy(BuildSplitPolicy());
            await _dnsForwarder.StartAsync(TunDnsAddressV4, _v6Enabled ? TunDnsAddressV6 : null, ct);

            var dnsAddress = _dnsForwarder.BoundAddress ?? System.Net.IPAddress.Loopback;
            var dnsAddressV6 = _dnsForwarder.BoundAddressV6;
            await WintunDnsShell.SetAdapterDnsAsync(
                TunInterfaceName, dnsAddress.ToString(), dnsAddressV6?.ToString());
            await WintunDnsShell.SetInterfaceMetricAsync(TunInterfaceName, 1);
            _adapterDnsSet = true;

            WintunDnsShell.SetSmartNameResolution(disable: true);
            if (!_settings.Settings.SplitTunnelEnabled)
            {
                try { await WintunFirewallShell.InstallDnsLeakBlockAsync(ct); }
                catch (Exception ex) { WriteDiag($"dns leak block rule install failed: {ex.Message}"); }
            }

            WriteDiag($"dns forwarder listening on {dnsAddress}:53"
                      + (dnsAddressV6 is not null ? $" and [{dnsAddressV6}]:53" : "")
                      + (System.Net.IPAddress.IsLoopback(dnsAddress)
                          ? " (loopback fallback — AppContainer apps will not resolve)"
                          : "")
                      + (_v6Enabled && dnsAddressV6 is null
                          ? " (no v6 listener — v6 DNS cleared on the adapter so AAAA lookups "
                            + "cannot reach the ISP resolver around the tunnel)"
                          : ""));

            await ConfigureQuicFailFastAsync(socksPort, ct);

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

    internal static string[] BuildCoreArguments(int socksPort) => new[]
    {
        "-device", TunInterfaceName,
        "-proxy", $"socks5://127.0.0.1:{socksPort}",
        "-mtu", TunMtu.ToString(),
        "-tcp-auto-tuning",
        "-udp-timeout", UdpSessionTimeout,
        "-loglevel", "info",
    };

    private async Task ConfigureQuicFailFastAsync(int socksPort, CancellationToken ct)
    {
        bool? udpRelayed = null;
        try
        {
            udpRelayed = await SocksUdpProbe.SupportsUdpAssociateAsync(socksPort, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            WriteDiag($"udp probe failed: {ex.Message}");
        }

        if (udpRelayed is null)
        {

            WriteDiag("udp probe inconclusive; QUIC fail-fast rule left as-is");
            return;
        }

        try
        {
            if (udpRelayed.Value)
            {
                WriteDiag("socks listener relays UDP (UDP ASSOCIATE accepted); QUIC allowed through the tunnel");
                await WintunFirewallShell.RemoveQuicBlockAsync(ct);
                _quicBlockInstalled = false;
            }
            else
            {

                var v6 = _v6Enabled ? TunAddressV6 : null;
                WriteDiag("socks listener is TCP-only (UDP ASSOCIATE refused); installing non-DNS UDP fail-fast rule "
                          + $"for {TunAddressV4}{(v6 is null ? "" : $" and {v6}")} "
                          + "so HTTP/3 and Telegram clients fall back to TCP at once");
                await WintunFirewallShell.InstallQuicBlockAsync(TunAddressV4, v6, ct);
                _quicBlockInstalled = true;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            WriteDiag($"could not update QUIC fail-fast rule: {ex.Message}");
        }
    }
}
