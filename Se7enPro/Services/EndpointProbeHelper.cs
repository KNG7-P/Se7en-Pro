using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Se7enPro.Services;

public sealed record EndpointLocationResult(
    string Ip,
    string CountryCode,
    string? CityOrColo = null
);

public static class EndpointProbeHelper
{
    public static async Task<EndpointLocationResult?> ProbeAsync(int socksPort, CancellationToken ct)
    {
        if (socksPort <= 0) return null;

        try
        {
            using var handler = new SocketsHttpHandler
            {
                Proxy = new WebProxy($"socks5://127.0.0.1:{socksPort}"),
                ConnectTimeout = TimeSpan.FromSeconds(5)
            };
            using var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(12)
            };

            var cfResult = await QueryCloudflareTraceAsync(client, ct);
            if (cfResult != null && !string.IsNullOrEmpty(cfResult.Ip) && !string.IsNullOrEmpty(cfResult.CountryCode))
            {
                return cfResult;
            }

            var countryIsResult = await QueryCountryIsAsync(client, ct);
            if (countryIsResult != null && !string.IsNullOrEmpty(countryIsResult.Ip) && !string.IsNullOrEmpty(countryIsResult.CountryCode))
            {
                return countryIsResult;
            }

            var ipWhoIsResult = await QueryIpWhoIsAsync(client, ct);
            if (ipWhoIsResult != null && !string.IsNullOrEmpty(ipWhoIsResult.Ip) && !string.IsNullOrEmpty(ipWhoIsResult.CountryCode))
            {
                return ipWhoIsResult;
            }

            var ipApiResult = await QueryIpApiAsync(client, ct);
            if (ipApiResult != null && !string.IsNullOrEmpty(ipApiResult.Ip) && !string.IsNullOrEmpty(ipApiResult.CountryCode))
            {
                return ipApiResult;
            }

            if (cfResult != null && !string.IsNullOrEmpty(cfResult.Ip))
            {
                return cfResult;
            }
        }
        catch
        {
        }

        return null;
    }

    private static async Task<EndpointLocationResult?> QueryCloudflareTraceAsync(HttpClient client, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(4));

            var text = await client.GetStringAsync("https://www.cloudflare.com/cdn-cgi/trace", cts.Token);
            string? ip = null;
            string? loc = null;
            string? colo = null;

            foreach (var line in text.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("ip=", StringComparison.OrdinalIgnoreCase))
                {
                    ip = trimmed.Substring(3).Trim();
                }
                else if (trimmed.StartsWith("loc=", StringComparison.OrdinalIgnoreCase))
                {
                    var c = trimmed.Substring(4).Trim().ToUpperInvariant();
                    if (c.Length == 2 && c != "T1" && c != "XX") loc = c;
                }
                else if (trimmed.StartsWith("colo=", StringComparison.OrdinalIgnoreCase))
                {
                    var col = trimmed.Substring(5).Trim().ToUpperInvariant();
                    if (col.Length >= 2) colo = col;
                }
            }

            if (!string.IsNullOrEmpty(ip))
            {
                return new EndpointLocationResult(ip, loc ?? "", colo);
            }
        }
        catch { }
        return null;
    }

    private static async Task<EndpointLocationResult?> QueryCountryIsAsync(HttpClient client, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3.5));

            var json = await client.GetStringAsync("https://api.country.is", cts.Token);
            using var doc = JsonDocument.Parse(json);
            string? ip = null;
            string? country = null;

            if (doc.RootElement.TryGetProperty("ip", out var ipProp))
                ip = ipProp.GetString()?.Trim();
            if (doc.RootElement.TryGetProperty("country", out var cProp))
            {
                var c = cProp.GetString()?.Trim().ToUpperInvariant();
                if (!string.IsNullOrEmpty(c) && c.Length == 2 && c != "T1" && c != "XX") country = c;
            }

            if (!string.IsNullOrEmpty(ip) && !string.IsNullOrEmpty(country))
            {
                return new EndpointLocationResult(ip, country);
            }
        }
        catch { }
        return null;
    }

    private static async Task<EndpointLocationResult?> QueryIpWhoIsAsync(HttpClient client, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3.5));

            var json = await client.GetStringAsync("https://ipwho.is/", cts.Token);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("success", out var sProp) && !sProp.GetBoolean())
                return null;

            string? ip = null;
            string? country = null;
            string? city = null;

            if (doc.RootElement.TryGetProperty("ip", out var ipProp))
                ip = ipProp.GetString()?.Trim();
            if (doc.RootElement.TryGetProperty("country_code", out var cProp))
            {
                var c = cProp.GetString()?.Trim().ToUpperInvariant();
                if (!string.IsNullOrEmpty(c) && c.Length == 2 && c != "T1" && c != "XX") country = c;
            }
            if (doc.RootElement.TryGetProperty("city", out var cityProp))
                city = cityProp.GetString()?.Trim();

            if (!string.IsNullOrEmpty(ip) && !string.IsNullOrEmpty(country))
            {
                return new EndpointLocationResult(ip, country, city);
            }
        }
        catch { }
        return null;
    }

    private static async Task<EndpointLocationResult?> QueryIpApiAsync(HttpClient client, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3.5));

            var lines = (await client.GetStringAsync("http://ip-api.com/line/?fields=status,countryCode,city,query", cts.Token))
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            if (lines.Length >= 4 && lines[0].Trim().Equals("success", StringComparison.OrdinalIgnoreCase))
            {
                var country = lines[1].Trim().ToUpperInvariant();
                var city = lines[2].Trim();
                var ip = lines[3].Trim();

                if (country.Length == 2 && country != "T1" && country != "XX")
                {
                    return new EndpointLocationResult(ip, country, string.IsNullOrEmpty(city) ? null : city);
                }
            }
        }
        catch { }
        return null;
    }
}
