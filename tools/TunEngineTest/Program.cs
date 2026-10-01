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
        Check(routes4.Contains("10.0.0.0"), "LAN protection route (10.0.0.0/8 → real gw) present");

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
}
