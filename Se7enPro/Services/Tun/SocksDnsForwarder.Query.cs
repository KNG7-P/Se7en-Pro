using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Se7enPro.Services;

internal sealed partial class SocksDnsForwarder
{
        /// <summary>
        /// Relays the query to configured resolvers sequentially until an answer is received.
        /// </summary>
        private async Task<byte[]?> QueryUpstreamAsync(byte[] query, CancellationToken outerCt)
        {
            if (_relayTargets.Count == 0) return null;

            string? lastDetail = null;

            for (var i = 0; i < _relayTargets.Count; i++)
            {
                var target = _relayTargets[i];

                using var linked = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
                linked.CancelAfter(_queryTimeout);
                var ct = linked.Token;

                try
                {
                    var answer = await QueryOneAsync(target, query, ct);
                    if (answer is not null) return answer;
                    lastDetail = "no answer";
                }
                catch (OperationCanceledException)
                {
                    if (outerCt.IsCancellationRequested) return null;
                    lastDetail = "timed out";
                }
                catch (Exception ex)
                {
                    lastDetail = ex.GetType().Name;
                }

                Diag?.Invoke($"dns: resolver {i + 1}/{_relayTargets.Count} "
                             + $"({target.TargetHostPort}) did not answer"
                             + (lastDetail is null ? "" : $" - {lastDetail}")
                             + (i + 1 < _relayTargets.Count ? "; trying the next one" : ""));
            }

            return null;
        }

        private async Task<byte[]?> QueryOneAsync(DnsServerEntry target, byte[] query, CancellationToken ct)
        {
            using var tcp = new TcpClient();
            var connectTask = tcp.ConnectAsync(IPAddress.Loopback, _socksPort, ct).AsTask();
            var finished = await Task.WhenAny(connectTask, Task.Delay(_connectTimeout, ct));
            if (finished != connectTask) return null;
            await connectTask;

            await using var stream = tcp.GetStream();

            await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, ct);
            var greeting = new byte[2];
            await ReadExactlyAsync(stream, greeting, ct);
            if (greeting[0] != 0x05 || greeting[1] != 0x00) return null;

            // Prefer a literal address; fall back to a SOCKS domain-name target (ATYP 3)
            // when the resolver was configured as a hostname.
            byte[] connectReq;
            if (target.Address is { } literal)
            {
                var addr = literal.GetAddressBytes();
                // Layout: VER CMD RSV ATYP, then the address, then a 2-byte port.
                connectReq = new byte[4 + addr.Length + 2];
                connectReq[0] = 0x05;
                connectReq[1] = 0x01;
                connectReq[3] = 0x01;
                Buffer.BlockCopy(addr, 0, connectReq, 4, addr.Length);
                connectReq[4 + addr.Length] = (byte)(target.Port >> 8);   // big endian
                connectReq[4 + addr.Length + 1] = (byte)(target.Port & 0xFF);
            }
            else
            {
                var host = Encoding.ASCII.GetBytes(target.Host);
                connectReq = new byte[7 + host.Length];
                connectReq[0] = 0x05;
                connectReq[1] = 0x01;
                connectReq[3] = 0x03;
                connectReq[4] = (byte)host.Length;
                Buffer.BlockCopy(host, 0, connectReq, 5, host.Length);
                connectReq[5 + host.Length] = (byte)(target.Port >> 8);
                connectReq[6 + host.Length] = (byte)(target.Port & 0xFF);
            }
            await stream.WriteAsync(connectReq, ct);

            var header = new byte[4];
            await ReadExactlyAsync(stream, header, ct);
            if (header[1] != 0x00) return null;

            var extra = header[3] switch
            {
                0x01 => 4 + 2,
                0x03 => -1,
                0x04 => 16 + 2,
                _ => 0,
            };
            if (extra < 0)
            {
                var lenByte = new byte[1];
                await ReadExactlyAsync(stream, lenByte, ct);
                extra = 1 + lenByte[0] + 2;
            }
            if (extra > 0)
            {
                var rest = new byte[extra];
                await ReadExactlyAsync(stream, rest, ct);
            }

            var framed = new byte[2 + query.Length];
            framed[0] = (byte)(query.Length >> 8);
            framed[1] = (byte)(query.Length & 0xFF);
            Buffer.BlockCopy(query, 0, framed, 2, query.Length);
            await stream.WriteAsync(framed, ct);

            var len = new byte[2];
            await ReadExactlyAsync(stream, len, ct);
            var answerLen = (len[0] << 8) | len[1];

            if (answerLen is <= 0 or > 65535) return null;

            var answer = new byte[answerLen];
            await ReadExactlyAsync(stream, answer, ct);

            if (query.Length >= 2 && answerLen >= 3)
            {
                if (answer[0] != query[0] || answer[1] != query[1]) return null;
                if ((answer[2] & 0x80) == 0) return null;
            }

            return answer;
        }

    private static async Task ReadExactlyAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(offset), ct);
            if (n <= 0) throw new IOException("DNS relay closed the connection");
            offset += n;
        }
    }
}
