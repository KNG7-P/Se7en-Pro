using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Se7enRelay;

/// <summary>One provisioned identity held by the relay.</summary>
internal sealed record StockedIdentity(
    string DeviceId,
    string Token,
    string PrivateKeyB64,
    string PeerPublicKeyB64,
    string Ipv4,
    string Ipv6,
    string ClientId,
    string GatewayProxy,
    string AssignedEndpoint);

/// <summary>
/// Thread-safe inventory of pre-generated Cloudflare WARP identities.
/// </summary>
internal sealed class Stock
{
    private readonly ConcurrentQueue<StockedIdentity> _queue = new();
    private readonly object _reserveLock = new();
    private readonly List<StockedIdentity> _reserved = new();
    private int _issued;

    public int OnHand => _queue.Count;

    public int Reserved => _reserved.Count;

    public int Issued => _issued;

    public void Load(IEnumerable<StockedIdentity> items)
    {
        foreach (var i in items) _queue.Enqueue(i);
    }

    public void Add(StockedIdentity item) => _queue.Enqueue(item);

    /// <summary>
    /// Reserves an identity for pending client delivery.
    /// </summary>
    public bool TryReserve(out StockedIdentity? item)
    {
        item = null;
        if (!_queue.TryDequeue(out var found)) return false;

        lock (_reserveLock) { _reserved.Add(found); }
        item = found;
        return true;
    }

    /// <summary>Confirms a reserved identity was actually delivered.</summary>
    public void Commit()
    {
        lock (_reserveLock)
        {
            if (_reserved.Count == 0) return;
            _reserved.RemoveAt(_reserved.Count - 1);
            Interlocked.Increment(ref _issued);
        }
    }

    /// <summary>
    /// Returns an uncommitted reservation back to available stock.
    /// </summary>
    public void Unreserve()
    {
        lock (_reserveLock)
        {
            if (_reserved.Count == 0) return;
            var back = _reserved[^1];
            _reserved.RemoveAt(_reserved.Count - 1);
            _queue.Enqueue(back);
        }
    }

    public StockStats Stats() => new(OnHand, Reserved, Issued);
}

internal sealed record StockStats(int OnHand, int Reserved, int Issued);

/// <summary>
/// Client rate limiter based on sliding time windows.
/// </summary>
internal sealed class RateLimiter
{
    private readonly ConcurrentDictionary<string, Window> _clients = new();

    private sealed class Window
    {
        public int Count;
        public long ResetUnix;
    }

    public RateLimiter(int perWindow, TimeSpan window)
    {
        _perWindow = perWindow;
        _windowSeconds = (long)window.TotalSeconds;
    }

    private readonly int _perWindow;
    private readonly long _windowSeconds;

    public bool TryTake(string clientId, out int remaining)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var w = _clients.GetOrAdd(clientId, _ => new Window { ResetUnix = now + _windowSeconds });

        lock (w)
        {
            if (now >= w.ResetUnix)
            {
                w.Count = 0;
                w.ResetUnix = now + _windowSeconds;
            }

            if (w.Count >= _perWindow)
            {
                remaining = 0;
                return false;
            }

            w.Count++;
            remaining = _perWindow - w.Count;
            return true;
        }
    }
}

/// <summary>
/// Cloudflare API proxy and identity parsing helpers.
/// </summary>
internal static class Relay
{
    public const string ApiVersion = "v0a4471";
    public const string CloudflareBase = "https://api.cloudflareclient.com";

    public const string UserAgent = "okhttp/3.12.1";
    public const string ClientVersion = "a-6.41-2158";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(10),
        AllowAutoRedirect = false,
    })
    {
        Timeout = TimeSpan.FromSeconds(20),
    };

    /// <summary>
    /// Forwards an HTTP registration request directly to Cloudflare.
    /// </summary>
    public static async Task<(int Status, string Body)> ForwardAsync(
        HttpMethod method,
        string path,
        string body,
        string? bearer,
        CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, $"{CloudflareBase}/{ApiVersion}/{path}");
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        req.Headers.TryAddWithoutValidation("CF-Client-Version", ClientVersion);

        if (bearer is not null)
        {
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {bearer}");
        }

        using var resp = await Http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        return ((int)resp.StatusCode, text);
    }

    /// <summary>
    /// Parses Cloudflare registration JSON into a stocked identity.
    /// </summary>
    public static StockedIdentity? ParseRegistration(string json, string privateKeyB64)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var id = root.GetProperty("id").GetString();
            var token = root.GetProperty("token").GetString();
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(token)) return null;

            var config = root.GetProperty("config");
            var addresses = config.GetProperty("interface").GetProperty("addresses");

            var peer = config.GetProperty("peers")[0];
            var peerPub = peer.GetProperty("public_key").GetString() ?? "";
            if (peerPub.Length == 0) return null;

            return new StockedIdentity(
                id,
                token,
                privateKeyB64,
                peerPub,
                addresses.GetProperty("v4").GetString() ?? "172.16.0.2",
                addresses.GetProperty("v6").GetString() ?? "",
                config.TryGetProperty("client_id", out var c) ? c.GetString() ?? "" : "",
                root.TryGetProperty("services", out var s) && s.TryGetProperty("http_proxy", out var h)
                    ? h.GetString() ?? "" : "",
                peer.GetProperty("endpoint").GetProperty("v4").GetString() ?? "162.159.192.1:2408");
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Serializes a stocked identity into Cloudflare registration JSON format.
    /// </summary>
    public static string RenderAsCloudflareReply(StockedIdentity s) =>
        JsonSerializer.Serialize(new
        {
            id = s.DeviceId,
            token = s.Token,
            type = "Device",
            config = new
            {
                client_id = s.ClientId,

                // "interface" is a C# keyword, and Cloudflare's own field name has to be
                // reproduced exactly or the client will not find it.
                @interface = new
                {
                    addresses = new { v4 = s.Ipv4, v6 = s.Ipv6 },
                },
                peers = new[]
                {
                    new
                    {
                        public_key = s.PeerPublicKeyB64,
                        endpoint = new { v4 = s.AssignedEndpoint },
                    },
                },
            },
            services = new { http_proxy = s.GatewayProxy },
        }, JsonOpts);
}