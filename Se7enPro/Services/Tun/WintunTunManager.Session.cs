using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Se7enPro.Services;

public sealed partial class WintunTunManager
{
    private bool RequiresLocalSocksAuth(Models.UserSettings s)
    {
        if (!LanPolicy.CanExpose(s)) return false;
        return _tunnel.ActiveMethod is ConnectionMethod.Psiphon or ConnectionMethod.Shard;
    }

    private string BuildLocalProxyUrl(int socksPort)
    {
        var s = _settings.Settings;
        if (!RequiresLocalSocksAuth(s)) return $"socks5://127.0.0.1:{socksPort}";

        var user = Uri.EscapeDataString(s.LanProxyUsername ?? "");
        var pass = Uri.EscapeDataString(s.LanProxyPassword ?? "");
        return $"socks5://{user}:{pass}@127.0.0.1:{socksPort}";
    }

    private static string YamlSingleQuote(string value) => MihomoConfigBuilder.YamlSingleQuote(value);

    private static string SanitizeRuleAtom(string value) => MihomoConfigBuilder.SanitizeRuleAtom(value);

    internal static bool SupportsSocksUdp(ConnectionMethod method) => MihomoConfigBuilder.SupportsSocksUdp(method);

    internal static bool SupportsTunnelIpv6(ConnectionMethod method, Se7enPro.Models.UserSettings settings)
        => MihomoConfigBuilder.SupportsTunnelIpv6(method, settings);

    private bool CanTunnelIpv6()
        => SupportsTunnelIpv6(_tunnel.ActiveMethod, _settings.Settings) && HasGlobalIPv6();

    internal string BuildMihomoConfigYaml(int socksPort, bool enableV6)
        => BuildMihomoConfigYaml(socksPort, enableV6, _tunnel.ActiveMethod);

    internal string BuildMihomoConfigYaml(int socksPort, bool enableV6, ConnectionMethod activeMethod)
    {
        var s = _settings.Settings;
        var realIfaceName = FindRealDefaultInterfaceName();
        List<string>? splitDomains = null;
        List<(string CleanCidr, AddressFamily Family)>? splitIpCidrs = null;
        List<string>? splitProcNames = null;
        List<string>? splitProcPaths = null;

        if (s.SplitTunnelEnabled)
        {
            SplitRules.ClassifySplitEntries(s, out splitDomains, out var rawCidrs, out splitProcNames, out splitProcPaths);
            splitIpCidrs = new List<(string, AddressFamily)>(rawCidrs.Count);
            foreach (var cidr in rawCidrs)
            {
                var clean = SanitizeRuleAtom(cidr);
                var parsed = SplitRules.ParseIpCidr(clean);
                if (parsed is not null)
                    splitIpCidrs.Add((clean, parsed.Value.Addr.AddressFamily));
            }
        }

        return MihomoConfigBuilder.BuildYaml(
            socksPort,
            enableV6,
            activeMethod,
            _activeTunMtu,
            TunInterfaceName,
            TunAddressV4.ToString(),
            TunPrefixV4,
            TunAddressV6.ToString(),
            TunPrefixV6,
            s,
            realIfaceName,
            EngineProcessNames.All,
            splitDomains,
            splitIpCidrs,
            splitProcNames,
            splitProcPaths);
    }

    private async Task<bool> StartTunAndWaitForReadyAsync(int socksPort, CancellationToken ct)
    {
        _recentOutput.Clear();
        var startedAt = DateTime.UtcNow;
        try
        {
            WintunRouteApi.VerifyLayout();

            var tunMethod = NormalizeTunMethod(_settings.Settings.TunMethod);
            var (workSubDir, srcSubDir, srcExeName, cachedExeName, logFileName) = tunMethod switch
            {
                "zeptun" => ("zeptun", "zeptun", "zeptun.exe", CachedZeptunExeName, "zeptun.log"),
                "mihomo" => ("mihomo", "mihomo", "mihomo.exe", CachedMihomoExeName, "mihomo.log"),
                _ => ("tun2socks", "tun2socks", "tun2socks.exe", CachedTunExeName, "tun2socks.log"),
            };

            _workDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Se7en", workSubDir);
            Directory.CreateDirectory(_workDir);
            CleanupLegacyTunWorkDirs();
            try { RecoverStaleRoutes(_logger); } catch { }
            await CleanupStaleAdapterStateAsync();

            var diagDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Se7en", "logs");
            Directory.CreateDirectory(diagDir);
            _logPath = Path.Combine(diagDir, logFileName);
            OpenSessionLog();
            WriteDiag($"starting TUN session with method={tunMethod} ({srcExeName})");

            var sourceDir = Path.Combine(AppContext.BaseDirectory, "Resources", srcSubDir);
            if (!Directory.Exists(sourceDir))
            {
                SetError(string.Format(Loc.Of("Bundled {0} resources not found next to the app."), srcSubDir));
                return false;
            }

            var runFromBundle = EngineTrust.RunFromBundle;
            foreach (var (src, dst) in new[] { (srcExeName, cachedExeName), ("wintun.dll", "wintun.dll") })
            {
                var from = Path.Combine(sourceDir, src);
                if (!File.Exists(from))
                {
                    SetError(string.Format(Loc.Of("Bundled {0} resource missing: {1}"), srcSubDir, src));
                    return false;
                }
                if (runFromBundle) continue;
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
                FileName = runFromBundle
                    ? Path.Combine(sourceDir, srcExeName)
                    : Path.Combine(_workDir, cachedExeName),
                WorkingDirectory = _workDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            if (string.Equals(tunMethod, "zeptun", StringComparison.Ordinal))
            {
                psi.ArgumentList.Add("run");
                psi.ArgumentList.Add("--tun");
                psi.ArgumentList.Add(TunInterfaceName);
                psi.ArgumentList.Add("--mtu");
                psi.ArgumentList.Add(_activeTunMtu.ToString());
                psi.ArgumentList.Add("--no-configure");
                psi.ArgumentList.Add("--no-network-monitor");
                psi.ArgumentList.Add("--preset");
                psi.ArgumentList.Add("desktop");
                psi.ArgumentList.Add("--socks5");
                psi.ArgumentList.Add($"127.0.0.1:{socksPort}");
                var s = _settings.Settings;
                if (RequiresLocalSocksAuth(s))
                {
                    if (!string.IsNullOrEmpty(s.LanProxyUsername))
                    {
                        psi.ArgumentList.Add("--socks5-user");
                        psi.ArgumentList.Add(s.LanProxyUsername);
                    }
                    if (!string.IsNullOrEmpty(s.LanProxyPassword))
                    {
                        psi.ArgumentList.Add("--socks5-pass");
                        psi.ArgumentList.Add(s.LanProxyPassword);
                    }
                }
                if (!SupportsSocksUdp(_tunnel.ActiveMethod))
                {
                    psi.ArgumentList.Add("--no-udp");
                }
                psi.ArgumentList.Add("--socks5-pool");
                psi.ArgumentList.Add("0");
                psi.ArgumentList.Add("--no-tcp-early-accept");
                psi.ArgumentList.Add("--tcp-idle-timeout");
                psi.ArgumentList.Add("600000");
                psi.ArgumentList.Add("--udp-timeout");
                psi.ArgumentList.Add("120000");
                psi.ArgumentList.Add("--memory-budget");
                psi.ArgumentList.Add("192m");
                psi.ArgumentList.Add("--log-level");
                psi.ArgumentList.Add("warn");
            }
            else if (string.Equals(tunMethod, "mihomo", StringComparison.Ordinal))
            {
                var enableV6 = CanTunnelIpv6();
                var configPath = Path.Combine(_workDir, "mihomo_tun.yaml");
                var yaml = BuildMihomoConfigYaml(socksPort, enableV6);
                await File.WriteAllTextAsync(configPath, yaml, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), ct);
                psi.EnvironmentVariables["GOMEMLIMIT"] = "192MiB";
                psi.EnvironmentVariables["GODEBUG"] = "madvdontneed=1";
                psi.ArgumentList.Add("-d");
                psi.ArgumentList.Add(_workDir);
                psi.ArgumentList.Add("-f");
                psi.ArgumentList.Add(configPath);
            }
            else
            {
                psi.EnvironmentVariables["GOMEMLIMIT"] = "192MiB";
                psi.EnvironmentVariables["GODEBUG"] = "madvdontneed=1";
                psi.ArgumentList.Add("-device");
                psi.ArgumentList.Add(TunInterfaceName);
                psi.ArgumentList.Add("-proxy");
                psi.ArgumentList.Add(BuildLocalProxyUrl(socksPort));
                psi.ArgumentList.Add("-mtu");
                psi.ArgumentList.Add(_activeTunMtu.ToString());
                psi.ArgumentList.Add("-udp-timeout");
                psi.ArgumentList.Add("2m");
                psi.ArgumentList.Add("-tcp-auto-tuning");
                psi.ArgumentList.Add("-loglevel");
                psi.ArgumentList.Add("error");
            }

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.OutputDataReceived += OnTunOutput;
            proc.ErrorDataReceived += OnTunOutput;
            try { proc.Start(); }
            catch (Exception ex)
            {
                SetError($"Failed to start {cachedExeName}: {ex.Message}");
                return false;
            }

            _childGuard.Adopt(proc);
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            lock (_lock) _process = proc;

            WriteDiag($"tun core spawned (pid {proc.Id}, method={tunMethod}); waiting for adapter '{TunInterfaceName}'");
            if (!await WaitForAdapterUpAsync(proc, ct))
            {
                if (ct.IsCancellationRequested) return false;
                SetError(proc.HasExited
                    ? $"{cachedExeName} exited (code={proc.ExitCode}) before the adapter came up. "
                      + $"Last output: {DescribeRecentOutput()}. Full log: {_logPath}"
                    : $"Adapter '{TunInterfaceName}' never appeared within {AdapterWaitTimeout.TotalSeconds:0}s. "
                      + $"Last output: {DescribeRecentOutput()}. Full log: {_logPath}");
                return false;
            }

            
            var nic = WintunRouteApi.FindAdapter(TunInterfaceName);
            if (nic is null)
            {
                SetError(string.Format(
                    Loc.Of("The TUN adapter '{0}' disappeared right after it came up."), TunInterfaceName));
                return false;
            }

            uint resolvedIndex = 0;
            ulong adapterLuid = 0;
            var identityDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (DateTime.UtcNow < identityDeadline)
            {
                if (WintunRouteApi.TryResolveAdapterIdentity(TunInterfaceName, out resolvedIndex, out adapterLuid))
                {
                    break;
                }
                await Task.Delay(150, ct);
            }
            if (resolvedIndex == 0 && adapterLuid == 0)
            {
                SetError(string.Format(
                    Loc.Of("The TUN adapter '{0}' came up but its interface index could not be resolved. "
                        + "Reboot or remove the stale adapter and try again."), TunInterfaceName));
                return false;
            }
            var ifIndex = (int)resolvedIndex;
            WriteDiag($"adapter identity: ifIndex={resolvedIndex} luid={adapterLuid}");

            Exception? addressError = null;
            for (var attempt = 0; attempt < 8; attempt++)
            {
                try
                {
                    WintunRouteApi.SetAdapterIpAddress(ifIndex, TunAddressV4, TunPrefixV4, adapterLuid);
                    addressError = null;
                    break;
                }
                catch (Exception ex)
                {
                    addressError = ex;
                    try { await Task.Delay(200, ct); }
                    catch (OperationCanceledException) { return false; }
                }
            }
            if (addressError is not null)
            {
                SetError(string.Format(
                    Loc.Of("Could not assign the TUN adapter address: {0}"), addressError.Message));
                return false;
            }

            _v6Enabled = CanTunnelIpv6();
            if (_v6Enabled)
            {
                try
                {
                    WintunRouteApi.SetAdapterIpAddress(ifIndex, TunAddressV6, TunPrefixV6, adapterLuid);
                }
                catch (Exception ex)
                {
                    _v6Enabled = false;
                    WriteDiag($"ipv6: address setup failed; continuing v4-only: {ex.Message}");
                }
            }

            try
            {
                await Task.WhenAll(
                    WintunDnsShell.SetAdapterMetricAsync(TunInterfaceName, TunInterfaceMetric),
                    WintunDnsShell.SetAdapterMtuAsync(TunInterfaceName, _activeTunMtu));
            }
            catch (Exception ex)
            {
                WriteDiag($"metric/mtu setup non-fatal notice: {ex.Message}");
            }

            
            
            
            
            
            
            
            
            
            
            
            
            var dnsPolicy = new DnsResolverPolicy(_logger);
            var dnsPlan = dnsPolicy.Build(_settings.Settings, hasV6Address: _v6Enabled);
            var relayTargets = DnsResolverPolicy.RelayTargets(dnsPlan);

            if (dnsPlan.Rejected.Count > 0)
            {
                WriteDiag("dns: ignored " + dnsPlan.Rejected.Count
                          + " unusable resolver entr(ies): " + string.Join("; ", dnsPlan.Rejected));
            }
            if (relayTargets.Count == 0)
            {
                WriteDiag("dns: WARNING — no usable resolver is available for TUN DNS. "
                          + "Strict mode is on and only DoT entries were configured, "
                          + "which the local forwarder cannot dial. Apps that resolve via "
                          + "the TUN adapter will get no answer. Add a UDP or DoH entry or "
                          + "turn off strict mode.");
            }
            else if (dnsPlan.HasUserEntries)
            {
                WriteDiag(dnsPlan.Strict
                    ? $"dns: custom resolvers only ({relayTargets.Count} target(s)); "
                      + "built-in defaults suppressed by strict mode"
                    : $"dns: custom resolvers first ({relayTargets.Count} target(s)), "
                      + "built-in defaults as fallback");
            }
            else
            {
                WriteDiag($"dns: no custom resolvers configured; using built-in defaults "
                          + $"({relayTargets.Count} target(s))");
            }

            var requiresAuth = RequiresLocalSocksAuth(_settings.Settings);
            var forwarder = new SocksDnsForwarder(
                socksPort,
                DnsResolverPolicy.DefaultUdpResolvers[0],
                relayTargets: relayTargets,
                queryTimeout: TimeSpan.FromSeconds(12),
                socksUsername: requiresAuth ? _settings.Settings.LanProxyUsername : null,
                socksPassword: requiresAuth ? _settings.Settings.LanProxyPassword : null,
                suppressIpv6Queries: !_v6Enabled);
            forwarder.Diag = WriteDiag;
            _underlyingDnsServers = DetectUnderlyingDnsServersV4();
            forwarder.UpdateSplitPolicy(BuildSplitPolicy());

            try
            {
                forwarder.Start();
                ct.ThrowIfCancellationRequested();

                if (!await forwarder.ProbeAsync(ct))
                {
                    if (ct.IsCancellationRequested) return false;
                    try { forwarder.Stop(); } catch { }
                    try { forwarder.Dispose(); } catch { }
                    SetError(Loc.Of("The tunnel is up but DNS is not answering inside it, so sites "
                        + "will not resolve. The Tor/V2Ray core or the resolver path is broken; "
                        + "TUN will retry."));
                    return false;
                }

                lock (_routeLock)
                {
                    
                    
                    
                    if (ct.IsCancellationRequested)
                    {
                        forwarder.Stop();
                        forwarder.Dispose();
                        return false;
                    }
                    _dnsForwarder = forwarder;
                }

                var adapterDns = forwarder.BindAddress.ToString();
                WriteDiag($"dns: forwarder listening on {adapterDns}:53; pointing the TUN adapter at it");
                await WintunDnsShell.SetAdapterDnsAsync(TunInterfaceName, adapterDns);
                ct.ThrowIfCancellationRequested();
                lock (_routeLock) { _adapterDnsSet = true; }

                WintunDnsShell.EnableNrptPolicy(adapterDns);
                WintunRouteApi.FlushDnsCache();

                WintunRouteApi.ResetActiveNonEngineWebConnections();

                await ApplyRoutesAsync(ifIndex, ct);
                ct.ThrowIfCancellationRequested();

                
                var refresherCts = new CancellationTokenSource();
                var processSplitCts = new CancellationTokenSource();
                Task? refresherTask = null;
                Task? processSplitTask = null;
                lock (_routeLock)
                {
                    if (ct.IsCancellationRequested)
                    {
                        refresherCts.Dispose();
                        processSplitCts.Dispose();
                        return false;
                    }
                    _refresherCts = refresherCts;
                    _refresherTask = refresherTask = RunSplitDnsRefresherAsync(refresherCts.Token);
                    _processSplitCts = processSplitCts;
                    _processSplitTask = processSplitTask = RunProcessSplitMonitorAsync(processSplitCts.Token);
                }
                SweepProcessConnectionsNow();

                
                
                
                SuppressSystemProxy();

                try { _dnsLeakProtector.Arm(); } catch { }

                WriteDiag($"tun ready in {(DateTime.UtcNow - startedAt).TotalMilliseconds:0} ms "
                          + $"(ifIndex={ifIndex}, v6CatchAll={_v6Enabled})");
                return true;
            }
            catch (OperationCanceledException)
            {
                
                
                lock (_routeLock)
                {
                    _dnsForwarder = null;
                    _adapterDnsSet = false;
                }
                try { forwarder.Stop(); } catch { }
                try { forwarder.Dispose(); } catch { }
                return false;
            }
            catch (Exception ex)
            {
                lock (_routeLock) { _dnsForwarder = null; }
                try { forwarder.Stop(); } catch { }
                try { forwarder.Dispose(); } catch { }
                SetError(string.Format(Loc.Of("TUN startup failed: {0}"), ex.Message));
                return false;
            }
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            SetError(string.Format(Loc.Of("TUN startup failed: {0}"), ex.Message));
            return false;
        }
    }
}
