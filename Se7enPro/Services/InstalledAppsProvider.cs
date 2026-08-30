using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Se7enPro.Services;

public sealed class InstalledAppInfo
{

    public string Name { get; init; } = "";

    public string ExePath { get; init; } = "";

    public string FileName { get; init; } = "";

    public bool IsRunning { get; init; }

    public ImageSource? Icon { get; set; }

    public string SearchText => $"{Name}\n{FileName}\n{ExePath}";
}

public static class InstalledAppsProvider
{
    private const int MaxResults = 600;
    private const int ShallowScanDepth = 3;

    private static readonly string[] NoisePrefixes =
    {
        "unins", "setup", "install", "update", "vcredist", "vc_redist",
        "crashpad", "crashreport", "werfault", "dxsetup", "dotnetfx",
    };

    public static Task<List<InstalledAppInfo>> LoadAsync(CancellationToken ct = default)
    {

        var tcs = new TaskCompletionSource<List<InstalledAppInfo>>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            try { tcs.TrySetResult(Load(ct)); }
            catch (OperationCanceledException) { tcs.TrySetCanceled(ct); }
            catch (Exception) { tcs.TrySetResult(new List<InstalledAppInfo>()); }
        })
        {
            IsBackground = true,
            Name = "AppPickerScan",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        return tcs.Task;
    }

    private static List<InstalledAppInfo> Load(CancellationToken ct)
    {

        var byPath = new Dictionary<string, InstalledAppInfo>(StringComparer.OrdinalIgnoreCase);

        var running = CollectRunningExePaths(ct);

        foreach (var path in CollectShortcutTargets(ct))
        {
            AddCandidate(byPath, path, name: null, running);
        }

        foreach (var path in running)
        {
            AddCandidate(byPath, path, name: null, running);
        }

        if (byPath.Count == 0)
        {
            foreach (var path in ShallowScanInstallRoots(ct))
            {
                AddCandidate(byPath, path, name: null, running);
            }
        }

        var list = byPath.Values
            .OrderByDescending(a => a.IsRunning)
            .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(MaxResults)
            .ToList();

        LoadIcons(list, ct);
        return list;
    }

    private static void AddCandidate(
        Dictionary<string, InstalledAppInfo> byPath,
        string exePath,
        string? name,
        HashSet<string> running)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return;
        if (!exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return;
        if (byPath.ContainsKey(exePath)) return;

        var fileName = Path.GetFileName(exePath);
        if (IsNoise(fileName)) return;
        if (!File.Exists(exePath)) return;

        byPath[exePath] = new InstalledAppInfo
        {
            Name = name ?? DescribeExe(exePath, fileName),
            ExePath = exePath,
            FileName = fileName,
            IsRunning = running.Contains(exePath),
        };
    }

    private static bool IsNoise(string fileName)
    {
        foreach (var prefix in NoisePrefixes)
        {
            if (fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static string DescribeExe(string exePath, string fileName)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(exePath);
            var description = (info.FileDescription ?? "").Trim();
            if (description.Length > 0 && description.Length < 80) return description;

            var product = (info.ProductName ?? "").Trim();
            if (product.Length > 0 && product.Length < 80) return product;
        }
        catch
        {

        }

        return Path.GetFileNameWithoutExtension(fileName);
    }

    private static HashSet<string> CollectRunningExePaths(CancellationToken ct)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Process[] processes;
        try { processes = Process.GetProcesses(); }
        catch { return paths; }

        foreach (var p in processes)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var path = p.MainModule?.FileName;
                if (!string.IsNullOrEmpty(path) && !IsSystemPath(path)) paths.Add(path!);
            }
            catch
            {

            }
            finally { p.Dispose(); }
        }

        return paths;
    }

    private static IEnumerable<string> CollectShortcutTargets(CancellationToken ct)
    {
        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs"),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
        };

        object? shell = null;
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type is not null) shell = Activator.CreateInstance(type);
        }
        catch
        {
            shell = null;
        }

        if (shell is null) yield break;

        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;

            string[] links;
            try { links = Directory.GetFiles(root, "*.lnk", SearchOption.AllDirectories); }
            catch { continue; }

            foreach (var link in links)
            {
                ct.ThrowIfCancellationRequested();
                var target = ResolveShortcut(shell, link);
                if (target is not null) yield return target;
            }
        }
    }

    private static string? ResolveShortcut(object shell, string lnkPath)
    {
        try
        {
            var shortcut = shell.GetType().InvokeMember(
                "CreateShortcut",
                System.Reflection.BindingFlags.InvokeMethod,
                binder: null,
                target: shell,
                args: new object[] { lnkPath });
            if (shortcut is null) return null;

            var target = shortcut.GetType().InvokeMember(
                "TargetPath",
                System.Reflection.BindingFlags.GetProperty,
                binder: null,
                target: shortcut,
                args: null) as string;

            return string.IsNullOrWhiteSpace(target) ? null : target!.Trim();
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string> ShallowScanInstallRoots(CancellationToken ct)
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
        };

        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
            foreach (var exe in EnumerateExes(root, ShallowScanDepth, ct)) yield return exe;
        }
    }

    private static IEnumerable<string> EnumerateExes(string dir, int depth, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        string[] files;
        try { files = Directory.GetFiles(dir, "*.exe"); }
        catch { yield break; }
        foreach (var f in files) yield return f;

        if (depth <= 1) yield break;

        string[] subDirs;
        try { subDirs = Directory.GetDirectories(dir); }
        catch { yield break; }

        foreach (var sub in subDirs)
        {
            var name = Path.GetFileName(sub);
            if (name.Equals("Uninstall", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var f in EnumerateExes(sub, depth - 1, ct)) yield return f;
        }
    }

    private static bool IsSystemPath(string path)
    {
        var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var sysWow = Environment.GetFolderPath(Environment.SpecialFolder.SystemX86);
        return StartsWith(path, system32) || StartsWith(path, sysWow);

        static bool StartsWith(string p, string root) =>
            !string.IsNullOrEmpty(root)
            && p.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static void LoadIcons(List<InstalledAppInfo> apps, CancellationToken ct)
    {
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Min(8, Environment.ProcessorCount),
            CancellationToken = ct,
        };

        try
        {
            Parallel.ForEach(apps, options, app => app.Icon = TryExtractIcon(app.ExePath));
        }
        catch (OperationCanceledException) { throw; }
        catch
        {

        }
    }

    private static ImageSource? TryExtractIcon(string exePath)
    {
        System.Drawing.Icon? icon = null;
        try
        {
            icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
            if (icon is null) return null;

            var source = Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle,
                System.Windows.Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
        finally
        {
            icon?.Dispose();
        }
    }
}
