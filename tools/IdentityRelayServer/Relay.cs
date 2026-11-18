using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace Se7enRelay;

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
        _lastSweep = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    private long _lastSweep;

    private readonly int _perWindow;
    private readonly long _windowSeconds;

    public bool TryTake(string clientId, out int remaining)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        if (now - _lastSweep > _windowSeconds && System.Threading.Interlocked.CompareExchange(ref _lastSweep, now, _lastSweep) == _lastSweep)
        {
            foreach (var kvp in _clients)
            {
                if (kvp.Value.ResetUnix < now) _clients.TryRemove(kvp.Key, out _);
            }
        }
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

internal static class Relay
{
    public const string ApiVersion = "v0a4471";
    public const string CloudflareBase = "https://api.cloudflareclient.com";

    public const string UserAgent = "okhttp/3.12.1";
    public const string ClientVersion = "a-6.41-2158";

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(10),
        AllowAutoRedirect = false,
    })
    {
        Timeout = TimeSpan.FromSeconds(20),
    };

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
}
