using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace PsiphonUI.Services;

public static class IpRangeParser
{
    public const int MaxEntries = 200_000;

    public const int CidrMaxHosts = 262_144;

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
        idx = line.IndexOf("//", StringComparison.Ordinal);
        if (idx >= 0) line = line[..idx];
        return line;
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

        warnings.Add($"'{token}': not a valid IPv4 / CIDR / dash range");
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
        var size = hostBits == 32 ? uint.MaxValue : ((1u << hostBits));
        if (size > CidrMaxHosts)
        {
            warnings.Add($"'{token}': /{prefix} has {size:N0} hosts, exceeds the per-range cap of {CidrMaxHosts:N0}");
            return;
        }

        var mask = hostBits == 32 ? 0u : ~((1u << hostBits) - 1u);
        baseAddr &= mask;

        for (uint i = 0; i < size && result.Count < MaxEntries; i++)
        {
            var addr = baseAddr + i;
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
        if (!IPAddress.TryParse(s, out var parsed)) return false;
        if (parsed.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        addr = parsed;
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
}
