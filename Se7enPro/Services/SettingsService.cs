using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using Microsoft.Extensions.Logging;
using Se7enPro.Models;

namespace Se7enPro.Services;

public sealed class SettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly ILogger<SettingsService> _logger;
    private readonly string _path;
    private readonly string _backupPath;
    private readonly object _writeLock = new();

    
    
    private static readonly HashSet<string> KnownKeys = BuildKnownKeys();

    private JsonObject? _foreignKeys;

    // Test hooks: let the offline suite drive the real persistence path against a temp
    // directory instead of the user profile.
    internal static string? DirectoryOverride { get; set; }

    public UserSettings Settings { get; private set; } = new();

    public event EventHandler? SettingsChanged;

    public SettingsService(ILogger<SettingsService> logger)
    {
        _logger = logger;
        ProtectedSecret.Warning ??= message => _logger.LogWarning("{SecretWarning}", message);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dir = DirectoryOverride ?? Path.Combine(localAppData, BuildIdentity.AppFolderName);
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "settings.json");
        _backupPath = Path.Combine(dir, "settings.json.last-good");

        
        if (!File.Exists(_path))
        {
            var legacyPath = Path.Combine(localAppData, "Psiphon", "settings.json");
            if (File.Exists(legacyPath))
            {
                try { File.Copy(legacyPath, _path, overwrite: false); } catch { }
            }
        }
    }

    private static HashSet<string> BuildKnownKeys()
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in typeof(UserSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanWrite) continue;
            keys.Add(prop.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? prop.Name);
        }
        return keys;
    }

    
    
    
    private void CaptureForeignKeys(string json)
    {
        _foreignKeys = null;
        try
        {
            var parsed = JsonNode.Parse(json)?.AsObject();
            if (parsed is null) return;

            var foreign = new JsonObject();
            foreach (var property in parsed)
            {
                if (KnownKeys.Contains(property.Key)) continue;
                foreign[property.Key] = property.Value?.DeepClone();
            }

            if (foreign.Count > 0) _foreignKeys = foreign;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not inspect foreign keys in {Path}", _path);
        }
    }

    private string MergeForeignKeys(string json)
    {
        var foreign = _foreignKeys;
        if (foreign is null || foreign.Count == 0) return json;

        try
        {
            var obj = JsonNode.Parse(json)?.AsObject();
            if (obj is null) return json;

            foreach (var property in foreign)
            {
                if (obj.ContainsKey(property.Key)) continue;
                obj[property.Key] = property.Value?.DeepClone();
            }

            return obj.ToJsonString(JsonOpts);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not restore foreign keys in {Path}; writing typed settings", _path);
            return json;
        }
    }

    public void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                Settings = new UserSettings { SettingsSchemaVersion = CurrentSettingsSchema };
                _foreignKeys = null;
                SplitTunnelProfileStore.NormalizeAll(Settings);
                Save();
                return;
            }

            var json = File.ReadAllText(_path);
            var plain = UnprotectSecrets(json);
            Settings = JsonSerializer.Deserialize<UserSettings>(plain, JsonOpts)
                       ?? new UserSettings();
            CaptureForeignKeys(json);
            MigrateRemovedAutoMethod();
            MigrateAetherDefaults();
            MigrateSplitTunnelProfiles(plain);
        }
        catch (Exception ex)
        {
            
            
            
            
            _logger.LogWarning(ex, "Failed to load settings from {Path}; trying the last good copy", _path);
            try
            {
                var rescue = File.ReadAllText(_backupPath);
                var plainRescue = UnprotectSecrets(rescue);
                Settings = JsonSerializer.Deserialize<UserSettings>(plainRescue, JsonOpts)
                           ?? new UserSettings();
                CaptureForeignKeys(rescue);
                MigrateRemovedAutoMethod();
                MigrateAetherDefaults();
                MigrateSplitTunnelProfiles(plainRescue);
                return;
            }
            catch (Exception rescueEx)
            {
                _logger.LogWarning(rescueEx, "Last good settings copy unusable; falling back to defaults");
            }

            Settings = new UserSettings { SettingsSchemaVersion = CurrentSettingsSchema };
            _foreignKeys = null;
        }
    }

        private void MigrateRemovedAutoMethod()
    {
        var settings = Settings;
        if (settings is null) return;

        var method = (settings.ConnectionMethod ?? "").Trim();
        if (string.Equals(method, "auto", StringComparison.OrdinalIgnoreCase))
        {
            settings.ConnectionMethod = ConnectionMethodExtensions.ParseConnectionMethod(method).ToToken();
            _logger?.LogInformation(
                "Settings carried the removed Auto connection method; migrated to {Method}",
                settings.ConnectionMethod);
        }

        if (_foreignKeys is null) return;
        if (!_foreignKeys.Remove("autoLastSuccessfulMethod")) return;

        _logger?.LogInformation("Dropped the removed autoLastSuccessfulMethod setting");
        Save();
    }

    // One-time upgrade of the old flat split-tunnel list into the profile model, plus a
    // normalisation pass that keeps persisted profiles internally consistent.
    private void MigrateSplitTunnelProfiles(string plainJson)
    {
        var migrated = SplitTunnelMigration.Migrate(Settings, plainJson);
        SplitTunnelProfileStore.NormalizeAll(Settings);
        if (!migrated) return;

        // The legacy keys must not be carried forward into every later save.
        if (_foreignKeys is not null)
        {
            _foreignKeys.Remove("splitTunnelMode");
            _foreignKeys.Remove("splitTunnelEntries");
            if (_foreignKeys.Count == 0) _foreignKeys = null;
        }

        Save();
    }

    private const int CurrentSettingsSchema = 6;

    private void MigrateAetherDefaults()
    {
        var settings = Settings;
        if (settings is null) return;

        if (settings.SettingsSchemaVersion >= CurrentSettingsSchema) return;

        if (settings.SettingsSchemaVersion < 1)
        {
            if (string.IsNullOrWhiteSpace(settings.AetherFragmentSize))
            {
                settings.AetherFragmentSize = "16-32";
            }
            if (string.IsNullOrWhiteSpace(settings.AetherFragmentDelay))
            {
                settings.AetherFragmentDelay = "2-10";
            }
            if (string.IsNullOrWhiteSpace(settings.AetherScanModeMim)
                || settings.AetherScanModeMim.Equals("balanced", StringComparison.OrdinalIgnoreCase))
            {
                settings.AetherScanModeMim = "turbo";
            }
        }

        if (settings.SettingsSchemaVersion < 2)
        {
            settings.AetherMasqueTransport = "h2";
            settings.AetherMasqueQuic = false;
        }

        if (settings.SettingsSchemaVersion < 3)
        {
            settings.AetherMasqueTransport = "h2";
            settings.AetherMasqueQuic = false;
        }

        if (settings.SettingsSchemaVersion < 4)
        {
            settings.AetherFragment = false;
            if (string.IsNullOrWhiteSpace(settings.AetherScanMode)
                || settings.AetherScanMode.Equals("balanced", StringComparison.OrdinalIgnoreCase))
                settings.AetherScanMode = "turbo";
            if (string.IsNullOrWhiteSpace(settings.AetherScanModeMasque)
                || settings.AetherScanModeMasque.Equals("balanced", StringComparison.OrdinalIgnoreCase))
                settings.AetherScanModeMasque = "turbo";
            if (string.IsNullOrWhiteSpace(settings.AetherScanModeWireguard)
                || settings.AetherScanModeWireguard.Equals("balanced", StringComparison.OrdinalIgnoreCase))
                settings.AetherScanModeWireguard = "turbo";
            if (string.IsNullOrWhiteSpace(settings.AetherScanModeWarp)
                || settings.AetherScanModeWarp.Equals("balanced", StringComparison.OrdinalIgnoreCase))
                settings.AetherScanModeWarp = "turbo";
            if (string.IsNullOrWhiteSpace(settings.AetherNoizeMasque)
                || settings.AetherNoizeMasque.Equals("balanced", StringComparison.OrdinalIgnoreCase))
                settings.AetherNoizeMasque = "firewall";
            if (string.IsNullOrWhiteSpace(settings.AetherNoizeMim)
                || settings.AetherNoizeMim.Equals("balanced", StringComparison.OrdinalIgnoreCase))
                settings.AetherNoizeMim = "firewall";
        }

        if (settings.SettingsSchemaVersion < 5)
        {
            settings.V2RayEnableFragment = false;
        }

        if (settings.SettingsSchemaVersion < 6)
        {
            foreach (var cfg in settings.V2RayConfigs)
            {
                cfg.EnableFragment = null;
            }
        }

        settings.SettingsSchemaVersion = CurrentSettingsSchema;
        _logger?.LogInformation(
            "Applied one-time Aether defaults migration and stamped settings schema v{Version}",
            CurrentSettingsSchema);
        Save();
    }

    public void ResetToDefaults()
    {
        var defaults = new UserSettings { SettingsSchemaVersion = CurrentSettingsSchema };
        foreach (var prop in typeof(UserSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanRead || !prop.CanWrite) continue;
            prop.SetValue(Settings, prop.GetValue(defaults));
        }

        Save();
    }

    public bool Save() => SaveCore(notify: true);

    public bool SaveQuietly() => SaveCore(notify: false);

    private bool SaveCore(bool notify)
    {
        string? json = null;
        for (var attempt = 0; attempt < 5 && json is null; attempt++)
        {
            try
            {
                lock (Settings.SyncRoot)
                {
                    json = JsonSerializer.Serialize(Settings, JsonOpts);
                }
            }
            catch (InvalidOperationException)
            {
                Thread.Sleep(20 * (attempt + 1));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to serialize settings for {Path}", _path);
                return false;
            }
        }

        if (json is null)
        {
            _logger.LogError("Failed to save settings to {Path}: the settings collection kept changing", _path);
            return false;
        }

        lock (_writeLock)
        {
            try
            {
                json = ProtectSecrets(MergeForeignKeys(json));
                var temp = _path + ".tmp";
                File.WriteAllText(temp, json);

                try
                {
                    if (File.Exists(_path)) File.Copy(_path, _backupPath, overwrite: true);
                }
                catch { }

                var moved = false;
                for (var attempt = 0; attempt < 5; attempt++)
                {
                    try
                    {
                        File.Move(temp, _path, overwrite: true);
                        moved = true;
                        break;
                    }
                    catch (IOException)
                    {
                        Thread.Sleep(25 * (attempt + 1));
                    }
                }

                if (!moved)
                {
                    File.WriteAllText(_path, json);
                    try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save settings to {Path}", _path);
                return false;
            }
        }

        if (notify)
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    
    
    
    
    
    
    

    private static readonly string[] SecretKeys =
    {
        "upstreamProxyPassword",
        "lanProxyPassword",
        "firefoxCustomProxyPassword",
        "upstreamProxy",
    };

    private static readonly string[] FoxyTokenKeys =
    {
        "sessionToken",
        "accessToken",
        "refreshToken",
        "proxyPassToken",
    };

    private static void ProtectFoxyAccounts(JsonObject obj, bool protect)
    {
        if (obj["firefoxAccounts"] is not JsonArray accounts) return;

        foreach (var entry in accounts)
        {
            if (entry is not JsonObject account) continue;
            foreach (var key in FoxyTokenKeys)
            {
                try
                {
                    if (account[key] is not JsonValue value || !value.TryGetValue<string>(out var token)
                        || string.IsNullOrEmpty(token)) continue;

                    if (protect)
                    {
                        if (!ProtectedSecret.LooksProtected(token)) account[key] = ProtectedSecret.Protect(token);
                    }
                    else
                    {
                        account[key] = ProtectedSecret.Unprotect(token);
                    }
                }
                catch
                {
                }
            }
        }
    }

    private string ProtectSecrets(string json)
    {
        try
        {
            var obj = JsonNode.Parse(json)?.AsObject();
            if (obj is null) return json;

            foreach (var key in SecretKeys)
            {
                if (obj[key] is JsonValue v && v.TryGetValue<string>(out var s) &&
                    !string.IsNullOrEmpty(s) && !ProtectedSecret.LooksProtected(s))
                {
                    obj[key] = ProtectedSecret.Protect(s);
                }
            }

            
            
            
            if (obj["v2RayConfigs"] is JsonArray configs)
            {
                foreach (var entry in configs)
                {
                    if (entry is JsonObject cfg &&
                        cfg["userId"] is JsonValue cv && cv.TryGetValue<string>(out var id) &&
                        !string.IsNullOrEmpty(id) && !ProtectedSecret.LooksProtected(id))
                    {
                        cfg["userId"] = ProtectedSecret.Protect(id);
                    }
                }
            }

            ProtectFoxyAccounts(obj, protect: true);

            return obj.ToJsonString(JsonOpts);
        }
        catch (Exception ex)
        {
            
            
            _logger.LogWarning(ex, "Could not protect stored secrets; writing them unprotected");
            return json;
        }
    }

    private string UnprotectSecrets(string json)
    {
        try
        {
            var obj = JsonNode.Parse(json)?.AsObject();
            if (obj is null) return json;

            foreach (var key in SecretKeys)
            {
                if (obj[key] is JsonValue v && v.TryGetValue<string>(out var s))
                {
                    obj[key] = ProtectedSecret.Unprotect(s);
                }
            }

            if (obj["v2RayConfigs"] is JsonArray configs)
            {
                foreach (var entry in configs)
                {
                    if (entry is JsonObject cfg &&
                        cfg["userId"] is JsonValue cv && cv.TryGetValue<string>(out var id))
                    {
                        cfg["userId"] = ProtectedSecret.Unprotect(id);
                    }
                }
            }

            ProtectFoxyAccounts(obj, protect: false);

            return obj.ToJsonString(JsonOpts);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not unprotect stored secrets");
            return json;
        }
    }
}
