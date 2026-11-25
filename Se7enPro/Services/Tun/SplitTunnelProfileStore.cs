using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Se7enPro.Models;

namespace Se7enPro.Services;

public enum SplitMoveResult
{
    Added,
    Moved,
    AlreadyThere,
    Full,
}

public static class SplitTunnelProfileStore
{
    public const int MaxProfiles = 32;
    public const int MaxRulesPerProfile = 512;

    private static long _revision;

    // Bumped on every mutation so consumers can memoise derived routing plans.
    public static long Revision => Interlocked.Read(ref _revision);

    public static void Touch() => Interlocked.Increment(ref _revision);

    public static SplitTunnelProfile? ResolveActive(UserSettings settings)
    {
        var profiles = settings.SplitTunnelProfiles;
        if (profiles is not { Count: > 0 }) return null;

        foreach (var profile in profiles)
        {
            if (profile is not null
                && string.Equals(profile.Id, settings.ActiveSplitTunnelProfileId, StringComparison.Ordinal))
            {
                return profile;
            }
        }

        return profiles.FirstOrDefault(p => p is not null);
    }

    // Returns null when the caller asked for a profile with no name. The UI treats that
    // as a validation error instead of silently inventing "Profile".
    public static SplitTunnelProfile? CreateProfile(UserSettings settings, string? name)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0) return null;

        var profiles = settings.SplitTunnelProfiles;
        var unique = UniqueName(profiles, SanitizeName(trimmed));
        var profile = new SplitTunnelProfile
        {
            Id = NewId(),
            Name = unique,
            DefaultOutbound = SplitOutbound.Proxy,
        };
        profiles.Add(profile);
        Touch();

        if (string.IsNullOrEmpty(settings.ActiveSplitTunnelProfileId)
            || profiles.Count == 1)
        {
            settings.ActiveSplitTunnelProfileId = profile.Id;
        }

        return profile;
    }

    public static bool DeleteProfile(UserSettings settings, string profileId)
    {
        var profiles = settings.SplitTunnelProfiles;
        var victim = profiles.FirstOrDefault(p => string.Equals(p.Id, profileId, StringComparison.Ordinal));
        if (victim is null) return false;

        profiles.Remove(victim);
        Touch();

        if (string.Equals(settings.ActiveSplitTunnelProfileId, profileId, StringComparison.Ordinal))
        {
            settings.ActiveSplitTunnelProfileId = profiles.FirstOrDefault()?.Id ?? "";
        }

        return true;
    }

    public static bool SetActive(UserSettings settings, string profileId)
    {
        var profiles = settings.SplitTunnelProfiles;
        if (!profiles.Any(p => string.Equals(p.Id, profileId, StringComparison.Ordinal))) return false;
        if (string.Equals(settings.ActiveSplitTunnelProfileId, profileId, StringComparison.Ordinal)) return false;
        settings.ActiveSplitTunnelProfileId = profileId;
        Touch();
        return true;
    }

    public static List<SplitTunnelRule> RulesFor(SplitTunnelProfile profile, string action) =>
        action switch
        {
            SplitRouteAction.Bypass => profile.Bypass,
            SplitRouteAction.Block => profile.Block,
            _ => profile.Tunnel,
        };

    public static SplitMoveResult SetApplication(
        SplitTunnelProfile profile,
        string kind,
        string value,
        string action,
        string? displayName = null)
    {
        if (profile is null) throw new ArgumentNullException(nameof(profile));
        action = NormalizeAction(action);
        var normalizedKind = NormalizeKindForMerge(kind, value);
        var normalizedValue = NormalizeValue(normalizedKind, value);
        if (normalizedValue.Length == 0) return SplitMoveResult.AlreadyThere;

        var existing = FindOwner(profile, normalizedKind, normalizedValue);
        if (existing is not null)
        {
            if (string.Equals(existing.Value, normalizedValue, StringComparison.OrdinalIgnoreCase))
            {
                existing.DisplayName = PickDisplayName(existing.DisplayName, displayName);
            }

            if (string.Equals(OwnerAction(profile, existing), action, StringComparison.Ordinal))
                return SplitMoveResult.AlreadyThere;

            var from = RulesFor(profile, OwnerAction(profile, existing));
            from.Remove(existing);
            var to = RulesFor(profile, action);
            to.Add(existing);
            Touch();
            return SplitMoveResult.Moved;
        }

        var target = RulesFor(profile, action);
        if (profile.RuleCount >= MaxRulesPerProfile) return SplitMoveResult.Full;

        target.Add(new SplitTunnelRule
        {
            Kind = normalizedKind,
            Value = normalizedValue,
            DisplayName = (displayName ?? "").Trim(),
        });
        Touch();
        return SplitMoveResult.Added;
    }

    // Removes one rule by identity, from whichever of the three lists owns it. The
    // caller never has to know the owning mode: passing the wrong mode cannot leave
    // the rule stranded, which is what made the delete button look broken.
    public static bool RemoveValue(SplitTunnelProfile profile, string kind, string value)
    {
        if (profile is null) return false;

        var normalizedKind = NormalizeKindForMerge(kind, value);
        var normalizedValue = NormalizeValue(normalizedKind, value);
        if (normalizedValue.Length == 0) return false;

        var owner = FindOwner(profile, normalizedKind, normalizedValue);
        if (owner is null) return false;

        var removed = profile.Block.Remove(owner)
                      || profile.Tunnel.Remove(owner)
                      || profile.Bypass.Remove(owner);

        if (removed) Touch();
        return removed;
    }

    // Convenience overload kept for callers that only have a display name and a value.
    public static bool RemoveValue(SplitTunnelProfile profile, string action, string kind, string value)
    {
        _ = action;
        return RemoveValue(profile, kind, value);
    }

    public static int Clear(SplitTunnelProfile profile, string action)
    {
        var list = RulesFor(profile, NormalizeAction(action));
        var count = list.Count;
        list.Clear();
        if (count > 0) Touch();
        return count;
    }

    public static SplitTunnelRule? FindOwner(SplitTunnelProfile profile, string kind, string value)
    {
        foreach (var rule in profile.AllRules())
        {
            if (rule is null) continue;
            if (!string.Equals(NormalizeKindForMerge(rule.Kind, rule.Value), kind, StringComparison.Ordinal)) continue;
            if (string.Equals(NormalizeValue(kind, rule.Value), value, StringComparison.OrdinalIgnoreCase)) return rule;
        }
        return null;
    }

    public static string OwnerAction(SplitTunnelProfile profile, SplitTunnelRule rule)
    {
        if (profile.Block.Contains(rule)) return SplitRouteAction.Block;
        if (profile.Bypass.Contains(rule)) return SplitRouteAction.Bypass;
        return SplitRouteAction.Tunnel;
    }

    public static string NormalizeAction(string? action) =>
        action is SplitRouteAction.Bypass or SplitRouteAction.Block ? action : SplitRouteAction.Tunnel;

    // An explicit kind always wins. Only the picker's "App" kind is inferred from the
    // value, so a path stays a path rule and a bare image name becomes a name rule.
    public static string NormalizeKindForMerge(string? kind, string? value)
    {
        var normalized = SplitTargetKind.Normalize(kind);
        if (normalized != SplitTargetKind.App) return normalized;
        return SplitRules.LooksLikeAppPath(value ?? "") ? SplitTargetKind.App : SplitTargetKind.Process;
    }

    public static string NormalizeValue(string kind, string value)
    {
        var raw = (value ?? "").Trim();
        if (raw.Length == 0) return "";
        return SplitTargetKind.Normalize(kind) switch
        {
            SplitTargetKind.Process => SplitRules.NormalizeProcessName(raw),
            SplitTargetKind.ProcessPath => raw,
            SplitTargetKind.Domain => SplitRules.NormalizeSplitDomain(raw) ?? "",
            SplitTargetKind.Ip => SplitRules.NormalizeSplitIpCidr(raw) ?? "",
            SplitTargetKind.Keyword => SplitRules.NormalizeKeyword(raw),
            SplitTargetKind.Regex => SplitRules.NormalizeRegex(raw) ?? "",
            SplitTargetKind.Suffix => SplitRules.NormalizeSuffix(raw),
            _ => SplitRules.LooksLikeAppPath(raw) ? raw : SplitRules.NormalizeProcessName(raw),
        };
    }

    public static string SanitizeName(string? name)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0) return "Profile";
        return trimmed.Length > 40 ? trimmed[..40] : trimmed;
    }

    public static string UniqueName(IReadOnlyList<SplitTunnelProfile> profiles, string baseName)
    {
        var name = SanitizeName(baseName);
        if (!profiles.Any(p => p is not null && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            return name;
        }

        for (var i = 2; i < 1000; i++)
        {
            var candidate = $"{name} {i}";
            if (!profiles.Any(p => p is not null && string.Equals(p.Name, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                return candidate;
            }
        }

        return $"{name} {Guid.NewGuid():N}"[..Math.Min(44, name.Length + 33)];
    }

    public static string NewId() => "p" + Guid.NewGuid().ToString("N")[..12];

    public static bool Rename(UserSettings settings, SplitTunnelProfile profile, string? newName)
    {
        // A blank name means "leave it alone" — the previous behaviour silently renamed
        // the profile to "Profile", which is exactly the empty-input bug.
        var trimmed = (newName ?? "").Trim();
        if (trimmed.Length == 0) return false;

        var desired = SanitizeName(trimmed);
        if (string.Equals(desired, profile.Name, StringComparison.Ordinal)) return false;

        var others = settings.SplitTunnelProfiles.Where(p => !ReferenceEquals(p, profile)).ToList();
        profile.Name = UniqueName(others, desired);
        Touch();
        return true;
    }

    private static string PickDisplayName(string? current, string? candidate)
    {
        var c = (candidate ?? "").Trim();
        if (c.Length == 0) return (current ?? "").Trim();
        return c;
    }

    public static void NormalizeAll(UserSettings settings)
    {
        settings.ActiveSplitTunnelProfile = NormalizeAction(settings.ActiveSplitTunnelProfile);

        var profiles = settings.SplitTunnelProfiles;
        for (var i = profiles.Count - 1; i >= 0; i--)
        {
            var profile = profiles[i];
            if (profile is null)
            {
                profiles.RemoveAt(i);
                continue;
            }

            profile.Bypass ??= new List<SplitTunnelRule>();
            profile.Tunnel ??= new List<SplitTunnelRule>();
            profile.Block ??= new List<SplitTunnelRule>();

            if (string.IsNullOrWhiteSpace(profile.Id))
            {
                profile.Id = NewId();
            }
            profile.Name = SanitizeName(profile.Name);
            profile.DefaultOutbound = SplitOutbound.Normalize(profile.DefaultOutbound);
        }

        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < profiles.Count; i++)
        {
            var profile = profiles[i];
            if (!seenIds.Add(profile.Id))
            {
                profile.Id = NewId();
                seenIds.Add(profile.Id);
            }

            SanitizeList(profile.Block, profile);
            SanitizeList(profile.Tunnel, profile);
            SanitizeList(profile.Bypass, profile);
        }

        if (!profiles.Any(p => string.Equals(p.Id, settings.ActiveSplitTunnelProfileId, StringComparison.Ordinal)))
        {
            settings.ActiveSplitTunnelProfileId = profiles.FirstOrDefault()?.Id ?? "";
        }

        Touch();
    }

    private static void SanitizeList(List<SplitTunnelRule> list, SplitTunnelProfile profile)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = list.Count - 1; i >= 0; i--)
        {
            var rule = list[i];
            if (rule is null)
            {
                list.RemoveAt(i);
                continue;
            }

            var kind = NormalizeKindForMerge(rule.Kind, rule.Value);
            var value = NormalizeValue(kind, rule.Value);
            if (value.Length == 0)
            {
                list.RemoveAt(i);
                continue;
            }

            rule.Kind = kind;
            rule.Value = value;
            rule.DisplayName = (rule.DisplayName ?? "").Trim();

            var key = $"{kind}\u0001{value}";
            if (!seen.Add(key) || IsOwnedElsewhere(profile, list, kind, value))
            {
                list.RemoveAt(i);
            }
        }
    }

    // Block wins over Through Tunnel, and Through Tunnel wins over Bypass, when the
    // same target was somehow persisted twice (hand-edited file, older build, merge).
    private static bool IsOwnedElsewhere(SplitTunnelProfile profile, List<SplitTunnelRule> current, string kind, string value)
    {
        var order = new List<SplitTunnelRule>[]
        {
            profile.Block,
            profile.Tunnel,
            profile.Bypass,
        };

        foreach (var list in order)
        {
            if (ReferenceEquals(list, current)) return false;
            foreach (var rule in list)
            {
                if (rule is null) continue;
                var otherKind = NormalizeKindForMerge(rule.Kind, rule.Value);
                if (!string.Equals(otherKind, kind, StringComparison.Ordinal)) continue;
                if (string.Equals(NormalizeValue(otherKind, rule.Value), value, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
