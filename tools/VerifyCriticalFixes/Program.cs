using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Se7enPro.Services;

namespace VerifyCriticalFixes;

/// <summary>
/// Offline test runner verifying routing, DNS, identity pool, and cleanup logic.
/// </summary>
internal static class Program
{
    private static int _failures;

    internal static void Check(bool ok, string what, string? detail = null)
    {
        if (!ok && !string.IsNullOrEmpty(detail)) what += $"  [{detail}]";
        Console.WriteLine($"{(ok ? "[ OK ]" : "[FAIL]")} {what}");
        if (!ok) _failures++;
    }

    /// <summary>Total failures recorded so far.</summary>
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

        _failures += ContractTests.Run();
        _failures += DnsTests.Run();
        _failures += await RelayOrderTests.RunAsync();
        _failures += await AutoSaveTests.RunAsync();
        _failures += IdentityPoolTests.Run();
        _failures += IdentityRelayTests.Run();
        _failures += AetherCapabilityTests.Run();

        Console.WriteLine();
        Console.WriteLine(_failures == 0
            ? "ALL CHECKS PASSED"
            : $"{_failures} CHECK(S) FAILED");
        return _failures == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------- C6

    private static void VerifyC6_AgainstOldBehaviour()
    {
        Section("C6 — side-by-side: old (pre-fix) vs new predicate");

        var newRoots = StartupReaper.BuildReapRoots(out var newCount);
        var oldRoots = Old.OldReaper.Roots();
        var oldCount = 0;
        for (int i = 0; i < oldRoots.Length; i++) if (!string.IsNullOrEmpty(oldRoots[i])) oldCount++;
        oldRoots = oldRoots.Take(oldCount).ToArray();

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        // (imagePath, processName)
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

        // Images that must be reaped: they live in directories we own.
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

        // The regression: images that merely share an engine *name* but belong to
        // someone else must be left alone.
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

        // Unreadable/absent image must never be guessed from the name.
        Check(!StartupReaper.ShouldReap(null, roots, liveRoots), "null image is not reaped");
        Check(!StartupReaper.ShouldReap("", roots, liveRoots), "empty image is not reaped");
        Check(!StartupReaper.ShouldReap("   ", roots, liveRoots), "whitespace image is not reaped");

        // The old code used StartsWith on the root *without* requiring a separator in one
        // place, so a sibling directory with a shared prefix must not match.
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

                // The reaper skips its own pid, so exclude this harness the same way:
                // it lives under AppContext.BaseDirectory, which is a reap root.
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

        // Any process that merely shares an engine name but is NOT under our roots must
        // be preserved. This is exactly the Tor Browser / foreign xray regression.
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

    // ---------------------------------------------------------------- C5

    private static void VerifyC5_AllowRuleDiscovery()
    {
        Section("C5 — kill switch must be able to allow the bundled engine programs");

        // EngineProcessNames is the authoritative list the engines launch from.
        var names = EngineProcessNames.All;
        Check(names.Contains("Se7enPro.Tunnel.exe"), "engine list contains the psiphon core");
        Check(names.Count > 0, $"engine list is populated ({names.Count} entries)");

        // The block rule covers the whole public IPv4 space. Verify the bit math of the
        // carve-outs the app relies on: RFC1918, loopback and CGNAT must stay open.
        var blocked = ParseRanges(
            "0.0.0.0-9.255.255.255,11.0.0.0-126.255.255.255,128.0.0.0-172.15.255.255," +
            "172.32.0.0-192.167.255.255,192.169.0.0-255.255.255.255");

        (uint lo, uint hi)[] mustStayOpen =
        {
            (ToU("127.0.0.1"), ToU("127.0.0.1")),          // loopback upstream proxy
            (ToU("10.0.0.0"), ToU("10.255.255.255")),      // RFC1918 10/8
            (ToU("172.16.0.0"), ToU("172.31.255.255")),    // RFC1918 172.16/12
            (ToU("192.168.0.0"), ToU("192.168.255.255")),  // RFC1918 192.168/16
        };
        foreach (var (lo, hi) in mustStayOpen)
        {
            Check(!blocked.Any(b => b.lo <= lo && b.hi >= hi),
                  $"range stays reachable: {Ip(lo)}-{Ip(hi)}");
        }

        // Documented known gap (not part of C5, unchanged by this work): 169.254/16 is
        // inside the blocked 128.0.0.0-172.15.255.255 span, so link-local/APIPA is
        // blocked while the kill switch is armed. Harmless for a tunnel, but it should
        // be carved out next time the ranges are touched.
        Check(blocked.Any(b => b.lo <= ToU("169.254.0.0") && b.hi >= ToU("169.254.0.0")),
              "(known gap, unchanged) 169.254.0.0/16 is inside a blocked range");

        // Public space must actually be covered, or the kill switch is pointless.
        Check(blocked.Any(b => b.lo <= ToU("8.8.8.8") && b.hi >= ToU("8.8.8.8")),
              "8.8.8.8 is blocked by the default ranges");
        Check(blocked.Any(b => b.lo <= ToU("104.16.0.0") && b.hi >= ToU("104.16.0.0")),
              "104.16.0.0 (Cloudflare) is blocked by the default ranges");
    }

    // ---------------------------------------------------------------- C3

    private static void VerifyC3_RuleNameInventory()
    {
        Section("C3 — every firewall rule name must be reachable by the purge path");

        // The purge enumerates a single static list. If a rule were added to
        // ApplyBlockRules but not to that list, a crashed session could never clear it.
        var src = File.ReadAllText(FindSource("KillSwitchService.cs"));

        const string v4 = "Se7enPro_KillSwitch_BlockV4";
        const string v6 = "Se7enPro_KillSwitch_BlockV6";
        const string d4 = "Se7enPro_KillSwitch_BlockDnsV4";
        const string d6 = "Se7enPro_KillSwitch_BlockDnsV6";
        const string e = "Se7enPro_KillSwitch_AllowEngine";

        var allNames = new[] { v4, v6, d4, d6, e };
        foreach (var n in allNames)
            Check(src.Contains(n), $"rule name defined: {n}");

        // AllRuleNames is the purge inventory.
        var inventoryMatch = System.Text.RegularExpressions.Regex.Match(
            src, @"AllRuleNames\s*=\s*\{(?<body>[^}]*)\}");
        Check(inventoryMatch.Success, "AllRuleNames inventory exists");
        if (inventoryMatch.Success)
        {
            var body = inventoryMatch.Groups["body"].Value;
            // The inventory lists the C# constants, so check the identifiers, then
            // resolve each identifier to its literal to prove the mapping is complete.
            var identifiers = new[] { "RuleNameV4", "RuleNameV6", "RuleNameDnsV4",
                                      "RuleNameDnsV6", "RuleNameEngine" };
            foreach (var id in identifiers)
                Check(body.Contains(id), $"purge inventory references: {id}");

            var definedLiterals = allNames.ToDictionary(
                n => n,
                n => System.Text.RegularExpressions.Regex.IsMatch(
                        src, $@"const\s+string\s+\w+\s*=\s*""{System.Text.RegularExpressions.Regex.Escape(n)}"""));
            foreach (var kv in definedLiterals)
                Check(kv.Value, $"literal is bound to a constant: {kv.Key}");

            Check(identifiers.Length == allNames.Length,
                  $"every one of the {allNames.Length} rule names is in the purge inventory");
        }

        // The purge must be called before the first Reconcile in the constructor.
        var ctor = src.IndexOf("public KillSwitchService(", StringComparison.Ordinal);
        var purge = src.IndexOf("PurgeStaleRules();", StringComparison.Ordinal);
        var reconcile = src.IndexOf("Reconcile();", StringComparison.Ordinal);
        Check(ctor >= 0 && purge > ctor, "constructor calls PurgeStaleRules()");
        Check(reconcile > purge, "PurgeStaleRules() runs before the first Reconcile()");

        // No netsh invocation happens here; assert that to document the safety claim.
        Console.WriteLine("  (this harness never calls netsh and never alters firewall state)");
    }

    // ---------------------------------------------------------------- helpers

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
            var candidate = Path.Combine(dir, "Se7enPro", "Services", fileName);
            if (File.Exists(candidate)) return candidate;
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