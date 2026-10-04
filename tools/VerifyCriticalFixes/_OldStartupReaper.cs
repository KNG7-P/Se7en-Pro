// Snapshot of the PRE-FIX StartupReaper decision logic (git HEAD), kept only so the
// verification harness can demonstrate the regression it had. Not part of the app.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace VerifyCriticalFixes.Old;

internal static class OldReaper
{
    internal static readonly string[] EngineNames =
    {
        "Se7enPro.Tunnel.exe", "Se7enPro.Aether.exe", "Se7enPro.Tor.exe",
        "Se7enPro.Shard.exe", "xray.exe", "sing-box.exe", "sing-box",
        "Se7enPro.Shard", "lyrebird.exe", "conjure-client.exe",
    };

    internal static string[] Roots()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roots = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Resources"),
            Path.Combine(localAppData, "Se7en", "tunnel-core"),
            Path.Combine(localAppData, "Se7en", "tun2socks"),
            Path.Combine(localAppData, "Se7en", "tor"),
            Path.Combine(localAppData, "Se7en", "aether"),
            Path.Combine(localAppData, "Se7en", "shard"),
            Path.Combine(localAppData, "Psiphon", "tunnel-core"),
            Path.Combine(localAppData, "Psiphon", "tun2socks"),
            Path.Combine(localAppData, "Psiphon", "singbox-tun"),
            Path.Combine(localAppData, "Psiphon", "xray-tun"),
            Path.Combine(Path.GetTempPath(), "Se7en"),
            Path.Combine(Path.GetTempPath(), "Psiphon"),
        };

        var normalisedRoots = new string[roots.Length];
        var liveRoots = 0;
        for (int i = 0; i < roots.Length; i++)
        {
            var normalised = Normalise(roots[i]);
            if (string.IsNullOrEmpty(normalised)) continue;
            normalisedRoots[liveRoots++] = normalised;
        }
        return normalisedRoots;
    }

    internal static int LiveRoots()
    {
        var n = 0;
        foreach (var r in Roots()) if (!string.IsNullOrEmpty(r)) n++;
        return 0; // Roots() is already normalised; count used below is derived separately.
    }

    /// <summary>The exact predicate from the pre-fix code (StartupReaper.cs:76-117).</summary>
    internal static bool OldShouldReap(string? imagePath, string? processName, string[] normalisedRoots, int count)
    {
        bool isKnownEngine =
            EngineNames.Any(n =>
                string.Equals(Path.GetFileNameWithoutExtension(n), processName, StringComparison.OrdinalIgnoreCase))
            || string.Equals(processName, "aether", StringComparison.OrdinalIgnoreCase)
            || string.Equals(processName, "tor", StringComparison.OrdinalIgnoreCase)
            || string.Equals(processName, "psiphon-tunnel-core", StringComparison.OrdinalIgnoreCase);

        if (string.IsNullOrEmpty(imagePath))
        {
            // Pre-fix: unreadable image still killed by name.
            return isKnownEngine;
        }

        if (!IsUnderAny(imagePath, normalisedRoots, count) && !isKnownEngine) return false;
        return true;
    }

    private static bool IsUnderAny(string path, string[] normalisedRoots, int count)
    {
        var normalised = Normalise(path);
        for (var i = 0; i < count; i++)
        {
            var nroot = normalisedRoots[i];
            if (normalised.StartsWith(nroot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || normalised.Equals(nroot, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static string Normalise(string p)
    {
        try { return Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar); }
        catch { return p; }
    }
}