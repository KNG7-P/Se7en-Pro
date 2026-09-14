using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Se7enPro.Services;

internal sealed partial class SocksDnsForwarder
{

    private const ushort QTypeA = 1;
    private const ushort QTypeAaaa = 28;

    internal static byte[] BuildQuery(string name) => BuildQuery(name, QTypeA);

    internal static byte[] BuildQuery(string name, ushort qtype)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        var id = (ushort)Random.Shared.Next(1, short.MaxValue);
        w.Write((byte)(id >> 8)); w.Write((byte)(id & 0xFF));
        w.Write((byte)0x01); w.Write((byte)0x00);
        w.Write((byte)0x00); w.Write((byte)0x01);
        w.Write((byte)0x00); w.Write((byte)0x00);
        w.Write((byte)0x00); w.Write((byte)0x00);
        w.Write((byte)0x00); w.Write((byte)0x00);
        foreach (var label in name.Split('.'))
        {
            if (label.Length is 0 or > 63) throw new ArgumentException($"invalid label in '{name}'");
            w.Write((byte)label.Length);
            w.Write(Encoding.ASCII.GetBytes(label));
        }
        w.Write((byte)0x00);
        w.Write((byte)(qtype >> 8)); w.Write((byte)(qtype & 0xFF));
        w.Write((byte)0x00); w.Write((byte)0x01);
        w.Flush();
        return ms.ToArray();
    }

    public async Task<IPAddress[]?> ResolveForPolicyAsync(string name, CancellationToken ct)
    {
        var split = _split;
        if (split is null) return null;

        var found = new List<IPAddress>();

        var families = split.CanPinLocalV6 || !split.ExcludeMode
            ? new ushort[] { QTypeA, QTypeAaaa }
            : new ushort[] { QTypeA };

        foreach (var qtype in families)
        {
            if (ct.IsCancellationRequested) break;
            var addrs = await ResolveOneAsync(name, qtype, split, ct);
            if (addrs is null) continue;
            foreach (var a in addrs)
            {
                if (!found.Contains(a)) found.Add(a);
            }
        }

        return found.Count == 0 ? null : found.ToArray();
    }

    private async Task<List<IPAddress>?> ResolveOneAsync(
        string name, ushort qtype, SplitPolicy split, CancellationToken ct)
    {
        byte[] query;
        try { query = BuildQuery(name, qtype); }
        catch { return null; }
        if (ParseQuestion(query) is null) return null;

        byte[]? answer;
        var pin = false;

        if (split.LocalDnsIp is not null)
        {
            var matched = MatchDomain(name, split.Domains);
            var useLocal = split.ExcludeMode ? matched is not null : matched is null;
            if (useLocal)
            {
                answer = await QueryLocalAsync(query, ct);
                if (answer is null)
                {
                    Diag?.Invoke($"split dns: local resolver ({split.LocalDnsIp}) did not answer "
                                 + $"'{name}' (refresh); falling back to the tunnel path");
                    answer = await QueryUpstreamAsync(query, ct);
                }
                else
                {
                    pin = true;
                }
            }
            else
            {
                answer = await QueryUpstreamAsync(query, ct);
                pin = answer is not null && !split.ExcludeMode;
            }
        }
        else
        {
            answer = await QueryUpstreamAsync(query, ct);
            pin = answer is not null && !split.ExcludeMode;
        }

        if (answer is null) return null;

        var addrs = ExtractAnswerAddresses(answer);
        if (pin)
        {
            foreach (var a in addrs)
            {
                try { split.AddressSeen?.Invoke(a, name); } catch { }
            }
        }
        return addrs;
    }
}
