using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VerifyCriticalFixes;

/// <summary>
/// Source-level contract tests for C1 and C2.
///
/// These assert on the shipped source text rather than re-implementing the logic, so a
/// regression that reintroduces the unsafe behaviour fails here even if the code still
/// compiles. Nothing is executed, no process is started and no network call is made.
/// </summary>
internal static class ContractTests
{
    private static void Check(bool ok, string what) => Program.Check(ok, what);

    private static void Section(string title) => Program.Section(title);

    internal static int Run()
    {
        var failures = 0;

        Section("C1 — V2Ray must never build a direct (freedom) outbound");
        var v2ray = File.ReadAllText(FindSource("V2RayEngine.cs", "Se7enPro\\Services"));

        // A `freedom` outbound may legitimately appear in two places that are NOT the
        // whole path: the TLS-fragment helper outbound, and the final catch-all entry
        // that is added *after* a real proxy outbound exists. Those are fine. What must
        // not exist is a builder that RETURNS ONLY a direct outbound for a bad node.
        var sb = ExtractMethod(v2ray, "private static object[] BuildSingBoxOutbounds(", 800);
        Check(!RegexContains(sb, @"type\s*=\s*""direct"""),
              "BuildSingBoxOutbounds no longer returns a direct-only outbound list");
        Check(sb.Contains("ValidateOutboundNode(c)"),
              "BuildSingBoxOutbounds refuses a node with no address");
        Check(sb.Contains("throw new InvalidOperationException"),
              "BuildSingBoxOutbounds throws rather than returning a direct outbound");

        var fragmentOutbound = RegexContains(v2ray,
            @"tag = ""fragment"",\s*\r?\n\s*protocol = ""freedom""");
        Check(fragmentOutbound,
              "the TLS-fragment helper outbound is still present (expected: freedom + fragment)");

        var terminalDirect = CountOccurrences(v2ray,
            "outbounds.Add(new { tag = \"direct\", protocol = \"freedom\", settings = new { } });");
        Check(terminalDirect == 1,
              "exactly one trailing catch-all direct outbound remains for the proxy path");

        Check(v2ray.Contains("internal static string? ValidateOutboundNode"),
              "ValidateOutboundNode exists");
        Check(v2ray.Contains("ValidateOutboundNode(configProblem =") ||
              v2ray.Contains("var configProblem = ValidateOutboundNode(config);"),
              "StartAsync validates the node before launching the core");

        // The guard must set Error and return; it must not merely log.
        var guardBody = ExtractMethod(v2ray, "var configProblem = ValidateOutboundNode(config);", 900);
        Check(guardBody.Contains("SetState(ConnectionState.Error)"),
              "the guard reports ConnectionState.Error");
        Check(guardBody.Contains("return;"),
              "the guard returns without starting the core");

        // GenerateConfigFile must be defensive on its own too.
        var genBody = ExtractMethod(v2ray, "private string GenerateConfigFile(", 700);
        Check(genBody.Contains("ValidateOutboundNode(config)"),
              "GenerateConfigFile validates independently of StartAsync");
        Check(genBody.Contains("throw new InvalidOperationException"),
              "GenerateConfigFile throws instead of emitting a direct config");

        // The config builder must not degrade silently.
        var outbounds = ExtractMethod(v2ray, "private static object[] BuildXrayOutbounds(", 900);
        Check(outbounds.Contains("ValidateOutboundNode(c)"),
              "BuildXrayOutbounds refuses a node with no address");
        Check(outbounds.Contains("throw new InvalidOperationException"),
              "BuildXrayOutbounds throws rather than returning a direct outbound");

        // The live-test path must surface the reason instead of a bare -3.
        Check(v2ray.Contains("Cannot test this node:"),
              "the live node test reports the reason a node cannot be used");

        Section("C2b — ordering simulation: is the catch-all ever absent?");

        var scenarios = new (string Name,
                          Func<(List<RouteSimulation.Route>, List<RouteSimulation.Route>)> Build,
                          bool ExpectCatchAll)[]
        {
            ("full-tunnel, 0 dynamic routes", () => RouteSimulation.FullTunnelSet(0), true),
            ("full-tunnel, 40 dynamic routes", () => RouteSimulation.FullTunnelSet(40), true),
            ("full-tunnel, 500 dynamic routes", () => RouteSimulation.FullTunnelSet(500), true),
            ("include mode, 10 dynamic routes", () => RouteSimulation.IncludeModeSet(10), false),
        };

        // Routes.cs installs the catch-all pair only when !include, so include mode must
        // pass installCatchAll:false to both orderings.

        // asyncCost stands in for the work inside ApplyRoutesAsync that happens before the
        // catch-all pair is added: DNS lookups (3s cap each), the full TCP-table
        // sweep, and the two blocking Dns.GetHostAddresses calls.
        var asyncCost = TimeSpan.FromMilliseconds(3200);

        foreach (var (name, build, expectCatchAll) in scenarios)
        {
            var (appliedA, dynamicA) = build();
            var (appliedB, dynamicB) = build();

            var oldR = RouteSimulation.OldOrdering(appliedA, dynamicA, asyncCost, expectCatchAll);
            var newR = RouteSimulation.NewOrdering(appliedB, dynamicB, asyncCost, expectCatchAll);

            Console.WriteLine($"  {name}");
            Console.WriteLine($"      OLD ordering:\n      {oldR.Timeline}");
            Console.WriteLine($"      -> catch-all present at end: {YesNo(oldR.CatchAllPresentAtEnd)}, " +
                              $"leak window: {Fmt(oldR.LeakWindow)}");
            Console.WriteLine($"      NEW ordering:\n      {newR.Timeline}");
            Console.WriteLine($"      -> catch-all present at end: {YesNo(newR.CatchAllPresentAtEnd)}, " +
                              $"leak window: {Fmt(newR.LeakWindow)}");

            if (expectCatchAll)
            {
                Check(oldR.LeakWindow > TimeSpan.Zero,
                      $"[old] {name}: reproduces the leak window");
                Check(newR.CatchAllPresentAtEnd && newR.LeakWindow == TimeSpan.Zero,
                      $"[new] {name}: leak window fully eliminated");
                Check(newR.LeakWindow < oldR.LeakWindow,
                      $"[new] {name}: strictly shorter leak window than before");
            }
            else
            {
                // Include mode: the catch-all must stay absent in both orderings.
                Check(!newR.CatchAllPresentAtEnd,
                      $"[new] {name}: catch-all correctly not installed in include mode");
            }
        }

        Section("C2 — route re-apply must add before it deletes");
        var helpers = File.ReadAllText(FindSource("WintunTunManager.Helpers.cs", "Se7enPro\\Services\\Tun"));

        var applyIdx = helpers.IndexOf("await ApplyRoutesAsync(", StringComparison.Ordinal);
        var deleteIdx = helpers.IndexOf("WintunRouteApi.DeleteRoute(r)", StringComparison.Ordinal);

        Check(applyIdx >= 0, "ReapplyRoutesAsync still calls ApplyRoutesAsync");
        Check(deleteIdx >= 0, "ReapplyRoutesAsync still deletes stale routes");
        Check(applyIdx >= 0 && deleteIdx > applyIdx,
              "ApplyRoutesAsync runs BEFORE any DeleteRoute (no leak window)");

        // The old code cleared _appliedRoutes wholesale; that is what removed the
        // 0.0.0.0/1 + 128.0.0.0/1 catch-all pair before the new set existed.
        Check(!helpers.Contains("_appliedRoutes.RemoveAll("),
              "_appliedRoutes is no longer wiped wholesale before the re-apply");
        Check(!helpers.Contains("survivors"),
              "the 'survivors' concept that discarded the static routes is gone");

        Check(helpers.Contains("!_appliedRoutes.Contains(e)"),
              "only entries the re-apply did NOT restore are deleted");

        var routes = File.ReadAllText(FindSource("WintunTunManager.Routes.cs", "Se7enPro\\Services\\Tun"));
        Check(routes.Contains("previouslyApplied"),
              "ApplyRoutesAsync snapshots the previously applied set");
        Check(routes.Contains("retired"),
              "ApplyRoutesAsync retires statics the new config no longer needs");
        Check(routes.Contains("previouslyApplied.Count > 0"),
              "the retirement pass is skipped on the very first apply");

        // The catch-all pair must still be installed on every full-tunnel pass.
        Check(routes.Contains("IPAddress.Parse(\"0.0.0.0\"), 1, TunAddressV4"),
              "0.0.0.0/1 catch-all still installed");
        Check(routes.Contains("IPAddress.Parse(\"128.0.0.0\"), 1, TunAddressV4"),
              "128.0.0.0/1 catch-all still installed");

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "ALL CONTRACT CHECKS PASSED"
            : $"{failures} CONTRACT CHECK(S) FAILED");
        return failures;
    }

    // ---------------------------------------------------------------- helpers

    private static int CountOccurrences(string haystack, string needle)
    {
        int n = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }

    private static bool RegexContains(string s, string pattern) =>
        System.Text.RegularExpressions.Regex.IsMatch(s, pattern);

    /// <summary>Returns up to <paramref name="maxChars"/> of text starting at an anchor.</summary>
    private static string ExtractMethod(string src, string anchor, int maxChars)
    {
        var i = src.IndexOf(anchor, StringComparison.Ordinal);
        if (i < 0) return "";
        return src.Substring(i, Math.Min(maxChars, src.Length - i));
    }

    private static string YesNo(bool b) => b ? "yes" : "NO";

    private static string Fmt(TimeSpan t) =>
        t < TimeSpan.Zero ? "PERMANENT (apply failed)" : $"{t.TotalSeconds:0.00}s";

    private static string FindSource(string fileName, string relativeFolder)
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 12 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, relativeFolder, fileName);
            if (File.Exists(candidate)) return candidate;
            var alt = Path.Combine(dir, "Se7enPro",
                                   relativeFolder.Replace("Se7enPro\\", ""), fileName);
            if (File.Exists(alt)) return alt;
            dir = Path.GetDirectoryName(dir.TrimEnd('\\'));
        }
        throw new FileNotFoundException($"could not locate {fileName}");
    }
}