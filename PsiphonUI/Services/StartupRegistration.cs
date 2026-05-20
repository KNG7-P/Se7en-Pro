using System;
using Microsoft.Win32;

namespace PsiphonUI.Services;

public sealed class StartupRegistration : IStartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private const string ValueName = "PsiphonUI";

    public bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            if (key is null) return false;
            var stored = key.GetValue(ValueName) as string;
            if (string.IsNullOrEmpty(stored)) return false;

            var expected = BuildCommand();
            return string.Equals(NormalizePath(stored), NormalizePath(expected), StringComparison.OrdinalIgnoreCase);
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

        return path.Contains(' ', StringComparison.Ordinal) ? $"\"{path}\"" : path;
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
