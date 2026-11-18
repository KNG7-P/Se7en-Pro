using System;
using Se7enPro.Models;

namespace Se7enPro.Services;

public interface ISettingsService
{
    UserSettings Settings { get; }

    void Load();

    bool Save();

    bool SaveQuietly();

    void ResetToDefaults();

    event EventHandler? SettingsChanged;
}
