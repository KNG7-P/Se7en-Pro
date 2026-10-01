using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace TunEngineTest;

public sealed partial class MockSocksServer
{
    private static async Task HandleDnsAsync(NetworkStream stream)
    {
        var lenBuf = new byte[2];
        await ReadExactlyAsync(stream, lenBuf);
        var len = (lenBuf[0] << 8) | lenBuf[1];
        if (len is < 17 or > 900) return;

        var query = new byte[len];
        await ReadExactlyAsync(stream, query);

        var response = BuildDnsResponse(query, new byte[] { 203, 0, 113, 99 });

        await stream.WriteAsync(new byte[] { (byte)(response.Length >> 8), (byte)(response.Length & 0xFF) });
        await stream.WriteAsync(response);
    }

        public static byte[] BuildDnsResponse(byte[] query, byte[] addr)
    {
        var len = query.Length;
        var response = new byte[len + 16];
        Buffer.BlockCopy(query, 0, response, 0, len);
        response[2] = 0x81; response[3] = 0x80;
        response[6] = 0x00; response[7] = 0x01;
        var o = len; 
        response[o + 0] = 0xC0; response[o + 1] = 0x0C;
        response[o + 2] = 0x00; response[o + 3] = 0x01;
        response[o + 4] = 0x00; response[o + 5] = 0x01;
        response[o + 6] = 0x00; response[o + 7] = 0x00;
        response[o + 8] = 0x00; response[o + 9] = 0x3C;
        response[o + 10] = 0x00; response[o + 11] = 0x04;
        response[o + 12] = addr[0]; response[o + 13] = addr[1];
        response[o + 14] = addr[2]; response[o + 15] = addr[3];
        return response;
    }

    private static async Task RelayAsync(NetworkStream a, NetworkStream b)
    {
        var t1 = PumpAsync(a, b);
        var t2 = PumpAsync(b, a);
        await Task.WhenAny(t1, t2);
    }

    private static async Task PumpAsync(NetworkStream from, NetworkStream to)
    {
        var buf = new byte[16384];
        int n;
        while ((n = await from.ReadAsync(buf)) > 0)
        {
            await to.WriteAsync(buf.AsMemory(0, n));
        }
    }

    private static async Task ReadExactlyAsync(NetworkStream s, byte[] buf)
    {
        var off = 0;
        while (off < buf.Length)
        {
            var n = await s.ReadAsync(buf.AsMemory(off));
            if (n <= 0) throw new IOException("eof");
            off += n;
        }
    }
}
