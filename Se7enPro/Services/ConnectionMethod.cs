using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;

namespace Se7enPro.Services;

public enum ConnectionMethod
{
        Psiphon = 0,

        Masque = 1,

        WireGuard = 2,

        WarpOnWarp = 3,

        Tor = 4,

        PsiphonOverWarp = 5,

        TorOverWarp = 6,

        Shard = 7,

        PsiphonOverV2Ray = 8,

        TorOverV2Ray = 9,

        MasqueInMasque = 10,

        WarpOverV2Ray = 11,

        Foxy = 12,

        FoxyOverWarp = 13,

        FoxyOverV2Ray = 14,

        PsiphonOverFoxy = 15,

        Lantern = 16,

        LanternOverWarp = 17,

        Veep = 18,

        VeepOverWarp = 19,

        PsiphonOverLantern = 20,
}

public sealed record ConnectionMethodOption(
    string Key,
    string English,
    string EnglishDescription,
    string GroupKey,
    string IconKind = "Shield",
    string ColorHex = "#06B6D4") : LocalizedItem
{
    public string Display => Loc.Of(English);

    public string Description => Loc.Of(EnglishDescription);

    
    
    
    public Brush AccentBrush => OptionBrush.For(ColorHex);

    private static class OptionBrush
    {
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Brush> Cache = new();

        public static Brush For(string hex) => Cache.GetOrAdd(hex, h =>
        {
            try
            {
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(h));
                brush.Freeze();
                return brush;
            }
            catch
            {
                return Brushes.Cyan;
            }
        });
    }
}

public static class ConnectionMethodExtensions
{
        public static List<ConnectionMethodOption> CreateOptions() => new(AllOptions);

        private static readonly IReadOnlyList<ConnectionMethodOption> AllOptions = new List<ConnectionMethodOption>
    {
        
        new("wireguard", "WireGuard",
            "Classic Cloudflare WARP over WireGuard.",
            GroupKey: "WARP",
            IconKind: "Flash",
            ColorHex: "#8B5CF6"),

        new("masque", "MASQUE",
            "Cloudflare WARP over MASQUE (QUIC / HTTP-3). Fast and hard to fingerprint.",
            GroupKey: "WARP",
            IconKind: "Flash",
            ColorHex: "#8B5CF6"),

        new("warp_on_warp", "Gool",
            "A WireGuard WARP identity carried inside MASQUE, registered through that tunnel, so Cloudflare sees a foreign exit. Classic WARP-in-WARP is a setting.",
            GroupKey: "WARP",
            IconKind: "Flash",
            ColorHex: "#8B5CF6"),

        new("warp_over_v2ray", "WARP over V2Ray",
            "Cloudflare WARP (MASQUE over HTTP/2) dialed through your selected V2Ray node, for networks that block the WARP edges outright.",
            GroupKey: "WARP",
            IconKind: "SwapHorizontal",
            ColorHex: "#8B5CF6"),

        new("masque_in_masque", "MASQUE on MASQUE",
            "Double-hop MASQUE: MASQUE tunnelled inside MASQUE for alternate egress routes.",
            GroupKey: "WARP",
            IconKind: "Flash",
            ColorHex: "#8B5CF6"),

        
        new("psiphon", "Psiphon",
            "CDN-fronting capable Psiphon core. Most resilient on heavily filtered networks.",
            GroupKey: "PSIPHON",
            IconKind: "ShieldLock",
            ColorHex: "#06B6D4"),

        new("psiphon_over_warp", "Psiphon over WARP",
            "Multi-hop: Psiphon tunnelled inside Cloudflare WARP/MASQUE for maximum DPI evasion.",
            GroupKey: "PSIPHON",
            IconKind: "ShieldLock",
            ColorHex: "#06B6D4"),

        new("psiphon_over_v2ray", "Psiphon over V2Ray",
            "Multi-hop: Psiphon tunnelled inside V2Ray / Xray / Sing-box proxy node.",
            GroupKey: "PSIPHON",
            IconKind: "ShieldLock",
            ColorHex: "#06B6D4"),

        new("psiphon_over_foxy", "Psiphon over Foxy",
            "Multi-hop: Psiphon tunnelled inside Firefox VPN (Foxy).",
            GroupKey: "PSIPHON",
            IconKind: "ShieldLock",
            ColorHex: "#06B6D4"),

        new("psiphon_over_lantern", "Psiphon over Lantern",
            "Multi-hop: Psiphon tunnelled inside the Lantern server pool.",
            GroupKey: "PSIPHON",
            IconKind: "ShieldLock",
            ColorHex: "#06B6D4"),

        
        new("tor", "Tor",
            "The Tor network with optional bridges and pluggable transports.",
            GroupKey: "TOR",
            IconKind: "ShieldMoon",
            ColorHex: "#C084FC"),

        new("tor_over_warp", "Tor over WARP",
            "Multi-hop: Tor network traffic routed through Cloudflare WARP.",
            GroupKey: "TOR",
            IconKind: "ShieldMoon",
            ColorHex: "#C084FC"),

        new("tor_over_v2ray", "Tor over V2Ray",
            "Multi-hop: Tor network traffic routed through V2Ray / Xray / Sing-box proxy node.",
            GroupKey: "TOR",
            IconKind: "ShieldMoon",
            ColorHex: "#C084FC"),

        
        new("shard", "SHARD",
            "Dedicated SHARD transport over Cloudflare with TLS fragmentation and cipher-suite pinning.",
            GroupKey: "SHARD",
            IconKind: "HubOutline",
            ColorHex: "#38BDF8"),

        
        new("foxy", "Foxy",
            "Firefox VPN: your Mozilla account's 50 GB allowance, tunnelled straight to a Fastly edge.",
            GroupKey: "FOXY",
            IconKind: "Firefox",
            ColorHex: "#FF7139"),

        new("foxy_over_warp", "Foxy over WARP",
            "Multi-hop: Firefox VPN tunnelled inside Cloudflare WARP.",
            GroupKey: "FOXY",
            IconKind: "Firefox",
            ColorHex: "#FF7139"),

        new("foxy_over_v2ray", "Foxy over V2Ray",
            "Multi-hop: Firefox VPN tunnelled inside a V2Ray / Xray / Sing-box node.",
            GroupKey: "FOXY",
            IconKind: "Firefox",
            ColorHex: "#FF7139"),

        
        new("lantern", "Lantern",
            "Lantern's free server pool: Reality, Shadowsocks and Samizdat routes picked and "
          + "load-balanced by the Lantern daemon. Free and unlimited, tuned for Iran.",
            GroupKey: "LANTERN",
            IconKind: "RadioTower",
            ColorHex: "#E11D48"),

        new("lantern_over_warp", "Lantern over WARP",
            "Multi-hop: every Lantern outbound dialed through Cloudflare WARP, for networks "
          + "that block Lantern's servers outright.",
            GroupKey: "LANTERN",
            IconKind: "RadioTower",
            ColorHex: "#E11D48"),

        
        new("veep", "Veep",
            "Veep free proxy pool: TLS-encrypted HTTP CONNECT proxies across 7 regions, auto-rotated.",
            GroupKey: "VEEP",
            IconKind: "ShieldCheck",
            ColorHex: "#3B82F6"),

        new("veep_over_warp", "Veep over WARP",
            "Multi-hop: Veep proxy dialed through Cloudflare WARP for censorship circumvention.",
            GroupKey: "VEEP",
            IconKind: "ShieldCheck",
            ColorHex: "#3B82F6"),
    };

        public static string ToToken(this ConnectionMethod method) => method switch
    {
        ConnectionMethod.Psiphon => "psiphon",
        ConnectionMethod.Masque => "masque",
        ConnectionMethod.MasqueInMasque => "masque_in_masque",
        ConnectionMethod.WarpOverV2Ray => "warp_over_v2ray",
        ConnectionMethod.WireGuard => "wireguard",
        ConnectionMethod.WarpOnWarp => "warp_on_warp",
        ConnectionMethod.Tor => "tor",
        ConnectionMethod.Shard => "shard",
        ConnectionMethod.PsiphonOverWarp => "psiphon_over_warp",
        ConnectionMethod.TorOverWarp => "tor_over_warp",
        ConnectionMethod.PsiphonOverV2Ray => "psiphon_over_v2ray",
        ConnectionMethod.TorOverV2Ray => "tor_over_v2ray",
        ConnectionMethod.Foxy => "foxy",
        ConnectionMethod.FoxyOverWarp => "foxy_over_warp",
        ConnectionMethod.FoxyOverV2Ray => "foxy_over_v2ray",
        ConnectionMethod.PsiphonOverFoxy => "psiphon_over_foxy",
        ConnectionMethod.PsiphonOverLantern => "psiphon_over_lantern",
        ConnectionMethod.Lantern => "lantern",
        ConnectionMethod.LanternOverWarp => "lantern_over_warp",
        ConnectionMethod.Veep => "veep",
        ConnectionMethod.VeepOverWarp => "veep_over_warp",
        _ => "psiphon",
    };

        public static string ToDisplayName(this ConnectionMethod method)
    {
        var token = method.ToToken();
        foreach (var option in AllOptions)
        {
            if (string.Equals(option.Key, token, StringComparison.Ordinal)) return option.Display;
        }
        return token;
    }

    public static ConnectionMethod ParseConnectionMethod(string? token) =>
        (token ?? "").Trim().ToLowerInvariant() switch
        {
            "psiphon" => ConnectionMethod.Psiphon,
            "masque" => ConnectionMethod.Masque,
            "masque_in_masque" or "masqueinmasque" or "mim" or "double_masque" => ConnectionMethod.MasqueInMasque,
            "warp_over_v2ray" or "warp_v2ray" or "aether_over_v2ray" or "wov" => ConnectionMethod.WarpOverV2Ray,
            "wireguard" or "warp" or "wg" => ConnectionMethod.WireGuard,
            "warp_on_warp" or "warponwarp" or "gool" or "wiw" => ConnectionMethod.WarpOnWarp,
            "tor" => ConnectionMethod.Tor,
            "shard" => ConnectionMethod.Shard,
            "psiphon_over_warp" or "psiphonoverwarp" or "chain" or "pow" => ConnectionMethod.PsiphonOverWarp,
            "tor_over_warp" or "toroverwarp" or "tow" => ConnectionMethod.TorOverWarp,
            "psiphon_over_v2ray" or "psiphonoverv2ray" or "psiphon_v2ray" or "pov" => ConnectionMethod.PsiphonOverV2Ray,
            "tor_over_v2ray" or "toroverv2ray" or "tor_v2ray" or "tov" => ConnectionMethod.TorOverV2Ray,
            "foxy" or "firefox" or "firefox_vpn" or "foxy_vpn" => ConnectionMethod.Foxy,
            "foxy_over_warp" or "foxyoverwarp" or "firefox_over_warp" or "fow" => ConnectionMethod.FoxyOverWarp,
            "foxy_over_v2ray" or "foxyoverv2ray" or "firefox_over_v2ray" or "fov" => ConnectionMethod.FoxyOverV2Ray,
            "psiphon_over_foxy" or "psiphonoverfoxy" or "psiphon_foxy" or "pof" => ConnectionMethod.PsiphonOverFoxy,
            "psiphon_over_lantern" or "psiphonoverlantern" or "psiphon_lantern" or "pol" => ConnectionMethod.PsiphonOverLantern,
            "lantern" => ConnectionMethod.Lantern,
            "lantern_over_warp" or "lanternoverwarp" or "lantern_warp" or "low" => ConnectionMethod.LanternOverWarp,
            "veep" or "vpeen" => ConnectionMethod.Veep,
            "veep_over_warp" or "veepoverwarp" or "veep_warp" or "vow" => ConnectionMethod.VeepOverWarp,
            "psiphon_over_tor" or "psiphonovertor" or "psiphon_tor" or "pot"
                or "psiphon_over_shard" or "psiphonoovershard" or "psiphon_shard" or "pos" => ConnectionMethod.Psiphon,

            
            
            
            
            
            "auto" or "automatic" or "best" or "smart" => ConnectionMethod.WireGuard,

            _ => ConnectionMethod.WireGuard,
        };

        public static bool IsAether(this ConnectionMethod method) =>
        method is ConnectionMethod.Masque
               or ConnectionMethod.MasqueInMasque
               or ConnectionMethod.WireGuard
               or ConnectionMethod.WarpOnWarp;

        public static bool UsesWarp(this ConnectionMethod method) =>
        method.IsAether()
        || method is ConnectionMethod.PsiphonOverWarp
                   or ConnectionMethod.TorOverWarp
                   or ConnectionMethod.FoxyOverWarp
                   or ConnectionMethod.LanternOverWarp
                   or ConnectionMethod.VeepOverWarp
                   or ConnectionMethod.WarpOverV2Ray;

        public static bool IsShard(this ConnectionMethod method) =>
        method == ConnectionMethod.Shard;

        public static bool IsChained(this ConnectionMethod method) =>
        method is ConnectionMethod.PsiphonOverWarp
               or ConnectionMethod.TorOverWarp
               or ConnectionMethod.PsiphonOverV2Ray
               or ConnectionMethod.TorOverV2Ray
               or ConnectionMethod.WarpOverV2Ray
               or ConnectionMethod.FoxyOverWarp
               or ConnectionMethod.FoxyOverV2Ray
               or ConnectionMethod.PsiphonOverFoxy
               or ConnectionMethod.PsiphonOverLantern
               or ConnectionMethod.LanternOverWarp
               or ConnectionMethod.VeepOverWarp;

        public static bool IsLantern(this ConnectionMethod method) =>
        method is ConnectionMethod.Lantern
               or ConnectionMethod.LanternOverWarp;

        public static bool IsVeep(this ConnectionMethod method) =>
        method is ConnectionMethod.Veep
               or ConnectionMethod.VeepOverWarp;

        public static bool IsFoxy(this ConnectionMethod method) =>
        method is ConnectionMethod.Foxy
               or ConnectionMethod.FoxyOverWarp
               or ConnectionMethod.FoxyOverV2Ray
               or ConnectionMethod.PsiphonOverFoxy;

        public static bool IsV2Ray(this ConnectionMethod method) =>
        method is ConnectionMethod.PsiphonOverV2Ray
               or ConnectionMethod.TorOverV2Ray
               or ConnectionMethod.WarpOverV2Ray
               or ConnectionMethod.FoxyOverV2Ray;

        public static IReadOnlyList<ConnectionMethodGroup> Groups { get; } = BuildGroups();

    private static IReadOnlyList<ConnectionMethodGroup> BuildGroups()
    {
        var seen = new List<ConnectionMethodGroup>();
        foreach (var opt in AllOptions)
        {
            if (seen.Count > 0 && string.Equals(seen[^1].Key, opt.GroupKey, StringComparison.Ordinal))
                continue;
            seen.Add(new ConnectionMethodGroup(opt.GroupKey, opt.IconKind, opt.ColorHex, ShowDivider: seen.Count > 0));
        }
        return seen;
    }

    public static ConnectionMethodGroup GroupOf(string? key) =>
        Groups.FirstOrDefault(g => string.Equals(g.Key, key, StringComparison.Ordinal))
        ?? new ConnectionMethodGroup(key ?? "", "Shield", "#06B6D4", ShowDivider: false);
}

public sealed record ConnectionMethodGroup(string Key, string IconKind, string ColorHex, bool ShowDivider)
    : LocalizedItem
{
    public string Title => Loc.Of(Key);
}

public static class EngineProcessNames
{
        public const string Psiphon = "Se7enPro.Tunnel.exe";

        public const string Aether = "Se7enPro.Aether.exe";

        public const string Tor = "Se7enPro.Tor.exe";

        public const string Shard = "Se7enPro.Shard.exe";

        public const string Lantern = "Se7enPro.Lantern.exe";

        public const string Veep = "Se7enPro.Veep.exe";

    
    
    
    
    public const string TorPtLyrebird = "lyrebird.exe";
    public const string TorPtConjure = "conjure-client.exe";

    public static readonly IReadOnlyList<string> All = new[]
    {
        Psiphon,
        Aether,
        Tor,
        Shard,
        Lantern,
        Veep,
        "psiphon-tunnel-core.exe",
        "aether.exe",
        "tor.exe",
        "shard.exe",
        "xray.exe",
        "sing-box.exe",
        "sing-box",
        "Se7enPro.Shard",
        "lanternd.exe",
        "lanternd",
        "veep-core.exe",
        "veep-core",
        TorPtLyrebird,
        TorPtConjure,
    };

    internal static IReadOnlyList<string> ResolveEngineExecutablePaths()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in All)
        {
            names.Add(name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe");
        }
        names.Add("psiphon-tunnel-core.exe");
        names.Add("veep-core.exe");
        names.Add("tun2socks.exe");
        names.Add("Se7enPro.Tun.exe");
        names.Add("zeptun.exe");
        names.Add("Se7enPro.Zeptun.exe");
        names.Add("mihomo.exe");
        names.Add("Se7enPro.Mihomo.exe");

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(Environment.ProcessPath))
        {
            paths.Add(Environment.ProcessPath);
        }
        foreach (var root in new[]
                 {
                     System.IO.Path.Combine(
                         Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         BuildIdentity.AppFolderName),
                     System.IO.Path.Combine(AppContext.BaseDirectory, "Resources"),
                 })
        {
            try
            {
                if (!System.IO.Directory.Exists(root)) continue;
                foreach (var file in System.IO.Directory.EnumerateFiles(root, "*.exe", System.IO.SearchOption.AllDirectories))
                {
                    var leaf = System.IO.Path.GetFileName(file);
                    if (leaf.Length > 0 && names.Contains(leaf)) paths.Add(file);
                }
            }
            catch
            {
            }
        }

        return paths.ToList();
    }
}
