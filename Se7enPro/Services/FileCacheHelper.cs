using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Se7enPro.Services;

internal static class FileCacheHelper
{
    public static bool IsCachedCopyUpToDate(string source, string cached)
    {
        try
        {
            if (!File.Exists(source) || !File.Exists(cached)) return false;

            var srcInfo = new FileInfo(source);
            var dstInfo = new FileInfo(cached);

            if (srcInfo.Length != dstInfo.Length) return false;

            var srcMtime = srcInfo.LastWriteTimeUtc;
            var dstMtime = dstInfo.LastWriteTimeUtc;
            var delta = (srcMtime - dstMtime).Duration();

            if (delta >= TimeSpan.FromSeconds(2))
            {
                return false;
            }

            if (srcInfo.Length < 100 * 1024 * 1024)
            {
                return ComputeSha256(source) == ComputeSha256(cached);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string ComputeSha256(string filePath)
    {
        using var sha = SHA256.Create();
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var hash = sha.ComputeHash(stream);
        return Convert.ToHexString(hash);
    }

    public static bool StageFileSafe(
        string sourcePath,
        string destPath,
        ILogger? logger = null,
        IEnumerable<string>? processesToKill = null)
    {
        if (!File.Exists(sourcePath))
        {
            logger?.LogError("Source file does not exist for staging: {Source}", sourcePath);
            return false;
        }

        if (IsCachedCopyUpToDate(sourcePath, destPath))
        {
            return true;
        }

        var destDir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(destDir))
        {
            Directory.CreateDirectory(destDir);
            CleanOldTempFiles(destDir);
        }

        logger?.LogInformation("Updating staged core: {Source} -> {Dest}", sourcePath, destPath);

        KillProcessesHoldingFile(destPath, processesToKill, logger);

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                File.Copy(sourcePath, destPath, overwrite: true);
                try { File.SetLastWriteTimeUtc(destPath, File.GetLastWriteTimeUtc(sourcePath)); } catch { }
                logger?.LogInformation("Staged core updated successfully on attempt {Attempt}: {Dest}", attempt, destPath);
                return true;
            }
            catch (IOException ioEx)
            {
                logger?.LogWarning("Attempt {Attempt} to copy {Source} to {Dest} failed: {Msg}", attempt, sourcePath, destPath, ioEx.Message);

                KillProcessesHoldingFile(destPath, processesToKill, logger);

                if (File.Exists(destPath))
                {
                    var tempOld = destPath + ".old." + Guid.NewGuid().ToString("N")[..8];
                    try
                    {
                        File.Move(destPath, tempOld, overwrite: true);
                        File.Copy(sourcePath, destPath, overwrite: true);
                        try { File.SetLastWriteTimeUtc(destPath, File.GetLastWriteTimeUtc(sourcePath)); } catch { }
                        try { File.Delete(tempOld); } catch { }
                        logger?.LogInformation("Staged core replaced via atomic rename fallback: {Dest}", destPath);
                        return true;
                    }
                    catch (Exception moveEx)
                    {
                        logger?.LogWarning("Atomic rename fallback also failed: {Msg}", moveEx.Message);
                    }
                }

                Thread.Sleep(100 * attempt);
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Unexpected error staging file: {Source} -> {Dest}", sourcePath, destPath);
                break;
            }
        }

        return File.Exists(destPath);
    }

    private static void KillProcessesHoldingFile(string filePath, IEnumerable<string>? extraNames, ILogger? logger)
    {
        try
        {
            var targetExe = Path.GetFileNameWithoutExtension(filePath);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { targetExe };

            if (extraNames != null)
            {
                foreach (var n in extraNames)
                {
                    if (string.IsNullOrWhiteSpace(n)) continue;
                    var clean = n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                        ? Path.GetFileNameWithoutExtension(n)
                        : n;
                    names.Add(clean);
                }
            }

            foreach (var name in names)
            {
                Process[] procs;
                try { procs = Process.GetProcessesByName(name); }
                catch { continue; }

                foreach (var proc in procs)
                {
                    try
                    {
                        bool shouldKill = false;
                        try
                        {
                            var modulePath = proc.MainModule?.FileName;
                            if (string.IsNullOrEmpty(modulePath) ||
                                string.Equals(modulePath, filePath, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(Path.GetFileName(modulePath), Path.GetFileName(filePath), StringComparison.OrdinalIgnoreCase))
                            {
                                shouldKill = true;
                            }
                        }
                        catch
                        {

                            shouldKill = true;
                        }

                        if (shouldKill)
                        {
                            logger?.LogInformation("Terminating locking process {ProcName} (PID {Pid}) before staging file update", proc.ProcessName, proc.Id);
                            proc.Kill(entireProcessTree: true);
                            proc.WaitForExit(1000);
                        }
                    }
                    catch { }
                    finally
                    {
                        proc.Dispose();
                    }
                }
            }
        }
        catch { }
    }

    private static void CleanOldTempFiles(string directory)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.old.*"))
            {
                try { File.Delete(file); } catch { }
            }
        }
        catch { }
    }
}
