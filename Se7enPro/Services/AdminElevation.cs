using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;

namespace Se7enPro.Services;

public static class AdminElevation
{
    
    
    
    
    
    
    private static readonly Lazy<bool> _isAdministrator = new(() =>
    {
        try
        {
            using var ident = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(ident).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    });

    public static bool IsAdministrator() => _isAdministrator.Value;

    public static Action? ReleaseMutexAction { get; set; }
    public static Action? ReacquireMutexAction { get; set; }
    public static Action? ShutdownAppAction { get; set; }

        public static bool IsTrustedInstallPath(string? exePath = null)
    {
        try
        {
            exePath ??= Environment.ProcessPath
                        ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exePath)) return false;

            if (!string.Equals(Path.GetExtension(exePath), ".exe", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var full = Path.GetFullPath(exePath);
            var allowed = new List<string>
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Programs"),
            };

            foreach (var root in allowed)
            {
                if (string.IsNullOrWhiteSpace(root)) continue;
                if (full.StartsWith(
                        Path.GetFullPath(root).TrimEnd('\\') + "\\",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryRestartElevated()
    {
        try
        {
            var exePath = Environment.ProcessPath
                          ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exePath)) return false;

            if (!IsTrustedInstallPath(exePath))
            {
                
                
                System.Diagnostics.Debug.WriteLine(
                    $"[elevation] elevating from a user-writable location (user consented): {exePath}");
            }

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory,
                
                
                
                Arguments = BuildForwardedArguments(),
            };

            
            ReleaseMutexAction?.Invoke();

            Process? p = null;
            try
            {
                p = Process.Start(psi);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                
                ReacquireMutexAction?.Invoke();
                return false;
            }

            if (p is not null)
            {
                
                
                
                
                ShutdownAppAction?.Invoke();
                return true;
            }

            ReacquireMutexAction?.Invoke();
            return false;
        }
        catch
        {
            ReacquireMutexAction?.Invoke();
            return false;
        }
    }

        private static string BuildForwardedArguments()
    {
        try
        {
            var allowed = new[] { "--autostart", "--minimized", "/minimized", "-minimized" };
            var forwarded = new List<string>();

            var args = Environment.GetCommandLineArgs();
            for (int i = 1; i < args.Length; i++)
            {
                if (allowed.Contains(args[i], StringComparer.OrdinalIgnoreCase))
                {
                    forwarded.Add(args[i]);
                }
            }

            return string.Join(' ', forwarded);
        }
        catch
        {
            return "";
        }
    }
}
