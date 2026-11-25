using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Se7enPro.Models;

namespace Se7enPro.Services;

// Converts the old two-way split list ("include"/"exclude" + one shared entry list)
// into the profile model exactly once. The old keys are read straight from the JSON
// document because they no longer exist on UserSettings.
internal static class SplitTunnelMigration
{
    private const string LegacyEntriesKey = "splitTunnelEntries";
    private const string LegacyModeKey = "splitTunnelMode";

    internal static bool NeedsMigration(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;

        try
        {
            if (JsonNode.Parse(json) is not JsonObject root) return false;

            if (root.ContainsKey("splitTunnelProfiles")) return false;

            var entries = root[LegacyEntriesKey] as JsonArray;
            return entries is { Count: > 0 };
        }
        catch
        {
            return false;
        }
    }

    internal static bool Migrate(UserSettings settings, string? json)
    {
        if (settings is null || !NeedsMigration(json)) return false;

        // Already migrated (or the user already has profiles): never add a second import.
        if (settings.SplitTunnelProfiles is { Count: > 0 }) return false;

        var legacyMode = "exclude";
        var entries = new List<LegacySplitTunnelEntry>();

        try
        {
            var root = JsonNode.Parse(json!) as JsonObject;
            legacyMode = (root?[LegacyModeKey]?.GetValue<string>() ?? "exclude").Trim().ToLowerInvariant();

            if (root?[LegacyEntriesKey] is JsonArray array)
            {
                foreach (var node in array)
                {
                    if (node is not JsonObject entry) continue;
                    var kind = (entry["kind"]?.GetValue<string>() ?? "domain").Trim().ToLowerInvariant();
                    var value = (entry["value"]?.GetValue<string>() ?? "").Trim();
                    if (value.Length == 0) continue;
                    entries.Add(new LegacySplitTunnelEntry { Kind = kind, Value = value });
                }
            }
        }
        catch
        {
            return false;
        }

        var include = string.Equals(legacyMode, "include", StringComparison.OrdinalIgnoreCase);
        var profile = new SplitTunnelProfile
        {
            Id = SplitTunnelProfileStore.NewId(),
            Name = "Imported",
            DefaultOutbound = include ? SplitOutbound.Direct : SplitOutbound.Proxy,
        };

        var bucket = include ? SplitRouteAction.Tunnel : SplitRouteAction.Bypass;
        foreach (var entry in entries)
        {
            SplitTunnelProfileStore.SetApplication(profile, entry.Kind, entry.Value, bucket);
        }

        settings.SplitTunnelProfiles ??= new List<SplitTunnelProfile>();
        settings.SplitTunnelProfiles.Add(profile);
        settings.ActiveSplitTunnelProfileId = profile.Id;
        settings.ActiveSplitTunnelProfile = bucket;
        return true;
    }
}
