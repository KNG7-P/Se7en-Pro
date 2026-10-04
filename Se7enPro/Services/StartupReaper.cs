using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace Se7enPro.Services;

public sealed class StartupReaper : IStartupReaper
{
    private readonly ILogger<StartupReaper> _logger;

    public StartupReaper(ILogger<StartupReaper> logger) => _logger = logger;

    public void ReapStaleProcesses()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var normalisedRoots = BuildReapRoots(out var liveRoots);

        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Process.GetProcesses failed; skipping reaper");
            return;
        }

        var ownPid = -1;
        try { ownPid = Process.GetCurrentProcess().Id; } catch {  }

        var killed = 0;
        foreach (var p in processes)
        {
            try
            {
                if (p.Id == ownPid) continue;

                // Match on executable image path only
                string? imagePath;
                try
                {
                    imagePath = p.MainModule?.FileName;
                }
                catch
                {
                    continue;
                }

                if (string.IsNullOrEmpty(imagePath)) continue;
                if (!IsUnderAny(imagePath, normalisedRoots, liveRoots)) continue;

                _logger.LogInformation(
                    "Killing stale child pid {Pid} ({Image})",
                    p.Id,
                    imagePath);

                try
                {
                    p.Kill(entireProcessTree: true);
                    if (!p.WaitForExit(2000))
                    {
                        _logger.LogWarning(
                            "Stale child pid {Pid} did not exit within 2s",
                            p.Id);
                    }
                    else
                    {
                        killed++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to kill stale pid {Pid}", p.Id);
                }
            }
            finally
            {
                try { p.Dispose(); } catch { }
            }
        }

        TryRemoveStaleLocks(Path.Combine(localAppData, "Se7en", "tunnel-core"));
        TryRemoveStaleLocks(Path.Combine(localAppData, "Psiphon", "tunnel-core"));

        if (killed > 0)
        {
            _logger.LogInformation("Reaper killed {Count} stale child process(es)", killed);
        }
    }

    /// <summary>
    /// Builds the normalised set of directories whose executables belong to Se7en Pro.
    /// </summary>
    internal static string[] BuildReapRoots(out int liveRoots)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roots = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Resources"),
            Path.Combine(AppContext.BaseDirectory),
            Path.Combine(localAppData, "Se7en"),
            Path.Combine(localAppData, "Se7en", "tunnel-core"),
            Path.Combine(localAppData, "Se7en", "tun2socks"),
            Path.Combine(localAppData, "Se7en", "tor"),
            Path.Combine(localAppData, "Se7en", "aether"),
            Path.Combine(localAppData, "Se7en", "shard"),
            Path.Combine(localAppData, "Se7en", "xray"),
            Path.Combine(localAppData, "Se7en", "sing-box"),
            Path.Combine(localAppData, "Psiphon", "tunnel-core"),
            Path.Combine(localAppData, "Psiphon", "tun2socks"),
            Path.Combine(localAppData, "Psiphon", "singbox-tun"),
            Path.Combine(localAppData, "Psiphon", "xray-tun"),
            Path.Combine(Path.GetTempPath(), "Se7en"),
            Path.Combine(Path.GetTempPath(), "Psiphon"),
        };

        var normalisedRoots = new string[roots.Length];
        liveRoots = 0;
        for (int i = 0; i < roots.Length; i++)
        {
            var normalised = NormalisePath(roots[i]);
            if (string.IsNullOrEmpty(normalised)) continue;
            normalisedRoots[liveRoots++] = normalised;
        }

        return normalisedRoots;
    }

    /// <summary>Decides whether a process belongs to Se7en Pro based on image path.</summary>
    internal static bool ShouldReap(string? imagePath, string[] normalisedRoots, int liveRoots)
    {
        if (string.IsNullOrWhiteSpace(imagePath)) return false;
        return IsUnderAny(imagePath, normalisedRoots, liveRoots);
    }

    internal static bool IsUnderAny(string path, string[] normalisedRoots, int count)
    {

        var normalised = NormalisePath(path);
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

    private static string NormalisePath(string p)
    {
        try
        {
            return Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar);
        }
        catch
        {
            return p;
        }
    }

    private void TryRemoveStaleLocks(string root)
    {
        if (!Directory.Exists(root)) return;

        try
        {
            foreach (var lockFile in Directory.EnumerateFiles(
                         root, "*.lock", SearchOption.AllDirectories))
            {
                try
                {
                    File.Delete(lockFile);
                    _logger.LogInformation("Removed stale lock {Path}", lockFile);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not remove stale lock {Path}", lockFile);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Enumerating stale locks under {Root} failed", root);
        }
    }
}
