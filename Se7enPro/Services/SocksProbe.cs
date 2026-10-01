using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Se7enPro.Services;

public static class SocksProbe
{
        private static readonly (string Host, int Port)[] HttpTargets =
    {
        ("cloudflare.com", 80),
        ("www.google.com", 80),
    };

        private static readonly string[] HttpsTargets =
    {
        "https://cp.cloudflare.com/generate_204",
        "https://www.gstatic.com/generate_204",
    };

        public static async Task<string?> WaitForTunnelAsync(
        int socksPort, DateTime deadline, CancellationToken ct, TimeSpan? retryGap = null)
    {
        var gap = retryGap ?? TimeSpan.FromMilliseconds(1500);

        
        
        
        
        
        
        using var deadlineCts = new CancellationTokenSource();
        var remaining = deadline - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero) return null;
        deadlineCts.CancelAfter(remaining);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadlineCts.Token);
        var hard = linked.Token;

        while (!hard.IsCancellationRequested)
        {
            foreach (var (host, port) in HttpTargets)
            {
                if (hard.IsCancellationRequested) return null;
                try
                {
                    if (await ProbeConnectAsync(socksPort, host, port, hard)) return $"{host}:{port}";
                }
                catch { }
            }

            foreach (var url in HttpsTargets)
            {
                if (hard.IsCancellationRequested) return null;
                try
                {
                    if (await ProbeHttpsAsync(socksPort, url, hard)) return new Uri(url).Host;
                }
                catch { }
            }

            try { await Task.Delay(gap, hard); }
            catch (OperationCanceledException) { return null; }
        }

        return null;
    }

        private static async Task<bool> ProbeHttpsAsync(int socksPort, string url, CancellationToken outerCt)
    {
        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        attemptCts.CancelAfter(TimeSpan.FromSeconds(6));

        using var handler = new SocketsHttpHandler
        {
            Proxy = new WebProxy($"socks5://127.0.0.1:{socksPort}"),
            UseProxy = true,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionLifetime = TimeSpan.FromSeconds(1),
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(6) };
        using var res = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, attemptCts.Token);
        return true;
    }

        public static async Task<bool> ProbeConnectAsync(
        int socksPort, string host, int port, CancellationToken outerCt,
        TimeSpan? attemptTimeout = null)
    {
        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        attemptCts.CancelAfter(attemptTimeout ?? TimeSpan.FromSeconds(7));
        var ct = attemptCts.Token;

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, socksPort, ct);
        await using var stream = client.GetStream();

        
        await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, ct);
        var methodResp = new byte[2];
        await ReadExactAsync(stream, methodResp, ct);
        if (methodResp[0] != 0x05 || methodResp[1] != 0x00) return false;

        
        
        byte[] req;
        if (IPAddress.TryParse(host, out var ip) &&
            ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            req = new byte[] { 0x05, 0x01, 0x00, 0x01, b[0], b[1], b[2], b[3],
                               (byte)(port >> 8), (byte)(port & 0xFF) };
        }
        else
        {
            var h = Encoding.ASCII.GetBytes(host);
            req = new byte[4 + 1 + h.Length + 2];
            req[0] = 0x05; req[1] = 0x01; req[2] = 0x00; req[3] = 0x03;
            req[4] = (byte)h.Length;
            Array.Copy(h, 0, req, 5, h.Length);
            req[5 + h.Length] = (byte)(port >> 8);
            req[6 + h.Length] = (byte)(port & 0xFF);
        }
        await stream.WriteAsync(req, ct);

        
        var reply = new byte[4];
        await ReadExactAsync(stream, reply, ct);
        if (reply[0] != 0x05 || reply[1] != 0x00) return false;
        await DrainBoundAddressAsync(stream, reply[3], ct);

        
        
        
        
        var head = Encoding.ASCII.GetBytes(
            $"HEAD / HTTP/1.1\r\nHost: {host}\r\nUser-Agent: Mozilla/5.0\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head, ct);
        var probe = new byte[1];
        return await stream.ReadAsync(probe, ct) > 0;
    }

        private static async Task DrainBoundAddressAsync(NetworkStream stream, byte atyp, CancellationToken ct)
    {
        var tail = atyp switch
        {
            0x01 => 6,   
            0x04 => 18,  
            _ => 0,
        };
        if (atyp == 0x03)
        {
            
            var len = new byte[1];
            await ReadExactAsync(stream, len, ct);
            tail = len[0] + 2;
        }
        if (tail > 0) await ReadExactAsync(stream, new byte[tail], ct);
    }

    private static async Task ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(offset), ct);
            if (n <= 0) throw new IOException("SOCKS peer closed the connection");
            offset += n;
        }
    }
}
