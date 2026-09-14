using System;
using System.Collections.Generic;

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

    PsiphonOverV2Ray = 7,

    TorOverV2Ray = 8,

    Shard = 9,

    MasqueOnMasque = 10,
}

public sealed record ConnectionMethodOption(string Key, string Display, string Description);

public static class ConnectionMethodExtensions
{
    public static readonly IReadOnlyList<ConnectionMethodOption> AllOptions = new List<ConnectionMethodOption>
    {
        new("psiphon", "Psiphon",
            "CDN-fronting capable Psiphon core. Most resilient on heavily filtered networks."),
        new("masque", "MASQUE",
            "Cloudflare WARP over MASQUE (QUIC / HTTP-3). Fast and hard to fingerprint."),
        new("wireguard", "WireGuard",
            "Classic Cloudflare WARP over WireGuard."),
        new("warp_on_warp", "Warp on Warp",
            "WARP tunnelled inside WARP — an extra hop for tougher networks."),
        new("tor", "Tor",
            "The Tor network with optional bridges and pluggable transports."),
        new("psiphon_over_warp", "Psiphon over WARP",
            "Multi-hop: Psiphon tunnelled inside Cloudflare WARP/MASQUE for maximum DPI evasion."),
        new("tor_over_warp", "Tor over WARP",
            "Multi-hop: Tor network traffic routed through Cloudflare WARP."),
        new("psiphon_over_v2ray", "Psiphon over V2Ray",
            "Multi-hop: Psiphon tunnelled inside V2Ray / Xray / Sing-box proxy node."),
        new("tor_over_v2ray", "Tor over V2Ray",
            "Multi-hop: Tor network traffic routed through V2Ray / Xray / Sing-box proxy node."),
        new("shard", "SHARD",
            "Dedicated SHARD transport over Cloudflare with TLS fragmentation and cipher-suite pinning."),
        new("masque_on_masque", "Masque on Masque",
            "Double MASQUE hops inside Cloudflare European backbone. Clean foreign exit IP."),
    };

    public static string ToToken(this ConnectionMethod method) => method switch
    {
        ConnectionMethod.Psiphon => "psiphon",
        ConnectionMethod.Masque => "masque",
        ConnectionMethod.WireGuard => "wireguard",
        ConnectionMethod.WarpOnWarp => "warp_on_warp",
        ConnectionMethod.Tor => "tor",
        ConnectionMethod.PsiphonOverWarp => "psiphon_over_warp",
        ConnectionMethod.TorOverWarp => "tor_over_warp",
        ConnectionMethod.PsiphonOverV2Ray => "psiphon_over_v2ray",
        ConnectionMethod.TorOverV2Ray => "tor_over_v2ray",
        ConnectionMethod.Shard => "shard",
        ConnectionMethod.MasqueOnMasque => "masque_on_masque",
        _ => "psiphon",
    };

    public static string ToDisplayName(this ConnectionMethod method) => method switch
    {
        ConnectionMethod.Psiphon => "Psiphon",
        ConnectionMethod.Masque => "MASQUE",
        ConnectionMethod.WireGuard => "WireGuard",
        ConnectionMethod.WarpOnWarp => "Warp on Warp",
        ConnectionMethod.Tor => "Tor",
        ConnectionMethod.PsiphonOverWarp => "Psiphon over WARP",
        ConnectionMethod.TorOverWarp => "Tor over WARP",
        ConnectionMethod.PsiphonOverV2Ray => "Psiphon over V2Ray",
        ConnectionMethod.TorOverV2Ray => "Tor over V2Ray",
        ConnectionMethod.Shard => "SHARD",
        ConnectionMethod.MasqueOnMasque => "Masque on Masque",
        _ => "Psiphon",
    };

    public static ConnectionMethod ParseConnectionMethod(string? token) =>
        (token ?? "").Trim().ToLowerInvariant() switch
        {
            "psiphon" => ConnectionMethod.Psiphon,
            "masque" => ConnectionMethod.Masque,
            "wireguard" or "warp" or "wg" => ConnectionMethod.WireGuard,
            "warp_on_warp" or "warponwarp" or "gool" or "wiw" => ConnectionMethod.WarpOnWarp,
            "tor" => ConnectionMethod.Tor,
            "psiphon_over_warp" or "psiphonoverwarp" or "chain" or "pow" => ConnectionMethod.PsiphonOverWarp,
            "tor_over_warp" or "toroverwarp" or "tow" => ConnectionMethod.TorOverWarp,
            "psiphon_over_v2ray" or "psiphonoverv2ray" or "psiphon_v2ray" or "pov" => ConnectionMethod.PsiphonOverV2Ray,
            "tor_over_v2ray" or "toroverv2ray" or "tor_v2ray" or "tov" => ConnectionMethod.TorOverV2Ray,
            "shard" => ConnectionMethod.Shard,
            "masque_on_masque" or "masqueonmasque" or "masque_in_masque" or "mim" or "mom" => ConnectionMethod.MasqueOnMasque,
            _ => ConnectionMethod.Psiphon,
        };

    public static bool IsAether(this ConnectionMethod method) =>
        method is ConnectionMethod.Masque
                or ConnectionMethod.WireGuard
                or ConnectionMethod.WarpOnWarp
                or ConnectionMethod.MasqueOnMasque;

    public static bool IsChained(this ConnectionMethod method) =>
        method is ConnectionMethod.PsiphonOverWarp
               or ConnectionMethod.TorOverWarp
               or ConnectionMethod.PsiphonOverV2Ray
               or ConnectionMethod.TorOverV2Ray;

    public static bool IsShard(this ConnectionMethod method) =>
        method is ConnectionMethod.Shard;

    public static bool IsTor(this ConnectionMethod method) =>
        method is ConnectionMethod.Tor
               or ConnectionMethod.TorOverWarp
               or ConnectionMethod.TorOverV2Ray;
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
        TorPtLyrebird,
        TorPtConjure,
        "xray.exe",
        "sing-box.exe",
        "xray",
        "sing-box",
        Shard,
        "Se7enPro.Shard",
    };
}
