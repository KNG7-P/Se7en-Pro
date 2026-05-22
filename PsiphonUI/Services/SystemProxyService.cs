using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace PsiphonUI.Services;

public sealed class SystemProxyService : ISystemProxyService
{
    private const string InternetSettingsKey =
        @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    private const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
    private const int INTERNET_OPTION_REFRESH = 37;

    [DllImport("wininet.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool InternetSetOption(
        IntPtr hInternet,
        int dwOption,
        IntPtr lpBuffer,
        int lpdwBufferLength);

    private readonly ILogger<SystemProxyService> _logger;

    public SystemProxyService(ILogger<SystemProxyService> logger) => _logger = logger;

    public void Set(int httpProxyPort)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsKey, writable: true);
            if (key is null)
            {
                _logger.LogWarning("Could not open Internet Settings key");
                return;
            }

            var appProxyServer = BuildAppProxyServer(httpProxyPort);
            if (!File.Exists(BackupPath))
            {
                var backup = new ProxyBackup
                {
                    ProxyEnable = key.GetValue("ProxyEnable") as int? ?? 0,
                    ProxyServer = key.GetValue("ProxyServer") as string ?? "",
                    ProxyOverride = key.GetValue("ProxyOverride") as string ?? "",
                    OwnedProxyServer = appProxyServer,
                };
                WriteBackup(backup);
            }
            else
            {
                var backup = ReadBackup();
                if (backup is not null)
                {
                    backup.OwnedProxyServer = appProxyServer;
                    WriteBackup(backup);
                }
            }

            key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
            key.SetValue("ProxyServer", appProxyServer, RegistryValueKind.String);

            key.SetValue("ProxyOverride", "<local>", RegistryValueKind.String);

            NotifyWinINet();
            _logger.LogInformation("System proxy set to 127.0.0.1:{Port}", httpProxyPort);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set system proxy");
        }
    }

    public void Clear()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsKey, writable: true);
            if (key is null) return;

            var backup = ReadBackup();
            var currentProxyServer = key.GetValue("ProxyServer") as string ?? "";
            if (backup is not null)
            {
                if (!IsAppOwnedProxy(currentProxyServer, backup))
                {
                    _logger.LogWarning(
                        "Current system proxy no longer points at PsiphonUI ({ProxyServer}); leaving user settings unchanged",
                        currentProxyServer);
                    TryDeleteBackup();
                    return;
                }

                key.SetValue("ProxyEnable", backup.ProxyEnable, RegistryValueKind.DWord);
                if (string.IsNullOrEmpty(backup.ProxyServer))
                {
                    try { key.DeleteValue("ProxyServer", throwOnMissingValue: false); } catch { }
                }
                else
                {
                    key.SetValue("ProxyServer", backup.ProxyServer, RegistryValueKind.String);
                }
                if (string.IsNullOrEmpty(backup.ProxyOverride))
                {
                    try { key.DeleteValue("ProxyOverride", throwOnMissingValue: false); } catch { }
                }
                else
                {
                    key.SetValue("ProxyOverride", backup.ProxyOverride, RegistryValueKind.String);
                }
                TryDeleteBackup();
            }
            else
            {
                _logger.LogDebug("No PsiphonUI proxy backup exists; leaving current proxy settings unchanged");
                return;
            }

            NotifyWinINet();
            _logger.LogInformation("System proxy cleared");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear system proxy");
        }
    }

    public void RestoreIfCrashed()
    {
        try
        {
            var backup = ReadBackup();
            if (backup is null) return;

            _logger.LogWarning(
                "Found leftover proxy backup from previous crashed run; restoring original WinINet values");

            Clear();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RestoreIfCrashed failed");
        }
    }

    private static void NotifyWinINet()
    {

        InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
    }

    private static string BackupPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Psiphon",
        "proxy-backup.json");

    private sealed class ProxyBackup
    {
        [JsonPropertyName("proxyEnable")] public int ProxyEnable { get; set; }
        [JsonPropertyName("proxyServer")] public string ProxyServer { get; set; } = "";
        [JsonPropertyName("proxyOverride")] public string ProxyOverride { get; set; } = "";
        [JsonPropertyName("ownedProxyServer")] public string OwnedProxyServer { get; set; } = "";
    }

    private static string BuildAppProxyServer(int httpProxyPort) => $"127.0.0.1:{httpProxyPort}";

    private static bool IsAppOwnedProxy(string proxyServer, ProxyBackup backup)
    {
        if (string.IsNullOrWhiteSpace(proxyServer)) return false;

        var value = proxyServer.Trim();
        if (!string.IsNullOrWhiteSpace(backup.OwnedProxyServer))
        {
            return string.Equals(value, backup.OwnedProxyServer.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        return value.StartsWith("127.0.0.1:", StringComparison.OrdinalIgnoreCase)
               || value.StartsWith("http=127.0.0.1:", StringComparison.OrdinalIgnoreCase)
               || value.Contains(";http=127.0.0.1:", StringComparison.OrdinalIgnoreCase);
    }

    private void WriteBackup(ProxyBackup backup)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(BackupPath)!);
            File.WriteAllText(BackupPath, JsonSerializer.Serialize(backup));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not write proxy backup at {Path}", BackupPath);
        }
    }

    private ProxyBackup? ReadBackup()
    {
        try
        {
            if (!File.Exists(BackupPath)) return null;
            return JsonSerializer.Deserialize<ProxyBackup>(File.ReadAllText(BackupPath));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read proxy backup at {Path}", BackupPath);
            return null;
        }
    }

    private void TryDeleteBackup()
    {
        try { File.Delete(BackupPath); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not delete proxy backup at {Path}", BackupPath);
        }
    }
}
