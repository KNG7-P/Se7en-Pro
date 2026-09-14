using System;
using System.IO;
using System.Text;
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

    private readonly object _writeLock = new();

    private string BackupPath => _path + ".bak";

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
            var settings = TryRead(_path);

            settings ??= TryRead(BackupPath);

            if (settings is null)
            {
                var fresh = !File.Exists(_path) && !File.Exists(BackupPath);
                if (!fresh)
                {
                    _logger.LogWarning(
                        "settings.json and its backup are unreadable; keeping files and using defaults in-memory");
                    try
                    {
                        var corruptPath = _path + ".corrupt." + DateTime.UtcNow.Ticks;
                        if (File.Exists(_path)) File.Copy(_path, corruptPath, true);
                    }
                    catch { }
                }
                Settings = new UserSettings();
                if (fresh)
                {
                    Save();
                }
                return;
            }

            Settings = settings;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load settings from {Path}; using defaults", _path);
            Settings = new UserSettings();
        }
    }

    private UserSettings? TryRead(string path)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return null;
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs, Encoding.UTF8);
                var json = sr.ReadToEnd();
                if (string.IsNullOrWhiteSpace(json)) return null;
                return JsonSerializer.Deserialize<UserSettings>(json, JsonOpts);
            }
            catch (Exception ex) when (attempt < 2)
            {
                _logger.LogWarning(ex, "settings file {Path} read attempt {Attempt} failed; retrying", path, attempt);
                System.Threading.Thread.Sleep(50);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "settings file {Path} could not be read", path);
                return null;
            }
        }
        return null;
    }

    public void Save()
    {
        try
        {

            var json = JsonSerializer.Serialize(Settings, JsonOpts);
            lock (_writeLock)
            {
                WriteAtomic(json);
            }
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save settings to {Path}", _path);
        }
    }

    private void WriteAtomic(string json)
    {
        var tmp = _path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(json);
            fs.Write(bytes, 0, bytes.Length);

            fs.Flush(flushToDisk: true);
        }

        if (File.Exists(_path))
        {

            File.Replace(tmp, _path, BackupPath, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(tmp, _path);
        }
    }

    public void Update(UserSettings updated)
    {
        if (updated == null) return;
        Settings = updated;
        Save();
    }
}
