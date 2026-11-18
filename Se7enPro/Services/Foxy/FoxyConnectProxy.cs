using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Se7enPro.Services.Foxy;

public sealed class FoxyConnectProxy : IDisposable
{
    private static readonly ConcurrentDictionary<int, byte> ActivePorts = new();
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    private static readonly string[] AllowedSignInDomains =
    {
        "firefox.com",
        "mozilla.org",
        "mozilla.net",
        "mozilla.com",
        "mozgcp.net",
        "fastly.net",
        "fastly-masque.net",
        "cloudflare.com",
        "cloudflareinsights.com",
        "hcaptcha.com",
        "recaptcha.net",
        "google.com",
        "gstatic.com",
        "googleapis.com",
        "googleusercontent.com",
        "apple.com",
        "cdn-apple.com",
        "icloud.com",
        "arkoselabs.com",
        "funcaptcha.com",
        "stripe.com",
        "workers.dev",
    };

    private static readonly object DefaultGate = new();
    private static FoxyConnectProxy? _defaultInstance;

    private readonly object _gate = new();
    private readonly Func<IWebProxy?>? _upstreamProxyProvider;
    private readonly bool _restrictToSignInHosts;
    private readonly HelloSplitMode _splitMode;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private int _port;
    private bool _disposed;

    public FoxyConnectProxy(
        Func<IWebProxy?>? upstreamProxyProvider = null,
        bool restrictToSignInHosts = true,
        HelloSplitMode splitMode = HelloSplitMode.Sni)
    {
        _upstreamProxyProvider = upstreamProxyProvider;
        _restrictToSignInHosts = restrictToSignInHosts;
        _splitMode = splitMode;
    }

    public static FoxyConnectProxy Default
    {
        get
        {
            if (_defaultInstance is not null)
                return _defaultInstance;
            lock (DefaultGate)
            {
                return _defaultInstance ??= new FoxyConnectProxy(restrictToSignInHosts: true);
            }
        }
    }

    public int Port => _port;

    public string ProxyUriString => _port > 0 ? $"http://127.0.0.1:{_port}" : "";

    public string WebViewProxyArg => _port > 0 ? $"http=127.0.0.1:{_port}" : "";

    public static bool IsOwnProxyUri(Uri? uri) =>
        uri is not null &&
        (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
         (IPAddress.TryParse(uri.Host, out var ip) && IPAddress.IsLoopback(ip))) &&
        ActivePorts.ContainsKey(uri.Port);

    public static bool IsSignInHostAllowed(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return false;

        var trimmed = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (trimmed == "localhost")
            return false;

        if (IPAddress.TryParse(trimmed, out var ip))
            return !FoxyNetwork.IsBlockPageOrBogon(ip);

        foreach (var suffix in AllowedSignInDomains)
        {
            if (trimmed == suffix || trimmed.EndsWith("." + suffix, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    public string EnsureStarted()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_listener is not null && _port > 0)
                return ProxyUriString;

            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var cts = new CancellationTokenSource();

            _listener = listener;
            _cts = cts;
            _port = port;
            ActivePorts[port] = 0;

            _ = Task.Run(() => AcceptLoopAsync(listener, cts.Token), cts.Token);
            return ProxyUriString;
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch
            {
                continue;
            }

            _ = Task.Run(() => HandleClientAsync(client, ct), CancellationToken.None);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using var _ = client;
        client.NoDelay = true;
        using var clientStream = client.GetStream();

        string? requestLine;
        try
        {
            requestLine = await ReadConnectRequestAsync(clientStream, ct).ConfigureAwait(false);
        }
        catch
        {
            return;
        }

        if (string.IsNullOrEmpty(requestLine))
            return;

        if (!TryParseConnectTarget(requestLine, out var targetHost, out var targetPort))
        {
            await SendHttpErrorAsync(clientStream, "405 Method Not Allowed", ct).ConfigureAwait(false);
            return;
        }

        if (_restrictToSignInHosts && !IsSignInHostAllowed(targetHost))
        {
            await SendHttpErrorAsync(clientStream, "403 Forbidden", ct).ConfigureAwait(false);
            return;
        }

        Stream? remoteStream = null;
        IPAddress? directRemoteIp = null;
        var receivedAnyRemoteByte = 0;

        try
        {
            using var dialCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            dialCts.CancelAfter(ConnectTimeout);

            var upstreamUri = ResolveNonSelfUpstream(targetHost, targetPort, out var upstreamCreds);
            if (upstreamUri is not null)
            {
                remoteStream = await DialViaUpstreamProxyAsync(
                    upstreamUri, upstreamCreds, targetHost, targetPort, dialCts.Token).ConfigureAwait(false);
            }
            else
            {
                var (stream, remoteIp) = await DialDirectWithAntiCensorshipAsync(
                    targetHost, targetPort, dialCts.Token).ConfigureAwait(false);
                remoteStream = stream;
                directRemoteIp = remoteIp;
            }

            await clientStream.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), ct)
                .ConfigureAwait(false);
            await clientStream.FlushAsync(ct).ConfigureAwait(false);

            var up = PumpAsync(clientStream, remoteStream, onBytesRead: null, ct);
            var down = PumpAsync(remoteStream, clientStream, _ => Interlocked.Exchange(ref receivedAnyRemoteByte, 1), ct);
            var firstDone = await Task.WhenAny(up, down).ConfigureAwait(false);
            if (directRemoteIp is not null &&
                Volatile.Read(ref receivedAnyRemoteByte) == 0 &&
                firstDone == down &&
                await down.ConfigureAwait(false))
            {
                FoxyNetwork.MarkAddressFailed(directRemoteIp);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (directRemoteIp is not null && Volatile.Read(ref receivedAnyRemoteByte) == 0)
                FoxyNetwork.MarkAddressFailed(directRemoteIp);

            if (remoteStream is null)
            {
                try { await SendHttpErrorAsync(clientStream, "502 Bad Gateway", ct).ConfigureAwait(false); } catch { }
            }
        }
        finally
        {
            if (remoteStream is not null)
            {
                try { await remoteStream.DisposeAsync().ConfigureAwait(false); } catch { }
            }
        }
    }

    private Uri? ResolveNonSelfUpstream(string host, int port, out NetworkCredential? credentials)
    {
        credentials = null;
        var proxy = _upstreamProxyProvider?.Invoke();
        if (proxy is null)
            return null;

        var targetUri = new Uri($"https://{host}:{port}/");
        if (proxy.IsBypassed(targetUri))
            return null;

        var pUri = proxy.GetProxy(targetUri);
        if (pUri is null || pUri.Equals(targetUri) || IsOwnProxyUri(pUri))
            return null;

        credentials = proxy.Credentials?.GetCredential(pUri, "Basic");
        return pUri;
    }

    private async Task<(Stream Stream, IPAddress? RemoteIp)> DialDirectWithAntiCensorshipAsync(
        string targetHost, int targetPort, CancellationToken ct)
    {
        var addresses = await FoxyNetwork.ResolveHostAddressesAsync(targetHost, ct).ConfigureAwait(false);
        if (addresses.Length == 0)
            throw new SocketException((int)SocketError.HostNotFound);

        var sock = await FoxyNetwork.ConnectRaceAsync(addresses, targetPort, ct).ConfigureAwait(false);
        var remoteIp = (sock.RemoteEndPoint as IPEndPoint)?.Address;
        Stream netStream = new NetworkStream(sock, ownsSocket: true);
        if (_splitMode != HelloSplitMode.Off)
            netStream = new HelloSplitStream(netStream, _splitMode);
        return (netStream, remoteIp);
    }

    private static async Task<Stream> DialViaUpstreamProxyAsync(
        Uri proxyUri,
        NetworkCredential? creds,
        string targetHost,
        int targetPort,
        CancellationToken ct)
    {
        var sock = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await sock.ConnectAsync(proxyUri.Host, proxyUri.Port, ct).ConfigureAwait(false);
            var stream = new NetworkStream(sock, ownsSocket: true);
            if (proxyUri.Scheme.StartsWith("socks5", StringComparison.OrdinalIgnoreCase))
            {
                await PerformSocks5ConnectAsync(stream, creds, targetHost, targetPort, ct).ConfigureAwait(false);
            }
            else
            {
                await PerformHttpConnectAsync(stream, creds, targetHost, targetPort, ct).ConfigureAwait(false);
            }
            return stream;
        }
        catch
        {
            sock.Dispose();
            throw;
        }
    }

    private static async Task PerformHttpConnectAsync(
        Stream stream, NetworkCredential? creds, string host, int port, CancellationToken ct)
    {
        var authority = host.Contains(':') ? $"[{host}]:{port}" : $"{host}:{port}";
        var sb = new StringBuilder();
        sb.Append("CONNECT ").Append(authority).Append(" HTTP/1.1\r\n");
        sb.Append("Host: ").Append(authority).Append("\r\n");
        if (creds is not null && !string.IsNullOrEmpty(creds.UserName))
        {
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{creds.UserName}:{creds.Password}"));
            sb.Append("Proxy-Authorization: Basic ").Append(token).Append("\r\n");
        }
        sb.Append("\r\n");

        await stream.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()), ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);

        var statusLine = await ReadHeaderBlockAsync(stream, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(statusLine) || !statusLine.Contains(" 200", StringComparison.Ordinal))
            throw new IOException($"Upstream HTTP proxy rejected CONNECT ({statusLine}).");
    }

    private static async Task PerformSocks5ConnectAsync(
        Stream stream, NetworkCredential? creds, string host, int port, CancellationToken ct)
    {
        var hasAuth = creds is not null && !string.IsNullOrEmpty(creds.UserName);
        var greeting = hasAuth ? new byte[] { 0x05, 0x02, 0x00, 0x02 } : new byte[] { 0x05, 0x01, 0x00 };
        await stream.WriteAsync(greeting, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);

        var methodReply = new byte[2];
        await ReadExactlyAsync(stream, methodReply, ct).ConfigureAwait(false);
        if (methodReply[0] != 0x05 || methodReply[1] == 0xFF)
            throw new IOException("SOCKS5 proxy rejected authentication methods.");

        if (methodReply[1] == 0x02)
        {
            var u = Encoding.UTF8.GetBytes(creds?.UserName ?? "");
            var p = Encoding.UTF8.GetBytes(creds?.Password ?? "");
            if (u.Length > 255 || p.Length > 255)
                throw new IOException("SOCKS5 credentials exceed 255 bytes.");
            var authReq = new byte[3 + u.Length + p.Length];
            authReq[0] = 0x01;
            authReq[1] = (byte)u.Length;
            u.CopyTo(authReq, 2);
            authReq[2 + u.Length] = (byte)p.Length;
            p.CopyTo(authReq, 3 + u.Length);
            await stream.WriteAsync(authReq, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);

            var authReply = new byte[2];
            await ReadExactlyAsync(stream, authReply, ct).ConfigureAwait(false);
            if (authReply[1] != 0x00)
                throw new IOException("SOCKS5 authentication failed.");
        }

        var hostBytes = Encoding.ASCII.GetBytes(host);
        if (hostBytes.Length > 255)
            throw new IOException("SOCKS5 hostname too long.");

        var req = new byte[7 + hostBytes.Length];
        req[0] = 0x05;
        req[1] = 0x01;
        req[2] = 0x00;
        req[3] = 0x03;
        req[4] = (byte)hostBytes.Length;
        hostBytes.CopyTo(req, 5);
        req[5 + hostBytes.Length] = (byte)(port >> 8);
        req[6 + hostBytes.Length] = (byte)(port & 0xFF);

        await stream.WriteAsync(req, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);

        var head = new byte[4];
        await ReadExactlyAsync(stream, head, ct).ConfigureAwait(false);
        if (head[0] != 0x05 || head[1] != 0x00)
            throw new IOException($"SOCKS5 CONNECT failed with code 0x{head[1]:X2}.");

        var addrLen = head[3] switch
        {
            0x01 => 4,
            0x04 => 16,
            0x03 => await ReadSingleByteLengthAsync(stream, ct).ConfigureAwait(false),
            _ => throw new IOException("SOCKS5 returned unknown address type."),
        };
        var discard = new byte[addrLen + 2];
        await ReadExactlyAsync(stream, discard, ct).ConfigureAwait(false);
    }

    private static async Task<int> ReadSingleByteLengthAsync(Stream stream, CancellationToken ct)
    {
        var b = new byte[1];
        await ReadExactlyAsync(stream, b, ct).ConfigureAwait(false);
        return b[0];
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(offset), ct).ConfigureAwait(false);
            if (n == 0)
                throw new EndOfStreamException("Unexpected EOF while negotiating upstream proxy.");
            offset += n;
        }
    }

    private static async Task<string?> ReadConnectRequestAsync(Stream stream, CancellationToken ct) =>
        await ReadHeaderBlockAsync(stream, ct).ConfigureAwait(false);

    private static async Task<string?> ReadHeaderBlockAsync(Stream stream, CancellationToken ct)
    {
        const int maxHeaderBytes = 16 * 1024;
        var buf = new byte[maxHeaderBytes];
        var len = 0;
        var one = new byte[1];
        while (len < maxHeaderBytes)
        {
            var read = await stream.ReadAsync(one, ct).ConfigureAwait(false);
            if (read == 0)
                return null;
            buf[len++] = one[0];
            if (len >= 4 &&
                buf[len - 4] == (byte)'\r' &&
                buf[len - 3] == (byte)'\n' &&
                buf[len - 2] == (byte)'\r' &&
                buf[len - 1] == (byte)'\n')
            {
                var headerText = Encoding.ASCII.GetString(buf, 0, len);
                var eol = headerText.IndexOf('\r');
                return eol > 0 ? headerText[..eol] : headerText;
            }
        }
        return null;
    }

    private static bool TryParseConnectTarget(string requestLine, out string host, out int port)
    {
        host = "";
        port = 0;
        var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !string.Equals(parts[0], "CONNECT", StringComparison.OrdinalIgnoreCase))
            return false;

        var authority = parts[1];
        if (authority.StartsWith('['))
        {
            var closeBracket = authority.IndexOf(']');
            if (closeBracket <= 1 || closeBracket + 2 >= authority.Length || authority[closeBracket + 1] != ':')
                return false;
            host = authority[1..closeBracket];
            return int.TryParse(authority[(closeBracket + 2)..], out port) && port is > 0 and <= 65535;
        }

        var colon = authority.LastIndexOf(':');
        if (colon <= 0 || colon + 1 >= authority.Length)
            return false;

        host = authority[..colon];
        return int.TryParse(authority[(colon + 1)..], out port) && port is > 0 and <= 65535;
    }

    private static async Task SendHttpErrorAsync(Stream stream, string status, CancellationToken ct)
    {
        var bytes = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task<bool> PumpAsync(Stream src, Stream dst, Action<int>? onBytesRead, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16384);
        try
        {
            while (true)
            {
                var read = await src.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                if (read == 0)
                    return false;
                onBytesRead?.Invoke(read);
                await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch
        {
            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            if (_port > 0)
                ActivePorts.TryRemove(_port, out _);
            _port = 0;
            try { _cts?.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }
            try { _cts?.Dispose(); } catch { }
            _listener = null;
            _cts = null;
        }
    }
}
