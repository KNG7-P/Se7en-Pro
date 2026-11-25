using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using Se7enPro.Models;

namespace Se7enPro.Services;

internal static class AetherExtras
{
    internal static List<string> AppliedOptionalFlags { get; } = new();

    internal static bool IsGoolH2(UserSettings s) =>
        string.Equals((s.AetherGoolMode ?? "").Trim(), "masque_h2",
                      StringComparison.OrdinalIgnoreCase);
    internal static bool IsClassicGool(UserSettings s) =>
        string.Equals((s.AetherGoolMode ?? "masque").Trim(), "classic",
                      StringComparison.OrdinalIgnoreCase);

    internal readonly record struct GoolLayout(
        string DeviceFile, string WireGuardFile, bool DeviceIsMasque);

    internal static GoolLayout GoolLayoutFor(UserSettings s) =>
        IsClassicGool(s)
            ? new GoolLayout("aether.toml", "aether-secondary.toml", DeviceIsMasque: false)
            : new GoolLayout("aether-masque.toml", "aether-masque-gool.toml", DeviceIsMasque: true);

    internal static string BuildDnsField(UserSettings s)
    {
        var plan = new DnsResolverPolicy().Build(s, hasV6Address: false);
        var field = DnsResolverPolicy.ToCommaSeparatedResolvers(plan);
        return field.Length > 0 ? field : "1.1.1.1,1.0.0.1";
    }

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

    internal static bool ShouldDeferProvisioning(UserSettings s)
    {
        if (!s.AetherEch) return false;
        return Volatile.Read(ref _echGaveUp) == 0;
    }

    internal static bool TryClaimEchFallback() =>
        Interlocked.CompareExchange(ref _echGaveUp, 1, 0) == 0;

    internal static void ResetEchFallback() => Interlocked.Exchange(ref _echGaveUp, 0);

    private static int _echGaveUp;

    internal enum TlsSurface
    {
        None,

        Http2,

        Http3,
    }

    internal static TlsSurface TlsSurfaceFor(UserSettings s, ConnectionMethod method, bool hasUpstream = false)
    {
        switch (method)
        {
            case ConnectionMethod.WireGuard:
                return TlsSurface.None;

            case ConnectionMethod.WarpOnWarp:
                if (IsClassicGool(s)) return TlsSurface.None;
                if (hasUpstream || IsGoolH2(s)) return TlsSurface.Http2;
                return TlsSurface.Http3;

            case ConnectionMethod.Masque:
            case ConnectionMethod.MasqueInMasque:
                if (hasUpstream) return TlsSurface.Http2;
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

    internal static string EffectiveTransport(UserSettings s, bool hasUpstream) =>
        hasUpstream ? "h2"
        : NormalizeAetherTransport(s) == "h3" ? "h3" : "h2";

    internal static List<string> BuildUpstreamArgs(string? url)
    {
        var value = (url ?? "").Trim();
        if (value.Length == 0) return new List<string>();

        foreach (var scheme in new[] { "socks5://", "socks5h://", "http://", "https://" })
        {
            if (!value.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) continue;
            var host = value.Substring(scheme.Length);
            if (host.Length == 0 || host.StartsWith('/')) return new List<string>();
            return new List<string> { "--upstream", value };
        }

        return new List<string>();
    }

    internal static string RedactUpstreamUrl(string? url)
    {
        var value = (url ?? "").Trim();
        var schemeEnd = value.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0) return value;

        var authorityStart = schemeEnd + 3;
        var authorityEnd = value.IndexOfAny(new[] { '/', '?', '#' }, authorityStart);
        if (authorityEnd < 0) authorityEnd = value.Length;

        var authority = value.Substring(authorityStart, authorityEnd - authorityStart);
        var at = authority.LastIndexOf('@');
        if (at < 0) return value;

        return value.Substring(0, authorityStart)
             + authority.Substring(at + 1)
             + value.Substring(authorityEnd);
    }

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

    internal static bool ShouldRefreshStagedCore(string? bundledVersion, string? stagedVersion)
    {
        var bundled = (bundledVersion ?? "").Trim();
        var staged = (stagedVersion ?? "").Trim();
        if (bundled.Length == 0 || staged.Length == 0) return false;
        return !string.Equals(bundled, staged, StringComparison.OrdinalIgnoreCase);
    }

    internal static void RefreshStagedCoreIfStale()
    {
        var source = Path.Combine(AppDir, "Resources", "aether", "aether.exe");
        var staged = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            BuildIdentity.AppFolderName, "aether", EngineProcessNames.Aether);

        if (!File.Exists(source) || !File.Exists(staged)) return;

        try
        {
            var srcFi = new FileInfo(source);
            var stgFi = new FileInfo(staged);
            if (srcFi.Length == stgFi.Length && srcFi.LastWriteTimeUtc == stgFi.LastWriteTimeUtc) return;
        }
        catch { }

        if (!ShouldRefreshStagedCore(
                AetherCapabilities.Version(source), AetherCapabilities.Version(staged))) return;

        try
        {
            File.Copy(source, staged, overwrite: true);
            try { File.SetLastWriteTimeUtc(staged, File.GetLastWriteTimeUtc(source)); } catch { }
            AetherCapabilities.Invalidate();
        }
        catch
        {
        }
    }

    internal static void AddOptional(
        List<string> args, UserSettings s, string exePath, ConnectionMethod method,
        Action<string>? note = null)
    {
        AppliedOptionalFlags.Clear();

        var hasUpstream = args.Contains("--upstream");
        var surface = TlsSurfaceFor(s, method, hasUpstream);

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

        if (!string.IsNullOrWhiteSpace(s.AetherMasqueSni) && surface != TlsSurface.None
            && AetherCapabilities.Supports(exePath, "--masque-sni"))
        {
            args.Add("--masque-sni");
            args.Add(s.AetherMasqueSni.Trim());
            AppliedOptionalFlags.Add($"masque-sni={s.AetherMasqueSni.Trim()}");
            note?.Invoke($"MASQUE SNI {s.AetherMasqueSni.Trim()}");
        }

        var fp = (s.AetherTlsFingerprint ?? "").Trim().ToLowerInvariant();
        var chromeCompat = fp is "chrome-compat" or "chrome";

        var tlsCiphers = !string.IsNullOrWhiteSpace(s.AetherTlsCiphers)
            ? s.AetherTlsCiphers.Trim()
            : chromeCompat
                ? "ECDHE-ECDSA-AES256-GCM-SHA384:ECDHE-RSA-AES256-GCM-SHA384:ECDHE-ECDSA-AES128-GCM-SHA256:ECDHE-RSA-AES128-GCM-SHA256:ECDHE-ECDSA-CHACHA20-POLY1305:ECDHE-RSA-CHACHA20-POLY1305:ECDHE-ECDSA-AES256-SHA:ECDHE-RSA-AES256-SHA:ECDHE-ECDSA-AES128-SHA256:ECDHE-RSA-AES128-SHA256"
                : "";

        var tls13Ciphers = !string.IsNullOrWhiteSpace(s.AetherTls13Ciphers)
            ? s.AetherTls13Ciphers.Trim()
            : chromeCompat
                ? "TLS_AES_256_GCM_SHA384:TLS_CHACHA20_POLY1305_SHA256:TLS_AES_128_GCM_SHA256"
                : "";

        var tlsGroups = !string.IsNullOrWhiteSpace(s.AetherTlsGroups)
            ? s.AetherTlsGroups.Trim()
            : chromeCompat
                ? "X25519MLKEM768:X25519:P-256:P-384:P-521"
                : "";

        var tlsSigalgs = !string.IsNullOrWhiteSpace(s.AetherTlsSigalgs)
            ? s.AetherTlsSigalgs.Trim()
            : chromeCompat
                ? "mldsa65:mldsa87:mldsa44:ecdsa_secp256r1_sha256:ecdsa_secp384r1_sha384:ecdsa_secp521r1_sha512:ed25519:rsa_pss_rsae_sha256:rsa_pss_rsae_sha384:rsa_pss_rsae_sha512:rsa_pkcs1_sha256:rsa_pkcs1_sha384:rsa_pkcs1_sha512"
                : "";

        var disableGrease = s.AetherDisableGrease || chromeCompat;
        var disableOcsp = s.AetherDisableOcspStapling || chromeCompat;
        var disableSct = s.AetherDisableSct || chromeCompat;
        var disablePerm = s.AetherDisableExtensionPermutation || chromeCompat;

        if (chromeCompat && surface != TlsSurface.None)
        {
            AppliedOptionalFlags.Add("tls-fp=chrome-compat");
            note?.Invoke("TLS fingerprint preset: chrome-compat");
        }

        if (tlsCiphers.Length > 0 && surface == TlsSurface.Http2)
        {
            if (AetherCapabilities.Supports(exePath, "--tls-ciphers"))
            {
                args.Add("--tls-ciphers");
                args.Add(tlsCiphers);
                AppliedOptionalFlags.Add("tls-ciphers");
                note?.Invoke("custom TLS 1.2 cipher list");
            }
        }

        if (tls13Ciphers.Length > 0 && surface != TlsSurface.None
            && AetherCapabilities.Supports(exePath, "--tls13-ciphers"))
        {
            args.Add("--tls13-ciphers");
            args.Add(tls13Ciphers);
            AppliedOptionalFlags.Add("tls13-ciphers");
            note?.Invoke("custom TLS 1.3 cipher list");
        }

        if (tlsGroups.Length > 0 && surface == TlsSurface.Http2)
        {
            if (AetherCapabilities.Supports(exePath, "--tls-groups"))
            {
                args.Add("--tls-groups");
                args.Add(tlsGroups);
                AppliedOptionalFlags.Add("tls-groups");
                note?.Invoke($"custom TLS group order {tlsGroups}");
            }
        }

        if (!string.IsNullOrWhiteSpace(s.AetherTlsKeyShares) && surface != TlsSurface.None
            && AetherCapabilities.Supports(exePath, "--tls-key-shares"))
        {
            args.Add("--tls-key-shares");
            args.Add(s.AetherTlsKeyShares.Trim());
            AppliedOptionalFlags.Add("tls-key-shares");
            note?.Invoke($"custom TLS key shares {s.AetherTlsKeyShares.Trim()}");
        }

        if (disableGrease && surface != TlsSurface.None
            && AetherCapabilities.Supports(exePath, "--disable-grease"))
        {
            args.Add("--disable-grease");
            AppliedOptionalFlags.Add("disable-grease");
            note?.Invoke("GREASE values omitted");
        }

        if (tlsSigalgs.Length > 0 && surface != TlsSurface.None
            && AetherCapabilities.Supports(exePath, "--tls-sigalgs"))
        {
            args.Add("--tls-sigalgs");
            args.Add(tlsSigalgs);
            AppliedOptionalFlags.Add("tls-sigalgs");
            note?.Invoke("custom signature algorithm order");
        }

        var alps = (s.AetherTlsAlps ?? "").Trim().ToLowerInvariant();
        if ((alps == "new" || alps == "old") && surface != TlsSurface.None
            && AetherCapabilities.Supports(exePath, "--tls-alps"))
        {
            args.Add("--tls-alps");
            args.Add(alps);
            AppliedOptionalFlags.Add($"tls-alps={alps}");
            note?.Invoke($"ALPS offered ({alps} codepoint)");
        }

        if (disableOcsp && surface != TlsSurface.None
            && AetherCapabilities.Supports(exePath, "--disable-ocsp-stapling"))
        {
            args.Add("--disable-ocsp-stapling");
            AppliedOptionalFlags.Add("disable-ocsp-stapling");
        }

        if (disableSct && surface != TlsSurface.None
            && AetherCapabilities.Supports(exePath, "--disable-sct"))
        {
            args.Add("--disable-sct");
            AppliedOptionalFlags.Add("disable-sct");
        }

        if (s.AetherDisableSessionTicket && surface != TlsSurface.None
            && AetherCapabilities.Supports(exePath, "--disable-session-ticket"))
        {
            args.Add("--disable-session-ticket");
            AppliedOptionalFlags.Add("disable-session-ticket");
        }

        if (disablePerm && surface != TlsSurface.None
            && AetherCapabilities.Supports(exePath, "--disable-extension-permutation"))
        {
            args.Add("--disable-extension-permutation");
            AppliedOptionalFlags.Add("disable-extension-permutation");
        }

        if (s.AetherEnableEchGrease && surface != TlsSurface.None
            && AetherCapabilities.Supports(exePath, "--enable-ech-grease"))
        {
            args.Add("--enable-ech-grease");
            AppliedOptionalFlags.Add("enable-ech-grease");
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

        var effectiveExitLoc = method switch
        {
            ConnectionMethod.WireGuard => !string.IsNullOrWhiteSpace(s.AetherExitLocWireguard) ? s.AetherExitLocWireguard : s.AetherExitLoc,
            ConnectionMethod.WarpOnWarp => !string.IsNullOrWhiteSpace(s.AetherExitLocWarp) ? s.AetherExitLocWarp : s.AetherExitLoc,
            ConnectionMethod.MasqueInMasque => !string.IsNullOrWhiteSpace(s.AetherExitLocMim) ? s.AetherExitLocMim : s.AetherExitLoc,
            _ => !string.IsNullOrWhiteSpace(s.AetherExitLocMasque) ? s.AetherExitLocMasque : s.AetherExitLoc,
        };
        if (s.AetherExitLocSecs > 0
            && AetherCapabilities.Supports(exePath, "--exit-loc-secs")
            && !string.IsNullOrWhiteSpace(effectiveExitLoc))
        {
            args.Add("--exit-loc-secs");
            args.Add(Math.Clamp(s.AetherExitLocSecs, 10, 3600).ToString());
            AppliedOptionalFlags.Add($"exit-loc-secs={s.AetherExitLocSecs}");
            note?.Invoke($"exit country rechecked every {s.AetherExitLocSecs}s");
        }
    }

    internal static void StageTorSupportFiles()
    {
        var roots = new[]
        {
            Path.Combine(AppDir, "Resources", "aether"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), BuildIdentity.AppFolderName, "aether"),
        };

        foreach (var aetherDir in roots)
        {
            try { Directory.CreateDirectory(aetherDir); } catch { continue; }
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
                    }
                }
            }
        }
    }

    internal const int DefaultValidateSecs = 12;

    internal static string LastConnBucketKey(ConnectionMethod method, string? transport, bool isClassicGool, bool isChained = false)
    {
        var t = (transport ?? "").Trim().ToLowerInvariant() switch
        {
            "h3" or "http3" or "http/3" or "quic" => "h3",
            _ => "h2",
        };
        var baseKey = method switch
        {
            ConnectionMethod.WireGuard => "wg",
            ConnectionMethod.MasqueInMasque => $"mim-{t}",
            ConnectionMethod.WarpOnWarp => isClassicGool ? "gool-classic" : $"gool-{t}",
            _ => $"masque-{t}",
        };
        return isChained ? $"chain-{baseKey}" : baseKey;
    }

    internal static void RestoreLastConn(string workDir, string bucketKey, bool cacheEnabled)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(workDir) || !Directory.Exists(workDir)) return;

            var cacheRoot = Path.Combine(workDir, "lastconn-cache");
            var bucketDir = Path.Combine(cacheRoot, bucketKey);

            if (!cacheEnabled)
            {
                DeleteLiveLastConnFiles(workDir);
                return;
            }

            if (Directory.Exists(bucketDir))
            {
                DeleteLiveLastConnFiles(workDir);
                foreach (var src in Directory.GetFiles(bucketDir, "*-lastconn.toml", SearchOption.TopDirectoryOnly))
                {
                    var dst = Path.Combine(workDir, Path.GetFileName(src));
                    File.Copy(src, dst, overwrite: true);
                }
            }
            else if (Directory.Exists(cacheRoot))
            {
                DeleteLiveLastConnFiles(workDir);
            }
        }
        catch { }
    }

    internal static void SnapshotLastConn(string? workDir, string? bucketKey)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(workDir) || string.IsNullOrWhiteSpace(bucketKey)) return;
            if (!Directory.Exists(workDir)) return;

            var live = Directory.GetFiles(workDir, "*-lastconn.toml", SearchOption.TopDirectoryOnly);
            if (live.Length == 0) return;

            var bucketDir = Path.Combine(workDir, "lastconn-cache", bucketKey);
            Directory.CreateDirectory(bucketDir);
            foreach (var src in live)
            {
                var dst = Path.Combine(bucketDir, Path.GetFileName(src));
                File.Copy(src, dst, overwrite: true);
            }
        }
        catch { }
    }

    private static void DeleteLiveLastConnFiles(string workDir)
    {
        foreach (var f in Directory.GetFiles(workDir, "*-lastconn.toml", SearchOption.TopDirectoryOnly))
        {
            try { File.Delete(f); } catch { }
        }
    }

    private static string AppDir { get; } =
        Path.GetDirectoryName(typeof(AetherExtras).Assembly.Location) is { Length: > 0 } d
            ? d
            : AppContext.BaseDirectory;
}
