using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;

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

    // A process name is a bare image file name. Anything that carries a directory
    // separator is a path and must not be coerced into a name ("C:\x\chrome.exe" would
    // otherwise become "C:\x\chrome.exe.exe" and never match).
    public static string NormalizeProcessName(string raw)
    {
        var s = (raw ?? "").Trim().Trim('"');
        if (s.Length == 0) return "";
        var cut = s.LastIndexOfAny(new[] { '\\', '/' });
        if (cut >= 0) s = s[(cut + 1)..];
        if (s.Length == 0) return "";
        if (!s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s += ".exe";
        return s;
    }

    // ---- keyword ------------------------------------------------------------
    // A keyword matches anywhere inside the exe file name or the full image path,
    // case-insensitively: "chrom" finds chrome.exe without the user knowing the exact name.
    public static string NormalizeKeyword(string raw)
    {
        var s = (raw ?? "").Trim().Trim('"');
        if (s.Length == 0) return "";
        // Strip a trailing ".exe" so "chrome.exe" and "chrome" produce the same rule.
        if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s = s[..^4];
        return s.Trim().ToLowerInvariant();
    }

    public static bool KeywordMatches(string keyword, string? processName, string? processPath)
    {
        if (string.IsNullOrEmpty(keyword)) return false;
        if (!string.IsNullOrEmpty(processName)
            && processName.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return true;
        }
        return !string.IsNullOrEmpty(processPath)
            && processPath.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    // ---- regex --------------------------------------------------------------
    private static readonly Dictionary<string, Regex?> RegexCache = new(StringComparer.Ordinal);
    private static readonly object RegexLock = new();

    public static string? NormalizeRegex(string raw)
    {
        var s = (raw ?? "").Trim();
        if (s.Length == 0) return null;
        return IsValidRegex(s) ? s : null;
    }

    public static bool IsValidRegex(string pattern)
    {
        try
        {
            _ = new Regex(pattern, RegexOptions.IgnoreCase);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool RegexMatches(string pattern, string? processName, string? processPath)
    {
        if (string.IsNullOrEmpty(pattern)) return false;

        Regex? regex;
        lock (RegexLock)
        {
            if (!RegexCache.TryGetValue(pattern, out regex))
            {
                try
                {
                    regex = new Regex(
                        pattern,
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                        TimeSpan.FromMilliseconds(250));
                }
                catch
                {
                    regex = null;
                }

                // Bounded so a profile that cycles through many patterns cannot grow
                // this dictionary without limit.
                if (RegexCache.Count > 256) RegexCache.Clear();
                RegexCache[pattern] = regex;
            }
        }

        if (regex is null) return false;

        try
        {
            if (!string.IsNullOrEmpty(processName) && regex.IsMatch(processName)) return true;
            return !string.IsNullOrEmpty(processPath) && regex.IsMatch(processPath);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    // ---- suffix -------------------------------------------------------------
    // "suffix" is the most literal of the name matchers: the exe file name must end
    // with the text, minus the optional ".exe". "rome" matches chrome.exe but not
    // romex.exe.
    public static string NormalizeSuffix(string raw)
    {
        var s = (raw ?? "").Trim().Trim('"');
        if (s.Length == 0) return "";
        if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s = s[..^4];
        return s.TrimStart('.').Trim().ToLowerInvariant();
    }

    public static bool SuffixMatches(string suffix, string? processName)
    {
        if (string.IsNullOrEmpty(suffix) || string.IsNullOrEmpty(processName)) return false;
        var stem = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? processName[..^4]
            : processName;
        return stem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
    }

    public static (IPAddress Addr, byte Prefix)? ParseIpCidr(string cidr)
    {
        if (string.IsNullOrEmpty(cidr)) return null;
        var slash = cidr.IndexOf('/');
        var addrPart = slash >= 0 ? cidr[..slash] : cidr;
        var prefixPart = slash >= 0 ? cidr[(slash + 1)..] : null;

        if (!IPAddress.TryParse(addrPart, out var ip)) return null;
        var max = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32;
        if (prefixPart is null) return (ip, (byte)max);
        if (!byte.TryParse(prefixPart, out var prefix) || prefix > max) return null;
        return (ip, prefix);
    }

    public static bool DomainMatchesSuffix(string? host, string suffix)
    {
        if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(suffix)) return false;
        host = host.Trim().TrimEnd('.');
        return string.Equals(host, suffix, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IpInCidr(IPAddress address, string cidr)
    {
        if (ParseIpCidr(cidr) is not { } parsed) return false;
        if (parsed.Addr.AddressFamily != address.AddressFamily) return false;

        var a = address.GetAddressBytes();
        var b = parsed.Addr.GetAddressBytes();
        if (a.Length != b.Length) return false;

        var remaining = parsed.Prefix;
        for (var i = 0; i < a.Length; i++)
        {
            if (remaining <= 0) return true;
            var take = (byte)Math.Min(8, (int)remaining);
            var mask = (byte)(0xFF << (8 - take));
            if ((a[i] & mask) != (b[i] & mask)) return false;
            remaining -= take;
        }

        return true;
    }

    public static IReadOnlyList<string> SplitValues(string? raw) =>
        (raw ?? "")
            .Split(new[] { '\n', '\r', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(static v => v.Trim())
            .Where(static v => v.Length > 0)
            .ToList();
}
