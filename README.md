# PsiphonUI

A modern, MVVM-based Windows desktop client built on top of the official
[Psiphon 3](https://github.com/Psiphon-Inc/psiphon-windows) network tunnel.
This project provides only the UI / orchestration layer in C# / WPF /
[.NET 8](https://dotnet.microsoft.com/) — it does **not** include any
Psiphon credentials, server lists, or sponsor identifiers. Those are
provided by you (see [Configuration](#configuration) below).

The repo is published as a clean starting point for anyone who wants to
build their own Windows UI around `psiphon-tunnel-core.exe`.

---

## Features

- WPF + [Material Design In XAML Toolkit](https://github.com/MaterialDesignInXAML/MaterialDesignInXamlToolkit)
  (Material Design 3) UI, dark / light / themed palettes
- MVVM with [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet)
- Single-instance enforcement via a named mutex; re-launching the .exe while
  the app is hidden in the tray restores the window via a named
  `EventWaitHandle` signal
- Pages: Home, Settings, Logs, About, IP Scanner
- Optional system-wide tunneling via [Xray-core](https://github.com/XTLS/Xray-core)
  + [wintun](https://www.wintun.net/)
- Optional CDN-fronting helpers (Akamai / Cloudflare / Fastly / Bunny)
- Start with Windows, minimize to tray, allow LAN connections, etc.

---

## Project layout

```
PsiphonUI/
  Assets/                       application icon
  Converters/                   value converters used in XAML
  Models/                       plain DTOs (UserSettings, Notice, …)
  Resources/
    Flags/                      country flag PNGs
    akamai_seed_ips.txt         curated Akamai CDN seed IPs (embedded resource)
    psiphon-tunnel-core.exe     bundled - Psiphon-Labs/psiphon-tunnel-core (GPLv3)
    server_entries.txt          (optional) you provide — Psiphon embedded server list
    xray/
      xray.exe                  bundled - XTLS/Xray-core (MPL-2.0)
      wintun.dll                bundled - wintun.net (GPLv2)
      geosite.dat / geoip.dat   bundled - Loyalsoldier/v2ray-rules-dat
  Services/                     app services (TunnelCoreManager, SettingsService, …)
  Themes/                       ResourceDictionaries for the Material palettes
  ViewModels/                   one VM per page + the main shell VM
  Views/                        .xaml + .xaml.cs (one per page)
  App.xaml / App.xaml.cs        DI host, single-instance, tray glue
  PsiphonUI.csproj
```

---

## Build

Requirements:

- Windows 10 / 11 (x64)
- [.NET SDK 8](https://dotnet.microsoft.com/download/dotnet/8.0)
- Visual Studio 2022 (17.8+) or the `dotnet` CLI

From the repo root:

```bash
dotnet build PsiphonUI\PsiphonUI.csproj -c Release -r win-x64 --self-contained false
```

Or open `PsiphonUI.sln` in Visual Studio 2022 → press F5.

The app starts and shows a connect button, but **it will not actually
connect** until you complete [Configuration](#configuration) below — the
bundled `psiphon-tunnel-core.exe` is real but it has no
`PropagationChannelId` / `SponsorId` to authenticate with.

---

## Configuration

The values that Psiphon needs in order to talk to its network are NOT
shipped in this repo. To make the app connect you have to provide them
yourself, from the official Psiphon distribution or your own Psiphon
network deployment.

### 1. Fill in `EmbeddedValues`

Open `PsiphonUI/Services/EmbeddedValues.cs`. Every constant is currently
a placeholder:

```csharp
public const string PropagationChannelId = "PROPAGATION_CHANNEL_ID";
public const string SponsorId           = "SPONSOR_ID";
// public RSA / Ed25519 keys, fronted URL lists, feedback upload URLs, …
```

Replace each placeholder with the matching value from the official
Psiphon 3 client (look in `psiphon-windows/src/embeddedvalues.h` upstream)
or from your own propagation channel / sponsor configuration. The
property names line up 1:1 with the C++ build's `embeddedvalues.h` and
with `psiphon-tunnel-core`'s `tunnel-core-config.json` schema, so any
upstream `embeddedvalues.h` should drop in cleanly.

> **Important:** never commit real `PropagationChannelId` / `SponsorId`
> / fronted URL lists to a public repository. Keep your real values in
> a private fork or use a build-time secret-injection step.

### 2. (Optional) Provide an embedded server list

The redistributable binaries (`psiphon-tunnel-core.exe`, `xray.exe`,
`wintun.dll`, `geosite.dat`, `geoip.dat`) are already bundled under
`PsiphonUI/Resources/`, so the project builds and runs out of the box.

The only file you might want to add yourself is
`PsiphonUI/Resources/server_entries.txt` — a plain-text list of pre-known
Psiphon servers (one JSON entry per line) used for the initial bootstrap
before tunnel-core can fetch a fresh remote server list. Without it,
tunnel-core will rely entirely on the fronted remote server list URLs
you configured in `EmbeddedValues.cs`.

The `.csproj` uses `Condition="Exists(...)"` for `server_entries.txt`,
so the project still compiles whether or not the file is present.

### 3. (Optional) Rebrand

The csproj `Product` / `Company`, the window titles, the tray text, the
single-instance mutex name, and the registry value used for "Start with
Windows" all currently read `PsiphonUI`. Search for `PsiphonUI` and replace
it with your product name if you publish a downstream build.

---

## Run

```bash
cd PsiphonUI\bin\Release\net8.0-windows10.0.19041.0\win-x64
.\PsiphonUI.exe
```

On first run the app stores user settings under `%LOCALAPPDATA%\Psiphon\`.
Runtime copies of tunnel-core/Xray assets are isolated under
`%LOCALAPPDATA%\PsiphonUI\runtime\` for normal launches, and under
`%PROGRAMDATA%\PsiphonUI\runtime\` when system-wide tunneling runs elevated.

---

## License

The **C# / XAML source code** in this repository is published under the
MIT License — see [LICENSE](LICENSE).

The binaries bundled under `PsiphonUI/Resources/` are **not** MIT-licensed.
They are redistributed unmodified under their original licenses, listed
below with links to the upstream source. If you fork or repackage this
project, you must continue to honor each of those licenses.

| Bundled file                       | Upstream                                                                                       | License |
| ---------------------------------- | ---------------------------------------------------------------------------------------------- | ------- |
| `psiphon-tunnel-core.exe`          | [Psiphon-Labs/psiphon-tunnel-core](https://github.com/Psiphon-Labs/psiphon-tunnel-core)        | GPLv3   |
| `xray/xray.exe`                    | [XTLS/Xray-core](https://github.com/XTLS/Xray-core)                                            | MPL-2.0 |
| `xray/wintun.dll`                  | [wintun.net](https://www.wintun.net/)                                                          | GPLv2   |
| `xray/geosite.dat`, `geoip.dat`    | [Loyalsoldier/v2ray-rules-dat](https://github.com/Loyalsoldier/v2ray-rules-dat)                | per upstream |
