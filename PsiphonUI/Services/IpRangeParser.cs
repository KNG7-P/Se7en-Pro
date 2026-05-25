using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;

namespace PsiphonUI.Services;

public static class IpRangeParser
{
    public const int MaxEntries = 5_000_000;

    public const int CidrMaxHosts = 16_777_216;

    private static readonly Regex IPv4LiteralRegex = new(
        @"(?<![\d.])(?:(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)\.){3}(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)(?![\d.])",
        RegexOptions.Compiled);

    private static readonly Regex RangeRegex = new(
        @"^\s*(\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3})\s*-\s*(\d{1,3}(?:\.\d{1,3}\.\d{1,3}\.\d{1,3})?)\s*$",
        RegexOptions.Compiled);

    public static List<string> Expand(string? input)
    {
        var r = ExpandWithDiagnostics(input);
        return new List<string>(r.Ips);
    }

    public static ExpansionResult ExpandWithDiagnostics(string? input)
    {
        var ips = new List<string>();
        var warnings = new List<string>();
        if (string.IsNullOrWhiteSpace(input))
            return new ExpansionResult(ips, warnings, HitCap: false);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        bool hitCap = false;
        foreach (var rawLine in input.Split('\n'))
        {
            var line = StripComment(rawLine).Trim();
            if (line.Length == 0) continue;

            foreach (var token in line.Split(
                new[] { ' ', '\t', ',', ';' },
                StringSplitOptions.RemoveEmptyEntries))
            {
                if (ips.Count >= MaxEntries)
                {
                    hitCap = true;
                    return new ExpansionResult(ips, warnings, hitCap);
                }
                ExpandToken(token, ips, seen, warnings);
                if (ips.Count >= MaxEntries)
                {
                    hitCap = true;
                    return new ExpansionResult(ips, warnings, hitCap);
                }
            }
        }
        return new ExpansionResult(ips, warnings, hitCap);
    }

    public sealed record ExpansionResult(
    IReadOnlyList<string> Ips,
    IReadOnlyList<string> Warnings,
    bool HitCap);

    private static string StripComment(string line)
    {
        var idx = line.IndexOf('#');
        if (idx >= 0) line = line[..idx];

        idx = FindLineCommentStart(line);
        if (idx >= 0) line = line[..idx];

        return line;
    }

    private static int FindLineCommentStart(string line)
    {
        var idx = line.IndexOf("//", StringComparison.Ordinal);
        while (idx >= 0)
        {
            if (idx == 0 || char.IsWhiteSpace(line[idx - 1]))
            {
                return idx;
            }

            idx = line.IndexOf("//", idx + 2, StringComparison.Ordinal);
        }

        return -1;
    }

    private static void ExpandToken(
        string token, List<string> result, HashSet<string> seen, List<string> warnings)
    {

        var slashIdx = token.IndexOf('/');
        if (slashIdx > 0)
        {
            ExpandCidr(token, slashIdx, result, seen, warnings);
            return;
        }

        var m = RangeRegex.Match(token);
        if (m.Success)
        {
            ExpandDashRange(m.Groups[1].Value, m.Groups[2].Value, result, seen, warnings);
            return;
        }

        if (TryParseIPv4(token, out var ip))
        {
            AddUnique(result, seen, ip.ToString());
            return;
        }

        var extracted = ExtractIPv4Literals(token);
        if (extracted.Count > 0)
        {
            foreach (var literal in extracted)
            {
                AddUnique(result, seen, literal);
                if (result.Count >= MaxEntries) break;
            }
            return;
        }

        warnings.Add($"'{token}': not a valid IPv4 / CIDR / dash range");
    }

    private static IReadOnlyList<string> ExtractIPv4Literals(string token)
    {
        var matches = IPv4LiteralRegex.Matches(token);
        if (matches.Count == 0) return Array.Empty<string>();

        var result = new List<string>(matches.Count);
        foreach (Match match in matches)
        {
            if (TryParseIPv4(match.Value, out var ip))
            {
                result.Add(ip.ToString());
            }
        }

        return result;
    }

    private static void ExpandCidr(
        string token, int slashIdx, List<string> result, HashSet<string> seen, List<string> warnings)
    {
        var ipPart = token[..slashIdx];
        var prefixPart = token[(slashIdx + 1)..];

        if (!TryParseIPv4(ipPart, out _))
        {
            warnings.Add($"'{token}': base address is not a valid IPv4");
            return;
        }
        if (!int.TryParse(prefixPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var prefix))
        {
            warnings.Add($"'{token}': prefix is not an integer");
            return;
        }
        if (prefix is < 0 or > 32)
        {
            warnings.Add($"'{token}': prefix must be 0-32");
            return;
        }

        var ipBytes = IPAddress.Parse(ipPart).GetAddressBytes();
        var baseAddr = ((uint)ipBytes[0] << 24) | ((uint)ipBytes[1] << 16) |
                       ((uint)ipBytes[2] << 8) | (uint)ipBytes[3];

        var hostBits = 32 - prefix;
        var size = hostBits == 32 ? 1UL << 32 : 1UL << hostBits;
        if (size > CidrMaxHosts)
        {
            warnings.Add($"'{token}': /{prefix} has {size:N0} hosts, exceeds the per-range cap of {CidrMaxHosts:N0}");
            return;
        }

        var mask = hostBits == 32 ? 0u : ~((1u << hostBits) - 1u);
        baseAddr &= mask;

        for (ulong i = 0; i < size && result.Count < MaxEntries; i++)
        {
            var addr = baseAddr + (uint)i;
            AddUnique(result, seen, FormatIPv4(addr));
        }
    }

    private static void ExpandDashRange(
        string startStr, string endStr, List<string> result, HashSet<string> seen, List<string> warnings)
    {
        if (!TryParseIPv4(startStr, out _))
        {
            warnings.Add($"'{startStr}-{endStr}': start is not a valid IPv4");
            return;
        }

        uint startAddr = IPv4ToUInt(startStr);
        uint endAddr;

        if (endStr.Contains('.'))
        {
            if (!TryParseIPv4(endStr, out _))
            {
                warnings.Add($"'{startStr}-{endStr}': end is not a valid IPv4");
                return;
            }
            endAddr = IPv4ToUInt(endStr);
        }
        else
        {

            if (!int.TryParse(endStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var lastOctet))
            {
                warnings.Add($"'{startStr}-{endStr}': end is not a valid IPv4 or last octet");
                return;
            }
            if (lastOctet is < 0 or > 255)
            {
                warnings.Add($"'{startStr}-{endStr}': last octet must be 0-255");
                return;
            }
            endAddr = (startAddr & 0xFFFFFF00u) | (uint)lastOctet;
        }

        if (endAddr < startAddr)
        {
            warnings.Add($"'{startStr}-{endStr}': end < start");
            return;
        }
        if (endAddr - startAddr + 1 > CidrMaxHosts)
        {
            warnings.Add($"'{startStr}-{endStr}': {endAddr - startAddr + 1:N0} hosts, exceeds per-range cap of {CidrMaxHosts:N0}");
            return;
        }

        for (uint addr = startAddr; addr <= endAddr && result.Count < MaxEntries; addr++)
        {
            AddUnique(result, seen, FormatIPv4(addr));
            if (addr == uint.MaxValue) break;
        }
    }

    private static void AddUnique(List<string> result, HashSet<string> seen, string ip)
    {
        if (seen.Add(ip)) result.Add(ip);
    }

    public static bool TryParseIPv4(string s, out IPAddress addr)
    {
        addr = IPAddress.None;
        if (string.IsNullOrEmpty(s)) return false;
        var parts = s.Split('.');
        if (parts.Length != 4) return false;

        Span<byte> bytes = stackalloc byte[4];
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part.Length is < 1 or > 3) return false;
            foreach (var c in part)
            {
                if (c < '0' || c > '9') return false;
            }

            if (!byte.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out bytes[i]))
            {
                return false;
            }
        }

        addr = new IPAddress(bytes);
        return true;
    }

    private static uint IPv4ToUInt(string ip)
    {
        var bytes = IPAddress.Parse(ip).GetAddressBytes();
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) |
               ((uint)bytes[2] << 8) | (uint)bytes[3];
    }

    private static string FormatIPv4(uint addr) =>
        string.Create(CultureInfo.InvariantCulture, $"{(byte)(addr >> 24)}.{(byte)(addr >> 16)}.{(byte)(addr >> 8)}.{(byte)addr}");

    public readonly record struct CidrSegment(uint BaseAddr, uint Count);

    public static List<CidrSegment> ParseSegments(IEnumerable<string> cidrs, out ulong totalHosts)
    {
        var segs = new List<CidrSegment>();
        ulong total = 0;
        foreach (var rawCidr in cidrs)
        {
            var cidr = rawCidr?.Trim();
            if (string.IsNullOrEmpty(cidr)) continue;
            var slashIdx = cidr.IndexOf('/');
            if (slashIdx <= 0) continue;
            var ipPart = cidr[..slashIdx];
            var prefixPart = cidr[(slashIdx + 1)..];
            if (!TryParseIPv4(ipPart, out _)) continue;
            if (!int.TryParse(prefixPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var prefix)) continue;
            if (prefix is < 0 or > 32) continue;
            var ipBytes = IPAddress.Parse(ipPart).GetAddressBytes();
            var baseAddr = ((uint)ipBytes[0] << 24) | ((uint)ipBytes[1] << 16) |
                           ((uint)ipBytes[2] << 8) | (uint)ipBytes[3];
            var hostBits = 32 - prefix;
            uint size = hostBits == 32 ? uint.MaxValue : (1u << hostBits);
            var mask = hostBits == 32 ? 0u : ~((1u << hostBits) - 1u);
            baseAddr &= mask;
            segs.Add(new CidrSegment(baseAddr, size));
            total += size;
        }
        totalHosts = total;
        return segs;
    }

    public sealed record SamplingResult(IReadOnlyList<string> Ips, ulong TotalAvailable, bool Truncated);

    public static SamplingResult GenerateFromCidrs(
        IEnumerable<string> cidrs,
        long targetCount,
        long hardCap,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        var segments = ParseSegments(cidrs, out var total);
        if (total == 0 || segments.Count == 0)
            return new SamplingResult(Array.Empty<string>(), 0, Truncated: false);

        var requested = targetCount <= 0 ? (long)Math.Min(total, (ulong)hardCap) : Math.Min(targetCount, hardCap);
        var actual = (ulong)requested <= total ? (ulong)requested : total;
        if (actual == 0)
            return new SamplingResult(Array.Empty<string>(), total, Truncated: false);

        var truncated = (ulong)requested > total
            || ((ulong)hardCap < total && (targetCount <= 0 || (ulong)targetCount > (ulong)hardCap));

        var ips = new List<string>((int)Math.Min(actual, (ulong)int.MaxValue));
        var lastReport = 0;
        var reportEvery = Math.Max(2000, (int)Math.Min(actual / 50, 50_000));

        if (actual >= total)
        {
            foreach (var seg in segments)
            {
                for (uint i = 0; i < seg.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    ips.Add(FormatIPv4(seg.BaseAddr + i));
                    if (ips.Count - lastReport >= reportEvery)
                    {
                        lastReport = ips.Count;
                        progress?.Report(ips.Count);
                    }
                    if (seg.Count == uint.MaxValue && i == uint.MaxValue) break;
                }
            }
        }
        else
        {
            var rng = new Random();
            var seen = new HashSet<uint>(capacity: (int)Math.Min(actual * 2, (ulong)int.MaxValue));

            var prefix = new ulong[segments.Count];
            ulong running = 0;
            for (int s = 0; s < segments.Count; s++)
            {
                running += segments[s].Count;
                prefix[s] = running;
            }
            while ((ulong)ips.Count < actual)
            {
                ct.ThrowIfCancellationRequested();
                var pick = NextUlong(rng, total);
                var idx = UpperBound(prefix, pick);
                var seg = segments[idx];
                var offset = (uint)(pick - (idx == 0 ? 0UL : prefix[idx - 1]));
                var addr = seg.BaseAddr + offset;
                if (seen.Add(addr))
                {
                    ips.Add(FormatIPv4(addr));
                    if (ips.Count - lastReport >= reportEvery)
                    {
                        lastReport = ips.Count;
                        progress?.Report(ips.Count);
                    }
                }
            }
        }

        progress?.Report(ips.Count);
        return new SamplingResult(ips, total, truncated);
    }

    private static ulong NextUlong(Random rng, ulong exclusiveMax)
    {
        if (exclusiveMax <= int.MaxValue) return (ulong)rng.Next((int)exclusiveMax);
        Span<byte> buf = stackalloc byte[8];
        while (true)
        {
            rng.NextBytes(buf);
            var v = BitConverter.ToUInt64(buf);
            var threshold = ulong.MaxValue - (ulong.MaxValue % exclusiveMax);
            if (v < threshold) return v % exclusiveMax;
        }
    }

    private static int UpperBound(ulong[] prefix, ulong value)
    {
        int lo = 0, hi = prefix.Length - 1;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (prefix[mid] <= value) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    /// <summary>
    /// Lazy / chunked parsing path used by the scanner for very large
    /// inputs (multi-million IPs). Walks input once, collects parsed
    /// segments (CIDR / dash / singleton) plus diagnostics without ever
    /// materializing all hosts. The returned <see cref="StreamSource"/>
    /// exposes <see cref="StreamSource.TotalHosts"/> (sum of segment
    /// sizes) and <see cref="StreamSource.Enumerate"/> which yields
    /// IPs on demand — letting the caller scan in chunks while keeping
    /// memory bounded.
    /// </summary>
    public static StreamSource BuildStream(string? input)
    {
        var warnings = new List<string>();
        var entries = new List<StreamEntry>();
        ulong total = 0;

        if (string.IsNullOrWhiteSpace(input))
            return new StreamSource(entries, warnings, total);

        foreach (var rawLine in input.Split('\n'))
        {
            var line = StripComment(rawLine).Trim();
            if (line.Length == 0) continue;
            foreach (var token in line.Split(
                new[] { ' ', '\t', ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (TryBuildEntry(token, warnings, out var entry))
                {
                    entries.Add(entry);
                    total += entry.Count;
                }
            }
        }
        return new StreamSource(entries, warnings, total);
    }

    private static bool TryBuildEntry(string token, List<string> warnings, out StreamEntry entry)
    {
        entry = default;
        var slashIdx = token.IndexOf('/');
        if (slashIdx > 0)
        {
            var ipPart = token[..slashIdx];
            var prefixPart = token[(slashIdx + 1)..];
            if (!TryParseIPv4(ipPart, out _))
            {
                warnings.Add($"'{token}': base address is not a valid IPv4");
                return false;
            }
            if (!int.TryParse(prefixPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var prefix))
            {
                warnings.Add($"'{token}': prefix is not an integer");
                return false;
            }
            if (prefix is < 0 or > 32)
            {
                warnings.Add($"'{token}': prefix must be 0-32");
                return false;
            }
            var baseAddr = IPv4ToUInt(ipPart);
            var hostBits = 32 - prefix;
            uint size = hostBits == 32 ? uint.MaxValue : (1u << hostBits);
            var mask = hostBits == 32 ? 0u : ~((1u << hostBits) - 1u);
            baseAddr &= mask;
            entry = new StreamEntry(baseAddr, size);
            return true;
        }

        var m = RangeRegex.Match(token);
        if (m.Success)
        {
            var startStr = m.Groups[1].Value;
            var endStr = m.Groups[2].Value;
            if (!TryParseIPv4(startStr, out _))
            {
                warnings.Add($"'{startStr}-{endStr}': start is not a valid IPv4");
                return false;
            }
            uint startAddr = IPv4ToUInt(startStr);
            uint endAddr;
            if (endStr.Contains('.'))
            {
                if (!TryParseIPv4(endStr, out _))
                {
                    warnings.Add($"'{startStr}-{endStr}': end is not a valid IPv4");
                    return false;
                }
                endAddr = IPv4ToUInt(endStr);
            }
            else
            {
                if (!int.TryParse(endStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var lastOctet) ||
                    lastOctet is < 0 or > 255)
                {
                    warnings.Add($"'{startStr}-{endStr}': end is not a valid IPv4 or last octet");
                    return false;
                }
                endAddr = (startAddr & 0xFFFFFF00u) | (uint)lastOctet;
            }
            if (endAddr < startAddr)
            {
                warnings.Add($"'{startStr}-{endStr}': end < start");
                return false;
            }
            ulong count = (ulong)endAddr - startAddr + 1;
            if (count > uint.MaxValue)
            {
                warnings.Add($"'{startStr}-{endStr}': spans more than 2^32 addresses");
                return false;
            }
            entry = new StreamEntry(startAddr, (uint)count);
            return true;
        }

        if (TryParseIPv4(token, out var ip))
        {
            entry = new StreamEntry(IPv4ToUInt(ip.ToString()), 1u);
            return true;
        }

        warnings.Add($"'{token}': not a valid IPv4 / CIDR / dash range");
        return false;
    }

    public readonly record struct StreamEntry(uint BaseAddr, uint Count);

    public sealed class StreamSource
    {
        private readonly IReadOnlyList<StreamEntry> _entries;

        public IReadOnlyList<string> Warnings { get; }
        public ulong TotalHosts { get; }

        internal StreamSource(IReadOnlyList<StreamEntry> entries, IReadOnlyList<string> warnings, ulong totalHosts)
        {
            _entries = entries;
            Warnings = warnings;
            TotalHosts = totalHosts;
        }

        /// <summary>
        /// Yields IPs lazily across all entries in declaration order.
        /// De-duplication is NOT performed in the stream — for the
        /// huge-range use case (e.g. /8), the cost of a HashSet over
        /// 16M strings would defeat the purpose. Callers that need
        /// dedup should use <see cref="ExpandWithDiagnostics"/>
        /// instead.
        /// </summary>
        public IEnumerable<string> Enumerate(CancellationToken ct = default)
        {
            foreach (var seg in _entries)
            {
                if (seg.Count == 1u)
                {
                    ct.ThrowIfCancellationRequested();
                    yield return FormatIPv4(seg.BaseAddr);
                    continue;
                }
                uint remaining = seg.Count;
                uint addr = seg.BaseAddr;
                while (remaining > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    yield return FormatIPv4(addr);
                    remaining--;
                    if (remaining == 0) break;
                    addr++;
                }
            }
        }
    }
}
