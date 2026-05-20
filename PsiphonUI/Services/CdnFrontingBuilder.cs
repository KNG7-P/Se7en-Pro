using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace PsiphonUI.Services;

public static class CdnFrontingBuilder
{

    public const int MaxCustomCdnFrontingIpAddresses = 32;

    public static readonly IReadOnlyList<(string OverrideId, string IpAddress)> DefaultEdgeIps =
        new (string, string)[]
        {
            ("edge-a-1",      "23.215.0.206"),
            ("edge-a-2",      "23.215.0.203"),
            ("edge-b-1",      "23.212.250.91"),
            ("edge-b-2",      "23.212.250.78"),
            ("edge-c-1",      "23.12.147.13"),
            ("edge-c-2",      "23.12.147.29"),
            ("edge-d-1",      "23.73.207.8"),
            ("edge-d-2",      "23.73.207.15"),
            ("edge-original", "92.123.102.43"),
        };

    private static readonly string[] FastlyVerifyServerNames =
    {
        "www.python.org",
        "pypi.org",
        "fastly.com",
        "www.fastly.com",
        "developer.fastly.com",
        "githubassets.com",
        "github.com",
        "github.io",
        "githubusercontent.com",
    };

    public static JsonArray BuildDialOverrides(string? customIpList, string? customSni)
    {
        var overrides = new JsonArray();
        var edgeDialAddresses = new HashSet<string>(StringComparer.Ordinal);
        var edgeSni = NormalizeCdnFrontingCustomSni(customSni);

        overrides.Add(MakeOverride(
            overrideId: "fastly-provider",
            matchFrontingProviderIdRegexes: new[] { "(?i)fastly" },
            matchDialAddressRegexes: null,
            dialAddress: "pypi.org",
            sniServerName: "pypi.org",
            verifyServerNames: FastlyVerifyServerNames,
            alpnProtocols: new[] { "h2", "http/1.1" }));

        overrides.Add(MakeOverride(
            overrideId: "fastly-address",
            matchFrontingProviderIdRegexes: null,
            matchDialAddressRegexes: new[] { "(?i)(fastly|pypi|python|github)" },
            dialAddress: "pypi.org",
            sniServerName: "pypi.org",
            verifyServerNames: FastlyVerifyServerNames,
            alpnProtocols: new[] { "h2", "http/1.1" }));

        var customIndex = 1;
        foreach (var ip in ParseCdnFrontingCustomIpList(customIpList))
        {
            PutEdgeOverride(overrides, edgeDialAddresses, $"edge-custom-{customIndex}", ip, edgeSni);
            customIndex++;
        }

        foreach (var (id, ip) in DefaultEdgeIps)
        {
            PutEdgeOverride(overrides, edgeDialAddresses, id, ip, edgeSni);
        }

        return overrides;
    }

    public static List<string> ParseCdnFrontingCustomIpList(string? customIpList)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(customIpList)) return result;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in customIpList.Split(new[] { ' ', '\t', '\r', '\n', ',', ';' },
                                                 StringSplitOptions.RemoveEmptyEntries))
        {
            var ip = entry.Trim();
            if (ip.Length == 0 || !IsValidIPv4(ip)) continue;
            if (!seen.Add(ip)) continue;
            result.Add(ip);
            if (result.Count >= MaxCustomCdnFrontingIpAddresses) break;
        }
        return result;
    }

    public static string NormalizeCdnFrontingCustomSni(string? customSni)
    {
        if (string.IsNullOrEmpty(customSni)) return "";
        var sni = customSni.Trim();
        return IsValidHostname(sni) ? sni : "";
    }

    public static bool IsValidIPv4(string ip)
    {
        if (string.IsNullOrEmpty(ip)) return false;
        var parts = ip.Split('.');
        if (parts.Length != 4) return false;
        foreach (var part in parts)
        {
            if (part.Length == 0 || part.Length > 3) return false;
            foreach (var c in part)
            {
                if (c < '0' || c > '9') return false;
            }
            if (!int.TryParse(part, out var v) || v < 0 || v > 255) return false;
        }
        return true;
    }

    public static bool IsValidHostname(string hostname)
    {
        if (string.IsNullOrEmpty(hostname) || hostname.Length > 253) return false;
        if (IsValidIPv4(hostname)) return false;

        var normalised = hostname;
        if (normalised.EndsWith('.')) normalised = normalised.Substring(0, normalised.Length - 1);
        if (normalised.Length == 0) return false;

        foreach (var label in normalised.Split('.'))
        {
            if (label.Length == 0 || label.Length > 63) return false;
            if (label.StartsWith('-') || label.EndsWith('-')) return false;
            foreach (var c in label)
            {
                var ok = (c >= 'a' && c <= 'z') ||
                         (c >= 'A' && c <= 'Z') ||
                         (c >= '0' && c <= '9') ||
                         c == '-';
                if (!ok) return false;
            }
        }
        return true;
    }

    private static JsonObject MakeEdgeOverride(string overrideId, string ipAddress, string customSni)
    {
        var sniServerName = string.IsNullOrEmpty(customSni) ? ipAddress : customSni;
        return MakeOverride(
            overrideId: overrideId,
            matchFrontingProviderIdRegexes: null,
            matchDialAddressRegexes: new[] { ".*" },
            dialAddress: ipAddress,
            sniServerName: sniServerName,
            verifyServerNames: BuildEdgeVerifyServerNames(ipAddress, sniServerName),
            alpnProtocols: new[] { "http/1.1" });
    }

    private static void PutEdgeOverride(
        JsonArray overrides,
        HashSet<string> dialAddresses,
        string overrideId,
        string ipAddress,
        string customSni)
    {
        if (dialAddresses.Add(ipAddress))
        {
            overrides.Add(MakeEdgeOverride(overrideId, ipAddress, customSni));
        }
    }

    private static JsonObject MakeOverride(
        string overrideId,
        IReadOnlyList<string>? matchFrontingProviderIdRegexes,
        IReadOnlyList<string>? matchDialAddressRegexes,
        string dialAddress,
        string sniServerName,
        IReadOnlyList<string> verifyServerNames,
        IReadOnlyList<string> alpnProtocols)
    {
        var obj = new JsonObject
        {
            ["OverrideID"] = overrideId,
        };
        if (matchFrontingProviderIdRegexes is not null)
        {
            obj["MatchFrontingProviderIDRegexes"] = ToJsonArray(matchFrontingProviderIdRegexes);
        }
        if (matchDialAddressRegexes is not null)
        {
            obj["MatchDialAddressRegexes"] = ToJsonArray(matchDialAddressRegexes);
        }
        obj["DialAddresses"] = ToJsonArray(new[] { dialAddress });
        obj["SNIServerName"] = sniServerName;
        obj["VerifyServerNames"] = ToJsonArray(verifyServerNames);
        obj["ALPNProtocols"] = ToJsonArray(alpnProtocols);
        obj["TLSProfile"] = "Chrome-83";
        return obj;
    }

    private static List<string> BuildEdgeVerifyServerNames(string ipAddress, string sniServerName)
    {
        var list = new List<string>(capacity: 9);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(string? v)
        {
            if (!string.IsNullOrEmpty(v) && seen.Add(v)) list.Add(v);
        }
        Add(sniServerName);
        Add(ipAddress);
        Add("a248.e.akamai.net");
        Add("a.akamaized.net");
        Add("a.akamaized-staging.net");
        Add("a.akamaihd.net");
        Add("a.akamaihd-staging.net");
        Add("www.akamai.com");
        return list;
    }

    private static JsonArray ToJsonArray(IReadOnlyList<string> values)
    {
        var nodes = new JsonNode?[values.Count];
        for (var i = 0; i < values.Count; i++)
        {
            nodes[i] = JsonValue.Create(values[i]);
        }
        return new JsonArray(nodes);
    }
}
