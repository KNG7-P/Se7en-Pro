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
            _ => ConnectionMethod.Psiphon,
        };

    public static bool IsAether(this ConnectionMethod method) =>
        method is ConnectionMethod.Masque
               or ConnectionMethod.WireGuard
               or ConnectionMethod.WarpOnWarp;

    public static bool IsChained(this ConnectionMethod method) =>
        method is ConnectionMethod.PsiphonOverWarp
               or ConnectionMethod.TorOverWarp;
}

public static class EngineProcessNames
{

    public const string Psiphon = "Se7enPro.Tunnel.exe";

    public const string Aether = "Se7enPro.Aether.exe";

    public const string Tor = "Se7enPro.Tor.exe";

    public const string TorPtLyrebird = "lyrebird.exe";
    public const string TorPtConjure = "conjure-client.exe";

    public static readonly IReadOnlyList<string> All = new[]
    {
        Psiphon,
        Aether,
        Tor,
        TorPtLyrebird,
        TorPtConjure,
    };
}
