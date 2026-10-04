using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace Se7enPro.Services;

internal static class FileCacheHelper
{
    public static bool IsCachedCopyUpToDate(string source, string cached)
    {
        try
        {
            if (!File.Exists(cached)) return false;
            if (!File.Exists(source)) return true;

            var srcInfo = new FileInfo(source);
            var dstInfo = new FileInfo(cached);

            if (dstInfo.Length == 0) return false;

            // If both files are executables, check if cached is a newer user-installed core
            if (source.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                cached.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                var srcVerStr = QueryVersion(source);
                var dstVerStr = QueryVersion(cached);

                if (Version.TryParse(srcVerStr, out var srcV) && Version.TryParse(dstVerStr, out var dstV))
                {
                    // User applied an in-app core update with a higher version -> keep cached!
                    if (dstV > srcV) return true;
                    // Bundled binary has a higher version (e.g. app upgrade from v1.0.3 to v1.0.5) -> overwrite cached!
                    if (srcV > dstV) return false;
                }
            }

            if (srcInfo.Length != dstInfo.Length)
            {
                // If cached was updated significantly after bundled source, it may be an updated core without a parseable version
                if (dstInfo.LastWriteTimeUtc > srcInfo.LastWriteTimeUtc + TimeSpan.FromMinutes(5) &&
                    dstInfo.Length > 1024 * 512)
                {
                    return true;
                }
                return false;
            }

            var delta = (srcInfo.LastWriteTimeUtc - dstInfo.LastWriteTimeUtc).Duration();
            if (delta < TimeSpan.FromSeconds(2)) return true;

            // Same length but different mtime: compare SHA256 hashes
            return HashesMatch(source, cached);
        }
        catch
        {
            return false;
        }
    }

    public static bool EnsureCachedCopy(string source, string cached, ILogger? logger = null)
    {
        try
        {
            if (!File.Exists(source)) return false;
            if (IsCachedCopyUpToDate(source, cached)) return true;

            var dir = Path.GetDirectoryName(cached);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // Terminate any process that might be holding a lock on the cached binary
            if (File.Exists(cached))
            {
                var procName = Path.GetFileNameWithoutExtension(cached);
                try
                {
                    foreach (var p in Process.GetProcessesByName(procName))
                    {
                        try
                        {
                            p.Kill(entireProcessTree: true);
                            p.WaitForExit(1000);
                        }
                        catch { }
                    }
                }
                catch { }
            }

            try
            {
                File.Copy(source, cached, overwrite: true);
                try { File.SetLastWriteTimeUtc(cached, File.GetLastWriteTimeUtc(source)); } catch { }
                return true;
            }
            catch (IOException)
            {
                // Locked on Windows: rename existing file to .stale and copy new file
                var stalePath = cached + ".stale." + Guid.NewGuid().ToString("N");
                try
                {
                    File.Move(cached, stalePath, overwrite: true);
                    File.Copy(source, cached, overwrite: true);
                    try { File.SetLastWriteTimeUtc(cached, File.GetLastWriteTimeUtc(source)); } catch { }
                    try { File.Delete(stalePath); } catch { }
                    return true;
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "Failed to replace cached binary {Cached} via rename trick", cached);
                    return false;
                }
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to ensure cached copy of {Source} to {Cached}", source, cached);
            return false;
        }
        finally
        {
            try
            {
                var dir = Path.GetDirectoryName(cached);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    foreach (var stale in Directory.EnumerateFiles(dir, "*.stale.*"))
                    {
                        try { File.Delete(stale); } catch { }
                    }
                }
            }
            catch { }
        }
    }

    private static string QueryVersion(string exePath)
    {
        try
        {
            var fvi = FileVersionInfo.GetVersionInfo(exePath);
            if (!string.IsNullOrEmpty(fvi.FileVersion) && fvi.FileVersion != "0.0.0.0")
                return CleanVersion(fvi.FileVersion);
        }
        catch { }

        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = "--version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                }
            };
            if (!proc.Start()) return "";
            var output = proc.StandardOutput.ReadToEnd();
            if (proc.WaitForExit(1500))
            {
                var s = output.Trim();
                var parts = s.Split(new[] { ' ', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = parts.Length - 1; i >= 0; i--)
                {
                    var p = parts[i].Trim().TrimStart('v', 'V');
                    if (p.Length > 0 && char.IsDigit(p[0]))
                    {
                        return CleanVersion(p);
                    }
                }
            }
            try { proc.Kill(); } catch { }
        }
        catch { }

        return "";
    }

    private static string CleanVersion(string v)
    {
        var s = v.Trim().TrimStart('v', 'V');
        var dash = s.IndexOf('-');
        if (dash > 0) s = s.Substring(0, dash);
        return s;
    }

    private static bool HashesMatch(string file1, string file2)
    {
        try
        {
            using var sha = SHA256.Create();
            using var s1 = File.OpenRead(file1);
            using var s2 = File.OpenRead(file2);
            var h1 = sha.ComputeHash(s1);
            var h2 = sha.ComputeHash(s2);
            return h1.AsSpan().SequenceEqual(h2);
        }
        catch
        {
            return false;
        }
    }
}
