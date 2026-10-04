using System;
using System.Collections.Generic;
using System.Linq;

namespace VerifyCriticalFixes;

/// <summary>
/// Simulates route table transitions to verify leak-free reapplication.
/// </summary>
internal static class RouteSimulation
{
    /// <summary>A route key, mirroring WintunRouteApi.RouteEntry equality.</summary>
    internal readonly record struct Route(int IfIndex, string Destination, byte Prefix, string NextHop);

    private const int TunIfIndex = 42;
    private const string TunNextHop = "198.18.0.1";

    internal static Route CatchAllLow => new(TunIfIndex, "0.0.0.0", 1, TunNextHop);
    internal static Route CatchAllHigh => new(TunIfIndex, "128.0.0.0", 1, TunNextHop);

    internal sealed record Outcome(
        bool CatchAllPresentAtEnd,
        TimeSpan LeakWindow,
        string Timeline);

    private static bool HasCatchAll(IEnumerable<Route> table) =>
        table.Contains(CatchAllLow) && table.Contains(CatchAllHigh);

    /// <summary>
    /// Simulates pre-fix ordering: delete old routes prior to installing new routes.
    /// </summary>
    internal static Outcome OldOrdering(
        List<Route> appliedRoutes,
        List<Route> dynamicRoutes,
        TimeSpan asyncInsideApply,
        bool installCatchAll = true)
    {
        var table = new HashSet<Route>(appliedRoutes);
        var timeline = new List<string>
        {
            $"start: catch-all present = {HasCatchAll(table)} ({appliedRoutes.Count} routes)",
        };

        // Step 1+2: survivors holds dynamic entries only.
        var survivors = new HashSet<Route>(dynamicRoutes);
        var doomed = table.Where(e => !survivors.Contains(e)).ToList();
        timeline.Add($"compute doomed: {doomed.Count} route(s) (static routes are never survivors)");

        // Step 3: delete.
        foreach (var r in doomed) table.Remove(r);
        timeline.Add($"after delete: catch-all present = {HasCatchAll(table)} " +
                     (installCatchAll ? "<-- traffic now leaves via the real NIC" : "(include mode: none expected)"));

        // Step 4: the await window, then the catch-all is re-added last — but only when
        // the mode is full/exclude tunnel, exactly as Routes.cs does.
        timeline.Add($"await inside ApplyRoutesAsync ({asyncInsideApply.TotalSeconds:0.00}s): " +
                     $"catch-all present = {HasCatchAll(table)}");
        if (installCatchAll)
        {
            table.Add(CatchAllLow);
            table.Add(CatchAllHigh);
        }
        timeline.Add($"end of apply: catch-all present = {HasCatchAll(table)}");

        var present = HasCatchAll(table);
        if (!installCatchAll)
        {
            return new Outcome(present, TimeSpan.Zero, string.Join("\n      ", timeline));
        }

        // If the apply throws before the catch-all is added, it stays gone forever.
        var leakWindow = present ? asyncInsideApply : TimeSpan.FromMilliseconds(-1);
        return new Outcome(present, leakWindow, string.Join("\n      ", timeline));
    }

    /// <summary>
    /// Simulates post-fix ordering: re-apply new routes while existing routes remain active, then prune stale routes.
    /// </summary>
    internal static Outcome NewOrdering(
        List<Route> appliedRoutes,
        List<Route> dynamicRoutes,
        TimeSpan asyncInsideApply,
        bool installCatchAll = true)
    {
        var table = new HashSet<Route>(appliedRoutes);
        var timeline = new List<string>
        {
            $"start: catch-all present = {HasCatchAll(table)} ({appliedRoutes.Count} routes)",
        };

        // Step 1: dynamic routes that no longer match. Statics are not considered.
        // In the real code this compares against the current split rules; for the
        // simulation every dynamic route is treated as no longer matching.
        var doomedDynamic = new List<Route>(dynamicRoutes);
        timeline.Add($"compute doomed dynamic: {doomedDynamic.Count} route(s) (statics untouched)");

        // Step 2: apply first. The catch-all is already installed, so the apply's
        // await window cannot expose a gap.
        timeline.Add($"await inside ApplyRoutesAsync ({asyncInsideApply.TotalSeconds:0.00}s): " +
                     $"catch-all present = {HasCatchAll(table)} " +
                     (installCatchAll ? "<-- never absent, so no leak" : "(include mode: none expected)"));
        if (installCatchAll)
        {
            table.Add(CatchAllLow);
            table.Add(CatchAllHigh);
        }

        // Step 3: delete only what the apply did not put back.
        var removed = 0;
        foreach (var r in doomedDynamic.Where(d => !table.Contains(d)))
        {
            table.Remove(r);
            removed++;
        }
        timeline.Add($"after retiring {removed} stale route(s): catch-all present = {HasCatchAll(table)}");

        var present = HasCatchAll(table);
        return new Outcome(present, TimeSpan.Zero, string.Join("\n      ", timeline));
    }

    /// <summary>Builds a realistic full-tunnel route set.</summary>
    internal static (List<Route> applied, List<Route> dynamic) FullTunnelSet(int dynamicCount)
    {
        var applied = new List<Route>
        {
            CatchAllLow,
            CatchAllHigh,
            new(1, "10.0.0.0", 8, "192.168.1.1"),
            new(1, "172.16.0.0", 12, "192.168.1.1"),
            new(1, "192.168.0.0", 16, "192.168.1.1"),
            new(1, "104.16.0.0", 12, "192.168.1.1"),
            new(1, "172.64.0.0", 13, "192.168.1.1"),
        };

        var dynamic = new List<Route>();
        for (int i = 0; i < dynamicCount; i++)
            dynamic.Add(new(1, $"203.0.113.{i % 250 + 1}", 32, "192.168.1.1"));

        applied.AddRange(dynamic);
        return (applied, dynamic);
    }

    /// <summary>
    /// Include mode: no catch-all by design — only the listed routes are tunnelled.
    /// The catch-all must NOT appear, in either ordering.
    /// </summary>
    internal static (List<Route> applied, List<Route> dynamic) IncludeModeSet(int dynamicCount)
    {
        var applied = new List<Route>();
        var dynamic = new List<Route>();
        for (int i = 0; i < dynamicCount; i++)
            dynamic.Add(new(TunIfIndex, $"198.51.100.{i % 250 + 1}", 32, TunNextHop));
        applied.AddRange(dynamic);
        return (applied, dynamic);
    }
}