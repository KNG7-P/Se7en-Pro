using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Se7enPro.Models;
using Se7enPro.Services;

namespace VerifyCriticalFixes;

/// <summary>
/// Tests for the aether 2.3.0 wiring.
/// </summary>
/// <remarks>
/// Two things are worth pinning here, and both came from reading the release notes rather
/// than the binary:
///
///  1. --gool changed MEANING in 2.3.0. Sending "--gool" after upgrading silently turns
///     WARP-in-WARP into the new MASQUE-carried gool, so the mode has to be explicit.
///
///  2. The release notes announce --tls-verify, but the shipped Windows 2.3.0 binary does
///     not have it. That is the whole reason AetherCapabilities exists, and it is proved
///     here against the REAL bundled binary rather than a fixture.
/// </remarks>
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

        Program.Section("Aether — ECH key resolver");
        failures += TestEchDns();

        Program.Section("Aether — ECH first, SHARD fallback");
        failures += TestEchFallbackPolicy();

        Program.Section("Aether — stale staged core is detected");
        failures += TestStagedCoreRefresh();

        return failures;
    }

    /// <summary>
    /// A bundled upgrade must not be shadowed by the copy staged in %LOCALAPPDATA%.
    /// </summary>
    /// <remarks>
    /// This is the bug that made the 2.3.0 upgrade look like it had not happened: the repo and
    /// the build output both carried 2.3.0, while %LOCALAPPDATA%\Se7en\aether still held 2.1.0
    /// from an earlier run, and that is the file that executes.
    /// </remarks>
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

        // Cannot-tell is not grounds for deleting a working core on every single connect.
        Check(!AetherExtras.ShouldRefreshStagedCore("", ""),
              "two unknowns do not trigger a refresh");
        Check(!AetherExtras.ShouldRefreshStagedCore("aether 2.3.0", ""),
              "an unreadable staged version does not trigger a refresh");
        Check(!AetherExtras.ShouldRefreshStagedCore(null, null),
              "and neither does two nulls");
        Check(!AetherExtras.ShouldRefreshStagedCore("  ", "aether 2.1.0"),
              "a blank bundled version is not grounds for a refresh either");

        // The bundled copy really is 2.3.0, and it really is readable as such.
        var exe = FindBundled();
        if (exe is not null)
        {
            var v = AetherCapabilities.Version(exe);
            Check(v.Contains("2.3.0", StringComparison.Ordinal),
                  "the bundled core reports 2.3.0, so this check has something to compare", v);
        }

        return f;
    }

    /// <summary>
    /// A flag that does nothing must not be sent, and the user must be able to see why.
    /// </summary>
    /// <remarks>
    /// Asked for directly: "are these new changes only for masque? not for the other aether
    /// protocols?" - which turned out to be a real gap, not a misunderstanding.
    ///
    /// What aether's help actually says:
    ///
    ///   --ech         "on the MASQUE handshakes"          - needs a handshake to hide in
    ///   --tls-ciphers "TLS 1.2 cipher suites" / "HTTP/3 lists none: QUIC offers TLS 1.3 alone"
    ///   --tls-groups  same TLS 1.2 restriction
    ///   --disable-grease  GREASE rides in TLS 1.2 and 1.3 alike, so this one does apply on QUIC
    ///
    /// Plain --warp is a bare UDP socket with no TLS at all, so none of them apply there.
    /// Sending them anyway is not harmless: it looks configured and changes nothing, which is
    /// the exact failure mode this whole capability probe exists to prevent.
    /// </remarks>
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

        // ---- plain WireGuard: a bare UDP socket, no ClientHello ----
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

        // ---- QUIC: TLS 1.3 only ----
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

        // ---- HTTP/2: everything applies ----
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

        // ---- the transport flag decides, and QUIC is the default ----
        Check(AetherExtras.TlsSurfaceFor(
                  new UserSettings { AetherMasqueQuic = true }, ConnectionMethod.Masque)
              == AetherExtras.TlsSurface.Http3,
              "the QUIC setting switches the surface for MASQUE");
        Check(AetherExtras.TlsSurfaceFor(
                  new UserSettings { AetherMasqueQuic = false }, ConnectionMethod.Masque)
              == AetherExtras.TlsSurface.Http2,
              "and turning it off switches it back to HTTP/2");

        // ---- Tor is not TLS-shaped and works on every method, WireGuard included ----
        // Tor speaks TCP and a WireGuard tunnel carries TCP fine. An earlier version
        // withheld --tor there on the assumption it was MASQUE-only; it is
        // --tor-REVERSE that aether refuses alongside --wg, and that is not exposed here.
        foreach (var m in new[] { ConnectionMethod.WireGuard, ConnectionMethod.Masque,
                                  ConnectionMethod.MasqueInMasque, ConnectionMethod.WarpOnWarp })
        {
            args.Clear();
            AetherExtras.AddOptional(
                args, new UserSettings { AetherTor = true, AetherTorRelays = true }, exe, m);
            Check(args.Contains("--tor") && args.Contains("--tor-relays"),
                  $"carrying Tor inside the tunnel is offered for {m}");
        }

        // ---- and the reason must be reportable, not just enforced ----
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

    /// <summary>
    /// The cipher/group/GREASE levers are passed through verbatim, and only when asked for.
    /// </summary>
    /// <remarks>
    /// Verbatim matters: these are OpenSSL-style expressions, not a list the app should be
    /// reformatting. Rewriting "ALL:!aPSK:!ECDSA+SHA1:!3DES" into something tidier would
    /// change which suites a middlebox sees.
    /// </remarks>
    private static int TestTlsFingerprintFlags()
    {
        var f = 0;
        var exe = FindBundled();
        if (exe is null) return f;

        // Nothing configured: nothing sent. The default fingerprint is Chrome's, and the
        // release notes do not say that is wrong, so the app has no business overriding it.
        var args = new List<string>();
        AetherExtras.AddOptional(args, new UserSettings(), exe, ConnectionMethod.Masque);
        Check(!args.Contains("--tls-ciphers"), "no cipher list is sent when none is configured");
        Check(!args.Contains("--tls-groups"), "no group order is sent when none is configured");
        Check(!args.Contains("--disable-grease"),
              "GREASE is left alone by default, because Chrome does send it");

        const string ciphers = "ECDHE-ECDSA-AES128-GCM-SHA256:ECDHE-RSA-AES128-GCM-SHA256";

        // The transport has to be HTTP/2 for a TLS 1.2 list to mean anything, and the
        // shipping default is QUIC - so on a default configuration these are withheld even
        // when configured. Worth pinning: a user who typed a cipher list on the default
        // transport would otherwise see it silently ignored.
        var quic = new UserSettings { AetherTlsCiphers = ciphers, AetherTlsGroups = "X25519:P-256" };
        args.Clear();
        AetherExtras.AddOptional(args, quic, exe, ConnectionMethod.Masque);
        Check(!args.Contains("--tls-ciphers"),
              "a cipher list is withheld on the default QUIC transport, where TLS 1.2 does not exist");

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

        // A binary without them must not be handed them.
        var stub = StubBinary();
        args.Clear();
        AetherExtras.AddOptional(
            args, new UserSettings { AetherTlsCiphers = ciphers, AetherDisableGrease = true },
            stub, ConnectionMethod.Masque);
        Check(!args.Contains("--tls-ciphers") && !args.Contains("--disable-grease"),
              "a binary lacking these flags is never sent them");

        return f;
    }

    /// <summary>
    /// The ECH key lookup goes to the user's own resolver.
    /// </summary>
    /// <remarks>
    /// This is the wiring that makes Custom DNS matter on this engine. The key is fetched
    /// BEFORE any tunnel exists, so unlike every other lookup in the app there is no tunnel to
    /// hide behind - an encrypted key fetched over a blocked plain port is as visible as no
    /// encryption at all.
    /// </remarks>
    private static int TestEchDns()
    {
        var f = 0;
        var exe = FindBundled();
        if (exe is null) return f;

        // Explicit wins over everything.
        Check(AetherExtras.BuildEchDnsField(
                  new UserSettings { AetherEchDns = "https://d.example/dns-query", CustomDnsUdp = "9.9.9.9" })
              == "https://d.example/dns-query",
              "an explicit ECH resolver is used as given");

        // An encrypted custom resolver beats plain UDP, even when UDP is listed first.
        var both = AetherExtras.BuildEchDnsField(new UserSettings
        {
            CustomDnsUdp = "9.9.9.9",
            CustomDnsDoh = "https://dns.quad9.net/dns-query",
        });
        Check(both.StartsWith("https://", StringComparison.OrdinalIgnoreCase),
              "an encrypted custom resolver is preferred over plain UDP", both);

        // Plain UDP still works, but must gain the scheme aether expects.
        var udpOnly = AetherExtras.BuildEchDnsField(new UserSettings { CustomDnsUdp = "9.9.9.9" });
        Check(udpOnly == "udp://9.9.9.9",
              "a bare resolver address is given the udp:// scheme aether expects", udpOnly);

        // Strict mode is honoured: a suppressed default cannot sneak back in.
        var strict = AetherExtras.BuildEchDnsField(
            new UserSettings { CustomDnsUdp = "9.9.9.9", CustomDnsStrict = true });
        Check(strict == "udp://9.9.9.9", "strict mode still yields the user's resolver", strict);

        // Nothing configured: leave aether alone rather than send an empty --ech-dns.
        Check(AetherExtras.BuildEchDnsField(new UserSettings()).Length == 0,
              "with nothing configured no --ech-dns is sent at all");

        // And it must actually land on the command line when ECH is on.
        var args = new List<string>();
        AetherExtras.AddOptional(
            args,
            new UserSettings { AetherEch = true, CustomDnsDoh = "https://dns.quad9.net/dns-query" },
            exe, ConnectionMethod.Masque);
        var i = args.IndexOf("--ech-dns");
        Check(i >= 0 && args[i + 1] == "https://dns.quad9.net/dns-query",
              "the user's encrypted resolver is handed to aether for the key lookup",
              i >= 0 ? args[i + 1] : "(not sent)");

        // The key domain is only sent when the user actually set one.
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

    /// <summary>
    /// With ECH on, the app stands aside; if that fails, exactly one SHARD fallback.
    /// </summary>
    /// <remarks>
    /// The reason for standing aside is that the app's own provisioner uses HttpClient, which
    /// cannot encrypt the server name. On a network filtering the WARP API it is precisely the
    /// request that fails, and it runs first and blocking.
    /// </remarks>
    private static int TestEchFallbackPolicy()
    {
        var f = 0;

        AetherExtras.ResetEchFallback();

        Check(!AetherExtras.ShouldDeferProvisioning(new UserSettings()),
              "with ECH off the app provisions first, exactly as before");
        Check(AetherExtras.ShouldDeferProvisioning(new UserSettings { AetherEch = true }),
              "with ECH on the app stands aside and lets aether register its own identity");

        // The fallback is claimed once, not once per caller.
        Check(AetherExtras.TryClaimEchFallback(), "the SHARD fallback can be claimed");
        Check(!AetherExtras.TryClaimEchFallback(),
              "a second claim is refused, so two SHARD sessions cannot race the same work dir");
        Check(!AetherExtras.ShouldDeferProvisioning(new UserSettings { AetherEch = true }),
              "and once claimed, the next attempt does not stand aside again");

        // Reset on a good connect, so a later connect gets the fast path back.
        AetherExtras.ResetEchFallback();
        Check(AetherExtras.ShouldDeferProvisioning(new UserSettings { AetherEch = true }),
              "a successful connect resets the latch and the fast path is tried again");
        Check(AetherExtras.TryClaimEchFallback(), "and the fallback becomes claimable again");

        AetherExtras.ResetEchFallback();
        return f;
    }

    // ---------------------------------------------------------------- cases

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
        Check(version.Contains("2.3.0", StringComparison.Ordinal),
              "the bundled aether reports 2.3.0", version);
        Check(version.StartsWith("aether", StringComparison.OrdinalIgnoreCase),
              "the banner reads like a version line", version);

        // The whole premise of the capability probe.
        Check(AetherCapabilities.Supports(exe, "--gool"), "2.3.0 has --gool");
        Check(AetherCapabilities.Supports(exe, "--gool-classic"),
              "2.3.0 has --gool-classic, so the old gool is still reachable");

        // --tls-verify is NOT in --help, but aether's own startup log says
        //   "tls verification: disabled (default; --tls-verify enables pinning)"
        // so it works. A probe that trusted --help alone would withhold a usable flag -
        // which is exactly the bug this check exists to prevent.
        Check(AetherCapabilities.Supports(exe, "--tls-verify"),
              "--tls-verify works even though --help omits it, and the probe finds it anyway");

        // Whole-token matching: --tor must not match --tor-relays.
        Check(AetherCapabilities.Supports(exe, "--tor"), "2.3.0 has --tor");
        Check(AetherCapabilities.Supports(exe, "--tor-relays"), "2.3.0 has --tor-relays");
        Check(AetherCapabilities.Supports(exe, "--ech"), "2.3.0 has --ech");
        Check(AetherCapabilities.Supports(exe, "--exit-loc-secs"), "2.3.0 has --exit-loc-secs");

        // Flags that do not exist must not be reported as present. This also covers the
        // binary-string path: an invented flag is absent from the image too.
        Check(!AetherCapabilities.Supports(exe, "--definitely-not-a-flag"),
              "an invented flag is reported as unsupported");
        Check(!AetherCapabilities.Supports(exe, "--psiphon-reversee"),
              "a near-miss flag name is not mistaken for a real one");

        // A missing binary must answer "no", not throw. A tunnel start depends on this.
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

        // An older build only has the original meaning, so classic must degrade to --gool
        // rather than sending a flag that build cannot parse.
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

        // TLS verification is absent from --help but present in the binary, so it must
        // actually be sent. An earlier version of this test asserted the opposite and was
        // the reason the flag never worked.
        var args = new List<string>();
        var notes = new List<string>();
        AetherExtras.AddOptional(
            args, new UserSettings { AetherTlsVerify = true }, exe,
            ConnectionMethod.Masque, notes.Add);

        Check(args.Contains("--tls-verify"),
              "--tls-verify is sent when the binary supports it, even though --help omits it");
        Check(AetherExtras.AppliedOptionalFlags.Contains("tls-verify"),
              "and it is recorded as applied", string.Join(",", AetherExtras.AppliedOptionalFlags));

        // A binary that genuinely lacks it must still have it withheld and REPORTED.
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

        // ECH exists in the real binary, so it must actually be sent.
        args.Clear();
        AetherExtras.AddOptional(
            args, new UserSettings { AetherEch = true }, exe, ConnectionMethod.Masque);
        Check(args.Contains("--ech"), "--ech is sent when the binary supports it");
        Check(AetherExtras.AppliedOptionalFlags.Contains("ech=auto"),
              "and it is recorded as applied", string.Join(",", AetherExtras.AppliedOptionalFlags));

        // Tor
        args.Clear();
        AetherExtras.AddOptional(
            args, new UserSettings { AetherTor = true, AetherTorRelays = true }, exe,
            ConnectionMethod.Masque);
        Check(args.Contains("--tor"), "--tor is sent when asked for");
        Check(args.Contains("--tor-relays"), "--tor-relays is sent when asked for");
        Check(args.Contains("web"), "--tor-relays gets the web port set");

        // Tor is not a TLS feature, so it is not gated on the transport at all.
        args.Clear();
        AetherExtras.AddOptional(
            args, new UserSettings { AetherTor = true }, exe, ConnectionMethod.WireGuard);
        Check(args.Contains("--tor"),
              "carrying Tor is offered on plain WireGuard: Tor speaks TCP and WireGuard carries TCP");

        // --exit-loc-secs is meaningless without --exit-loc.
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

        // A zero or negative interval must not reach aether at all.
        args.Clear();
        AetherExtras.AddOptional(
            args, new UserSettings { AetherExitLocSecs = -5, AetherExitLoc = "!IR" },
            exe, ConnectionMethod.Masque);
        Check(!args.Contains("--exit-loc-secs"),
              "a non-positive interval is withheld rather than clamped up to a minimum");

        // A nonsense interval is clamped rather than passed through to aether.
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

        // Never empty: aether with an empty --dns would have no resolver at all.
        Check(!string.IsNullOrWhiteSpace(AetherExtras.BuildDnsField(new UserSettings())),
              "with nothing configured the field still names resolvers");
        Check(AetherExtras.BuildDnsField(new UserSettings()).Contains("1.1.1.1",
                  StringComparison.Ordinal),
              "and falls back to the historical default",
              AetherExtras.BuildDnsField(new UserSettings()));

        // The user's resolver must lead, which is the whole point of the ordering rule.
        var custom = AetherExtras.BuildDnsField(
            new UserSettings { CustomDnsUdp = "9.9.9.9", CustomDnsDoh = "https://dns.quad9.net/dns-query" });
        Check(custom.StartsWith("9.9.9.9", StringComparison.Ordinal),
              "the user's resolver is first in aether's --dns", custom);
        Check(custom.Contains("https://dns.quad9.net/dns-query", StringComparison.Ordinal),
              "a DoH entry survives into aether's --dns with its scheme intact", custom);
        Check(custom.Contains("1.1.1.1", StringComparison.Ordinal),
              "and the built-in defaults remain as the fallback", custom);

        // No whitespace: aether splits this on commas.
        Check(!custom.Contains(' '), "no spaces in the field", custom);

        // Strict mode with only an encrypted resolver still yields something dialable.
        var strict = AetherExtras.BuildDnsField(
            new UserSettings { CustomDnsUdp = "9.9.9.9", CustomDnsStrict = true });
        Check(strict.Contains("9.9.9.9", StringComparison.Ordinal),
              "a strict custom resolver is still passed through", strict);
        Check(!strict.Contains("1.1.1.1", StringComparison.Ordinal),
              "and strict really does suppress the defaults", strict);

        return f;
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// Pins the file names each gool mode reads.
    /// </summary>
    /// <remarks>
    /// These are not guesses. They come from aether 2.3.0's own log lines, captured by
    /// running the real binary:
    ///
    ///   --gool-classic -> "provisioned and saved new warp identity to aether.toml"
    ///                     "provisioned and saved new warp identity to aether-secondary.toml"
    ///   --gool         -> "provisioned and saved new masque identity to aether-masque.toml"
    ///                     "registering the gool wireguard identity through the masque tunnel"
    ///                     "gool wireguard identity registered from inside warp and saved to
    ///                      aether-masque-gool.toml: device=0d7c0604..."
    ///
    /// Note the two device ids in that last pair of lines: the gool WireGuard identity is a
    /// DIFFERENT device from the masque one, and it is registered FROM INSIDE THE TUNNEL.
    /// That is deliberate - it is what makes the exit foreign - so the app must never
    /// pre-write that file.
    ///
    /// The failure this guards against is quiet, which is exactly why it needs a test:
    /// writing the wrong names does NOT make aether complain. It ignores them, registers its
    /// own account, and the app's provisioning chain has already burned up to 63 seconds
    /// producing files nobody read.
    /// </remarks>
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

        // The layouts must not overlap. If they did, switching mode would silently reuse
        // files this app considers valid but aether does not.
        Check(!classic.DeviceFile.Equals(masque.DeviceFile, StringComparison.Ordinal)
              && !classic.WireGuardFile.Equals(masque.WireGuardFile, StringComparison.Ordinal),
              "the two gool layouts share no file names, so switching mode cannot reuse one");

        // Neither layout may borrow plain WireGuard's outer file.
        Check(!masque.DeviceFile.Equals("aether.toml", StringComparison.Ordinal)
              && !masque.WireGuardFile.Equals("aether.toml", StringComparison.Ordinal),
              "the new gool does not share plain WireGuard's identity file");

        // An unset or unrecognised mode must land on the new gool, which is the safe
        // default: it does not depend on two separately registered devices.
        foreach (var mode in new[] { "", "  ", "nonsense", "MASQUE" })
        {
            Check(!AetherExtras.IsClassicGool(new UserSettings { AetherGoolMode = mode }),
                  $"an unrecognised gool mode (\"{mode}\") falls back to masque, not classic");
        }
        Check(!AetherExtras.IsClassicGool(new UserSettings { AetherGoolMode = null! }),
              "a null gool mode falls back to masque");

        return f;
    }

    /// <summary>A binary that contains no flag strings, for the "lacks the flag" case.</summary>
    private static string StubBinary()
    {
        var dir = Path.Combine(Path.GetTempPath(),
                               "se7en-aether-stub-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, "stub.exe");
        File.WriteAllBytes(exe, new byte[] { 0x4D, 0x5A, 0x90, 0x00 }); // MZ header, no flags
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

    /// <summary>
    /// Writes a stand-in that prints the given help, so the probe can be exercised against
    /// a build that lacks a flag without needing a second real binary.
    /// </summary>
    private static string FakeBinaryWithHelp(string help)
    {
        var dir = Path.Combine(Path.GetTempPath(), "se7en-aether-fake-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, "fake-aether.cmd");

        // cmd shim: the probe only reads stdout, and this keeps the test hermetic.
        File.WriteAllText(exe, "@echo off\r\necho " + help.Replace("\n", "\r\necho ").Trim() + "\r\n");
        return exe;
    }

    private static void Check(bool ok, string what, string? detail = null) => Program.Check(ok, what, detail);
}