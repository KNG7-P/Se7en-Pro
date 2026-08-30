using System;
using System.Collections.Generic;
using System.Net;
using Se7enPro.Models;

namespace Se7enPro.Services;

public static class SplitRules
{

    public static string? NormalizeSplitDomain(string raw)
    {
        var s = (raw ?? "").Trim();
        if (s.Length == 0) return null;

        var scheme = s.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) s = s[(scheme + 3)..];

        var at = s.IndexOf('@');
        if (at >= 0) s = s[(at + 1)..];

        var cut = s.IndexOfAny(new[] { '/', '\\', '?', '#' });
        if (cut >= 0) s = s[..cut];

        if (!s.Contains('[') && s.IndexOf(':') is var c && c > 0)
            s = s[..c];

        s = s.Trim().TrimStart('*').TrimStart('.').Trim().ToLowerInvariant();
        if (s.Length == 0 || s.Contains(' ')) return null;
        return s;
    }

    public static string? NormalizeSplitIpCidr(string raw)
    {
        var s = (raw ?? "").Trim();
        if (s.Length == 0) return null;

        var slash = s.IndexOf('/');
        var addrPart = slash >= 0 ? s[..slash].Trim() : s;
        var prefixPart = slash >= 0 ? s[(slash + 1)..].Trim() : null;

        if (!IPAddress.TryParse(addrPart, out var ip)) return null;

        var maxPrefix = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32;
        int prefix;
        if (prefixPart is null)
        {
            prefix = maxPrefix;
        }
        else if (!int.TryParse(prefixPart, out prefix) || prefix < 0 || prefix > maxPrefix)
        {
            return null;
        }

        return $"{ip}/{prefix}";
    }

    public static bool LooksLikeAppPath(string s) =>
        s.Contains('\\') || s.Contains('/') || (s.Length >= 2 && s[1] == ':');

    public static string NormalizeProcessName(string raw)
    {
        var s = (raw ?? "").Trim();
        if (!s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s += ".exe";
        return s;
    }

    public static void ClassifySplitEntries(
        UserSettings? settings,
        out List<string> domains,
        out List<string> ipCidrs,
        out List<string> procNames,
        out List<string> procPaths)
    {
        domains = new List<string>();
        ipCidrs = new List<string>();
        procNames = new List<string>();
        procPaths = new List<string>();

        var entries = settings?.SplitTunnelEntries;
        if (entries is null) return;

        foreach (var e in entries)
        {
            if (e is null) continue;
            var raw = (e.Value ?? "").Trim();
            if (raw.Length == 0) continue;

            switch ((e.Kind ?? "").Trim().ToLowerInvariant())
            {
                case "ip":
                    var cidr = NormalizeSplitIpCidr(raw);
                    if (cidr is not null && !ipCidrs.Contains(cidr)) ipCidrs.Add(cidr);
                    break;

                case "app":
                    if (LooksLikeAppPath(raw))
                    {
                        if (!procPaths.Contains(raw)) procPaths.Add(raw);
                    }
                    else
                    {
                        var name = NormalizeProcessName(raw);
                        if (!procNames.Contains(name)) procNames.Add(name);
                    }
                    break;

                default:
                    var d = NormalizeSplitDomain(raw);
                    if (d is not null && !domains.Contains(d)) domains.Add(d);
                    break;
            }
        }
    }

    public static (IPAddress Addr, byte Prefix)? ParseIpCidr(string cidr)
    {
        var slash = cidr.IndexOf('/');
        var addrPart = slash >= 0 ? cidr[..slash] : cidr;
        var prefixPart = slash >= 0 ? cidr[(slash + 1)..] : null;

        if (!IPAddress.TryParse(addrPart, out var ip)) return null;
        var max = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32;
        if (prefixPart is null) return (ip, (byte)max);
        if (!byte.TryParse(prefixPart, out var prefix) || prefix > max) return null;
        return (ip, prefix);
    }
}
