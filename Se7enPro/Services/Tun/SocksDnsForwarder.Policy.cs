using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Se7enPro.Services;

internal sealed partial class SocksDnsForwarder
{

    internal static byte[] BuildQuery(string name)
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
        w.Write((byte)0x00); w.Write((byte)0x01);
        w.Write((byte)0x00); w.Write((byte)0x01);
        w.Flush();
        return ms.ToArray();
    }

    public async Task<IPAddress[]?> ResolveForPolicyAsync(string name, CancellationToken ct)
    {
        var split = _split;
        if (split is null) return null;

        byte[] query;
        try { query = BuildQuery(name); }
        catch { return null; }
        if (ParseQuestion(query) is null) return null;

        byte[]? answer;
        IPAddress? seen = null;

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
                    seen = ExtractAnswerARecords(answer).FirstOrDefault();
                }
            }
            else
            {
                answer = await QueryUpstreamAsync(query, ct);
                if (answer is not null && !split.ExcludeMode)
                {
                    seen = ExtractAnswerARecords(answer).FirstOrDefault();
                }
            }
        }
        else
        {
            answer = await QueryUpstreamAsync(query, ct);
            if (answer is not null && !split.ExcludeMode)
            {
                seen = ExtractAnswerARecords(answer).FirstOrDefault();
            }
        }

        if (answer is null) return null;
        if (seen is not null)
        {
            try { split.AddressSeen?.Invoke(seen, name); } catch { }
        }
        return ExtractAnswerARecords(answer).ToArray();
    }
}
