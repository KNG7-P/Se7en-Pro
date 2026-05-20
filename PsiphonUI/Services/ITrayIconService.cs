using System;

namespace PsiphonUI.Services;

public interface ITrayIconService : IDisposable
{
    void Initialize();
    void ShowWindow();
    void HideToTray();
    bool IsHidden { get; }
    event EventHandler? RequestShow;
    event EventHandler? RequestExit;
}
