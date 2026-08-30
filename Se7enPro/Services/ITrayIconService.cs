using System;
using Se7enPro.Models;

namespace Se7enPro.Services;

public interface ITrayIconService : IDisposable
{
    void Initialize();
    void ShowWindow();
    void HideToTray();
    bool IsHidden { get; }
    event EventHandler? RequestShow;
    event EventHandler? RequestExit;
    event EventHandler? RequestToggleConnection;

    void UpdateConnectionState(ConnectionState state);
}
