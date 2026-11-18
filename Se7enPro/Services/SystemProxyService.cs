using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Se7enPro.Services;

public sealed class SystemProxyService : ISystemProxyService
{
    private const string InternetSettingsKey =
        @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    private const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
    private const int INTERNET_OPTION_REFRESH = 37;

    private const string StandardBypassList =
        "localhost;127.*;10.*;172.16.*;172.17.*;172.18.*;172.19.*;172.20.*;172.21.*;172.22.*;"
        + "172.23.*;172.24.*;172.25.*;172.26.*;172.27.*;172.28.*;172.29.*;172.30.*;172.31.*;"
        + "192.168.*;169.254.*;[::1];<local>";

    [DllImport("wininet.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool InternetSetOption(
        IntPtr hInternet,
        int dwOption,
        IntPtr lpBuffer,
        int lpdwBufferLength);

    private readonly ILogger<SystemProxyService> _logger;

    public SystemProxyService(ILogger<SystemProxyService> logger) => _logger = logger;

    public bool IsApplied => File.Exists(BackupPath);

    public void Set(int httpProxyPort) => Set(httpProxyPort, null);

    public void Set(int httpProxyPort, System.Collections.Generic.IEnumerable<string>? bypassEntries)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsKey, writable: true);
            if (key is null)
            {
                _logger.LogWarning("Could not open Internet Settings key");
                return;
            }

            if (!File.Exists(BackupPath))
            {
                var backup = new ProxyBackup
                {
                    ProxyEnable = key.GetValue("ProxyEnable") as int? ?? 0,
                    ProxyServer = key.GetValue("ProxyServer") as string ?? "",
                    ProxyOverride = key.GetValue("ProxyOverride") as string ?? "",



                    AutoConfigUrl = key.GetValue("AutoConfigURL") as string ?? "",
                };
                WriteBackup(backup);
            }

            key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
            key.SetValue("ProxyServer", $"127.0.0.1:{httpProxyPort}", RegistryValueKind.String);

            var bypassList = StandardBypassList;
            if (bypassEntries != null)
            {
                var extra = string.Join(";", System.Linq.Enumerable.Where(bypassEntries, e => !string.IsNullOrWhiteSpace(e)));
                if (extra.Length > 0)
                {
                    bypassList = $"{bypassList};{extra}";
                }
            }

            key.SetValue("ProxyOverride", bypassList, RegistryValueKind.String);

            
            
            
            try { key.DeleteValue("AutoConfigURL", throwOnMissingValue: false); } catch { }

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
            if (backup is not null)
            {
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

                
                if (string.IsNullOrEmpty(backup.AutoConfigUrl))
                {
                    try { key.DeleteValue("AutoConfigURL", throwOnMissingValue: false); } catch { }
                }
                else
                {
                    key.SetValue("AutoConfigURL", backup.AutoConfigUrl, RegistryValueKind.String);
                }

                TryDeleteBackup();
            }
            else
            {
                var current = new ProxyBackup
                {
                    ProxyEnable = key.GetValue("ProxyEnable") as int? ?? 0,
                    ProxyServer = key.GetValue("ProxyServer") as string ?? "",
                    ProxyOverride = key.GetValue("ProxyOverride") as string ?? "",
                    AutoConfigUrl = key.GetValue("AutoConfigURL") as string ?? "",
                };

                if (current.ProxyEnable == 0)
                {
                    _logger.LogInformation("No active system proxy configured; nothing to clear");
                    return;
                }

                if (current.ProxyServer.StartsWith("127.0.0.1:", StringComparison.Ordinal)
                    || current.ProxyServer.StartsWith("localhost:", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(current.ProxyOverride, StandardBypassList, StringComparison.OrdinalIgnoreCase))
                {
                    key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
                    try { key.DeleteValue("ProxyServer", throwOnMissingValue: false); } catch { }
                    try { key.DeleteValue("ProxyOverride", throwOnMissingValue: false); } catch { }
                    NotifyWinINet();
                    _logger.LogWarning("Removed a leftover Se7en system proxy; no original backup was available");
                    return;
                }

                WriteBackup(current);
                key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
                _logger.LogInformation(
                    "System proxy disabled; the manual configuration was backed up for restore");
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
            if (backup is null && File.Exists(LegacyBackupPath))
            {
                try
                {
                    var json = File.ReadAllText(LegacyBackupPath);
                    backup = JsonSerializer.Deserialize<ProxyBackup>(json);
                    if (backup is not null)
                    {
                        WriteBackup(backup);
                    }
                    try { File.Delete(LegacyBackupPath); } catch { }
                }
                catch { }
            }
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
        "Se7en",
        "proxy-backup.json");

    private static string LegacyBackupPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Psiphon",
        "proxy-backup.json");

    private sealed class ProxyBackup
    {
        [JsonPropertyName("proxyEnable")] public int ProxyEnable { get; set; }
        [JsonPropertyName("proxyServer")] public string ProxyServer { get; set; } = "";
        [JsonPropertyName("proxyOverride")] public string ProxyOverride { get; set; } = "";
                [JsonPropertyName("autoConfigUrl")] public string AutoConfigUrl { get; set; } = "";
    }

    private static void WriteBackup(ProxyBackup backup)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(BackupPath)!);
        File.WriteAllText(BackupPath, JsonSerializer.Serialize(backup));
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
