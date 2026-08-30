using System;
using Se7enPro.Models;

namespace Se7enPro.Services;

public interface ISettingsService
{
    UserSettings Settings { get; }

    void Load();

    void Save();

    event EventHandler? SettingsChanged;
}
