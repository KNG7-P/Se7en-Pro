using System.Net;
using System.Text;
using System.Text.Json;

namespace Se7enRelay;

/// <summary>
/// Cloudflare WARP identity relay server supporting live proxying and pre-warmed stock distribution.
/// </summary>
internal static class Program
{
    private static Stock _stock = new();
    private static RateLimiter? _limiter;
    private static string _clientHeader = "X-Se7en-Client";
    private static string? _sharedSecret;

    private static async Task<int> Main(string[] args)
    {
        if (HasFlag(args, "--help") || args.Contains("-h"))
        {
            PrintHelp();
            return 0;
        }

        var port = ArgInt(args, "--port", 8787);
        var allowList = Arg(args, "--allow");
        var rateLimit = ArgInt(args, "--rate", 6);
        var rateWindow = ArgInt(args, "--rate-window", 86400);
        var stockFile = Arg(args, "--stock");
        var prewarm = ArgInt(args, "--prewarm", 0);
        var requireSecret = HasFlag(args, "--require-secret");

        _clientHeader = Arg(args, "--client-header") ?? _clientHeader;
        _sharedSecret = Environment.GetEnvironmentVariable("SE7EN_RELAY_SECRET");

        if (requireSecret && string.IsNullOrEmpty(_sharedSecret))
        {
            Console.Error.WriteLine(
                "--require-secret was given but SE7EN_RELAY_SECRET is not set. Refusing to start.");
            return 2;
        }

        _stock = new Stock();
        _limiter = new RateLimiter(Math.Max(1, rateLimit), TimeSpan.FromSeconds(Math.Max(60, rateWindow)));

        // ---- stock ----
        if (!string.IsNullOrEmpty(stockFile) && File.Exists(stockFile))
        {
            var loaded = StockFile.Load(stockFile);
            _stock.Load(loaded);
            Console.WriteLine($"[relay] loaded {loaded.Count} identities from {stockFile}");
        }

        if (prewarm > 0)
        {
            Console.WriteLine($"[relay] pre-warming {prewarm} identities from this machine...");
            var made = 0;
            for (var i = 0; i < prewarm; i++)
            {
                try
                {
                    var item = await StockFiller.CreateAsync(CancellationToken.None);
                    if (item is null)
                    {
                        Console.Error.WriteLine("[relay] pre-warm stopped early: registration failed");
                        break;
                    }
                    _stock.Add(item);
                    made++;
                    Console.WriteLine($"[relay]   {made}/{prewarm} ready");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[relay] pre-warm failed: {ex.Message}");
                    break;
                }
            }

            Console.WriteLine($"[relay] pre-warmed {made} identities");

            if (!string.IsNullOrEmpty(stockFile) && made > 0)
            {
                StockFile.Save(stockFile, _stock);
                Console.WriteLine($"[relay] stock written to {stockFile}");
            }
        }

        if (string.IsNullOrEmpty(stockFile) && _stock.OnHand == 0)
        {
            Console.WriteLine("[relay] running in LIVE mode: requests are forwarded to Cloudflare.");
        }
        else
        {
            Console.WriteLine($"[relay] running in STOCK mode: {_stock.OnHand} identities on hand.");
        }

        // ---- listener ----
        var allowed = ParseCidrList(allowList);
        if (allowed.Count > 0)
        {
            Console.WriteLine($"[relay] allow-listing {allowed.Count} range(s)");
        }

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://+:{port}/");
        listener.Prefixes.Add($"http://localhost:{port}/");

        try
        {
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            Console.Error.WriteLine($"[relay] cannot bind port {port}: {ex.Message}");
            Console.Error.WriteLine("On Windows run this from an elevated prompt, or bind to localhost only.");
            return 3;
        }

        Console.WriteLine($"[relay] listening on :{port}");

        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        while (!cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync(); }
            catch { break; }

            _ = Task.Run(() => HandleAsync(ctx, allowed, cts.Token));
        }

        if (!string.IsNullOrEmpty(stockFile))
        {
            StockFile.Save(stockFile, _stock);
            Console.WriteLine("[relay] stock saved on shutdown");
        }

        return 0;
    }

    private static async Task HandleAsync(
        HttpListenerContext ctx, List<(IPAddress lo, IPAddress hi)> allowed, CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        try
        {
            var req = ctx.Request;
            var res = ctx.Response;

            void Reply(int status, string body, string contentType = "application/json")
            {
                var bytes = Encoding.UTF8.GetBytes(body);
                res.StatusCode = status;
                res.ContentType = contentType;
                res.ContentLength64 = bytes.Length;
                res.OutputStream.Write(bytes);
                res.OutputStream.Close();
            }

            // ---- allow list, before anything else ----
            if (allowed.Count > 0)
            {
                var remote = (ctx.Request.RemoteEndPoint as IPEndPoint)?.Address;
                if (remote is null || !allowed.Any(r => InRange(remote, r.lo, r.hi)))
                {
                    Reply(403, """{"error":"forbidden"}""");
                    return;
                }
            }

            // ---- health ----
            if (req.HttpMethod == "GET" && req.Url!.AbsolutePath == "/v1/health")
            {
                var s = _stock.Stats();
                Reply(200, JsonSerializer.Serialize(new
                {
                    ok = true,
                    mode = _stock.OnHand > 0 || _stock.Issued > 0 ? "stock" : "live",
                    onHand = s.OnHand,
                    reserved = s.Reserved,
                    issued = s.Issued,
                }));
                return;
            }

            // ---- shared secret, if configured ----
            if (!string.IsNullOrEmpty(_sharedSecret))
            {
                var supplied = req.Headers[_clientHeader] ?? "";
                if (!FixedTimeEquals(supplied, _sharedSecret))
                {
                    Reply(401, """{"error":"bad client credential"}""");
                    return;
                }
            }

            // ---- route ----
            var path = req.Url!.AbsolutePath.Trim('/');
            if (!path.StartsWith(Relay.ApiVersion, StringComparison.Ordinal))
            {
                Reply(404, """{"error":"not found"}""");
                return;
            }

            var clientId = req.Headers[_clientHeader]
                           ?? req.UserAgent
                           ?? ctx.Request.RemoteEndPoint?.ToString()
                           ?? "unknown";

            if (_limiter is not null && !_limiter.TryTake(clientId, out var remaining))
            {
                res.AddHeader("X-Relay-Remaining", "0");
                Reply(429, """{"error":"rate limited; this relay allows one identity per client per window"}""");
                return;
            }

            var body = "";
            using (var reader = new StreamReader(req.InputStream, Encoding.UTF8))
            {
                body = await reader.ReadToEndAsync(ct);
            }

            var suffix = path[(Relay.ApiVersion.Length + 1)..];

            // ---- stock mode: hand out a pre-generated identity ----
            if (_stock.OnHand > 0 || _stock.Issued > 0)
            {
                if (suffix.Equals("reg", StringComparison.OrdinalIgnoreCase)
                    && req.HttpMethod == "POST")
                {
                    if (!_stock.TryReserve(out var item) || item is null)
                    {
                        Reply(503, """{"error":"relay out of stock"}""");
                        return;
                    }

                    // The client's public key is in the body, but a stocked identity was
                    // registered against the key generated when it was created. Say so
                    // plainly rather than returning a mismatch that fails at handshake time
                    // with a far less obvious message.
                    Reply(409, JsonSerializer.Serialize(new
                    {
                        error = "relay has pre-generated stock; this client must use LIVE mode " +
                                "(remove identityRelayUrls) or the relay must run with no stock",
                    }));
                    _stock.Unreserve();
                    return;
                }

                if (suffix.StartsWith("reg/", StringComparison.OrdinalIgnoreCase)
                    && req.HttpMethod == "PATCH")
                {
                    Reply(409, """{"error":"MASQUE key enrolment needs LIVE mode"}""");
                    return;
                }
            }

            // ---- live mode: forward to Cloudflare ----
            var bearer = req.Headers["Authorization"] is { Length: > 7 } a
                ? a["Bearer ".Length..].Trim()
                : null;

            try
            {
                var (status, text) = await Relay.ForwardAsync(
                    new HttpMethod(req.HttpMethod), suffix, body, bearer, ct);

                res.AddHeader("X-Relay-Forwarded", "cloudflare");
                Reply(status, text, "application/json");
            }
            catch (Exception ex)
            {
                Reply(502, JsonSerializer.Serialize(new { error = "upstream failed", detail = ex.Message }));
            }
        }
        catch (Exception ex)
        {
            try
            {
                var bytes = Encoding.UTF8.GetBytes(
                    JsonSerializer.Serialize(new { error = "relay failure", detail = ex.Message }));
                ctx.Response.StatusCode = 500;
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.OutputStream.Write(bytes);
                ctx.Response.OutputStream.Close();
            }
            catch { }
        }
        finally
        {
            _ = (DateTimeOffset.UtcNow - started).TotalMilliseconds;
        }
    }

    // ---------------------------------------------------------------- helpers

    private static bool FixedTimeEquals(string a, string b)
    {
        var x = Encoding.UTF8.GetBytes(a);
        var y = Encoding.UTF8.GetBytes(b);
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(x, y);
    }

    private static List<(IPAddress lo, IPAddress hi)> ParseCidrList(string? raw)
    {
        var result = new List<(IPAddress, IPAddress)>();
        if (string.IsNullOrWhiteSpace(raw)) return result;

        foreach (var token in raw.Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var t = token.Trim();
            if (!t.Contains('/')) continue;
            var parts = t.Split('/', 2);
            if (!IPAddress.TryParse(parts[0], out var addr)) continue;
            if (!int.TryParse(parts[1], out var bits)) continue;

            var mask = bits == 0
                ? 0
                : ~((1 << (32 - bits)) - 1);
            var loBytes = addr.GetAddressBytes();
            var maskBytes = BitConverter.GetBytes(mask);
            var lo = new IPAddress(loBytes.Select((b, i) => (byte)(b & maskBytes[i])).ToArray());
            var hi = new IPAddress(loBytes.Select((b, i) => (byte)(b | ~maskBytes[i])).ToArray());
            result.Add((lo, hi));
        }

        return result;
    }

    private static bool InRange(IPAddress ip, IPAddress lo, IPAddress hi)
    {
        var a = ip.GetAddressBytes();
        var l = lo.GetAddressBytes();
        var h = hi.GetAddressBytes();
        if (a.Length != l.Length) return false;

        for (var i = 0; i < a.Length; i++)
        {
            if (a[i] < l[i]) return false;
            if (a[i] > h[i]) return false;
        }
        return true;
    }

    /// <summary>
    /// Parses a named command-line argument supporting both space and equals syntax.
    /// </summary>
    private static string? Arg(string[] args, string name)
    {
        var prefix = name + "=";
        foreach (var a in args)
        {
            if (a.StartsWith(prefix, StringComparison.Ordinal)) return a[prefix.Length..];
        }

        var i = Array.IndexOf(args, name);
        if (i < 0 || i + 1 >= args.Length) return null;
        return args[i + 1];
    }

    private static int ArgInt(string[] args, string name, int fallback)
    {
        var raw = Arg(args, name);
        return int.TryParse(raw, out var v) ? v : fallback;
    }

    private static bool HasFlag(string[] args, string name) =>
        args.Any(a => a.Equals(name, StringComparison.Ordinal)
                      || a.StartsWith(name + "=", StringComparison.Ordinal));

    private static void PrintHelp()
    {
        Console.WriteLine("""
        se7en-relay — a bootstrap relay for Cloudflare WARP identity registration.

        The client generates its own keypair and sends only the public key, so this relay
        never handles a private key in LIVE mode. It forwards the request and returns
        Cloudflare's reply verbatim.

        USAGE
          se7en-relay [--port=N] [--allow=CIDR,CIDR] [--client-header=NAME]
                      [--rate=N] [--rate-window=SECONDS] [--prewarm=N]
                      [--stock=FILE] [--require-secret]

        OPTIONS
          --port=N            Listen port. Default 8787.
          --allow=CIDR,...    Only serve these source ranges. Strongly recommended.
          --client-header=NAME  Header carrying the client credential. Default X-Se7en-Client.
          --rate=N            Identities per client per window. Default 6.
          --rate-window=SEC   Window length. Default 86400 (24h).
          --prewarm=N         Generate N identities from this machine before serving.
          --stock=FILE        Persist stock across restarts.
          --require-secret    Refuse to start unless SE7EN_RELAY_SECRET is set.

        ENVIRONMENT
          SE7EN_RELAY_SECRET  Shared secret clients must present in the client header.

        DEPLOYMENT
          Put it behind TLS (it is HTTPListener, so terminate TLS in front) and an allow
          list. An open forwarder to Cloudflare will be abused, and the fastest way to lose
          the relay is to let somebody drive unlimited registrations through your address.

        CLIENT SIDE
          Settings > Network > Identity relays, comma separated base URLs, e.g.
            https://relay1.example.com,https://relay2.example.com
        """);
    }
}