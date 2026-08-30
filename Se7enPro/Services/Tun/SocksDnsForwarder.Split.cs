﻿using System;
using System.Collections.Generic;
using System.Net;

namespace Se7enPro.Services;

internal sealed partial class SocksDnsForwarder
{

    public sealed class SplitPolicy
    {

        public bool ExcludeMode = true;

        public IReadOnlyList<string> Domains = Array.Empty<string>();

        public string? LocalDnsIp;

        public Action<IPAddress, string>? AddressSeen;
    }

    private volatile SplitPolicy? _split;

    public void UpdateSplitPolicy(SplitPolicy? policy) => _split = policy;

    internal static string? MatchDomain(string name, IReadOnlyList<string> domains)
    {
        foreach (var d in domains)
        {
            if (d.Length == 0) continue;
            if (string.Equals(name, d, StringComparison.OrdinalIgnoreCase)) return d;
            if (name.Length > d.Length + 1
                && name.EndsWith("." + d, StringComparison.OrdinalIgnoreCase))
            {
                return d;
            }
        }
        return null;
    }

    internal static (string Name, ushort Type, int QuestionLength)? ParseQuestion(byte[] q)
    {
        if (q.Length < 17) return null;
        var i = 12;
        var sb = new System.Text.StringBuilder();
        while (i < q.Length)
        {
            var len = q[i];
            if (len == 0)
            {
                i += 1;
                if (i + 4 > q.Length) return null;
                var type = (ushort)((q[i] << 8) | q[i + 1]);
                return (sb.ToString(), type, i + 4);
            }
            if ((len & 0xC0) != 0) return null;
            if (i + 1 + len > q.Length) return null;
            if (sb.Length > 0) sb.Append('.');
            sb.Append(System.Text.Encoding.ASCII.GetString(q, i + 1, len));
            i += 1 + len;
        }
        return null;
    }
}
