using System;
using System.IO;
using System.Text.Json;
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

    public UserSettings Settings { get; private set; } = new();

    public event EventHandler? SettingsChanged;

    public SettingsService(ILogger<SettingsService> logger)
    {
        _logger = logger;
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dir = Path.Combine(localAppData, "Se7en");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "settings.json");

        if (!File.Exists(_path))
        {
            var legacyPath = Path.Combine(localAppData, "Psiphon", "settings.json");
            if (File.Exists(legacyPath))
            {
                try { File.Copy(legacyPath, _path, overwrite: false); } catch { }
            }
        }
    }

    public void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                Settings = new UserSettings();
                Save();
                return;
            }

            var json = File.ReadAllText(_path);
            Settings = JsonSerializer.Deserialize<UserSettings>(json, JsonOpts) ?? new UserSettings();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load settings from {Path}; using defaults", _path);
            Settings = new UserSettings();
        }
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(Settings, JsonOpts);
            File.WriteAllText(_path, json);
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save settings to {Path}", _path);
        }
    }
}
