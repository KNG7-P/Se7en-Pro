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
            GroupKey: "AETHER (CLOUDFLARE WARP)",
            IconKind: "Flash",
            ColorHex: "#8B5CF6"),

        new("masque", "MASQUE",
            "Cloudflare WARP over MASQUE (QUIC / HTTP-3). Fast and hard to fingerprint.",
            GroupKey: "AETHER (CLOUDFLARE WARP)",
            IconKind: "Flash",
            ColorHex: "#8B5CF6"),

        new("warp_on_warp", "Warp on Warp",
            "WARP tunnelled inside WARP — an extra hop for tougher networks.",
            GroupKey: "AETHER (CLOUDFLARE WARP)",
            IconKind: "Flash",
            ColorHex: "#8B5CF6"),

        new("masque_in_masque", "MASQUE on MASQUE",
            "Double-hop MASQUE: MASQUE tunnelled inside MASQUE for alternate egress routes.",
            GroupKey: "AETHER (CLOUDFLARE WARP)",
            IconKind: "Flash",
            ColorHex: "#8B5CF6"),

        
        new("psiphon", "Psiphon",
            "CDN-fronting capable Psiphon core. Most resilient on heavily filtered networks.",
            GroupKey: "PSIPHON PROTOCOL",
            IconKind: "ShieldLock",
            ColorHex: "#06B6D4"),

        new("psiphon_over_warp", "Psiphon over WARP",
            "Multi-hop: Psiphon tunnelled inside Cloudflare WARP/MASQUE for maximum DPI evasion.",
            GroupKey: "PSIPHON PROTOCOL",
            IconKind: "ShieldLock",
            ColorHex: "#06B6D4"),

        new("psiphon_over_v2ray", "Psiphon over V2Ray",
            "Multi-hop: Psiphon tunnelled inside V2Ray / Xray / Sing-box proxy node.",
            GroupKey: "PSIPHON PROTOCOL",
            IconKind: "ShieldLock",
            ColorHex: "#06B6D4"),

        
        new("tor", "Tor",
            "The Tor network with optional bridges and pluggable transports.",
            GroupKey: "TOR ONION NETWORK",
            IconKind: "ShieldMoon",
            ColorHex: "#C084FC"),

        new("tor_over_warp", "Tor over WARP",
            "Multi-hop: Tor network traffic routed through Cloudflare WARP.",
            GroupKey: "TOR ONION NETWORK",
            IconKind: "ShieldMoon",
            ColorHex: "#C084FC"),

        new("tor_over_v2ray", "Tor over V2Ray",
            "Multi-hop: Tor network traffic routed through V2Ray / Xray / Sing-box proxy node.",
            GroupKey: "TOR ONION NETWORK",
            IconKind: "ShieldMoon",
            ColorHex: "#C084FC"),

        
        new("shard", "SHARD",
            "Dedicated SHARD transport over Cloudflare with TLS fragmentation and cipher-suite pinning.",
            GroupKey: "SHARD (CF FRAGMENTATION)",
            IconKind: "HubOutline",
            ColorHex: "#38BDF8"),
    };

        public static string ToToken(this ConnectionMethod method) => method switch
    {
        ConnectionMethod.Psiphon => "psiphon",
        ConnectionMethod.Masque => "masque",
        ConnectionMethod.MasqueInMasque => "masque_in_masque",
        ConnectionMethod.WireGuard => "wireguard",
        ConnectionMethod.WarpOnWarp => "warp_on_warp",
        ConnectionMethod.Tor => "tor",
        ConnectionMethod.Shard => "shard",
        ConnectionMethod.PsiphonOverWarp => "psiphon_over_warp",
        ConnectionMethod.TorOverWarp => "tor_over_warp",
        ConnectionMethod.PsiphonOverV2Ray => "psiphon_over_v2ray",
        ConnectionMethod.TorOverV2Ray => "tor_over_v2ray",
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
            "wireguard" or "warp" or "wg" => ConnectionMethod.WireGuard,
            "warp_on_warp" or "warponwarp" or "gool" or "wiw" => ConnectionMethod.WarpOnWarp,
            "tor" => ConnectionMethod.Tor,
            "shard" => ConnectionMethod.Shard,
            "psiphon_over_warp" or "psiphonoverwarp" or "chain" or "pow" => ConnectionMethod.PsiphonOverWarp,
            "tor_over_warp" or "toroverwarp" or "tow" => ConnectionMethod.TorOverWarp,
            "psiphon_over_v2ray" or "psiphonoverv2ray" or "psiphon_v2ray" or "pov" => ConnectionMethod.PsiphonOverV2Ray,
            "tor_over_v2ray" or "toroverv2ray" or "tor_v2ray" or "tov" => ConnectionMethod.TorOverV2Ray,

            
            
            
            
            
            "auto" or "automatic" or "best" or "smart" => ConnectionMethod.WireGuard,

            _ => ConnectionMethod.WireGuard,
        };

        public static bool IsAether(this ConnectionMethod method) =>
        method is ConnectionMethod.Masque
               or ConnectionMethod.MasqueInMasque
               or ConnectionMethod.WireGuard
               or ConnectionMethod.WarpOnWarp;

        public static bool IsShard(this ConnectionMethod method) =>
        method == ConnectionMethod.Shard;

        public static bool IsChained(this ConnectionMethod method) =>
        method is ConnectionMethod.PsiphonOverWarp
               or ConnectionMethod.TorOverWarp
               or ConnectionMethod.PsiphonOverV2Ray
               or ConnectionMethod.TorOverV2Ray;

        public static bool IsV2Ray(this ConnectionMethod method) =>
        method is ConnectionMethod.PsiphonOverV2Ray
               or ConnectionMethod.TorOverV2Ray;

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

    
    
    
    
    public const string TorPtLyrebird = "lyrebird.exe";
    public const string TorPtConjure = "conjure-client.exe";

    public static readonly IReadOnlyList<string> All = new[]
    {
        Psiphon,
        Aether,
        Tor,
        Shard,
        "xray.exe",
        "sing-box.exe",
        "sing-box",
        "Se7enPro.Shard",
        TorPtLyrebird,
        TorPtConjure,
    };
}
