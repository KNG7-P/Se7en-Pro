using System;
using System.IO;
using Microsoft.Win32;

namespace Se7enPro.Services;

public sealed class StartupRegistration : IStartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private const string ValueName = "Se7enPro";

    public const string AutostartArg = "--autostart";

    public bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            if (key is null) return false;
            var stored = key.GetValue(ValueName) as string;
            if (string.IsNullOrEmpty(stored)) return false;

            
            
            
            
            
            
            var exe = StoredExecutable(stored);
            if (exe is null) return false;

            var path = Environment.ProcessPath ?? "";
            if (string.IsNullOrEmpty(path)) return false;

            return string.Equals(exe, path.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

        private static string? StoredExecutable(string commandLine)
    {
        var v = commandLine.Trim();
        if (v.Length == 0) return null;

        if (v[0] == '"')
        {
            var close = v.IndexOf('"', 1);
            if (close <= 1) return null;
            return v[1..close];
        }

        
        
        
        var firstSpace = v.IndexOf(' ');
        if (firstSpace == 0) return null;
        if (firstSpace < 0) return v;
        if (v.Contains('"', StringComparison.Ordinal)) return null;

        
        var token = v[..firstSpace];
        return token.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? token : null;
    }

    public void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null) return;

            if (enabled)
            {
                var command = BuildCommand();
                if (command.Length == 0) return;
                key.SetValue(ValueName, command, RegistryValueKind.String);
            }
            else
            {
                
                
                var stored = key.GetValue(ValueName) as string;
                var exe = stored is null ? null : StoredExecutable(stored);
                var ours = exe is not null &&
                           string.Equals(exe, (Environment.ProcessPath ?? "").Trim(),
                               StringComparison.OrdinalIgnoreCase);
                if (stored is not null && ours)
                {
                    key.DeleteValue(ValueName, throwOnMissingValue: false);
                }
            }
        }
        catch
        {
        }
    }

    public void SyncFromSetting(bool desired)
    {
        var actual = IsEnabled();
        if (actual != desired)
        {
            SetEnabled(desired);
        }
    }

    private static string BuildCommand()
    {
        var path = Environment.ProcessPath ?? "";
        if (string.IsNullOrEmpty(path)) return "";

        
        
        
        
        var leaf = Path.GetFileName(path);
        if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(leaf, "dotnet.exe", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(leaf, "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }

        
        
        return $"\"{path}\" {AutostartArg}";
    }

    private static string NormalizePath(string value)
    {
        var v = value.Trim();
        if (v.Length >= 2 && v[0] == '"' && v[^1] == '"')
        {
            v = v.Substring(1, v.Length - 2);
        }
        return v;
    }
}
