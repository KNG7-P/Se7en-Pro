using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Se7enPro.Services;

internal static class SocksUdpProbe
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    public static async Task<bool?> SupportsUdpAssociateAsync(int socksPort, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProbeTimeout);
        var token = timeout.Token;

        using var client = new TcpClient(AddressFamily.InterNetwork);
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, socksPort, token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        catch (SocketException) { return null; }

        var stream = client.GetStream();

        try
        {

            await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, token);
            var hello = new byte[2];
            if (!await ReadExactlyAsync(stream, hello, token)) return null;
            if (hello[0] != 0x05 || hello[1] != 0x00) return null;

            var request = new byte[]
            {
                0x05, 0x03, 0x00, 0x01,
                0x00, 0x00, 0x00, 0x00,
                0x00, 0x00,
            };
            await stream.WriteAsync(request, token);

            var reply = new byte[4];
            if (!await ReadExactlyAsync(stream, reply, token))
            {

                return false;
            }
            if (reply[0] != 0x05) return null;
            return reply[1] == 0x00;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        catch (System.IO.IOException) { return false; }
        catch (SocketException) { return false; }
    }

    private static async Task<bool> ReadExactlyAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read, buffer.Length - read), ct);
            if (n <= 0) return false;
            read += n;
        }
        return true;
    }
}
