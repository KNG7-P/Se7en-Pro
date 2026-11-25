using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Se7enPro.Models;

namespace Se7enPro.Services;

public enum SplitRuleCategory
{
    Direct,
    Proxy,
    Block,
}

public enum SplitRuleTarget
{
    ProcessPath,
    ProcessName,
    Keyword,
    Regex,
    Suffix,
    DomainSuffix,
    IpCidr,
}

public readonly record struct SplitRouteRule(
    SplitRuleCategory Category,
    SplitRuleTarget Target,
    string Value)
{
    public bool IsProcessRule => Target is
        SplitRuleTarget.ProcessPath
        or SplitRuleTarget.ProcessName
        or SplitRuleTarget.Keyword
        or SplitRuleTarget.Regex
        or SplitRuleTarget.Suffix;

    public bool IsHostRule => Target is SplitRuleTarget.DomainSuffix or SplitRuleTarget.IpCidr;
}

public sealed class SplitRoutingPlan
{
    public static readonly SplitRoutingPlan Disabled = new(
        enabled: false,
        profileId: "",
        profileName: "",
        defaultCategory: SplitRuleCategory.Proxy,
        rules: Array.Empty<SplitRouteRule>());

    public SplitRoutingPlan(
        bool enabled,
        string profileId,
        string profileName,
        SplitRuleCategory defaultCategory,
        IReadOnlyList<SplitRouteRule> rules)
    {
        Enabled = enabled;
        ProfileId = profileId;
        ProfileName = profileName;
        DefaultCategory = defaultCategory;
        Rules = rules;

        var paths = new List<string>();
        var names = new List<string>();
        var keywords = new List<string>();
        var regexes = new List<string>();
        var suffixes = new List<string>();
        var domains = new List<string>();
        var cidrs = new List<string>();

        var direct = new List<string>();
        var proxy = new List<string>();
        var block = new List<string>();

        var blockedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var blockedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var blockedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var blockedCidrs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var directDomains = new List<string>();
        var proxyDomains = new List<string>();
        var directCidrs = new List<string>();

        foreach (var rule in rules)
        {
            switch (rule.Target)
            {
                case SplitRuleTarget.ProcessPath:
                    if (paths.Contains(rule.Value, StringComparer.OrdinalIgnoreCase)) continue;
                    paths.Add(rule.Value);
                    break;
                case SplitRuleTarget.ProcessName:
                    if (names.Contains(rule.Value, StringComparer.OrdinalIgnoreCase)) continue;
                    names.Add(rule.Value);
                    break;
                case SplitRuleTarget.Keyword:
                    if (keywords.Contains(rule.Value, StringComparer.OrdinalIgnoreCase)) continue;
                    keywords.Add(rule.Value);
                    break;
                case SplitRuleTarget.Regex:
                    if (regexes.Contains(rule.Value, StringComparer.Ordinal)) continue;
                    regexes.Add(rule.Value);
                    break;
                case SplitRuleTarget.Suffix:
                    if (suffixes.Contains(rule.Value, StringComparer.OrdinalIgnoreCase)) continue;
                    suffixes.Add(rule.Value);
                    break;
                case SplitRuleTarget.DomainSuffix:
                    if (domains.Contains(rule.Value, StringComparer.OrdinalIgnoreCase)) continue;
                    domains.Add(rule.Value);
                    break;
                default:
                    if (cidrs.Contains(rule.Value, StringComparer.OrdinalIgnoreCase)) continue;
                    cidrs.Add(rule.Value);
                    break;
            }

            switch (rule.Category)
            {
                case SplitRuleCategory.Direct:
                    direct.Add(rule.Value);
                    if (rule.Target == SplitRuleTarget.DomainSuffix) directDomains.Add(rule.Value);
                    else if (rule.Target == SplitRuleTarget.IpCidr) directCidrs.Add(rule.Value);
                    break;
                case SplitRuleCategory.Block:
                    block.Add(rule.Value);
                    switch (rule.Target)
                    {
                        case SplitRuleTarget.ProcessName: blockedNames.Add(rule.Value); break;
                        case SplitRuleTarget.ProcessPath: blockedPaths.Add(rule.Value); break;
                        case SplitRuleTarget.DomainSuffix: blockedDomains.Add(rule.Value); break;
                        case SplitRuleTarget.IpCidr: blockedCidrs.Add(rule.Value); break;
                    }
                    break;
                default:
                    proxy.Add(rule.Value);
                    if (rule.Target == SplitRuleTarget.DomainSuffix) proxyDomains.Add(rule.Value);
                    break;
            }
        }

        ProcessPaths = paths;
        ProcessNames = names;
        Keywords = keywords;
        Regexes = regexes;
        Suffixes = suffixes;
        Domains = domains;
        IpCidrs = cidrs;

        DirectValues = direct;
        ProxyValues = proxy;
        BlockValues = block;

        BlockedProcessNames = blockedNames;
        BlockedProcessPaths = blockedPaths;
        BlockedDomains = blockedDomains;
        BlockedIpCidrs = blockedCidrs;

        DirectDomains = directDomains;
        ProxyDomains = proxyDomains;
        BypassDomains = directDomains;
        DirectIpCidrs = directCidrs;

        var hashInput = new System.Text.StringBuilder(96);
        hashInput.Append(enabled ? '1' : '0')
                 .Append('|').Append(profileId)
                 .Append('|').Append(defaultCategory)
                 .Append('|').Append(string.Join(",", paths))
                 .Append('|').Append(string.Join(",", names))
                 .Append('|').Append(string.Join(",", keywords))
                 .Append('|').Append(string.Join(",", regexes))
                 .Append('|').Append(string.Join(",", suffixes))
                 .Append('|').Append(string.Join(",", domains))
                 .Append('|').Append(string.Join(",", cidrs));
        Hash = Convert.ToHexString(
            System.Security.Cryptography.MD5.HashData(
                System.Text.Encoding.UTF8.GetBytes(hashInput.ToString())));
    }

    public bool Enabled { get; }

    public string ProfileId { get; }

    public string ProfileName { get; }

    public SplitRuleCategory DefaultCategory { get; }

    public IReadOnlyList<SplitRouteRule> Rules { get; }

    public IReadOnlyList<string> ProcessPaths { get; }

    public IReadOnlyList<string> ProcessNames { get; }

    public IReadOnlyList<string> Keywords { get; }

    public IReadOnlyList<string> Regexes { get; }

    public IReadOnlyList<string> Suffixes { get; }

    public IReadOnlyList<string> Domains { get; }

    public IReadOnlyList<string> IpCidrs { get; }

    public IReadOnlyList<string> DirectValues { get; }

    public IReadOnlyList<string> ProxyValues { get; }

    public IReadOnlyList<string> BlockValues { get; }

    public ISet<string> BlockedProcessNames { get; }

    public ISet<string> BlockedProcessPaths { get; }

    public ISet<string> BlockedDomains { get; }

    // Domain rules split by the mode that owns them. Only Bypass domains may be pinned to
    // the physical gateway; Through Tunnel and Block domains must stay on the tunnel path.
    public IReadOnlyList<string> DirectDomains { get; }

    public IReadOnlyList<string> ProxyDomains { get; }

    public IReadOnlyList<string> BypassDomains { get; }

    public IReadOnlyList<string> DirectIpCidrs { get; }

    public ISet<string> BlockedIpCidrs { get; }

    public bool HasAppRules =>
        ProcessPaths.Count > 0 || ProcessNames.Count > 0
        || Keywords.Count > 0 || Regexes.Count > 0 || Suffixes.Count > 0;

    public bool HasDirectRules => DirectValues.Count > 0;

    public bool HasBlockRules => BlockValues.Count > 0;

    public bool HasDomainRules => Domains.Count > 0;

    public bool HasHostRules => Domains.Count > 0 || IpCidrs.Count > 0;

    public string Hash { get; }

    // Path.GetFileName is platform-dependent for backslash paths, so split manually.
    public static string FileNameOf(string? path)
    {
        var s = (path ?? "").Trim();
        if (s.Length == 0) return "";
        var cut = s.LastIndexOfAny(new[] { '\\', '/' });
        return cut >= 0 ? s[(cut + 1)..] : s;
    }

    // Deterministic precedence: Block beats Through Tunnel beats Bypass, and a more
    // specific matcher beats a looser one. The order below is the single source of
    // truth for that, and it does not depend on insertion order in the profile.
    private static readonly SplitRuleTarget[] Precedence =
    {
        SplitRuleTarget.ProcessPath,
        SplitRuleTarget.ProcessName,
        SplitRuleTarget.Suffix,
        SplitRuleTarget.Keyword,
        SplitRuleTarget.Regex,
    };

    public static SplitRuleCategory ResolveCategory(
        string? processName,
        string? processPath,
        SplitRuleCategory defaultCategory,
        IReadOnlyList<SplitRouteRule> rules)
    {
        if (rules is null || rules.Count == 0) return defaultCategory;

        var name = (processName ?? "").Trim();
        if (name.Length == 0) name = FileNameOf(processPath);
        var path = (processPath ?? "").Trim();

        SplitRuleCategory? best = null;
        var bestRank = int.MaxValue;

        foreach (var rule in rules)
        {
            if (!rule.IsProcessRule) continue;
            if (!Matches(rule, name, path)) continue;

            var rank = Array.IndexOf(Precedence, rule.Target);
            if (rank < 0) rank = Precedence.Length;

            // Block wins over any non-Block match regardless of specificity.
            if (rule.Category == SplitRuleCategory.Block)
            {
                return SplitRuleCategory.Block;
            }

            if (rank < bestRank)
            {
                bestRank = rank;
                best = rule.Category;
            }
        }

        return best ?? defaultCategory;
    }

    private static bool Matches(SplitRouteRule rule, string name, string path)
    {
        switch (rule.Target)
        {
            case SplitRuleTarget.ProcessPath:
                if (path.Length > 0)
                {
                    return string.Equals(rule.Value, path, StringComparison.OrdinalIgnoreCase);
                }
                // Name-only caller: fall back to the image name of the path rule.
                return name.Length > 0
                    && string.Equals(FileNameOf(rule.Value), name, StringComparison.OrdinalIgnoreCase);

            case SplitRuleTarget.ProcessName:
                return name.Length > 0
                    && string.Equals(rule.Value, name, StringComparison.OrdinalIgnoreCase);

            case SplitRuleTarget.Suffix:
                return SplitRules.SuffixMatches(rule.Value, name);

            case SplitRuleTarget.Keyword:
                return SplitRules.KeywordMatches(rule.Value, name, path);

            case SplitRuleTarget.Regex:
                return SplitRules.RegexMatches(rule.Value, name, path);

            default:
                return false;
        }
    }

    // Host callers (route pinning, WFP, the DNS split) ask the same question about a
    // domain or an address. Block still wins, and it is checked first.
    public SplitRuleCategory ResolveHost(string? host, IPAddress? address)
    {
        foreach (var rule in Rules)
        {
            if (rule.Category != SplitRuleCategory.Block) continue;
            if (rule.Target == SplitRuleTarget.DomainSuffix
                && SplitRules.DomainMatchesSuffix(host, rule.Value))
            {
                return SplitRuleCategory.Block;
            }
            if (rule.Target == SplitRuleTarget.IpCidr
                && address is not null
                && SplitRules.IpInCidr(address, rule.Value))
            {
                return SplitRuleCategory.Block;
            }
        }

        return DefaultCategory;
    }
}

public static class SplitRoutingPlanner
{
    public static SplitRoutingPlan Build(UserSettings settings)
    {
        if (settings is null || !settings.SplitTunnelEnabled) return SplitRoutingPlan.Disabled;

        var profiles = settings.SplitTunnelProfiles;
        if (profiles is not { Count: > 0 }) return SplitRoutingPlan.Disabled;

        var active = SplitTunnelProfileStore.ResolveActive(settings);
        if (active is null) return SplitRoutingPlan.Disabled;

        return Build(active);
    }

    public static SplitRoutingPlan Build(SplitTunnelProfile profile)
    {
        var rules = new List<SplitRouteRule>();
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(IEnumerable<SplitTunnelRule> source, SplitRuleCategory category)
        {
            foreach (var rule in source)
            {
                if (rule is null) continue;
                var target = ToTarget(rule.Kind, rule.Value, out var value);
                if (target is null) continue;
                var key = $"{target.Value}\u0001{value}";
                if (!seenKeys.Add(key)) continue;
                rules.Add(new SplitRouteRule(category, target.Value, value));
            }
        }

        // Block first so it wins when sanitising has not already removed a duplicate,
        // then Through Tunnel, then Bypass.
        Add(profile.Block, SplitRuleCategory.Block);
        Add(profile.Tunnel, SplitRuleCategory.Proxy);
        Add(profile.Bypass, SplitRuleCategory.Direct);

        var defaultCategory = profile.DefaultOutbound switch
        {
            SplitOutbound.Direct => SplitRuleCategory.Direct,
            SplitOutbound.Block => SplitRuleCategory.Block,
            _ => SplitRuleCategory.Proxy,
        };

        return new SplitRoutingPlan(
            enabled: true,
            profileId: profile.Id ?? "",
            profileName: profile.Name ?? "",
            defaultCategory,
            rules);
    }

    public static SplitRuleTarget? ToTarget(string? kind, string? rawValue, out string value)
    {
        value = "";
        var normalizedKind = SplitTargetKind.Normalize(kind);
        var raw = (rawValue ?? "").Trim();
        if (raw.Length == 0) return null;

        switch (normalizedKind)
        {
            case SplitTargetKind.Process:
                value = SplitRules.NormalizeProcessName(raw);
                return value.Length == 0 ? null : SplitRuleTarget.ProcessName;

            case SplitTargetKind.ProcessPath:
                value = raw;
                return SplitRuleTarget.ProcessPath;

            case SplitTargetKind.Domain:
                var domain = SplitRules.NormalizeSplitDomain(raw);
                if (domain is null) return null;
                value = domain;
                return SplitRuleTarget.DomainSuffix;

            case SplitTargetKind.Ip:
                var cidr = SplitRules.NormalizeSplitIpCidr(raw);
                if (cidr is null) return null;
                value = cidr;
                return SplitRuleTarget.IpCidr;

            case SplitTargetKind.Keyword:
                value = SplitRules.NormalizeKeyword(raw);
                return value.Length == 0 ? null : SplitRuleTarget.Keyword;

            case SplitTargetKind.Regex:
                var pattern = SplitRules.NormalizeRegex(raw);
                if (pattern is null) return null;
                value = pattern;
                return SplitRuleTarget.Regex;

            case SplitTargetKind.Suffix:
                value = SplitRules.NormalizeSuffix(raw);
                return value.Length == 0 ? null : SplitRuleTarget.Suffix;

            default:
                if (SplitRules.LooksLikeAppPath(raw))
                {
                    value = raw;
                    return SplitRuleTarget.ProcessPath;
                }
                value = SplitRules.NormalizeProcessName(raw);
                return value.Length == 0 ? null : SplitRuleTarget.ProcessName;
        }
    }
}

// Turns a plan's rules into concrete image paths for the two enforcement paths
// (the TUN adapter and the system-proxy mode). Keeping it here means both paths
// resolve names, keywords, suffixes and regexes through exactly one implementation.
public static class SplitRuleResolver
{
    public static List<string> ResolvePaths(
        SplitRoutingPlan plan,
        SplitRuleCategory category,
        Func<string, string?> pathByName)
    {
        var paths = new List<string>();
        if (plan is null || !plan.Enabled) return paths;

        void Add(string? p)
        {
            if (string.IsNullOrWhiteSpace(p)) return;
            if (!paths.Contains(p, StringComparer.OrdinalIgnoreCase)) paths.Add(p);
        }

        foreach (var rule in plan.Rules)
        {
            if (rule.Category != category) continue;

            switch (rule.Target)
            {
                case SplitRuleTarget.ProcessPath:
                    Add(rule.Value);
                    break;

                case SplitRuleTarget.ProcessName:
                    Add(pathByName?.Invoke(rule.Value));
                    break;

                case SplitRuleTarget.Keyword:
                case SplitRuleTarget.Suffix:
                case SplitRuleTarget.Regex:
                    foreach (var running in EnumerateMatchingProcesses(rule, pathByName))
                    {
                        Add(running);
                    }
                    break;
            }
        }

        return paths;
    }

    // The loose matchers cannot be expressed as a filter on a path we do not know, so
    // they are resolved against the processes that are running right now.
    private static IEnumerable<string> EnumerateMatchingProcesses(
        SplitRouteRule rule,
        Func<string, string?>? pathByName)
    {
        System.Diagnostics.Process[] all;
        try { all = System.Diagnostics.Process.GetProcesses(); }
        catch { yield break; }

        foreach (var process in all)
        {
            string? path = null;
            string? name = null;
            try
            {
                path = process.MainModule?.FileName;
                name = string.IsNullOrEmpty(path) ? null : SplitRoutingPlan.FileNameOf(path);
            }
            catch
            {
                // Access denied on a protected process is normal; skip it.
            }

            var isMatch = rule.Target switch
            {
                SplitRuleTarget.Keyword => SplitRules.KeywordMatches(rule.Value, name, path),
                SplitRuleTarget.Suffix => SplitRules.SuffixMatches(rule.Value, name),
                SplitRuleTarget.Regex => SplitRules.RegexMatches(rule.Value, name, path),
                _ => false,
            };

            string? result = null;
            if (isMatch)
            {
                // A keyword match may be known only by name; the WFP filter needs a path.
                result = path ?? (name is null ? null : pathByName?.Invoke(name));
            }

            try { process.Dispose(); } catch { }

            if (!string.IsNullOrEmpty(result)) yield return result!;
        }
    }
}
