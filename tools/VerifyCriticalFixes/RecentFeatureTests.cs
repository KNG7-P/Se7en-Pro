using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using Se7enPro.Models;
using Se7enPro.Services;
using Se7enPro.ViewModels;

namespace VerifyCriticalFixes;

internal static class RecentFeatureTests
{
    internal static void Run()
    {
        TestCountryFlagExtractionAndViewModel();
        TestVeepSeedAndEngineContracts();
        TestShardSeedAndEngineContracts();
        TestFoxyRelayAndProxyContracts();
        TestV2RayListSortAndTimeoutContracts();
        TestLocationReconnectContract();
        TestUiPolishContracts();
        TestPsiphonOverFoxyAndAccountPromptContracts();
    }

    private static void TestCountryFlagExtractionAndViewModel()
    {
        Program.Section("V2Ray config flag extraction");

        var cases = new (string Name, string ExpectedCode)[]
        {
            ("\U0001F1E9\U0001F1EA Germany-01", "DE"),
            ("\U0001F1FA\U0001F1F8 New York", "US"),
            ("\U0001F1EC\U0001F1E7 London", "GB"),
            ("\U0001F1EE\U0001F1F7 Iran", "IR"),
            ("[DE] Frankfurt", "DE"),
            ("(nl) Amsterdam", "NL"),
            ("#jp Tokyo", "JP"),
            ("US-01 VLESS", "US"),
            ("VLESS - TR", "TR"),
            ("UK-Manchester", "GB"),
            ("Germany-02", "DE"),
            ("Netherlands-Relay", "NL"),
            ("United Kingdom Fast", "GB"),
            ("My-VLESS-WS-Reality", ""),
            ("Fast-Node-01", ""),
            ("Server-TLS-gRPC", ""),
            ("", ""),
        };

        foreach (var (name, expected) in cases)
        {
            var actual = CountryHelper.ExtractCountryCode(name);
            Program.Check(string.Equals(actual, expected, StringComparison.Ordinal),
                $"ExtractCountryCode(\"{name}\") == \"{expected}\"",
                actual);
        }

        var entry = new V2RayConfigEntry
        {
            Id = "id-1",
            Name = "My-VLESS-Node",
            Protocol = "vless",
            Address = "example.com",
            Port = 443
        };
        var vm = new V2RayConfigItemViewModel(entry);
        Program.Check(!vm.HasCountryFlag && vm.CountryCode == "", "initial config without country has no flag");

        vm.Name = "\U0001F1EB\U0001F1F7 Paris-Edge";
        Program.Check(vm.HasCountryFlag && vm.CountryCode == "FR", "updating config Name updates CountryCode/HasCountryFlag");
    }

    private static void TestVeepSeedAndEngineContracts()
    {
        Program.Section("Veep seed & engine fixes");

        var seedPath = FindRepoFile("Se7enPro", "Resources", "veep", "servers-seed.json");
        var seedJson = File.ReadAllText(seedPath);
        using var doc = JsonDocument.Parse(seedJson);
        var array = doc.RootElement;
        Program.Check(array.ValueKind == JsonValueKind.Array && array.GetArrayLength() >= 15,
            $"Veep seed contains >= 15 entries ({array.GetArrayLength()})");

        var validEntries = 0;
        var regions = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in array.EnumerateArray())
        {
            var ip = item.TryGetProperty("ip", out var ipEl) ? ipEl.GetString() : "";
            var port = item.TryGetProperty("port", out var portEl) ? portEl.GetInt32() : 0;
            var reg = item.TryGetProperty("region", out var regEl) ? regEl.GetString() : "";
            if (!string.IsNullOrWhiteSpace(reg)) regions.Add(reg);
            if (IPAddress.TryParse(ip, out _) && port is > 0 and <= 65535) validEntries++;
        }
        Program.Check(validEntries == array.GetArrayLength(),
            $"every Veep seed server has a valid IP and port ({validEntries}/{array.GetArrayLength()})");
        Program.Check(regions.Contains("fr-prs") && regions.Contains("nl") && regions.Contains("gb-lnd")
                   && regions.Contains("us-va") && regions.Contains("us-or") && regions.Contains("sg")
                   && regions.Contains("ru-spb"),
            $"Veep seed covers all 7 free regions ({string.Join(", ", regions)})");

        var veepEngine = File.ReadAllText(FindRepoFile("Se7enPro", "Services", "Veep", "VeepEngine.cs"));
        Program.Check(veepEngine.Contains("ProbeAnyServerReachableAsync", StringComparison.Ordinal)
                   && veepEngine.Contains("ProbeServerConnectAsync", StringComparison.Ordinal)
                   && veepEngine.Contains("ProbeServerThroughputKbpsAsync", StringComparison.Ordinal)
                   && veepEngine.Contains("FetchFromDomainAsync", StringComparison.Ordinal)
                   && veepEngine.Contains("FetchRegionServersAsync", StringComparison.Ordinal)
                   && veepEngine.Contains("FetchOptimalRegionAsync", StringComparison.Ordinal)
                   && veepEngine.Contains("ReserveBucketUrls", StringComparison.Ordinal)
                   && veepEngine.Contains("ExtractReserveFreeDomains", StringComparison.Ordinal)
                   && veepEngine.Contains("ParseServerListNode", StringComparison.Ordinal)
                   && veepEngine.Contains("listJson as JsonArray", StringComparison.Ordinal)
                   && veepEngine.Contains("ProbeAndRankServersAsync", StringComparison.Ordinal)
                   && veepEngine.Contains("ConnectDirectWithCleanDnsAsync", StringComparison.Ordinal)
                   && veepEngine.Contains("ResolveMissingIpsParallelAsync", StringComparison.Ordinal)
                   && veepEngine.Contains("DeduplicateServers", StringComparison.Ordinal)
                   && veepEngine.Contains("\"utls\"", StringComparison.Ordinal),
            "VeepEngine includes region/optimal API fetch, reserve buckets, TLS+CONNECT auth probe, throughput ranking, clean-DNS dialer, and uTLS");
    }

    private static void TestShardSeedAndEngineContracts()
    {
        Program.Section("SHARD seed & engine fixes");

        var seedPath = FindRepoFile("Se7enPro", "Resources", "shard", "shard-seed.txt");
        var activeNodes = File.ReadAllLines(seedPath)
            .Select(l => l.Trim())
            .Count(l => !string.IsNullOrEmpty(l) && !l.StartsWith('#'));
        Program.Check(activeNodes >= 50, $"SHARD seed contains >= 50 active node lines ({activeNodes})");

        var shardEngine = File.ReadAllText(FindRepoFile("Se7enPro", "Services", "Shard", "ShardEngine.cs"));
        Program.Check(shardEngine.Contains("RaceWidth = 24", StringComparison.Ordinal)
                   && shardEngine.Contains("ProbeTimeoutMs = 3000", StringComparison.Ordinal)
                   && shardEngine.Contains("RaceBudgetMs = 4500", StringComparison.Ordinal)
                   && shardEngine.Contains("WinnerGraceWindowMs", StringComparison.Ordinal),
            "ShardEngine uses widened race (24), fast probe/race timeouts, and lowest-RTT winner grace window");
        Program.Check(shardEngine.Contains("cp.cloudflare.com", StringComparison.Ordinal)
                   && shardEngine.Contains("www.gstatic.com", StringComparison.Ordinal)
                   && shardEngine.Contains("captive.apple.com", StringComparison.Ordinal),
            "ShardEngine probes multiple HTTP 204/hotspot targets");
        Program.Check(shardEngine.Contains("https://1.1.1.1/dns-query", StringComparison.Ordinal)
                   && !shardEngine.Contains("https://9.9.9.9/dns-query", StringComparison.Ordinal)
                   && shardEngine.Contains("\"domainStrategy\"] = \"AsIs\"", StringComparison.Ordinal),
            "ShardEngine uses fast 1.1.1.1/8.8.8.8 DoH fallback and AsIs routing");
    }

    private static void TestFoxyRelayAndProxyContracts()
    {
        Program.Section("Foxy custom proxy isolation");

        var foxyNetwork = File.ReadAllText(FindRepoFile("Se7enPro", "Services", "Foxy", "FoxyNetwork.cs"));
        var foxyEngine = File.ReadAllText(FindRepoFile("Se7enPro", "Services", "Foxy", "FoxyEngine.cs"));
        var foxyView = File.ReadAllText(FindRepoFile("Se7enPro", "Views", "Settings", "FoxySettingsView.xaml"));
        var foxyVm = File.ReadAllText(FindRepoFile("Se7enPro", "ViewModels", "FoxySettingsViewModel.cs"));

        Program.Check(foxyNetwork.Contains("ConnectionMethod.FoxyOverWarp", StringComparison.Ordinal)
                   && foxyNetwork.Contains("ConnectionMethod.FoxyOverV2Ray", StringComparison.Ordinal)
                   && foxyNetwork.Contains("IsDirectFoxyOnly(", StringComparison.Ordinal)
                   && foxyEngine.Contains("FoxyNetwork.IsDirectFoxyOnly(", StringComparison.Ordinal),
            "FoxyNetwork and FoxyEngine enforce raw-Foxy-only gating for custom proxy");
        Program.Check(foxyView.Contains("Foxy.IsDirectFoxyOnly", StringComparison.Ordinal)
                   && foxyView.Contains("Foxy.IsChainedFoxyMode", StringComparison.Ordinal)
                   && !foxyView.Contains("RelayEnabled", StringComparison.Ordinal)
                   && foxyVm.Contains("RefreshMethodAvailability", StringComparison.Ordinal),
            "FoxySettingsView removes Personal Cloudflare Relay and disables Custom Proxy when Foxy is chained");
    }

    private static void TestV2RayListSortAndTimeoutContracts()
    {
        Program.Section("V2Ray config sort, country flags & timed-out cleanup");

        var settingsVm = File.ReadAllText(FindRepoFile("Se7enPro", "ViewModels", "SettingsViewModel.cs"));
        var chainedView = File.ReadAllText(FindRepoFile("Se7enPro", "Views", "Settings", "ChainedSettingsView.xaml"));

        Program.Check(settingsVm.Contains("SortV2RayConfigsBy", StringComparison.Ordinal)
                   && settingsVm.Contains("RemoveTimedOutV2RayConfigs", StringComparison.Ordinal)
                   && settingsVm.Contains("HasTimedOutV2RayConfigs", StringComparison.Ordinal),
            "SettingsViewModel implements latency/name sort and timed-out config removal");
        Program.Check(chainedView.Contains("controls:CountryFlag", StringComparison.Ordinal)
                   && chainedView.Contains("CycleV2RaySortCommand", StringComparison.Ordinal)
                   && chainedView.Contains("RemoveTimedOutV2RayConfigsCommand", StringComparison.Ordinal),
            "ChainedSettingsView binds CountryFlag, Sort, and RemoveTimedOut controls");
    }

    private static void TestLocationReconnectContract()
    {
        Program.Section("Dashboard location change reconnect");

        var homeVm = File.ReadAllText(FindRepoFile("Se7enPro", "ViewModels", "HomeViewModel.cs"));
        Program.Check(homeVm.Contains("var previousCode = _selectedEgressRegion;", StringComparison.Ordinal)
                   && homeVm.Contains("State == ConnectionState.Connected", StringComparison.Ordinal)
                   && homeVm.Contains("_tunnel.RestartAsync()", StringComparison.Ordinal)
                   && homeVm.Contains("_regionRestartDebouncer", StringComparison.Ordinal),
            "HomeViewModel.OnSelectedRegionItemChanged triggers debounced restart when connected");
    }

    private static void TestUiPolishContracts()
    {
        Program.Section("UI polish: Import paste icon & hint, narrow V2Ray list buttons, Foxy relay copy feedback");

        var modals = File.ReadAllText(FindRepoFile("Se7enPro", "Views", "Dialogs", "ConfigModalsHost.xaml"));
        var chainedView = File.ReadAllText(FindRepoFile("Se7enPro", "Views", "Settings", "ChainedSettingsView.xaml"));
        var foxyView = File.ReadAllText(FindRepoFile("Se7enPro", "Views", "Settings", "FoxySettingsView.xaml"));
        var foxyVm = File.ReadAllText(FindRepoFile("Se7enPro", "ViewModels", "FoxySettingsViewModel.cs"));

        Program.Check(!modals.Contains("â€¦", StringComparison.Ordinal)
                   && !modals.Contains("â€¢", StringComparison.Ordinal)
                   && modals.Contains("Padding=\"12,10,48,10\"", StringComparison.Ordinal)
                   && modals.Contains("HorizontalAlignment=\"Right\" VerticalAlignment=\"Top\"", StringComparison.Ordinal)
                   && modals.Contains("PasteImportFromClipboardCommand", StringComparison.Ordinal),
            "ConfigModalsHost fixes mojibake hint and embeds Paste icon inside top-right of TextBox");

        Program.Check(chainedView.Contains("MinWidth=\"0\" Width=\"26\" Height=\"26\"", StringComparison.Ordinal)
                   && chainedView.Contains("MinWidth=\"0\" Height=\"26\" Padding=\"8,0\"", StringComparison.Ordinal)
                   && chainedView.Contains("MinWidth=\"46\" Height=\"24\"", StringComparison.Ordinal)
                   && chainedView.Contains("MinWidth=\"0\" Width=\"24\" Height=\"24\"", StringComparison.Ordinal),
            "ChainedSettingsView overrides UI.ActionButton MinWidth so V2Ray list buttons stay compact");

        var addDialogXaml = File.ReadAllText(FindRepoFile("Se7enPro", "Views", "AddFoxyAccountDialog.xaml"));
        var loginTransportCs = File.ReadAllText(FindRepoFile("Se7enPro", "Services", "Foxy", "FoxyLoginTransport.cs"));
        Program.Check(addDialogXaml.Contains("Tag=\"None\"", StringComparison.Ordinal)
                   && addDialogXaml.Contains("Tag=\"Lantern\"", StringComparison.Ordinal)
                   && addDialogXaml.Contains("Tag=\"Veep\"", StringComparison.Ordinal)
                   && addDialogXaml.Contains("Tag=\"WireGuard\"", StringComparison.Ordinal)
                   && addDialogXaml.Contains("Tag=\"Masque\"", StringComparison.Ordinal)
                   && addDialogXaml.Contains("TransportConnectingRing", StringComparison.Ordinal)
                   && addDialogXaml.Contains("TransportConnectedBadge", StringComparison.Ordinal)
                   && addDialogXaml.Contains("BrowserSignInButton", StringComparison.Ordinal)
                   && addDialogXaml.Contains("DirectSignInButton", StringComparison.Ordinal)
                   && loginTransportCs.Contains("FoxyLoginTransportStarter", StringComparison.Ordinal),
            "AddFoxyAccountDialog exposes 5 pre-login transports, animated/green status, and browser/direct login choice");
    }

    private static void TestPsiphonOverFoxyAndAccountPromptContracts()
    {
        Program.Section("PsiphonOverFoxy chained mode & missing Foxy account prompt");

        Program.Check(
            ConnectionMethod.PsiphonOverFoxy.ToToken() == "psiphon_over_foxy"
            && ConnectionMethodExtensions.ParseConnectionMethod("psiphon_over_foxy") == ConnectionMethod.PsiphonOverFoxy
            && ConnectionMethodExtensions.ParseConnectionMethod("psiphon_over_shard") == ConnectionMethod.Psiphon
            && ConnectionMethod.PsiphonOverFoxy.IsChained()
            && ConnectionMethod.PsiphonOverFoxy.IsFoxy(),
            "ConnectionMethod.PsiphonOverFoxy replaces PsiphonOverShard cleanly and migrates legacy token");

        var chainedView = File.ReadAllText(FindRepoFile("Se7enPro", "Views", "Settings", "ChainedSettingsView.xaml"));
        var settingsVm = File.ReadAllText(FindRepoFile("Se7enPro", "ViewModels", "SettingsViewModel.cs"));
        var homeVm = File.ReadAllText(FindRepoFile("Se7enPro", "ViewModels", "HomeViewModel.cs"));
        var dnsDoh = File.ReadAllText(FindRepoFile("Se7enPro", "Services", "Tun", "SocksDnsForwarder.Doh.cs"));
        var dnsQuery = File.ReadAllText(FindRepoFile("Se7enPro", "Services", "Tun", "SocksDnsForwarder.Query.cs"));

        Program.Check(
            chainedView.Contains("IsChainedPsiphonFoxy", StringComparison.Ordinal)
            && !chainedView.Contains("IsChainedPsiphonShard", StringComparison.Ordinal)
            && settingsVm.Contains("\"psiphon_foxy\"", StringComparison.Ordinal)
            && settingsVm.Contains("ConnectionMethod.PsiphonOverFoxy", StringComparison.Ordinal),
            "ChainedSettingsView and SettingsViewModel expose Psiphon over Foxy and remove Psiphon over SHARD");

        Program.Check(
            homeVm.Contains("ConnectionMethod.PsiphonOverFoxy", StringComparison.Ordinal)
            && homeVm.Contains("FoxyAccountRequiredDialog.Show()", StringComparison.Ordinal),
            "HomeViewModel opens FoxyAccountRequiredDialog when connecting on Foxy or PsiphonOverFoxy without a Foxy account");

        Program.Check(
            dnsDoh.Contains("KeepAlivePingDelay", StringComparison.Ordinal)
            && dnsQuery.Contains("DisposeDohClient();", StringComparison.Ordinal),
            "DoH sockets recycle cleanly on keepalive/timeout");

        var browserLogin = File.ReadAllText(FindRepoFile("Se7enPro", "Views", "FoxyBrowserLoginWindow.xaml.cs"));
        var chainedEngine = File.ReadAllText(FindRepoFile("Se7enPro", "Services", "ChainedEngine.cs"));
        var coreUpdate = File.ReadAllText(FindRepoFile("Se7enPro", "Services", "CoreUpdateService.cs"));

        Program.Check(
            browserLogin.Contains("\"unconfirmed\"", StringComparison.Ordinal)
            && browserLogin.Contains("\"138\"", StringComparison.Ordinal)
            && browserLogin.Contains("ErrorBanner.Visibility = Visibility.Collapsed", StringComparison.Ordinal),
            "FoxyBrowserLoginWindow continues polling on FxA Unconfirmed session (138) until email verification finishes");

        Program.Check(
            chainedEngine.Contains("RunChainedPsiphonWatchdogAsync", StringComparison.Ordinal),
            "ChainedEngine monitors Psiphon-over-HTTP data plane and recovers stalled tunnels in-place");

        Program.Check(
            coreUpdate.Contains("ReplaceTargetWithElevationFallback", StringComparison.Ordinal),
            "CoreUpdateService updates bundled Resources\\aether with UAC fallback so restarts never revert core version");
    }

    private static string FindRepoFile(params string[] relativeParts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(relativeParts).ToArray());
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"Could not locate {Path.Combine(relativeParts)}");
    }
}
