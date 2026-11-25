using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace Se7enPro.Services;

public sealed class StartupReaper : IStartupReaper
{
    private static readonly HashSet<string> EngineExecutableNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "psiphon-tunnel-core.exe",
        "Se7enPro.Tunnel.exe",
        "tun2socks.exe",
        "Se7enPro.Tun.exe",
        "zeptun.exe",
        "Se7enPro.Zeptun.exe",
        "mihomo.exe",
        "Se7enPro.Mihomo.exe",
        "tor.exe",
        "lyrebird.exe",
        "conjure-client.exe",
        "aether.exe",
        "xray.exe",
        "sing-box.exe",
        "shard.exe",
        "Se7enPro.Aether.exe",
        "Se7enPro.Tor.exe",
        "Se7enPro.Shard.exe",
        "Se7enPro.Lantern.exe",
        "Se7enPro.Veep.exe",
        "veep-core.exe",
        "lanternd.exe",
    };

    private static readonly HashSet<string> EngineBaseNames = new(
        EngineExecutableNames.Select(Path.GetFileNameWithoutExtension).Where(s => !string.IsNullOrEmpty(s))!,
        StringComparer.OrdinalIgnoreCase);

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
        var ownSession = -1;
        try
        {
            var self = Process.GetCurrentProcess();
            ownPid = self.Id;
            ownSession = self.SessionId;
        }
        catch {  }

        var killed = 0;
        var failedToKill = 0;
        foreach (var p in processes)
        {
            try
            {
                if (p.Id == ownPid) continue;

                string pName;
                try
                {
                    pName = p.ProcessName;
                }
                catch
                {
                    continue;
                }
                if (string.IsNullOrEmpty(pName) || !EngineBaseNames.Contains(pName))
                    continue;

                try
                {
                    if (ownSession >= 0 && p.SessionId != ownSession) continue;
                }
                catch
                {
                    continue;
                }

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

                var imageName = Path.GetFileName(imagePath);
                if (string.IsNullOrEmpty(imageName) || !EngineExecutableNames.Contains(imageName))
                    continue;

                _logger.LogInformation(
                    "Killing stale child pid {Pid} ({Image})",
                    p.Id,
                    imagePath);

                try
                {
                    p.Kill(entireProcessTree: true);
                    if (!p.WaitForExit(2000))
                    {
                        failedToKill++;
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
                    failedToKill++;
                    _logger.LogWarning(ex, "Failed to kill stale pid {Pid}", p.Id);
                }
            }
            finally
            {
                try { p.Dispose(); } catch { }
            }
        }

        if (failedToKill == 0)
        {
            TryRemoveStaleLocks(Path.Combine(localAppData, BuildIdentity.AppFolderName, "tunnel-core"));
            TryRemoveStaleLocks(Path.Combine(localAppData, "Psiphon", "tunnel-core"));
        }

        if (killed > 0)
        {
            _logger.LogInformation("Reaper killed {Count} stale child process(es)", killed);
        }
    }

    internal static string[] BuildReapRoots(out int liveRoots)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roots = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Resources"),
            Path.Combine(AppContext.BaseDirectory),
            Path.Combine(localAppData, BuildIdentity.AppFolderName),
            Path.Combine(localAppData, BuildIdentity.AppFolderName, "tunnel-core"),
            Path.Combine(localAppData, BuildIdentity.AppFolderName, "tun2socks"),
            Path.Combine(localAppData, BuildIdentity.AppFolderName, "zeptun"),
            Path.Combine(localAppData, BuildIdentity.AppFolderName, "mihomo"),
            Path.Combine(localAppData, BuildIdentity.AppFolderName, "tor"),
            Path.Combine(localAppData, BuildIdentity.AppFolderName, "aether"),
            Path.Combine(localAppData, BuildIdentity.AppFolderName, "shard"),
            Path.Combine(localAppData, BuildIdentity.AppFolderName, "xray"),
            Path.Combine(localAppData, BuildIdentity.AppFolderName, "sing-box"),
            Path.Combine(localAppData, "Psiphon", "tunnel-core"),
            Path.Combine(localAppData, "Psiphon", "tun2socks"),
            Path.Combine(localAppData, "Psiphon", "singbox-tun"),
            Path.Combine(localAppData, "Psiphon", "xray-tun"),
            Path.Combine(Path.GetTempPath(), BuildIdentity.AppFolderName),
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

    internal static bool ShouldReap(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath)) return false;
        var roots = BuildReapRoots(out var liveRoots);
        return ShouldReap(imagePath, roots, liveRoots);
    }

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
