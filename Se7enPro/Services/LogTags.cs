using System;

namespace Se7enPro.Services;

public static class LogTags
{
    public const string Psiphon = "psiphon";
    public const string Aether = "aether";
    public const string Tor = "tor";
    public const string Shard = "shard";
    public const string V2Ray = "v2ray";
    public const string Chain = "chain";
    public const string Tun = "tun";
    public const string App = "app";

    private static readonly string[] All = { Psiphon, Aether, Tor, Shard, V2Ray, Chain, Tun, App, "warp" };

        public static bool Has(string? line) => Of(line) is not null;

        public static string? Of(string? line)
    {
        if (line is null || line.Length < 4 || line[0] != '[') return null;
        var close = line.IndexOf(']');
        if (close <= 1) return null;
        var tag = line.Substring(1, close - 1).ToLowerInvariant();
        if (Array.IndexOf(All, tag) < 0) return null;
        
        return tag == "warp" ? Aether : tag;
    }

        public static string Tag(string tag, string line) => Has(line) ? line : $"[{tag}] {line}";

        public static string ForMethod(ConnectionMethod method) => method switch
    {
        ConnectionMethod.Psiphon => Psiphon,
        var m when m.IsAether() => Aether,
        ConnectionMethod.Tor => Tor,
        ConnectionMethod.Shard => Shard,
        ConnectionMethod.PsiphonOverV2Ray or ConnectionMethod.TorOverV2Ray => V2Ray,
        ConnectionMethod.PsiphonOverWarp or ConnectionMethod.TorOverWarp => Chain,
        _ => App,
    };
}
