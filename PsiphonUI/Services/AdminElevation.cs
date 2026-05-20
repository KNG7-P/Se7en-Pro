using System;
using System.Diagnostics;
using System.Security.Principal;

namespace PsiphonUI.Services;

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

            using var p = Process.Start(psi);
            return p is not null;
        }
        catch (System.ComponentModel.Win32Exception)
        {

            return false;
        }
        catch
        {
            return false;
        }
    }
}
