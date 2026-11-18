using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Se7enPro.Models;

namespace Se7enPro.Services.Veep;

public sealed class VeepServer
{
    [JsonPropertyName("region")]
    public string Region { get; set; } = "";

    [JsonPropertyName("regionName")]
    public string RegionName { get; set; } = "";

    [JsonPropertyName("host")]
    public string Host { get; set; } = "";

    [JsonPropertyName("port")]
    public int Port { get; set; }

    [JsonPropertyName("username")]
    public string Username { get; set; } = "";

    [JsonPropertyName("password")]
    public string Password { get; set; } = "";

    [JsonPropertyName("ip")]
    public string Ip { get; set; } = "";

    [JsonIgnore]
    public List<string> CandidateIps { get; set; } = new();

    [JsonIgnore]
    public int MeasuredLatencyMs { get; set; } = int.MaxValue;

    [JsonIgnore]
    public double MeasuredSpeedKbps { get; set; }
}

public sealed class VeepEngine : LocalSocksEngineBase
{
    public const int DefaultListenPort = 1860;
    public const int DefaultHttpPort = 1861;

    public static string? UpstreamProxyOverride { get; set; }

    private readonly IChildProcessGuard _childGuard;
    private List<string> _knownEndpointIps = new();
    private string _detectedIp = "";
    private string _detectedCountry = "";

    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";

    private static readonly string[] ApiDomains =
    {
        "https://antpeak.com",
        "https://hibchr.com",
        "https://hisball.com",
        "https://bitphox.com",
        "https://freloop.com",
        "https://tronlit.com",
        "https://tronyza.com"
    };

    private static readonly string[] ReserveBucketUrls =
    {
        "https://s3-oregon-1.s3-us-west-2.amazonaws.com/api.json",
        "https://proigor.com/payload.json"
    };

    public VeepEngine(
        ILogger<VeepEngine> logger,
        ISettingsService settings,
        IChildProcessGuard childGuard)
        : base(logger, settings, childGuard)
    {
        _childGuard = childGuard;
    }

    public override ConnectionMethod Method => ConnectionMethod.Veep;

    public override IReadOnlyList<string> CoreProcessNames { get; } =
        new[] { EngineProcessNames.Veep, "sing-box.exe" };

    protected override string EngineDisplayName => "Veep";

    protected override string WorkSubdirectory => "veep";

    protected override TimeSpan ReadyTimeout => TimeSpan.FromSeconds(35);

    public override string ServerEndpointAddress => string.IsNullOrEmpty(UpstreamProxyOverride) ? _detectedIp : "";

    public override IReadOnlyList<string> ServerEndpointAddresses => string.IsNullOrEmpty(UpstreamProxyOverride) ? _knownEndpointIps : Array.Empty<string>();

    public override IReadOnlyList<string> AvailableEgressRegions =>
        new[] { "auto", "fr-prs", "nl", "gb-lnd", "us-va", "us-or", "sg", "ru-spb" };

    internal const int RefreshBudgetSeconds = 12;
    internal const int CacheProbeTimeoutMs = 1500;
    private const int RankConnectTimeoutMs = 1350;
    private const int ThroughputProbeTimeoutMs = 1300;
    private const int ThroughputTopCandidates = 4;

    protected override async Task BeforeStartAsync(string workDir, CancellationToken ct)
    {
        try
        {
            var workServers = Path.Combine(workDir, "servers.json");
            var hasCache = File.Exists(workServers);
            var fi = hasCache ? new FileInfo(workServers) : null;
            var cacheStale = fi == null || (DateTime.UtcNow - fi.LastWriteTimeUtc).TotalHours > 6;

            if (!cacheStale)
            {
                var cached = TryReadServersFile(workServers);
                var selectedRegion = (_settings.Settings.VeepRegion ?? "auto").Trim().ToLowerInvariant();
                var isAuto = string.IsNullOrEmpty(selectedRegion) || selectedRegion == "auto";
                var regionSubset = (cached == null || isAuto)
                    ? cached
                    : cached.Where(s => string.Equals(s.Region, selectedRegion, StringComparison.OrdinalIgnoreCase)).ToList();

                if (regionSubset == null || regionSubset.Count == 0)
                {
                    cacheStale = true;
                }
                else if (string.IsNullOrWhiteSpace(UpstreamProxyOverride) &&
                         !await ProbeAnyServerReachableAsync(regionSubset, ct).ConfigureAwait(false))
                {
                    cacheStale = true;
                }
            }

            if (cacheStale)
            {
                using var fetchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                fetchCts.CancelAfter(TimeSpan.FromSeconds(RefreshBudgetSeconds));
                var refreshed = await TryRefreshServersAsync(workServers, fetchCts.Token).ConfigureAwait(false);
                if (!refreshed && hasCache)
                {
                    try
                    {
                        var cached = TryReadServersFile(workServers);
                        if (cached != null && string.IsNullOrWhiteSpace(UpstreamProxyOverride) &&
                            !await ProbeAnyServerReachableAsync(cached, ct).ConfigureAwait(false))
                        {
                            File.Delete(workServers);
                        }
                    }
                    catch { }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Veep dynamic server fetch skipped or timed out");
        }
    }

    internal static async Task<bool> ProbeAnyServerReachableAsync(IReadOnlyList<VeepServer> servers, CancellationToken ct)
    {
        if (servers == null || servers.Count == 0) return false;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(CacheProbeTimeoutMs);
        var candidates = servers
            .Where(s => s.Port > 0 && (!string.IsNullOrWhiteSpace(s.Ip) || !string.IsNullOrWhiteSpace(s.Host)))
            .Take(6)
            .ToList();
        if (candidates.Count == 0) return false;

        var tasks = candidates.Select(async s =>
        {
            var rtt = await ProbeServerConnectAsync(s, CacheProbeTimeoutMs, cts.Token).ConfigureAwait(false);
            return rtt.HasValue;
        }).ToList();

        while (tasks.Count > 0)
        {
            var done = await Task.WhenAny(tasks).ConfigureAwait(false);
            tasks.Remove(done);
            if (await done.ConfigureAwait(false))
            {
                cts.Cancel();
                return true;
            }
        }
        return false;
    }

    private static async Task<int?> ProbeServerConnectAsync(VeepServer s, int timeoutMs, CancellationToken ct)
    {
        var targets = new List<string>();
        if (!string.IsNullOrWhiteSpace(s.Ip)) targets.Add(s.Ip.Trim());
        foreach (var candidateIp in s.CandidateIps)
        {
            if (!string.IsNullOrWhiteSpace(candidateIp) &&
                !targets.Contains(candidateIp.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                targets.Add(candidateIp.Trim());
            }
        }
        if (targets.Count == 0 && !string.IsNullOrWhiteSpace(s.Host)) targets.Add(s.Host.Trim());
        if (targets.Count == 0 || s.Port <= 0) return null;

        var sniHost = IsIpLiteral(s.Host) ? "" : s.Host.Trim();
        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        probeCts.CancelAfter(timeoutMs);

        foreach (var target in targets)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                using var tcp = new TcpClient { NoDelay = true };
                await tcp.ConnectAsync(target, s.Port, probeCts.Token).ConfigureAwait(false);
                using var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
                var authOpts = new SslClientAuthenticationOptions
                {
                    TargetHost = string.IsNullOrEmpty(sniHost) ? target : sniHost
                };
                await ssl.AuthenticateAsClientAsync(authOpts, probeCts.Token).ConfigureAwait(false);

                var reqBytes = BuildConnectRequestBytes(s, "cp.cloudflare.com", 443);
                await ssl.WriteAsync(reqBytes, probeCts.Token).ConfigureAwait(false);
                await ssl.FlushAsync(probeCts.Token).ConfigureAwait(false);

                var statusLine = await ReadHttpStatusLineAsync(ssl, probeCts.Token).ConfigureAwait(false);
                sw.Stop();
                if (statusLine.Contains(" 200", StringComparison.Ordinal))
                {
                    s.Ip = target;
                    s.MeasuredLatencyMs = (int)Math.Max(1, sw.ElapsedMilliseconds);
                    return s.MeasuredLatencyMs;
                }
            }
            catch
            {
            }
        }
        return null;
    }

    private static byte[] BuildConnectRequestBytes(VeepServer s, string targetHost, int targetPort)
    {
        var creds = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{s.Username}:{s.Password}"));
        var req =
            $"CONNECT {targetHost}:{targetPort} HTTP/1.1\r\n" +
            $"Host: {targetHost}:{targetPort}\r\n" +
            $"Proxy-Authorization: Basic {creds}\r\n" +
            $"User-Agent: {BrowserUserAgent}\r\n" +
            $"Proxy-Connection: keep-alive\r\n\r\n";
        return Encoding.ASCII.GetBytes(req);
    }

    private static async Task<string> ReadHttpStatusLineAsync(Stream stream, CancellationToken ct)
    {
        var buf = new byte[1024];
        var total = 0;
        while (total < buf.Length)
        {
            var read = await stream.ReadAsync(buf.AsMemory(total, buf.Length - total), ct).ConfigureAwait(false);
            if (read <= 0) break;
            total += read;
            var text = Encoding.ASCII.GetString(buf, 0, total);
            var headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (headerEnd >= 0)
            {
                var lineEnd = text.IndexOf('\n');
                return lineEnd >= 0 ? text[..lineEnd].TrimEnd('\r') : text;
            }
        }
        return total > 0 ? Encoding.ASCII.GetString(buf, 0, total) : "";
    }

    private static async Task<double> ProbeServerThroughputKbpsAsync(VeepServer s, int timeoutMs, CancellationToken ct)
    {
        var target = !string.IsNullOrWhiteSpace(s.Ip) ? s.Ip.Trim() : s.Host.Trim();
        if (string.IsNullOrEmpty(target) || s.Port <= 0) return 0;

        var sniHost = IsIpLiteral(s.Host) ? target : s.Host.Trim();
        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        probeCts.CancelAfter(timeoutMs);

        var sw = Stopwatch.StartNew();
        try
        {
            using var tcp = new TcpClient { NoDelay = true };
            await tcp.ConnectAsync(target, s.Port, probeCts.Token).ConfigureAwait(false);
            using var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = sniHost }, probeCts.Token).ConfigureAwait(false);

            var connectReq = BuildConnectRequestBytes(s, "speed.cloudflare.com", 80);
            await ssl.WriteAsync(connectReq, probeCts.Token).ConfigureAwait(false);
            await ssl.FlushAsync(probeCts.Token).ConfigureAwait(false);

            var statusLine = await ReadHttpStatusLineAsync(ssl, probeCts.Token).ConfigureAwait(false);
            if (!statusLine.Contains(" 200", StringComparison.Ordinal)) return 0;

            var httpGet = Encoding.ASCII.GetBytes(
                "GET /__down?bytes=65536 HTTP/1.1\r\n" +
                "Host: speed.cloudflare.com\r\n" +
                $"User-Agent: {BrowserUserAgent}\r\n" +
                "Accept: */*\r\n" +
                "Connection: close\r\n\r\n");
            await ssl.WriteAsync(httpGet, probeCts.Token).ConfigureAwait(false);
            await ssl.FlushAsync(probeCts.Token).ConfigureAwait(false);

            var totalBytes = 0;
            var buf = new byte[16384];
            while (totalBytes < 65536)
            {
                var n = await ssl.ReadAsync(buf.AsMemory(), probeCts.Token).ConfigureAwait(false);
                if (n <= 0) break;
                totalBytes += n;
            }
            sw.Stop();
            var secs = sw.Elapsed.TotalSeconds;
            if (totalBytes >= 8192 && secs >= 0.02)
            {
                return totalBytes / 1024.0 / secs;
            }
        }
        catch { }
        return 0;
    }

    private async Task<List<VeepServer>> ProbeAndRankServersAsync(List<VeepServer> servers, CancellationToken ct)
    {
        if (servers.Count <= 1) return servers;

        using var phase1Cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        phase1Cts.CancelAfter(RankConnectTimeoutMs);

        await Task.WhenAll(servers.Select(async s =>
        {
            s.MeasuredLatencyMs = int.MaxValue;
            s.MeasuredSpeedKbps = 0;
            await ProbeServerConnectAsync(s, RankConnectTimeoutMs, phase1Cts.Token).ConfigureAwait(false);
        })).ConfigureAwait(false);

        var working = servers
            .Where(s => s.MeasuredLatencyMs < int.MaxValue)
            .OrderBy(s => s.MeasuredLatencyMs)
            .ToList();

        if (working.Count == 0)
        {
            return servers;
        }

        if (working.Count > 1)
        {
            var topCandidates = working.Take(ThroughputTopCandidates).ToList();
            using var phase2Cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            phase2Cts.CancelAfter(ThroughputProbeTimeoutMs);

            await Task.WhenAll(topCandidates.Select(async s =>
            {
                s.MeasuredSpeedKbps = await ProbeServerThroughputKbpsAsync(s, ThroughputProbeTimeoutMs, phase2Cts.Token).ConfigureAwait(false);
            })).ConfigureAwait(false);

            working = working
                .OrderByDescending(s => s.MeasuredSpeedKbps > 0 ? 1 : 0)
                .ThenByDescending(s => s.MeasuredSpeedKbps)
                .ThenBy(s => s.MeasuredLatencyMs)
                .ToList();
        }

        var best = working[0];
        Log(best.MeasuredSpeedKbps > 0
            ? $"[Veep] Ranked {working.Count}/{servers.Count} healthy servers (best: {best.Host} [{best.Region}] {best.MeasuredLatencyMs}ms, {best.MeasuredSpeedKbps:F0} KB/s)."
            : $"[Veep] Ranked {working.Count}/{servers.Count} healthy servers (best: {best.Host} [{best.Region}] {best.MeasuredLatencyMs}ms).");
        return working;
    }

    private async Task<bool> TryRefreshServersAsync(string targetPath, CancellationToken ct)
    {
        var hasUpstream = !string.IsNullOrWhiteSpace(UpstreamProxyOverride);
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(4),
            PooledConnectionLifetime = TimeSpan.FromMinutes(1)
        };

        if (hasUpstream)
        {
            try
            {
                var proxyUri = UpstreamProxyOverride!.StartsWith("socks", StringComparison.OrdinalIgnoreCase)
                    ? new Uri(UpstreamProxyOverride)
                    : new Uri("socks5://" + UpstreamProxyOverride);
                handler.Proxy = new WebProxy(proxyUri);
                handler.UseProxy = true;
            }
            catch { }
        }
        else
        {
            handler.ConnectCallback = ConnectDirectWithCleanDnsAsync;
        }

        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(6) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserUserAgent);

        if (await TryRaceDomainsAsync(client, ApiDomains, hasUpstream, targetPath, ct).ConfigureAwait(false))
        {
            return true;
        }

        var tried = new HashSet<string>(ApiDomains, StringComparer.OrdinalIgnoreCase);
        var reserveDomains = await FetchReserveDomainsAsync(client, ct).ConfigureAwait(false);
        var freshDomains = reserveDomains.Where(d => tried.Add(d)).ToArray();
        if (freshDomains.Length > 0 &&
            await TryRaceDomainsAsync(client, freshDomains, hasUpstream, targetPath, ct).ConfigureAwait(false))
        {
            return true;
        }
        return false;
    }

    private async Task<bool> TryRaceDomainsAsync(
        HttpClient client, IReadOnlyList<string> domains, bool hasUpstream, string targetPath, CancellationToken ct)
    {
        if (domains.Count == 0) return false;
        using var raceCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var tasks = domains.Select(domain => FetchFromDomainAsync(client, domain, hasUpstream, raceCts.Token)).ToList();
        while (tasks.Count > 0)
        {
            var finished = await Task.WhenAny(tasks).ConfigureAwait(false);
            tasks.Remove(finished);
            try
            {
                var result = await finished.ConfigureAwait(false);
                if (result.Servers is { Count: > 0 } freshList)
                {
                    raceCts.Cancel();
                    var json = JsonSerializer.Serialize(freshList, new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
                    });
                    await File.WriteAllTextAsync(targetPath, json, ct).ConfigureAwait(false);
                    Log($"[Veep] Dynamically refreshed {freshList.Count} servers from {result.Domain}.");
                    return true;
                }
            }
            catch { }
        }
        return false;
    }

    private static async Task<List<string>> FetchReserveDomainsAsync(HttpClient client, CancellationToken ct)
    {
        var collected = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var bucketCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bucketCts.CancelAfter(TimeSpan.FromSeconds(4));

        var responses = await Task.WhenAll(ReserveBucketUrls.Select(async url =>
        {
            try
            {
                return await client.GetStringAsync(url, bucketCts.Token).ConfigureAwait(false);
            }
            catch
            {
                return null;
            }
        })).ConfigureAwait(false);

        foreach (var body in responses)
        {
            if (string.IsNullOrWhiteSpace(body)) continue;
            foreach (var d in ExtractReserveFreeDomains(body))
            {
                if (seen.Add(d)) collected.Add(d);
            }
        }
        return collected;
    }

    internal static IReadOnlyList<string> ExtractReserveFreeDomains(string json)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddDomain(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return;
            var d = raw.Trim().TrimEnd('/');
            if (!d.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                if (d.Contains("://", StringComparison.Ordinal)) return;
                d = "https://" + d;
            }
            if (Uri.TryCreate(d, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
            {
                var normalized = $"https://{uri.Host}";
                if (seen.Add(normalized)) result.Add(normalized);
            }
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return result;

            if (root.TryGetProperty("domains", out var domainsEl) &&
                domainsEl.ValueKind == JsonValueKind.Object &&
                domainsEl.TryGetProperty("free", out var freeList) &&
                freeList.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in freeList.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String) AddDomain(item.GetString());
                }
            }

            if (root.TryGetProperty("free", out var singleFree))
            {
                if (singleFree.ValueKind == JsonValueKind.String) AddDomain(singleFree.GetString());
                else if (singleFree.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in singleFree.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String) AddDomain(item.GetString());
                    }
                }
            }
        }
        catch { }
        return result;
    }

    private async Task<(string Domain, List<VeepServer>? Servers)> FetchFromDomainAsync(
        HttpClient client, string domain, bool hasUpstream, CancellationToken ct)
    {
        try
        {
            var launchRes = await client.PostAsJsonAsync($"{domain}/v3/launch/", new
            {
                udid = Guid.NewGuid().ToString("N"),
                appVersion = "5.0.2",
                platform = "chrome",
                platformVersion = "130.0.0.0",
                timeZone = "UTC",
                deviceName = "Chrome 130"
            }, ct).ConfigureAwait(false);

            if (!launchRes.IsSuccessStatusCode) return (domain, null);
            var launchJson = await launchRes.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ct).ConfigureAwait(false);
            var token = launchJson?["data"]?["token"]?.GetValue<string>()
                     ?? launchJson?["data"]?["access"]?.GetValue<string>()
                     ?? launchJson?["access"]?.GetValue<string>();

            if (string.IsNullOrEmpty(token)) return (domain, null);

            var optimalTask = FetchOptimalRegionAsync(client, domain, token, ct);
            var regions = AvailableEgressRegions.Where(r => !string.Equals(r, "auto", StringComparison.OrdinalIgnoreCase)).ToArray();
            var regionTasks = regions.Select(reg => FetchRegionServersAsync(client, domain, token, reg, ct)).ToArray();

            await Task.WhenAll(regionTasks).ConfigureAwait(false);
            var parsed = new List<VeepServer>();
            foreach (var list in regionTasks.Select(t => t.Result))
            {
                parsed.AddRange(list);
            }

            if (parsed.Count == 0)
            {
                parsed.AddRange(await FetchRegionServersAsync(client, domain, token, region: null, ct).ConfigureAwait(false));
            }

            var optimalRegion = await optimalTask.ConfigureAwait(false);
            parsed = DeduplicateServers(parsed);
            if (!string.IsNullOrWhiteSpace(optimalRegion))
            {
                parsed = parsed
                    .OrderByDescending(s => string.Equals(s.Region, optimalRegion, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            if (parsed.Count > 0)
            {
                await ResolveMissingIpsParallelAsync(parsed, ct, hasUpstream ? client : null).ConfigureAwait(false);
            }
            return (domain, parsed.Count > 0 ? parsed : null);
        }
        catch
        {
            return (domain, null);
        }
    }

    private static async Task<string?> FetchOptimalRegionAsync(HttpClient client, string domain, string token, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{domain}/v3/location/optimal/");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            using var optCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            optCts.CancelAfter(2500);
            using var res = await client.SendAsync(req, optCts.Token).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return null;
            var node = await res.Content.ReadFromJsonAsync<JsonNode>(cancellationToken: optCts.Token).ConfigureAwait(false);
            var obj = node as JsonObject;
            var dataObj = obj?["data"] as JsonObject;
            return obj?["region"]?.GetValue<string>() ?? dataObj?["region"]?.GetValue<string>();
        }
        catch
        {
            return null;
        }
    }

    private static async Task<List<VeepServer>> FetchRegionServersAsync(
        HttpClient client, string domain, string token, string? region, CancellationToken ct)
    {
        var parsed = new List<VeepServer>();
        try
        {
            using var listReq = new HttpRequestMessage(HttpMethod.Post, $"{domain}/v3/server/list/");
            listReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            listReq.Content = string.IsNullOrWhiteSpace(region)
                ? JsonContent.Create(new { protocol = "https", type = 0 })
                : JsonContent.Create(new { protocol = "https", region, type = 0 });

            using var listRes = await client.SendAsync(listReq, ct).ConfigureAwait(false);
            if (!listRes.IsSuccessStatusCode) return parsed;

            var listJson = await listRes.Content.ReadFromJsonAsync<JsonNode>(cancellationToken: ct).ConfigureAwait(false);
            return ParseServerListNode(listJson, region);
        }
        catch { }
        return parsed;
    }

    internal static List<VeepServer> ParseServerListNode(JsonNode? listJson, string? fallbackRegion = null)
    {
        var parsed = new List<VeepServer>();
        var rawList = listJson as JsonArray ?? (listJson as JsonObject)?["data"] as JsonArray;
        if (rawList == null || rawList.Count == 0) return parsed;

        foreach (var node in rawList)
        {
            if (node is not JsonObject item) continue;
            var addresses = item["addresses"] as JsonArray;
            string? firstHostname = null;
            string? firstLiteralIp = null;
            if (addresses != null)
            {
                foreach (var a in addresses)
                {
                    var val = a?.GetValue<string>()?.Trim();
                    if (string.IsNullOrEmpty(val)) continue;
                    if (IsIpLiteral(val)) firstLiteralIp ??= val;
                    else firstHostname ??= val;
                }
            }

            var itemHost = item["host"]?.GetValue<string>()?.Trim() ?? "";
            var host = firstHostname ?? (!string.IsNullOrEmpty(itemHost) ? itemHost : firstLiteralIp) ?? "";
            var port = item["port"]?.GetValue<int>() ?? 0;
            var user = item["username"]?.GetValue<string>() ?? "";
            var pass = item["password"]?.GetValue<string>() ?? "";
            var reg = item["region"]?.GetValue<string>() ?? (fallbackRegion ?? "");
            var regName = item["regionName"]?.GetValue<string>() ?? "";

            if (!string.IsNullOrEmpty(host) && port > 0)
            {
                var ip = item["ip"]?.GetValue<string>()?.Trim();
                if (string.IsNullOrEmpty(ip)) ip = firstLiteralIp ?? "";
                parsed.Add(new VeepServer
                {
                    Region = reg,
                    RegionName = regName,
                    Host = host,
                    Port = port,
                    Username = user,
                    Password = pass,
                    Ip = ip
                });
            }
        }
        return parsed;
    }

    internal static bool IsIpLiteral(string? host) =>
        !string.IsNullOrWhiteSpace(host) && IPAddress.TryParse(host.Trim(), out _);

    internal static List<VeepServer> DeduplicateServers(IEnumerable<VeepServer> servers)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<VeepServer>();
        foreach (var s in servers)
        {
            if (string.IsNullOrWhiteSpace(s.Host) || s.Port <= 0) continue;
            var key = $"{s.Host.Trim()}:{s.Port}";
            if (seen.Add(key))
            {
                result.Add(s);
            }
        }
        return result;
    }

    internal static bool IsUsablePublicIpv4(IPAddress a)
    {
        if (a.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = a.GetAddressBytes();
        if (b[0] is 0 or 10 or 127) return false;
        if (b[0] >= 224) return false;
        if (b[0] == 37 && b[1] == 156) return false;
        if (b[0] == 217 && b[1] == 218) return false;
        if (b[0] == 169 && b[1] == 254) return false;
        if (b[0] == 100 && (b[1] & 0xC0) == 64) return false;
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return false;
        if (b[0] == 192 && b[1] == 168) return false;
        return true;
    }

    private static async Task<IPAddress[]> ResolveCleanIpv4Async(
        string host, CancellationToken ct, HttpClient? dohHttpClient = null)
    {
        if (IPAddress.TryParse(host, out var literal))
            return literal.AddressFamily == AddressFamily.InterNetwork ? new[] { literal } : Array.Empty<IPAddress>();

        if (dohHttpClient == null)
        {
            try
            {
                using var dnsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                dnsCts.CancelAfter(1200);
                var addrs = await Dns.GetHostAddressesAsync(host, dnsCts.Token).ConfigureAwait(false);
                var clean = addrs.Where(IsUsablePublicIpv4).ToArray();
                if (clean.Length > 0) return clean;
            }
            catch { }
        }

        try
        {
            HttpClient? ownedClient = null;
            HttpClient activeClient;
            if (dohHttpClient != null)
            {
                activeClient = dohHttpClient;
            }
            else
            {
                var dohHandler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(2) };
                ownedClient = new HttpClient(dohHandler) { Timeout = TimeSpan.FromSeconds(2.5) };
                activeClient = ownedClient;
            }

            try
            {
                using var dohCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var endpoints = new[]
                {
                    $"https://1.1.1.1/dns-query?name={Uri.EscapeDataString(host)}&type=A",
                    $"https://8.8.8.8/dns-query?name={Uri.EscapeDataString(host)}&type=A"
                };
                var tasks = endpoints.Select(ep => QueryDohEndpointAsync(activeClient, ep, dohCts.Token)).ToList();
                while (tasks.Count > 0)
                {
                    var finished = await Task.WhenAny(tasks).ConfigureAwait(false);
                    tasks.Remove(finished);
                    var found = await finished.ConfigureAwait(false);
                    if (found.Length > 0)
                    {
                        dohCts.Cancel();
                        return found;
                    }
                }
            }
            finally
            {
                ownedClient?.Dispose();
            }
        }
        catch { }

        return Array.Empty<IPAddress>();
    }

    private static async Task<IPAddress[]> QueryDohEndpointAsync(HttpClient client, string ep, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, ep);
            req.Headers.TryAddWithoutValidation("Accept", "application/dns-json");
            using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return Array.Empty<IPAddress>();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (doc.RootElement.TryGetProperty("Answer", out var ans) && ans.ValueKind == JsonValueKind.Array)
            {
                var found = new List<IPAddress>();
                foreach (var item in ans.EnumerateArray())
                {
                    if (item.TryGetProperty("type", out var t) && t.GetInt32() == 1 &&
                        item.TryGetProperty("data", out var d) &&
                        IPAddress.TryParse(d.GetString(), out var ip) && IsUsablePublicIpv4(ip))
                    {
                        found.Add(ip);
                    }
                }
                return found.ToArray();
            }
        }
        catch { }
        return Array.Empty<IPAddress>();
    }

    private static async ValueTask<Stream> ConnectDirectWithCleanDnsAsync(
        SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var addrs = await ResolveCleanIpv4Async(context.DnsEndPoint.Host, ct).ConfigureAwait(false);
        if (addrs.Length == 0) throw new SocketException((int)SocketError.HostNotFound);

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addrs, context.DnsEndPoint.Port, ct).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static Task ResolveMissingIpsParallelAsync(
        IReadOnlyList<VeepServer> servers, CancellationToken ct, HttpClient? dohHttpClient = null)
    {
        var unresolved = servers.Where(s => string.IsNullOrWhiteSpace(s.Ip) && !string.IsNullOrWhiteSpace(s.Host)).ToList();
        if (unresolved.Count == 0) return Task.CompletedTask;
        return Task.WhenAll(unresolved.Select(async s =>
        {
            try
            {
                var addrs = await ResolveCleanIpv4Async(s.Host, ct, dohHttpClient).ConfigureAwait(false);
                if (addrs.Length > 0)
                {
                    s.CandidateIps = addrs.Select(a => a.ToString()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    s.Ip = s.CandidateIps[0];
                }
            }
            catch { }
        }));
    }

    private static List<VeepServer>? TryReadServersFile(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text)) return null;
            var servers = JsonSerializer.Deserialize<List<VeepServer>>(text);
            return servers is { Count: > 0 } ? servers : null;
        }
        catch
        {
            return null;
        }
    }

    protected override PreparedLaunch Prepare(string workDir, int socksPort, int httpPort)
    {
        Directory.CreateDirectory(workDir);
        IdentityFileGuard.RestrictToCurrentUser(workDir, out _);

        var resVeep = Path.Combine(AppDir, "Resources", "veep");
        var singBoxDir = Path.Combine(AppDir, "Resources", "sing_box");
        var singBoxExe = Path.Combine(singBoxDir, "sing-box.exe");
        if (!File.Exists(singBoxExe))
        {
            throw new FileNotFoundException("Sing-box core binary not found at " + singBoxExe);
        }

        var exePath = StageExecutable(singBoxExe, Path.Combine(workDir, EngineProcessNames.Veep));
        var servers = DeduplicateServers(LoadServers(workDir, resVeep));
        if (servers.Count == 0)
        {
            throw new InvalidOperationException("No Veep servers found in configuration or seed files.");
        }

        var selectedRegion = (_settings.Settings.VeepRegion ?? "auto").Trim().ToLowerInvariant();
        var isAuto = string.IsNullOrEmpty(selectedRegion) || selectedRegion == "auto";

        var chosenServers = isAuto
            ? servers.Where(s => !s.Region.StartsWith("ru", StringComparison.OrdinalIgnoreCase)).ToList()
            : servers.Where(s => string.Equals(s.Region, selectedRegion, StringComparison.OrdinalIgnoreCase)).ToList();

        if (chosenServers.Count == 0)
        {
            chosenServers = servers;
        }

        var predefinedHosts = new JsonObject
        {
            ["cp.cloudflare.com"] = new JsonArray { "104.16.132.229", "104.16.133.229" },
            ["one.one.one.one"] = new JsonArray { "1.1.1.1", "1.0.0.1" },
            ["dns.google"] = new JsonArray { "8.8.8.8", "8.8.4.4" }
        };

        var endpointAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hasUpstream = !string.IsNullOrWhiteSpace(UpstreamProxyOverride);
        if (!hasUpstream)
        {
            try
            {
                using var resolveCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                ResolveMissingIpsParallelAsync(chosenServers, resolveCts.Token).GetAwaiter().GetResult();
            }
            catch { }

            try
            {
                using var rankCts = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
                rankCts.CancelAfter(TimeSpan.FromMilliseconds(RankConnectTimeoutMs + ThroughputProbeTimeoutMs + 400));
                chosenServers = ProbeAndRankServersAsync(chosenServers, rankCts.Token).GetAwaiter().GetResult();
            }
            catch { }
        }

        foreach (var s in chosenServers)
        {
            var targetIp = !string.IsNullOrWhiteSpace(s.Ip) ? s.Ip : s.Host;
            endpointAddresses.Add(targetIp);
            endpointAddresses.Add(s.Host);

            if (!string.IsNullOrWhiteSpace(s.Ip) && !IsIpLiteral(s.Host))
            {
                var ipArr = new JsonArray { s.Ip };
                foreach (var alt in s.CandidateIps)
                {
                    if (!string.Equals(alt, s.Ip, StringComparison.OrdinalIgnoreCase))
                        ipArr.Add(alt);
                }
                predefinedHosts[s.Host] = ipArr;
            }
        }

        _knownEndpointIps = endpointAddresses.ToList();
        var firstServer = chosenServers.First();
        _detectedIp = !string.IsNullOrWhiteSpace(firstServer.Ip) ? firstServer.Ip : firstServer.Host;
        _detectedCountry = isAuto ? "Auto" : firstServer.Region;

        ConnectedServerRegion = isAuto ? "" : CountryHelper.FullName(firstServer.Region);
        CurrentRouteIp = isAuto ? "" : _detectedIp;
        CurrentRouteSni = $"Veep ({_detectedCountry})";
        RaiseRouteChanged();

        var listenHost = "127.0.0.1";
        var inbounds = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "socks",
                ["tag"] = "socks-in",
                ["listen"] = listenHost,
                ["listen_port"] = socksPort
            },
            new JsonObject
            {
                ["type"] = "mixed",
                ["tag"] = "mixed-in",
                ["listen"] = listenHost,
                ["listen_port"] = httpPort
            }
        };

        var outbounds = new JsonArray();
        string? detourTag = null;

        if (!string.IsNullOrWhiteSpace(UpstreamProxyOverride))
        {
            var cleanProxy = UpstreamProxyOverride.Trim();
            if (cleanProxy.StartsWith("socks5://", StringComparison.OrdinalIgnoreCase))
                cleanProxy = cleanProxy.Substring("socks5://".Length);
            else if (cleanProxy.StartsWith("socks://", StringComparison.OrdinalIgnoreCase))
                cleanProxy = cleanProxy.Substring("socks://".Length);

            var parts = cleanProxy.Split(':');
            var host = parts[0];
            var port = parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : 1080;
            detourTag = "upstream-chain";
            outbounds.Add(new JsonObject
            {
                ["type"] = "socks",
                ["tag"] = detourTag,
                ["server"] = host,
                ["server_port"] = port
            });
        }

        var serverTags = new List<string>();
        foreach (var s in chosenServers)
        {
            var targetIp = !string.IsNullOrWhiteSpace(s.Ip) ? s.Ip : s.Host;
            var tag = $"veep-{s.Region}-{s.Host}:{s.Port}";
            serverTags.Add(tag);

            var tlsObj = new JsonObject
            {
                ["enabled"] = true,
                ["insecure"] = false,
                ["alpn"] = new JsonArray { "http/1.1" },
                ["utls"] = new JsonObject
                {
                    ["enabled"] = false
                }
            };
            if (!IsIpLiteral(s.Host))
            {
                tlsObj["server_name"] = s.Host;
            }

            var outboundObj = new JsonObject
            {
                ["type"] = "http",
                ["tag"] = tag,
                ["server"] = targetIp,
                ["server_port"] = s.Port,
                ["connect_timeout"] = "5s",
                ["username"] = s.Username,
                ["password"] = s.Password,
                ["headers"] = new JsonObject
                {
                    ["User-Agent"] = BrowserUserAgent
                },
                ["tls"] = tlsObj
            };

            if (!string.IsNullOrEmpty(detourTag))
            {
                outboundObj["detour"] = detourTag;
            }

            outbounds.Add(outboundObj);
        }

        var autoOutbound = new JsonObject
        {
            ["type"] = "urltest",
            ["tag"] = "auto",
            ["outbounds"] = new JsonArray(serverTags.Select(t => (JsonNode)JsonValue.Create(t)!).ToArray()),
            ["url"] = "https://www.gstatic.com/generate_204",
            ["interval"] = "60s",
            ["tolerance"] = 200,
            ["idle_timeout"] = "15m",
            ["interrupt_exist_connections"] = false
        };
        outbounds.Insert(0, autoOutbound);

        outbounds.Add(new JsonObject
        {
            ["type"] = "direct",
            ["tag"] = "direct"
        });

        var dnsServers = new JsonArray
        {
            new JsonObject
            {
                ["tag"] = "dns_hosts",
                ["type"] = "hosts",
                ["predefined"] = predefinedHosts
            },
            new JsonObject
            {
                ["tag"] = "dns_remote",
                ["type"] = "https",
                ["server"] = "1.1.1.1",
                ["server_port"] = 443,
                ["path"] = "/dns-query",
                ["detour"] = "auto"
            }
        };
        if (!string.IsNullOrEmpty(detourTag))
        {
            dnsServers.Add(new JsonObject
            {
                ["tag"] = "dns_upstream",
                ["type"] = "https",
                ["server"] = "1.1.1.1",
                ["server_port"] = 443,
                ["path"] = "/dns-query",
                ["detour"] = detourTag
            });
        }

        var dnsConfig = new JsonObject
        {
            ["servers"] = dnsServers,
            ["rules"] = new JsonArray
            {
                new JsonObject
                {
                    ["domain"] = new JsonArray
                    {
                        "cp.cloudflare.com",
                        "one.one.one.one",
                        "dns.google"
                    },
                    ["server"] = "dns_hosts"
                },
                new JsonObject
                {
                    ["domain_suffix"] = new JsonArray
                    {
                        ".nexorcdn.com",
                        "nexorcdn.com"
                    },
                    ["server"] = "dns_hosts"
                }
            },
            ["final"] = "dns_remote",
            ["strategy"] = "ipv4_only"
        };

        var routeRules = new JsonArray
        {
            new JsonObject { ["action"] = "sniff" },
            new JsonObject
            {
                ["protocol"] = "dns",
                ["action"] = "hijack-dns"
            },
            new JsonObject
            {
                ["port"] = 53,
                ["action"] = "hijack-dns"
            },
            new JsonObject
            {
                ["network"] = new JsonArray("udp"),
                ["action"] = "reject"
            }
        };

        var routeConfig = new JsonObject
        {
            ["rules"] = routeRules,
            ["final"] = "auto",
            ["default_domain_resolver"] = !string.IsNullOrEmpty(detourTag) ? "dns_upstream" : "dns_remote",
            ["auto_detect_interface"] = true
        };

        var rootConfig = new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = "info" },
            ["dns"] = dnsConfig,
            ["inbounds"] = inbounds,
            ["outbounds"] = outbounds,
            ["route"] = routeConfig
        };

        var runConfigPath = Path.Combine(workDir, "veep_run.json");
        File.WriteAllText(runConfigPath, rootConfig.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
        }));

        var regionLabel = !isAuto ? $" [Region: {selectedRegion.ToUpperInvariant()}]" : " [Auto Region]";
        var chainLabel = !string.IsNullOrEmpty(UpstreamProxyOverride)
            ? $" (Chained via outer SOCKS: {UpstreamProxyOverride})"
            : "";

        Log($"Launching Veep core ({chosenServers.Count} servers loaded){regionLabel}{chainLabel}...");
        SetConnectProgress(25, Loc.Of("Connecting to Veep network..."));

        return new PreparedLaunch(
            exePath,
            new[] { "run", "-c", runConfigPath },
            workDir,
            HttpProxyPort: httpPort,
            SocksPortOverride: socksPort);
    }

    private List<VeepServer> LoadServers(string workDir, string resVeep)
    {
        var candidates = new[]
        {
            Path.Combine(workDir, "servers.json"),
            Path.Combine(resVeep, "servers-seed.json")
        };

        foreach (var path in candidates)
        {
            if (File.Exists(path))
            {
                try
                {
                    var text = File.ReadAllText(path);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        var servers = JsonSerializer.Deserialize<List<VeepServer>>(text);
                        if (servers != null && servers.Count > 0)
                        {
                            return servers;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to parse Veep servers from {Path}", path);
                }
            }
        }

        return new List<VeepServer>();
    }

    protected override void Log(string line) => base.Log(LogTags.Tag(LogTags.Veep, line));
}
