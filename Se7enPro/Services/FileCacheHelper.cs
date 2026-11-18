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
            if (srcInfo.Length != dstInfo.Length) return false;

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

            if (File.Exists(cached))
            {
                var procName = Path.GetFileNameWithoutExtension(cached);
                try
                {
                    foreach (var p in Process.GetProcessesByName(procName))
                    {
                        using (p)
                        {
                            try
                            {
                                p.Kill(entireProcessTree: true);
                                p.WaitForExit(1000);
                            }
                            catch { }
                        }
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
