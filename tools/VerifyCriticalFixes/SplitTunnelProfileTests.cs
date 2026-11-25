using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Se7enPro.Models;
using Se7enPro.Services;

namespace VerifyCriticalFixes;

// Headless checks for the profile-based split-tunnel core.
//
// Nothing here opens a tunnel or touches the network stack: the routing plan, the
// mihomo rule emission, the store's isolation guarantees and the real JSON persistence
// path are all driven directly.
internal static class SplitTunnelProfileTests
{
    private const string ChromePath = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
    private const string SteamPath = @"C:\Program Files (x86)\Steam\steam.exe";
    private const string GamePath = @"D:\Games\Game.exe";
    private const string AnotherAppPath = @"C:\Apps\AnotherApp.exe";
    private const string DiscordPath = @"C:\Users\me\AppData\Local\Discord\app-1.0\Discord.exe";

    public static void Run()
    {
        Program.Section("Split tunnel — Bypass / Through Tunnel / Block isolation");
        Test1And2And3_Isolation();
        Test4_MovingApplications();
        Test5_ProfileIsolation();
        Test6_PersistenceAcrossRestart();
        Test7_DefaultOutbound();
        Test8_MihomoRuleEmission();
        TestNoSharedState();
        TestDomainAndIpRuleModes();
        TestEdgeCases();
        TestMatchers();
        TestDeleteAndNaming();
        TestProxyModeResolution();
        TestEnforcementGaps();
        TestRuleCapReportsFull();
        TestMigration();
    }

    // Test 1-3: the three modes hold their own rules and nothing bleeds between them.
    private static void Test1And2And3_Isolation()
    {
        var settings = NewSettings();
        var profile = CreateProfile(settings, "Gaming", SplitOutbound.Proxy);

        Step("Test 1 — App A + App B into Bypass");
        Program.Check(Add(profile, ChromePath, SplitRouteAction.Bypass) == SplitMoveResult.Added, "App A added to bypass");
        Program.Check(Add(profile, SteamPath, SplitRouteAction.Bypass) == SplitMoveResult.Added, "App B added to bypass");
        Program.Check(profile.Bypass.Count == 2 && profile.Tunnel.Count == 0 && profile.Block.Count == 0,
            "bypass holds exactly the two apps, the other lists stay empty",
            $"bypass={profile.Bypass.Count} tunnel={profile.Tunnel.Count} block={profile.Block.Count}");

        var plan = SplitRoutingPlanner.Build(settings);
        Program.Check(CategoryOf(plan, "chrome.exe") == SplitRuleCategory.Direct, "App A resolves to Direct (bypass)");
        Program.Check(CategoryOf(plan, "steam.exe") == SplitRuleCategory.Direct, "App B resolves to Direct (bypass)");

        Step("Test 2 — App C into Through Tunnel");
        Program.Check(Add(profile, GamePath, SplitRouteAction.Tunnel) == SplitMoveResult.Added, "App C added to tunnel");
        Program.Check(profile.Bypass.Count == 2 && profile.Tunnel.Count == 1 && profile.Block.Count == 0,
            "A and B are still in bypass; C is the only tunnel rule",
            $"bypass={profile.Bypass.Count} tunnel={profile.Tunnel.Count} block={profile.Block.Count}");

        plan = SplitRoutingPlanner.Build(settings);
        Program.Check(CategoryOf(plan, "chrome.exe") == SplitRuleCategory.Direct, "App A still bypasses after C was added");
        Program.Check(CategoryOf(plan, "steam.exe") == SplitRuleCategory.Direct, "App B still bypasses after C was added");
        Program.Check(CategoryOf(plan, "game.exe") == SplitRuleCategory.Proxy, "App C uses the proxy");

        Step("Test 3 — App D into Block");
        Program.Check(Add(profile, AnotherAppPath, SplitRouteAction.Block) == SplitMoveResult.Added, "App D added to block");
        plan = SplitRoutingPlanner.Build(settings);
        Program.Check(CategoryOf(plan, "anotherapp.exe") == SplitRuleCategory.Block, "App D is blocked");
        Program.Check(CategoryOf(plan, "chrome.exe") == SplitRuleCategory.Direct, "App A unaffected by the block rule");
        Program.Check(CategoryOf(plan, "steam.exe") == SplitRuleCategory.Direct, "App B unaffected by the block rule");
        Program.Check(CategoryOf(plan, "game.exe") == SplitRuleCategory.Proxy, "App C unaffected by the block rule");
        Program.Check(profile.Bypass.Count == 2 && profile.Tunnel.Count == 1 && profile.Block.Count == 1,
            "each mode kept its own list",
            $"bypass={profile.Bypass.Count} tunnel={profile.Tunnel.Count} block={profile.Block.Count}");
    }

    // Test 4: moving an application is explicit and never duplicates it.
    private static void Test4_MovingApplications()
    {
        Step("Test 4 — move App A from Bypass to Through Tunnel");
        var settings = NewSettings();
        var profile = CreateProfile(settings, "Move", SplitOutbound.Direct);
        Add(profile, ChromePath, SplitRouteAction.Bypass);
        Add(profile, SteamPath, SplitRouteAction.Bypass);

        var result = Add(profile, ChromePath, SplitRouteAction.Tunnel);
        Program.Check(result == SplitMoveResult.Moved, "the move is reported as Moved");
        Program.Check(profile.Bypass.Count == 1 && SameValue(profile.Bypass[0], ChromePath) == false,
            "App A was actually removed from bypass");
        Program.Check(profile.Tunnel.Count == 1 && SameValue(profile.Tunnel[0], ChromePath), "App A is in tunnel");
        Program.Check(profile.Bypass.Count == 1 && SameValue(profile.Bypass[0], SteamPath), "App B is still in bypass");
        Program.Check(CountOwned(profile, ChromePath) == 1, "App A exists in exactly one mode");

        var plan = SplitRoutingPlanner.Build(settings);
        Program.Check(CategoryOf(plan, "chrome.exe") == SplitRuleCategory.Proxy, "App A now resolves to the proxy");
        Program.Check(CategoryOf(plan, "steam.exe") == SplitRuleCategory.Direct, "App B still resolves to direct");

        var again = Add(profile, ChromePath, SplitRouteAction.Tunnel);
        Program.Check(again == SplitMoveResult.AlreadyThere && profile.Tunnel.Count == 1,
            "re-adding the same app to the same mode does not duplicate it");

        var kindBefore = profile.Tunnel[0].Kind;
        Add(profile, ChromePath, SplitRouteAction.Block);
        Program.Check(profile.Tunnel.Count == 0 && profile.Block.Count == 1,
            "moving to Block removes it from Tunnel");
        Program.Check(kindBefore == profile.Block[0].Kind, "the stored identifier kind is preserved across moves");

        Step("Test 4b — removing one app does not touch the others");
        Add(profile, GamePath, SplitRouteAction.Block);
        SplitTunnelProfileStore.RemoveValue(profile, SplitRouteAction.Block, profile.Block[0].Kind, profile.Block[0].Value);
        Program.Check(profile.Block.Count == 1 && SameValue(profile.Block[0], GamePath),
            "only the removed app left the block list");
        Program.Check(profile.Bypass.Count == 1 && SameValue(profile.Bypass[0], SteamPath),
            "the bypass app is untouched");
    }

    // Test 5: profiles never exchange rules.
    private static void Test5_ProfileIsolation()
    {
        Step("Test 5 — two profiles, rapid switching");
        var settings = NewSettings();

        var gaming = CreateProfile(settings, "Gaming", SplitOutbound.Proxy);
        Add(gaming, DiscordPath, SplitRouteAction.Bypass);
        Add(gaming, SteamPath, SplitRouteAction.Tunnel);
        Add(gaming, GamePath, SplitRouteAction.Tunnel);
        Add(gaming, AnotherAppPath, SplitRouteAction.Block);

        var normal = CreateProfile(settings, "Normal", SplitOutbound.Direct);
        Add(normal, ChromePath, SplitRouteAction.Tunnel);
        Add(normal, AnotherAppPath, SplitRouteAction.Bypass);
        Add(normal, SteamPath, SplitRouteAction.Bypass);

        var gamingSnapshot = Snapshot(gaming);
        var normalSnapshot = Snapshot(normal);
        Program.Check(gamingSnapshot != normalSnapshot, "the two profiles have different rule sets");

        for (var i = 0; i < 25; i++)
        {
            SplitTunnelProfileStore.SetActive(settings, i % 2 == 0 ? normal.Id : gaming.Id);
            var plan = SplitRoutingPlanner.Build(settings);
            var expectedId = i % 2 == 0 ? normal.Id : gaming.Id;
            if (!string.Equals(plan.ProfileId, expectedId, StringComparison.Ordinal))
            {
                Program.Check(false, "the active plan follows the active profile", $"iteration {i}");
                return;
            }
        }

        Program.Check(Snapshot(gaming) == gamingSnapshot, "Gaming's rules survived 25 switches");
        Program.Check(Snapshot(normal) == normalSnapshot, "Normal's rules survived 25 switches");

        SplitTunnelProfileStore.SetActive(settings, gaming.Id);
        var gamingPlan = SplitRoutingPlanner.Build(settings);
        Program.Check(CategoryOf(gamingPlan, "discord.exe") == SplitRuleCategory.Direct, "Gaming: Discord bypasses");
        Program.Check(CategoryOf(gamingPlan, "steam.exe") == SplitRuleCategory.Proxy, "Gaming: Steam is tunneled");
        Program.Check(CategoryOf(gamingPlan, "game.exe") == SplitRuleCategory.Proxy, "Gaming: Game is tunneled");
        Program.Check(CategoryOf(gamingPlan, "anotherapp.exe") == SplitRuleCategory.Block, "Gaming: AnotherApp is blocked");
        Program.Check(CategoryOf(gamingPlan, "chrome.exe") == SplitRuleCategory.Proxy, "Gaming: Chrome follows the proxy default");

        SplitTunnelProfileStore.SetActive(settings, normal.Id);
        var normalPlan = SplitRoutingPlanner.Build(settings);
        Program.Check(CategoryOf(normalPlan, "chrome.exe") == SplitRuleCategory.Proxy, "Normal: Chrome is tunneled");
        Program.Check(CategoryOf(normalPlan, "anotherapp.exe") == SplitRuleCategory.Direct, "Normal: AnotherApp bypasses");
        Program.Check(CategoryOf(normalPlan, "steam.exe") == SplitRuleCategory.Direct, "Normal: Steam bypasses");
        Program.Check(CategoryOf(normalPlan, "discord.exe") == SplitRuleCategory.Direct, "Normal: Discord follows the direct default");
        Program.Check(CategoryOf(normalPlan, "game.exe") == SplitRuleCategory.Direct, "Normal: Game follows the direct default");

        Step("Test 5b — deleting a profile leaves the other one intact");
        SplitTunnelProfileStore.DeleteProfile(settings, gaming.Id);
        Program.Check(settings.SplitTunnelProfiles.Count == 1, "only one profile left");
        Program.Check(Snapshot(settings.SplitTunnelProfiles[0]) == normalSnapshot, "the surviving profile is unchanged");
        Program.Check(string.Equals(settings.ActiveSplitTunnelProfileId, normal.Id, StringComparison.Ordinal),
            "the active id moved to the surviving profile");
    }

    // Test 6: the real SettingsService write/read cycle restores everything.
    private static void Test6_PersistenceAcrossRestart()
    {
        Step("Test 6 — persistence through a real write + reload");

        var dir = Path.Combine(Path.GetTempPath(), "se7en-split-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            SettingsService.DirectoryOverride = dir;

            var first = new SettingsService(NullLogger<SettingsService>.Instance);
            first.Load();

            var settings = first.Settings;
            settings.SplitTunnelEnabled = true;
            settings.SystemWideTunneling = true;

            var gaming = CreateProfile(settings, "Gaming", SplitOutbound.Proxy);
            Add(gaming, DiscordPath, SplitRouteAction.Bypass);
            Add(gaming, SteamPath, SplitRouteAction.Tunnel);
            Add(gaming, GamePath, SplitRouteAction.Tunnel);
            Add(gaming, AnotherAppPath, SplitRouteAction.Block);
            Add(gaming, "example.com", SplitRouteAction.Bypass, SplitTargetKind.Domain);
            Add(gaming, "203.0.113.0/24", SplitRouteAction.Tunnel, SplitTargetKind.Ip);

            var normal = CreateProfile(settings, "Normal", SplitOutbound.Direct);
            Add(normal, ChromePath, SplitRouteAction.Tunnel);
            Add(normal, AnotherAppPath, SplitRouteAction.Bypass);
            SplitTunnelProfileStore.SetActive(settings, normal.Id);

            Program.Check(first.Save(), "settings written to disk");

            // A fresh service instance is exactly what a restart does.
            var second = new SettingsService(NullLogger<SettingsService>.Instance);
            second.Load();
            var restored = second.Settings;

            Program.Check(restored.SplitTunnelProfiles.Count == 2, "both profiles restored",
                $"count={restored.SplitTunnelProfiles.Count}");
            Program.Check(restored.SplitTunnelEnabled, "the enable switch is restored");
            Program.Check(string.Equals(restored.ActiveSplitTunnelProfileId, normal.Id, StringComparison.Ordinal),
                "the active profile id is restored");

            var restoredGaming = restored.SplitTunnelProfiles.First(p => p.Name == "Gaming");
            var restoredNormal = restored.SplitTunnelProfiles.First(p => p.Name == "Normal");
            Program.Check(Snapshot(restoredGaming) == Snapshot(gaming), "Gaming's rules round-trip byte for byte");
            Program.Check(Snapshot(restoredNormal) == Snapshot(normal), "Normal's rules round-trip byte for byte");
            Program.Check(restoredGaming.DefaultOutbound == SplitOutbound.Proxy
                          && restoredNormal.DefaultOutbound == SplitOutbound.Direct,
                "each profile's default outbound is restored");

            var plan = SplitRoutingPlanner.Build(restored);
            Program.Check(CategoryOf(plan, "chrome.exe") == SplitRuleCategory.Proxy, "after restart Chrome still uses the proxy");
            Program.Check(CategoryOf(plan, "anotherapp.exe") == SplitRuleCategory.Direct, "after restart AnotherApp still bypasses");

            // The domain/ip rules live in Gaming, so look at the plan of the profile
            // that owns them instead of the active one.
            var gamingPlan = SplitRoutingPlanner.Build(restoredGaming);
            Program.Check(gamingPlan.Enabled && gamingPlan.Domains.Contains("example.com"), "domain rules are restored");
            Program.Check(gamingPlan.IpCidrs.Contains("203.0.113.0/24"), "ip rules are restored");
            Program.Check(gamingPlan.DirectValues.Contains("example.com")
                          && !gamingPlan.BlockedDomains.Contains("example.com")
                          && !gamingPlan.ProxyValues.Contains("example.com"),
                "the restored domain rule keeps its Bypass action");
            Program.Check(gamingPlan.ProxyValues.Contains("203.0.113.0/24"),
                "the restored ip rule keeps its Through Tunnel action");

            Step("Test 6b — a corrupted profile in the file is repaired, not fatal");
            var path = Path.Combine(dir, "settings.json");
            var raw = File.ReadAllText(path);
            File.WriteAllText(path, raw.Replace("\"splitTunnelProfiles\":", "\"splitTunnelProfilesX\":"));
            var third = new SettingsService(NullLogger<SettingsService>.Instance);
            third.Load();
            Program.Check(third.Settings.SplitTunnelProfiles.Count == 0,
                "without the key the app falls back to no profiles instead of crashing");
        }
        finally
        {
            SettingsService.DirectoryOverride = null;
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // Test 7: unmatched traffic follows the profile default in both directions.
    private static void Test7_DefaultOutbound()
    {
        Step("Test 7 — default outbound");
        var settings = NewSettings();

        var proxyDefault = CreateProfile(settings, "Proxy default", SplitOutbound.Proxy);
        Add(proxyDefault, SteamPath, SplitRouteAction.Bypass);
        var proxyPlan = SplitRoutingPlanner.Build(settings);
        Program.Check(CategoryOf(proxyPlan, "unknown_app.exe") == SplitRuleCategory.Proxy,
            "with a Proxy default, an unmatched app is tunneled");

        var directDefault = CreateProfile(settings, "Direct default", SplitOutbound.Direct);
        Add(directDefault, SteamPath, SplitRouteAction.Tunnel);
        var directPlan = SplitRoutingPlanner.Build(settings);
        Program.Check(CategoryOf(directPlan, "unknown_app.exe") == SplitRuleCategory.Direct,
            "with a Direct default, an unmatched app bypasses");
        Program.Check(CategoryOf(directPlan, "steam.exe") == SplitRuleCategory.Proxy,
            "a Through Tunnel rule still overrides a Direct default");

        var blockDefault = CreateProfile(settings, "Block default", SplitOutbound.Block);
        Add(blockDefault, ChromePath, SplitRouteAction.Tunnel);
        var blockPlan = SplitRoutingPlanner.Build(settings);
        Program.Check(CategoryOf(blockPlan, "unknown_app.exe") == SplitRuleCategory.Block,
            "with a Block default, an unmatched app is blocked");
        Program.Check(CategoryOf(blockPlan, "chrome.exe") == SplitRuleCategory.Proxy,
            "an explicit Through Tunnel rule still wins over a Block default");

        Step("Test 7b — the plan is disabled while the feature is off");
        settings.SplitTunnelEnabled = false;
        SplitTunnelProfileStore.Touch();
        Program.Check(!SplitRoutingPlanner.Build(settings).Enabled, "disabling split tunnelling disables the plan");
        settings.SplitTunnelEnabled = true;
        SplitTunnelProfileStore.Touch();
        Program.Check(SplitRoutingPlanner.Build(settings).Enabled, "enabling it again restores the plan");
    }

    // Test 8: the rules the engine actually consumes (mihomo YAML) are correct and ordered.
    private static void Test8_MihomoRuleEmission()
    {
        Step("Test 8 — generated engine rules (mihomo process rules)");
        var settings = NewSettings();
        var profile = CreateProfile(settings, "Engine", SplitOutbound.Proxy);
        Add(profile, AnotherAppPath, SplitRouteAction.Block);
        Add(profile, ChromePath, SplitRouteAction.Bypass);
        Add(profile, SteamPath, SplitRouteAction.Tunnel);
        Add(profile, GamePath, SplitRouteAction.Tunnel);

        var plan = SplitRoutingPlanner.Build(settings);
        var yaml = MihomoConfigBuilder.BuildYaml(
            10808, false, ConnectionMethod.WireGuard, 1280,
            "se7en_tun", "198.18.0.1", 30, "fdfe:dcba:9876::1", 126,
            settings, "Ethernet", new[] { "aether" }, plan);

        var blockIdx = yaml.IndexOf("PROCESS-PATH," + AnotherAppPath + ",REJECT", StringComparison.Ordinal);
        var bypassIdx = yaml.IndexOf("PROCESS-PATH," + ChromePath + ",Se7enDirect", StringComparison.Ordinal);
        var steamIdx = yaml.IndexOf("PROCESS-PATH," + SteamPath + ",Se7enProxy", StringComparison.Ordinal);
        var gameIdx = yaml.IndexOf("PROCESS-PATH," + GamePath + ",Se7enProxy", StringComparison.Ordinal);
        var matchIdx = yaml.IndexOf("MATCH,Se7enProxy", StringComparison.Ordinal);

        Program.Check(blockIdx > 0, "the blocked app is emitted as a REJECT rule");
        Program.Check(bypassIdx > 0, "the bypassed app is emitted as a Se7enDirect rule");
        Program.Check(steamIdx > 0 && gameIdx > 0, "both tunneled apps are emitted as Se7enProxy rules");
        Program.Check(blockIdx < bypassIdx && bypassIdx < steamIdx && bypassIdx < gameIdx,
            "precedence is Block -> Bypass -> Tunnel");
        Program.Check(matchIdx > gameIdx && matchIdx > blockIdx, "the default MATCH rule comes last");
        Program.Check(yaml.Contains("find-process-mode: strict", StringComparison.Ordinal),
            "process matching is enabled for the engine");
        Program.Check(!yaml.Contains("PROCESS-PATH," + ChromePath + ",REJECT", StringComparison.Ordinal),
            "no application appears under two different targets");

        Step("Test 8b — Direct default emits MATCH,Se7enDirect");
        profile.DefaultOutbound = SplitOutbound.Direct;
        SplitTunnelProfileStore.Touch();
        plan = SplitRoutingPlanner.Build(settings);
        yaml = MihomoConfigBuilder.BuildYaml(
            10808, false, ConnectionMethod.WireGuard, 1280,
            "se7en_tun", "198.18.0.1", 30, "fdfe:dcba:9876::1", 126,
            settings, "Ethernet", new[] { "aether" }, plan);
        Program.Check(yaml.Contains("'MATCH,Se7enDirect'", StringComparison.Ordinal),
            "a Direct default becomes MATCH,Se7enDirect");
        Program.Check(yaml.Contains("PROCESS-PATH," + SteamPath + ",Se7enProxy", StringComparison.Ordinal),
            "explicit tunnel rules still override the Direct default");

        Step("Test 8c — Block default emits MATCH,REJECT");
        profile.DefaultOutbound = SplitOutbound.Block;
        SplitTunnelProfileStore.Touch();
        plan = SplitRoutingPlanner.Build(settings);
        yaml = MihomoConfigBuilder.BuildYaml(
            10808, false, ConnectionMethod.WireGuard, 1280,
            "se7en_tun", "198.18.0.1", 30, "fdfe:dcba:9876::1", 126,
            settings, "Ethernet", new[] { "aether" }, plan);
        Program.Check(yaml.Contains("'MATCH,REJECT'", StringComparison.Ordinal),
            "a Block default becomes MATCH,REJECT");

        Step("Test 8d — feature off emits no split rules at all");
        settings.SplitTunnelEnabled = false;
        SplitTunnelProfileStore.Touch();
        yaml = MihomoConfigBuilder.BuildYaml(
            10808, false, ConnectionMethod.WireGuard, 1280,
            "se7en_tun", "198.18.0.1", 30, "fdfe:dcba:9876::1", 126,
            settings, "Ethernet", new[] { "aether" }, SplitRoutingPlanner.Build(settings));
        Program.Check(!yaml.Contains(AnotherAppPath, StringComparison.Ordinal)
                      && !yaml.Contains("PROCESS-PATH," + ChromePath, StringComparison.Ordinal),
            "turning split tunnelling off removes every application rule");
        Program.Check(yaml.Contains("'MATCH,Se7enProxy'", StringComparison.Ordinal), "everything falls back to the proxy");
    }

    // The reported bug: adding an app to Through Tunnel used to drag every previously
    // added Bypass app with it because both modes pointed at one list.
    // Domain/IP rules must follow the mode that owns them. Only Bypass entries may be
    // resolved locally and pinned to the physical gateway; Tunnel and Block entries must
    // stay on the tunnel path.
    private static void TestDomainAndIpRuleModes()
    {
        Step("Per-mode domain and IP rules");
        var settings = NewSettings();
        var profile = CreateProfile(settings, "Domains", SplitOutbound.Proxy);

        Add(profile, "direct.example", SplitRouteAction.Bypass, SplitTargetKind.Domain);
        Add(profile, "proxied.example", SplitRouteAction.Tunnel, SplitTargetKind.Domain);
        Add(profile, "blocked.example", SplitRouteAction.Block, SplitTargetKind.Domain);
        Add(profile, "192.0.2.0/24", SplitRouteAction.Bypass, SplitTargetKind.Ip);
        Add(profile, "198.51.100.0/24", SplitRouteAction.Tunnel, SplitTargetKind.Ip);
        Add(profile, "203.0.113.0/24", SplitRouteAction.Block, SplitTargetKind.Ip);

        var plan = SplitRoutingPlanner.Build(settings);
        Program.Check(plan.BypassDomains.SequenceEqual(new[] { "direct.example" }),
            "only the Bypass domain is eligible for local resolution + direct pinning",
            string.Join(",", plan.BypassDomains));
        Program.Check(plan.DirectDomains.SequenceEqual(plan.BypassDomains),
            "DirectDomains and BypassDomains always agree");
        Program.Check(plan.ProxyDomains.SequenceEqual(new[] { "proxied.example" }),
            "the Through Tunnel domain is kept on the proxy path",
            string.Join(",", plan.ProxyDomains));
        Program.Check(plan.BlockedDomains.Contains("blocked.example"),
            "the Block domain is withheld from every direct path");

        Program.Check(plan.DirectIpCidrs.SequenceEqual(new[] { "192.0.2.0/24" }),
            "only the Bypass CIDR is pinned to the gateway",
            string.Join(",", plan.DirectIpCidrs));
        Program.Check(plan.Domains.Count == 3 && plan.IpCidrs.Count == 3,
            "the engine still sees all six targets");

        // The mihomo rule list must still carry each domain under its own action.
        var yaml = MihomoConfigBuilder.BuildYaml(
            10808, false, ConnectionMethod.WireGuard, 1280,
            "se7en_tun", "198.18.0.1", 30, "fdfe:dcba:9876::1", 126,
            settings, "Ethernet", new[] { "aether" }, plan);
        Program.Check(yaml.Contains("DOMAIN-SUFFIX,direct.example,Se7enDirect", StringComparison.Ordinal),
            "Bypass domain emitted as Se7enDirect");
        Program.Check(yaml.Contains("DOMAIN-SUFFIX,proxied.example,Se7enProxy", StringComparison.Ordinal),
            "Through Tunnel domain emitted as Se7enProxy");
        Program.Check(yaml.Contains("DOMAIN-SUFFIX,blocked.example,REJECT", StringComparison.Ordinal),
            "Block domain emitted as REJECT");
        Program.Check(yaml.Contains("IP-CIDR,203.0.113.0/24,REJECT", StringComparison.Ordinal),
            "Block CIDR emitted as REJECT");

        Step("Moving a domain rule between modes follows the same store");
        Add(profile, "proxied.example", SplitRouteAction.Bypass, SplitTargetKind.Domain);
        plan = SplitRoutingPlanner.Build(settings);
        Program.Check(plan.BypassDomains.Contains("proxied.example")
                      && !plan.ProxyDomains.Contains("proxied.example"),
            "a moved domain becomes eligible for direct pinning only after the move");
    }

    private static void TestNoSharedState()
    {
        Step("Regression — independent lists, exactly one owner per target");
        var settings = NewSettings();
        var profile = CreateProfile(settings, "Shared", SplitOutbound.Proxy);

        var bypassList = profile.Bypass;
        var tunnelList = profile.Tunnel;
        var blockList = profile.Block;

        Add(profile, ChromePath, SplitRouteAction.Bypass);
        Add(profile, SteamPath, SplitRouteAction.Tunnel);

        Program.Check(ReferenceEquals(profile.Bypass, bypassList)
                      && ReferenceEquals(profile.Tunnel, tunnelList)
                      && ReferenceEquals(profile.Block, blockList),
            "adding rules never replaces the three lists");
        Program.Check(!ReferenceEquals(profile.Bypass, profile.Tunnel)
                      && !ReferenceEquals(profile.Tunnel, profile.Block)
                      && !ReferenceEquals(profile.Bypass, profile.Block),
            "the three modes never share a list instance");

        // Mutating one list directly (as a buggy call site could) must not touch the others.
        var tunnelBefore = string.Join("|", profile.Tunnel.Select(r => r.Value));
        profile.Bypass.Clear();
        Program.Check(string.Join("|", profile.Tunnel.Select(r => r.Value)) == tunnelBefore,
            "clearing Bypass leaves Through Tunnel untouched");

        Add(profile, ChromePath, SplitRouteAction.Bypass);
        Program.Check(profile.Bypass.Count == 1 && profile.Tunnel.Count == 1 && profile.Block.Count == 0,
            "each mode still holds only its own rules",
            $"bypass={profile.Bypass.Count} tunnel={profile.Tunnel.Count} block={profile.Block.Count}");

        var plan = SplitRoutingPlanner.Build(settings);
        Program.Check(CategoryOf(plan, "chrome.exe") == SplitRuleCategory.Direct
                      && CategoryOf(plan, "steam.exe") == SplitRuleCategory.Proxy,
            "the generated rules keep the two apps in different modes");

        // A rule object handed out by one list must never be aliased into another one.
        Program.Check(!profile.Bypass.Any(b => profile.Tunnel.Any(t => ReferenceEquals(b, t))),
            "no rule instance exists in two modes at once");
        Program.Check(profile.AllRules().Count() == 2, "the profile owns exactly two rules",
            $"count={profile.AllRules().Count()}");
    }

    private static void TestEdgeCases()
    {
        Step("Edge cases");
        var settings = NewSettings();

        // empty profile
        var empty = CreateProfile(settings, "Empty", SplitOutbound.Proxy);
        var emptyPlan = SplitRoutingPlanner.Build(settings);
        Program.Check(emptyPlan.Enabled && emptyPlan.Rules.Count == 0
                      && emptyPlan.DefaultCategory == SplitRuleCategory.Proxy,
            "an empty profile is valid and only carries its default");
        Program.Check(empty.Bypass.Count == 0 && empty.Tunnel.Count == 0 && empty.Block.Count == 0,
            "an empty profile has no rules in any mode");

        // duplicate names get unique labels
        var duplicate = CreateProfile(settings, "Empty", SplitOutbound.Direct);
        Program.Check(!string.Equals(duplicate.Name, empty.Name, StringComparison.Ordinal),
            "a duplicate profile name is made unique", duplicate.Name);

        // invalid input is rejected instead of stored
        var rejected = SplitTunnelProfileStore.SetApplication(empty, SplitTargetKind.Ip, "not-an-ip", SplitRouteAction.Bypass);
        Program.Check(rejected == SplitMoveResult.AlreadyThere && empty.Bypass.Count == 0,
            "an invalid IP is rejected");

        // normalisation of the same target written two ways
        Add(empty, "C:\\Tools\\app.exe", SplitRouteAction.Bypass);
        Add(empty, "c:\\tools\\APP.EXE", SplitRouteAction.Tunnel);
        Program.Check(CountOwned(empty, "C:\\Tools\\app.exe") == 1 && empty.Bypass.Count == 0 && empty.Tunnel.Count == 1,
            "a path differing only in case is treated as the same application",
            $"bypass={empty.Bypass.Count} tunnel={empty.Tunnel.Count}");

        // name-only rule vs path rule are distinct targets
        Add(empty, "tool.exe", SplitRouteAction.Block, SplitTargetKind.Process);
        Program.Check(empty.Block.Count == 1 && empty.Tunnel.Count == 1,
            "a name-only rule does not collide with a path rule");

        // clearing one mode leaves the others
        SplitTunnelProfileStore.Clear(empty, SplitRouteAction.Tunnel);
        Program.Check(empty.Tunnel.Count == 0 && empty.Block.Count == 1, "clearing Tunnel leaves Block alone");

        // persisted duplicate (hand-edited file) collapses deterministically: block wins
        var corrupted = new SplitTunnelProfile
        {
            Id = "pdup",
            Name = "Dup",
            DefaultOutbound = SplitOutbound.Proxy,
            Bypass = { new SplitTunnelRule { Kind = SplitTargetKind.Process, Value = "dup.exe" } },
            Tunnel = { new SplitTunnelRule { Kind = SplitTargetKind.Process, Value = "dup.exe" } },
            Block = { new SplitTunnelRule { Kind = SplitTargetKind.Process, Value = "dup.exe" } },
        };
        var dupSettings = new UserSettings
        {
            SplitTunnelEnabled = true,
            SplitTunnelProfiles = new List<SplitTunnelProfile> { corrupted },
            ActiveSplitTunnelProfileId = "pdup",
        };
        SplitTunnelProfileStore.NormalizeAll(dupSettings);
        var dupPlan = SplitRoutingPlanner.Build(dupSettings);
        Program.Check(corrupted.Block.Count == 1 && corrupted.Tunnel.Count == 0 && corrupted.Bypass.Count == 0,
            "a target persisted in three modes is kept once, in Block",
            $"bypass={corrupted.Bypass.Count} tunnel={corrupted.Tunnel.Count} block={corrupted.Block.Count}");
        Program.Check(CategoryOf(dupPlan, "dup.exe") == SplitRuleCategory.Block, "Block precedence wins the conflict");

        // uninstalled / renamed application keeps its rule but is simply never seen
        var stale = CreateProfile(settings, "Stale", SplitOutbound.Proxy);
        Add(stale, @"C:\Removed\gone.exe", SplitRouteAction.Block);
        var stalePlan = SplitRoutingPlanner.Build(settings);
        Program.Check(stalePlan.BlockedProcessPaths.Contains(@"C:\Removed\gone.exe"),
            "a rule for an uninstalled app is preserved and matched if the path ever appears again");
        Program.Check(SplitRoutingPlan.ResolveCategory(@"gone.exe", @"C:\Removed\gone.exe",
                stalePlan.DefaultCategory, stalePlan.Rules) == SplitRuleCategory.Block,
            "the block rule still resolves for that name");
        Program.Check(SplitRoutingPlan.ResolveCategory(@"gone.exe", null,
                stalePlan.DefaultCategory, stalePlan.Rules) == SplitRuleCategory.Block,
            "the block rule resolves even when only the image name is known");

        // rapid enable toggling does not corrupt anything
        for (var i = 0; i < 10; i++)
        {
            settings.SplitTunnelEnabled = i % 2 == 0;
            SplitTunnelProfileStore.Touch();
            _ = SplitRoutingPlanner.Build(settings);
        }
        settings.SplitTunnelEnabled = true;
        Program.Check(Snapshot(stale) == Snapshot(stale), "rapid toggling leaves the rules intact");
    }

    private static void TestMatchers()
    {
        Program.Section("Split tunnel — the seven matchers");

        var rule = new UserSettings();
        var profile = SplitTunnelProfileStore.CreateProfile(rule, "Matchers")!;

        // --- process path: exact, stable across renames
        SplitTunnelProfileStore.SetApplication(profile, SplitTargetKind.ProcessPath,
            @"C:\Program Files\Google\Chrome\Application\chrome.exe", SplitRouteAction.Block);
        var plan = SplitRoutingPlanner.Build(profile);
        Program.Check(
            SplitRoutingPlan.ResolveCategory("chrome.exe",
                @"C:\Program Files\Google\Chrome\Application\chrome.exe",
                plan.DefaultCategory, plan.Rules) == SplitRuleCategory.Block,
            "process path matches the exact image path");
        Program.Check(
            SplitRoutingPlan.ResolveCategory("chrome.exe",
                @"D:\Other\chrome.exe", plan.DefaultCategory, plan.Rules) == plan.DefaultCategory,
            "process path does not match a different image path");

        // --- process name: bare image name
        var p2 = SplitTunnelProfileStore.CreateProfile(rule, "Names")!;
        SplitTunnelProfileStore.SetApplication(p2, SplitTargetKind.Process, "steam.exe", SplitRouteAction.Tunnel);
        var plan2 = SplitRoutingPlanner.Build(p2);
        Program.Check(
            SplitRoutingPlan.ResolveCategory("steam.exe", @"X:\games\steam.exe",
                plan2.DefaultCategory, plan2.Rules) == SplitRuleCategory.Proxy,
            "process name matches regardless of folder");

        // --- keyword: partial name, the "I don't know the exe" case
        var p3 = SplitTunnelProfileStore.CreateProfile(rule, "Keyword")!;
        SplitTunnelProfileStore.SetApplication(p3, SplitTargetKind.Keyword, "chrom", SplitRouteAction.Bypass);
        var plan3 = SplitRoutingPlanner.Build(p3);
        Program.Check(
            SplitRoutingPlan.ResolveCategory("chrome.exe", @"C:\x\chrome.exe",
                plan3.DefaultCategory, plan3.Rules) == SplitRuleCategory.Direct,
            "keyword matches inside the image name");
        Program.Check(
            SplitRoutingPlan.ResolveCategory("msedge.exe", null,
                plan3.DefaultCategory, plan3.Rules) == plan3.DefaultCategory,
            "keyword does not match an unrelated image");

        // --- suffix
        var p4 = SplitTunnelProfileStore.CreateProfile(rule, "Suffix")!;
        SplitTunnelProfileStore.SetApplication(p4, SplitTargetKind.Suffix, "code.exe", SplitRouteAction.Block);
        var plan4 = SplitRoutingPlanner.Build(p4);
        Program.Check(
            SplitRoutingPlan.ResolveCategory("vscode.exe", null, plan4.DefaultCategory, plan4.Rules)
                == SplitRuleCategory.Block,
            "suffix matches the tail of the image stem");
        Program.Check(
            SplitRoutingPlan.ResolveCategory("codesign.exe", null, plan4.DefaultCategory, plan4.Rules)
                == plan4.DefaultCategory,
            "suffix is an anchored tail match, not a substring");

        // --- regex
        var p5 = SplitTunnelProfileStore.CreateProfile(rule, "Regex")!;
        SplitTunnelProfileStore.SetApplication(p5, SplitTargetKind.Regex, @"^(chrome|msedge)\.exe$", SplitRouteAction.Bypass);
        var plan5 = SplitRoutingPlanner.Build(p5);
        Program.Check(
            SplitRoutingPlan.ResolveCategory("msedge.exe", null, plan5.DefaultCategory, plan5.Rules)
                == SplitRuleCategory.Direct,
            "regex matches a browser image name");
        Program.Check(
            SplitRoutingPlan.ResolveCategory("explorer.exe", null, plan5.DefaultCategory, plan5.Rules)
                == plan5.DefaultCategory,
            "regex leaves a non-matching image alone");

        // --- domain and ip still work on the host side
        var p6 = SplitTunnelProfileStore.CreateProfile(rule, "Hosts")!;
        SplitTunnelProfileStore.SetApplication(p6, SplitTargetKind.Domain, "example.com", SplitRouteAction.Block);
        SplitTunnelProfileStore.SetApplication(p6, SplitTargetKind.Ip, "10.0.0.0/8", SplitRouteAction.Bypass);
        var plan6 = SplitRoutingPlanner.Build(p6);
        Program.Check(
            plan6.ResolveHost("cdn.example.com", null) == SplitRuleCategory.Block,
            "domain suffix covers subdomains");
        Program.Check(
            SplitRules.IpInCidr(System.Net.IPAddress.Parse("10.4.5.6"), "10.0.0.0/8"),
            "ip cidr contains an address in range");
        Program.Check(
            !SplitRules.IpInCidr(System.Net.IPAddress.Parse("11.0.0.1"), "10.0.0.0/8"),
            "ip cidr excludes an address outside it");

        // --- an invalid regex is rejected rather than stored
        var bad = SplitTunnelProfileStore.NormalizeValue(SplitTargetKind.Regex, "[unclosed");
        Program.Check(bad.Length == 0, "an invalid regex is rejected");

        // --- a typed path never turns into "path.exe"
        var pathVal = SplitTunnelProfileStore.NormalizeValue(SplitTargetKind.ProcessPath, @"C:\a\b.exe");
        Program.Check(pathVal == @"C:\a\b.exe", "an explicit path keeps its exact form");
        var nameVal = SplitTunnelProfileStore.NormalizeValue(SplitTargetKind.Process, @"C:\a\b.exe");
        Program.Check(nameVal == "b.exe", "a name rule strips the folder from a pasted path");
    }

    private static void TestDeleteAndNaming()
    {
        Program.Section("Split tunnel — delete from any mode, blank names");

        var settings = new UserSettings();
        var profile = SplitTunnelProfileStore.CreateProfile(settings, "Delete")!;

        // The reported bug: an app in Through Tunnel or Block could not be removed.
        SplitTunnelProfileStore.SetApplication(profile, SplitTargetKind.App, @"C:\a\tunnel.exe", SplitRouteAction.Tunnel);
        SplitTunnelProfileStore.SetApplication(profile, SplitTargetKind.App, @"C:\a\block.exe", SplitRouteAction.Block);
        SplitTunnelProfileStore.SetApplication(profile, SplitTargetKind.App, @"C:\a\direct.exe", SplitRouteAction.Bypass);

        Program.Check(SplitTunnelProfileStore.RemoveValue(profile, SplitTargetKind.App, @"C:\a\tunnel.exe"),
            "an app in Through Tunnel can be removed");
        Program.Check(SplitTunnelProfileStore.RemoveValue(profile, SplitTargetKind.App, @"C:\a\block.exe"),
            "an app in Block can be removed");
        Program.Check(SplitTunnelProfileStore.RemoveValue(profile, SplitTargetKind.App, @"C:\a\direct.exe"),
            "an app in Bypass can be removed");
        Program.Check(profile.RuleCount == 0, "all three modes end up empty");

        // Blank profile name is refused, not silently turned into "Profile".
        Program.Check(SplitTunnelProfileStore.CreateProfile(settings, "") is null,
            "a blank profile name is refused");
        Program.Check(SplitTunnelProfileStore.CreateProfile(settings, "   ") is null,
            "a whitespace-only profile name is refused");
        Program.Check(SplitTunnelProfileStore.CreateProfile(settings, "  Real  ") is not null,
            "a real name is still accepted");

        var before = profile.Name;
        Program.Check(!SplitTunnelProfileStore.Rename(settings, profile, ""),
            "a blank rename is refused");
        Program.Check(profile.Name == before, "the name is unchanged after a blank rename");
        Program.Check(!SplitTunnelProfileStore.Rename(settings, profile, "   "),
            "a whitespace-only rename is refused");
    }

    private static void TestProxyModeResolution()
    {
        Program.Section("Split tunnel — proxy mode resolves the same rules");

        // Proxy mode has no TUN adapter, so it feeds the same plan through
        // SplitRuleResolver. A path rule must come out verbatim, a name rule must go
        // through the name lookup, and Block must never be mistaken for Direct.
        var settings = new UserSettings();
        var profile = SplitTunnelProfileStore.CreateProfile(settings, "ProxyMode")!;

        SplitTunnelProfileStore.SetApplication(profile, SplitTargetKind.ProcessPath,
            @"C:\Apps\blocked.exe", SplitRouteAction.Block);
        SplitTunnelProfileStore.SetApplication(profile, SplitTargetKind.ProcessPath,
            @"C:\Apps\direct.exe", SplitRouteAction.Bypass);
        SplitTunnelProfileStore.SetApplication(profile, SplitTargetKind.Process,
            "byname.exe", SplitRouteAction.Block);
        SplitTunnelProfileStore.SetApplication(profile, SplitTargetKind.Process,
            "tunnelled.exe", SplitRouteAction.Tunnel);

        var plan = SplitRoutingPlanner.Build(profile);
        string? Lookup(string name) =>
            string.Equals(name, "byname.exe", StringComparison.OrdinalIgnoreCase)
                ? @"C:\Apps\byname.exe"
                : null;

        var blocked = SplitRuleResolver.ResolvePaths(plan, SplitRuleCategory.Block, Lookup);
        var direct = SplitRuleResolver.ResolvePaths(plan, SplitRuleCategory.Direct, Lookup);
        var proxy = SplitRuleResolver.ResolvePaths(plan, SplitRuleCategory.Proxy, Lookup);

        Program.Check(blocked.Contains(@"C:\Apps\blocked.exe"), "a Block path rule is resolved for WFP");
        Program.Check(blocked.Contains(@"C:\Apps\byname.exe"), "a Block name rule is resolved through the lookup");
        Program.Check(direct.Contains(@"C:\Apps\direct.exe"), "a Direct path rule lands in the proxy bypass set");
        Program.Check(!blocked.Contains(@"C:\Apps\direct.exe"), "Direct is never reported as Block");
        Program.Check(!direct.Contains(@"C:\Apps\blocked.exe"), "Block is never reported as Direct");
        Program.Check(proxy.Count == 0, "Through Tunnel installs nothing in proxy mode");

        // A disabled plan must resolve to nothing at all.
        var disabled = SplitRoutingPlan.Disabled;
        Program.Check(SplitRuleResolver.ResolvePaths(disabled, SplitRuleCategory.Block, Lookup).Count == 0,
            "a disabled plan resolves no Block paths");
        Program.Check(SplitRuleResolver.ResolvePaths(disabled, SplitRuleCategory.Direct, Lookup).Count == 0,
            "a disabled plan resolves no Direct paths");

        // A missing name must not throw and must not invent a path.
        Program.Check(SplitRuleResolver.ResolvePaths(plan, SplitRuleCategory.Block, _ => null)
                .All(p => !p.EndsWith("byname.exe", StringComparison.OrdinalIgnoreCase)),
            "an unresolvable name is skipped rather than guessed");
    }

    private static void TestEnforcementGaps()
    {
        Program.Section("Split tunnel — enforcement gaps found in audit");

        // (1) default = Block must produce a plan the enforcement layer can act on.
        var settings = new UserSettings();
        var p1 = SplitTunnelProfileStore.CreateProfile(settings, "DefaultBlock")!;
        p1.DefaultOutbound = SplitOutbound.Block;
        SplitTunnelProfileStore.SetApplication(p1, SplitTargetKind.Process, "allowed.exe", SplitRouteAction.Tunnel);

        var plan = SplitRoutingPlanner.Build(p1);
        Program.Check(plan.DefaultCategory == SplitRuleCategory.Block,
            "a Block default reaches the plan as Block");
        Program.Check(plan.ResolveHost("anything.test", null) == SplitRuleCategory.Block,
            "an unlisted host resolves to Block under a Block default");

        // The yaml must reject the fallthrough, not forward it.
        var yaml = MihomoConfigBuilder.BuildYaml(
            10808, false, ConnectionMethod.WireGuard, 1500, "Se7enTun",
            "198.18.0.1", 16, "fc00::1", 64, settings, "Ethernet",
            Array.Empty<string>(), plan);
        Program.Check(yaml.Contains("MATCH,REJECT"),
            "a Block default emits MATCH,REJECT");
        Program.Check(yaml.Contains("PROCESS-NAME,allowed.exe,Se7enProxy"),
            "the allowed app keeps its own Proxy rule ahead of MATCH");

        // (2) Block domain must be rejected BEFORE the port-53 redirect, otherwise a
        // DNS query for it is dialled through the proxy instead of being denied.
        var p2 = SplitTunnelProfileStore.CreateProfile(settings, "BlockDomain")!;
        SplitTunnelProfileStore.SetApplication(p2, SplitTargetKind.Domain, "blocked.test", SplitRouteAction.Block);
        var plan2 = SplitRoutingPlanner.Build(p2);
        var yaml2 = MihomoConfigBuilder.BuildYaml(
            10808, false, ConnectionMethod.WireGuard, 1500, "Se7enTun",
            "198.18.0.1", 16, "fc00::1", 64, settings, "Ethernet",
            Array.Empty<string>(), plan2);

        var blockIdx = yaml2.IndexOf("DOMAIN-SUFFIX,blocked.test,REJECT", StringComparison.Ordinal);
        var dnsIdx = yaml2.IndexOf("DST-PORT,53", StringComparison.Ordinal);
        Program.Check(blockIdx > 0, "a blocked domain is emitted as REJECT");
        Program.Check(dnsIdx > 0, "a DNS rule is emitted");
        Program.Check(blockIdx < dnsIdx,
            "the blocked domain is rejected BEFORE the DNS rule can dial it");

        // (3) Block IP ranges must reach the plan so WFP and the route table can use them.
        var p3 = SplitTunnelProfileStore.CreateProfile(settings, "BlockRange")!;
        SplitTunnelProfileStore.SetApplication(p3, SplitTargetKind.Ip, "203.0.113.0/24", SplitRouteAction.Block);
        var plan3 = SplitRoutingPlanner.Build(p3);
        Program.Check(plan3.BlockedIpCidrs.Contains("203.0.113.0/24"),
            "a Block CIDR reaches the plan's blocked range set");
        Program.Check(plan3.ResolveHost(null, System.Net.IPAddress.Parse("203.0.113.9"))
                == SplitRuleCategory.Block,
            "an address inside a Block CIDR resolves to Block");
        Program.Check(plan3.ResolveHost(null, System.Net.IPAddress.Parse("203.0.114.9"))
                == plan3.DefaultCategory,
            "an address outside the range is unaffected");

        // (4) default = Direct must still leave an explicit Proxy app on the proxy.
        var p4 = SplitTunnelProfileStore.CreateProfile(settings, "DefaultDirect")!;
        p4.DefaultOutbound = SplitOutbound.Direct;
        SplitTunnelProfileStore.SetApplication(p4, SplitTargetKind.Process, "proxied.exe", SplitRouteAction.Tunnel);
        var plan4 = SplitRoutingPlanner.Build(p4);
        Program.Check(plan4.DefaultCategory == SplitRuleCategory.Direct,
            "a Direct default is reflected in the plan");
        Program.Check(
            SplitRoutingPlan.ResolveCategory("proxied.exe", null, plan4.DefaultCategory, plan4.Rules)
                == SplitRuleCategory.Proxy,
            "an explicit Proxy app overrides a Direct default");
        Program.Check(
            SplitRoutingPlan.ResolveCategory("unlisted.exe", null, plan4.DefaultCategory, plan4.Rules)
                == SplitRuleCategory.Direct,
            "an unlisted app follows the Direct default");
    }

    // A profile at the rule cap must report Full, not AlreadyThere - the UI shows a
    // different message for each, and "Already in this mode." is a lie when the list
    // simply has no room left.
    private static void TestRuleCapReportsFull()
    {
        Program.Section("Split tunnel — the per-profile rule cap reports Full");
        var settings = NewSettings();
        var profile = CreateProfile(settings, "Cap", SplitOutbound.Proxy);

        var added = 0;
        for (var i = 0; i < SplitTunnelProfileStore.MaxRulesPerProfile; i++)
        {
            var result = SplitTunnelProfileStore.SetApplication(
                profile, SplitTargetKind.Process, "app" + i + ".exe", SplitRouteAction.Tunnel);
            if (result == SplitMoveResult.Added) added++;
        }

        Program.Check(added == SplitTunnelProfileStore.MaxRulesPerProfile,
            "the cap lets exactly MaxRulesPerProfile rules in");
        Program.Check(profile.RuleCount == SplitTunnelProfileStore.MaxRulesPerProfile,
            "the profile is at the cap");

        var overflow = SplitTunnelProfileStore.SetApplication(
            profile, SplitTargetKind.Process, "onemore.exe", SplitRouteAction.Tunnel);
        Program.Check(overflow == SplitMoveResult.Full,
            "one past the cap reports Full, not AlreadyThere");
        Program.Check(profile.RuleCount == SplitTunnelProfileStore.MaxRulesPerProfile,
            "the refused rule was not added");

        // A rule already present in the profile must still report AlreadyThere, so the
        // two conditions stay distinguishable.
        var duplicate = SplitTunnelProfileStore.SetApplication(
            profile, SplitTargetKind.Process, "app0.exe", SplitRouteAction.Tunnel);
        Program.Check(duplicate == SplitMoveResult.AlreadyThere,
            "a duplicate still reports AlreadyThere, distinct from Full");
    }

    private static void TestMigration()
    {
        Program.Section("Split tunnel — one-time migration of the old flat list");
        var legacyJson = """
        {
          "splitTunnelEnabled": true,
          "splitTunnelMode": "exclude",
          "splitTunnelEntries": [
            { "kind": "domain", "value": "example.com" },
            { "kind": "app", "value": "legacy.exe" }
          ]
        }
        """;

        Program.Check(SplitTunnelMigration.NeedsMigration(legacyJson), "the old payload is detected");

        var settings = new UserSettings();
        SplitTunnelMigration.Migrate(settings, legacyJson);
        Program.Check(settings.SplitTunnelProfiles.Count == 1, "one profile is created");
        var profile = settings.SplitTunnelProfiles[0];
        Program.Check(profile.Bypass.Count == 2, "legacy exclude-mode entries become Bypass rules");
        Program.Check(profile.DefaultOutbound == SplitOutbound.Proxy, "the legacy default is Proxy");
        Program.Check(string.Equals(settings.ActiveSplitTunnelProfileId, profile.Id, StringComparison.Ordinal),
            "the migrated profile becomes active");

        settings.SplitTunnelProfiles.Add(new SplitTunnelProfile { Id = "p2", Name = "Later" });
        Program.Check(!SplitTunnelMigration.Migrate(settings, legacyJson)
                      && settings.SplitTunnelProfiles.Count == 2,
            "migration never runs twice");

        Program.Check(!SplitTunnelMigration.Migrate(new UserSettings(), null) ,
            "an empty payload is not migrated");

        var includeJson = """
        { "splitTunnelEnabled": true, "splitTunnelMode": "include",
          "splitTunnelEntries": [ { "kind": "app", "value": "only.exe" } ] }
        """;
        var includeSettings = new UserSettings();
        SplitTunnelMigration.Migrate(includeSettings, includeJson);
        var includeProfile = includeSettings.SplitTunnelProfiles[0];
        Program.Check(includeProfile.Tunnel.Count == 1 && includeProfile.DefaultOutbound == SplitOutbound.Direct,
            "legacy include-mode entries become Through Tunnel rules with a Direct default");
    }

    // ----------------------------------------------------------------- helpers

    private static void Step(string title) => Console.WriteLine($"  -- {title}");

    private static UserSettings NewSettings() => new()
    {
        SplitTunnelEnabled = true,
    };

    private static SplitTunnelProfile CreateProfile(UserSettings settings, string name, string defaultOutbound)
    {
        var profile = SplitTunnelProfileStore.CreateProfile(settings, name);
        profile.DefaultOutbound = defaultOutbound;
        SplitTunnelProfileStore.SetActive(settings, profile.Id);
        SplitTunnelProfileStore.Touch();
        return profile;
    }

    private static SplitMoveResult Add(
        SplitTunnelProfile profile,
        string value,
        string action,
        string kind = SplitTargetKind.App) =>
        SplitTunnelProfileStore.SetApplication(profile, kind, value, action);

    private static bool SameValue(SplitTunnelRule rule, string value) =>
        string.Equals(
            SplitTunnelProfileStore.NormalizeValue(
                SplitTunnelProfileStore.NormalizeKindForMerge(rule.Kind, rule.Value), rule.Value),
            SplitTunnelProfileStore.NormalizeValue(
                SplitTunnelProfileStore.NormalizeKindForMerge(rule.Kind, value), value),
            StringComparison.OrdinalIgnoreCase);

    private static int CountOwned(SplitTunnelProfile profile, string value) =>
        profile.AllRules().Count(r => SameValue(r, value));

    // Emulates what the engine sees: the process image path (when the plan knows one for
    // that file name) plus the image name. Domain lookups pass no path.
    private static SplitRuleCategory CategoryOf(SplitRoutingPlan plan, string processName)
    {
        string? imagePath = null;
        if (processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            imagePath = plan.ProcessPaths
                .Concat(plan.BlockedProcessPaths)
                .FirstOrDefault(p => string.Equals(
                    SplitRoutingPlan.FileNameOf(p), processName, StringComparison.OrdinalIgnoreCase));
        }

        return SplitRoutingPlan.ResolveCategory(processName, imagePath, plan.DefaultCategory, plan.Rules);
    }

    private static string Snapshot(SplitTunnelProfile profile)
    {
        static string Join(IEnumerable<SplitTunnelRule> rules) =>
            string.Join(";", rules
                .Where(r => r is not null)
                .OrderBy(r => r.Kind, StringComparer.Ordinal)
                .ThenBy(r => r.Value, StringComparer.OrdinalIgnoreCase)
                .Select(r => $"{r.Kind}={r.Value}"));

        return $"{profile.Name}|{profile.DefaultOutbound}|B:{Join(profile.Bypass)}|T:{Join(profile.Tunnel)}|X:{Join(profile.Block)}";
    }
}
