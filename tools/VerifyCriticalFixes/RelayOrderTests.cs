using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Se7enPro.Models;
using Se7enPro.Services;

namespace VerifyCriticalFixes;

/// <summary>
/// Verifies resolver relay ordering over loopback sockets.
/// </summary>
internal static class RelayOrderTests
{
    internal static async Task<int> RunAsync()
    {
        Program.Section("DNS — live relay ordering over loopback");

        var failures = 0;

        // Two distinguishable upstreams: "alpha" answers, "beta" is a black hole.
        using var alpha = new FakeDnsServer("A") { Answer = true };
        using var beta = new FakeDnsServer("B") { Answer = false };
        alpha.Start();
        beta.Start();

        using var relay = new FakeSocksRelay();
        relay.Start();

        // The user's list: alpha first, the dead beta second. If ordering is honoured,
        // alpha answers and beta is never dialled.
        var targets = new List<DnsServerEntry>
        {
            new(DnsTransport.Udp, alpha.EndPointText,
                IPAddress.Loopback, "127.0.0.1", alpha.Port),
            new(DnsTransport.Udp, beta.EndPointText,
                IPAddress.Loopback, "127.0.0.1", beta.Port),
        };

        // Start() is deliberately not called: it binds 127.0.0.1:53, which needs
        // privileges and collides with anything already listening. SendOneAsync drives
        // the same relay path without the listener.
        var forwarder = new SocksDnsForwarder(relay.Port, "203.0.113.99", relayTargets: targets);
        try
        {
            var query = DnsProber.BuildProbeQuery();
            var answer = await RelayOnceAsync(forwarder, query, TimeSpan.FromSeconds(5));

            Program.Check(answer is not null,
                          "the query is answered through the relay",
                          $"forwarder holds {forwarder.RelayTargetsSnapshot.Count} target(s)"
                          + $" [{(forwarder.RelayTargetsSnapshot.Count > 0 ? string.Join(",", forwarder.RelayTargetsSnapshot.Select(t => t.TargetHostPort)) : "none")}]"
                          + $"; dialled: {relay.DialledSummary}"
                          + $"; alpha={alpha.Queries} beta={beta.Queries}");
            Program.Check(alpha.Queries > 0,
                          "the FIRST configured resolver was asked",
                          $"alpha queries = {alpha.Queries}");
            Program.Check(beta.Queries == 0,
                          "the dead resolver behind it was never dialled",
                          $"beta queries = {beta.Queries}");

            // Now the reverse: only the dead one is configured. The relay must be
            // dialled, get nothing, and the forwarder must report no answer rather than
            // inventing one.
            alpha.Reset();
            beta.Reset();
            using var deadOnly = new SocksDnsForwarder(relay.Port, "203.0.113.99",
                relayTargets: new List<DnsServerEntry>
                {
                    new(DnsTransport.Udp, beta.EndPointText,
                        IPAddress.Loopback, "127.0.0.1", beta.Port),
                });
            var none = await RelayOnceAsync(deadOnly, query, TimeSpan.FromSeconds(5));

            Program.Check(beta.Queries > 0,
                          "the only configured resolver IS dialled even when it is dead",
                          $"beta queries = {beta.Queries}");
            Program.Check(none is null,
                          "a dead resolver yields no answer rather than a fabricated one");

            // Falling through: dead first, live second. The live one must still answer,
            // which is what makes the configured list a preference rather than a
            // single point of failure.
            alpha.Reset();
            beta.Reset();
            using var fallthrough = new SocksDnsForwarder(relay.Port, "203.0.113.99",
                relayTargets: new List<DnsServerEntry>
                {
                    new(DnsTransport.Udp, beta.EndPointText,
                        IPAddress.Loopback, "127.0.0.1", beta.Port),
                    new(DnsTransport.Udp, alpha.EndPointText,
                        IPAddress.Loopback, "127.0.0.1", alpha.Port),
                });
            var recovered = await RelayOnceAsync(fallthrough, query, TimeSpan.FromSeconds(8));

            Program.Check(recovered is not null,
                          "a live resolver behind a dead one still answers");
            Program.Check(alpha.Queries > 0,
                          "the second entry was reached after the first failed",
                          $"alpha queries = {alpha.Queries}");

            // An empty relay list must be refused rather than accepted: a session with no
            // resolver resolves nothing at all.
            var before = targets.Count;
            forwarder.UpdateRelayTargets(Array.Empty<DnsServerEntry>());
            Program.Check(before > 0 && forwarder.RelayTargetsSnapshot.Count == before,
                          "an empty relay-target update is rejected, leaving the list intact",
                          $"count = {forwarder.RelayTargetsSnapshot.Count}");

            // A non-empty update must be taken.
            var replacement = new List<DnsServerEntry>
            {
                new(DnsTransport.Udp, alpha.EndPointText,
                    IPAddress.Loopback, "127.0.0.1", alpha.Port),
            };
            forwarder.UpdateRelayTargets(replacement);
            Program.Check(forwarder.RelayTargetsSnapshot.Count == 1
                          && forwarder.RelayTargetsSnapshot[0].Port == alpha.Port,
                          "a non-empty update replaces the list",
                          $"count = {forwarder.RelayTargetsSnapshot.Count}");

            if (Program.FailedCount > 0 && failures == 0) failures = 1;
        }
        catch (Exception ex)
        {
            Program.Check(false, $"live relay test threw: {ex.GetType().Name}: {ex.Message}");
            failures++;
        }
        finally
        {
            forwarder.Stop();
            relay.Stop();
        }

        return failures;
    }

    /// <summary>Drives one query through the forwarder's public path.</summary>
    private static async Task<byte[]?> RelayOnceAsync(
        SocksDnsForwarder forwarder, byte[] query, TimeSpan timeout)
    {
        // The forwarder listens on 127.0.0.1:53, which needs privileges this test does
        // not assume. Reach the relay logic through the internal entry point instead.
        return await forwarder.SendOneAsync(query, timeout);
    }

    /// <summary>
    /// A DNS server on the loopback interface, speaking the length-prefixed framing the
    /// forwarder uses when it relays a query. TCP, not UDP: that is what the forwarder
    /// actually speaks to its upstream once the SOCKS hop is spliced out.
    /// </summary>
    private sealed class FakeDnsServer : IDisposable
    {
        private TcpListener? _listener;
        private CancellationTokenSource? _cts;

        public FakeDnsServer(string tag) { }

        /// <summary>False makes it a black hole: it accepts, reads, and never answers.</summary>
        public bool Answer { get; set; }
        public int Port { get; private set; }
        public int Queries;

        public string EndPointText => $"127.0.0.1:{Port}";

        public void Start()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _ = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    TcpClient client;
                    try { client = await _listener.AcceptTcpClientAsync(token); }
                    catch (OperationCanceledException) { return; }
                    catch (ObjectDisposedException) { return; }
                    catch { continue; }
                    _ = Task.Run(() => ServeAsync(client), CancellationToken.None);
                }
            }, token);
        }

        private async Task ServeAsync(TcpClient client)
        {
            try
            {
                using (client)
                await using (var stream = client.GetStream())
                {
                    while (true)
                    {
                        var lenBuf = new byte[2];
                        if (!await ReadExactAsync(stream, lenBuf)) return;
                        var len = (lenBuf[0] << 8) | lenBuf[1];
                        if (len is <= 0 or > 65535) return;

                        var query = new byte[len];
                        if (!await ReadExactAsync(stream, query)) return;

                        Interlocked.Increment(ref Queries);
                        if (!Answer)
                        {
                            // Black hole: hold the connection open, say nothing. The
                            // forwarder must time this out rather than fabricate a reply.
                            await Task.Delay(TimeSpan.FromSeconds(8));
                            return;
                        }

                        var answer = BuildAnswer(query);
                        await stream.WriteAsync(new byte[]
                        {
                            (byte)(answer.Length >> 8),
                            (byte)(answer.Length & 0xFF),
                        });
                        await stream.WriteAsync(answer);
                        await stream.FlushAsync();
                    }
                }
            }
            catch { }
        }

        private static async Task<bool> ReadExactAsync(NetworkStream s, byte[] buf)
        {
            var off = 0;
            while (off < buf.Length)
            {
                var n = await s.ReadAsync(buf.AsMemory(off));
                if (n <= 0) return false;
                off += n;
            }
            return true;
        }

        public void Reset() => Interlocked.Exchange(ref Queries, 0);

        public void Dispose()
        {
            try { _cts?.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }
            _cts?.Dispose();
        }

        /// <summary>Echoes the query with the QR bit set and one A record.</summary>
        private static byte[] BuildAnswer(byte[] query)
        {
            var q = (byte[])query.Clone();
            if (q.Length < 12) return q;
            q[2] |= 0x80;     // QR = response
            q[3] |= 0x80;     // RA
            q[6] = 0; q[7] = 1;   // ANCOUNT = 1

            var answer = new List<byte>(q);
            answer.Add(0xC0); answer.Add(0x0C);   // name pointer to offset 12
            answer.Add(0x00); answer.Add(0x01);   // TYPE = A
            answer.Add(0x00); answer.Add(0x01);   // CLASS = IN
            answer.Add(0x00); answer.Add(0x00);
            answer.Add(0x00); answer.Add(0x3C);   // TTL = 60
            answer.Add(0x00); answer.Add(0x04);   // RDLENGTH = 4
            // 203.0.113.1, a TEST-NET-3 address, so nothing here can be mistaken for a real host.
            answer.Add(203);
            answer.Add(0);
            answer.Add(113);
            answer.Add(1);
            return answer.ToArray();
        }
    }

    /// <summary>
    /// A minimal SOCKS5 server that records which host:port each CONNECT asked for and
    /// splices the connection through to that target.
    /// </summary>
    private sealed class FakeSocksRelay : IDisposable
    {
        private TcpListener? _listener;
        private CancellationTokenSource? _cts;

        public int Port { get; private set; }
        public List<string> Dialled { get; } = new();
        public int Accepted;
        private readonly object _lock = new();

        public void Start()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _ = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    TcpClient client;
                    try { client = await _listener.AcceptTcpClientAsync(token); }
                    catch (OperationCanceledException) { return; }
                    catch (ObjectDisposedException) { return; }
                    catch { continue; }
                    Interlocked.Increment(ref Accepted);
                    _ = Task.Run(() => HandleAsync(client), CancellationToken.None);
                }
            }, token);
        }

        private async Task HandleAsync(TcpClient client)
        {
            LastError = null;
            try
            {
                using (client)
                await using (var stream = client.GetStream())
                {
                    // greeting
                    var greet = new byte[2];
                    if (!await ReadExact(stream, greet)) { LastError = "short greeting"; return; }
                    var methodCount = greet[1];
                    var methods = new byte[methodCount];
                    if (!await ReadExact(stream, methods)) { LastError = "short methods"; return; }
                    await stream.WriteAsync(new byte[] { 0x05, 0x00 });
                    await stream.FlushAsync();


                    // request
                    var header = new byte[4];
                    if (!await ReadExact(stream, header))
                    {
                        LastError = "short header (client closed before sending CONNECT)";
                        return;
                    }
                    if (header[1] != 0x01) { LastError = $"cmd={header[1]}"; return; } // only CONNECT

                    string host;
                    int port;
                    switch (header[3])
                    {
                        case 0x01:
                        {
                            var a = new byte[4];
                            if (!await ReadExact(stream, a)) return;
                            host = new IPAddress(a).ToString();
                            var p = new byte[2];
                            if (!await ReadExact(stream, p)) return;
                            port = (p[0] << 8) | p[1];
                            break;
                        }
                        case 0x03:
                        {
                            var l = new byte[1];
                            if (!await ReadExact(stream, l)) return;
                            var h = new byte[l[0]];
                            if (!await ReadExact(stream, h)) return;
                            host = Encoding.ASCII.GetString(h);
                            var p = new byte[2];
                            if (!await ReadExact(stream, p)) return;
                            port = (p[0] << 8) | p[1];
                            break;
                        }
                        default:
                            await stream.WriteAsync(new byte[] { 0x05, 0x07, 0x00, 0x01, 0, 0, 0, 0, 0, 0 });
                            return;
                    }

                    lock (_lock) Dialled.Add($"{host}:{port}");

                    using var upstream = new TcpClient();
                    try
                    {
                        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts!.Token);
                        cts.CancelAfter(TimeSpan.FromSeconds(3));
                        await upstream.ConnectAsync(host, port, cts.Token);
                    }
                    catch
                    {
                        // 0x05 = connection refused
                        await stream.WriteAsync(new byte[] { 0x05, 0x05, 0x00, 0x01, 0, 0, 0, 0, 0, 0 });
                        return;
                    }

                    await stream.WriteAsync(new byte[] { 0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 });
                    await using var us = upstream.GetStream();
                    var t = Task.WhenAny(Pump(stream, us), Pump(us, stream));
                    await t;
                }
            }
            catch (Exception ex)
            {
                LastError = $"{ex.GetType().Name}: {ex.Message}";
            }
        }

        public string? LastError { get; private set; }

        

        private static async Task Pump(NetworkStream from, NetworkStream to)
        {
            var buf = new byte[4096];
            try
            {
                while (true)
                {
                    var n = await from.ReadAsync(buf);
                    if (n <= 0) break;
                    await to.WriteAsync(buf.AsMemory(0, n));
                    await to.FlushAsync();
                }
            }
            catch { }
        }

        private static async Task<bool> ReadExact(NetworkStream s, byte[] buf)
        {
            var off = 0;
            while (off < buf.Length)
            {
                var n = await s.ReadAsync(buf.AsMemory(off));
                if (n <= 0) return false;
                off += n;
            }
            return true;
        }

        public string DialledSummary
        {
            get { lock (_lock) return Dialled.Count == 0 ? "(none)" : string.Join(",", Dialled); }
        }

        public void Stop()
        {
            try { _cts?.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }
        }

        public void Dispose()
        {
            Stop();
            _cts?.Dispose();
        }
    }
}