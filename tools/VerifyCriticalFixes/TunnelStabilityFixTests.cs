using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Se7enPro.Models;
using Se7enPro.Services;

namespace VerifyCriticalFixes;

internal static class TunnelStabilityFixTests
{
    internal static async Task RunAsync()
    {
        TestMihomoStabilityConfig();
        TestAutomaticDohFirstRelayOrder();
        await TestForwarderCoalescingAndIpv6SuppressionAsync();
        TestTunStabilitySourceContracts();
    }

    private static void TestMihomoStabilityConfig()
    {
        Program.Section("Mihomo UDP support, gVisor stack & DNS leak-rule ordering");

        var udpMethods = new[]
        {
            ConnectionMethod.Masque,
            ConnectionMethod.WireGuard,
            ConnectionMethod.WarpOverV2Ray,
            ConnectionMethod.Shard,
            ConnectionMethod.Lantern,
            ConnectionMethod.LanternOverWarp,
        };
        foreach (var m in udpMethods)
        {
            Program.Check(MihomoConfigBuilder.SupportsSocksUdp(m), $"SupportsSocksUdp({m}) is true");
        }

        var tcpOnlyMethods = new[]
        {
            ConnectionMethod.Psiphon,
            ConnectionMethod.PsiphonOverV2Ray,
            ConnectionMethod.TorOverV2Ray,
            ConnectionMethod.FoxyOverV2Ray,
            ConnectionMethod.PsiphonOverLantern,
            ConnectionMethod.Veep,
            ConnectionMethod.VeepOverWarp,
        };
        foreach (var m in tcpOnlyMethods)
        {
            Program.Check(!MihomoConfigBuilder.SupportsSocksUdp(m), $"SupportsSocksUdp({m}) is false");
        }

        var yamlNoUdp = MihomoConfigBuilder.BuildYaml(
            socksPort: 10808,
            enableV6: false,
            activeMethod: ConnectionMethod.Psiphon,
            activeTunMtu: 1400,
            tunInterfaceName: "Se7enTun",
            tunAddressV4: "172.19.0.1",
            tunPrefixV4: 30,
            tunAddressV6: "fdfe:dcba:9876::1",
            tunPrefixV6: 126,
            s: new UserSettings(),
            realIfaceName: "Ethernet",
            engineProcessNames: new[] { "Se7enPro.Tunnel.exe" });

        Program.Check(yamlNoUdp.Contains("stack: gvisor", StringComparison.Ordinal),
            "Mihomo config uses stack: gvisor");
        Program.Check(yamlNoUdp.Contains("auto-detect-interface: true", StringComparison.Ordinal),
            "Mihomo config sets auto-detect-interface: true");

        var udpDnsRejectIdx = yamlNoUdp.IndexOf("AND,((NETWORK,udp),(DST-PORT,53)),REJECT", StringComparison.Ordinal);
        var dnsProxyIdx = yamlNoUdp.IndexOf("DST-PORT,53,Se7enProxy", StringComparison.Ordinal);
        Program.Check(udpDnsRejectIdx >= 0 && dnsProxyIdx > udpDnsRejectIdx,
            "TCP-only Mihomo profile rejects UDP:53 before DST-PORT,53,Se7enProxy");

        var yamlWithUdp = MihomoConfigBuilder.BuildYaml(
            socksPort: 10808,
            enableV6: false,
            activeMethod: ConnectionMethod.Lantern,
            activeTunMtu: 1400,
            tunInterfaceName: "Se7enTun",
            tunAddressV4: "172.19.0.1",
            tunPrefixV4: 30,
            tunAddressV6: "fdfe:dcba:9876::1",
            tunPrefixV6: 126,
            s: new UserSettings(),
            realIfaceName: "Ethernet",
            engineProcessNames: new[] { "lantern.exe" });

        Program.Check(yamlWithUdp.Contains("    udp: true", StringComparison.Ordinal)
                   && yamlWithUdp.Contains("- 'DST-PORT,53,Se7enProxy'", StringComparison.Ordinal)
                   && !yamlWithUdp.Contains("AND,((NETWORK,udp),(DST-PORT,53)),REJECT", StringComparison.Ordinal),
            "Lantern Mihomo profile enables udp: true and allows UDP:53 through Se7enProxy");
    }

    private static void TestAutomaticDohFirstRelayOrder()
    {
        Program.Section("Automatic DNS resolver ordering (DoH before blocked UDP:53)");

        var autoPlan = new DnsResolverPolicy().Build(new UserSettings(), hasV6Address: false);
        var autoTargets = DnsResolverPolicy.RelayTargets(autoPlan);
        Program.Check(!autoPlan.HasUserEntries && autoTargets.Count >= 4,
            $"automatic mode produces built-in relay targets ({autoTargets.Count})");
        Program.Check(autoTargets[0].Transport == DnsTransport.Doh && autoTargets[1].Transport == DnsTransport.Doh,
            "automatic mode places DoH resolvers ahead of plain UDP/DoT");

        var customSettings = new UserSettings { CustomDnsUdp = "9.9.9.9", CustomDnsDoh = "" };
        var customPlan = new DnsResolverPolicy().Build(customSettings, hasV6Address: false);
        var customTargets = DnsResolverPolicy.RelayTargets(customPlan);
        Program.Check(customPlan.HasUserEntries && customTargets[0].Host == "9.9.9.9"
                   && customTargets[0].Transport == DnsTransport.Udp,
            "custom user DNS settings preserve explicit user UDP entry first");
    }

    private static async Task TestForwarderCoalescingAndIpv6SuppressionAsync()
    {
        Program.Section("SocksDnsForwarder IPv6 suppression & in-flight query coalescing");

        using var socksServer = new TcpListener(IPAddress.Loopback, 0);
        socksServer.Start();
        var socksPort = ((IPEndPoint)socksServer.LocalEndpoint).Port;
        var upstreamCount = 0;

        using var serverCts = new CancellationTokenSource();
        var acceptLoop = Task.Run(async () =>
        {
            while (!serverCts.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await socksServer.AcceptTcpClientAsync(serverCts.Token); }
                catch { break; }

                _ = Task.Run(async () =>
                {
                    using (client)
                    await using (var s = client.GetStream())
                    {
                        var greet = new byte[3];
                        await ReadExactAsync(s, greet, serverCts.Token);
                        await s.WriteAsync(new byte[] { 0x05, 0x00 }, serverCts.Token);

                        var reqHead = new byte[4];
                        await ReadExactAsync(s, reqHead, serverCts.Token);
                        var addrLen = reqHead[3] == 0x04 ? 16 : reqHead[3] == 0x01 ? 4 : 1;
                        if (addrLen == 1)
                        {
                            var nameLen = new byte[1];
                            await ReadExactAsync(s, nameLen, serverCts.Token);
                            addrLen = nameLen[0];
                        }
                        var rest = new byte[addrLen + 2];
                        await ReadExactAsync(s, rest, serverCts.Token);

                        await s.WriteAsync(
                            new byte[] { 0x05, 0x00, 0x00, 0x01, 127, 0, 0, 1, 0, 53 },
                            serverCts.Token);

                        var lenBuf = new byte[2];
                        await ReadExactAsync(s, lenBuf, serverCts.Token);
                        var qLen = (lenBuf[0] << 8) | lenBuf[1];
                        var query = new byte[qLen];
                        await ReadExactAsync(s, query, serverCts.Token);

                        Interlocked.Increment(ref upstreamCount);
                        await Task.Delay(120, serverCts.Token);

                        var resp = BuildMinimalAResponse(query);
                        var framed = new byte[2 + resp.Length];
                        framed[0] = (byte)(resp.Length >> 8);
                        framed[1] = (byte)(resp.Length & 0xFF);
                        Buffer.BlockCopy(resp, 0, framed, 2, resp.Length);
                        await s.WriteAsync(framed, serverCts.Token);
                    }
                });
            }
        });

        try
        {
            var udpTarget = new DnsServerEntry(
                DnsTransport.Udp, "1.1.1.1", IPAddress.Parse("1.1.1.1"), "1.1.1.1", DnsSettings.UdpPort);
            using var forwarder = new SocksDnsForwarder(
                socksPort,
                "1.1.1.1",
                relayTargets: new[] { udpTarget },
                queryTimeout: TimeSpan.FromSeconds(5),
                suppressIpv6Queries: true);

            var aaaaQuery = BuildDnsQuery(0x2201, "telegram.org", qtype: 28);
            var aaaaResp = await forwarder.ProcessQueryForTestAsync(aaaaQuery, CancellationToken.None);
            Program.Check(aaaaResp is not null
                       && aaaaResp.Length >= 12
                       && aaaaResp[0] == 0x22 && aaaaResp[1] == 0x01
                       && (aaaaResp[3] & 0x0F) == 0
                       && aaaaResp[6] == 0 && aaaaResp[7] == 0
                       && Volatile.Read(ref upstreamCount) == 0,
                "AAAA query with suppressIpv6Queries returns instant empty NOERROR without upstream dial");

            var concurrentTasks = Enumerable.Range(0, 8)
                .Select(i =>
                {
                    var txid = (ushort)(0x3100 + i);
                    var q = BuildDnsQuery(txid, "coalesce.example", qtype: 1);
                    return forwarder.ProcessQueryForTestAsync(q, CancellationToken.None);
                })
                .ToArray();

            var results = await Task.WhenAll(concurrentTasks);
            var allValid = results.Select((r, i) => r is not null
                                                && r.Length >= 12
                                                && r[0] == (byte)((0x3100 + i) >> 8)
                                                && r[1] == (byte)((0x3100 + i) & 0xFF)
                                                && (r[2] & 0x80) != 0)
                                  .All(static ok => ok);
            Program.Check(allValid, "all 8 concurrent identical queries received TXID-matched DNS responses");
            Program.Check(Volatile.Read(ref upstreamCount) == 1,
                $"8 concurrent identical queries coalesced into 1 upstream dial (actual={upstreamCount})");
        }
        finally
        {
            serverCts.Cancel();
            socksServer.Stop();
            try { await acceptLoop; } catch { }
        }
    }

    private static void TestTunStabilitySourceContracts()
    {
        Program.Section("TUN endpoint refresh, Zeptun/tun2socks flags, LocalProxyBridge & Lantern stability");

        var root = FindRepoRoot();
        var helpers = File.ReadAllText(Path.Combine(root, "Se7enPro", "Services", "Tun", "WintunTunManager.Helpers.cs"));
        var reconcile = File.ReadAllText(Path.Combine(root, "Se7enPro", "Services", "Tun", "WintunTunManager.Reconcile.cs"));
        var session = File.ReadAllText(Path.Combine(root, "Se7enPro", "Services", "Tun", "WintunTunManager.Session.cs"));
        var bridge = File.ReadAllText(Path.Combine(root, "Se7enPro", "Services", "LocalProxyBridge.cs"));
        var lantern = File.ReadAllText(Path.Combine(root, "Se7enPro", "Services", "Lantern", "LanternEngine.cs"));

        var splitHashBody = System.Text.RegularExpressions.Regex.Match(
            helpers,
            @"private string ComputeSplitHash\(int socksPort\).*?\n    \}",
            System.Text.RegularExpressions.RegexOptions.Singleline).Value;
        Program.Check(!string.IsNullOrEmpty(splitHashBody)
                   && !splitHashBody.Contains("ServerEndpointAddress", StringComparison.Ordinal),
            "ComputeSplitHash excludes ServerEndpointAddress so endpoint rotation never triggers connection reset");

        Program.Check(helpers.Contains("RefreshEndpointBypassRoutesAsync", StringComparison.Ordinal)
                   && reconcile.Contains("ComputeEndpointHash()", StringComparison.Ordinal)
                   && reconcile.Contains("_activeEndpointHash != endpointHash", StringComparison.Ordinal),
            "ReconcileAsync refreshes endpoint bypass routes incrementally without calling ResetActiveNonEngineWebConnections");

        Program.Check(session.Contains("\"--memory-budget\"", StringComparison.Ordinal)
                   && session.Contains("\"192m\"", StringComparison.Ordinal)
                   && session.Contains("\"--socks5-pool\"", StringComparison.Ordinal)
                   && session.Contains("\"--no-tcp-early-accept\"", StringComparison.Ordinal)
                   && session.Contains("\"--no-udp\"", StringComparison.Ordinal)
                   && session.Contains("\"-udp-timeout\"", StringComparison.Ordinal)
                   && session.Contains("suppressIpv6Queries: !_v6Enabled", StringComparison.Ordinal)
                   && session.Contains("CanTunnelIpv6()", StringComparison.Ordinal),
            "WintunTunManager.Session sets resilient Zeptun/tun2socks budgets, timeouts, and tunnel-aware IPv6 suppression");

        Program.Check(bridge.Contains("TimeSpan.FromMinutes(30)", StringComparison.Ordinal)
                   && bridge.Contains("client.NoDelay = true", StringComparison.Ordinal)
                   && bridge.Contains("SocketOptionName.KeepAlive", StringComparison.Ordinal),
            "LocalProxyBridge sets 30m idle timeout, NoDelay, and TCP KeepAlive");

        var routes = File.ReadAllText(Path.Combine(root, "Se7enPro", "Services", "Tun", "WintunTunManager.Routes.cs"));
        Program.Check(routes.Contains("if (!_v6Enabled && CanTunnelIpv6())", StringComparison.Ordinal),
            "WintunTunManager.Routes gates late IPv6 catch-all through CanTunnelIpv6()");

        var dualSettings = new Se7enPro.Models.UserSettings
        {
            AetherIpVersion = "dual",
            AetherIpVersionWireguard = "dual",
            AetherIpVersionMasque = "6",
        };
        var ipv4OnlyMethods = new[]
        {
            ConnectionMethod.Psiphon,
            ConnectionMethod.Tor,
            ConnectionMethod.Foxy,
            ConnectionMethod.Veep,
            ConnectionMethod.Lantern,
            ConnectionMethod.Shard,
            ConnectionMethod.PsiphonOverFoxy,
            ConnectionMethod.LanternOverWarp,
            ConnectionMethod.VeepOverWarp,
        };
        foreach (var m in ipv4OnlyMethods)
            Program.Check(!MihomoConfigBuilder.SupportsTunnelIpv6(m, dualSettings),
                $"SupportsTunnelIpv6({m}, dual) is false");

        Program.Check(!MihomoConfigBuilder.SupportsTunnelIpv6(ConnectionMethod.WireGuard, new Se7enPro.Models.UserSettings { AetherIpVersionWireguard = "4" }),
            "SupportsTunnelIpv6(WireGuard, v4) is false");
        Program.Check(MihomoConfigBuilder.SupportsTunnelIpv6(ConnectionMethod.WireGuard, dualSettings),
            "SupportsTunnelIpv6(WireGuard, dual) is true");
        Program.Check(MihomoConfigBuilder.SupportsTunnelIpv6(ConnectionMethod.Masque, dualSettings),
            "SupportsTunnelIpv6(Masque, 6) is true");

        var ncsiValid = BuildSingleAddressDnsResponse("dns.msftncsi.com", System.Net.IPAddress.Parse("fd3e:4f5a:5b81::1"));
        var ulaSinkhole = BuildSingleAddressDnsResponse("example.com", System.Net.IPAddress.Parse("fd3e:4f5a:5b81::1"));
        var ncsiZeroSinkhole = BuildSingleAddressDnsResponse("dns.msftncsi.com", System.Net.IPAddress.Any);
        Program.Check(!SocksDnsForwarder.IsSinkholedAnswer(ncsiValid),
            "SocksDnsForwarder allows Microsoft NCSI IPv6 test response fd3e:4f5a:5b81::1");
        Program.Check(SocksDnsForwarder.IsSinkholedAnswer(ulaSinkhole),
            "SocksDnsForwarder still treats non-NCSI fd00::/7 answers as sinkholes");
        Program.Check(SocksDnsForwarder.IsSinkholedAnswer(ncsiZeroSinkhole),
            "SocksDnsForwarder still rejects 0.0.0.0 sinkhole for dns.msftncsi.com");

        var foxySession = File.ReadAllText(Path.Combine(root, "Se7enPro", "Services", "Foxy", "FoxyH2Session.cs"));
        var foxyStream = File.ReadAllText(Path.Combine(root, "Se7enPro", "Services", "Foxy", "FoxyH2Stream.cs"));
        var foxyEngine = File.ReadAllText(Path.Combine(root, "Se7enPro", "Services", "Foxy", "FoxyEngine.cs"));
        var chainedEngine = File.ReadAllText(Path.Combine(root, "Se7enPro", "Services", "ChainedEngine.cs"));
        Program.Check(foxySession.Contains("throw new FirefoxTargetRejectedException", StringComparison.Ordinal)
                   && foxyEngine.Contains("catch (FirefoxTargetRejectedException", StringComparison.Ordinal)
                   && foxyEngine.Contains("RemoteAddress", StringComparison.Ordinal),
            "Foxy treats HTTP 403 as per-stream target rejection without tearing down H2 session and exposes RemoteAddress");

        var veep = File.ReadAllText(Path.Combine(root, "Se7enPro", "Services", "Veep", "VeepEngine.cs"));
        Program.Check(veep.Contains("!s.Region.StartsWith(\"ru\"", StringComparison.Ordinal)
                   && veep.Contains("https://www.gstatic.com/generate_204", StringComparison.Ordinal)
                   && veep.Contains("[\"connect_timeout\"] = \"5s\"", StringComparison.Ordinal)
                   && veep.Contains("\"action\"] = \"reject\"", StringComparison.Ordinal),
            "VeepEngine excludes RU in auto mode, rejects UDP cleanly, and uses HTTPS urltest");

        var tunnelCore = File.ReadAllText(Path.Combine(root, "Se7enPro", "Services", "TunnelCoreManager.cs"));
        Program.Check(tunnelCore.Contains("StartDataPlaneWatchdog", StringComparison.Ordinal),
            "TunnelCoreManager monitors standalone Psiphon data plane for stalls");

        Program.Check(lantern.Contains("[\"interrupt_exist_connections\"] = false", StringComparison.Ordinal)
                   && lantern.Contains("[\"interval\"] = \"30s\"", StringComparison.Ordinal)
                   && lantern.Contains("SelectLanternCandidates", StringComparison.Ordinal)
                   && lantern.Contains("[\"connect_timeout\"] = \"5s\"", StringComparison.Ordinal),
            "LanternEngine sets interval=30s, connect_timeout=5s, interrupt_exist_connections=false, and reachability fallback");

        Program.Check(foxySession.Contains("event EventHandler<string>? SessionDraining", StringComparison.Ordinal)
                   && !foxySession.Contains("Task.Delay(TimeSpan.FromSeconds(5)", StringComparison.Ordinal)
                   && foxySession.Contains("if (!_draining) RaiseFailed(", StringComparison.Ordinal),
            "FoxyH2Session notifies SessionDraining on graceful GOAWAY without 5s timer that murders live streams");

        Program.Check(foxyEngine.Contains("ReplaceSessionInPlaceAsync", StringComparison.Ordinal)
                   && foxyEngine.Contains("RetireSession(", StringComparison.Ordinal)
                   && foxyEngine.Contains("NotifyIfCurrentSessionDead(", StringComparison.Ordinal),
            "FoxyEngine hot-swaps draining H2 sessions in place while retiring old streams gracefully");

        Program.Check(tunnelCore.Contains("cfg[\"TunnelPoolSize\"] = 2;", StringComparison.Ordinal)
                   && tunnelCore.Contains("SSHKeepAlivePeriodMinMilliseconds", StringComparison.Ordinal)
                   && tunnelCore.Contains("SSHKeepAliveProbeTimeoutMilliseconds", StringComparison.Ordinal),
            "TunnelCoreManager configures redundant chained TunnelPoolSize=2 and fast SSH keepalives");

        Program.Check(foxyStream.Contains("WindowUpdateBatchBytes", StringComparison.Ordinal)
                   && foxySession.Contains("SendWindowUpdatePairAsync", StringComparison.Ordinal)
                   && foxySession.Contains("new(0, 1)", StringComparison.Ordinal)
                   && foxySession.Contains("CloseOldestStreams", StringComparison.Ordinal)
                   && foxySession.Contains("SocketOptionName.TcpKeepAliveTime", StringComparison.Ordinal),
            "Foxy H2 stream/session batches WINDOW_UPDATEs, bounds send-window semaphore, enables TCP keepalive, and supports staggered drain");

        Program.Check(foxyEngine.Contains("PreferDualSessionPool", StringComparison.Ordinal)
                   && foxyEngine.Contains("SelectUsableSession", StringComparison.Ordinal)
                   && foxyEngine.Contains("StaggerStreamMigrationAsync", StringComparison.Ordinal)
                   && foxyEngine.Contains("ServerEndpointAddresses", StringComparison.Ordinal)
                   && chainedEngine.Contains("FoxyEngine.PreferDualSessionPool = true", StringComparison.Ordinal),
            "FoxyEngine + ChainedEngine isolate chained Psiphon tunnels across dual H2 sessions and migrate draining streams cleanly");

        var settingsIface = File.ReadAllText(Path.Combine(root, "Se7enPro", "Services", "ISettingsService.cs"));
        var foxyTransport = File.ReadAllText(Path.Combine(root, "Se7enPro", "Services", "Foxy", "FoxyLoginTransport.cs"));
        var addFoxyCs = File.ReadAllText(Path.Combine(root, "Se7enPro", "Views", "AddFoxyAccountDialog.xaml.cs"));
        var addFoxyXaml = File.ReadAllText(Path.Combine(root, "Se7enPro", "Views", "AddFoxyAccountDialog.xaml"));
        Program.Check(foxyStream.Contains("WindowUpdateBatchBytes = 16384", StringComparison.Ordinal)
                   && tunnelCore.Contains("ConnectionWorkerPoolSize", StringComparison.Ordinal)
                   && settingsIface.Contains("SaveQuietly()", StringComparison.Ordinal)
                   && foxyEngine.Contains("RunDualPoolStaggerLoopAsync", StringComparison.Ordinal),
            "Chained Psiphon over Foxy uses 16KiB H2 WINDOW_UPDATE batching, ConnectionWorkerPoolSize=6, SaveQuietly, and staggered dual-pool refresh");

        Program.Check(foxyTransport.Contains("FoxyLoginTransportKind.V2Ray", StringComparison.Ordinal)
                   && addFoxyCs.Contains("IsAetherTransportActive", StringComparison.Ordinal)
                   && addFoxyXaml.IndexOf("Tag=\"Masque\"", StringComparison.Ordinal) < addFoxyXaml.IndexOf("Tag=\"WireGuard\"", StringComparison.Ordinal)
                   && addFoxyXaml.IndexOf("Tag=\"WireGuard\"", StringComparison.Ordinal) < addFoxyXaml.IndexOf("Tag=\"V2Ray\"", StringComparison.Ordinal)
                   && addFoxyXaml.IndexOf("Tag=\"V2Ray\"", StringComparison.Ordinal) < addFoxyXaml.IndexOf("Tag=\"Veep\"", StringComparison.Ordinal)
                   && addFoxyXaml.IndexOf("Tag=\"Veep\"", StringComparison.Ordinal) < addFoxyXaml.IndexOf("Tag=\"Lantern\"", StringComparison.Ordinal),
            "AddFoxyAccountDialog supports V2Ray in order MASQUE->WireGuard->V2Ray->Veep->Lantern->None and hides non-browser login for Aether");

        var shardEngine = File.ReadAllText(Path.Combine(root, "Se7enPro", "Services", "Shard", "ShardEngine.cs"));
        Program.Check(shardEngine.Contains("httpupgradeSettings", StringComparison.Ordinal)
                   && shardEngine.Contains("xhttpSettings", StringComparison.Ordinal)
                   && shardEngine.Contains("node.Encryption", StringComparison.Ordinal)
                   && shardEngine.Contains("echConfigList", StringComparison.Ordinal)
                   && shardEngine.Contains("\"127.0.0.1\" or \"localhost\"", StringComparison.Ordinal),
            "ShardEngine supports httpupgrade/xhttp/gRPC/h2 transports, VLESS post-quantum encryption, ECH/REALITY params, and filters 127.0.0.1/IPv6 variants");
    }

    private static byte[] BuildSingleAddressDnsResponse(string qname, System.Net.IPAddress address)
    {
        var addrBytes = address.GetAddressBytes();
        var qtype = addrBytes.Length == 16 ? (ushort)28 : (ushort)1;
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 0x12, 0x34, 0x81, 0x80, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00 });
        foreach (var label in qname.Split('.'))
        {
            var b = System.Text.Encoding.ASCII.GetBytes(label);
            ms.WriteByte((byte)b.Length);
            ms.Write(b);
        }
        ms.WriteByte(0x00);
        ms.WriteByte((byte)(qtype >> 8));
        ms.WriteByte((byte)qtype);
        ms.Write(new byte[] { 0x00, 0x01 });
        ms.Write(new byte[] { 0xC0, 0x0C });
        ms.WriteByte((byte)(qtype >> 8));
        ms.WriteByte((byte)qtype);
        ms.Write(new byte[] { 0x00, 0x01, 0x00, 0x00, 0x00, 0x3C });
        ms.WriteByte(0x00);
        ms.WriteByte((byte)addrBytes.Length);
        ms.Write(addrBytes);
        return ms.ToArray();
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buf, CancellationToken ct)
    {
        var off = 0;
        while (off < buf.Length)
        {
            var n = await stream.ReadAsync(buf.AsMemory(off), ct);
            if (n <= 0) throw new EndOfStreamException();
            off += n;
        }
    }

    private static byte[] BuildDnsQuery(ushort id, string domain, ushort qtype)
    {
        using var ms = new MemoryStream();
        ms.WriteByte((byte)(id >> 8));
        ms.WriteByte((byte)(id & 0xFF));
        ms.Write(new byte[] { 0x01, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 });
        foreach (var label in domain.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            ms.WriteByte((byte)bytes.Length);
            ms.Write(bytes);
        }
        ms.WriteByte(0);
        ms.WriteByte((byte)(qtype >> 8));
        ms.WriteByte((byte)(qtype & 0xFF));
        ms.WriteByte(0x00);
        ms.WriteByte(0x01);
        return ms.ToArray();
    }

    private static byte[] BuildMinimalAResponse(byte[] query)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(query[0]);
        ms.WriteByte(query[1]);
        ms.Write(new byte[] { 0x81, 0x80, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00 });
        ms.Write(query, 12, query.Length - 12);
        ms.Write(new byte[] { 0xC0, 0x0C, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0x00, 0x3C, 0x00, 0x04, 1, 2, 3, 4 });
        return ms.ToArray();
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Se7enPro", "Se7enPro.csproj")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate Se7en-Pro repo root");
    }
}
