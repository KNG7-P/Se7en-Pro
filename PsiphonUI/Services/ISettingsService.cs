using System;
using PsiphonUI.Models;

namespace PsiphonUI.Services;

public interface ISettingsService
{
    UserSettings Settings { get; }

    void Load();

    void Save();

    event EventHandler? SettingsChanged;
}
