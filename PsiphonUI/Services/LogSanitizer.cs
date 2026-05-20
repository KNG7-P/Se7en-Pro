using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PsiphonUI.Services;

public static class LogSanitizer
{
    private static readonly HashSet<string> SafeNoticeTypes = new(StringComparer.Ordinal)
    {
        "Tunnels",
        "ListeningSocksProxyPort",
        "ListeningHttpProxyPort",
        "ClientRegion",
        "ConnectedServerRegion",
        "AvailableEgressRegions",
        "Info",
        "Alert",
        "CoreVersion",
        "ApplicationParameters",
        "TrafficRateLimits",
    };

    private static readonly Regex Ipv4Regex = new(
        @"\b(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)\b",
        RegexOptions.Compiled);

    private static readonly Regex Ipv6Regex = new(
        @"(?:[A-Fa-f0-9]{1,4}:){2,7}[A-Fa-f0-9]{1,4}|::(?:[A-Fa-f0-9]{1,4}(?::|$)){1,7}|(?:[A-Fa-f0-9]{1,4}:){1,7}:",
        RegexOptions.Compiled);

    private static readonly Regex LongHexRegex = new(
        @"\b[A-Fa-f0-9]{32,}\b",
        RegexOptions.Compiled);

    private static readonly Regex LongBase64Regex = new(
        @"\b[A-Za-z0-9_\-+/]{40,}={0,2}\b",
        RegexOptions.Compiled);

    public static string FormatNotice(string noticeType, JsonElement data)
    {
        if (SafeNoticeTypes.Contains(noticeType))
        {
            var raw = data.ValueKind == JsonValueKind.Undefined ? "" : data.GetRawText();
            return $"[{noticeType}] {Scrub(raw)}";
        }

        return $"[{noticeType}] (sensitive data redacted)";
    }

    public static string Scrub(string line)
    {
        if (string.IsNullOrEmpty(line)) return line;

        var s = Ipv4Regex.Replace(line, "<ip>");
        s = Ipv6Regex.Replace(s, "<ipv6>");
        s = LongHexRegex.Replace(s, "<hex>");
        s = LongBase64Regex.Replace(s, "<b64>");
        return s;
    }
}
