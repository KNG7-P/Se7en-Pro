using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace Se7enPro.Services;

/// <summary>
/// Supported DNS transport protocols.
/// </summary>
public enum DnsTransport
{
    /// <summary>Plain UDP to port 53.</summary>
    Udp,

    /// <summary>DNS over TLS, default port 853.</summary>
    Dot,

    /// <summary>DNS over HTTPS, default port 443.</summary>
    Doh,
}

public sealed record DnsServerEntry(
    DnsTransport Transport,
    string Raw,
    IPAddress? Address,
    string Host,
    int Port)
{
    /// <summary>Host:port as a SOCKS relay target needs it (IPv6 bracketed).</summary>
    public string TargetHostPort =>
        Address is not null && Address.AddressFamily == AddressFamily.InterNetworkV6
            ? $"[{Address}]:{Port}"
            : $"{Host}:{Port}";

    public override string ToString() => Raw;
}

/// <summary>Validation outcome for one entry: null problem means valid.</summary>
public static class DnsSettings
{
    public const int UdpPort = 53;
    public const int DotPort = 853;
    public const int DohPort = 443;

    /// <summary>Upper bound on entries per list. Guards against a pasted dump.</summary>
    public const int MaxEntriesPerList = 8;

    private static readonly string[] DotPrefixes = { "tls://", "dot://" };

    private static readonly string[] ForeignPrefixes = { "tls://", "dot://", "https://", "doh:", "doh://" };

    /// <summary>Splits raw DNS list text on standard delimiters.</summary>
    public static List<string> SplitList(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new List<string>();
        return raw.Split(new[] { ',', ';', ' ', '\t', '\r', '\n' },
                         StringSplitOptions.RemoveEmptyEntries)
                  .Select(s => s.Trim())
                  .Where(s => s.Length > 0)
                  .ToList();
    }

    /// <summary>
    /// Validates one entry for the given transport. Returns null when acceptable, or a
    /// short reason that names the entry so the UI can point at it.
    /// </summary>
    public static string? ValidateEntry(DnsTransport transport, string? entry)
    {
        var raw = (entry ?? "").Trim();
        if (raw.Length == 0) return "empty entry";

        switch (transport)
        {
            case DnsTransport.Dot:
            {
                var body = StripPrefix(raw, DotPrefixes);
                if (body.Length == raw.Length)
                {
                    return "DoT entries need the tls:// prefix, e.g. tls://dns.google";
                }
                var (host, port) = SplitHostPort(body, DotPort);
                if (!IsValidHost(host)) return $"\"{host}\" is not an IP or hostname";
                if (port is < 1 or > 65535) return $"port {port} is out of range";
                return null;
            }

            case DnsTransport.Doh:
            {
                string body;
                if (raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    body = raw;
                }
                else if (raw.StartsWith("doh://", StringComparison.OrdinalIgnoreCase))
                {
                    body = "https://" + raw[5..];
                }
                else if (raw.StartsWith("doh:", StringComparison.OrdinalIgnoreCase))
                {
                    body = "https://" + raw[4..];
                }
                else
                {
                    return "DoH entries need https:// or the doh: prefix, "
                         + "e.g. https://cloudflare-dns.com/dns-query";
                }

                if (!Uri.TryCreate(body, UriKind.Absolute, out var uri))
                {
                    return "that is not a usable DoH URL";
                }
                if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                {
                    return "DoH requires https://";
                }
                var host = uri.DnsSafeHost;
                if (!IsValidHost(host)) return $"\"{host}\" is not an IP or hostname";
                if (uri.Port is < 1 or > 65535) return $"port {uri.Port} is out of range";
                return null;
            }

            default:
            {
                foreach (var p in ForeignPrefixes)
                {
                    if (raw.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                    {
                        var owner = p is "tls://" or "dot://" ? "DoT" : "DoH";
                        return $"this is a {p} entry — it belongs in the {owner} list";
                    }
                }
                var (host, port) = SplitHostPort(raw, UdpPort);
                if (!IsValidHost(host)) return $"\"{host}\" is not an IP or hostname";
                if (port is < 1 or > 65535) return $"port {port} is out of range";
                return null;
            }
        }
    }

    /// <summary>
    /// Validates and converts one entry. Returns null when the entry is unusable, after
    /// recording the reason in <paramref name="problem"/>.
    /// </summary>
    public static DnsServerEntry? ParseEntry(DnsTransport transport, string entry, out string? problem)
    {
        problem = ValidateEntry(transport, entry);
        if (problem is not null) return null;

        var raw = entry.Trim();

        if (transport == DnsTransport.Doh)
        {
            var body = raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? raw
                     : raw.StartsWith("doh://", StringComparison.OrdinalIgnoreCase) ? "https://" + raw[5..]
                     : raw.StartsWith("doh:", StringComparison.OrdinalIgnoreCase) ? "https://" + raw[4..]
                     : raw;
            if (!Uri.TryCreate(body, UriKind.Absolute, out var uri))
            {
                problem = "that is not a usable DoH URL";
                return null;
            }
            var host = uri.DnsSafeHost;
            var port = uri.Port > 0 ? uri.Port : DohPort;
            IPAddress? addr = IPAddress.TryParse(host, out var a) ? a : null;
            return new DnsServerEntry(transport, raw, addr, host, port);
        }

        var (h, p) = SplitHostPort(
            StripPrefix(raw, transport == DnsTransport.Dot ? DotPrefixes : Array.Empty<string>()),
            transport == DnsTransport.Dot ? DotPort : UdpPort);
        IPAddress? parsed = IPAddress.TryParse(h, out var ip) ? ip : null;
        return new DnsServerEntry(transport, raw, parsed, h, p);
    }

    /// <summary>
    /// Parses a whole list. Entries that fail are skipped and reported, so a single typo
    /// does not discard the rest of the list.
    /// </summary>
    public static List<DnsServerEntry> ParseList(
        DnsTransport transport,
        string? raw,
        out List<string> problems)
    {
        problems = new List<string>();
        var result = new List<DnsServerEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var token in SplitList(raw))
        {
            if (result.Count >= MaxEntriesPerList)
            {
                problems.Add($"only the first {MaxEntriesPerList} entries are used");
                break;
            }

            var entry = ParseEntry(transport, token, out var problem);
            if (problem is not null)
            {
                problems.Add($"{token}: {problem}");
                continue;
            }
            if (entry is null) continue;

            var key = entry.Address is { } literal
                ? $"{entry.Transport}|{literal}|{entry.Port}"
                : $"{entry.Transport}|{entry.Host}|{entry.Port}";
            if (!seen.Add(key)) continue;

            result.Add(entry);
        }

        return result;
    }

    /// <summary>Resolves hostnames to IP addresses for relay targets.</summary>
    public static List<DnsServerEntry> ResolveForRelay(List<DnsServerEntry> entries)
    {
        var result = new List<DnsServerEntry>();
        foreach (var e in entries)
        {
            if (e.Address is not null || e.Host.Length == 0) { result.Add(e); continue; }
            try
            {
                var resolved = Dns.GetHostAddresses(e.Host)
                    .FirstOrDefault(a => a.AddressFamily is AddressFamily.InterNetwork
                                             or AddressFamily.InterNetworkV6);
                if (resolved is not null)
                {
                    result.Add(e with { Address = resolved });
                    continue;
                }
            }
            catch { }
            result.Add(e);
        }
        return result;
    }

    /// <summary>A compact summary for the settings row, e.g. "UDP 2 · DoH 1".</summary>
    public static string Summarise(string? udp, string? dot, string? doh)
    {
        var u = SplitList(udp).Count;
        var t = SplitList(dot).Count;
        var h = SplitList(doh).Count;
        if (u == 0 && t == 0 && h == 0) return "";

        var parts = new List<string>(3);
        if (u > 0) parts.Add($"{u} UDP");
        if (t > 0) parts.Add($"{t} DoT");
        if (h > 0) parts.Add($"{h} DoH");
        return string.Join(" · ", parts);
    }

    /// <summary>Separator used by <see cref="Summarise"/>.</summary>
    internal const string SummarySeparator = " · ";

    // ---------------------------------------------------------------- internals

    private static string StripPrefix(string raw, string[] prefixes)
    {
        foreach (var p in prefixes)
        {
            if (raw.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return raw[p.Length..].Trim();
        }
        return raw.Trim();
    }

    /// <summary>Splits an entry into host and port, supporting IPv6 bracketed addresses.</summary>
    internal static (string Host, int Port) SplitHostPort(string entry, int defaultPort)
    {
        var e = entry.Trim();
        if (e.Length == 0) return (e, defaultPort);

        if (e[0] == '[')
        {
            var close = e.IndexOf(']');
            if (close <= 0) return (e[1..], defaultPort);
            var host = e[1..close];
            var rest = e[(close + 1)..].TrimStart(':');
            return int.TryParse(rest, NumberStyles.Integer, CultureInfo.InvariantCulture, out var vp) && vp > 0
                ? (host, vp)
                : (host, defaultPort);
        }

        if (e.Count(c => c == ':') > 1)
        {
            return (IPAddress.TryParse(e, out var v6) ? v6.ToString() : e, defaultPort);
        }

        var colon = e.LastIndexOf(':');
        if (colon <= 0) return (e, defaultPort);

        var tail = e[(colon + 1)..];
        if (!int.TryParse(tail, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) || port <= 0)
            return (e, defaultPort);

        if (e[..colon].All(c => char.IsAsciiDigit(c) || c == '.') &&
            !IPAddress.TryParse(e[..colon], out _))
        {
            return (e, defaultPort);
        }

        return (e[..colon], port);
    }

    /// <summary>Validates whether a string is a valid IP or hostname.</summary>
    internal static bool IsValidHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        if (IPAddress.TryParse(host, out _)) return true;
        if (!host.Contains('.')) return false;
        return IsValidHostname(host);
    }

    private static bool IsValidHostname(string value)
    {
        if (value.Length is 0 or > 253) return false;
        if (value.StartsWith('.') || value.EndsWith('.')) return false;

        foreach (var label in value.Split('.'))
        {
            if (label.Length is 0 or > 63) return false;
            if (label.StartsWith('-') || label.EndsWith('-')) return false;
            var sawDigit = false;
            var sawAlpha = false;
            foreach (var c in label)
            {
                if (char.IsAsciiDigit(c)) { sawDigit = true; continue; }
                if (char.IsAsciiLetter(c)) { sawAlpha = true; continue; }
                if (c == '-') continue;
                return false;
            }
            if (sawDigit && !sawAlpha) return false;
        }
        return true;
    }
}