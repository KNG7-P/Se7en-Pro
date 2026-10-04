using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using Se7enPro.Models;

namespace Se7enPro.Services;

/// <summary>
/// Extra aether capabilities and command line flag helpers.
/// </summary>
internal static class AetherExtras
{
    /// <summary>Flags actually put on the command line by the last Prepare(). For diagnostics.</summary>
    internal static List<string> AppliedOptionalFlags { get; } = new();

    internal static bool IsClassicGool(UserSettings s) =>
        string.Equals((s.AetherGoolMode ?? "masque").Trim(), "classic",
                      StringComparison.OrdinalIgnoreCase);

    /// <summary>Identity file layout for selected gool mode.</summary>
    internal readonly record struct GoolLayout(
        string DeviceFile, string WireGuardFile, bool DeviceIsMasque);

    internal static GoolLayout GoolLayoutFor(UserSettings s) =>
        IsClassicGool(s)
            ? new GoolLayout("aether.toml", "aether-secondary.toml", DeviceIsMasque: false)
            : new GoolLayout("aether-masque.toml", "aether-masque-gool.toml", DeviceIsMasque: true);

    /// <summary>Builds the resolver argument for aether --dns.</summary>
    internal static string BuildDnsField(UserSettings s)
    {
        var plan = new DnsResolverPolicy().Build(s, hasV6Address: false);
        var field = DnsResolverPolicy.ToCommaSeparatedResolvers(plan);
        return field.Length > 0 ? field : "1.1.1.1,1.0.0.1";
    }

    /// <summary>Builds the resolver argument for aether --ech-dns.</summary>
    internal static string BuildEchDnsField(UserSettings s)
    {
        if (!string.IsNullOrWhiteSpace(s.AetherEchDns)) return s.AetherEchDns.Trim();

        var typed = new List<string>();
        foreach (var (transport, raw) in new[]
                 {
                     (DnsTransport.Doh, s.CustomDnsDoh),
                     (DnsTransport.Dot, s.CustomDnsDot),
                     (DnsTransport.Udp, s.CustomDnsUdp),
                 })
        {
            var parsed = DnsSettings.ParseList(transport, raw, out _);
            foreach (var e in parsed) typed.Add(e.Raw.Trim());
        }

        foreach (var v in typed)
        {
            if (v.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || v.StartsWith("tls://", StringComparison.OrdinalIgnoreCase))
            {
                return v;
            }
        }

        foreach (var v in typed)
        {
            if (v.Length == 0 || v.Contains("://")) continue;
            if (IPAddress.TryParse(v, out _)) return "udp://" + v;
        }

        return "";
    }

    /// <summary>Whether aether should register its own identity directly via ECH.</summary>
    internal static bool ShouldDeferProvisioning(UserSettings s)
    {
        if (!s.AetherEch) return false;
        return Volatile.Read(ref _echGaveUp) == 0;
    }

    /// <summary>Claims the single ECH-fallback attempt.</summary>
    internal static bool TryClaimEchFallback() =>
        Interlocked.CompareExchange(ref _echGaveUp, 1, 0) == 0;

    /// <summary>Clears the fallback latch once a connection has actually come up.</summary>
    internal static void ResetEchFallback() => Interlocked.Exchange(ref _echGaveUp, 0);

    private static int _echGaveUp;

    /// <summary>What TLS, if any, this protocol actually puts on the wire.</summary>
    /// <remarks>
    /// Not decoration. Every TLS flag below behaves differently depending on which of these
    /// applies, and aether's own help is explicit about the difference:
    ///
    ///   --ech        "on the MASQUE handshakes"  - needs a TLS handshake to hide inside
    ///   --tls-ciphers  "TLS 1.2 cipher suites"   - and "HTTP/3 lists none: QUIC offers
    ///                 --tls-groups               TLS 1.3 alone"
    ///
    /// So plain WireGuard gets none of it (a bare UDP socket with no ClientHello), and QUIC
    /// gets ECH and GREASE but not the TLS 1.2 cipher list.
    /// </remarks>
    internal enum TlsSurface
    {
        /// <summary>No TLS at all. Plain --warp.</summary>
        None,

        /// <summary>TLS 1.2 over HTTP/2. The only place --tls-ciphers and --tls-groups bite.</summary>
        Http2,

        /// <summary>QUIC. TLS 1.3 only, so no TLS 1.2 cipher list.</summary>
        Http3,
    }

    /// <summary>
    /// Which TLS surface the selected protocol puts on the wire.
    /// </summary>
    /// <remarks>
    /// For gool the app sends neither --h2 nor --h3, so aether picks, and its default is
    /// QUIC - observed live: "[outer] MASQUE transport: HTTP/3 (QUIC) to 162.159.198.2:443".
    /// That is why the TLS 1.2 cipher list is reported as unavailable there rather than
    /// silently accepted and doing nothing.
    /// </remarks>
    internal static TlsSurface TlsSurfaceFor(UserSettings s, ConnectionMethod method)
    {
        switch (method)
        {
            case ConnectionMethod.WireGuard:
                return TlsSurface.None;

            case ConnectionMethod.WarpOnWarp:
                return TlsSurface.Http3;

            case ConnectionMethod.Masque:
            case ConnectionMethod.MasqueInMasque:
                var transport = NormalizeAetherTransport(s);
                return transport == "h3" ? TlsSurface.Http3 : TlsSurface.Http2;

            default:
                return TlsSurface.None;
        }
    }

    private static string NormalizeAetherTransport(UserSettings s)
    {
        if (!string.IsNullOrWhiteSpace(s.AetherMasqueTransport))
        {
            return s.AetherMasqueTransport.Trim().ToLowerInvariant();
        }
        return s.AetherMasqueQuic ? "h3" : "h2";
    }

    /// <summary>Why a TLS control is unavailable, or empty when it is available.</summary>
    internal static string UnavailableReason(TlsSurface surface, string flag)
    {
        var isTls12Only = flag is "--tls-ciphers" or "--tls-groups";

        if (surface == TlsSurface.None)
        {
            return "This protocol carries no TLS, so there is no handshake to change.";
        }
        if (isTls12Only && surface == TlsSurface.Http3)
        {
            return "This connection uses QUIC, which negotiates TLS 1.3 only. Aether takes no TLS 1.2 cipher list here.";
        }
        return "";
    }

    /// <summary>Whether a staged core reports the same version as the bundled core.</summary>
    internal static bool ShouldRefreshStagedCore(string? bundledVersion, string? stagedVersion)
    {
        var bundled = (bundledVersion ?? "").Trim();
        var staged = (stagedVersion ?? "").Trim();
        if (bundled.Length == 0 || staged.Length == 0) return false;
        return !string.Equals(bundled, staged, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Appends the optional capability flags the user asked for AND the binary understands.
    /// </summary>
    internal static void AddOptional(
        List<string> args, UserSettings s, string exePath, ConnectionMethod method,
        Action<string>? note = null)
    {
        AppliedOptionalFlags.Clear();

        var surface = TlsSurfaceFor(s, method);

        if (s.AetherEch && surface != TlsSurface.None)
        {
            if (AetherCapabilities.Supports(exePath, "--ech"))
            {
                args.Add("--ech");
                args.Add("auto");
                AppliedOptionalFlags.Add("ech=auto");
                note?.Invoke("ECH on (auto)");

                var echDns = BuildEchDnsField(s);
                if (echDns.Length > 0 && AetherCapabilities.Supports(exePath, "--ech-dns"))
                {
                    args.Add("--ech-dns");
                    args.Add(echDns);
                    AppliedOptionalFlags.Add($"ech-dns={echDns}");
                    note?.Invoke($"ECH key looked up via {echDns}");
                }

                if (!string.IsNullOrWhiteSpace(s.AetherEchDomain)
                    && AetherCapabilities.Supports(exePath, "--ech-domain"))
                {
                    args.Add("--ech-domain");
                    args.Add(s.AetherEchDomain.Trim());
                    AppliedOptionalFlags.Add($"ech-domain={s.AetherEchDomain.Trim()}");
                    note?.Invoke($"ECH key domain {s.AetherEchDomain.Trim()}");
                }
            }
            else
            {
                note?.Invoke("ECH requested but this aether build has no --ech; skipped");
            }
        }
        else if (s.AetherEch)
        {
            note?.Invoke($"ECH not applicable: {UnavailableReason(surface, "--ech").ToLowerInvariant()}");
        }

        if (!string.IsNullOrWhiteSpace(s.AetherTlsCiphers) && surface == TlsSurface.Http2)
        {
            if (AetherCapabilities.Supports(exePath, "--tls-ciphers"))
            {
                args.Add("--tls-ciphers");
                args.Add(s.AetherTlsCiphers.Trim());
                AppliedOptionalFlags.Add("tls-ciphers");
                note?.Invoke("custom TLS 1.2 cipher list");
            }
        }

        if (!string.IsNullOrWhiteSpace(s.AetherTlsGroups) && surface == TlsSurface.Http2)
        {
            if (AetherCapabilities.Supports(exePath, "--tls-groups"))
            {
                args.Add("--tls-groups");
                args.Add(s.AetherTlsGroups.Trim());
                AppliedOptionalFlags.Add("tls-groups");
                note?.Invoke($"custom TLS group order {s.AetherTlsGroups.Trim()}");
            }
        }

        if (s.AetherDisableGrease && surface != TlsSurface.None
            && AetherCapabilities.Supports(exePath, "--disable-grease"))
        {
            args.Add("--disable-grease");
            AppliedOptionalFlags.Add("disable-grease");
            note?.Invoke("GREASE values omitted");
        }

        if (s.AetherTlsVerify)
        {
            if (AetherCapabilities.Supports(exePath, "--tls-verify"))
            {
                args.Add("--tls-verify");
                AppliedOptionalFlags.Add("tls-verify");
                note?.Invoke("TLS verification on");
            }
            else
            {
                note?.Invoke(
                    "TLS verification requested but this aether build has no --tls-verify; skipped");
            }
        }

        var wantsTor = s.AetherTor || s.AetherTorRelays;
        if (wantsTor)
        {
            if (AetherCapabilities.Supports(exePath, "--tor"))
            {
                args.Add("--tor");
                AppliedOptionalFlags.Add("tor");
                note?.Invoke("Tor inside the tunnel");
            }

            if (s.AetherTorRelays && AetherCapabilities.Supports(exePath, "--tor-relays"))
            {
                args.Add("--tor-relays");
                args.Add("web");
                AppliedOptionalFlags.Add("tor-relays=web");
                note?.Invoke("Tor bridges from running relays");
            }

            StageTorSupportFiles();
        }

        if (s.AetherExitLocSecs > 0
            && AetherCapabilities.Supports(exePath, "--exit-loc-secs")
            && !string.IsNullOrWhiteSpace(s.AetherExitLoc))
        {
            args.Add("--exit-loc-secs");
            args.Add(Math.Clamp(s.AetherExitLocSecs, 10, 3600).ToString());
            AppliedOptionalFlags.Add($"exit-loc-secs={s.AetherExitLocSecs}");
            note?.Invoke($"exit country rechecked every {s.AetherExitLocSecs}s");
        }
    }

    /// <summary>Copies pluggable transport and tunnel binaries for Tor support.</summary>
    internal static void StageTorSupportFiles()
    {
        var aetherDir = Path.Combine(AppDir, "Resources", "aether");
        if (!Directory.Exists(aetherDir)) return;

        var ptDir = Path.Combine(aetherDir, "pt");
        var targets = new (string Dest, string[] Sources)[]
        {
            (Path.Combine(ptDir, "lyrebird.exe"),
                new[] { Path.Combine(AppDir, "Resources", "tor", "pluggable_transports", "lyrebird.exe") }),
            (Path.Combine(aetherDir, "psiphon-tunnel-core.exe"),
                new[] { Path.Combine(AppDir, "Resources", "psiphon-tunnel-core.exe") }),
        };

        foreach (var (dest, sources) in targets)
        {
            if (File.Exists(dest)) continue;

            foreach (var src in sources)
            {
                if (!File.Exists(src)) continue;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.Copy(src, dest, overwrite: false);
                }
                catch
                {
                    // Best effort. A missing transport only means an obfs4 bridge cannot
                    // start, and it must not stop a tunnel that does not need one.
                }
            }
        }
    }

    /// <summary>App root, resolved once. Mirrors the rest of the engine layer.</summary>
    private static string AppDir { get; } =
        Path.GetDirectoryName(typeof(AetherExtras).Assembly.Location) is { Length: > 0 } d
            ? d
            : AppContext.BaseDirectory;
}