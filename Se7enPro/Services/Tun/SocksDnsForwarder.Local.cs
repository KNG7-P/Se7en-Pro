﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Se7enPro.Services;

internal sealed partial class SocksDnsForwarder
{

    private async Task<byte[]?> QueryLocalAsync(byte[] query, CancellationToken ct)
    {
        var localIp = IPAddress.Parse(_split!.LocalDnsIp!);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var udp = new UdpClient();
                udp.Client.SendTimeout = 3000;
                udp.Client.ReceiveTimeout = 3000;
                await udp.SendAsync(query, query.Length, new IPEndPoint(localIp, 53));

                var receiveTask = udp.ReceiveAsync(ct).AsTask();
                var finished = await Task.WhenAny(receiveTask, Task.Delay(3000, ct));
                if (finished != receiveTask) continue;
                return (await receiveTask).Buffer;
            }
            catch (OperationCanceledException) { return null; }
            catch { }
        }
        return null;
    }

    internal static List<IPAddress> ExtractAnswerARecords(byte[] r)
    {
        var result = new List<IPAddress>();
        if (r.Length < 12) return result;

        var ancount = (r[6] << 8) | r[7];
        var i = 12;
        while (i < r.Length && r[i] != 0) i += 1 + r[i];
        i += 5;

        for (var n = 0; n < ancount && i + 10 <= r.Length; n++)
        {
            while (i < r.Length)
            {
                var len = r[i];
                if (len == 0) { i += 1; break; }
                if ((len & 0xC0) == 0xC0) { i += 2; break; }
                i += 1 + len;
            }
            if (i + 10 > r.Length) break;

            var type = (r[i] << 8) | r[i + 1];
            var rdlen = (r[i + 8] << 8) | r[i + 9];
            i += 10;
            if (i + rdlen > r.Length) break;

            if (type == 1 && rdlen == 4)
            {
                result.Add(new IPAddress(new[] { r[i], r[i + 1], r[i + 2], r[i + 3] }));
            }
            i += rdlen;
        }
        return result;
    }

    internal static byte[] BuildEmptyResponse(byte[] query, int questionLength)
    {
        var resp = new byte[questionLength];
        Buffer.BlockCopy(query, 0, resp, 0, questionLength);
        resp[2] |= 0x80;
        resp[3] |= 0x80;
        resp[3] = (byte)(resp[3] & 0xF0);
        resp[6] = 0; resp[7] = 0;
        resp[8] = 0; resp[9] = 0;
        resp[10] = 0; resp[11] = 0;
        return resp;
    }
}
