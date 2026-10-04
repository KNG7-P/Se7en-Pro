using System;
using System.Linq;
using Se7enPro.Services;

namespace VerifyCriticalFixes;

/// <summary>
/// Unit tests for bootstrap relay parsing and fallback routing order.
/// </summary>
internal static class IdentityRelayTests
{
    internal static int Run()
    {
        Program.Section("Identity relay — list parsing");
        var failures = TestParsing();

        Program.Section("Identity relay — route ordering");
        failures += TestOrdering();

        return failures;
    }

    private static int TestParsing()
    {
        var f = 0;

        Check(IdentityRelay.ParseRelays(null).Count == 0, "null yields no relays");
        Check(IdentityRelay.ParseRelays("").Count == 0, "empty yields no relays");
        Check(IdentityRelay.ParseRelays("   ").Count == 0, "whitespace yields no relays");

        var one = IdentityRelay.ParseRelays("https://relay.example.com");
        Check(one.Count == 1 && one[0] == "https://relay.example.com", "one relay parses", string.Join(",", one));

        // A trailing slash would otherwise produce a doubled path when the Cloudflare
        // suffix is appended.
        var slash = IdentityRelay.ParseRelays("https://relay.example.com/");
        Check(slash.Count == 1 && slash[0] == "https://relay.example.com",
              "a trailing slash is trimmed, not doubled into the path", string.Join(",", slash));

        var many = IdentityRelay.ParseRelays("https://a.example.com, https://b.example.com;https://c.example.com\nhttps://d.example.com");
        Check(many.Count == 4, "comma, semicolon and newline all separate",
              many.Count.ToString());

        // Duplicates would make the client retry the same dead relay twice.
        var dupes = IdentityRelay.ParseRelays("https://a.example.com, https://a.example.com/");
        Check(dupes.Count == 1, "duplicates collapse", string.Join(",", dupes));

        // Rejections: these are the shapes that would produce a request going somewhere
        // unexpected rather than an obvious failure.
        Check(IdentityRelay.ParseRelays("relay.example.com").Count == 0,
              "a bare host with no scheme is rejected");
        Check(IdentityRelay.ParseRelays("ftp://relay.example.com").Count == 0,
              "a non-HTTP scheme is rejected");
        Check(IdentityRelay.ParseRelays("https://relay.example.com/some/path").Count == 0,
              "a base URL carrying a path is rejected (it would be doubled)");
        Check(IdentityRelay.ParseRelays("https://relay.example.com, nonsense").Count == 1,
              "a bad entry does not take the good ones down with it");

        Check(IdentityRelay.ParseRelays("http://localhost:8787").Count == 1,
              "plain http is allowed, for a relay on loopback during testing");

        return f;
    }

    private static int TestOrdering()
    {
        var f = 0;

        // No relays, no tunnel: just direct.
        var directOnly = IdentityRelay.BuildRoutes(
            directProxy: null, relayList: "", tunnelProxy: null, tunnelLabel: "tunnel");
        Check(directOnly.Count == 1, "with nothing configured there is one route",
              directOnly.Count.ToString());
        Check(directOnly[0].IsDirect, "and it is direct");

        // Relays before the tunnel. A relay is an ordinary HTTPS hop; a tunnel is a whole
        // extra connection to stand up. That ordering is the whole reason relays are cheap.
        var both = IdentityRelay.BuildRoutes(
            directProxy: null,
            relayList: "https://r1.example.com,https://r2.example.com",
            tunnelProxy: new System.Net.WebProxy("socks5://127.0.0.1:1080"),
            tunnelLabel: "SHARD");

        Check(both.Count == 4, "direct plus two relays plus the tunnel", both.Count.ToString());
        Check(both[0].IsDirect, "direct is first: it costs nothing");
        Check(both[1].IsRelay && both[2].IsRelay, "the relays come next");
        Check(both[1].RelayBaseUrl == "https://r1.example.com", "relays keep the configured order",
              both[1].RelayBaseUrl ?? "(null)");
        Check(both[3].Label == "SHARD", "the tunnel is last", both[3].Label);
        Check(both[3].Proxy is not null && !both[3].IsRelay, "the tunnel route uses a proxy, not a relay");

        // A relay route must never carry a proxy: routing the relay through SOCKS would
        // defeat the point of having a reachable-from-here relay.
        Check(both.Skip(1).Take(2).All(r => r.Proxy is null),
              "relay routes never carry a proxy");

        // Every relay gets its own label, so a failure names which one died.
        var labels = both.Where(r => r.IsRelay).Select(r => r.Label).ToList();
        Check(labels.Distinct().Count() == labels.Count, "relay labels are distinguishable",
              string.Join(" | ", labels));

        return f;
    }

    private static void Check(bool ok, string what, string? detail = null) => Program.Check(ok, what, detail);
}