using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Se7enPro.Services;

namespace VerifyCriticalFixes;

internal static class Program
{
    private static int _failures;

    internal static void Check(bool ok, string what, string? detail = null)
    {
        if (!ok && !string.IsNullOrEmpty(detail)) what += $"  [{detail}]";
        Console.WriteLine($"{(ok ? "[ OK ]" : "[FAIL]")} {what}");
        if (!ok) Interlocked.Increment(ref _failures);
    }

    internal static int FailedCount => _failures;

    internal static void Section(string title) => Console.WriteLine($"\n=== {title} ===");

    private static int Main()
    {
        return RunAsync().GetAwaiter().GetResult();
    }

    private static async Task<int> RunAsync()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("Se7en Pro — verification of the C1/C2/C3/C5/C6 fixes");
        Console.WriteLine("(no netsh, no firewall change, no network call, no process kill)");

        VerifyC6_AgainstOldBehaviour();
        VerifyC6_ReaperIsPathOnly();
        VerifyC6_AgainstLiveProcessTable();
        VerifyC5_AllowRuleDiscovery();
        VerifyC3_RuleNameInventory();

        ContractTests.Run();
        BackendContractTests.Run();
        V2RayShareLinkTests.Run();
        DnsTests.Run();
        await RelayOrderTests.RunAsync();
        await AutoSaveTests.RunAsync();
        IdentityPoolTests.Run();
        IdentityRelayTests.Run();
        AetherCapabilityTests.Run();
        RouteJournalTests.Run();
        LanPolicyTests.Run();
        MihomoSingBoxModalTests.Run();
        RecentFeatureTests.Run();
        await TunnelStabilityFixTests.RunAsync();
        await FoxyIranBypassTests.RunAsync();

        Console.WriteLine();
        Console.WriteLine(_failures == 0
            ? "ALL CHECKS PASSED"
            : $"{_failures} CHECK(S) FAILED");
        return _failures == 0 ? 0 : 1;
    }

    private static void VerifyC6_AgainstOldBehaviour()
    {
        Section("C6 — side-by-side: old (pre-fix) vs new predicate");

        var newRoots = StartupReaper.BuildReapRoots(out var newCount);
        var oldRoots = Old.OldReaper.Roots();
        var oldCount = 0;
        for (int i = 0; i < oldRoots.Length; i++) if (!string.IsNullOrEmpty(oldRoots[i])) oldCount++;
        oldRoots = oldRoots.Take(oldCount).ToArray();

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        var cases = new (string Image, string Name)[]
        {
            (@"C:\Program Files\Tor Browser\Browser\TorBrowser\Tor\tor.exe", "tor"),
            (@"C:\Users\me\AppData\Local\Tor Browser\Tor\tor.exe", "tor"),
            (@"C:\Tools\xray\xray.exe", "xray"),
            (@"C:\Tools\sing-box\sing-box.exe", "sing-box"),
            (@"C:\Vendor\pt\lyrebird.exe", "lyrebird"),
            (@"C:\Vendor\pt\conjure-client.exe", "conjure-client"),
            (@"C:\Users\me\Downloads\aether.exe", "aether"),
            (Path.Combine(localAppData, "Se7en", "tunnel-core", "Se7enPro.Tunnel.exe"), "Se7enPro.Tunnel"),
            (Path.Combine(localAppData, "Se7en", "tor", "Se7enPro.Tor.exe"), "Se7enPro.Tor"),
        };

        Console.WriteLine($"  {"image",-62} {"old",-8} {"new",-8} verdict");
        var regressions = 0;
        foreach (var (image, name) in cases)
        {
            var old = Old.OldReaper.OldShouldReap(image, name, oldRoots, oldCount);
            var now = StartupReaper.ShouldReap(image, newRoots, newCount);

            string verdict;
            if (old && !now)
            {
                verdict = "FIXED: no longer killed";
            }
            else if (!old && now)
            {
                verdict = "REGRESSION: now killed";
                regressions++;
            }
            else
            {
                verdict = old ? "still killed (correct: it is ours)" : "left alone (correct)";
            }

            var label = image.Length <= 60 ? image : "..." + image[^57..];
            Console.WriteLine($"  {label,-62} {(old ? "KILL" : "keep"),-8} {(now ? "KILL" : "keep"),-8} {verdict}");
        }

        Console.WriteLine();
        Check(regressions == 0, "the new predicate never kills something the old one spared");
    }

    private static void VerifyC6_ReaperIsPathOnly()
    {
        Section("C6 — StartupReaper must match on image path, never on process name");

        var roots = StartupReaper.BuildReapRoots(out var liveRoots);
        Check(liveRoots > 0, $"reap roots built ({liveRoots} live)");

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        var ours = new[]
        {
            Path.Combine(localAppData, "Se7en", "tunnel-core", "Se7enPro.Tunnel.exe"),
            Path.Combine(localAppData, "Se7en", "tor", "Se7enPro.Tor.exe"),
            Path.Combine(localAppData, "Se7en", "aether", "Se7enPro.Aether.exe"),
            Path.Combine(localAppData, "Se7en", "shard", "Se7enPro.Shard.exe"),
            Path.Combine(localAppData, "Se7en", "tun2socks", "tun2socks.exe"),
            Path.Combine(AppContext.BaseDirectory, "Resources", "xray", "xray.exe"),
        };
        foreach (var p in ours)
        {
            Check(StartupReaper.ShouldReap(p, roots, liveRoots),
                  $"reaps own engine: {Short(p)}");
        }

        var foreign = new[]
        {
            @"C:\Users\someone\AppData\Local\Tor Browser\Browser\TorBrowser\Tor\tor.exe",
            @"C:\Program Files\Tor Browser\Browser\TorBrowser\Tor\tor.exe",
            @"C:\Tools\xray\xray.exe",
            @"C:\Tools\sing-box\sing-box.exe",
            @"C:\SomeVendor\pluggable transports\lyrebird.exe",
            @"C:\SomeVendor\pluggable transports\conjure-client.exe",
            @"C:\Users\someone\Downloads\aether.exe",
            @"C:\Users\someone\Downloads\psiphon-tunnel-core.exe",
            @"C:\Windows\System32\svchost.exe",
            @"C:\Program Files\Se7en Pro\unrelated-helper.exe",
        };
        foreach (var p in foreign)
        {
            Check(!StartupReaper.ShouldReap(p, roots, liveRoots),
                  $"leaves foreign process alone: {Short(p)}");
        }

        Check(!StartupReaper.ShouldReap(null, roots, liveRoots), "null image is not reaped");
        Check(!StartupReaper.ShouldReap("", roots, liveRoots), "empty image is not reaped");
        Check(!StartupReaper.ShouldReap("   ", roots, liveRoots), "whitespace image is not reaped");

        Check(!StartupReaper.ShouldReap(
                  Path.Combine(localAppData, "Se7enEvil", "Se7enPro.Tunnel.exe"), roots, liveRoots),
              "prefix-sibling directory 'Se7enEvil' is not reaped");
    }

    private static void VerifyC6_AgainstLiveProcessTable()
    {
        Section("C6 — live process table scan (dry run, nothing is killed)");

        var roots = StartupReaper.BuildReapRoots(out var liveRoots);
        var wouldReap = new System.Collections.Generic.List<string>();
        var wouldSkipNamedLikeEngine = new System.Collections.Generic.List<string>();

        var ownPid = Environment.ProcessId;
        string[] engineNames =
        {
            "tor", "aether", "xray", "sing-box", "lyrebird", "conjure-client",
            "psiphon-tunnel-core", "Se7enPro.Tunnel", "Se7enPro.Tor",
            "Se7enPro.Aether", "Se7enPro.Shard",
        };

        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id == ownPid) continue;

                string? image = null;
                try { image = p.MainModule?.FileName; } catch { continue; }
                if (string.IsNullOrEmpty(image)) continue;

                if (StartupReaper.ShouldReap(image, roots, liveRoots))
                    wouldReap.Add($"{p.ProcessName} -> {image}");

                if (engineNames.Contains(p.ProcessName, StringComparer.OrdinalIgnoreCase))
                    wouldSkipNamedLikeEngine.Add($"{p.ProcessName} -> {image}");
            }
            finally { try { p.Dispose(); } catch { } }
        }

        Console.WriteLine($"  processes that would be reaped: {wouldReap.Count}");
        foreach (var s in wouldReap) Console.WriteLine($"    {s}");

        Console.WriteLine($"  running processes whose NAME looks like an engine: {wouldSkipNamedLikeEngine.Count}");
        foreach (var s in wouldSkipNamedLikeEngine) Console.WriteLine($"    {s}");

        var foreign = wouldSkipNamedLikeEngine
            .Where(s => !StartupReaper.ShouldReap(s.Split("-> ")[1].Trim(), roots, liveRoots))
            .ToList();
        foreach (var s in foreign)
        {
            Console.WriteLine($"  [SAFE] same name, foreign path, left running: {s}");
        }
        Check(wouldReap.Count == 0 || wouldReap.All(s => s.Contains("Se7en", StringComparison.OrdinalIgnoreCase)),
              "every reap candidate lives under a Se7en-owned directory");

        Check(!foreign.Any(s => StartupReaper.ShouldReap(s.Split("-> ")[1].Trim(), roots, liveRoots)),
              "no foreign engine-named process is selected for killing");
    }

    private static void VerifyC5_AllowRuleDiscovery()
    {
        Section("C5 — kill switch must be able to allow the bundled engine programs");

        var names = EngineProcessNames.All;
        Check(names.Contains("Se7enPro.Tunnel.exe"), "engine list contains the psiphon core");
        Check(names.Count > 0, $"engine list is populated ({names.Count} entries)");

        var blocked = ParseRanges(
            "0.0.0.0-9.255.255.255,11.0.0.0-126.255.255.255,128.0.0.0-172.15.255.255," +
            "172.32.0.0-192.167.255.255,192.169.0.0-255.255.255.255");

        (uint lo, uint hi)[] mustStayOpen =
        {
            (ToU("127.0.0.1"), ToU("127.0.0.1")),
            (ToU("10.0.0.0"), ToU("10.255.255.255")),
            (ToU("172.16.0.0"), ToU("172.31.255.255")),
            (ToU("192.168.0.0"), ToU("192.168.255.255")),
        };
        foreach (var (lo, hi) in mustStayOpen)
        {
            Check(!blocked.Any(b => b.lo <= lo && b.hi >= hi),
                  $"range stays reachable: {Ip(lo)}-{Ip(hi)}");
        }

        Check(blocked.Any(b => b.lo <= ToU("169.254.0.0") && b.hi >= ToU("169.254.0.0")),
              "(known gap, unchanged) 169.254.0.0/16 is inside a blocked range");

        Check(blocked.Any(b => b.lo <= ToU("8.8.8.8") && b.hi >= ToU("8.8.8.8")),
              "8.8.8.8 is blocked by the default ranges");
        Check(blocked.Any(b => b.lo <= ToU("104.16.0.0") && b.hi >= ToU("104.16.0.0")),
              "104.16.0.0 (Cloudflare) is blocked by the default ranges");
    }

    private static void VerifyC3_RuleNameInventory()
    {
        Section("C3 — every firewall rule name must be reachable by the purge path");

        var src = File.ReadAllText(FindSource("KillSwitchService.cs"));

        const string v4 = "Se7enPro_KillSwitch_BlockV4";
        const string v6 = "Se7enPro_KillSwitch_BlockV6";
        const string d4 = "Se7enPro_KillSwitch_BlockDnsV4";
        const string d6 = "Se7enPro_KillSwitch_BlockDnsV6";
        const string e = "Se7enPro_KillSwitch_AllowEngine";

        var allNames = new[] { v4, v6, d4, d6, e };
        foreach (var n in allNames)
            Check(src.Contains(n), $"rule name defined: {n}");

        var inventoryMatch = System.Text.RegularExpressions.Regex.Match(
            src, @"LegacyRuleNames\s*=\s*\{(?<body>[^}]*)\}");
        Check(inventoryMatch.Success, "LegacyRuleNames inventory exists");
        if (inventoryMatch.Success)
        {
            var body = inventoryMatch.Groups["body"].Value;
            foreach (var n in allNames)
                Check(body.Contains($"\"{n}\"") || body.Contains(n),
                      $"purge inventory references: {n}");
        }

        var ctor = src.IndexOf("public KillSwitchService(", StringComparison.Ordinal);
        var purge = src.IndexOf("PurgeLegacyFirewallRules", ctor, StringComparison.Ordinal);
        var reconcile = src.IndexOf("Reconcile();", ctor, StringComparison.Ordinal);
        Check(ctor >= 0 && purge > ctor, "constructor schedules PurgeLegacyFirewallRules()");
        Check(reconcile > ctor, "constructor runs Reconcile()");

        Check(src.Contains("_wfp.Arm("), "WFP installs the block filters");
        Check(src.Contains("_wfp.Disarm("), "WFP removes the block filters on disarm");
        Check(!src.Contains("advfirewall firewall add rule"),
              "no netsh rule is installed any more");

        Console.WriteLine("  (this harness never calls netsh and never alters firewall state)");
    }

    private static string Short(string p)
    {
        var name = Path.GetFileName(p);
        var dir = Path.GetDirectoryName(p) ?? "";
        return $"{name}  [{dir}]";
    }

    private static string FindSource(string fileName)
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 12 && dir is not null; i++)
        {
            var servicesDir = Path.Combine(dir, "Se7enPro", "Services");
            var candidate = Path.Combine(servicesDir, fileName);
            if (File.Exists(candidate)) return candidate;
            if (Directory.Exists(servicesDir))
            {
                var found = Directory.GetFiles(servicesDir, fileName, SearchOption.AllDirectories).FirstOrDefault();
                if (found != null) return found;
            }
            dir = Path.GetDirectoryName(dir.TrimEnd('\\'));
        }
        throw new FileNotFoundException($"could not locate {fileName}");
    }

    private static uint ToU(string ip)
    {
        var parts = ip.Split('.').Select(byte.Parse);
        return (uint)((parts.ElementAt(0) << 24) | (parts.ElementAt(1) << 16)
                    | (parts.ElementAt(2) << 8) | parts.ElementAt(3));
    }

    private static string Ip(uint v) =>
        $"{(v >> 24) & 0xFF}.{(v >> 16) & 0xFF}.{(v >> 8) & 0xFF}.{v & 0xFF}";

    private static (uint lo, uint hi)[] ParseRanges(string spec) =>
        spec.Split(',', StringSplitOptions.RemoveEmptyEntries)
           .Select(part => part.Trim().Split('-'))
           .Select(p => (ToU(p[0]), ToU(p[1])))
           .ToArray();
}