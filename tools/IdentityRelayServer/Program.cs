using System.Net;
using System.Text;
using System.Text.Json;

namespace Se7enRelay;

internal static class Program
{
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

        if (HasFlag(args, "--self-test"))
        {
            return SelfTest();
        }

        var port = ArgInt(args, "--port", 8787);
        var allowList = Arg(args, "--allow");
        var rateLimit = ArgInt(args, "--rate", 6);
        var rateWindow = ArgInt(args, "--rate-window", 86400);
        var requireSecret = HasFlag(args, "--require-secret");

        if (Arg(args, "--stock") is not null || ArgInt(args, "--prewarm", 0) > 0)
        {
            Console.Error.WriteLine(
                "--stock/--prewarm are not supported: clients register their own keypair, so "
                + "pre-generated identities cannot be used. Run the relay in LIVE mode.");
            return 2;
        }

        _clientHeader = Arg(args, "--client-header") ?? _clientHeader;
        _sharedSecret = Environment.GetEnvironmentVariable("SE7EN_RELAY_SECRET");

        if (requireSecret && string.IsNullOrEmpty(_sharedSecret))
        {
            Console.Error.WriteLine(
                "--require-secret was given but SE7EN_RELAY_SECRET is not set. Refusing to start.");
            return 2;
        }

        _limiter = new RateLimiter(Math.Max(1, rateLimit), TimeSpan.FromSeconds(Math.Max(60, rateWindow)));

        Console.WriteLine("[relay] running in LIVE mode: requests are forwarded to Cloudflare.");

        var (allowed, invalid) = ParseCidrList(allowList);
        foreach (var bad in invalid)
        {
            Console.Error.WriteLine($"[relay] ignoring invalid allow-list entry: {bad}");
        }
        if (invalid.Count > 0 && allowed.Count == 0)
        {
            Console.Error.WriteLine("[relay] the allow list has no valid range. Refusing to start open.");
            return 2;
        }
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

        return 0;
    }
    private static async Task HandleAsync(
        HttpListenerContext ctx, List<(IPAddress network, int prefix)> allowed, CancellationToken ct)
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

            if (allowed.Count > 0)
            {
                var remote = (ctx.Request.RemoteEndPoint as IPEndPoint)?.Address;
                if (remote is null || !allowed.Any(r => InRange(remote, r.network, r.prefix)))
                {
                    Reply(403, """{"error":"forbidden"}""");
                    return;
                }
            }

            if (req.HttpMethod == "GET" && req.Url!.AbsolutePath == "/v1/health")
            {
                Reply(200, """{"ok":true,"mode":"live"}""");
                return;
            }

            if (!string.IsNullOrEmpty(_sharedSecret))
            {
                var supplied = req.Headers[_clientHeader] ?? "";
                if (!FixedTimeEquals(supplied, _sharedSecret))
                {
                    Reply(401, """{"error":"bad client credential"}""");
                    return;
                }
            }

            var path = req.Url!.AbsolutePath.Trim('/');
            if (!path.StartsWith(Relay.ApiVersion, StringComparison.Ordinal))
            {
                Reply(404, """{"error":"not found"}""");
                return;
            }

            var clientId = ctx.Request.RemoteEndPoint?.Address?.ToString() ?? "unknown";

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

    private static bool FixedTimeEquals(string a, string b)
    {
        var x = Encoding.UTF8.GetBytes(a);
        var y = Encoding.UTF8.GetBytes(b);
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(x, y);
    }

    private static (List<(IPAddress network, int prefix)> Valid, List<string> Invalid) ParseCidrList(string? raw)
    {
        var result = new List<(IPAddress, int)>();
        var invalid = new List<string>();
        if (string.IsNullOrWhiteSpace(raw)) return (result, invalid);

        foreach (var token in raw.Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var t = token.Trim();
            var parts = t.Split('/', 2);
            if (parts.Length != 2
                || !IPAddress.TryParse(parts[0], out var addr)
                || !int.TryParse(parts[1], out var bits)
                || bits < 0
                || bits > addr.GetAddressBytes().Length * 8)
            {
                invalid.Add(t);
                continue;
            }

            result.Add((addr, bits));
        }

        return (result, invalid);
    }

    private static bool InRange(IPAddress ip, IPAddress network, int prefix)
    {
        var a = ip.GetAddressBytes();
        var n = network.GetAddressBytes();
        if (a.Length != n.Length) return false;

        var fullBytes = prefix / 8;
        for (var i = 0; i < fullBytes; i++)
        {
            if (a[i] != n[i]) return false;
        }

        var remainder = prefix % 8;
        if (remainder == 0) return true;

        var mask = (byte)(0xFF << (8 - remainder));
        return (a[fullBytes] & mask) == (n[fullBytes] & mask);
    }

    private static int SelfTest()
    {
        var failures = 0;
        void Check(bool ok, string what)
        {
            Console.WriteLine($"{(ok ? "[ OK ]" : "[FAIL]")} {what}");
            if (!ok) failures++;
        }

        var (valid, invalid) = ParseCidrList(
            "10.0.0.5/8, 192.168.1.0/24;2001:db8::/32 , junk/8 10.0.0.0 10.0.0.0/33");
        Check(valid.Count == 3, $"three good entries parsed (got {valid.Count})");
        Check(invalid.Count == 3, $"three bad entries rejected (got {invalid.Count})");

        Check(InRange(IPAddress.Parse("10.99.1.2"), valid[0].network, valid[0].prefix),
              "host bits in the network address are ignored");
        Check(!InRange(IPAddress.Parse("11.0.0.1"), valid[0].network, valid[0].prefix),
              "11/8 is outside 10/8");
        Check(InRange(IPAddress.Parse("192.168.1.255"), valid[1].network, valid[1].prefix),
              "/24 includes its last address");
        Check(!InRange(IPAddress.Parse("192.168.2.1"), valid[1].network, valid[1].prefix),
              "/24 excludes the next subnet");
        Check(InRange(IPAddress.Parse("2001:db8::1"), valid[2].network, valid[2].prefix),
              "an IPv6 /32 matches inside it");
        Check(!InRange(IPAddress.Parse("2001:db9::1"), valid[2].network, valid[2].prefix),
              "an IPv6 /32 excludes the next prefix");
        Check(!InRange(IPAddress.Parse("10.0.0.1"), valid[2].network, valid[2].prefix),
              "an IPv4 address never matches an IPv6 range");

        var (all, _) = ParseCidrList("0.0.0.0/0");
        Check(InRange(IPAddress.Parse("203.0.113.9"), all[0].network, all[0].prefix),
              "/0 matches every IPv4 address");

        Console.WriteLine(failures == 0
            ? "relay self-test passed"
            : $"{failures} relay self-test check(s) failed");
        return failures == 0 ? 0 : 1;
    }

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
                      [--rate=N] [--rate-window=SECONDS] [--require-secret]

        OPTIONS
          --port=N            Listen port. Default 8787.
          --allow=CIDR,...    Only serve these source ranges. Strongly recommended.
          --client-header=NAME  Header carrying the client credential. Default X-Se7en-Client.
          --rate=N            Registrations per source address per window. Default 6.
          --rate-window=SEC   Window length. Default 86400 (24h).
          --require-secret    Refuse to start unless SE7EN_RELAY_SECRET is set.
          --self-test         Check the allow-list parser and exit.

        ENVIRONMENT
          SE7EN_RELAY_SECRET  Shared secret clients must present in the client header.

        DEPLOYMENT
          Put it behind TLS (it is HTTPListener, so terminate TLS in front) and an allow
          list. An open forwarder to Cloudflare will be abused, and the fastest way to lose
          the relay is to let somebody drive unlimited registrations through your address.
          The rate limit counts the TCP source address, so behind a reverse proxy every
          client shares the proxy's address: set --rate accordingly.

        CLIENT SIDE
          Settings > Network > Identity relays, comma separated base URLs, e.g.
            https://relay1.example.com,https://relay2.example.com
        """);
    }
}