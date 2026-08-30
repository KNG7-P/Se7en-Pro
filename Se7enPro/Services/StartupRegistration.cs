using System;
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

            var path = Environment.ProcessPath ?? "";
            return !string.IsNullOrEmpty(path) && stored.IndexOf(path, StringComparison.OrdinalIgnoreCase) >= 0;
        }
        catch
        {
            return false;
        }
    }

    public void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null) return;

            if (enabled)
            {
                key.SetValue(ValueName, BuildCommand(), RegistryValueKind.String);
            }
            else
            {
                if (key.GetValue(ValueName) is not null)
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

        var quotedPath = path.Contains(' ', StringComparison.Ordinal) ? $"\"{path}\"" : path;
        return $"{quotedPath} {AutostartArg}";
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
