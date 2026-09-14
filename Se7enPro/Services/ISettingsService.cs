using System;
using Se7enPro.Models;

namespace Se7enPro.Services;

public interface ISettingsService
{
    UserSettings Settings { get; }

    void Load();

    void Save();

    void Update(UserSettings updated);

    event EventHandler? SettingsChanged;
}
