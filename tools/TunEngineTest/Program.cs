using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Se7enPro.Models;
using Se7enPro.Services;

namespace TunEngineTest;

internal static partial class Program
{
    private const string TunName = "se7en_tun";
    private static int _failures;

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("=== Se7en Pro Wintun TUN engine — integration test ===");

        if (!AdminElevation.IsAdministrator())
        {
            Console.WriteLine("FAIL: must run elevated (Wintun driver + routes need admin).");
            return 1;
        }

        if (args.Contains("live", StringComparer.OrdinalIgnoreCase))
        {
            return await RunLiveVpnSplitTestAsync(args);
        }

        try
        {
            WintunRouteApi.VerifyLayout();
            Console.WriteLine("[✓] P/Invoke layouts match the Windows headers (sizes & offsets)");
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL: " + ex.Message);
            return 1;
        }

        var socks = new MockSocksServer();
        socks.Start();
        Console.WriteLine($"[i] mock SOCKS5 relay on 127.0.0.1:{socks.Port}");

        using var fakeIspDns = new UdpFakeDnsServer();
        fakeIspDns.Start();
        Console.WriteLine($"[i] fake ISP DNS on {UdpFakeDnsServer.FakeServerIp}:53 (local-path marker {UdpFakeDnsServer.LocalMarker})");

        WintunTunManager.UnderlyingDnsServersOverride =
            () => new[] { UdpFakeDnsServer.FakeServerIp };

        var settings = new FakeSettings();
        var tunnel = new FakeTunnel();
        var fakeProxy = new FakeSystemProxy();
        await using var tun = new WintunTunManager(
            NullLogger<WintunTunManager>.Instance, tunnel, settings, new NullChildGuard(), fakeProxy);
        tun.StateChanged += (_, s) => Console.WriteLine($"    state → {s}");

        settings.Settings.SystemWideTunneling = true;
        var startAt = Stopwatch.StartNew();
        tunnel.Configure(socks.Port, ConnectionState.Connected);

        if (!await WaitStateAsync(() => tun.State == TunState.Running, TimeSpan.FromSeconds(60)))
        {
            Console.WriteLine($"FAIL: TUN never reached Running (state={tun.State}, err={tun.LastError})");
            DumpLogTail();
            return 1;
        }
        startAt.Stop();
        Console.WriteLine($"[✓] TUN Running in {startAt.ElapsedMilliseconds} ms");

        Check(WintunRouteApi.IsAdapterUp(TunName), $"adapter '{TunName}' exists & is up");

        var routes4 = await RoutePrintAsync("-4");
        Check(routes4.Contains("128.0.0.0") && routes4.Contains("198.18.0.1"),
              "catch-all 128.0.0.0/1 → 198.18.0.1 present");
        Check(routes4.Contains("192.168.0.0") || routes4.Contains("172.16.0.0") || routes4.Contains("10.0.0.0"), "LAN protection route present");

        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false })
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
        string body = "<no attempt>";
        for (var attempt = 1; attempt <= 8; attempt++)
        {
            try
            {
                body = await http.GetStringAsync("http://198.18.100.7:9999/ping");
                break;
            }
            catch (Exception ex)
            {
                body = "<EXCEPTION: " + ex.GetBaseException().Message + ">";
                await Task.Delay(1500); 
            }
        }
        Check(body.Trim() == "OK",
              $"data plane works (HTTP via TUN→tun2socks→SOCKS returned '{body.Trim()}')");
        Check(socks.ConnectsServed >= 1, $"mock SOCKS received the CONNECT (served={socks.ConnectsServed})");

        settings.Settings.SplitTunnelEnabled = true;
        settings.Settings.SplitTunnelMode = "exclude";
        settings.Settings.SplitTunnelEntries.Add(new SplitTunnelEntry { Kind = "ip", Value = "8.8.8.8/32" });
        settings.Settings.SplitTunnelEntries.Add(new SplitTunnelEntry { Kind = "domain", Value = "https://www.digikala.com/" });
        settings.Settings.SplitTunnelEntries.Add(new SplitTunnelEntry { Kind = "domain", Value = "https://www.whatismyip.com/" });
        settings.Save(); 
        await Task.Delay(2000);

        routes4 = await RoutePrintAsync("-4");
        Check(routes4.Contains("8.8.8.8"), "split bypass route 8.8.8.8/32 via real gateway present");
        Check(routes4.Contains("128.0.0.0"), "catch-all still present after live re-apply");
        Check(fakeProxy.ClearCalls >= 1, "system proxy suppressed while the TUN owns traffic");
        for (var attempt = 1; attempt <= 6; attempt++)
        {
            try
            {
                body = await http.GetStringAsync("http://198.18.100.7:9999/ping");
                break;
            }
            catch (Exception ex)
            {
                body = "<EXCEPTION: " + ex.GetBaseException().Message + ">";
                await Task.Delay(1500);
            }
        }
        Check(body.Trim() == "OK", "data plane still works after live re-apply");

        try
        {
            var bypassName = $"t{Environment.TickCount64:x}.whatismyip.com";
            var bypassAddrs = await Dns.GetHostAddressesAsync(bypassName);
            Check(bypassAddrs.Any(a => a.Equals(IPAddress.Parse(UdpFakeDnsServer.LocalMarker))),
                  $"bypass domain '{bypassName}' answered by the LOCAL resolver (ISP vantage)");
        }
        catch (Exception ex)
        {
            Check(false, $"bypass-domain DNS failed: {ex.GetBaseException().Message}");
        }
        try
        {
            var dnsName = $"t{Environment.TickCount64:x}.example.com";
            var addrs = await Dns.GetHostAddressesAsync(dnsName);
            Check(addrs.Any(a => a.Equals(IPAddress.Parse("203.0.113.99"))),
                  $"non-bypass domain '{dnsName}' still answered through the tunnel");
        }
        catch (Exception ex)
        {
            Check(false, $"tunnel-path DNS failed: {ex.GetBaseException().Message}");
        }
        try
        {
            routes4 = await RoutePrintAsync("-4");
            Check(routes4.Contains(UdpFakeDnsServer.LocalMarker),
                  $"dynamic host route for the bypass answer ({UdpFakeDnsServer.LocalMarker}) present");
        }
        catch (Exception ex)
        {
            Check(false, $"route verification failed: {ex.Message}");
        }

        settings.Settings.SplitTunnelMode = "include";
        settings.Save();
        await Task.Delay(2500);

        routes4 = await RoutePrintAsync("-4");
        Check(!routes4.Contains("128.0.0.0"),
              "catch-all 128.0.0.0/1 removed when switching to 'include' mode");
        Check(routes4.Contains("8.8.8.8"),
              "static include route 8.8.8.8/32 present in 'include' mode");
        try
        {
            var incName = $"inc{Environment.TickCount64:x}.whatismyip.com";
            var incAddrs = await Dns.GetHostAddressesAsync(incName);
            Check(incAddrs.Any(a => a.Equals(IPAddress.Parse("203.0.113.99"))),
                  $"included domain '{incName}' answered through the tunnel in 'include' mode");
        }
        catch (Exception ex)
        {
            Check(false, $"include-domain DNS failed: {ex.GetBaseException().Message}");
        }
        try
        {
            var nonIncName = $"noninc{Environment.TickCount64:x}.example.com";
            var nonIncAddrs = await Dns.GetHostAddressesAsync(nonIncName);
            Check(nonIncAddrs.Any(a => a.Equals(IPAddress.Parse(UdpFakeDnsServer.LocalMarker))),
                  $"non-included domain '{nonIncName}' answered by LOCAL resolver in 'include' mode");
        }
        catch (Exception ex)
        {
            Check(false, $"non-included domain DNS failed: {ex.GetBaseException().Message}");
        }

        settings.Settings.SplitTunnelEnabled = false;
        settings.Settings.SplitTunnelMode = "exclude";
        settings.Settings.TunMethod = "zeptun";
        var zeptunServesBefore = socks.ConnectsServed;
        settings.Save();
        await Task.Delay(1500);
        if (!await WaitStateAsync(() => tun.State == TunState.Running, TimeSpan.FromSeconds(30)))
        {
            Console.WriteLine($"FAIL: Zeptun TUN never reached Running (state={tun.State}, err={tun.LastError})");
            DumpLogTail();
            return 1;
        }
        Check(WintunRouteApi.IsAdapterUp(TunName), $"Zeptun adapter '{TunName}' exists & is up");
        body = "<no attempt>";
        for (var attempt = 1; attempt <= 8; attempt++)
        {
            try
            {
                body = await http.GetStringAsync("http://198.18.100.7:9999/ping");
                break;
            }
            catch (Exception ex)
            {
                body = "<EXCEPTION: " + ex.GetBaseException().Message + ">";
                await Task.Delay(1500);
            }
        }
        Check(body.Trim() == "OK",
              $"Zeptun data plane works (HTTP via TUN→zeptun→SOCKS returned '{body.Trim()}')");
        Check(socks.ConnectsServed > zeptunServesBefore,
              $"Zeptun forwarded TCP CONNECT to SOCKS5 (served={socks.ConnectsServed})");

        settings.Settings.SplitTunnelEnabled = false;
        settings.Settings.SplitTunnelMode = "exclude";
        settings.Settings.TunMethod = "mihomo";
        var mihomoServesBefore = socks.ConnectsServed;
        settings.Save();
        await Task.Delay(1500);
        if (!await WaitStateAsync(() => tun.State == TunState.Running, TimeSpan.FromSeconds(30)))
        {
            Console.WriteLine($"FAIL: Mihomo TUN never reached Running (state={tun.State}, err={tun.LastError})");
            DumpLogTail();
            return 1;
        }
        Check(WintunRouteApi.IsAdapterUp(TunName), $"Mihomo adapter '{TunName}' exists & is up");
        body = "<no attempt>";
        for (var attempt = 1; attempt <= 8; attempt++)
        {
            try
            {
                body = await http.GetStringAsync("http://198.18.100.7:9999/ping");
                break;
            }
            catch (Exception ex)
            {
                body = "<EXCEPTION: " + ex.GetBaseException().Message + ">";
                await Task.Delay(1500);
            }
        }
        Check(body.Trim() == "OK",
              $"Mihomo data plane works (HTTP via TUN→mihomo→SOCKS returned '{body.Trim()}')");
        Check(socks.ConnectsServed > mihomoServesBefore,
              $"Mihomo forwarded TCP CONNECT to SOCKS5 (served={socks.ConnectsServed})");

        if (args.Contains("hold"))
        {
            Console.WriteLine("[i] holding TUN up for 120s — inspect now (Get-NetAdapter / Get-NetRoute / route print)");
            var probeTask = Task.Run(async () =>
            {
                for (var i = 0; i < 10; i++)
                {
                    await Task.Delay(10000);
                    try
                    {
                        var b = await http.GetStringAsync("http://198.18.100.7:9999/ping");
                        Console.WriteLine($"    [hold probe #{i + 1}] OK: {b.Trim()} (served={socks.ConnectsServed})");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"    [hold probe #{i + 1}] {ex.GetBaseException().Message} (served={socks.ConnectsServed})");
                    }
                }
            });
            await Task.Delay(TimeSpan.FromSeconds(120));
        }

        settings.Settings.SystemWideTunneling = false;
        var stopAt = Stopwatch.StartNew();
        settings.Save();
        if (!await WaitStateAsync(() => tun.State == TunState.Off, TimeSpan.FromSeconds(20)))
        {
            Console.WriteLine($"FAIL: TUN never reached Off (state={tun.State}, err={tun.LastError})");
            DumpLogTail();
            return 1;
        }
        stopAt.Stop();
        Console.WriteLine($"[✓] TUN Off in {stopAt.ElapsedMilliseconds} ms");

        await WaitAdapterDownAsync(TimeSpan.FromSeconds(6));
        Check(!WintunRouteApi.IsAdapterUp(TunName), "adapter destroyed after stop");
        routes4 = await RoutePrintAsync("-4");
        Check(!routes4.Contains("198.18.0.1"), "no TUN routes remain after stop");
        Check(fakeProxy.SetCalls >= 1 && fakeProxy.LastSetPort == socks.Port,
              "system proxy restored after the TUN went away (tunnel still connected)");

        Console.WriteLine();
        Console.WriteLine(_failures == 0 ? "=== ALL CHECKS PASSED ===" : $"=== {_failures} CHECK(S) FAILED ===");
        return _failures == 0 ? 0 : 1;
    }

    private static async Task<int> RunLiveVpnSplitTestAsync(string[] args)
    {
        Console.WriteLine("=== LIVE VPN + TUN (Wintun, Zeptun, Mihomo) + SPLIT TUNNEL (Websites & Apps) + DNS LEAK TEST ===");

        using var directHttp = new HttpClient(new SocketsHttpHandler
        {
            UseProxy = false,
            PooledConnectionLifetime = TimeSpan.Zero,
            ConnectTimeout = TimeSpan.FromSeconds(8),
        })
        { Timeout = TimeSpan.FromSeconds(12) };

        var baselineIpApi = await FetchIpApiAsync(directHttp);
        var baselineIpify = await FetchIpTextAsync(directHttp, "https://api.ipify.org");
        Console.WriteLine($"[Baseline] ip-api.com -> {baselineIpApi.Ip} ({baselineIpApi.Country} / {baselineIpApi.Isp})");
        Console.WriteLine($"[Baseline] api.ipify.org -> {baselineIpify}");
        Check(!string.IsNullOrWhiteSpace(baselineIpApi.Ip), "baseline ip-api.com returned a valid ISP IP");
        Check(baselineIpApi.Ip == baselineIpify, "baseline ip-api.com and api.ipify.org match");

        var realRoute = WintunTunManager.FindRealDefaultRouteV4()
            ?? throw new InvalidOperationException("No physical IPv4 default gateway found.");
        var pinnedMgmtRoutes = new System.Collections.Generic.List<WintunRouteApi.RouteEntry>();
        foreach (var conn in WintunRouteApi.GetActiveTcpConnections())
        {
            if (IPAddress.IsLoopback(conn.RemoteIp) || conn.RemoteIp.Equals(IPAddress.Any)) continue;
            try
            {
                var r = WintunRouteApi.AddRoute(realRoute.IfIndex, conn.RemoteIp, 32, realRoute.Gateway);
                pinnedMgmtRoutes.Add(r);
            }
            catch { }
        }

        var aetherExe = System.IO.Path.GetFullPath(
            System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Se7enPro", "Resources", "aether", "aether.exe"));
        var aetherWorkDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Se7en", "aether");
        System.IO.Directory.CreateDirectory(aetherWorkDir);

        const int aetherPort = 19081;
        var aetherPsi = new ProcessStartInfo(aetherExe, $"--bind 127.0.0.1:{aetherPort} --warp --scan turbo --log-level info")
        {
            WorkingDirectory = aetherWorkDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var aetherProc = Process.Start(aetherPsi)!;
        await aetherProc.StandardInput.WriteLineAsync("1");
        aetherProc.BeginOutputReadLine();
        aetherProc.BeginErrorReadLine();

        try
        {
            string vpnExitIp = "";
            for (var i = 0; i < 20; i++)
            {
                await Task.Delay(1000);
                try
                {
                    using var socksHandler = new SocketsHttpHandler
                    {
                        Proxy = new WebProxy($"socks5://127.0.0.1:{aetherPort}"),
                        UseProxy = true,
                        ConnectTimeout = TimeSpan.FromSeconds(4),
                    };
                    using var socksClient = new HttpClient(socksHandler) { Timeout = TimeSpan.FromSeconds(5) };
                    var probe = await FetchIpApiAsync(socksClient);
                    if (!string.IsNullOrWhiteSpace(probe.Ip) && probe.Ip != baselineIpApi.Ip)
                    {
                        vpnExitIp = probe.Ip;
                        Console.WriteLine($"[Aether Ready] VPN Exit IP via SOCKS5: {probe.Ip} ({probe.Country} / {probe.Isp})");
                        break;
                    }
                }
                catch { }
            }

            Check(!string.IsNullOrWhiteSpace(vpnExitIp) && vpnExitIp != baselineIpApi.Ip,
                  $"Aether SOCKS5 tunnel established with distinct VPN exit IP ({vpnExitIp})");
            if (string.IsNullOrWhiteSpace(vpnExitIp)) return 1;

            var settings = new FakeSettings();
            settings.Settings.ConnectionMethod = "wireguard";
            settings.Settings.SystemWideTunneling = true;

            var tunnel = new FakeTunnel();
            var fakeProxy = new FakeSystemProxy();
            await using var tun = new WintunTunManager(
                NullLogger<WintunTunManager>.Instance, tunnel, settings, new NullChildGuard(), fakeProxy);

            var methodsToTest = args.Contains("mihomo-only", StringComparer.OrdinalIgnoreCase)
                ? new[] { "mihomo" }
                : new[] { "wintun", "zeptun", "mihomo" };

            foreach (var method in methodsToTest)
            {
                async Task<bool> ApplyAndWaitAsync()
                {
                    settings.Save();
                    await Task.Delay(method == "mihomo" ? 1200 : 2500);
                    var ok = await WaitStateAsync(() => tun.State == TunState.Running, TimeSpan.FromSeconds(30));
                    WintunRouteApi.FlushDnsCache();
                    return ok;
                }

                Console.WriteLine();
                Console.WriteLine($"--- Testing Tunnel Method: {method.ToUpperInvariant()} ---");
                settings.Settings.TunMethod = method;
                settings.Settings.SplitTunnelEnabled = false;
                settings.Settings.SplitTunnelMode = "exclude";
                settings.Settings.SplitTunnelEntries.Clear();

                if (tun.State == TunState.Off)
                {
                    tunnel.Configure(aetherPort, ConnectionState.Connected);
                }
                else
                {
                    settings.Save();
                    await Task.Delay(1500);
                }

                var up = await WaitStateAsync(() => tun.State == TunState.Running, TimeSpan.FromSeconds(30));
                Check(up, $"[{method}] TUN reached Running state");
                if (!up)
                {
                    DumpLogTail();
                    return 1;
                }

                WintunRouteApi.FlushDnsCache();
                await Task.Delay(1000);
                var fullIpApi = await FetchIpApiWithRetryAsync(directHttp, expectVpn: true, baselineIpApi.Ip);
                var fullIpify = await FetchIpTextWithRetryAsync(directHttp, "https://api.ipify.org", expectVpn: true, baselineIpApi.Ip);
                var fullIcanhaz = await FetchIpTextWithRetryAsync(directHttp, "http://icanhazip.com", expectVpn: true, baselineIpApi.Ip);
                Console.WriteLine($"[{method} | Full Tunnel] ip-api.com -> {fullIpApi.Ip} ({fullIpApi.Isp})");
                Console.WriteLine($"[{method} | Full Tunnel] api.ipify.org -> {fullIpify}");
                Console.WriteLine($"[{method} | Full Tunnel] icanhazip.com -> {fullIcanhaz}");
                Check(!string.IsNullOrWhiteSpace(fullIpApi.Ip) && fullIpApi.Ip != baselineIpApi.Ip,
                      $"[{method} | Full Tunnel] ip-api.com routed through VPN ({fullIpApi.Ip})");
                Check(!string.IsNullOrWhiteSpace(fullIpify) && fullIpify != baselineIpApi.Ip,
                      $"[{method} | Full Tunnel] api.ipify.org routed through VPN ({fullIpify})");
                Check(!string.IsNullOrWhiteSpace(fullIcanhaz) && fullIcanhaz != baselineIpApi.Ip,
                      $"[{method} | Full Tunnel] icanhazip.com routed through VPN ({fullIcanhaz})");

                settings.Settings.SplitTunnelEnabled = true;
                settings.Settings.SplitTunnelMode = "exclude";
                settings.Settings.SplitTunnelEntries.Clear();
                settings.Settings.SplitTunnelEntries.Add(new SplitTunnelEntry { Kind = "domain", Value = "ip-api.com" });
                Check(await ApplyAndWaitAsync(), $"[{method} | Split Exclude] TUN Running");

                var exIpApi = await FetchIpApiWithRetryAsync(directHttp, expectVpn: false, baselineIpApi.Ip);
                var exIpify = await FetchIpTextWithRetryAsync(directHttp, "https://api.ipify.org", expectVpn: true, baselineIpApi.Ip);
                var exIcanhaz = await FetchIpTextWithRetryAsync(directHttp, "http://icanhazip.com", expectVpn: true, baselineIpApi.Ip);
                Console.WriteLine($"[{method} | Split Exclude] ip-api.com (excluded) -> {exIpApi.Ip} ({exIpApi.Isp})");
                Console.WriteLine($"[{method} | Split Exclude] api.ipify.org (tunnel) -> {exIpify}");
                Console.WriteLine($"[{method} | Split Exclude] icanhazip.com (tunnel) -> {exIcanhaz}");
                Check(exIpApi.Ip == baselineIpApi.Ip,
                      $"[{method} | Split Exclude] excluded domain ip-api.com bypassed VPN ({exIpApi.Ip} == {baselineIpApi.Ip})");
                Check(!string.IsNullOrWhiteSpace(exIpify) && exIpify != baselineIpApi.Ip,
                      $"[{method} | Split Exclude] non-excluded api.ipify.org routed through VPN ({exIpify})");
                Check(!string.IsNullOrWhiteSpace(exIcanhaz) && exIcanhaz != baselineIpApi.Ip,
                      $"[{method} | Split Exclude] non-excluded icanhazip.com routed through VPN ({exIcanhaz})");

                settings.Settings.SplitTunnelEnabled = true;
                settings.Settings.SplitTunnelMode = "include";
                settings.Settings.SplitTunnelEntries.Clear();
                settings.Settings.SplitTunnelEntries.Add(new SplitTunnelEntry { Kind = "domain", Value = "ip-api.com" });
                Check(await ApplyAndWaitAsync(), $"[{method} | Split Include] TUN Running");

                var incIpApi = await FetchIpApiWithRetryAsync(directHttp, expectVpn: true, baselineIpApi.Ip);
                var incIpify = await FetchIpTextWithRetryAsync(directHttp, "https://api.ipify.org", expectVpn: false, baselineIpApi.Ip);
                var incIcanhaz = await FetchIpTextWithRetryAsync(directHttp, "http://icanhazip.com", expectVpn: false, baselineIpApi.Ip);
                Console.WriteLine($"[{method} | Split Include] ip-api.com (included) -> {incIpApi.Ip} ({incIpApi.Isp})");
                Console.WriteLine($"[{method} | Split Include] api.ipify.org (bypassed) -> {incIpify}");
                Console.WriteLine($"[{method} | Split Include] icanhazip.com (bypassed) -> {incIcanhaz}");
                Check(!string.IsNullOrWhiteSpace(incIpApi.Ip) && incIpApi.Ip != baselineIpApi.Ip,
                      $"[{method} | Split Include] included domain ip-api.com routed through VPN ({incIpApi.Ip})");
                Check(incIpify == baselineIpApi.Ip,
                      $"[{method} | Split Include] non-included api.ipify.org bypassed VPN ({incIpify} == {baselineIpApi.Ip})");
                Check(incIcanhaz == baselineIpApi.Ip,
                      $"[{method} | Split Include] non-included icanhazip.com bypassed VPN ({incIcanhaz} == {baselineIpApi.Ip})");

                settings.Settings.SplitTunnelEnabled = true;
                settings.Settings.SplitTunnelMode = "exclude";
                settings.Settings.SplitTunnelEntries.Clear();
                settings.Settings.SplitTunnelEntries.Add(new SplitTunnelEntry { Kind = "app", Value = "curl.exe" });
                Check(await ApplyAndWaitAsync(), $"[{method} | App Exclude] TUN Running");

                var appExCurlIp = await FetchIpViaCurlWithRetryAsync(expectVpn: false, baselineIpApi.Ip);
                var appExSelfIp = await FetchIpApiWithRetryAsync(directHttp, expectVpn: true, baselineIpApi.Ip);
                Console.WriteLine($"[{method} | App Exclude] curl.exe (excluded) -> {appExCurlIp}");
                Console.WriteLine($"[{method} | App Exclude] TunEngineTest (tunnel) -> {appExSelfIp.Ip}");
                Check(appExCurlIp == baselineIpApi.Ip,
                      $"[{method} | App Exclude] excluded app curl.exe bypassed VPN ({appExCurlIp} == {baselineIpApi.Ip})");
                Check(!string.IsNullOrWhiteSpace(appExSelfIp.Ip) && appExSelfIp.Ip != baselineIpApi.Ip,
                      $"[{method} | App Exclude] non-excluded TunEngineTest routed through VPN ({appExSelfIp.Ip})");

                settings.Settings.SplitTunnelEnabled = true;
                settings.Settings.SplitTunnelMode = "include";
                settings.Settings.SplitTunnelEntries.Clear();
                settings.Settings.SplitTunnelEntries.Add(new SplitTunnelEntry { Kind = "app", Value = "curl.exe" });
                Check(await ApplyAndWaitAsync(), $"[{method} | App Include] TUN Running");

                var appIncCurlIp = await FetchIpViaCurlWithRetryAsync(expectVpn: true, baselineIpApi.Ip, warmFirst: method != "mihomo");
                var appIncSelfIpify = await FetchIpTextWithRetryAsync(directHttp, "https://api.ipify.org", expectVpn: false, baselineIpApi.Ip);
                Console.WriteLine($"[{method} | App Include] curl.exe (included) -> {appIncCurlIp}");
                Console.WriteLine($"[{method} | App Include] TunEngineTest api.ipify.org (bypassed) -> {appIncSelfIpify}");
                Check(!string.IsNullOrWhiteSpace(appIncCurlIp) && appIncCurlIp != baselineIpApi.Ip,
                      $"[{method} | App Include] included app curl.exe routed through VPN ({appIncCurlIp})");
                Check(appIncSelfIpify == baselineIpApi.Ip,
                      $"[{method} | App Include] non-included TunEngineTest bypassed VPN ({appIncSelfIpify} == {baselineIpApi.Ip})");
                if (method == "mihomo")
                {
                    var appIncSelfSameIpApi = await FetchIpApiWithRetryAsync(directHttp, expectVpn: false, baselineIpApi.Ip);
                    Console.WriteLine($"[{method} | App Include] TunEngineTest SAME site ip-api.com (bypassed) -> {appIncSelfSameIpApi.Ip}");
                    Check(appIncSelfSameIpApi.Ip == baselineIpApi.Ip,
                          $"[{method} | App Include] non-included app hitting same IP as included app still bypassed via mihomo ({appIncSelfSameIpApi.Ip} == {baselineIpApi.Ip})");
                }

                settings.Settings.SplitTunnelEnabled = false;
                settings.Settings.SplitTunnelMode = "exclude";
                settings.Settings.SplitTunnelEntries.Clear();
                Check(await ApplyAndWaitAsync(), $"[{method} | DNS Leak] TUN Running");
                await VerifyNoDnsLeakAsync(method);
            }

            settings.Settings.SystemWideTunneling = false;
            settings.Save();
            await WaitStateAsync(() => tun.State == TunState.Off, TimeSpan.FromSeconds(15));
        }
        finally
        {
            try { if (!aetherProc.HasExited) aetherProc.Kill(entireProcessTree: true); } catch { }
            foreach (var r in pinnedMgmtRoutes)
            {
                try { WintunRouteApi.DeleteRoute(r); } catch { }
            }
        }

        Console.WriteLine();
        Console.WriteLine(_failures == 0 ? "=== ALL LIVE VPN & SPLIT TUNNEL CHECKS PASSED ===" : $"=== {_failures} LIVE CHECK(S) FAILED ===");
        return _failures == 0 ? 0 : 1;
    }

    private static async Task<(string Ip, string Country, string Isp)> FetchIpApiWithRetryAsync(
        HttpClient http, bool expectVpn, string baselineIp)
    {
        (string Ip, string Country, string Isp) last = ("", "", "");
        for (var i = 0; i < 5; i++)
        {
            last = await FetchIpApiAsync(http);
            if (!string.IsNullOrWhiteSpace(last.Ip))
            {
                var isVpn = last.Ip != baselineIp;
                if (isVpn == expectVpn) return last;
            }
            WintunRouteApi.FlushDnsCache();
            await Task.Delay(1000);
        }
        return last;
    }

    private static async Task<string> FetchIpTextWithRetryAsync(
        HttpClient http, string url, bool expectVpn, string baselineIp)
    {
        var last = "";
        for (var i = 0; i < 5; i++)
        {
            last = await FetchIpTextAsync(http, url);
            if (!string.IsNullOrWhiteSpace(last))
            {
                var isVpn = last != baselineIp;
                if (isVpn == expectVpn) return last;
            }
            WintunRouteApi.FlushDnsCache();
            await Task.Delay(1000);
        }
        return last;
    }

    private static async Task<(string Ip, string Country, string Isp)> FetchIpApiAsync(HttpClient http)
    {
        try
        {
            var json = await http.GetStringAsync("http://ip-api.com/json");
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            var ip = root.TryGetProperty("query", out var q) ? q.GetString() ?? "" : "";
            var cc = root.TryGetProperty("countryCode", out var c) ? c.GetString() ?? "" : "";
            var isp = root.TryGetProperty("isp", out var s) ? s.GetString() ?? "" : "";
            return (ip.Trim(), cc.Trim(), isp.Trim());
        }
        catch (Exception ex)
        {
            return ("", "", ex.GetBaseException().Message);
        }
    }

    private static async Task<string> FetchIpTextAsync(HttpClient http, string url)
    {
        try
        {
            return (await http.GetStringAsync(url)).Trim();
        }
        catch
        {
            return "";
        }
    }

    private static async Task<string> FetchIpViaCurlWithRetryAsync(bool expectVpn, string baselineIp, bool warmFirst = false)
    {
        var curlExe = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "curl.exe");
        if (!System.IO.File.Exists(curlExe)) return "";

        var last = "";
        for (var i = 0; i < 6; i++)
        {
            try
            {

                var args = warmFirst && i == 0
                    ? "--noproxy * -s --max-time 6 http://ip-api.com/json http://ip-api.com/json"
                    : "--noproxy * -s --max-time 6 http://ip-api.com/json";
                var psi = new ProcessStartInfo(curlExe, args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using var p = Process.Start(psi)!;
                var stdout = await p.StandardOutput.ReadToEndAsync();
                _ = await p.StandardError.ReadToEndAsync();
                await p.WaitForExitAsync();

                var lastBrace = stdout.LastIndexOf('{');
                if (lastBrace >= 0)
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(stdout[lastBrace..]);
                    if (doc.RootElement.TryGetProperty("query", out var q))
                    {
                        last = (q.GetString() ?? "").Trim();
                    }
                }
            }
            catch { }

            if (!string.IsNullOrWhiteSpace(last))
            {
                var isVpn = last != baselineIp;
                if (isVpn == expectVpn) return last;
            }
            await Task.Delay(i == 0 && warmFirst ? 300 : 1000);
        }
        return last;
    }

    private static async Task VerifyNoDnsLeakAsync(string method)
    {

        using var nrptKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Services\Dnscache\Parameters\DnsPolicyConfig\{6F1F6C2A-5B9E-4C1B-9A7E-2D5E3B8C4A10}");
        var nrptServers = nrptKey?.GetValue("GenericDNSServers") as string ?? "";
        Check(nrptServers == "127.0.0.9", $"[{method} | DNS Leak] NRPT GenericDNSServers == '127.0.0.9' (actual: '{nrptServers}')");
        using var smhnrKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
            @"Software\Policies\Microsoft\Windows NT\DNSClient");
        var smhnrDisabled = (smhnrKey?.GetValue("DisableSmartNameResolution") as int?) == 1;
        Check(smhnrDisabled, $"[{method} | DNS Leak] SMHNR DisableSmartNameResolution == 1");

        var nic = WintunRouteApi.FindAdapter(TunName);
        var dnsAddrs = nic?.GetIPProperties().DnsAddresses.Select(a => a.ToString()).ToList() ?? new System.Collections.Generic.List<string>();
        Check(dnsAddrs.Contains("127.0.0.9"), $"[{method} | DNS Leak] se7en_tun DNS includes 127.0.0.9 (actual: [{string.Join(", ", dnsAddrs)}])");

        var nslookupExe = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nslookup.exe");
        var directLeakBlocked = false;
        if (System.IO.File.Exists(nslookupExe))
        {
            var psi = new ProcessStartInfo(nslookupExe, "-timeout=2 -retry=1 example.com 1.1.1.1")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            var outText = await p.StandardOutput.ReadToEndAsync();
            var errText = await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            var combined = outText + "\n" + errText;
            directLeakBlocked = !outText.Contains("Name:", StringComparison.OrdinalIgnoreCase)
                && (combined.Contains("timed out", StringComparison.OrdinalIgnoreCase)
                    || combined.Contains("timed-out", StringComparison.OrdinalIgnoreCase)
                    || combined.Contains("No response from server", StringComparison.OrdinalIgnoreCase)
                    || combined.Contains("can't find", StringComparison.OrdinalIgnoreCase));
            Console.WriteLine($"[{method} | DNS Leak] Direct 1.1.1.1:53 probe blocked={directLeakBlocked}");
        }
        Check(directLeakBlocked, $"[{method} | DNS Leak] direct outbound port 53 to 1.1.1.1 blocked by WFP");

        var resolved = await Dns.GetHostAddressesAsync("ip-api.com");
        Check(resolved.Length > 0, $"[{method} | DNS Leak] system DNS resolved ip-api.com via tunnel forwarder ({string.Join(", ", resolved.Take(2))})");

        if (method == "mihomo")
        {
            var cfgPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Se7en", "mihomo", "mihomo_tun.yaml");
            var yaml = System.IO.File.Exists(cfgPath) ? await System.IO.File.ReadAllTextAsync(cfgPath) : "";
            Check(yaml.Contains("dns:\r\n  enable: false") || yaml.Contains("dns:\n  enable: false"),
                  "[mihomo | DNS Leak] mihomo_tun.yaml has dns.enable: false");
            Check(yaml.Contains("DST-PORT,53,Se7enProxy"),
                  "[mihomo | DNS Leak] mihomo_tun.yaml enforces DST-PORT,53,Se7enProxy");
        }
    }
}
