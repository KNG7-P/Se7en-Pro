using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace Se7enPro.Models;

public static class SplitRouteAction
{
    public const string Bypass = "bypass";
    public const string Tunnel = "tunnel";
    public const string Block = "block";
}

public static class SplitOutbound
{
    public const string Direct = "direct";
    public const string Proxy = "proxy";
    public const string Block = "block";

    public static bool IsValid(string? value) =>
        value is Direct or Proxy or Block;

    public static string Normalize(string? value)
    {
        var v = (value ?? "").Trim().ToLowerInvariant();
        return IsValid(v) ? v : Proxy;
    }
}

// The matcher kinds, in the order the UI offers them. "App" is what the application
// picker writes: a full image path, which is stable across renames. The rest are the
// manual matchers.
public static class SplitTargetKind
{
    public const string App = "app";
    public const string Process = "process";
    public const string ProcessPath = "processpath";
    public const string Ip = "ip";
    public const string Keyword = "keyword";
    public const string Regex = "regex";
    public const string Suffix = "suffix";
    public const string Domain = "domain";

    public static readonly string[] All =
    {
        ProcessPath,
        Process,
        Ip,
        Keyword,
        Regex,
        Suffix,
        Domain,
    };

    public static string Normalize(string? value)
    {
        var v = (value ?? "").Trim().ToLowerInvariant();
        return v switch
        {
            Process or ProcessPath or Ip or Keyword or Regex or Suffix or Domain => v,
            _ => App,
        };
    }

    public static bool IsProcessKind(string? kind) => Normalize(kind) is
        App or Process or ProcessPath or Keyword or Regex or Suffix;
}

public sealed class SplitTunnelRule
{
    public string Kind { get; set; } = SplitTargetKind.App;

    public string Value { get; set; } = "";

    public string DisplayName { get; set; } = "";
}

public sealed class SplitTunnelProfile
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public string DefaultOutbound { get; set; } = SplitOutbound.Proxy;

    // Ordered Bypass → Tunnel → Block. The UI presents them as Direct / Proxy / Block.
    public List<SplitTunnelRule> Bypass { get; set; } = new();

    public List<SplitTunnelRule> Tunnel { get; set; } = new();

    public List<SplitTunnelRule> Block { get; set; } = new();

    [JsonIgnore]
    public int RuleCount => Bypass.Count + Tunnel.Count + Block.Count;

    public IEnumerable<SplitTunnelRule> AllRules() =>
        Bypass.Concat(Tunnel).Concat(Block);
}
