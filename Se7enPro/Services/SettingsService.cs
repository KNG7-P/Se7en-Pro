using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
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

    public UserSettings Settings { get; private set; } = new();

    public event EventHandler? SettingsChanged;

    public SettingsService(ILogger<SettingsService> logger)
    {
        _logger = logger;
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dir = Path.Combine(localAppData, "Se7en");
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
                Settings = new UserSettings();
                _foreignKeys = null;
                Save();
                return;
            }

            var json = File.ReadAllText(_path);
            Settings = JsonSerializer.Deserialize<UserSettings>(UnprotectSecrets(json), JsonOpts)
                       ?? new UserSettings();
            CaptureForeignKeys(json);
            MigrateRemovedAutoMethod();
        }
        catch (Exception ex)
        {
            
            
            
            
            _logger.LogWarning(ex, "Failed to load settings from {Path}; trying the last good copy", _path);
            try
            {
                var rescue = File.ReadAllText(_backupPath);
                Settings = JsonSerializer.Deserialize<UserSettings>(UnprotectSecrets(rescue), JsonOpts)
                           ?? new UserSettings();
                CaptureForeignKeys(rescue);
                MigrateRemovedAutoMethod();
                return;
            }
            catch (Exception rescueEx)
            {
                _logger.LogWarning(rescueEx, "Last good settings copy unusable; falling back to defaults");
            }

            Settings = new UserSettings();
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

    public void Save()
    {
        
        
        
        
        lock (_writeLock)
        {
            try
            {
                var json = MergeForeignKeys(JsonSerializer.Serialize(Settings, JsonOpts));
                json = ProtectSecrets(json);
                var temp = _path + ".tmp";
                File.WriteAllText(temp, json);
                if (File.Exists(_path)) File.Copy(_path, _backupPath, overwrite: true);
                File.Move(temp, _path, overwrite: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save settings to {Path}", _path);
                return;
            }
        }

        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    
    
    
    
    
    
    

    private static readonly string[] SecretKeys =
    {
        "upstreamProxyPassword",
        "lanProxyPassword",
    };

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

            return obj.ToJsonString(JsonOpts);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not unprotect stored secrets");
            return json;
        }
    }
}
