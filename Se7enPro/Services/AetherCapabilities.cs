using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace Se7enPro.Services;

/// <summary>
/// Probes and caches optional flag support for aether binaries.
/// </summary>
internal static class AetherCapabilities
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, string?> HelpCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, byte[]?> BinaryCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when this binary understands <paramref name="flag"/>.</summary>
    public static bool Supports(string exePath, string flag)
    {
        if (string.IsNullOrEmpty(flag)) return false;
        if (InHelp(HelpFor(exePath), flag)) return true;
        return InBinary(exePath, flag);
    }

    private static bool InHelp(string? help, string flag)
    {
        if (string.IsNullOrEmpty(help)) return false;

        foreach (var raw in help.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith(flag, StringComparison.Ordinal)) continue;

            var after = flag.Length;
            if (after >= line.Length) return true;

            var next = line[after];
            if (char.IsWhiteSpace(next) || next is '<' or '=' or ',') return true;
        }

        return false;
    }

    /// <summary>Looks for the literal flag inside the binary.</summary>
    private static bool InBinary(string exePath, string flag)
    {
        byte[]? image;
        lock (Gate)
        {
            if (BinaryCache.TryGetValue(exePath, out var cached)) image = cached;
            else
            {
                image = TryReadAllBytes(exePath);
                BinaryCache[exePath] = image;
            }
        }

        return image is not null && ContainsAscii(image, flag);
    }

    private static byte[]? TryReadAllBytes(string exePath)
    {
        try
        {
            return File.Exists(exePath) ? File.ReadAllBytes(exePath) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Naive substring search over raw bytes. Cheap enough for a 7 byte needle.</summary>
    private static bool ContainsAscii(byte[] haystack, string needle)
    {
        var n = Encoding.ASCII.GetBytes(needle);
        if (n.Length == 0 || n.Length > haystack.Length) return false;

        var last = haystack.Length - n.Length;
        for (var i = 0; i <= last; i++)
        {
            if (haystack[i] != n[0]) continue;

            var j = 1;
            while (j < n.Length && haystack[i + j] == n[j]) j++;
            if (j == n.Length) return true;
        }

        return false;
    }

    /// <summary>Reads the banner once so the user can tell which build is in use.</summary>
    public static string Version(string exePath)
    {
        var text = Run(exePath, "--version") ?? "";
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                   .Select(s => s.Trim())
                   .FirstOrDefault() ?? "";
    }

    /// <summary>Drops the caches. Needed when the binary on disk is replaced under us.</summary>
    public static void Invalidate()
    {
        lock (Gate)
        {
            HelpCache.Clear();
            BinaryCache.Clear();
        }
    }

    private static string? HelpFor(string exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return null;

        lock (Gate)
        {
            if (HelpCache.TryGetValue(exePath, out var cached)) return cached;

            var help = Run(exePath, "--help");
            HelpCache[exePath] = help;
            return help;
        }
    }

    private static string? Run(string exePath, string arg)
    {
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath)) return null;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arg,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            using var p = Process.Start(psi);
            if (p is null) return null;

            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();

            // Bounded: a binary that hangs here must not hold up a connect.
            if (!p.WaitForExit(8000))
            {
                try { p.Kill(); } catch { }
                return null;
            }

            var text = outTask.GetAwaiter().GetResult();
            if (string.IsNullOrWhiteSpace(text)) text = errTask.GetAwaiter().GetResult();
            return text;
        }
        catch
        {
            return null;
        }
    }
}