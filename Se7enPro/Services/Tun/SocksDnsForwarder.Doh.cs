using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;

namespace Se7enPro.Services;

internal sealed partial class SocksDnsForwarder
{
    private const int MaxDohResponse = 64 * 1024;
    private readonly object _dohClientLock = new();
    private HttpClient? _dohHttpClient;

    private HttpClient GetOrCreateDohClient()
    {
        lock (_dohClientLock)
        {
            return _dohHttpClient ??= CreateDohClient();
        }
    }

    private void DisposeDohClient()
    {
        HttpClient? client;
        lock (_dohClientLock)
        {
            client = _dohHttpClient;
            _dohHttpClient = null;
        }
        try { client?.Dispose(); } catch { }
    }

    private HttpClient CreateDohClient()
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            UseCookies = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(45),
            MaxConnectionsPerServer = 8,
            EnableMultipleHttp2Connections = true,
            KeepAlivePingDelay = TimeSpan.FromSeconds(15),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(4),
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always,
            ConnectTimeout = _connectTimeout,
            SslOptions = new SslClientAuthenticationOptions
            {
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            },
            ConnectCallback = async (context, ct) =>
            {
                var host = context.DnsEndPoint.Host.Trim('[', ']');
                IPAddress.TryParse(host, out var ip);
                var stream = await OpenSocksTunnelForEndpointAsync(ip, host, context.DnsEndPoint.Port, ct);
                if (stream is null)
                {
                    throw new IOException($"SOCKS5 tunnel to {host}:{context.DnsEndPoint.Port} failed");
                }
                return stream;
            },
        };

        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    private async Task<byte[]?> QueryDotAsync(DnsServerEntry target, byte[] query, CancellationToken ct)
    {
        await using var stream = await OpenSocksTunnelAsync(target, ct);
        if (stream is null) return null;

        try
        {
            await using var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = target.Host,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            }, ct);

            var framed = new byte[2 + query.Length];
            framed[0] = (byte)(query.Length >> 8);
            framed[1] = (byte)(query.Length & 0xFF);
            Buffer.BlockCopy(query, 0, framed, 2, query.Length);
            await ssl.WriteAsync(framed, ct);
            await ssl.FlushAsync(ct);

            var len = new byte[2];
            await ReadExactlyAsync(ssl, len, ct);
            var answerLen = (len[0] << 8) | len[1];
            if (answerLen is <= 0 or > 65535) return null;

            var answer = new byte[answerLen];
            await ReadExactlyAsync(ssl, answer, ct);

            if (query.Length >= 2 && answerLen >= 3)
            {
                if (answer[0] != query[0] || answer[1] != query[1]) return null;
                if ((answer[2] & 0x80) == 0) return null;
            }

            return answer;
        }
        catch (AuthenticationException ex)
        {
            Diag?.Invoke($"dns: DoT TLS handshake with {target.Host} rejected ({ex.Message})");
            return null;
        }
    }

    private async Task<byte[]?> QueryDohAsync(DnsServerEntry target, byte[] query, CancellationToken ct)
    {
        try
        {
            var client = GetOrCreateDohClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, DohUri(target))
            {
                Version = HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
                Content = new ByteArrayContent(query),
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/dns-message"));
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/dns-message");

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) return null;

            await using var bodyStream = await response.Content.ReadAsStreamAsync(ct);
            using var ms = new MemoryStream();
            var chunk = new byte[4096];
            while (true)
            {
                var n = await bodyStream.ReadAsync(chunk.AsMemory(), ct);
                if (n <= 0) break;
                if (ms.Length + n > MaxDohResponse) return null;
                ms.Write(chunk, 0, n);
            }

            var answer = ms.ToArray();
            if (answer.Length is <= 0 or > 65535) return null;

            if (query.Length >= 2 && answer.Length >= 3)
            {
                if (answer[0] != query[0] || answer[1] != query[1]) return null;
                if ((answer[2] & 0x80) == 0) return null;
            }

            return answer;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static Uri DohUri(DnsServerEntry target)
    {
        if (Uri.TryCreate(target.Raw, UriKind.Absolute, out var uri)
            && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return uri;
        if (target.Raw.StartsWith("doh://", StringComparison.OrdinalIgnoreCase) &&
            Uri.TryCreate("https://" + target.Raw[6..], UriKind.Absolute, out uri)) return uri;
        if (target.Raw.StartsWith("doh:", StringComparison.OrdinalIgnoreCase) &&
            Uri.TryCreate("https://" + target.Raw[4..], UriKind.Absolute, out uri)) return uri;
        var hostPart = target.Host.Contains(':') && !target.Host.StartsWith('[') ? $"[{target.Host}]" : target.Host;
        return new Uri($"https://{hostPart}:{target.Port}/dns-query");
    }
}
