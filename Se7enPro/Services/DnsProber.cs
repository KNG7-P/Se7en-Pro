using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Se7enPro.Services;

/// <summary>
/// Reachability probes for the DNS settings screen.
/// </summary>
public sealed record DnsProbeResult(
    string Entry,
    bool Reachable,
    string Detail,
    double? ResponseMs)
{
    public override string ToString() =>
        ResponseMs is { } ms ? $"{Entry}: OK ({ms:0} ms)" : $"{Entry}: {Detail}";
}

/// <summary>One probe per transport, so the caller can run the right one.</summary>
public interface IDnsProber
{
    Task<DnsProbeResult> ProbeAsync(DnsServerEntry entry, CancellationToken ct);
}

public sealed class DnsProber : IDnsProber
{
    private const int ConnectTimeoutMs = 4000;
    private const int ReadTimeoutMs = 4000;

    private static readonly HttpClient DohClient = CreateDohClient();

    private static HttpClient CreateDohClient()
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = static (_, _, _, _) => true,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 3,
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(ConnectTimeoutMs + ReadTimeoutMs) };
    }

    /// <summary>Accepts any certificate for reachability probe verification.</summary>
    private static bool AcceptAnyCertificate(
        object sender,
        System.Security.Cryptography.X509Certificates.X509Certificate? certificate,
        System.Security.Cryptography.X509Certificates.X509Chain? chain,
        SslPolicyErrors errors) => true;

    public async Task<DnsProbeResult> ProbeAsync(DnsServerEntry entry, CancellationToken ct)
    {
        var started = Stopwatch.StartNew();
        try
        {
            return entry.Transport switch
            {
                DnsTransport.Dot => await ProbeDotAsync(entry, ct, started),
                DnsTransport.Doh => await ProbeDohAsync(entry, ct, started),
                _ => await ProbeUdpAsync(entry, ct, started),
            };
        }
        catch (OperationCanceledException)
        {
            return new DnsProbeResult(entry.Raw, false, "cancelled", null);
        }
        catch (SocketException ex)
        {
            return new DnsProbeResult(entry.Raw, false, SocketReason(ex), null);
        }
        catch (Exception ex)
        {
            return new DnsProbeResult(entry.Raw, false, ex.GetType().Name, null);
        }
    }

    /// <summary>
    /// Plain UDP: send a real A query for a fixed name and accept ANY DNS response.
    /// NXDOMAIN still proves the server is answering DNS, so RCODE is not inspected.
    /// </summary>
    private static async Task<DnsProbeResult> ProbeUdpAsync(
        DnsServerEntry entry, CancellationToken ct, Stopwatch started)
    {
        var query = BuildProbeQuery();
        using var udp = new UdpClient(entry.Address?.AddressFamily ?? AddressFamily.InterNetwork);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ReadTimeoutMs);

        var host = entry.Address?.ToString() ?? entry.Host;
        var endpoint = new IPEndPoint(
            IPAddress.TryParse(host, out var ip) ? ip : IPAddress.Loopback, entry.Port);

        var sendTask = udp.SendAsync(query, query.Length, endpoint);
        await sendTask.WaitAsync(timeout.Token);

        var receive = await udp.ReceiveAsync(timeout.Token);
        if (receive.Buffer.Length < 12)
            return new DnsProbeResult(entry.Raw, false, "reply too short to be DNS", null);

        if (receive.Buffer[0] != query[0] || receive.Buffer[1] != query[1])
            return new DnsProbeResult(entry.Raw, false, "reply did not match the query", null);

        started.Stop();
        return new DnsProbeResult(entry.Raw, true, "answered", started.Elapsed.TotalMilliseconds);
    }

    /// <summary>DoT reachability probe via TLS handshake.</summary>
    private static async Task<DnsProbeResult> ProbeDotAsync(
        DnsServerEntry entry, CancellationToken ct, Stopwatch started)
    {
        using var tcp = new TcpClient();
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(ConnectTimeoutMs);

        var host = entry.Address?.ToString() ?? entry.Host;
        await tcp.ConnectAsync(host, entry.Port, connectCts.Token);

        await using var stream = tcp.GetStream();

        using var ssl = new SslStream(stream, leaveInnerStreamOpen: false, AcceptAnyCertificate);
        using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        handshakeCts.CancelAfter(ReadTimeoutMs);
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = entry.Host,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        }, handshakeCts.Token);

        started.Stop();
        return new DnsProbeResult(entry.Raw, true, "TLS handshake OK", started.Elapsed.TotalMilliseconds);
    }

    /// <summary>
    /// DoH: POST a wire-format query with content-type application/dns-message and
    /// accept any 2xx with a body at least as long as a DNS header.
    /// </summary>
    private static async Task<DnsProbeResult> ProbeDohAsync(
        DnsServerEntry entry, CancellationToken ct, Stopwatch started)
    {
        var body = entry.Raw.StartsWith("doh:", StringComparison.OrdinalIgnoreCase) &&
                   !entry.Raw.StartsWith("doh://", StringComparison.OrdinalIgnoreCase)
            ? "https://" + entry.Raw[4..]
            : entry.Raw;

        using var content = new ByteArrayContent(BuildProbeQuery());
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/dns-message");
        content.Headers.TryAddWithoutValidation("accept", "application/dns-message");

        using var req = new HttpRequestMessage(HttpMethod.Post, body) { Content = content };
        using var res = await DohClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!res.IsSuccessStatusCode)
            return new DnsProbeResult(entry.Raw, false, $"HTTP {(int)res.StatusCode}", null);

        await using var stream = await res.Content.ReadAsStreamAsync(ct);
        var buf = new byte[512];
        var read = await stream.ReadAsync(buf, ct);
        if (read < 12)
            return new DnsProbeResult(entry.Raw, false, "response shorter than a DNS header", null);

        started.Stop();
        return new DnsProbeResult(entry.Raw, true, $"{read} bytes", started.Elapsed.TotalMilliseconds);
    }

    /// <summary>
    /// A standard A query for example.com, on the wire. Header is 12 bytes; the question
    /// is 7 "example" 3 "com" 0, type A, class IN.
    /// </summary>
    internal static byte[] BuildProbeQuery()
    {
        var q = new byte[29];
        q[0] = 0x12; q[1] = 0x34;              // transaction id
        q[2] = 0x01; q[3] = 0x00;              // standard query, recursion desired
        q[4] = 0x00; q[5] = 0x01;              // QDCOUNT = 1
        q[6] = 0x00; q[7] = 0x00;              // ANCOUNT
        q[8] = 0x00; q[9] = 0x00;              // NSCOUNT
        q[10] = 0x00; q[11] = 0x00;            // ARCOUNT
        q[12] = 7;                             // "example"
        q[13] = (byte)'e'; q[14] = (byte)'x'; q[15] = (byte)'a';
        q[16] = (byte)'m'; q[17] = (byte)'p'; q[18] = (byte)'l'; q[19] = (byte)'e';
        q[20] = 3;                             // "com"
        q[21] = (byte)'c'; q[22] = (byte)'o'; q[23] = (byte)'m';
        q[24] = 0x00;                          // end of name
        q[25] = 0x00; q[26] = 0x01;            // QTYPE = A
        q[27] = 0x00; q[28] = 0x01;            // QCLASS = IN
        return q;
    }

    private static string SocketReason(SocketException ex) => ex.SocketErrorCode switch
    {
        SocketError.TimedOut => "timed out",
        SocketError.ConnectionRefused => "connection refused",
        SocketError.HostUnreachable => "host unreachable",
        SocketError.NetworkUnreachable => "network unreachable",
        SocketError.AddressFamilyNotSupported => "address family not supported",
        _ => ex.SocketErrorCode.ToString(),
    };
}