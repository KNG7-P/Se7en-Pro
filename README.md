<div align="center">

<img src="Se7enPro/Assets/app-icon.png" width="128" alt="Se7en Pro logo">

# Se7en Pro

**Multi-protocol Windows client and anti-censorship suite**

[![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011%20(x86%20%7C%20x64)-blue.svg?style=flat-square)](https://microsoft.com/windows)
[![Framework](https://img.shields.io/badge/.NET-8.0-purple.svg?style=flat-square)](https://dotnet.microsoft.com/)
[![UI](https://img.shields.io/badge/UI-WPF%20%7C%20Material%20Design%203-informational.svg?style=flat-square)](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/)
[![Version](https://img.shields.io/badge/Version-v1.0.5-orange.svg?style=flat-square)](https://github.com/KNG7-P/Se7en-Pro/releases)
[![License](https://img.shields.io/badge/License-MIT-green.svg?style=flat-square)](LICENSE)

</div>

---

## Architecture

```
┌──────────────────────────────────────────────────────────────┐
│                      Se7enPro.exe                            │
│        WPF · Material Design 3 · MVVM · tray · telemetry    │
└───────────────────────────┬──────────────────────────────────┘
                            │
        ┌───────────────────┴───────────────────┐
        │        ConnectionManager              │
        │   per-process supervision, routing    │
        └───┬──────────┬──────────┬─────────┬───┘
            │          │          │         │
        ┌───▼──┐   ┌───▼──┐   ┌───▼───┐ ┌───▼────┐
        │Aether│   │Psiphon   │ │ Tor   │ │ Xray / │
        │WARP &│   │tunnel-   │ │onion  │ │Sing-box│
        │MASQUE│   │core.exe  │ │ mesh  │ │(VLESS, │
        │      │   │          │ │       │ │VMess…) │
        └───┬──┘   └────┬─────┘ └───┬───┘ └───┬────┘
            │           │           │         │
            └───────────┴─────┬─────┴─────────┘
                              │
                  ┌───────────▼────────────┐
                  │ wintun.dll + tun2socks │
                  │ system-wide TUN, DNS    │
                  └─────────────────────────┘
```

`Se7enPro/` is one self-contained WPF process. It drives the bundled upstream
binaries in `Se7enPro/Resources/`, captures traffic through Wintun + tun2socks,
and applies the split-tunnel, DNS and kill-switch policy around it.

## Protocols

| Engine | What it does |
| :--- | :--- |
| **Aether** | MASQUE over HTTP/3 QUIC and HTTP/2 TCP, WireGuard WARP, Warp-on-Warp |
| **SHARD** | TLS ClientHello fragmentation and packet shredding to defeat DPI, with node-pool discovery |
| **Psiphon** | `psiphon-tunnel-core.exe` with CDN fronting (Akamai, Fastly, Cloudflare), custom SNI and clean edge-IP dials |
| **Tor** | Native `tor.exe` with Lyrebird pluggable transports (obfs4, Snowflake, WebTunnel, Conjure) and country-restricted egress |
| **Xray / Sing-box** | VLESS, VMess, Trojan and Shadowsocks inbound configs with TLS fragmenting and multiplexing |

Chained multi-hop tunnels are supported: Psiphon over WARP, Tor over WARP,
Psiphon over V2Ray and Tor over V2Ray.

## Features

- **System-wide TUN** through Wintun + tun2socks, with no DNS leak.
- **Split tunnelling** by application or by domain, with DNS interception that
  follows the same include/exclude rules.
- **Kill switch** that keeps the routes closed when a tunnel dies unexpectedly.
- **LAN sharing** with optional credentials, and an upstream proxy for the
  whole chain.
- **Live telemetry** for throughput, latency, session counters and duration.
- **Searchable log console** with categories and filters.
- **Three languages**: English, Russian and Chinese.

## Repository layout

```
Se7enPro/            the WPF client (this is the whole application)
  Services/          engines, TUN, IPC-free daemon services
  Services/Tun/      Wintun + tun2socks + DNS forwarder, split into partials
  ViewModels/        MVVM view models
  Views/             pages and dialogs
  Themes/            Material Design 3 palette and styles
  Resources/         bundled upstream binaries, GeoIP data, flags, fonts
installer/           Inno Setup scripts (x64, x86)
tools/               build pipeline, secret injector, TUN test harness
.github/workflows/   CI and release automation
```

## Building from source

See **[BUILDING.md](BUILDING.md)** for the full guide. The short version:

```powershell
dotnet build Se7enPro\Se7enPro.csproj -c Release -r win-x64
```

That builds a working client with placeholder Psiphon configuration. Supplying
real channel values is a build-time step driven by environment variables, so no
secret is ever committed — see [BUILDING.md](BUILDING.md#psiphon-configuration).

## Secrets

This repository contains **no** Psiphon channel values, no server list and no
device keys. `Se7enPro/Services/EmbeddedValues.cs` compiles against
placeholders unless a build injects real ones:

```
Services/BuildSecrets.g.cs   generated, AES-256-GCM ciphertext, git-ignored
                              present  -> the csproj defines SE7EN_SECRETS
                              absent   -> the placeholder branch is used
```

The release workflow fills the values from GitHub Actions secrets, encrypts
them, builds, and then scans every produced archive to confirm no secret
reached an asset in plaintext.

## Bundled third-party binaries

Source code in this repository is MIT licensed (see [LICENSE](LICENSE)).
Bundled binaries under `Se7enPro/Resources/` keep their upstream licenses:

| Component | Upstream | License |
| :--- | :--- | :--- |
| `psiphon-tunnel-core.exe` | [Psiphon-Labs/psiphon-tunnel-core](https://github.com/Psiphon-Labs/psiphon-tunnel-core) | GPLv3 |
| `tun2socks.exe` | [xjasonlyu/tun2socks](https://github.com/xjasonlyu/tun2socks) | GPLv3 |
| `wintun.dll` | [wintun.net](https://www.wintun.net/) | GPLv2 |
| `tor.exe`, `geoip`, `geoip6` | [The Tor Project](https://www.torproject.org/) | BSD-3-Clause |
| `lyrebird.exe`, `conjure-client.exe` | [Tor Pluggable Transports](https://gitlab.torproject.org/tpo/anti-censorship/pluggable-transports/lyrebird) | BSD-3-Clause |
| `xray.exe` | [XTLS/Xray-core](https://github.com/XTLS/Xray-core) | MPL-2.0 |
| `sing-box.exe` | [SagerNet/sing-box](https://github.com/SagerNet/sing-box) | GPLv3 |
| Inter, JetBrains Mono | [rsms/inter](https://github.com/rsms/inter), [JetBrains/JetBrainsMono](https://github.com/JetBrains/JetBrainsMono) | OFL-1.1 |

## Acknowledgements

- The chained-transport architecture (Psiphon/Tor over WARP) and the SHARD
  fragmentation method are inspired by the Android
  [MSN-GUARD](https://github.com/mbm110/MSN-GUARD) project by
  [mbm110](https://github.com/mbm110).
- Upstream projects: Psiphon-Labs, The Tor Project, Wintun, xjasonlyu,
  XTLS/Xray-core, SagerNet/sing-box and the .NET WPF stack.

---

<div align="center">
  <i>Developed for freedom of access, resilience, and modern circumvention.</i>
</div>