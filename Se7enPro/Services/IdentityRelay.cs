using System;
using System.Collections.Generic;
using System.Linq;

namespace Se7enPro.Services;

/// <summary>
/// One way of reaching Cloudflare's registration API.
/// </summary>
/// <param name="Label">For the log, so a failure names the route that failed.</param>
/// <param name="Proxy">SOCKS proxy, or null for a direct connection.</param>
/// <param name="RelayBaseUrl">
/// A bootstrap relay to go through instead of Cloudflare, or null to reach
/// api.cloudflareclient.com directly.
/// </param>
public sealed record ProvisioningRoute(
    string Label,
    System.Net.IWebProxy? Proxy,
    string? RelayBaseUrl)
{
    public bool IsDirect => Proxy is null && RelayBaseUrl is null;

    public bool IsRelay => RelayBaseUrl is not null;
}

/// <summary>
/// Bootstrap relays for forwarding Cloudflare device registrations.
/// </summary>
public static class IdentityRelay
{
    /// <summary>Header the relay requires, so an unrelated scanner cannot drive it.</summary>
    public const string RelayHeader = "X-Se7en-Relay";

    /// <summary>Value that must accompany the header.</summary>
    public const string RelayHeaderValue = "1";

    /// <summary>Splits configured relay list into validated base URLs.</summary>
    public static List<string> ParseRelays(string? raw)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(raw)) return result;

        foreach (var token in raw.Split(new[] { ',', ';', ' ', '\t', '\r', '\n' },
                                         StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = token.Trim().TrimEnd('/');
            if (candidate.Length == 0) continue;

            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)) continue;
            if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) continue;

            if (uri.AbsolutePath is { Length: > 1 } p && p != "/") continue;

            result.Add(candidate);
        }

        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Builds ordered candidate routes for device registration.</summary>
    public static List<ProvisioningRoute> BuildRoutes(
        System.Net.IWebProxy? directProxy,
        string? relayList,
        System.Net.IWebProxy? tunnelProxy,
        string tunnelLabel)
    {
        var routes = new List<ProvisioningRoute>
        {
            new("direct", directProxy, RelayBaseUrl: null),
        };

        foreach (var relay in ParseRelays(relayList))
        {
            routes.Add(new ProvisioningRoute($"relay {Shorten(relay)}", Proxy: null, RelayBaseUrl: relay));
        }

        if (tunnelProxy is not null)
        {
            routes.Add(new ProvisioningRoute(tunnelLabel, tunnelProxy, RelayBaseUrl: null));
        }

        return routes;
    }

    private static string Shorten(string url)
    {
        try
        {
            var uri = new Uri(url);
            return uri.Host.Length > 24 ? uri.Host[..24] : uri.Host;
        }
        catch
        {
            return url.Length > 24 ? url[..24] : url;
        }
    }
}