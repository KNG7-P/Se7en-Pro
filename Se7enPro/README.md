﻿# Se7en Pro - Modern Windows client for the Psiphon network (WPF / C#)

A C# / WPF replacement for the legacy MFC + embedded-WebBrowser UI in
`psiclient.exe`. It speaks the same protocol with `psiphon-tunnel-core.exe`
(the same Go binary the official client ships) and the same `embeddedvalues`
extracted from the official Windows installer, so it connects to the same
Psiphon network.

This is a **standalone executable** — it does not require `psiclient.exe`. The
existing C++ project keeps working as-is; this is just a parallel front-end.

## Highlights

- **Material Design 3** styling (MaterialDesignInXamlToolkit 5.x).
- **Custom frameless window** with our own title bar (icon, theme/language
  toggle, min/max/close).
- **Left navigation rail** with four pages:
  - **Home** — big circular Connect button, live status, region, local proxy ports
  - **Settings** — theme, language, egress region, system-proxy toggle, split
    tunnel, upstream proxy
  - **Logs** — live notice viewer with search/filter and copy/clear
  - **About** — version, links to FAQ / privacy
- **MVVM** with CommunityToolkit.Mvvm source generators (`[ObservableProperty]`,
  `[RelayCommand]`).
- **DI** with `Microsoft.Extensions.DependencyInjection`.
- **Persists settings** to `%LOCALAPPDATA%\Psiphon\settings.json`.
- **Sets the Windows system HTTP/HTTPS proxy** automatically on connect (opt-out).
- **Single-instance** enforced via a global mutex.
- **Per-user, asInvoker** UAC — no admin elevation required.

## Build

Requires .NET 8 SDK (Windows or Linux/macOS cross-build via
`EnableWindowsTargeting=true`).

```powershell
cd Se7enPro
dotnet build -c Release -r win-x64 --self-contained false
```

Output: `bin\Release\net8.0-windows10.0.19041.0\win-x64\Se7enPro.exe`.

For a single-file self-contained build (~80 MB, no .NET runtime required):

```powershell
dotnet publish -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

## How it works

1. `App.OnStartup` composes a DI container and applies the persisted theme.
2. `MainWindow` hosts the sidebar + a `ContentControl` whose content is the
   current `PageViewModelBase`.
3. When the user presses **Connect**, `HomeViewModel.ToggleConnectionCommand`
   calls `TunnelCoreManager.StartAsync()`.
4. `TunnelCoreManager`:
   - Builds a `config.json` from `EmbeddedValues` + `UserSettings`.
   - Copies `psiphon-tunnel-core.exe` to a random filename in
     `%LOCALAPPDATA%\Psiphon\tunnel-core\` (avoids name-based blocking).
   - Spawns it with `--config config.json`.
   - Streams stdout/stderr line-by-line, parses each line as a Psiphon
     notice JSON object, and raises events for the UI.
5. On the `Tunnels` notice with `count > 0`, the state flips to **Connected**
   and (if enabled) `SystemProxyService` writes
   `HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings` and
   pokes WinINet via `InternetSetOption`.

## Files of interest

| Path | What it does |
| --- | --- |
| `App.xaml(.cs)` | DI bootstrap, theme/lang init, fatal-exception handler. |
| `Views/MainWindow.xaml` | Frameless shell, title bar, sidebar, page host. |
| `Views/*Page.xaml` | The four pages. |
| `ViewModels/*ViewModel.cs` | MVVM logic for each page + main shell. |
| `Services/TunnelCoreManager.cs` | Owns `psiphon-tunnel-core.exe`. |
| `Services/SystemProxyService.cs` | HKCU + WinINet proxy management. |
| `Services/SettingsService.cs` | JSON-backed user prefs. |
| `Services/EmbeddedValues.cs` | Channel/Sponsor IDs, keys, fronted URLs. |
| `Themes/Colors.xaml` / `Styles.xaml` | Brand colors, button & nav styles. |

## Customization

- **Brand colors**: edit `Themes/Colors.xaml`.
- **Channel/Sponsor IDs**: edit `Services/EmbeddedValues.cs` (keep in sync with
  the C++ `embeddedvalues.h`).
- **Default theme**: change `theme` in the default `UserSettings` or the
  `BundledTheme` in `App.xaml`.

## Known limitations

- The Windows system-tray icon and "minimize to tray" toggle are present in
  settings but the tray icon itself is not yet wired up — first iteration
  focuses on the main window.
- Language toggle requires an app restart for the RTL flip to take full effect.
- Auto-start with Windows is a setting toggle only; the registry write to
  `Run` is not implemented in this first iteration.

These are explicitly noted in the UI tooltips and are scoped for follow-up
work.
