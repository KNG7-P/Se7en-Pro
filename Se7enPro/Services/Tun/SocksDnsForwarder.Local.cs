using System;
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
                udp.Client.SendTimeout = 1500;
                udp.Client.ReceiveTimeout = 1500;
                udp.Connect(localIp, 53);
                await udp.SendAsync(query, query.Length);

                var receiveTask = udp.ReceiveAsync(ct).AsTask();
                var finished = await Task.WhenAny(receiveTask, Task.Delay(1500, ct));
                if (finished != receiveTask) continue;

                var response = (await receiveTask).Buffer;
                if (!IsMatchingResponse(query, response)) continue;
                if (IsSinkholedAnswer(response)) continue;
                return response;
            }
            catch (OperationCanceledException) { return null; }
            catch { }
        }
        return null;
    }

    internal static bool IsMatchingResponse(byte[] query, byte[] response)
    {
        if (query.Length < 12 || response.Length < 12) return false;
        if (query[0] != response[0] || query[1] != response[1]) return false;
        if ((response[2] & 0x80) == 0) return false;

        var qdCount = (query[4] << 8) | query[5];
        if (qdCount != 1) return false;

        var qEnd = QuestionEnd(query);
        if (qEnd <= 12 || qEnd > query.Length || response.Length < qEnd) return false;

        return query.AsSpan(12, qEnd - 12).SequenceEqual(response.AsSpan(12, qEnd - 12));
    }

    private static int QuestionEnd(byte[] packet)
    {
        var i = 12;
        while (i < packet.Length)
        {
            var len = packet[i];
            if (len == 0) return i + 5 <= packet.Length ? i + 5 : -1;
            if ((len & 0xC0) == 0xC0) return i + 6 <= packet.Length ? i + 6 : -1;
            i += 1 + len;
        }
        return -1;
    }

    internal static List<IPAddress> ExtractAnswerARecords(byte[] r) =>
        ExtractAnswerAddresses(r).Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToList();

    private static List<IPAddress> ExtractAnswerAddresses(byte[] r)
    {
        var result = new List<IPAddress>();
        if (r.Length < 12) return result;

        var ancount = (r[6] << 8) | r[7];
        var i = QuestionEnd(r);
        if (i < 12) return result;                                          

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
            else if (type == 28 && rdlen == 16)
            {
                var v6 = new byte[16];
                Buffer.BlockCopy(r, i, v6, 0, 16);
                result.Add(new IPAddress(v6));
            }
            i += rdlen;
        }
        return result;
    }

    private static readonly IPAddress MsftNcsiV6 = IPAddress.Parse("fd3e:4f5a:5b81::1");

    internal static bool IsSinkholedAnswer(byte[] answer)
    {
        var addresses = ExtractAnswerAddresses(answer);
        if (addresses.Count == 0) return false;
        var isMsftNcsi = TryReadQuestionName(answer, out var qname)
                         && string.Equals(qname, "dns.msftncsi.com", StringComparison.OrdinalIgnoreCase);
        return addresses.All(ip => IsNullRoute(ip) && !(isMsftNcsi && ip.Equals(MsftNcsiV6)));
    }

    private static bool TryReadQuestionName(byte[] packet, out string qname)
    {
        qname = "";
        if (packet is null || packet.Length < 14) return false;
        var sb = new System.Text.StringBuilder();
        var offset = 12;
        while (offset < packet.Length)
        {
            var len = packet[offset++];
            if (len == 0)
            {
                qname = sb.ToString();
                return qname.Length > 0;
            }
            if ((len & 0xC0) != 0 || offset + len > packet.Length || sb.Length + len + 1 > 255)
                return false;
            if (sb.Length > 0) sb.Append('.');
            sb.Append(System.Text.Encoding.ASCII.GetString(packet, offset, len));
            offset += len;
        }
        return false;
    }

    private static bool IsNullRoute(IPAddress ip)
    {
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            if (b[0] is 0 or 10 or 127 || ip.Equals(IPAddress.Broadcast)) return true;
            if (b[0] == 172 && b[1] is >= 16 and <= 31) return true;
            if (b[0] == 192 && b[1] == 168) return true;
            if (b[0] == 169 && b[1] == 254) return true;
            if (b is [37, 156, 14, 11] or [217, 218, 127, 127]) return true;
            return false;
        }

        if (ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.IPv6Loopback)) return true;
        return (ip.GetAddressBytes()[0] & 0xFE) == 0xFC;
    }

    internal static string DescribeAnswerAddresses(byte[] answer) =>
        string.Join(", ", ExtractAnswerAddresses(answer).Select(a => a.ToString()).Distinct());

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

    internal static byte[]? BuildServFailResponse(byte[] query)
    {
        if (query is null || query.Length < 12) return null;
        var qEnd = QuestionEnd(query);
        var len = qEnd >= 12 && qEnd <= query.Length ? qEnd : 12;
        var resp = new byte[len];
        Buffer.BlockCopy(query, 0, resp, 0, len);
        resp[2] |= 0x80;
        resp[3] = (byte)((resp[3] & 0xF0) | 0x82);
        if (len == 12)
        {
            resp[4] = 0; resp[5] = 0;
        }
        resp[6] = 0; resp[7] = 0;
        resp[8] = 0; resp[9] = 0;
        resp[10] = 0; resp[11] = 0;
        return resp;
    }
}
