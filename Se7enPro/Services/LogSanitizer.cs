using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Se7enPro.Services;

public static class LogSanitizer
{
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

    public static string? FormatNotice(string noticeType, JsonElement data)
    {
        switch (noticeType)
        {
            case "CoreVersion":
                return TryStr(data, "version", out var ver)
                    ? $"Client Version {ver}"
                    : null;

            case "ListeningSocksProxyPort":
                return TryInt(data, "port", out var sp)
                    ? $"SOCKS running on port {sp}"
                    : null;

            case "ListeningHttpProxyPort":
                return TryInt(data, "port", out var hp)
                    ? $"HTTP proxy running on port {hp}"
                    : null;

            case "Tunnels":
                if (TryInt(data, "count", out var count) && count > 0)
                    return "Tunnel established";
                return null;

            case "ClientRegion":
                return TryStr(data, "region", out var cr)
                    ? $"Client region: {cr}"
                    : null;

            case "ConnectedServerRegion":
                return TryStr(data, "serverRegion", out var srv)
                    ? $"Connected via {srv}"
                    : null;

            case "Alert":
                if (TryStr(data, "message", out var alertMsg))
                    return $"Alert: {Scrub(alertMsg!)}";
                return null;

            case "Info":
                if (TryStr(data, "message", out var msg))
                    return PrettifyInfo(msg!);
                return null;

            case "ActiveTunnel":
            case "AvailableEgressRegions":
            case "ApplicationParameters":
            case "TrafficRateLimits":
            case "BytesTransferred":
            case "Tunneling":
            case "EstablishTunnelTimeout":
            case "ImpairedProtocolClassification":
            case "ActiveAuthorizationIDs":
            case "ServerAlert":
            case "TrafficShapingTactics":
            case "ConnectingServer":
            case "EstablishedServer":
            case "Untunneled":
            case "BuildInfo":
                return null;

            default:
                return null;
        }
    }

    private static string? PrettifyInfo(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var msg = raw.Replace("\\", "").Trim();

        var m = CdnScanActiveRegex.Match(msg);
        if (m.Success)
        {
            var mode = m.Groups[1].Value;
            var workers = m.Groups[2].Value;
            return $"CDN scan active - {mode} mode, {workers} workers";
        }

        m = CdnScanProgressRegex.Match(msg);
        if (m.Success)
            return $"CDN scan: {m.Groups[1].Value} attempts, working: {m.Groups[2].Value}";

        m = CdnScanFoundRegex.Match(msg);
        if (m.Success)
            return $"CDN scan found: {m.Groups[1].Value} via {m.Groups[2].Value}";

        if (msg.Equals("cdn fronting scan stopped", StringComparison.OrdinalIgnoreCase))
            return "CDN scan stopped";

        m = BeastModeRegex.Match(msg);
        if (m.Success)
            return $"Beast Mode active \u2014 testing all protocols ({m.Groups[1].Value} workers)";

        if (msg.StartsWith("FrontedMeekDialOverrides", StringComparison.OrdinalIgnoreCase))
            return null;
        if (msg.StartsWith("DialMeek", StringComparison.OrdinalIgnoreCase))
            return null;
        if (msg.Contains("config loaded", StringComparison.OrdinalIgnoreCase))
            return null;

        return Scrub(msg);
    }

    private static readonly Regex CdnScanActiveRegex = new(
        @"cdn fronting scan active\s*\(mode:\s*(\w+),\s*workers:\s*(\d+)\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CdnScanProgressRegex = new(
        @"cdn fronting scan progress\s*\(attempts:\s*(\d+),\s*working:\s*(\d+)\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CdnScanFoundRegex = new(
        @"cdn fronting scan found\s*\(ip:\s*([^\s,)]+),\s*sni:\s*([^\s,)]+)\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex BeastModeRegex = new(
        @"beast mode\s*[-:\u2014]?\s*testing all protocols\s*\((\d+)\s*workers?\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static bool TryStr(JsonElement data, string property, out string? value)
    {
        value = null;
        if (data.ValueKind != JsonValueKind.Object) return false;
        if (!data.TryGetProperty(property, out var el)) return false;
        if (el.ValueKind != JsonValueKind.String) return false;
        value = el.GetString();
        return !string.IsNullOrEmpty(value);
    }

    private static bool TryInt(JsonElement data, string property, out int value)
    {
        value = 0;
        if (data.ValueKind != JsonValueKind.Object) return false;
        if (!data.TryGetProperty(property, out var el)) return false;
        if (el.ValueKind != JsonValueKind.Number) return false;
        return el.TryGetInt32(out value);
    }

    public static string Scrub(string line)
    {
        if (string.IsNullOrEmpty(line)) return line;

        var s = LongHexRegex.Replace(line, "<hex>");
        s = LongBase64Regex.Replace(s, "<b64>");
        return s;
    }
}
