using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;
using Se7enPro.Models;

namespace Se7enPro.Services;

public static class V2RayLinkParser
{
    
    
    
    
    private static readonly HttpClient _httpClient = CreateClient();

        private const int MaxSubscriptionBytes = 8 * 1024 * 1024;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "v2rayNG/1.9.5 (Android 13)");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "text/plain, application/json, */*; q=0.5");
        return client;
    }

        public sealed class ImportResult
    {
        public List<V2RayConfigEntry> Entries { get; init; } = new();
        public string? Error { get; init; }
    }

    public static async Task<ImportResult> ParseInputAsync(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return new ImportResult();

        var entries = new List<V2RayConfigEntry>();
        string? error = null;
        var index = 1;

        
        
        
        foreach (var rawLine in input.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("//")) continue;

            if (IsHttpUrl(line))
            {
                var (fetched, fetchError) = await FetchSubscriptionAsync(line);
                if (fetched.Count > 0)
                {
                    foreach (var entry in fetched)
                    {
                        entry.Name = string.IsNullOrEmpty(entry.Name) ? $"Node {index}" : entry.Name;
                        entries.Add(entry);
                        index++;
                    }
                }
                else if (error is null)
                {
                    error = fetchError;
                }
                continue;
            }

            
            
            
            
            
            V2RayConfigEntry? parsed = null;
            try
            {
                parsed = ParseSingleLink(line, index);
            }
            catch (Exception ex)
            {
                error ??= string.Format(
                    Loc.Of("Skipped a malformed node: {0}"), ex.Message);
            }

            if (parsed is not null)
            {
                entries.Add(parsed);
                index++;
            }
        }

        return new ImportResult { Entries = entries, Error = error };
    }

    private static bool IsHttpUrl(string candidate)
    {
        
        
        
        if (!candidate.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !candidate.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return false;

        return Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    private static async Task<(List<V2RayConfigEntry> Entries, string? Error)> FetchSubscriptionAsync(string url)
    {
        string content;
        try
        {
            
            
            
            
            
            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode)
            {
                return (new List<V2RayConfigEntry>(), string.Format(
                    Loc.Of("The subscription link answered {0}."),
                    (int)response.StatusCode + " " + response.ReasonPhrase));
            }

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var buffer = new char[8192];
            var sb = new StringBuilder();
            int read;
            while ((read = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                if (sb.Length + read > MaxSubscriptionBytes)
                {
                    return (new List<V2RayConfigEntry>(), Loc.Of(
                        "The subscription response is too large to be a node list."));
                }
                sb.Append(buffer, 0, read);
            }
            content = sb.ToString();
        }
        catch (TaskCanceledException)
        {
            return (new List<V2RayConfigEntry>(), Loc.Of("The subscription link timed out."));
        }
        catch (Exception ex)
        {
            return (new List<V2RayConfigEntry>(), string.Format(
                Loc.Of("Could not download the subscription link: {0}"), ex.Message));
        }

        var body = content.Trim();
        if (body.Length == 0)
            return (new List<V2RayConfigEntry>(), Loc.Of("The subscription link returned nothing."));

        
        
        if (body.StartsWith("<", StringComparison.Ordinal) ||
            body.Contains("<html", StringComparison.OrdinalIgnoreCase))
            return (new List<V2RayConfigEntry>(), Loc.Of("The subscription link returned a web page instead of nodes."));

        return (ParseText(body), null);
    }

    public static List<V2RayConfigEntry> ParseText(string text, int startIndex = 1)
        => ParseText(text, startIndex, allowBase64Retry: true);

    private static List<V2RayConfigEntry> ParseText(string text, int startIndex, bool allowBase64Retry)
    {
        var result = new List<V2RayConfigEntry>();
        if (string.IsNullOrWhiteSpace(text)) return result;

        var raw = text.Trim();

        
        if (!raw.Contains('\n') && !raw.Contains("://") && TryBase64Decode(raw, out var decodedBlob))
        {
            raw = decodedBlob;
        }

        var lines = raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        var index = startIndex;

        foreach (var line in lines)
        {
            var l = line.Trim();
            if (string.IsNullOrEmpty(l) || l.StartsWith("#") || l.StartsWith("//")) continue;

            try
            {
                var entry = ParseSingleLink(l, index);
                if (entry != null)
                {
                    result.Add(entry);
                    index++;
                }
            }
            catch
            {
                
            }
        }

        if (result.Count == 0 && allowBase64Retry)
        {
            
            
            var joined = string.Concat(lines.Select(l => l.Trim()));
            if (joined != raw && TryBase64Decode(joined, out var unwrapped) && unwrapped != raw)
                return ParseText(unwrapped, startIndex, allowBase64Retry: false);
        }

        return result;
    }

    public static V2RayConfigEntry? ParseSingleLink(string link, int index = 1)
    {
        if (string.IsNullOrWhiteSpace(link)) return null;
        var trimmed = link.Trim();

        if (trimmed.StartsWith("vless://", StringComparison.OrdinalIgnoreCase))
            return ParseVless(trimmed, index);
        if (trimmed.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase))
            return ParseVmess(trimmed, index);
        if (trimmed.StartsWith("trojan://", StringComparison.OrdinalIgnoreCase))
            return ParseTrojan(trimmed, index);
        if (trimmed.StartsWith("ss://", StringComparison.OrdinalIgnoreCase))
            return ParseShadowsocks(trimmed, index);
        if (trimmed.StartsWith("hysteria2://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("hy2://", StringComparison.OrdinalIgnoreCase))
            return ParseHysteria2(trimmed, index);

        return null;
    }

    private static V2RayConfigEntry? ParseVless(string uriString, int index)
    {
        if (!Uri.TryCreate(uriString, UriKind.Absolute, out var uri)) return null;

        var name = !string.IsNullOrEmpty(uri.Fragment)
            ? HttpUtility.UrlDecode(uri.Fragment.TrimStart('#'))
            : $"VLESS Node {index}";

        var query = HttpUtility.ParseQueryString(uri.Query);
        var pbk = query["pbk"] ?? query["publicKey"];
        var sid = query["sid"] ?? query["shortId"];
        var flow = query["flow"];
        var sec = query["security"]?.ToLowerInvariant() ?? (string.IsNullOrEmpty(pbk) ? "none" : "reality");
        var net = query["type"]?.ToLowerInvariant() ?? query["headerType"]?.ToLowerInvariant() ?? "tcp";
        var sni = query["sni"] ?? query["host"];
        var path = query["path"] ?? query["serviceName"];

        return new V2RayConfigEntry
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Name = name,
            Protocol = "vless",
            Address = uri.Host,
            Port = uri.Port > 0 ? uri.Port : 443,
            UserId = Uri.UnescapeDataString(uri.UserInfo),
            Security = sec,
            Network = net == "splithttp" ? "xhttp" : net,
            Sni = sni,
            Host = sni,
            Path = path,
            PublicKey = pbk,
            ShortId = sid,
            Flow = flow,
            EnableFragment = true,
        };
    }

    private static V2RayConfigEntry? ParseTrojan(string uriString, int index)
    {
        if (!Uri.TryCreate(uriString, UriKind.Absolute, out var uri)) return null;

        var name = !string.IsNullOrEmpty(uri.Fragment)
            ? HttpUtility.UrlDecode(uri.Fragment.TrimStart('#'))
            : $"Trojan Node {index}";

        var query = HttpUtility.ParseQueryString(uri.Query);
        var sni = query["sni"] ?? query["host"];
        var net = query["type"]?.ToLowerInvariant() ?? "tcp";
        var path = query["path"] ?? query["serviceName"];

        return new V2RayConfigEntry
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Name = name,
            Protocol = "trojan",
            Address = uri.Host,
            Port = uri.Port > 0 ? uri.Port : 443,
            UserId = Uri.UnescapeDataString(uri.UserInfo),
            Security = "tls",
            Network = net,
            Sni = sni,
            Host = sni,
            Path = path,
            EnableFragment = true,
        };
    }

    private static V2RayConfigEntry? ParseHysteria2(string uriString, int index)
    {
        if (!Uri.TryCreate(uriString, UriKind.Absolute, out var uri)) return null;

        var name = !string.IsNullOrEmpty(uri.Fragment)
            ? HttpUtility.UrlDecode(uri.Fragment.TrimStart('#'))
            : $"Hysteria2 Node {index}";

        var query = HttpUtility.ParseQueryString(uri.Query);
        var sni = query["sni"] ?? query["host"];

        return new V2RayConfigEntry
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Name = name,
            Protocol = "hysteria2",
            Address = uri.Host,
            Port = uri.Port > 0 ? uri.Port : 443,
            UserId = Uri.UnescapeDataString(uri.UserInfo),
            Security = "tls",
            Network = "udp",
            Sni = sni,
            Host = sni,
        };
    }

    private static V2RayConfigEntry? ParseVmess(string link, int index)
    {
        var b64 = link[8..].Trim();
        if (!TryBase64Decode(b64, out var jsonStr)) return null;

        using var doc = JsonDocument.Parse(jsonStr);
        var root = doc.RootElement;

        var name = root.TryGetProperty("ps", out var ps) ? ps.GetString() : $"VMess Node {index}";
        var add = root.TryGetProperty("add", out var a) ? a.GetString() : "127.0.0.1";
        var port = 443;
        if (root.TryGetProperty("port", out var pElem))
        {
            int rawPort;
            if (pElem.ValueKind == JsonValueKind.Number && pElem.TryGetInt32(out rawPort))
                port = rawPort;
            else if (int.TryParse(pElem.GetString(), out var parsedPort))
                port = parsedPort;
        }

        
        
        
        if (port is < 1 or > 65535) return null;

        var id = root.TryGetProperty("id", out var idElem) ? idElem.GetString() : "";
        var net = root.TryGetProperty("net", out var netElem) ? netElem.GetString()?.ToLowerInvariant() : "tcp";
        var tls = root.TryGetProperty("tls", out var tlsElem) ? tlsElem.GetString()?.ToLowerInvariant() : "none";
        var path = root.TryGetProperty("path", out var pathElem) ? pathElem.GetString() : null;
        var host = root.TryGetProperty("host", out var hostElem) ? hostElem.GetString() :
                   (root.TryGetProperty("sni", out var sniElem) ? sniElem.GetString() : null);

        return new V2RayConfigEntry
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Name = string.IsNullOrWhiteSpace(name) ? $"VMess Node {index}" : name,
            Protocol = "vmess",
            Address = add ?? "127.0.0.1",
            Port = port,
            UserId = id ?? "",
            Security = tls == "tls" ? "tls" : "none",
            Network = net ?? "tcp",
            Path = path,
            Host = host,
            Sni = host,
        };
    }

    private static V2RayConfigEntry? ParseShadowsocks(string link, int index)
    {
        var raw = link[5..];
        var name = $"Shadowsocks Node {index}";
        var hashIdx = raw.IndexOf('#');
        if (hashIdx >= 0)
        {
            name = HttpUtility.UrlDecode(raw[(hashIdx + 1)..].Trim());
            raw = raw[..hashIdx];
        }

        string method = "2022-blake3-aes-128-gcm";
        string password = "";
        string server = "127.0.0.1";
        int port = 8388;

        if (raw.Contains('@'))
        {
            var atIdx = raw.LastIndexOf('@');
            var userB64 = raw[..atIdx];
            var hostPart = raw[(atIdx + 1)..];

            if (TryBase64Decode(userB64, out var decodedUser))
            {
                var parts = decodedUser.Split(':', 2);
                if (parts.Length == 2)
                {
                    method = parts[0];
                    password = parts[1];
                }
                else
                {
                    password = decodedUser;
                }
            }
            else
            {
                var parts = userB64.Split(':', 2);
                if (parts.Length == 2)
                {
                    method = parts[0];
                    password = parts[1];
                }
                else
                {
                    password = userB64;
                }
            }

            var hostSplit = hostPart.Split(':', 2);
            server = hostSplit[0];
            if (hostSplit.Length > 1 && int.TryParse(hostSplit[1].Split('?')[0], out var p))
                port = p;
        }
        else if (TryBase64Decode(raw, out var decodedAll))
        {
            if (decodedAll.Contains('@'))
            {
                var atIdx = decodedAll.LastIndexOf('@');
                var userPart = decodedAll[..atIdx];
                var hostPart = decodedAll[(atIdx + 1)..];

                var uParts = userPart.Split(':', 2);
                if (uParts.Length == 2)
                {
                    method = uParts[0];
                    password = uParts[1];
                }

                var hParts = hostPart.Split(':', 2);
                server = hParts[0];
                if (hParts.Length > 1 && int.TryParse(hParts[1].Split('?')[0], out var p))
                    port = p;
            }
        }

        if (port is < 1 or > 65535) return null;

        return new V2RayConfigEntry
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Name = name,
            Protocol = "shadowsocks",
            Address = server,
            Port = port,
            UserId = password,
            Security = method,
            Network = "tcp",
        };
    }

    private static bool TryBase64Decode(string input, out string decoded)
    {
        decoded = "";
        if (string.IsNullOrWhiteSpace(input)) return false;

        try
        {
            var padded = input.Trim().Replace('-', '+').Replace('_', '/');
            switch (padded.Length % 4)
            {
                case 2: padded += "=="; break;
                case 3: padded += "="; break;
            }
            var bytes = Convert.FromBase64String(padded);
            decoded = Encoding.UTF8.GetString(bytes);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
