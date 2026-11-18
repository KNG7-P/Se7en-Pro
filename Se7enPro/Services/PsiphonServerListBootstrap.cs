using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Se7enPro.Services;

internal static class PsiphonServerListBootstrap
{
    private const long PopulatedBoltDbThresholdBytes = 65_536;
    private const long ValidCacheThresholdBytes = 4_096;
    private static readonly TimeSpan CachedServerListMaxAge = TimeSpan.FromDays(7);

    internal static async Task<string?> ResolveOrFetchAsync(
        string? workDir,
        string? upstreamProxy,
        string? remoteServerListUrlsJson,
        Action<string>? log,
        ILogger? logger,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workDir)) return null;

        var cachedPath = Path.Combine(workDir, "cached_server_entries.txt");
        if (IsFreshCachedServerList(cachedPath))
            return cachedPath;

        var boltDb = Path.Combine(workDir, "data", "psiphon.boltdb");
        try
        {
            if (File.Exists(boltDb) && new FileInfo(boltDb).Length >= PopulatedBoltDbThresholdBytes)
                return File.Exists(cachedPath) && new FileInfo(cachedPath).Length >= ValidCacheThresholdBytes
                    ? cachedPath
                    : null;
        }
        catch { }

        var urls = ParseRemoteServerListUrls(remoteServerListUrlsJson);
        if (urls.Count == 0) return null;

        log?.Invoke("Pre-seeding initial Psiphon server list...");

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

            using var handler = CreateHttpHandler(upstreamProxy);
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };

            foreach (var url in urls)
            {
                if (timeoutCts.IsCancellationRequested) break;
                try
                {
                    var rawBytes = await http.GetByteArrayAsync(url, timeoutCts.Token).ConfigureAwait(false);
                    var payload = ExtractServerEntriesPayload(rawBytes);
                    if (string.IsNullOrWhiteSpace(payload)) continue;

                    var entryCount = payload.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
                    var tmpPath = cachedPath + ".tmp";
                    await File.WriteAllTextAsync(tmpPath, payload, new UTF8Encoding(false), timeoutCts.Token)
                        .ConfigureAwait(false);
                    File.Move(tmpPath, cachedPath, overwrite: true);
                    log?.Invoke($"Loaded {entryCount} Psiphon server entries into local bootstrap cache.");
                    return cachedPath;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return null;
                }
                catch (Exception ex)
                {
                    logger?.LogDebug(ex, "Remote server list pre-seed failed for one mirror");
                }
            }
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Remote server list pre-seed aborted");
        }

        return File.Exists(cachedPath) && new FileInfo(cachedPath).Length >= ValidCacheThresholdBytes
            ? cachedPath
            : null;
    }

    internal static bool IsFreshCachedServerList(string cachedPath)
    {
        try
        {
            if (!File.Exists(cachedPath)) return false;
            var info = new FileInfo(cachedPath);
            return info.Length >= ValidCacheThresholdBytes
                && (DateTime.UtcNow - info.LastWriteTimeUtc) < CachedServerListMaxAge;
        }
        catch
        {
            return false;
        }
    }

    private static SocketsHttpHandler CreateHttpHandler(string? upstreamProxy)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(4),
            PooledConnectionLifetime = TimeSpan.FromSeconds(1),
        };
        if (!string.IsNullOrWhiteSpace(upstreamProxy))
        {
            handler.Proxy = new WebProxy(upstreamProxy.Trim());
            handler.UseProxy = true;
        }
        else
        {
            handler.UseProxy = false;
        }
        return handler;
    }

    internal static IReadOnlyList<string> ParseRemoteServerListUrls(string? urlsJson)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(urlsJson)) return result;

        try
        {
            if (JsonNode.Parse(urlsJson) is not JsonArray arr) return result;
            foreach (var item in arr)
            {
                var raw = item?["URL"]?.GetValue<string>()?.Trim();
                if (string.IsNullOrWhiteSpace(raw)) continue;

                var decoded = TryDecodeUrl(raw);
                if (!string.IsNullOrWhiteSpace(decoded)
                    && Uri.TryCreate(decoded, UriKind.Absolute, out var uri)
                    && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                {
                    result.Add(decoded);
                }
            }
        }
        catch { }

        return result;
    }

    private static string? TryDecodeUrl(string raw)
    {
        if (raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return raw;

        try
        {
            var bytes = Convert.FromBase64String(raw);
            return Encoding.UTF8.GetString(bytes).Trim();
        }
        catch
        {
            return null;
        }
    }

    internal static string? ExtractServerEntriesPayload(byte[] rawBytes)
    {
        if (rawBytes is null || rawBytes.Length == 0) return null;

        var text = DecompressBytes(rawBytes);
        if (string.IsNullOrWhiteSpace(text)) return null;

        try
        {
            var trimmed = text.TrimStart();
            if (trimmed.StartsWith("{", StringComparison.Ordinal))
            {
                var data = JsonNode.Parse(trimmed)?["data"]?.GetValue<string>();
                return string.IsNullOrWhiteSpace(data) ? null : data;
            }

            return trimmed.Contains('\n') || trimmed.Length > 64 ? trimmed : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? DecompressBytes(byte[] rawBytes)
    {
        try
        {
            if (rawBytes.Length >= 2 && rawBytes[0] == 0x78)
            {
                using var ms = new MemoryStream(rawBytes);
                using var zl = new ZLibStream(ms, CompressionMode.Decompress);
                using var sr = new StreamReader(zl, Encoding.UTF8);
                return sr.ReadToEnd();
            }

            if (rawBytes.Length >= 2 && rawBytes[0] == 0x1F && rawBytes[1] == 0x8B)
            {
                using var ms = new MemoryStream(rawBytes);
                using var gz = new GZipStream(ms, CompressionMode.Decompress);
                using var sr = new StreamReader(gz, Encoding.UTF8);
                return sr.ReadToEnd();
            }

            return Encoding.UTF8.GetString(rawBytes);
        }
        catch
        {
            return null;
        }
    }
}
