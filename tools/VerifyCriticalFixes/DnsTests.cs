using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Se7enPro.Models;
using Se7enPro.Services;

namespace VerifyCriticalFixes;

/// <summary>
/// Tests for the custom-DNS feature.
///
/// Every case here is pure: no socket is opened and no name is resolved. The probes are
/// exercised separately by the manual round-trip in the Network tab, because a test that
/// depends on the network is a test that fails for reasons unrelated to the code.
/// </summary>
internal static class DnsTests
{
    internal static int Run()
    {
        var failures = 0;

        Program.Section("DNS — list splitting");
        failures += TestSplitting();

        Program.Section("DNS — per-transport validation");
        failures += TestValidation();

        Program.Section("DNS — host:port parsing");
        failures += TestHostPort();

        Program.Section("DNS — resolver ordering (custom first)");
        failures += TestOrdering();

        Program.Section("DNS — strict mode");
        failures += TestStrict();

        Program.Section("DNS — tunnel-core config field");
        failures += TestCoreField();

        Program.Section("DNS — Xray core servers");
        failures += TestXrayServers();

        Program.Section("DNS — sing-box core servers");
        failures += TestSingBoxServers();

        Program.Section("DNS — core config serialisation");
        failures += TestCoreJsonShape();

        Program.Section("DNS — interaction with the tunnel and split tunnel");
        failures += TestTunnelInteraction();

        Program.Section("DNS — summary");
        failures += TestSummary();

        Program.Section("DNS — settings round-trip");
        failures += TestPersistence();

        return failures;
    }

    /// <summary>
    /// The four new settings must survive a save/load cycle with the exact JSON names the
    /// app writes, since settings.json is shared across versions.
    /// </summary>
    private static int TestPersistence()
    {
        var f = 0;

        var opts = new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
        };

        var original = new UserSettings
        {
            CustomDnsUdp = "9.9.9.9, 149.112.112.112",
            CustomDnsDot = "tls://dns.google",
            CustomDnsDoh = "https://cloudflare-dns.com/dns-query",
            CustomDnsStrict = true,
        };

        var json = System.Text.Json.JsonSerializer.Serialize(original, opts);
        foreach (var key in new[] { "customDnsUdp", "customDnsDot", "customDnsDoh", "customDnsStrict" })
        {
            Check(json.Contains($"\"{key}\""), $"settings JSON carries {key}");
        }

        var reloaded = System.Text.Json.JsonSerializer.Deserialize<UserSettings>(json, opts)!;
        Check(reloaded.CustomDnsUdp == original.CustomDnsUdp, "customDnsUdp round-trips");
        Check(reloaded.CustomDnsDot == original.CustomDnsDot, "customDnsDot round-trips");
        Check(reloaded.CustomDnsDoh == original.CustomDnsDoh, "customDnsDoh round-trips");
        Check(reloaded.CustomDnsStrict == original.CustomDnsStrict, "customDnsStrict round-trips");

        // A settings file written by an older build has none of these keys. The defaults
        // must then read as "automatic", not as null.
        var oldJson = """{"theme":"dark","language":"en"}""";
        var fromOld = System.Text.Json.JsonSerializer.Deserialize<UserSettings>(oldJson, opts)!;
        Check(fromOld.CustomDnsUdp == "" && fromOld.CustomDnsDot == "" && fromOld.CustomDnsDoh == "",
              "an older settings file reads as no custom resolvers");
        Check(fromOld.CustomDnsStrict == false, "strict mode defaults to off");

        var empty = new UserSettings();
        Check(empty.CustomDnsUdp == "" && empty.CustomDnsDot == "" && empty.CustomDnsDoh == "",
              "a fresh profile starts with every list empty");
        Check(empty.CustomDnsStrict == false, "a fresh profile starts with strict off");

        // The reset-to-defaults path must include the new keys, or a factory reset would
        // leave the user's resolvers behind.
        var vm = System.IO.File.ReadAllText(FindSource("SettingsViewModel.cs", "ViewModels"));
        foreach (var property in new[]
                 {
                     "CustomDnsUdp", "CustomDnsDot", "CustomDnsDoh", "CustomDnsStrict",
                 })
        {
            Check(vm.Contains($"Settings.{property} = def.{property}"),
                  $"factory reset clears {property}");
        }

        return f;
    }

    // ---------------------------------------------------------------- cases

    private static int TestSplitting()
    {
        var f = 0;

        // Five tokens: "1.1.1.1", "8.8.8.8", "9.9.9.9", "1.0.0.1" is four — the newline
        // before "1.0.0.1" is a separator, and " 1.1.1.1 " collapses with the spaces
        // around the comma. Expect four, and assert the exact shape rather than a guess.
        var parts = DnsSettings.SplitList(" 1.1.1.1 , 8.8.8.8;9.9.9.9\n1.0.0.1  ");
        Check(parts.Count == 4, "splits on comma, semicolon, space and newline", "count=" + parts.Count);
        Check(parts.SequenceEqual(new[] { "1.1.1.1", "8.8.8.8", "9.9.9.9", "1.0.0.1" }),
              "entries are trimmed and kept in order", string.Join("|", parts));

        Check(DnsSettings.SplitList("").Count == 0, "empty string yields no entries");
        Check(DnsSettings.SplitList("   ").Count == 0, "whitespace yields no entries");
        Check(DnsSettings.SplitList(null).Count == 0, "null yields no entries");
        Check(DnsSettings.SplitList(",,;;").Count == 0, "separator-only yields no entries");

        return f;
    }

    private static int TestValidation()
    {
        var f = 0;

        // --- plain UDP accepts both shapes ---
        Check(DnsSettings.ValidateEntry(DnsTransport.Udp, "1.1.1.1") is null, "UDP accepts a bare address");
        Check(DnsSettings.ValidateEntry(DnsTransport.Udp, "1.1.1.1:53") is null, "UDP accepts address:port");
        Check(DnsSettings.ValidateEntry(DnsTransport.Udp, "dns.quad9.net") is null, "UDP accepts a hostname");
        Check(DnsSettings.ValidateEntry(DnsTransport.Udp, "2606:4700:4700::1111") is null,
              "UDP accepts a bare IPv6 literal");

        // --- plain UDP must reject a foreign transport, and say where it belongs ---
        foreach (var (entry, owner) in new[]
                 {
                     ("tls://dns.google", "DoT"),
                     ("dot://dns.google", "DoT"),
                     ("https://cloudflare-dns.com/dns-query", "DoH"),
                     ("doh:dns.quad9.net", "DoH"),
                 })
        {
            var problem = DnsSettings.ValidateEntry(DnsTransport.Udp, entry);
            Check(problem is not null && problem.Contains(owner),
                  $"UDP rejects {entry} and names the {owner} list",
                  problem ?? "(accepted)");
        }

        // --- DoT requires its prefix ---
        Check(DnsSettings.ValidateEntry(DnsTransport.Dot, "dns.google") is not null,
              "DoT rejects a bare host (it would never get a TLS server)");
        Check(DnsSettings.ValidateEntry(DnsTransport.Dot, "tls://dns.google") is null,
              "DoT accepts tls://");
        Check(DnsSettings.ValidateEntry(DnsTransport.Dot, "dot://dns.google") is null,
              "DoT accepts dot://");
        Check(DnsSettings.ValidateEntry(DnsTransport.Dot, "TLS://dns.google") is null,
              "DoT prefix is case-insensitive");
        Check(DnsSettings.ValidateEntry(DnsTransport.Dot, "tls://1.1.1.1:853") is null,
              "DoT accepts an explicit port");

        // --- DoH requires https:// or doh: ---
        Check(DnsSettings.ValidateEntry(DnsTransport.Doh, "dns.quad9.net") is not null,
              "DoH rejects a bare host");
        Check(DnsSettings.ValidateEntry(DnsTransport.Doh, "https://cloudflare-dns.com/dns-query") is null,
              "DoH accepts a full https URL");
        Check(DnsSettings.ValidateEntry(DnsTransport.Doh, "doh:dns.quad9.net") is null,
              "DoH accepts the doh: shorthand");

        // --- structural rejections ---
        Check(DnsSettings.ValidateEntry(DnsTransport.Udp, "999.1.1.1") is not null,
              "rejects an out-of-range IPv4 octet");
        Check(DnsSettings.ValidateEntry(DnsTransport.Udp, "1.1.1.1") is not null
              || DnsSettings.ValidateEntry(DnsTransport.Udp, "1.1.1.1.1") is not null,
              "rejects an IPv4 with the wrong number of octets");
Check(DnsSettings.ValidateEntry(DnsTransport.Udp, "1.1.1.256") is not null,
              "rejects an out-of-range final octet");
Check(DnsSettings.ValidateEntry(DnsTransport.Udp, "1.1.1:99999") is not null
              || DnsSettings.ValidateEntry(DnsTransport.Udp, "1.1.1.1:99999") is not null,
              "rejects an out-of-range port even on a short address");
        Check(DnsSettings.ValidateEntry(DnsTransport.Udp, "999.999.999.999") is not null,
              "rejects a hostname-shaped string of digits");
        Check(DnsSettings.ValidateEntry(DnsTransport.Udp, "-bad-.example") is not null,
              "rejects a label starting or ending with a dash");
        Check(DnsSettings.ValidateEntry(DnsTransport.Udp, "under_score.example") is not null,
              "rejects an underscore in a hostname");
        Check(DnsSettings.ValidateEntry(DnsTransport.Udp, "1.1.1.1:70000") is not null,
              "rejects an out-of-range port");
        Check(DnsSettings.ValidateEntry(DnsTransport.Udp, "") is not null, "rejects an empty entry");
        Check(DnsSettings.ValidateEntry(DnsTransport.Doh, "http://example.com/dns-query") is not null,
              "DoH rejects plain http");

        // --- the loopback guard that matters for a TUN forwarder ---
        Check(DnsSettings.ValidateEntry(DnsTransport.Udp, "127.0.0.1") is null,
              "loopback parses (the policy layer, not this layer, decides if it is usable)");

        // --- a bare single label is rejected ---
        // The space is a list separator, so "not a resolver" arrives here as three
        // tokens. Without this rule every one of them would pass and a sentence typed
        // into the box would look valid while resolving nothing.
        Check(DnsSettings.SplitList("not a resolver").Count == 3,
              "the space is a separator, so prose arrives as separate tokens");
        Check(DnsSettings.ValidateEntry(DnsTransport.Udp, "not") is not null,
              "a bare single label is rejected rather than treated as a hostname");
        Check(DnsSettings.ValidateEntry(DnsTransport.Udp, "google") is not null,
              "a dotless hostname is rejected: this is a list of public resolvers");
        Check(DnsSettings.ValidateEntry(DnsTransport.Udp, "dns.google") is null,
              "a dotted hostname is still accepted");

        return f;
    }

    private static int TestHostPort()
    {
        var f = 0;

        Check(Same(DnsSettings.SplitHostPort("1.1.1.1", 53), "1.1.1.1", 53), "bare address gets the default port");
        Check(Same(DnsSettings.SplitHostPort("1.1.1.1:5353", 53), "1.1.1.1", 5353), "explicit port is taken");
        Check(Same(DnsSettings.SplitHostPort("[2606:4700:4700::1111]:53", 53), "2606:4700:4700::1111", 53),
              "bracketed IPv6 with a port");
        Check(Same(DnsSettings.SplitHostPort("[2606:4700:4700::1111]", 53), "2606:4700:4700::1111", 53),
              "bracketed IPv6 without a port");
        Check(Same(DnsSettings.SplitHostPort("2606:4700:4700::1111", 53), "2606:4700:4700::1111", 53),
              "bare IPv6 survives untouched (its colons are not a port)");
        Check(Same(DnsSettings.SplitHostPort("dns.example.com", 853), "dns.example.com", 853),
              "hostname gets the default port");

        return f;
    }

    private static int TestOrdering()
    {
        var f = 0;
        var policy = new DnsResolverPolicy();

        var s = new UserSettings { CustomDnsUdp = "9.9.9.9, 149.112.112.112" };
        var plan = policy.Build(s, hasV6Address: false);

        Check(plan.HasUserEntries, "user entries are recognised");
        Check(plan.UdpLiterals.Count >= 4, "user entries plus the defaults", "count=" + plan.UdpLiterals.Count);

        // This is the defect the Android client found in the field: appending the custom
        // server after 1.1.1.1 means it is never asked.
        Check(plan.UdpLiterals[0] == "9.9.9.9",
              "the FIRST resolver asked is the user's", plan.UdpLiterals.FirstOrDefault() ?? "(none)");
        Check(plan.UdpLiterals[1] == "149.112.112.112",
              "the second is the user's second entry");
        Check(plan.UdpLiterals.Contains("1.1.1.1"),
              "the built-in default is still present as a fallback");
        Check(plan.UdpLiterals.ToList().IndexOf("1.1.1.1") > 1,
              "the default comes AFTER the custom entries");

        // Duplicate collapsing, so 1.1.1.1 and 1.1.1.1:53 are not both asked.
        var dup = policy.Build(new UserSettings { CustomDnsUdp = "1.1.1.1, 1.1.1.1:53" }, false);
        Check(dup.UdpLiterals.Count(u => u == "1.1.1.1") == 1,
              "the same resolver entered twice is asked once",
              string.Join(",", dup.UdpLiterals));

        // Entering a resolver that is also a built-in default must not produce a duplicate
        // when the default is appended as a fallback.
        var sameAsDefault = policy.Build(new UserSettings { CustomDnsUdp = "1.1.1.1" }, false);
        Check(sameAsDefault.UdpLiterals.Count(u => u == "1.1.1.1") == 1,
              "a resolver that is also the default is not listed twice",
              string.Join(",", sameAsDefault.UdpLiterals));

        // v6 only when the session actually has a v6 address.
        var v4Only = policy.Build(new UserSettings(), hasV6Address: false);
        var withV6 = policy.Build(new UserSettings(), hasV6Address: true);
        Check(!v4Only.UdpLiterals.Contains("2606:4700:4700::1111"),
              "no v6 resolver on a v4-only session");
        Check(withV6.UdpLiterals.Contains("2606:4700:4700::1111"),
              "a v6 resolver is advertised when the session has v6");

        // Nothing configured means exactly the defaults.
        Check(!v4Only.HasUserEntries, "no user entries when nothing is configured");
        Check(v4Only.UdpLiterals.Count == 2, "the two defaults and nothing else",
              string.Join(",", v4Only.UdpLiterals));

        // An unusable entry is dropped and reported, and does not take the good ones down.
        var mixed = policy.Build(new UserSettings { CustomDnsUdp = "9.9.9.9, garbage!!, 1.0.0.1" }, false);
        Check(mixed.UdpLiterals.Contains("9.9.9.9") && mixed.UdpLiterals.Contains("1.0.0.1"),
              "valid entries survive alongside an invalid one");
        Check(mixed.Rejected.Count == 1, "the invalid entry is reported",
              string.Join("|", mixed.Rejected));
        Check(mixed.UdpLiterals[0] == "9.9.9.9", "ordering holds after a rejection");

        // Encrypted transports are carried, not rewritten.
        var encrypted = policy.Build(new UserSettings
        {
            CustomDnsUdp = "9.9.9.9",
            CustomDnsDot = "tls://dns.google",
            CustomDnsDoh = "https://cloudflare-dns.com/dns-query",
        }, false);
        Check(encrypted.DotLiterals.Contains("tls://dns.google"), "the DoT entry is carried verbatim");
        Check(encrypted.DohLiterals.Contains("https://cloudflare-dns.com/dns-query"),
              "the DoH URL is carried verbatim");
        Check(!encrypted.UdpLiterals.Contains("tls://dns.google"),
              "a DoT entry is not mistaken for a UDP one");

        return f;
    }

    private static int TestStrict()
    {
        var f = 0;
        var policy = new DnsResolverPolicy();

        var loose = policy.Build(new UserSettings { CustomDnsUdp = "9.9.9.9" }, false);
        Check(!loose.Strict, "strict is off by default");
        Check(loose.UdpLiterals.Contains("1.1.1.1"), "defaults are kept when strict is off");

        var strict = policy.Build(new UserSettings
        {
            CustomDnsUdp = "9.9.9.9",
            CustomDnsStrict = true,
        }, false);
        Check(strict.Strict, "strict is honoured");
        Check(!strict.UdpLiterals.Contains("1.1.1.1"),
              "strict drops the built-in defaults", string.Join(",", strict.UdpLiterals));
        Check(strict.UdpLiterals.Count == 1, "only the user's resolver remains",
              string.Join(",", strict.UdpLiterals));

        // The dangerous combination: strict on, nothing valid entered. That must still
        // resolve, or the session has no DNS at all.
        var emptyStrict = policy.Build(new UserSettings { CustomDnsStrict = true }, false);
        Check(emptyStrict.UdpLiterals.Count > 0,
              "strict with an empty list still resolves something",
              string.Join(",", emptyStrict.UdpLiterals));
        Check(emptyStrict.UdpLiterals.Contains("1.1.1.1"),
              "strict with an empty list falls back to the defaults rather than going DNS-less");

        var garbageStrict = policy.Build(
            new UserSettings { CustomDnsUdp = "not a resolver", CustomDnsStrict = true }, false);
        Check(garbageStrict.UdpLiterals.Count > 0,
              "strict where every entry is unusable still resolves",
              string.Join(",", garbageStrict.UdpLiterals));

        return f;
    }

    /// <summary>
    /// The psiphon <c>tunnel-core</c> <c>dns_servers</c> field.
    ///
    /// This core validates the field against a schema that accepts IP literals only. A
    /// scheme-prefixed entry would make it reject the entire config, so the test is
    /// specifically that no scheme ever escapes into this string.
    /// </summary>
    private static int TestCoreField()
    {
        var f = 0;
        var policy = new DnsResolverPolicy();

        var plan = policy.Build(new UserSettings
        {
            CustomDnsUdp = "9.9.9.9, 1.0.0.1",
            CustomDnsDot = "tls://dns.google",
            CustomDnsDoh = "https://cloudflare-dns.com/dns-query",
        }, false);

        var field = DnsResolverPolicy.ToTunnelCoreDnsServers(plan);

        Check(field.StartsWith("9.9.9.9", StringComparison.Ordinal),
              "the custom resolver leads the core field", field);
        Check(field.Contains("1.0.0.1"), "the second custom resolver is present", field);
        Check(!field.Contains("tls://", StringComparison.Ordinal),
              "no DoT scheme reaches the tunnel-core field, which would reject the config", field);
        Check(!field.Contains("https://", StringComparison.Ordinal),
              "no DoH scheme reaches the tunnel-core field, which would reject the config", field);

        // Everything in the field must parse as a bare address.
        foreach (var token in field.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            Check(System.Net.IPAddress.TryParse(token, out _),
                  $"core field entry '{token}' is a bare IP address");
        }

        // No whitespace survives: the core splits this on commas.
        Check(!field.Contains(' '), "no spaces in the core field", field);

        // A hostname-only entry has no literal to hand the core, so it must be dropped
        // rather than passed through as a name.
        var byName = DnsResolverPolicy.ToTunnelCoreDnsServers(
            policy.Build(new UserSettings { CustomDnsUdp = "dns.quad9.net" }, false));
        Check(!byName.Contains("dns.quad9.net"),
              "a resolver given only as a hostname is not passed to the core as a name", byName);

        // The relay still gets it, though — the forwarder resolves the name itself.
        var relay = DnsResolverPolicy.RelayTargets(
            policy.Build(new UserSettings { CustomDnsUdp = "dns.quad9.net" }, false));
        Check(relay.Any(t => t.Host == "dns.quad9.net"),
              "the hostname is still available to the local forwarder");

        return f;
    }

    /// <summary>
    /// The Xray <c>dns.servers</c> array.
    ///
    /// This is the list that actually decides which resolver answers inside the tunnel,
    /// so the ordering and the presence of the local fallback are both load-bearing.
    /// </summary>
    private static int TestXrayServers()
    {
        var f = 0;
        var policy = new DnsResolverPolicy();

        var plan = policy.Build(new UserSettings
        {
            CustomDnsUdp = "9.9.9.9",
            CustomDnsDot = "tls://dns.google",
            CustomDnsDoh = "https://cloudflare-dns.com/dns-query",
        }, false);

        var servers = DnsResolverPolicy.ToXrayDnsServers(plan)
                              .Select(o => o as string ?? "(not a string)")
                              .ToList();

        Check(servers.Count >= 4, "every configured resolver reaches the Xray list",
              string.Join(" | ", servers));
        Check(servers[0] == "9.9.9.9", "the custom UDP resolver is FIRST in the Xray list",
              servers[0]);
        Check(servers.Contains("tls://dns.google"), "the DoT entry survives as tls://", 
              string.Join(" | ", servers));
        Check(servers.Contains("https://cloudflare-dns.com/dns-query"),
              "the DoH URL survives verbatim");
        Check(servers[servers.Count - 1] == "localhost",
              "the local resolver is last, as the last-resort fallback");
        Check(servers.Count(s => s == "localhost") == 1,
              "exactly one local fallback is emitted");
        Check(servers.All(s => !string.IsNullOrWhiteSpace(s)),
              "no empty entries in the Xray list");

        // "dot://" is this app's alias for DoT. Xray does not know that scheme, so it
        // must be normalised or the core rejects the whole config.
        var dotAlias = policy.Build(new UserSettings { CustomDnsDot = "dot://dns.quad9.net" }, false);
        var aliasServers = DnsResolverPolicy.ToXrayDnsServers(dotAlias).Select(o => o as string ?? "");
        Check(aliasServers.Contains("tls://dns.quad9.net"),
              "the dot:// alias is normalised to tls:// for Xray",
              string.Join(" | ", aliasServers));
        Check(!aliasServers.Any(s => s.StartsWith("dot://", StringComparison.OrdinalIgnoreCase)),
              "no unrecognised dot:// scheme reaches the Xray list");

        // A non-default DoT port must survive; dropping it would silently use 853.
        var dotPort = policy.Build(new UserSettings { CustomDnsDot = "tls://1.1.1.1:8853" }, false);
        Check(DnsResolverPolicy.ToXrayDnsServers(dotPort).Select(o => o as string ?? "")
                  .Contains("tls://1.1.1.1:8853"),
              "an explicit DoT port is preserved");

        // The doh: shorthand becomes a full https URL, because Xray only reads schemes.
        var dohShorthand = policy.Build(new UserSettings { CustomDnsDoh = "doh:dns.quad9.net" }, false);
        Check(DnsResolverPolicy.ToXrayDnsServers(dohShorthand).Select(o => o as string ?? "")
                  .Contains("https://dns.quad9.net"),
              "the doh: shorthand is expanded to an https URL");

        // Nothing configured must reproduce the default list Xray shipped with, exactly and in
        // order: DoH first, then the two plain resolvers, then localhost.
        var none = policy.Build(new UserSettings(), false);
        var noneServers = DnsResolverPolicy.ToXrayDnsServers(none).Select(o => o as string ?? "").ToList();
        Check(noneServers.SequenceEqual(new[]
              {
                  "https://1.1.1.1/dns-query", "1.1.1.1", "8.8.8.8", "localhost",
              }),
              "with nothing configured the Xray list is byte-for-byte the previous default",
              string.Join(" | ", noneServers));

        // One configured entry replaces the defaults rather than joining them.
        var oneUdp = DnsResolverPolicy.ToXrayDnsServers(
            policy.Build(new UserSettings { CustomDnsUdp = "9.9.9.9" }, false))
            .Select(o => o as string ?? "").ToList();
        Check(oneUdp.SequenceEqual(new[] { "9.9.9.9", "1.1.1.1", "8.8.8.8", "localhost" }),
              "one configured UDP entry leads, defaults follow as the fallback",
              string.Join(" | ", oneUdp));
        Check(!oneUdp.Contains("https://1.1.1.1/dns-query"),
              "a configured UDP list does not also get the default DoH entry",
              string.Join(" | ", oneUdp));

        return f;
    }

    /// <summary>
    /// The sing-box <c>dns.servers</c> array.
    ///
    /// sing-box needs objects, not scheme strings, and it rejects a config where two
    /// servers share a tag — so both the shape and the tag uniqueness matter.
    /// </summary>
    private static int TestSingBoxServers()
    {
        var f = 0;
        var policy = new DnsResolverPolicy();

        var plan = policy.Build(new UserSettings
        {
            CustomDnsUdp = "9.9.9.9",
            CustomDnsDot = "tls://dns.google",
            CustomDnsDoh = "https://cloudflare-dns.com/dns-query",
        }, false);

        var servers = DnsResolverPolicy.ToSingBoxDnsServers(plan);

        Check(servers.Count >= 4, "every configured resolver reaches the sing-box list",
              servers.Count.ToString());

        var types = servers.Select(DescribeSingBox).ToList();
        Check(types[0] == "udp:9.9.9.9:53", "the custom UDP resolver is FIRST", types[0]);
        Check(types.Contains("tls:dns.google:853"),
              "the DoT entry became a tls server object on port 853",
              string.Join(" | ", types));
        Check(types.Contains("https:cloudflare-dns.com:443/dns-query"),
              "the DoH entry became an https server object carrying its path",
              string.Join(" | ", types));
        Check(types.Last().StartsWith("local", StringComparison.Ordinal),
              "the local resolver is last", types.Last());

        // Tag uniqueness: sing-box refuses to start on a duplicate tag.
        var tags = Tags(servers);
        Check(tags.Count == tags.Distinct().Count(),
              "every sing-box server has a unique tag", string.Join(",", tags));

        // The DoH path must always be a real path, including for the doh: shorthand,
        // which carries no path of its own.
        var shorthand = policy.Build(new UserSettings { CustomDnsDoh = "doh:dns.quad9.net" }, false);
        var shorthandTypes = DnsResolverPolicy.ToSingBoxDnsServers(shorthand).Select(DescribeSingBox);
        Check(shorthandTypes.Any(t => t == "https:dns.quad9.net:443/dns-query"),
              "a DoH entry with no path gets the RFC 8484 default /dns-query",
              string.Join(" | ", shorthandTypes));

        // An explicit DoH path is kept.
        var withPath = policy.Build(
            new UserSettings { CustomDnsDoh = "https://dns.quad9.net:4443/custom-query" }, false);
        Check(DnsResolverPolicy.ToSingBoxDnsServers(withPath).Select(DescribeSingBox)
                  .Contains("https:dns.quad9.net:4443/custom-query"),
              "an explicit DoH host, port and path all survive");

        // An explicit DoT port survives.
        var dotPort = policy.Build(new UserSettings { CustomDnsDot = "tls://dns.quad9.net:8853" }, false);
        Check(DnsResolverPolicy.ToSingBoxDnsServers(dotPort).Select(DescribeSingBox)
                  .Contains("tls:dns.quad9.net:8853"),
              "an explicit DoT port survives into the sing-box list");

        // A resolver given as a hostname still produces a usable server object.
        var byName = policy.Build(new UserSettings { CustomDnsUdp = "dns.quad9.net" }, false);
        var byNameTypes = DnsResolverPolicy.ToSingBoxDnsServers(byName).Select(DescribeSingBox);
        Check(byNameTypes.Any(t => t == "udp:dns.quad9.net:53"),
              "a resolver entered as a hostname is passed through as a name",
              string.Join(" | ", byNameTypes));

        // Nothing configured must reproduce the default set both cores shipped with: Cloudflare
        // DoH first, then the two plain resolvers, then the local fallback.
        var none = policy.Build(new UserSettings(), false);
        var noneTypes = DnsResolverPolicy.ToSingBoxDnsServers(none).Select(DescribeSingBox).ToList();
        Check(noneTypes.SequenceEqual(new[]
              {
                  "https:1.1.1.1:443/dns-query", "udp:1.1.1.1:53", "udp:8.8.8.8:53", "local",
              }),
              "with nothing configured the sing-box list is the historical default set",
              string.Join(" | ", noneTypes));

        // A configured encrypted resolver is asked FIRST and the plain-UDP defaults remain as
        // its fallback — losing DNS because one DoH endpoint is down would be worse than
        // the leak the user was trying to avoid. The default DoH entry itself is NOT
        // re-added: the user's own encrypted server supersedes it.
        var oneDoh = DnsResolverPolicy.ToSingBoxDnsServers(
            policy.Build(new UserSettings { CustomDnsDoh = "https://dns.quad9.net/dns-query" }, false))
            .Select(DescribeSingBox).ToList();
        Check(oneDoh.First() == "https:dns.quad9.net:443/dns-query",
              "a configured DoH entry is asked first", oneDoh.First());
        Check(oneDoh.Contains("udp:1.1.1.1:53") && oneDoh.Contains("udp:8.8.8.8:53"),
              "the plain-UDP defaults remain as the fallback for that DoH entry",
              string.Join(" | ", oneDoh));
        Check(!oneDoh.Contains("https:1.1.1.1:443/dns-query"),
              "the default DoH entry is superseded by the user's own",
              string.Join(" | ", oneDoh));

        // Strict mode with one configured DoH entry drops every built-in, encrypted
        // transports included — that is the whole meaning of the toggle.
        var strictDoh = DnsResolverPolicy.ToSingBoxDnsServers(
            policy.Build(new UserSettings
            {
                CustomDnsDoh = "https://dns.quad9.net/dns-query",
                CustomDnsStrict = true,
            }, false))
            .Select(DescribeSingBox).ToList();
        Check(strictDoh.SequenceEqual(new[] { "https:dns.quad9.net:443/dns-query", "local" }),
              "strict with one configured DoH entry leaves only that entry and the local fallback",
              string.Join(" | ", strictDoh));

        // Strict mode with nothing configured must still produce a usable list rather
        // than an empty one the core would refuse to start with.
        var strictNone = DnsResolverPolicy.ToSingBoxDnsServers(
            policy.Build(new UserSettings { CustomDnsStrict = true }, false))
            .Select(DescribeSingBox).ToList();
        Check(strictNone.Any(t => !t.StartsWith("local", StringComparison.Ordinal)),
              "strict with nothing configured still yields a working resolver",
              string.Join(" | ", strictNone));

        return f;
    }

    /// <summary>Renders a sing-box server object as type:server:port/path for assertions.</summary>
    /// <summary>
    /// The generated arrays must survive JSON serialisation intact.
    ///
    /// Both cores read their config from a file, so a shape that cannot be serialised
    /// would surface as a core that refuses to start rather than as a compile error.
    /// Anonymous objects inside a <c>List&lt;object&gt;</c> are the case worth pinning.
    /// </summary>
    private static int TestCoreJsonShape()
    {
        var f = 0;
        var policy = new DnsResolverPolicy();

        var plan = policy.Build(new UserSettings
        {
            CustomDnsUdp = "9.9.9.9",
            CustomDnsDot = "tls://dns.google",
            CustomDnsDoh = "https://cloudflare-dns.com/dns-query",
        }, false);

        // Xray: an array of plain strings.
        var xray = System.Text.Json.JsonSerializer.Serialize(
            new { dns = new { servers = DnsResolverPolicy.ToXrayDnsServers(plan) } });
        Check(xray.Contains("\"servers\":[", StringComparison.Ordinal),
              "the Xray servers array serialises as a JSON array", xray);
        Check(xray.Contains("\"9.9.9.9\"", StringComparison.Ordinal),
              "the Xray array holds strings, not objects", xray);

        // sing-box: an array of objects, each with its own tag.
        var singbox = System.Text.Json.JsonSerializer.Serialize(
            new { dns = new { servers = DnsResolverPolicy.ToSingBoxDnsServers(plan) } });
        using (var doc = System.Text.Json.JsonDocument.Parse(singbox))
        {
            var servers = doc.RootElement.GetProperty("dns").GetProperty("servers");
            Check(servers.ValueKind == System.Text.Json.JsonValueKind.Array,
                  "the sing-box servers value is a JSON array");
            var tags = servers.EnumerateArray()
                               .Select(e => e.GetProperty("tag").GetString() ?? "")
                               .ToList();
            Check(tags.Count == servers.GetArrayLength(),
                  "every sing-box server object carries a tag");
            Check(tags.Distinct().Count() == tags.Count,
                  "sing-box tags stay unique through serialisation", string.Join(",", tags));
            Check(servers.EnumerateArray().All(e => e.GetProperty("type").ValueKind
                                                    == System.Text.Json.JsonValueKind.String),
                  "every sing-box server object has a string type");
        }

        return f;
    }

    /// <summary>
/// How the resolver list interacts with TUN mode and split tunnelling.
///
/// Three things are load-bearing and easy to break:
///
///  1. Split-DNS bypass domains must keep resolving through the CARRIER resolver, not
///     through the user's list. If they started using the custom resolvers, split
///     tunnelling would be defeated by the very setting meant to help with censorship.
///
///  2. Editing the list on a live session has to reach the running forwarder. The
///     reconcile loop gates the live re-apply on a hash of the split rules, so unless
///     the DNS settings are in that hash the edit is silently ignored until reconnect.
///
///  3. Strict mode must not be silently ignored when the plan leaves the forwarder with
///     no plain-UDP target to dial.
/// </summary>
private static int TestTunnelInteraction()
{
    var f = 0;
    var policy = new DnsResolverPolicy();

    // ---- 1. split-DNS bypass is unaffected -------------------------------------------

    // Bypass domains resolve through _underlyingDnsServers, which the DNS feature never
    // touches: BuildSplitPolicy takes LocalDnsIp from the detected carrier resolvers.
    var splitSource = System.IO.File.ReadAllText(FindSource("WintunTunManager.SplitDns.cs", "Services/Tun"));
    Check(splitSource.Contains("var localDns = _underlyingDnsServers.FirstOrDefault()"),
          "split-DNS bypass still uses the detected carrier resolver");
    Check(!splitSource.Contains("DnsResolverPolicy"),
          "the custom-DNS feature does not feed the split-DNS bypass path at all");

    // The bypass path must not consult the relay list. QueryLocalAsync reads the
    // carrier address straight out of the split policy.
    var localQuery = System.IO.File.ReadAllText(FindSource("SocksDnsForwarder.Local.cs", "Services/Tun"));
    Check(localQuery.Contains("_split!.LocalDnsIp"),
          "the local/bypass query path dials the carrier resolver, not a custom one");
    Check(!localQuery.Contains("_relayTargets"),
          "the bypass query path never consults the custom resolver list");

    // And the tunnel path must. Otherwise the configured list is dead code in TUN mode.
    var tunnelQuery = System.IO.File.ReadAllText(FindSource("SocksDnsForwarder.Query.cs", "Services/Tun"));
    Check(tunnelQuery.Contains("_relayTargets"),
          "the tunnel query path does walk the custom resolver list");

    // ---- 2. a live edit reaches the running session -----------------------------------

    var helpers = System.IO.File.ReadAllText(FindSource("WintunTunManager.Helpers.cs", "Services/Tun"));
    var hashBody = System.Text.RegularExpressions.Regex.Match(
        helpers, @"(?s)private string ComputeSplitHash.*?\n    \}").Value;

    Check(hashBody.Contains("CustomDnsUdp"), "the split hash includes the UDP resolver list");
    Check(hashBody.Contains("CustomDnsDot"), "the split hash includes the DoT resolver list");
    Check(hashBody.Contains("CustomDnsDoh"), "the split hash includes the DoH resolver list");
    Check(hashBody.Contains("CustomDnsStrict"), "the split hash includes strict mode");

    // Changing only the DNS list must therefore produce a different hash, which is what
    // makes ReconcileAsync take the live re-apply branch.
    var before = HashOf("9.9.9.9", "", "", strict: false);
    var after = HashOf("1.1.1.1", "", "", strict: false);
    var strictChanged = HashOf("9.9.9.9", "", "", strict: true);
    Check(before != after, "changing the resolver list changes the hash");
    Check(before != strictChanged, "toggling strict mode changes the hash");

    // ---- 3. strict mode is never silently ignored -------------------------------------

    // Strict + only an encrypted resolver leaves the forwarder with no plain-UDP target,
    // because a SOCKS CONNECT cannot carry a DNS-over-TLS handshake on its own.
    var strictEncrypted = policy.Build(new UserSettings
    {
        CustomDnsDoh = "https://dns.quad9.net/dns-query",
        CustomDnsStrict = true,
    }, false);

    Check(strictEncrypted.Strict, "strict is set");
    Check(DnsResolverPolicy.RelayTargets(strictEncrypted).Count == 0,
          "strict with only an encrypted resolver leaves the relay with nothing to dial");

    // The forwarder must refuse to paper over that by quietly dialling 1.1.1.1.
    var fwd = System.IO.File.ReadAllText(FindSource("SocksDnsForwarder.cs", "Services/Tun"));
    var ctor = System.Text.RegularExpressions.Regex.Match(
        fwd, @"(?s)public SocksDnsForwarder\(.*?\n    \}").Value;
    Check(!ctor.Contains("IPAddress.TryParse(upstreamDnsIp"),
          "the forwarder no longer silently substitutes a hard-coded resolver "
          + "when the configured list has no plain-UDP target");
    Check(fwd.Contains("RelayTargetsAreEmpty"),
          "the forwarder exposes the empty-relay state so the caller can report it");

    return f;
}

private static string HashOf(string udp, string dot, string doh, bool strict)
{
    var raw = string.Join("|",
        43076.ToString(), "0", "1", "exclude", "", "", "", "",
        udp, dot, doh, strict ? "1" : "0");
    return Convert.ToHexString(
        System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(raw)));
}

private static string DescribeSingBox(object server)
    {
        var t = server.GetType();
        string Get(string name) => t.GetProperty(name)?.GetValue(server)?.ToString() ?? "";
        var type = Get("type");
        if (type == "local") return "local";

        var path = Get("path");
        return $"{type}:{Get("server")}:{Get("server_port")}{path}";
    }

    private static List<string> Tags(IEnumerable<object> servers) =>
        servers.Select(s => s.GetType().GetProperty("tag")?.GetValue(s)?.ToString() ?? "")
                .ToList();

    private static int TestSummary()
    {
        var f = 0;

        Check(DnsSettings.Summarise("", "", "") == "", "nothing configured summarises as empty");
        Check(DnsSettings.Summarise("9.9.9.9", "", "") == "1 UDP", "one UDP entry", DnsSettings.Summarise("9.9.9.9", "", ""));
        Check(DnsSettings.Summarise("9.9.9.9,1.0.0.1", "", "") == "2 UDP", "two UDP entries");
        Check(DnsSettings.Summarise("", "tls://dns.google", "") == "1 DoT", "one DoT entry");
        Check(DnsSettings.Summarise("", "", "doh:dns.quad9.net") == "1 DoH", "one DoH entry");
        Check(DnsSettings.Summarise("9.9.9.9", "tls://dns.google", "doh:dns.quad9.net") == $"1 UDP{DnsSettings.SummarySeparator}1 DoT{DnsSettings.SummarySeparator}1 DoH",
              "all three transports listed", DnsSettings.Summarise("9.9.9.9", "tls://dns.google", "doh:dns.quad9.net"));

        return f;
    }

    // ---------------------------------------------------------------- helpers

    private static bool Same((string Host, int Port) actual, string host, int port) =>
        actual.Host == host && actual.Port == port;

    private static string FindSource(string fileName, string relativeFolder)
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 12 && dir is not null; i++)
        {
            var candidate = System.IO.Path.Combine(dir, "Se7enPro", relativeFolder, fileName);
            if (System.IO.File.Exists(candidate)) return candidate;
            dir = System.IO.Path.GetDirectoryName(dir.TrimEnd('\\'));
        }
        throw new System.IO.FileNotFoundException($"could not locate {fileName}");
    }

    private static void Check(bool ok, string what, string? detail = null)
    {
        if (!ok && detail is not null) what += $"  [{detail}]";
        Program.Check(ok, what);
    }
}