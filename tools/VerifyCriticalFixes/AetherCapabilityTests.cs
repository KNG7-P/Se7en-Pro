using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Se7enPro.Models;
using Se7enPro.Services;

namespace VerifyCriticalFixes;

internal static class AetherCapabilityTests
{
    internal static int Run()
    {
        var failures = 0;

        Program.Section("Aether — capability probe against the real binary");
        failures += TestAgainstBundledBinary();

        Program.Section("Aether — gool mode selection");
        failures += TestGoolMode();

        Program.Section("Aether — optional flags are gated, not assumed");
        failures += TestOptionalGating();

        Program.Section("Aether — DNS field");
        failures += TestDnsField();

        Program.Section("Aether — gool identity file layout");
        failures += TestGoolLayout();

        Program.Section("Aether — TLS fingerprint shaping");
        failures += TestTlsFingerprintFlags();

        Program.Section("Aether — flags are gated on what the protocol carries");
        failures += TestProtocolGating();

        Program.Section("Aether — upstream exit node, MASQUE SNI, 2.4.0 TLS levers");
        failures += TestUpstreamMasqueSniAndTlsLevers();

        Program.Section("Aether — ECH key resolver");
        failures += TestEchDns();

        Program.Section("Aether — ECH first, SHARD fallback");
        failures += TestEchFallbackPolicy();

        Program.Section("Aether — stale staged core is detected");
        failures += TestStagedCoreRefresh();

        Program.Section("Aether — fast defaults, chrome-compat TLS preset, lastconn isolation");
        failures += TestFastDefaultsChromeCompatAndLastConn();

        return failures;
    }

    private static int TestStagedCoreRefresh()
    {
        var f = 0;

        Check(AetherExtras.ShouldRefreshStagedCore("aether 2.3.0", "aether 2.1.0"),
              "a staged core older than the bundled one is refreshed");
        Check(AetherExtras.ShouldRefreshStagedCore("aether 2.1.0", "aether 2.3.0"),
              "and so is one that is somehow newer, rather than being trusted");
        Check(!AetherExtras.ShouldRefreshStagedCore("aether 2.3.0", "aether 2.3.0"),
              "matching versions are left alone, so a connect does not rewrite a good core");
        Check(!AetherExtras.ShouldRefreshStagedCore("AETHER 2.3.0", "aether 2.3.0"),
              "the comparison ignores case and surrounding whitespace");

        Check(!AetherExtras.ShouldRefreshStagedCore("", ""),
              "two unknowns do not trigger a refresh");
        Check(!AetherExtras.ShouldRefreshStagedCore("aether 2.3.0", ""),
              "an unreadable staged version does not trigger a refresh");
        Check(!AetherExtras.ShouldRefreshStagedCore(null, null),
              "and neither does two nulls");
        Check(!AetherExtras.ShouldRefreshStagedCore("  ", "aether 2.1.0"),
              "a blank bundled version is not grounds for a refresh either");

        var exe = FindBundled();
        if (exe is not null)
        {
            var v = AetherCapabilities.Version(exe);
            Check(v.Contains("2.4.0", StringComparison.Ordinal),
                  "the bundled core reports 2.4.0, so this check has something to compare", v);
        }

        return f;
    }

    private static int TestProtocolGating()
    {
        var f = 0;
        var exe = FindBundled();
        if (exe is null) return f;

        var tlsOn = new UserSettings
        {
            AetherEch = true,
            AetherTlsCiphers = "ECDHE-RSA-AES128-GCM-SHA256",
            AetherTlsGroups = "X25519:P-256",
            AetherDisableGrease = true,
        };

        Check(AetherExtras.TlsSurfaceFor(tlsOn, ConnectionMethod.WireGuard)
              == AetherExtras.TlsSurface.None,
              "plain WireGuard is reported as carrying no TLS");

        var args = new List<string>();
        AetherExtras.AddOptional(args, tlsOn, exe, ConnectionMethod.WireGuard);
        Check(!args.Contains("--ech"),
              "ECH is withheld on plain WireGuard, which has no handshake to hide the name in");
        Check(!args.Contains("--tls-ciphers") && !args.Contains("--tls-groups"),
              "the TLS 1.2 cipher list and group order are withheld there too");
        Check(!args.Contains("--disable-grease"), "GREASE is withheld there as well");

        Check(AetherExtras.TlsSurfaceFor(tlsOn, ConnectionMethod.WarpOnWarp)
              == AetherExtras.TlsSurface.Http3,
              "gool is reported as QUIC, which is what aether actually negotiates for it");

        args.Clear();
        AetherExtras.AddOptional(args, tlsOn, exe, ConnectionMethod.WarpOnWarp);
        Check(args.Contains("--ech"), "ECH does apply on gool, whose hops ride MASQUE", args.Count.ToString());
        Check(!args.Contains("--tls-ciphers"),
              "but the TLS 1.2 cipher list is withheld on QUIC, which has none to list");
        Check(!args.Contains("--tls-groups"), "and so is the group order");
        Check(args.Contains("--disable-grease"),
              "GREASE does apply on QUIC, because it rides in TLS 1.3 as well as 1.2");

        var h2 = new UserSettings
        {
            AetherEch = true,
            AetherTlsCiphers = "ECDHE-RSA-AES128-GCM-SHA256",
            AetherTlsGroups = "X25519:P-256",
            AetherDisableGrease = true,
            AetherMasqueTransport = "h2",
            AetherMasqueQuic = false,
        };
        Check(AetherExtras.TlsSurfaceFor(h2, ConnectionMethod.Masque)
              == AetherExtras.TlsSurface.Http2, "MASQUE on HTTP/2 is reported as TLS 1.2");

        args.Clear();
        AetherExtras.AddOptional(args, h2, exe, ConnectionMethod.Masque);
        Check(args.Contains("--ech"), "ECH is sent on HTTP/2");
        Check(args.Contains("--tls-ciphers"), "the TLS 1.2 cipher list is sent on HTTP/2");
        Check(args.Contains("--tls-groups"), "the group order is sent on HTTP/2");
        Check(args.Contains("--disable-grease"), "GREASE is sent on HTTP/2");

        Check(AetherExtras.TlsSurfaceFor(
                  new UserSettings { AetherMasqueQuic = true }, ConnectionMethod.Masque)
              == AetherExtras.TlsSurface.Http3,
              "the QUIC setting switches the surface for MASQUE");
        Check(AetherExtras.TlsSurfaceFor(
                  new UserSettings { AetherMasqueQuic = false }, ConnectionMethod.Masque)
              == AetherExtras.TlsSurface.Http2,
              "and turning it off switches it back to HTTP/2");

        foreach (var m in new[] { ConnectionMethod.WireGuard, ConnectionMethod.Masque,
                                  ConnectionMethod.MasqueInMasque, ConnectionMethod.WarpOnWarp })
        {
            args.Clear();
            AetherExtras.AddOptional(
                args, new UserSettings { AetherTor = true, AetherTorRelays = true }, exe, m);
            Check(args.Contains("--tor") && args.Contains("--tor-relays"),
                  $"carrying Tor inside the tunnel is offered for {m}");
        }

        Check(AetherExtras.UnavailableReason(AetherExtras.TlsSurface.None, "--ech").Length > 0,
              "there is a reason to show for a protocol with no TLS");
        Check(AetherExtras.UnavailableReason(AetherExtras.TlsSurface.Http3, "--tls-ciphers").Length > 0,
              "and one for a TLS 1.2 flag on QUIC");
        Check(AetherExtras.UnavailableReason(AetherExtras.TlsSurface.Http3, "--ech").Length == 0,
              "but no reason for ECH on QUIC, which is fully supported there");
        Check(AetherExtras.UnavailableReason(AetherExtras.TlsSurface.Http2, "--tls-ciphers").Length == 0,
              "and none for a TLS 1.2 flag on HTTP/2");

        return f;
    }

    private static int TestUpstreamMasqueSniAndTlsLevers()
    {
        var f = 0;

        var ok = AetherExtras.BuildUpstreamArgs("socks5://127.0.0.1:10808");
        Check(ok.Count == 2 && ok[0] == "--upstream" && ok[1] == "socks5://127.0.0.1:10808",
              "a socks5 URL becomes --upstream <url>", string.Join(' ', ok));

        var http = AetherExtras.BuildUpstreamArgs("http://127.0.0.1:8080");
        Check(http.Count == 2 && http[1] == "http://127.0.0.1:8080",
              "an http upstream is accepted as well", string.Join(' ', http));

        foreach (var bad in new string?[] { null, "", "   ", "127.0.0.1:10808", "socks5://",
                                            "socks5:///x", "ftp://1.2.3.4:21" })
        {
            Check(AetherExtras.BuildUpstreamArgs(bad).Count == 0,
                  $"\"{bad}\" is not an upstream aether can dial, so nothing is sent");
        }

        var redacted = AetherExtras.RedactUpstreamUrl(
            "http://alice:s3cr%40t@proxy.example:8080/path?x=1");
        Check(redacted == "http://proxy.example:8080/path?x=1",
              "upstream logging removes both username and password", redacted);
        Check(AetherExtras.RedactUpstreamUrl("socks5://127.0.0.1:10808")
                  == "socks5://127.0.0.1:10808",
              "upstream redaction preserves an endpoint without credentials");

        var quic = new UserSettings { AetherMasqueTransport = "h3", AetherMasqueQuic = true };
        Check(AetherExtras.EffectiveTransport(quic, hasUpstream: true) == "h2",
              "an upstream proxy forces MASQUE over HTTP/2, which a SOCKS hop can carry");
        Check(AetherExtras.EffectiveTransport(quic, hasUpstream: false) == "h3",
              "without a chain the configured QUIC transport is kept");
        Check(AetherExtras.EffectiveTransport(
                  new UserSettings { AetherMasqueTransport = "h2" }, hasUpstream: true) == "h2",
              "HTTP/2 is not changed when it was already chosen");

        var exe = FindBundled();
        if (exe is null) return f;

        foreach (var flag in new[] { "--upstream", "--masque-sni", "--tls-sigalgs", "--tls-alps" })
        {
            Check(AetherCapabilities.Supports(exe, flag),
                  $"the bundled 2.4.0 advertises {flag}");
        }

        var args = new List<string>();
        AetherExtras.AddOptional(args,
            new UserSettings
            {
                AetherMasqueSni = "consumer-masque.cloudflareclient.com",
                AetherMasqueTransport = "h2",
            },
            exe, ConnectionMethod.Masque);
        var si = args.IndexOf("--masque-sni");
        Check(si >= 0 && args[si + 1] == "consumer-masque.cloudflareclient.com",
              "--masque-sni carries the name verbatim", string.Join(' ', args));

        args.Clear();
        AetherExtras.AddOptional(args,
            new UserSettings { AetherMasqueSni = "consumer-masque.cloudflareclient.com" },
            exe, ConnectionMethod.WireGuard);
        Check(!args.Contains("--masque-sni"),
              "plain WireGuard has no ClientHello, so the SNI is withheld there");

        args.Clear();
        AetherExtras.AddOptional(args, new UserSettings(), exe, ConnectionMethod.Masque);
        Check(!args.Contains("--masque-sni"), "a blank SNI leaves aether's default alone");

        var levers = new UserSettings
        {
            AetherTlsSigalgs = "ecdsa_secp256r1_sha256:rsa_pss_rsae_sha256",
            AetherTlsAlps = "old",
            AetherMasqueTransport = "h2",
        };

        args.Clear();
        AetherExtras.AddOptional(args, levers, exe, ConnectionMethod.Masque);
        var gi = args.IndexOf("--tls-sigalgs");
        var ai = args.IndexOf("--tls-alps");
        Check(gi >= 0 && args[gi + 1] == levers.AetherTlsSigalgs,
              "--tls-sigalgs is passed through verbatim", string.Join(' ', args));
        Check(ai >= 0 && args[ai + 1] == "old", "the ALPS codepoint choice is passed through");

        args.Clear();
        AetherExtras.AddOptional(args, levers, exe, ConnectionMethod.WarpOnWarp);
        Check(args.Contains("--tls-sigalgs") && args.Contains("--tls-alps"),
              "both apply on QUIC as well, because they are not TLS 1.2 only");

        args.Clear();
        AetherExtras.AddOptional(args, levers, exe, ConnectionMethod.WireGuard);
        Check(!args.Contains("--tls-sigalgs") && !args.Contains("--tls-alps"),
              "and neither is sent on a protocol with no TLS handshake");

        args.Clear();
        AetherExtras.AddOptional(args, new UserSettings { AetherTlsAlps = "sometimes" },
                                 exe, ConnectionMethod.Masque);
        Check(!args.Contains("--tls-alps"), "an unknown ALPS value is dropped rather than sent");

        var goolH2 = new UserSettings { AetherGoolMode = "masque_h2" };
        Check(AetherExtras.TlsSurfaceFor(goolH2, ConnectionMethod.WarpOnWarp) == AetherExtras.TlsSurface.Http2,
              "modern gool over HTTP/2 reports as Http2 surface");

        var goolClassic = new UserSettings { AetherGoolMode = "classic", AetherMasqueTransport = "h2" };
        Check(AetherExtras.TlsSurfaceFor(goolClassic, ConnectionMethod.WarpOnWarp) == AetherExtras.TlsSurface.None,
              "classic gool (WireGuard-in-WireGuard) reports as no TLS surface");

        Check(AetherCapabilities.Supports(exe, "--tls-verify"), "the bundled 2.4.0 advertises --tls-verify");
        args.Clear();
        AetherExtras.AddOptional(args, new UserSettings { AetherTlsVerify = true }, exe, ConnectionMethod.Masque);
        Check(args.Contains("--tls-verify"), "--tls-verify is emitted when enabled in settings");

        return f;
    }

    private static int TestTlsFingerprintFlags()
    {
        var f = 0;
        var exe = FindBundled();
        if (exe is null) return f;

        var args = new List<string>();
        AetherExtras.AddOptional(args, new UserSettings(), exe, ConnectionMethod.Masque);
        Check(!args.Contains("--tls-ciphers"), "no cipher list is sent when none is configured");
        Check(!args.Contains("--tls-groups"), "no group order is sent when none is configured");
        Check(!args.Contains("--disable-grease"),
              "GREASE is left alone by default, because Chrome does send it");

        const string ciphers = "ECDHE-ECDSA-AES128-GCM-SHA256:ECDHE-RSA-AES128-GCM-SHA256";

        var dflt = new UserSettings { AetherTlsCiphers = ciphers, AetherTlsGroups = "X25519:P-256" };
        args.Clear();
        AetherExtras.AddOptional(args, dflt, exe, ConnectionMethod.Masque);
        var dci = args.IndexOf("--tls-ciphers");
        Check(dci >= 0 && args[dci + 1] == ciphers,
              "the default HTTP/2 transport passes a configured cipher list through",
              dci >= 0 ? args[dci + 1] : "(not sent)");

        var quic = new UserSettings
        {
            AetherTlsCiphers = ciphers,
            AetherTlsGroups = "X25519:P-256",
            AetherMasqueTransport = "h3",
            AetherMasqueQuic = true,
        };
        args.Clear();
        AetherExtras.AddOptional(args, quic, exe, ConnectionMethod.Masque);
        Check(!args.Contains("--tls-ciphers"),
              "a cipher list is withheld on QUIC, where TLS 1.2 does not exist");

        var h2 = new UserSettings
        {
            AetherTlsCiphers = "  " + ciphers + "  ",
            AetherTlsGroups = " X25519:P-256 ",
            AetherDisableGrease = true,
            AetherMasqueTransport = "h2",
            AetherMasqueQuic = false,
        };
        args.Clear();
        AetherExtras.AddOptional(args, h2, exe, ConnectionMethod.Masque);

        var ci = args.IndexOf("--tls-ciphers");
        Check(ci >= 0 && args[ci + 1] == ciphers,
              "on HTTP/2 the cipher list reaches aether unchanged, only trimmed",
              ci >= 0 ? args[ci + 1] : "(not sent)");

        var gi = args.IndexOf("--tls-groups");
        Check(gi >= 0 && args[gi + 1] == "X25519:P-256",
              "reordering the groups reaches aether, which is the point of the setting",
              gi >= 0 ? args[gi + 1] : "(not sent)");

        Check(args.Contains("--disable-grease"), "GREASE can be omitted when asked for");
        Check(AetherExtras.AppliedOptionalFlags.Contains("tls-ciphers")
              && AetherExtras.AppliedOptionalFlags.Contains("tls-groups")
              && AetherExtras.AppliedOptionalFlags.Contains("disable-grease"),
              "all three are recorded as applied",
              string.Join(",", AetherExtras.AppliedOptionalFlags));

        var stub = StubBinary();
        args.Clear();
        AetherExtras.AddOptional(
            args, new UserSettings { AetherTlsCiphers = ciphers, AetherDisableGrease = true },
            stub, ConnectionMethod.Masque);
        Check(!args.Contains("--tls-ciphers") && !args.Contains("--disable-grease"),
              "a binary lacking these flags is never sent them");

        return f;
    }

    private static int TestEchDns()
    {
        var f = 0;
        var exe = FindBundled();
        if (exe is null) return f;

        Check(AetherExtras.BuildEchDnsField(
                  new UserSettings { AetherEchDns = "https://d.example/dns-query", CustomDnsUdp = "9.9.9.9" })
              == "https://d.example/dns-query",
              "an explicit ECH resolver is used as given");

        var both = AetherExtras.BuildEchDnsField(new UserSettings
        {
            CustomDnsUdp = "9.9.9.9",
            CustomDnsDoh = "https://dns.quad9.net/dns-query",
        });
        Check(both.StartsWith("https://", StringComparison.OrdinalIgnoreCase),
              "an encrypted custom resolver is preferred over plain UDP", both);

        var udpOnly = AetherExtras.BuildEchDnsField(new UserSettings { CustomDnsUdp = "9.9.9.9" });
        Check(udpOnly == "udp://9.9.9.9",
              "a bare resolver address is given the udp:// scheme aether expects", udpOnly);

        var strict = AetherExtras.BuildEchDnsField(
            new UserSettings { CustomDnsUdp = "9.9.9.9", CustomDnsStrict = true });
        Check(strict == "udp://9.9.9.9", "strict mode still yields the user's resolver", strict);

        Check(AetherExtras.BuildEchDnsField(new UserSettings()).Length == 0,
              "with nothing configured no --ech-dns is sent at all");

        var args = new List<string>();
        AetherExtras.AddOptional(
            args,
            new UserSettings { AetherEch = true, CustomDnsDoh = "https://dns.quad9.net/dns-query" },
            exe, ConnectionMethod.Masque);
        var i = args.IndexOf("--ech-dns");
        Check(i >= 0 && args[i + 1] == "https://dns.quad9.net/dns-query",
              "the user's encrypted resolver is handed to aether for the key lookup",
              i >= 0 ? args[i + 1] : "(not sent)");

        args.Clear();
        AetherExtras.AddOptional(args, new UserSettings { AetherEch = true }, exe, ConnectionMethod.Masque);
        Check(!args.Contains("--ech-domain"), "aether's own key domain is left alone when unset");

        args.Clear();
        AetherExtras.AddOptional(
            args, new UserSettings { AetherEch = true, AetherEchDomain = " ech.example " },
            exe, ConnectionMethod.Masque);
        i = args.IndexOf("--ech-domain");
        Check(i >= 0 && args[i + 1] == "ech.example",
              "a custom key domain reaches aether, trimmed", i >= 0 ? args[i + 1] : "(not sent)");

        return f;
    }

    private static int TestEchFallbackPolicy()
    {
        var f = 0;

        AetherExtras.ResetEchFallback();

        Check(!AetherExtras.ShouldDeferProvisioning(new UserSettings()),
              "with ECH off the app provisions first, exactly as before");
        Check(AetherExtras.ShouldDeferProvisioning(new UserSettings { AetherEch = true }),
              "with ECH on the app stands aside and lets aether register its own identity");

        Check(AetherExtras.TryClaimEchFallback(), "the SHARD fallback can be claimed");
        Check(!AetherExtras.TryClaimEchFallback(),
              "a second claim is refused, so two SHARD sessions cannot race the same work dir");
        Check(!AetherExtras.ShouldDeferProvisioning(new UserSettings { AetherEch = true }),
              "and once claimed, the next attempt does not stand aside again");

        AetherExtras.ResetEchFallback();
        Check(AetherExtras.ShouldDeferProvisioning(new UserSettings { AetherEch = true }),
              "a successful connect resets the latch and the fast path is tried again");
        Check(AetherExtras.TryClaimEchFallback(), "and the fallback becomes claimable again");

        AetherExtras.ResetEchFallback();
        return f;
    }

    private static int TestAgainstBundledBinary()
    {
        var f = 0;
        var exe = FindBundled();
        if (exe is null)
        {
            Check(false, "the bundled aether.exe was found", "expected Resources/aether/aether.exe");
            return f;
        }

        var version = AetherCapabilities.Version(exe);
        Check(version.Contains("2.4.0", StringComparison.Ordinal),
              "the bundled aether reports 2.4.0", version);
        Check(version.StartsWith("aether", StringComparison.OrdinalIgnoreCase),
              "the banner reads like a version line", version);

        Check(AetherCapabilities.Supports(exe, "--gool"), "the bundled build has --gool");
        Check(AetherCapabilities.Supports(exe, "--gool-classic"),
              "the bundled build has --gool-classic, so the old gool is still reachable");

        Check(AetherCapabilities.Supports(exe, "--tls-verify"), "the bundled build has --tls-verify");

        Check(AetherCapabilities.Supports(exe, "--tor"), "the bundled build has --tor");
        Check(AetherCapabilities.Supports(exe, "--tor-relays"), "the bundled build has --tor-relays");
        Check(AetherCapabilities.Supports(exe, "--ech"), "the bundled build has --ech");
        Check(AetherCapabilities.Supports(exe, "--exit-loc-secs"),
              "the bundled build has --exit-loc-secs");

        Check(!AetherCapabilities.Supports(exe, "--definitely-not-a-flag"),
              "an invented flag is reported as unsupported");
        Check(!AetherCapabilities.Supports(exe, "--psiphon-reversee"),
              "a near-miss flag name is not mistaken for a real one");

        Check(!AetherCapabilities.Supports(Path.Combine(Path.GetTempPath(), "no-such-aether.exe"), "--gool"),
              "a missing binary reports nothing supported rather than throwing");

        return f;
    }

    private static int TestGoolMode()
    {
        var f = 0;

        Check(!AetherExtras.IsClassicGool(new UserSettings { AetherGoolMode = "masque" }),
              "masque mode is the default and is not classic");
        Check(AetherExtras.IsClassicGool(new UserSettings { AetherGoolMode = "classic" }),
              "classic mode is recognised");
        Check(AetherExtras.IsClassicGool(new UserSettings { AetherGoolMode = "CLASSIC" }),
              "mode matching is case-insensitive");
        Check(!AetherExtras.IsClassicGool(new UserSettings { AetherGoolMode = "" }),
              "an empty value falls back to the default, not to classic");

        var old = FakeBinaryWithHelp("  --gool, --wiw    use WARP-in-WARP\n  --dns <list>\n");
        var s = new UserSettings { AetherGoolMode = "classic" };

        var args = new List<string>();
        AetherExtras.AddOptional(args, s, old, ConnectionMethod.WarpOnWarp);
        Check(!args.Contains("--gool-classic"),
              "an older binary is never sent --gool-classic");

        return f;
    }

    private static int TestOptionalGating()
    {
        var f = 0;
        var exe = FindBundled();
        if (exe is null) return f;

        var args = new List<string>();
        var notes = new List<string>();
        AetherExtras.AddOptional(
            args, new UserSettings { AetherTlsVerify = true }, exe,
            ConnectionMethod.Masque, notes.Add);

        Check(args.Contains("--tls-verify"),
              "--tls-verify is sent when the binary supports it");
        Check(AetherExtras.AppliedOptionalFlags.Contains("tls-verify"),
              "and it is recorded as applied", string.Join(",", AetherExtras.AppliedOptionalFlags));

        var stub = StubBinary();
        args.Clear();
        notes.Clear();
        AetherExtras.AddOptional(
            args, new UserSettings { AetherTlsVerify = true, AetherEch = true }, stub,
            ConnectionMethod.Masque, notes.Add);

        Check(!args.Contains("--tls-verify"),
              "a binary without the flag is never sent it");
        Check(!args.Contains("--ech"),
              "and neither is any other flag it does not have");
        Check(notes.Any(n => n.Contains("--tls-verify", StringComparison.Ordinal)
                             && n.Contains("skipped", StringComparison.OrdinalIgnoreCase)),
              "the skip is reported, so 'I turned it on and nothing happened' is explainable",
              string.Join(" | ", notes));

        args.Clear();
        AetherExtras.AddOptional(
            args, new UserSettings { AetherEch = true }, exe, ConnectionMethod.Masque);
        Check(args.Contains("--ech"), "--ech is sent when the binary supports it");
        Check(AetherExtras.AppliedOptionalFlags.Contains("ech=auto"),
              "and it is recorded as applied", string.Join(",", AetherExtras.AppliedOptionalFlags));

        args.Clear();
        AetherExtras.AddOptional(
            args, new UserSettings { AetherTor = true, AetherTorRelays = true }, exe,
            ConnectionMethod.Masque);
        Check(args.Contains("--tor"), "--tor is sent when asked for");
        Check(args.Contains("--tor-relays"), "--tor-relays is sent when asked for");
        Check(args.Contains("web"), "--tor-relays gets the web port set");

        args.Clear();
        AetherExtras.AddOptional(
            args, new UserSettings { AetherTor = true }, exe, ConnectionMethod.WireGuard);
        Check(args.Contains("--tor"),
              "carrying Tor is offered on plain WireGuard: Tor speaks TCP and WireGuard carries TCP");

        args.Clear();
        AetherExtras.AddOptional(
            args, new UserSettings { AetherExitLocSecs = 30 }, exe, ConnectionMethod.Masque);
        Check(!args.Contains("--exit-loc-secs"),
              "--exit-loc-secs is withheld when no exit country filter is set");

        args.Clear();
        AetherExtras.AddOptional(
            args, new UserSettings { AetherExitLocSecs = 30, AetherExitLoc = "!IR,RU" },
            exe, ConnectionMethod.Masque);
        Check(args.Contains("--exit-loc-secs"), "--exit-loc-secs is sent alongside --exit-loc");
        Check(args.Contains("30"), "with the requested interval");

        args.Clear();
        AetherExtras.AddOptional(
            args, new UserSettings { AetherExitLocSecs = -5, AetherExitLoc = "!IR" },
            exe, ConnectionMethod.Masque);
        Check(!args.Contains("--exit-loc-secs"),
              "a non-positive interval is withheld rather than clamped up to a minimum");

        args.Clear();
        AetherExtras.AddOptional(
            args, new UserSettings { AetherExitLocSecs = 999999, AetherExitLoc = "!IR" },
            exe, ConnectionMethod.Masque);
        var idx = args.IndexOf("--exit-loc-secs");
        Check(idx >= 0 && int.Parse(args[idx + 1]) is > 0 and <= 3600,
              "an absurd interval is clamped into a sane range",
              idx >= 0 ? args[idx + 1] : "(not sent)");

        return f;
    }

    private static int TestDnsField()
    {
        var f = 0;

        Check(!string.IsNullOrWhiteSpace(AetherExtras.BuildDnsField(new UserSettings())),
              "with nothing configured the field still names resolvers");
        Check(AetherExtras.BuildDnsField(new UserSettings()).Contains("1.1.1.1",
                  StringComparison.Ordinal),
              "and falls back to the historical default",
              AetherExtras.BuildDnsField(new UserSettings()));

        var custom = AetherExtras.BuildDnsField(
            new UserSettings { CustomDnsUdp = "9.9.9.9", CustomDnsDoh = "https://dns.quad9.net/dns-query" });
        Check(custom.StartsWith("9.9.9.9", StringComparison.Ordinal),
              "the user's resolver is first in aether's --dns", custom);
        Check(custom.Contains("https://dns.quad9.net/dns-query", StringComparison.Ordinal),
              "a DoH entry survives into aether's --dns with its scheme intact", custom);
        Check(custom.Contains("1.1.1.1", StringComparison.Ordinal),
              "and the built-in defaults remain as the fallback", custom);

        Check(!custom.Contains(' '), "no spaces in the field", custom);

        var strict = AetherExtras.BuildDnsField(
            new UserSettings { CustomDnsUdp = "9.9.9.9", CustomDnsStrict = true });
        Check(strict.Contains("9.9.9.9", StringComparison.Ordinal),
              "a strict custom resolver is still passed through", strict);
        Check(!strict.Contains("1.1.1.1", StringComparison.Ordinal),
              "and strict really does suppress the defaults", strict);

        return f;
    }

    private static int TestGoolLayout()
    {
        var f = 0;

        var classic = AetherExtras.GoolLayoutFor(new UserSettings { AetherGoolMode = "classic" });
        Check(classic.DeviceFile == "aether.toml",
              "classic gool reads aether.toml for the outer hop", classic.DeviceFile);
        Check(classic.WireGuardFile == "aether-secondary.toml",
              "classic gool reads aether-secondary.toml for the inner hop", classic.WireGuardFile);
        Check(!classic.DeviceIsMasque,
              "classic gool is WARP-in-WARP, so the outer file is a WireGuard config, not MASQUE");

        var masque = AetherExtras.GoolLayoutFor(new UserSettings { AetherGoolMode = "masque" });
        Check(masque.DeviceFile == "aether-masque.toml",
              "the new gool reads aether-masque.toml for its outer device", masque.DeviceFile);
        Check(masque.WireGuardFile == "aether-masque-gool.toml",
              "the new gool names its carried WireGuard identity aether-masque-gool.toml",
              masque.WireGuardFile);
        Check(masque.DeviceIsMasque,
              "the new gool's device file is a MASQUE config, so it is validated as one");

        Check(!classic.DeviceFile.Equals(masque.DeviceFile, StringComparison.Ordinal)
              && !classic.WireGuardFile.Equals(masque.WireGuardFile, StringComparison.Ordinal),
              "the two gool layouts share no file names, so switching mode cannot reuse one");

        Check(!masque.DeviceFile.Equals("aether.toml", StringComparison.Ordinal)
              && !masque.WireGuardFile.Equals("aether.toml", StringComparison.Ordinal),
              "the new gool does not share plain WireGuard's identity file");

        foreach (var mode in new[] { "", "  ", "nonsense", "MASQUE" })
        {
            Check(!AetherExtras.IsClassicGool(new UserSettings { AetherGoolMode = mode }),
                  $"an unrecognised gool mode (\"{mode}\") falls back to masque, not classic");
        }
        Check(!AetherExtras.IsClassicGool(new UserSettings { AetherGoolMode = null! }),
              "a null gool mode falls back to masque");

        return f;
    }

    private static int TestFastDefaultsChromeCompatAndLastConn()
    {
        var f = 0;

        Check(AetherExtras.DefaultValidateSecs == 12,
              "default validate-secs is raised to 12s so cached edge reuse survives filtered validation");

        var defaults = new UserSettings();
        Check(!defaults.AetherFragment, "H2 fragmentation defaults to off to match aether 2.4.0 fast reconnect");
        Check(defaults.AetherScanMode == "turbo"
              && defaults.AetherScanModeMasque == "turbo"
              && defaults.AetherScanModeWireguard == "turbo"
              && defaults.AetherScanModeWarp == "turbo",
              "scan modes default to turbo across Aether protocols");
        Check(defaults.AetherNoizeMasque == "firewall" && defaults.AetherNoizeMim == "firewall",
              "MASQUE and MiM default noize profile is firewall");

        var exe = FindBundled();
        if (exe is not null)
        {
            var args = new List<string>();
            var compat = new UserSettings
            {
                AetherTlsFingerprint = "chrome-compat",
                AetherMasqueTransport = "h2",
                AetherMasqueQuic = false,
                AetherTlsKeyShares = "X25519:P-256",
                AetherDisableSessionTicket = true,
                AetherEnableEchGrease = true,
            };
            AetherExtras.AddOptional(args, compat, exe, ConnectionMethod.Masque);
            Check(args.Contains("--tls-ciphers")
                  && args.Contains("--tls13-ciphers")
                  && args.Contains("--tls-groups")
                  && args.Contains("--tls-sigalgs")
                  && args.Contains("--disable-grease")
                  && args.Contains("--disable-ocsp-stapling")
                  && args.Contains("--disable-sct")
                  && args.Contains("--disable-extension-permutation"),
                  "chrome-compat preset emits full aether 2.4.0 Chrome TLS fingerprint flags on HTTP/2");
            Check(args.Contains("--tls-key-shares")
                  && args.Contains("--disable-session-ticket")
                  && args.Contains("--enable-ech-grease"),
                  "individual 2.4.0 TLS flags are forwarded alongside the preset");
        }

        Check(AetherExtras.LastConnBucketKey(ConnectionMethod.Masque, "h2", false) == "masque-h2",
              "Masque H2 bucket key is masque-h2");
        Check(AetherExtras.LastConnBucketKey(ConnectionMethod.WarpOnWarp, "h2", false) == "gool-h2",
              "Gool H2 bucket key is gool-h2");

        var tmpDir = Path.Combine(Path.GetTempPath(), "se7en-lastconn-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmpDir);
        try
        {
            var masqueLive = Path.Combine(tmpDir, "aether-masque-lastconn.toml");
            File.WriteAllText(masqueLive, "endpoint = \"162.159.199.241:443\"\n");
            AetherExtras.SnapshotLastConn(tmpDir, "masque-h2");

            AetherExtras.RestoreLastConn(tmpDir, "gool-h2", cacheEnabled: true);
            Check(!File.Exists(masqueLive), "switching to unseeded gool-h2 bucket hides masque-h2 endpoint");

            File.WriteAllText(masqueLive, "endpoint = \"162.159.198.10:443\"\n");
            AetherExtras.SnapshotLastConn(tmpDir, "gool-h2");

            AetherExtras.RestoreLastConn(tmpDir, "masque-h2", cacheEnabled: true);
            Check(File.Exists(masqueLive) && File.ReadAllText(masqueLive).Contains("162.159.199.241"),
                  "switching back to masque-h2 restores masque-h2 cached endpoint");

            AetherExtras.RestoreLastConn(tmpDir, "masque-h2", cacheEnabled: false);
            Check(!File.Exists(masqueLive), "disabling edge cache clears active lastconn file");
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { }
        }

        return f;
    }

    private static string StubBinary()
    {
        var dir = Path.Combine(Path.GetTempPath(),
                               "se7en-aether-stub-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, "stub.exe");
        File.WriteAllBytes(exe, new byte[] { 0x4D, 0x5A, 0x90, 0x00 });
        return exe;
    }

    private static string? FindBundled()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 12 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "Se7enPro", "Resources", "aether", "aether.exe");
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir.TrimEnd('\\'));
        }
        return null;
    }

    private static string FakeBinaryWithHelp(string help)
    {
        var dir = Path.Combine(Path.GetTempPath(), "se7en-aether-fake-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, "fake-aether.cmd");

        File.WriteAllText(exe, "@echo off\r\necho " + help.Replace("\n", "\r\necho ").Trim() + "\r\n");
        return exe;
    }

    private static void Check(bool ok, string what, string? detail = null) => Program.Check(ok, what, detail);
}