using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VerifyCriticalFixes;

internal static class ContractTests
{
    private static void Check(bool ok, string what) => Program.Check(ok, what);

    private static void Section(string title) => Program.Section(title);

    internal static int Run()
    {
        var failures = 0;

        Section("C1 — V2Ray must never build a direct (freedom) outbound");
        var v2ray = File.ReadAllText(FindSource("V2RayEngine.cs", "Se7enPro\\Services"));
        var v2rayBuilder = File.ReadAllText(FindSource("V2RayConfigBuilder.cs", "Se7enPro\\Services"));

        var sb = ExtractMethod(v2rayBuilder, "internal static object[] BuildSingBoxOutbounds(", 800);
        Check(!RegexContains(sb, @"type\s*=\s*""direct"""),
              "BuildSingBoxOutbounds no longer returns a direct-only outbound list");
        Check(sb.Contains("ValidateOutboundNode(c)"),
              "BuildSingBoxOutbounds refuses a node with no address");
        Check(sb.Contains("throw new InvalidOperationException"),
              "BuildSingBoxOutbounds throws rather than returning a direct outbound");

        var fragmentOutbound = RegexContains(v2rayBuilder,
            @"tag = ""fragment"",\s*\r?\n\s*protocol = ""freedom""");
        Check(fragmentOutbound,
              "the TLS-fragment helper outbound is still present (expected: freedom + fragment)");

        var terminalDirect = CountOccurrences(v2rayBuilder,
            "outbounds.Add(new { tag = \"direct\", protocol = \"freedom\", settings = new { } });");
        Check(terminalDirect == 1,
              "exactly one trailing catch-all direct outbound remains for the proxy path");

        Check(v2ray.Contains("internal static string? ValidateOutboundNode"),
              "ValidateOutboundNode exists");
        Check(v2ray.Contains("ValidateOutboundNode(configProblem =") ||
              v2ray.Contains("var configProblem = ValidateOutboundNode(config);"),
              "StartAsync validates the node before launching the core");

        var guardBody = ExtractMethod(v2ray, "var configProblem = ValidateOutboundNode(config);", 900);
        Check(guardBody.Contains("SetState(ConnectionState.Error)"),
              "the guard reports ConnectionState.Error");
        Check(guardBody.Contains("return;"),
              "the guard returns without starting the core");

        var genBody = ExtractMethod(v2ray, "private string GenerateConfigFile(", 700);
        Check(genBody.Contains("ValidateOutboundNode(config)"),
              "GenerateConfigFile validates independently of StartAsync");
        Check(genBody.Contains("throw new InvalidOperationException"),
              "GenerateConfigFile throws instead of emitting a direct config");

        var outbounds = ExtractMethod(v2rayBuilder, "internal static object[] BuildXrayOutbounds(", 900);
        Check(outbounds.Contains("ValidateOutboundNode(c)"),
              "BuildXrayOutbounds refuses a node with no address");
        Check(outbounds.Contains("throw new InvalidOperationException"),
              "BuildXrayOutbounds throws rather than returning a direct outbound");

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

        var catchAllA = routes.IndexOf("ThroughTun(IPAddress.Parse(\"0.0.0.0\"), 1)", StringComparison.Ordinal);
        var catchAllB = routes.IndexOf("ThroughTun(IPAddress.Parse(\"128.0.0.0\"), 1)", StringComparison.Ordinal);
        Check(catchAllA >= 0, "0.0.0.0/1 catch-all still installed");
        Check(catchAllB >= 0, "128.0.0.0/1 catch-all still installed");

        var domainLookup = catchAllA >= 0
            ? routes.IndexOf("Dns.GetHostAddressesAsync(", catchAllA, StringComparison.Ordinal)
            : -1;
        Check(catchAllA >= 0 && domainLookup > catchAllA && domainLookup > catchAllB,
              "catch-all is installed before split-domain DNS lookups run");

        Section("UI regressions — clipboard, log flushing and split-tunnel warning");
        var ui = File.ReadAllText(FindSource("IUiAdapter.cs", "Se7enPro\\Services"));
        var home = File.ReadAllText(FindSource("HomeViewModel.cs", "Se7enPro\\ViewModels"));
        Check(ui.Contains("public static bool SetClipboard(string text)", StringComparison.Ordinal),
              "clipboard reports whether the platform write succeeded");
        Check(home.Contains("return Ui.SetClipboard(text);", StringComparison.Ordinal),
              "Home only shows a copied checkmark after a successful clipboard write");

        var logs = File.ReadAllText(FindSource("LogsViewModel.cs", "Se7enPro\\ViewModels"));
        Check(RegexContains(logs, @"_flushTimer\.Stop\(\);\s*if \(_pending\.IsEmpty\) return;"),
              "log flushing rechecks the queue after stopping the timer");

        var splitPage = File.ReadAllText(FindSource("SplitTunnelPage.xaml", "Se7enPro\\Views"));
        Check(splitPage.Contains("Text=\"{Binding StatusText}\"", StringComparison.Ordinal),
              "the split-tunnel status strip explains the actual admin or disabled state");
        Check(splitPage.Contains("Command=\"{Binding RestartAsAdminCommand}\"", StringComparison.Ordinal)
              && splitPage.Contains("Visibility=\"{Binding IsAdminElevated, Converter={StaticResource InvBoolToVis}}\"", StringComparison.Ordinal),
              "the restart-as-admin action is hidden from an already elevated user");
        Check(splitPage.Contains("Command=\"{Binding SelectModeCommand}\" CommandParameter=\"bypass\"", StringComparison.Ordinal)
              && splitPage.Contains("Command=\"{Binding SelectModeCommand}\" CommandParameter=\"tunnel\"", StringComparison.Ordinal)
              && splitPage.Contains("Command=\"{Binding SelectModeCommand}\" CommandParameter=\"block\"", StringComparison.Ordinal),
              "the page exposes the three independent routing modes");
        Check(splitPage.Contains("Command=\"{Binding SetDefaultOutboundCommand}", StringComparison.Ordinal),
              "the page exposes the per-profile default outbound");
        Check(splitPage.Contains("Command=\"{Binding DataContext.AssignAppCommand, RelativeSource={RelativeSource AncestorType=ListBox}}\"", StringComparison.Ordinal)
              && splitPage.Contains("Command=\"{Binding DataContext.RemoveAppAssignmentCommand, RelativeSource={RelativeSource AncestorType=ListBox}}\"", StringComparison.Ordinal),
              "the application list can assign and unassign an app");
        Check(splitPage.Contains("ItemTemplate=\"{StaticResource RuleRowTemplate}\"", StringComparison.Ordinal),
              "every mode renders its rules from one shared row template");

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "ALL CONTRACT CHECKS PASSED"
            : $"{failures} CONTRACT CHECK(S) FAILED");
        return failures;
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int n = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }

    private static bool RegexContains(string s, string pattern) =>
        System.Text.RegularExpressions.Regex.IsMatch(s, pattern);

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
            var targetFolder = Path.Combine(dir, relativeFolder);
            if (Directory.Exists(targetFolder))
            {
                var found = Directory.GetFiles(targetFolder, fileName, SearchOption.AllDirectories).FirstOrDefault();
                if (found != null) return found;
            }
            var alt = Path.Combine(dir, "Se7enPro",
                                   relativeFolder.Replace("Se7enPro\\", ""), fileName);
            if (File.Exists(alt)) return alt;
            var altFolder = Path.Combine(dir, "Se7enPro", relativeFolder.Replace("Se7enPro\\", ""));
            if (Directory.Exists(altFolder))
            {
                var foundAlt = Directory.GetFiles(altFolder, fileName, SearchOption.AllDirectories).FirstOrDefault();
                if (foundAlt != null) return foundAlt;
            }
            dir = Path.GetDirectoryName(dir.TrimEnd('\\'));
        }
        throw new FileNotFoundException($"could not locate {fileName}");
    }
}