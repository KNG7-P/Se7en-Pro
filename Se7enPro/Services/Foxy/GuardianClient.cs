using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Se7enPro.Services.Foxy;

public sealed class GuardianClient : IDisposable
{
    private const string GuardianBase = "https://vpn.mozilla.org";
    private const string RemoteSettingsUrl =
        "https://firefox.settings.services.mozilla.com/v1/buckets/main/collections/vpn-serverlist/records";
    private const string UserAgent = "MozillaVPN/2.35.0 (sys:linux; iap:true)";

    private readonly ILogger _logger;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _serverListLock = new(1, 1);
    private List<VpnCountry>? _cachedCountries;

    public List<VpnCountry> CachedCountries => _cachedCountries ?? GetBuiltInFallbackCountries();

    public GuardianClient(ILogger logger, string? upstreamSocks = null, List<VpnCountry>? seedCountries = null,
        Func<IWebProxy?>? proxyProvider = null)
    {
        _logger = logger;
        _cachedCountries = seedCountries;

        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = FoxyNetwork.ConnectDirectWithDohFallbackAsync,
        };
        if (!string.IsNullOrWhiteSpace(upstreamSocks))
        {
            handler.UseProxy = true;
            handler.Proxy = new WebProxy("socks5://" + upstreamSocks);
        }
        else if (proxyProvider is not null)
        {
            handler.UseProxy = true;
            handler.Proxy = new DynamicWebProxy(proxyProvider);
        }
        else
        {
            handler.UseProxy = false;
            handler.Proxy = null;
        }

        _http = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(20) };
    }

    public void Dispose()
    {
        try
        {
            _http.Dispose();
        }
        catch
        {
        }
    }

    public async Task<bool> ActivateAsync(string accessToken, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, GuardianBase + "/api/v1/fpn/activate");
            req.Headers.UserAgent.ParseAdd(UserAgent);
            req.Headers.Accept.ParseAdd("application/json");
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + accessToken);
            req.Content = new ByteArrayContent(Array.Empty<byte>());
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                _logger.LogInformation("Activated Mozilla Guardian entitlement.");
                return true;
            }
            _logger.LogWarning("Guardian activation answered HTTP {Status}.", resp.StatusCode);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Guardian activation failed: {Message}", ex.Message);
            return false;
        }
    }

    public Task<ProxyPassResult> FetchProxyPassAsync(string accessToken, CancellationToken ct = default) =>
        FetchProxyPassCoreAsync(accessToken, allowAutoActivate: true, ct);

    private async Task<ProxyPassResult> FetchProxyPassCoreAsync(
        string accessToken, bool allowAutoActivate, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, GuardianBase + "/api/v1/fpn/token");
        req.Headers.UserAgent.ParseAdd(UserAgent);
        req.Headers.Accept.ParseAdd("application/json");
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + accessToken);

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);

        if (resp.StatusCode == HttpStatusCode.TooManyRequests)
            throw new FirefoxQuotaExceededException("Account monthly traffic quota exceeded.");

        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            if (allowAutoActivate)
            {
                _logger.LogInformation("Guardian rejected the token; activating entitlement...");
                if (await ActivateAsync(accessToken, ct).ConfigureAwait(false))
                    return await FetchProxyPassCoreAsync(accessToken, allowAutoActivate: false, ct)
                        .ConfigureAwait(false);
            }
            throw new FirefoxAuthTokenInvalidException("Access token rejected by Mozilla Guardian.");
        }

        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(json);
        var token = doc.RootElement.GetProperty("token").GetString()
                    ?? throw new InvalidOperationException("Proxy pass response had no token.");

        var expiresAt = doc.RootElement.TryGetProperty("expires_at", out var e) && e.TryGetInt64(out var exp) && exp > 0
            ? exp
            : ExtractJwtExpiry(token) ?? (DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 900);

        return new ProxyPassResult(token, expiresAt,
            ReadQuotaHeader(resp, "X-Quota-Limit"),
            ReadQuotaHeader(resp, "X-Quota-Remaining"),
            ReadQuotaHeader(resp, "X-Quota-Reset"));
    }

    private static long? ReadQuotaHeader(HttpResponseMessage resp, string name) =>
        resp.Headers.TryGetValues(name, out var values) && long.TryParse(values.FirstOrDefault(), out var value)
            ? value : null;

    internal static long? ExtractJwtExpiry(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length < 2) return null;

            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = (payload.Length % 4) switch
            {
                2 => payload + "==",
                3 => payload + "=",
                _ => payload,
            };

            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            if (doc.RootElement.TryGetProperty("exp", out var e) && e.TryGetInt64(out var exp) && exp > 0)
                return exp;
        }
        catch
        {
        }
        return null;
    }

    public async Task<List<VpnCountry>> FetchCountriesAsync(CancellationToken ct = default)
    {
        if (_cachedCountries is { Count: > 0 }) return _cachedCountries;

        await _serverListLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cachedCountries is { Count: > 0 }) return _cachedCountries;

            using var req = new HttpRequestMessage(HttpMethod.Get, RemoteSettingsUrl);
            req.Headers.UserAgent.ParseAdd(UserAgent);
            req.Headers.Accept.ParseAdd("application/json");

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            var list = ParseServerListJson(json);
            _logger.LogInformation("Fetched {Count} Firefox VPN countries from Remote Settings.", list.Count);
            return _cachedCountries = list;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Remote server list failed; using built-in fallback servers: {Message}", ex.Message);
            return _cachedCountries = GetBuiltInFallbackCountries();
        }
        finally
        {
            _serverListLock.Release();
        }
    }

    public static List<VpnCountry> ParseServerListJson(string json)
    {
        var countries = new List<VpnCountry>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return countries;

        foreach (var record in data.EnumerateArray())
        {
            var name = record.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var code = record.TryGetProperty("code", out var c) ? c.GetString() ?? "" : "";
            if (string.Equals(name, "CatchAll Anycast", StringComparison.OrdinalIgnoreCase)) continue;

            var cities = new List<VpnCity>();
            if (record.TryGetProperty("cities", out var cityArray) && cityArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var city in cityArray.EnumerateArray())
                {
                    var cityName = city.TryGetProperty("name", out var cn) ? cn.GetString() ?? "" : "";
                    var cityCode = city.TryGetProperty("code", out var cc) ? cc.GetString() ?? "" : "";

                    var servers = new List<VpnServerNode>();
                    if (city.TryGetProperty("servers", out var serverArray) && serverArray.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var server in serverArray.EnumerateArray())
                        {
                            var hostname = server.TryGetProperty("hostname", out var h) ? h.GetString() ?? "" : "";
                            var port = server.TryGetProperty("port", out var p) ? p.GetInt32() : 2499;
                            var quarantined = server.TryGetProperty("quarantined", out var q) && q.GetBoolean();
                            if (!string.IsNullOrEmpty(hostname) && !quarantined)
                                servers.Add(new VpnServerNode(hostname, port > 0 ? port : 2499, quarantined));
                        }
                    }

                    if (servers.Count > 0) cities.Add(new VpnCity(cityName, cityCode, servers));
                }
            }

            if (!string.IsNullOrWhiteSpace(code) && cities.Count > 0) countries.Add(new VpnCountry(name, code, cities));
        }

        return countries;
    }

    public static List<ProxyCandidate> GetCandidates(
        List<VpnCountry> countries, string? countryCode = null, string? cityCode = null)
    {
        var candidates = new List<ProxyCandidate>();
        foreach (var country in countries)
        {
            var wantsAll = string.IsNullOrEmpty(countryCode)
                           || string.Equals(countryCode, "REC", StringComparison.OrdinalIgnoreCase)
                           || string.Equals(countryCode, "AUTO", StringComparison.OrdinalIgnoreCase);
            if (!wantsAll && !string.Equals(country.Code, countryCode, StringComparison.OrdinalIgnoreCase)) continue;

            foreach (var city in country.Cities)
            {
                if (!string.IsNullOrEmpty(cityCode)
                    && !string.Equals(city.Code, cityCode, StringComparison.OrdinalIgnoreCase)) continue;

                foreach (var server in city.Servers)
                {
                    if (!server.Quarantined)
                        candidates.Add(new ProxyCandidate(server.Hostname, server.Port, country.Code, country.Name,
                            city.Code));
                }
            }
        }

        if (candidates.Count == 0 && !string.IsNullOrEmpty(cityCode))
            return GetCandidates(countries, countryCode);

        if (candidates.Count == 0)
            candidates.Add(new ProxyCandidate("p.m1.fastly-masque.net", 2499, "REC", "Recommended", ""));

        return candidates;
    }

    public static List<ProxyCandidate> OrderCandidates(
        List<ProxyCandidate> candidates, string? preferredHost, int preferredPort,
        IReadOnlyCollection<string>? avoidHosts = null)
    {
        var ordered = new List<ProxyCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        bool Avoided(ProxyCandidate c) =>
            avoidHosts is { Count: > 0 } && avoidHosts.Contains(c.Host, StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(preferredHost))
        {
            foreach (var candidate in candidates)
            {
                if (string.Equals(candidate.Host, preferredHost, StringComparison.OrdinalIgnoreCase)
                    && (preferredPort <= 0 || candidate.Port == preferredPort)
                    && !Avoided(candidate) && seen.Add(candidate.Host))
                {
                    ordered.Add(candidate);
                }
            }
        }

        foreach (var candidate in candidates)
        {
            if (!Avoided(candidate) && seen.Add(candidate.Host)) ordered.Add(candidate);
        }

        if (avoidHosts is { Count: > 0 })
        {
            foreach (var candidate in candidates)
            {
                if (Avoided(candidate) && seen.Add(candidate.Host)) ordered.Add(candidate);
            }
        }

        return ordered;
    }

    public static List<VpnCountry> GetBuiltInFallbackCountries() =>
    [
        Country("Recommended", "REC", "Fastest Edge", "REC", "p.m1.fastly-masque.net"),
        Country("Argentina", "AR", "Argentina", "EZE", "eze223.m1.fastly-masque.net"),
        Country("Australia", "AU", "Australia", "YSBK", "ysbk106.m1.fastly-masque.net"),
        Country("Austria", "AT", "Austria", "VIE", "vie63.m1.fastly-masque.net"),
        Country("Belgium", "BE", "Belgium", "BRU", "bru148.m1.fastly-masque.net"),
        Country("Brazil", "BR", "Brazil", "SBSP", "sbsp209.m1.fastly-masque.net"),
        Country("Bulgaria", "BG", "Bulgaria", "SOF", "sof151.m1.fastly-masque.net"),
        Country("Canada", "CA", "Canada", "CA", "ca.m1.fastly-masque.net"),
        Country("Chile", "CL", "Chile", "SCL", "scl222.m1.fastly-masque.net"),
        Country("Colombia", "CO", "Colombia", "SKBO", "skbo234.m1.fastly-masque.net"),
        Country("Denmark", "DK", "Denmark", "CPH", "cph232.m1.fastly-masque.net"),
        Country("Finland", "FI", "Finland", "HEL", "hel141.m1.fastly-masque.net"),
        Country("France", "FR", "France", "LFPB", "lfpb115.m1.fastly-masque.net"),
        Country("Germany", "DE", "Germany", "MUC", "muc139.m1.fastly-masque.net"),
        Country("Ghana", "GH", "Ghana", "ACC", "acc96.m1.fastly-masque.net"),
        Country("Ireland", "IE", "Ireland", "DUB", "dub43.m1.fastly-masque.net"),
        Country("Italy", "IT", "Italy", "MXP", "mxp69.m1.fastly-masque.net"),
        Country("Japan", "JP", "Japan", "RJTF", "rjtf770.m1.fastly-masque.net"),
        Country("Korea", "KR", "Korea", "ICN", "icn145.m1.fastly-masque.net"),
        Country("Malaysia", "MY", "Malaysia", "KUL", "kul98.m1.fastly-masque.net"),
        Country("Mexico", "MX", "Mexico", "MMQT", "mmqt107.m1.fastly-masque.net"),
        Country("Netherlands", "NL", "Netherlands", "EHRD", "ehrd229.m1.fastly-masque.net"),
        Country("New Zealand", "NZ", "New Zealand", "AKL", "akl103.m1.fastly-masque.net"),
        Country("Norway", "NO", "Norway", "OSL", "osl65.m1.fastly-masque.net"),
        Country("Peru", "PE", "Peru", "SPJC", "spjc128.m1.fastly-masque.net"),
        Country("Philippines", "PH", "Philippines", "MNL", "mnl215.m1.fastly-masque.net"),
        Country("Portugal", "PT", "Portugal", "LIS", "lis149.m1.fastly-masque.net"),
        Country("Singapore", "SG", "Singapore", "WSAT", "wsat188.m1.fastly-masque.net"),
        Country("South Africa", "ZA", "South Africa", "FAOR", "faor73.m1.fastly-masque.net"),
        Country("Spain", "ES", "Spain", "LETO", "leto235.m1.fastly-masque.net"),
        Country("Sweden", "SE", "Sweden", "ESSB", "essb127.m1.fastly-masque.net"),
        Country("Thailand", "TH", "Thailand", "BKK", "bkk228.m1.fastly-masque.net"),
        Country("United Kingdom", "GB", "United Kingdom", "EGLC", "eglc860.m1.fastly-masque.net"),
        Country("United States", "US", "United States", "US", "us.m1.fastly-masque.net"),
    ];

    private static VpnCountry Country(string name, string code, string cityName, string cityCode, string host) =>
        new(name, code, [new VpnCity(cityName, cityCode, [new VpnServerNode(host, 2499)])]);
}
