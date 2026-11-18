using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Se7enPro.Models;
using Se7enPro.Services;
using Se7enPro.Services.Foxy;

namespace VerifyCriticalFixes;

internal static class FoxyIranBypassTests
{
    public static async Task RunAsync()
    {
        Program.Section("Foxy Iran bypass: HelloSplit, blockpage filter, Happy Eyeballs & CONNECT bridge");

        TestHelloSplitSegmentation();
        await TestHelloSplitStreamAsync();
        TestBlockPageFilterAndCooldown();
        await TestHappyEyeballsConnectRaceAsync();
        await TestConnectProxyAllowlistAndTunnelAsync();
        await TestConnectProxyUpstreamForwardingAsync();
        TestFxaCoreProxyRouting();
    }

    private static byte[] BuildTestClientHello(string sniHost)
    {
        var hostBytes = Encoding.ASCII.GetBytes(sniHost);

        var extPayload = new List<byte>();
        var entryLen = 1 + 2 + hostBytes.Length;
        WriteUInt16(extPayload, (ushort)entryLen);
        extPayload.Add(0x00);
        WriteUInt16(extPayload, (ushort)hostBytes.Length);
        extPayload.AddRange(hostBytes);

        var extensions = new List<byte>();
        WriteUInt16(extensions, 0x0000);
        WriteUInt16(extensions, (ushort)extPayload.Count);
        extensions.AddRange(extPayload);

        var body = new List<byte>();
        body.Add(0x03);
        body.Add(0x03);
        body.AddRange(new byte[32]);
        body.Add(0x00);
        WriteUInt16(body, 2);
        WriteUInt16(body, 0x1301);
        body.Add(0x01);
        body.Add(0x00);
        WriteUInt16(body, (ushort)extensions.Count);
        body.AddRange(extensions);

        var handshake = new List<byte>
        {
            0x01,
            (byte)((body.Count >> 16) & 0xFF),
            (byte)((body.Count >> 8) & 0xFF),
            (byte)(body.Count & 0xFF),
        };
        handshake.AddRange(body);

        var record = new List<byte> { 0x16, 0x03, 0x01 };
        WriteUInt16(record, (ushort)handshake.Count);
        record.AddRange(handshake);
        return record.ToArray();

        static void WriteUInt16(List<byte> target, ushort value)
        {
            target.Add((byte)(value >> 8));
            target.Add((byte)(value & 0xFF));
        }
    }

    private static void TestHelloSplitSegmentation()
    {
        const string sni = "api.accounts.firefox.com";
        var hello = BuildTestClientHello(sni);

        Program.Check(HelloSplitHelper.IsClientHello(hello), "HelloSplit detects valid TLS ClientHello");
        Program.Check(!HelloSplitHelper.IsClientHello("GET / HTTP/1.1\r\n"u8), "HelloSplit ignores plain HTTP traffic");
        Program.Check(!HelloSplitHelper.IsClientHello(new byte[] { 0x05, 0x01, 0x00, 0x01, 0x02, 0x03 }),
            "HelloSplit ignores SOCKS5 greetings");

        var sniOffset = HelloSplitHelper.FindSniServerNameOffset(hello);
        var hostBytes = Encoding.ASCII.GetBytes(sni);
        Program.Check(sniOffset > 0 && hello.AsSpan(sniOffset, hostBytes.Length).SequenceEqual(hostBytes),
            "FindSniServerNameOffset locates exact SNI server_name offset");

        var sniSegments = HelloSplitHelper.BuildSegments(hello, HelloSplitMode.Sni);
        Program.Check(sniSegments.Count == 3 &&
                      sniSegments[0].Length == sniOffset &&
                      sniSegments[1].Length == 1 &&
                      sniSegments[1][0] == (byte)'a' &&
                      sniSegments.SelectMany(s => s).SequenceEqual(hello),
            "HelloSplitMode.Sni cuts before/after first SNI byte with exact byte preservation");

        var firstByteSegments = HelloSplitHelper.BuildSegments(hello, HelloSplitMode.FirstByte);
        Program.Check(firstByteSegments.Count == 2 &&
                      firstByteSegments[0].Length == 1 &&
                      firstByteSegments.SelectMany(s => s).SequenceEqual(hello),
            "HelloSplitMode.FirstByte cuts 1st byte with exact byte preservation");

        var recordSegments = HelloSplitHelper.BuildSegments(hello, HelloSplitMode.Record);
        var rec1Len = recordSegments.Count == 2 ? BinaryPrimitives.ReadUInt16BigEndian(recordSegments[0].AsSpan(3, 2)) : -1;
        var rec2Len = recordSegments.Count == 2 ? BinaryPrimitives.ReadUInt16BigEndian(recordSegments[1].AsSpan(3, 2)) : -1;
        Program.Check(recordSegments.Count == 2 &&
                      recordSegments[0][0] == 0x16 &&
                      recordSegments[1][0] == 0x16 &&
                      rec1Len == recordSegments[0].Length - 5 &&
                      rec2Len == recordSegments[1].Length - 5 &&
                      rec1Len + rec2Len == hello.Length - 5,
            "HelloSplitMode.Record produces two valid TLS handshake records totaling exact hello payload");
    }

    private static async Task TestHelloSplitStreamAsync()
    {
        var hello = BuildTestClientHello("accounts.firefox.com");
        var rec = new RecordingWriteStream();
        await using var split = new HelloSplitStream(rec, HelloSplitMode.Sni);

        await split.WriteAsync(hello);
        await split.WriteAsync("FOLLOWUP"u8.ToArray());

        Program.Check(rec.Writes.Count == 4 &&
                      rec.Writes[1].SequenceEqual(new[] { (byte)'a' }) &&
                      rec.Writes[3].SequenceEqual("FOLLOWUP"u8.ToArray()) &&
                      rec.Writes.Take(3).SelectMany(w => w).SequenceEqual(hello),
            "HelloSplitStream segments only first ClientHello write and passes subsequent writes untouched");
    }

    private static void TestBlockPageFilterAndCooldown()
    {
        FoxyNetwork.ClearAllAddressFailuresForTests();

        var filtered = new[] { "10.10.34.34", "10.10.34.35", "10.10.34.36", "127.0.0.1", "192.168.1.1", "172.16.0.5", "169.254.1.1", "::1", "fc00::1", "2001:4188:2:600:10:10:34:35" };
        var clean = new[] { "151.101.1.91", "151.101.193.91", "8.8.8.8", "1.1.1.1", "2606:4700:4700::1111" };

        Program.Check(filtered.All(ip => FoxyNetwork.IsBlockPageOrBogon(IPAddress.Parse(ip))),
            "IsBlockPageOrBogon rejects Iran 10.10.34.x, RFC1918, loopback & IPv6 blockpage prefixes");
        Program.Check(clean.All(ip => !FoxyNetwork.IsBlockPageOrBogon(IPAddress.Parse(ip))),
            "IsBlockPageOrBogon accepts public Fastly/DoH IPv4 and IPv6 addresses");

        var badIp = IPAddress.Parse("151.101.193.91");
        var goodIp1 = IPAddress.Parse("151.101.1.91");
        var goodIp2 = IPAddress.Parse("151.101.65.91");
        FoxyNetwork.MarkAddressFailed(badIp);

        var ordered = FoxyNetwork.OrderAddressesByHealth(new[] { badIp, goodIp1, goodIp2 });
        Program.Check(ordered.Length == 3 && ordered[0].Equals(goodIp1) && ordered[1].Equals(goodIp2) && ordered[2].Equals(badIp),
            "OrderAddressesByHealth demotes timed-out Fastly edge IPs behind healthy ones");

        FoxyNetwork.ClearAddressFailure(badIp);
        var restored = FoxyNetwork.OrderAddressesByHealth(new[] { badIp, goodIp1 });
        Program.Check(restored[0].Equals(badIp), "ClearAddressFailure restores IP priority immediately");
    }

    private static async Task TestHappyEyeballsConnectRaceAsync()
    {
        FoxyNetwork.ClearAllAddressFailuresForTests();

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var acceptTask = listener.AcceptTcpClientAsync();
        var blackhole = IPAddress.Parse("192.0.2.1");
        var live = IPAddress.Loopback;

        var sw = Stopwatch.StartNew();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var winner = await FoxyNetwork.ConnectRaceAsync(
            new[] { blackhole, live }, port, cts.Token, staggerMs: 60).ConfigureAwait(false);
        sw.Stop();
        using var accepted = await acceptTask.ConfigureAwait(false);

        var connectedAddr = (winner.RemoteEndPoint as IPEndPoint)?.Address;
        Program.Check(winner.Connected && sw.ElapsedMilliseconds < 2000 && Equals(connectedAddr, live),
            $"ConnectRaceAsync staggers past stalled first IP in {sw.ElapsedMilliseconds}ms without waiting full timeout");

        var immediateBad = IPAddress.Parse("127.0.0.2");
        var acceptTask2 = listener.AcceptTcpClientAsync();
        using var winner2 = await FoxyNetwork.ConnectRaceAsync(
            new[] { immediateBad, live }, port, cts.Token, staggerMs: 5000).ConfigureAwait(false);
        using var accepted2 = await acceptTask2.ConfigureAwait(false);
        var postOrder = FoxyNetwork.OrderAddressesByHealth(new[] { immediateBad, live });
        Program.Check(winner2.Connected && postOrder[0].Equals(live),
            "ConnectRaceAsync immediately fails over on refused IP and demotes it behind healthy IPs");
        FoxyNetwork.ClearAllAddressFailuresForTests();
    }

    private static async Task TestConnectProxyAllowlistAndTunnelAsync()
    {
        Program.Check(FoxyConnectProxy.IsSignInHostAllowed("api.accounts.firefox.com") &&
                      FoxyConnectProxy.IsSignInHostAllowed("accounts.firefox.com") &&
                      FoxyConnectProxy.IsSignInHostAllowed("oauth.accounts.firefox.com") &&
                      FoxyConnectProxy.IsSignInHostAllowed("challenges.cloudflare.com") &&
                      FoxyConnectProxy.IsSignInHostAllowed("newassets.hcaptcha.com") &&
                      !FoxyConnectProxy.IsSignInHostAllowed("127.0.0.1") &&
                      !FoxyConnectProxy.IsSignInHostAllowed("example.org"),
            "FoxyConnectProxy allowlists Mozilla/Fastly/Captcha sign-in domains and blocks arbitrary hosts/loopback");

        using var restrictedProxy = new FoxyConnectProxy(restrictToSignInHosts: true);
        restrictedProxy.EnsureStarted();
        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, restrictedProxy.Port);
            using var s = client.GetStream();
            await s.WriteAsync("CONNECT evil.example:443 HTTP/1.1\r\nHost: evil.example:443\r\n\r\n"u8.ToArray());
            var buf = new byte[128];
            var n = await s.ReadAsync(buf);
            var reply = Encoding.ASCII.GetString(buf, 0, n);
            Program.Check(reply.Contains("403", StringComparison.Ordinal),
                "Restricted FoxyConnectProxy answers HTTP 403 for disallowed host");
        }

        using var target = new TcpListener(IPAddress.Loopback, 0);
        target.Start();
        var targetPort = ((IPEndPoint)target.LocalEndpoint).Port;
        var hello = BuildTestClientHello("api.accounts.firefox.com");

        var serverTask = Task.Run(async () =>
        {
            using var srv = await target.AcceptTcpClientAsync();
            using var net = srv.GetStream();
            var received = new MemoryStream();
            var tmp = new byte[2048];
            while (received.Length < hello.Length)
            {
                var r = await net.ReadAsync(tmp);
                if (r == 0) break;
                received.Write(tmp, 0, r);
            }
            await net.WriteAsync("SERVER_HELLO_OK"u8.ToArray());
            return received.ToArray();
        });

        using var openProxy = new FoxyConnectProxy(restrictToSignInHosts: false, splitMode: HelloSplitMode.Sni);
        openProxy.EnsureStarted();
        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, openProxy.Port);
            using var s = client.GetStream();
            var connectReq = $"CONNECT 127.0.0.1:{targetPort} HTTP/1.1\r\nHost: 127.0.0.1:{targetPort}\r\n\r\n";
            await s.WriteAsync(Encoding.ASCII.GetBytes(connectReq));
            var hdrBuf = new byte[128];
            var hdrN = await s.ReadAsync(hdrBuf);
            var hdrText = Encoding.ASCII.GetString(hdrBuf, 0, hdrN);

            await s.WriteAsync(hello);
            await s.FlushAsync();

            var respBuf = new byte[64];
            var respN = await s.ReadAsync(respBuf);
            var echo = Encoding.ASCII.GetString(respBuf, 0, respN);
            var serverGot = await serverTask;

            Program.Check(hdrText.Contains("200 Connection Established", StringComparison.Ordinal) &&
                          serverGot.SequenceEqual(hello) &&
                          echo == "SERVER_HELLO_OK",
                "FoxyConnectProxy establishes CONNECT tunnel and preserves full HelloSplit ClientHello bytes");
        }
    }

    private static async Task TestConnectProxyUpstreamForwardingAsync()
    {
        using var mockUpstream = new TcpListener(IPAddress.Loopback, 0);
        mockUpstream.Start();
        var upPort = ((IPEndPoint)mockUpstream.LocalEndpoint).Port;

        var upstreamTask = Task.Run(async () =>
        {
            using var c = await mockUpstream.AcceptTcpClientAsync();
            using var st = c.GetStream();
            var buf = new byte[1024];
            var n = await st.ReadAsync(buf);
            var req = Encoding.ASCII.GetString(buf, 0, n);
            await st.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray());
            var bodyBuf = new byte[64];
            var bn = await st.ReadAsync(bodyBuf);
            await st.WriteAsync("UPSTREAM_OK"u8.ToArray());
            return (req, Encoding.ASCII.GetString(bodyBuf, 0, bn));
        });

        var upProxy = new WebProxy($"http://127.0.0.1:{upPort}")
        {
            Credentials = new NetworkCredential("alice", "s3cret"),
        };
        using var bridge = new FoxyConnectProxy(() => upProxy, restrictToSignInHosts: true);
        bridge.EnsureStarted();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, bridge.Port);
        using var s = client.GetStream();
        await s.WriteAsync("CONNECT api.accounts.firefox.com:443 HTTP/1.1\r\nHost: api.accounts.firefox.com:443\r\n\r\n"u8.ToArray());
        var hbuf = new byte[128];
        _ = await s.ReadAsync(hbuf);
        await s.WriteAsync("PING"u8.ToArray());
        var rbuf = new byte[64];
        var rn = await s.ReadAsync(rbuf);
        var reply = Encoding.ASCII.GetString(rbuf, 0, rn);

        var (upReq, upPayload) = await upstreamTask;
        var expectedAuth = Convert.ToBase64String("alice:s3cret"u8.ToArray());
        Program.Check(upReq.Contains("CONNECT api.accounts.firefox.com:443 HTTP/1.1", StringComparison.Ordinal) &&
                      upReq.Contains($"Proxy-Authorization: Basic {expectedAuth}", StringComparison.Ordinal) &&
                      upPayload == "PING" &&
                      reply == "UPSTREAM_OK",
            "FoxyConnectProxy forwards CONNECT + Basic credentials cleanly through authenticated upstream HTTP proxies");
    }

    private static void TestFxaCoreProxyRouting()
    {
        var probe = new Uri("https://api.accounts.firefox.com/v1/account/login");

        using var directBridge = new FoxyConnectProxy(restrictToSignInHosts: true);
        var directHandler = new FxaCoreDelegatingHandler(
            new HttpClientHandler(), new CookieContainer(), () => null, null, directBridge);
        var resolvedDirect = directHandler.ResolveProxyUri(probe);
        Program.Check(resolvedDirect is null,
            "FxaCoreDelegatingHandler leaves X-Fxa-Proxy empty in direct mode so Go FxaCore uses native dialDirectBypass");

        var staleSettings = new UserSettings { LocalSocksProxyPort = 59999 };
        var staleResolved = FoxyNetwork.ResolveProxy(staleSettings, tunnel: null);
        Program.Check(staleResolved is null,
            "FoxyNetwork.ResolveProxy ignores non-listening LocalSocksProxyPort when no tunnel is active");

        var systemV2Ray = new WebProxy("http://127.0.0.1:10808");
        var proxyHandler = new FxaCoreDelegatingHandler(
            new HttpClientHandler(), new CookieContainer(), () => systemV2Ray, null, directBridge);
        var resolvedUpstream = proxyHandler.ResolveProxyUri(probe);
        Program.Check(resolvedUpstream == "http://127.0.0.1:10808/",
            "FxaCoreDelegatingHandler forwards active upstream/system proxy URI via X-Fxa-Proxy");

        var tunnelStub = new StubTunnelCoreManager { State = ConnectionState.Connected, SocksProxyPort = 20808 };
        var effective = FoxyNetwork.ResolveEffectiveProxy(new UserSettings(), tunnelStub, probe, FoxyConnectProxy.IsOwnProxyUri);
        var effectiveUri = effective?.GetProxy(probe);
        Program.Check(effectiveUri?.AbsoluteUri == "socks5://127.0.0.1:20808/",
            "FoxyNetwork.ResolveEffectiveProxy honors active connected Se7en Pro tunnel proxy");

        directBridge.EnsureStarted();
        Program.Check(FoxyConnectProxy.IsOwnProxyUri(new Uri(directBridge.ProxyUriString)),
            "FoxyConnectProxy.IsOwnProxyUri identifies active bridge loopback port to avoid self-loop");
    }

    private sealed class RecordingWriteStream : Stream
    {
        public List<byte[]> Writes { get; } = new();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get => 0; set { } }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => 0;
        public override long Seek(long offset, SeekOrigin origin) => 0;
        public override void SetLength(long value) { }
        public override void Write(byte[] buffer, int offset, int count) =>
            Writes.Add(buffer.AsSpan(offset, count).ToArray());
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            Writes.Add(buffer.ToArray());
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StubTunnelCoreManager : ITunnelCoreManager
    {
        public ConnectionMethod ActiveMethod => ConnectionMethod.Psiphon;
        public ConnectionState State { get; set; } = ConnectionState.Disconnected;
        public int SocksProxyPort { get; set; }
        public int HttpProxyPort { get; set; }
        public string ClientRegion => "";
        public string ConnectedServerRegion => "";
        public string CurrentRouteIp => "";
        public string CurrentRouteSni => "";
        public string ServerEndpointAddress => "";
        public IReadOnlyList<string> ServerEndpointAddresses => Array.Empty<string>();
        public event EventHandler? RouteChanged { add { } remove { } }
        public IReadOnlyList<string> AvailableEgressRegions => Array.Empty<string>();
        public IReadOnlyList<string> RecentLog => Array.Empty<string>();
        public long BytesSent => 0;
        public long BytesReceived => 0;
        public int ConnectProgressPercent => 0;
        public string ConnectProgressText => "";
        public event EventHandler<ConnectionState>? StateChanged { add { } remove { } }
        public event EventHandler<Notice>? NoticeReceived { add { } remove { } }
        public event EventHandler<string>? LogLineAppended { add { } remove { } }
        public event EventHandler<bool>? ConnectionIntentChanged { add { } remove { } }
        public event EventHandler? BytesTransferredChanged { add { } remove { } }
        public event EventHandler? LogCleared { add { } remove { } }
        public event EventHandler? ConnectProgressChanged { add { } remove { } }
        public Task StartAsync() => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public Task RestartAsync() => Task.CompletedTask;
        public void CancelInFlightConnection() { }
    }
}
