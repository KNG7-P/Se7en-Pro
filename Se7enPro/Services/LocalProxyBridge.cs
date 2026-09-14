using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Se7enPro.Services;

public sealed class LocalProxyBridge : IAsyncDisposable
{

    private const long NotifyIntervalMs = 500;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private long _bytesSent;
    private long _bytesReceived;
    private long _lastNotifyTick;

    private string? _username;
    private string? _password;
    private bool _isHttp;

    public int ListenPort { get; private set; }
    public int TargetPort { get; private set; }
    public long BytesSent => Interlocked.Read(ref _bytesSent);
    public long BytesReceived => Interlocked.Read(ref _bytesReceived);

    public event EventHandler? BytesTransferredChanged;

    public void Start(
        int listenPort,
        int targetPort,
        IPAddress? bindAddress = null,
        string? username = null,
        string? password = null,
        bool isHttp = false)
    {
        Stop();

        ResetCounters();
        ListenPort = listenPort;
        TargetPort = targetPort;
        _username = string.IsNullOrWhiteSpace(username) ? null : username.Trim();
        _password = string.IsNullOrEmpty(password) ? null : password;
        _isHttp = isHttp;

        _cts = new CancellationTokenSource();
        _listener = new TcpListener(bindAddress ?? IPAddress.Loopback, listenPort);
        try
        {
            _listener.ExclusiveAddressUse = true;
        }
        catch { }
        _listener.Start();

        _ = Task.Run(() => AcceptLoopAsync(_listener, targetPort, _cts.Token));
    }

    private async Task AcceptLoopAsync(TcpListener listener, int targetPort, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                client.NoDelay = true;
                _ = Task.Run(() => ForwardClientAsync(client, targetPort, ct), ct);
            }
            catch (OperationCanceledException) { break; }
            catch
            {

                try { await Task.Delay(50, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task ForwardClientAsync(TcpClient client, int targetPort, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                var clientStream = client.GetStream();
                var remoteIp = ((IPEndPoint?)client.Client.RemoteEndPoint)?.Address;
                var isLoopback = remoteIp != null && IPAddress.IsLoopback(remoteIp);

                byte[]? httpInitialBytes = null;
                int httpInitialLen = 0;

                if (!isLoopback && _username != null && _password != null)
                {
                    if (_isHttp)
                    {
                        var (authed, buf, len) = await AuthenticateHttpAsync(clientStream, ct);
                        if (!authed) return;
                        httpInitialBytes = buf;
                        httpInitialLen = len;
                    }
                    else
                    {
                        var authed = await AuthenticateSocks5Async(clientStream, ct);
                        if (!authed) return;
                    }
                }

                using var target = new TcpClient { NoDelay = true };
                await target.ConnectAsync(IPAddress.Loopback, targetPort, ct);
                var targetStream = target.GetStream();

                if (httpInitialBytes != null && httpInitialLen > 0)
                {
                    await targetStream.WriteAsync(httpInitialBytes.AsMemory(0, httpInitialLen), ct);
                }
                else if (!isLoopback && _username != null && _password != null && !_isHttp)
                {

                    await targetStream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, ct);
                    var greetResp = new byte[2];
                    await ReadExactAsync(targetStream, greetResp, ct);
                    if (greetResp[0] != 0x05 || greetResp[1] != 0x00) return;
                }

                var up = PumpStreamAsync(clientStream, targetStream, target.Client, isUpload: true, ct);
                var down = PumpStreamAsync(targetStream, clientStream, client.Client, isUpload: false, ct);
                await Task.WhenAll(up, down);
            }
            catch { }
        }
    }

    private async Task<bool> AuthenticateSocks5Async(NetworkStream stream, CancellationToken ct)
    {
        try
        {
            var header = new byte[2];
            await ReadExactAsync(stream, header, ct);
            if (header[0] != 0x05) return false;
            var nMethods = header[1];
            var methods = new byte[nMethods];
            await ReadExactAsync(stream, methods, ct);

            var supportsUserPass = false;
            for (var i = 0; i < nMethods; i++)
            {
                if (methods[i] == 0x02) supportsUserPass = true;
            }

            if (!supportsUserPass)
            {
                await stream.WriteAsync(new byte[] { 0x05, 0xFF }, ct);
                return false;
            }

            await stream.WriteAsync(new byte[] { 0x05, 0x02 }, ct);

            var authVer = new byte[1];
            await ReadExactAsync(stream, authVer, ct);
            if (authVer[0] != 0x01) return false;

            var ulenBuf = new byte[1];
            await ReadExactAsync(stream, ulenBuf, ct);
            var ulen = ulenBuf[0];
            var userBuf = new byte[ulen];
            await ReadExactAsync(stream, userBuf, ct);
            var user = Encoding.UTF8.GetString(userBuf);

            var plenBuf = new byte[1];
            await ReadExactAsync(stream, plenBuf, ct);
            var plen = plenBuf[0];
            var passBuf = new byte[plen];
            await ReadExactAsync(stream, passBuf, ct);
            var pass = Encoding.UTF8.GetString(passBuf);

            if (user == _username && pass == _password)
            {
                await stream.WriteAsync(new byte[] { 0x01, 0x00 }, ct);
                return true;
            }
            else
            {
                await stream.WriteAsync(new byte[] { 0x01, 0x01 }, ct);
                return false;
            }
        }
        catch
        {
            return false;
        }
    }

    private async Task<(bool Authed, byte[]? Buffer, int Length)> AuthenticateHttpAsync(NetworkStream stream, CancellationToken ct)
    {
        try
        {
            var buf = new byte[8192];
            var total = 0;
            while (total < buf.Length)
            {
                var r = await stream.ReadAsync(buf.AsMemory(total, buf.Length - total), ct);
                if (r <= 0) return (false, null, 0);
                total += r;
                var text = Encoding.ASCII.GetString(buf, 0, total);
                var headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (headerEnd >= 0)
                {
                    var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_username}:{_password}"));
                    var authed = text.Contains("Proxy-Authorization: " + expected, StringComparison.OrdinalIgnoreCase);
                    if (authed)
                    {
                        return (true, buf, total);
                    }
                    break;
                }
            }

            var challenge = "HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"Se7en\"\r\nConnection: close\r\nContent-Length: 0\r\n\r\n";
            var challengeBytes = Encoding.ASCII.GetBytes(challenge);
            await stream.WriteAsync(challengeBytes, ct);
            return (false, null, 0);
        }
        catch
        {
            return (false, null, 0);
        }
    }

    private static async Task ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(offset), ct);
            if (n <= 0) throw new IOException("Peer closed connection");
            offset += n;
        }
    }

    private async Task PumpStreamAsync(NetworkStream source, NetworkStream dest, Socket destSocket, bool isUpload, CancellationToken ct)
    {
        var buffer = new byte[32768];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n;
                try
                {
                    n = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
                }
                catch { break; }

                if (n <= 0) break;

                try
                {
                    await dest.WriteAsync(buffer.AsMemory(0, n), ct);
                }
                catch { break; }

                if (isUpload)
                {
                    Interlocked.Add(ref _bytesSent, n);
                }
                else
                {
                    Interlocked.Add(ref _bytesReceived, n);
                }

                MaybeNotify();
            }
        }
        finally
        {

            try { destSocket.Shutdown(SocketShutdown.Send); } catch { }
        }
    }

    private void MaybeNotify()
    {
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastNotifyTick);
        if (now - last < NotifyIntervalMs) return;
        if (Interlocked.CompareExchange(ref _lastNotifyTick, now, last) != last) return;
        try { BytesTransferredChanged?.Invoke(this, EventArgs.Empty); } catch { }
    }

    public void ResetCounters()
    {
        Interlocked.Exchange(ref _bytesSent, 0);
        Interlocked.Exchange(ref _bytesReceived, 0);
        Interlocked.Exchange(ref _lastNotifyTick, 0);
        try { BytesTransferredChanged?.Invoke(this, EventArgs.Empty); } catch { }
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        _cts = null;
        try { _listener?.Stop(); } catch { }
        _listener = null;
    }

    public ValueTask DisposeAsync()
    {
        Stop();
        return ValueTask.CompletedTask;
    }
}
