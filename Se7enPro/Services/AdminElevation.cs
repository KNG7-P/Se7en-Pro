using System;
using System.Diagnostics;
using System.Security.Principal;

namespace Se7enPro.Services;

public static class AdminElevation
{
    public static bool IsAdministrator()
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
    }

    public static Action? ReleaseMutexAction { get; set; }
    public static Action? ReacquireMutexAction { get; set; }
    public static Action? ShutdownAppAction { get; set; }

    public static bool TryRestartElevated()
    {
        try
        {
            var exePath = Environment.ProcessPath
                          ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exePath)) return false;

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory,
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
}
