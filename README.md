<div align="center">

<img src="Se7enPro/Assets/app-icon.png" width="130" alt="Se7en Pro Logo">

# 🛡️ Se7en Pro

**Modern WPF Multi-Protocol Windows Client & Anti-Censorship Suite**

[![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011%20(x86%20%7C%20x64)-blue.svg?style=flat-square)](https://microsoft.com/windows)
[![Framework](https://img.shields.io/badge/Framework-WPF%20%7C%20.NET%208.0-purple.svg?style=flat-square)](https://dotnet.microsoft.com/)
[![UI](https://img.shields.io/badge/UI-Material%20Design%203-informational.svg?style=flat-square)](https://github.com/MaterialDesignInXAML/MaterialDesignInXamlToolkit)
[![Release](https://img.shields.io/badge/Release-Passing-brightgreen.svg?style=flat-square)](https://github.com/KNG7-P/Se7en-Pro/actions/workflows/release.yml)
[![Version](https://img.shields.io/badge/Version-v1.0.8-orange.svg?style=flat-square)](https://github.com/KNG7-P/Se7en-Pro/releases)
[![License](https://img.shields.io/badge/License-MIT-green.svg?style=flat-square)](LICENSE)

[🇬🇧 English](#-english) | [🇮🇷 فارسی](#-فارسی) | [🇷🇺 Русский](#-русский) | [🇨🇳 中文](#-中文)

</div>

---

## 🏗️ Architecture

```
┌────────────────────────────────────────────────────────────────────────┐
│                              Se7enPro.exe                              │
│         Single-Process Native WPF (.NET 8) · Material Design 3         │
│               MVVM · Telemetry · Routing · Responsive Tray             │
├───────────────────┬───────────────────┬────────────────┬───────────────┤
│   Aether (2.4.0)  │  Foxy · Lantern   │  Psiphon · Tor │     SHARD     │
│  (MASQUE HTTP/3 / │      · Veep       │  (TunnelCore / │ (TLS Frag /   │
│  WireGuard Warp)  │  (H2 · Built-in)  │   Lyrebird PT) │  MTU Scanner) │
├───────────────────┴───────────────────┴────────────────┴───────────────┤
│                     Sing-box & Xray Multi-Hop Hub                      │
│        (VLESS · VMess · Trojan · Shadowsocks · Chained Tunnels)        │
├────────────────────────────────────────────────────────────────────────┤
│                         Wintun TUN & Multi-Core                        │
│     (Zeptun · Mihomo · tun2socks · Per-App Split · Zero Leak)          │
└────────────────────────────────────────────────────────────────────────┘
```

---

## 🇬🇧 English

### 📌 About The Project
**Se7en Pro** is an open-source, next-generation Windows anti-censorship orchestrator and multi-protocol VPN suite built on a modern, high-performance **single-process native WPF (.NET 8)** architecture featuring **Material Design 3**.

Engineered for extreme performance, security, and resilience, Se7en Pro features near-instant startup via **ReadyToRun (PGO)** pre-compilation, fluid 60fps animations, and minimal memory footprint.

Se7en Pro unifies state-of-the-art circumvention protocols — WireGuard Warp & Aether (v2.4.0 MASQUE HTTP/3 QUIC & HTTP/2), Foxy (Firefox VPN HTTP/2), Lantern, Veep, TLS ClientHello packet shredding (SHARD), Psiphon Network with CDN fronting, Tor Onion mesh with pluggable transports, and Xray/Sing-box chained multi-hop transports — into an intuitive, transparent desktop client.

The repository follows strict security decoupling: **no private credentials, server lists, or sponsor keys are stored in source code or Git history**. Build secrets are injected securely via GitHub Actions Secrets using AES-256-GCM encryption, allowing forks and public checkouts to build cleanly with safe placeholders.

---

### ✨ Core Features & Supported Protocols

#### 🌐 1. Supported Protocols & Multi-Engine Hub
* **WireGuard Warp & Aether Core (v2.4.0)**: High-speed kernel-level WireGuard Warp (primary protocol) alongside advanced MASQUE transport supporting HTTP/3 (QUIC over UDP) and HTTP/2 (TCP) with automatic fallback, Warp-on-Warp chaining, standalone WARP key acquisition through any active app protocol, and live MTU scanning/tuning.
* **Foxy (Firefox VPN HTTP/2)**: Fastly MASQUE HTTP/2 tunnel with HelloSplit TLS bypass, multi-account management with automatic quota failover, and direct in-app sign-in without requiring a separate VPN.
* **Lantern & Veep**: Built-in Lantern and Veep protocol engines with automated server health selection and resilient failover.
* **SHARD Engine**: Advanced TLS ClientHello fragmentation and IP-level packet shredding to defeat Deep Packet Inspection (DPI), with automatic latency-based node discovery and MTU configuration/scanner.
* **Psiphon Network**: Hardened `psiphon-tunnel-core` integration with multi-CDN fronting (Akamai, Fastly, Cloudflare), custom SNI override, and clean edge-IP dials.
* **Tor Expert Bundle**: Native Tor daemon integration with official Pluggable Transports (**Lyrebird** for obfs4 / Snowflake / WebTunnel / Conjure), strict country egress routing, and onion mesh circuits.
* **Sing-box & Xray Engines**: Native VLESS, VMess, Trojan, and Shadowsocks inbound support with TLS fragmenting and multiplexing.
* **🔗 Expanded Multi-Hop Chained Tunnels**:
  - *Psiphon over WARP*, *Psiphon over Foxy*, *Psiphon over Lantern*, & *Psiphon over V2Ray*
  - *Tor over WARP* & *Tor over V2Ray*
  - *Lantern over WARP* & *Veep over WARP*

#### ⚡ 2. High-Performance Multi-Core Wintun TUN & Routing
* **Multi-Core TUN Routing**: Seamless system-wide capture using `wintun.dll` powered by **`Zeptun`**, **`Mihomo`**, or **`tun2socks`** with zero DNS leaks.
* **Profile-Based Split Tunneling**: Every profile owns three fully independent lists — **Bypass** (direct), **Through Tunnel** (proxy) and **Block** (no network) — plus a default outbound for everything unmatched. Apps, domains and IP ranges are assigned to exactly one mode; moving one is always an explicit remove-and-add, so nothing is ever dragged between modes. Switching profiles switches the whole routing set.
* **Enforced per Mode**: Through Tunnel is owned by the TUN catch-all, Bypass cancels the redirect (or is route-pinned when WFP redirect is unavailable), and Block is dropped at the WFP connect **and datagram** layers so an open UDP socket cannot keep talking. mihomo TUN mode emits the same three lists as native process/domain/IP rules.
* **Kill Switch & Route Isolation**: Prevents IP leakage upon unexpected connection termination.
* **LAN Sharing**: Optional local network proxy sharing with customizable authentication.

#### 🎨 3. Modern Material Design 3 Interface & Performance
* **Single-Process WPF Client**: Fluid 60fps animations, optimized hot paths, zero overhead.
* **ReadyToRun Startup Acceleration**: Eliminates startup delays with ahead-of-time IL-to-native compilation.
* **Multi-Language Support**: Fully localized in **English**, **Russian (Русский)**, and **Chinese (中文)**.
* **Live Telemetry & Logs**: Real-time throughput graphs, round-trip latency, session counters, and searchable categorized log console.

---

### 💡 Acknowledgements & Credits

* **Chained Tunneling & SHARD Methods**: The chained transport architecture (*Psiphon/Tor over WARP*) and the **SHARD fragmentation method** are inspired by and credited to the Android [MSN-GUARD](https://github.com/mbm110/MSN-GUARD) project by **[mbm110](https://github.com/mbm110)**.
* **Core Upstreams**:
  - [Psiphon-Labs/psiphon-tunnel-core](https://github.com/Psiphon-Labs/psiphon-tunnel-core)
  - [The Tor Project](https://www.torproject.org/)
  - [Wintun](https://www.wintun.net/), [Noisemux/zeptun](https://github.com/Noisemux/zeptun), [xjasonlyu/tun2socks](https://github.com/xjasonlyu/tun2socks), & [MetaCubeX/mihomo](https://github.com/MetaCubeX/mihomo)
  - [XTLS/Xray-core](https://github.com/XTLS/Xray-core) & [SagerNet/sing-box](https://github.com/SagerNet/sing-box)

---

### ⚙️ Configuration & Secrets Management

The repository is safe to fork and publish. To inject private Psiphon network credentials at build time:
1. Define the 10 standard environment variables (or GitHub Repository Secrets):
   `SE7EN_PROPAGATION_CHANNEL_ID`, `SE7EN_SPONSOR_ID`, `SE7EN_CLIENT_VERSION`, `SE7EN_CLIENT_PLATFORM`, public keys, and endpoint JSONs.
2. Run `python tools/generate_build_secrets.py`.
3. The script generates an AES-256-GCM encrypted `BuildSecrets.g.cs` (git-ignored) which is compiled into the binary.

---

### 🛠️ Building from Source

**Requirements**:
* Windows 10 / 11 (x86 / x64)
* [.NET SDK 8.0](https://dotnet.microsoft.com/download/dotnet/8.0)
* Inno Setup 6 (optional, for installer builds)

```powershell
# 1. Quick Developer Build (Debug/Release)
dotnet build Se7enPro\Se7enPro.csproj -c Release -r win-x64

# 2. Complete Distribution Suite (Installers & Portables with/without bundled .NET for x64 and x86)
powershell -ExecutionPolicy Bypass -File .\tools\build-all.ps1 -Arch all -AllVariants -OutDir dist
```

---

### 📄 Licensing & Bundled Components

The **source code** in this repository is licensed under the [MIT License](LICENSE). Third-party binaries bundled under `Se7enPro/Resources/` retain their respective upstream licenses:

| Component | Upstream Project | License |
| :--- | :--- | :--- |
| `psiphon-tunnel-core.exe` | [Psiphon-Labs/psiphon-tunnel-core](https://github.com/Psiphon-Labs/psiphon-tunnel-core) | GPLv3 |
| `zeptun.exe` | [Noisemux/zeptun](https://github.com/Noisemux/zeptun) | MIT |
| `tun2socks.exe` | [xjasonlyu/tun2socks](https://github.com/xjasonlyu/tun2socks) | GPLv3 |
| `mihomo.exe` | [MetaCubeX/mihomo](https://github.com/MetaCubeX/mihomo) | GPLv3 |
| `wintun.dll` | [wintun.net](https://www.wintun.net/) | GPLv2 |
| `tor.exe`, `geoip`, `geoip6` | [The Tor Project](https://www.torproject.org/) | BSD-3-Clause |
| `lyrebird.exe`, `conjure-client.exe` | [Tor Pluggable Transports](https://gitlab.torproject.org/tpo/anti-censorship/pluggable-transports/lyrebird) | BSD-3-Clause |
| `xray.exe` | [XTLS/Xray-core](https://github.com/XTLS/Xray-core) | MPL-2.0 |
| `sing-box.exe` | [SagerNet/sing-box](https://github.com/SagerNet/sing-box) | GPLv3 |

---

## 🇮🇷 فارسی

### 📌 درباره پروژه
**سون پرو (Se7en Pro)** یک نرم‌افزار متن‌باز، نوین و بسیار قدرتمند برای عبور از سانسور اینترنت و مدیریت اتصالات در سیستم‌عامل ویندوز است که بر پایه معماری بهینه‌شده **تک‌پروسس بومی WPF (.NET 8)** و طراحی مدرن **Material Design 3** پیاده‌سازی شده است.

این نرم‌افزار با بهره‌گیری از تکنولوژی پیش‌کامپایل **ReadyToRun (PGO)**، شروع سریع و مصرف بهینه حافظه رم و پردازنده، تجربه‌ای روان و پایدار ارائه می‌دهد.

سون پرو مجموعه‌ای از پیشرفته‌ترین پروتکل‌های ارتباطی نظیر WireGuard Warp و هسته Aether (نسخه 2.4.0 با پشتیبانی از MASQUE HTTP/3 QUIC و HTTP/2)، متد Foxy (Firefox VPN HTTP/2)، متدهای Lantern و Veep، فناوری خردسازی بسته‌های TLS (پروتکل SHARD)، هسته ارتقایافته سایفون، شبکه تور و ترانزیت‌های چندمرحله‌ای Sing-box و Xray را در قالب کلاینتی یکپارچه ارائه می‌دهد.

---

### ✨ قابلیت‌های کلیدی و معماری پروژه

#### 🌐 ۱. پشتیبانی از پروتکل‌ها و هسته‌های ارتباطی
* **پروتکل WireGuard Warp و هسته Aether (نسخه 2.4.0)**: اتصال پرسرعت و پایدار در سطح کرنل سیستم‌عامل همراه با پشتیبانی پیشرفته از **MASQUE** بر بستر HTTP/3 (QUIC over UDP) و HTTP/2 (TCP)، سوییچ خودکار، Warp-on-Warp، دریافت مستقل کلید WARP از طریق پروتکل‌های فعال برنامه و تنظیم/اسکنر زنده MTU.
* **متد Foxy (Firefox VPN HTTP/2)**: تانل Fastly MASQUE HTTP/2 همراه با بایپس TLS به‌کمک HelloSplit، مدیریت چنداکانت با سوییچ خودکار سهمیه و ورود مستقیم درون‌برنامه‌ای بدون نیاز به VPN جداگانه.
* **متدهای Lantern و Veep**: موتورهای داخلی Lantern و Veep با انتخاب خودکار سرورهای سالم و پایداری بالا.
* **پروتکل قدرتمند SHARD**: فرگمنت پیشرفته بسته‌های TLS ClientHello و تکه‌تکه‌کردن ترافیک برای دورزدن فیلترینگ عمیق (DPI) همراه با اسکنر خودکار نزدیک‌ترین آی‌پی‌های لبه و تنظیم/اسکنر MTU.
* **شبکه سایفون (Psiphon Core)**: هسته اختصاصی سایفون با پشتیبانی کامل از CDN Fronting (سرویس‌های Akamai، Fastly و Cloudflare) و امکان وارد کردن SNI و IP تمیز.
* **شبکه تور (Tor Expert Bundle)**: ادغام کامل هسته Tor با ترنسپورت‌های رسمی Lyrebird (پل‌های obfs4، Snowflake، WebTunnel و Conjure)، قابلیت انتخاب کشور خروجی و ارتباط شبکه‌ای پیازی.
* **هسته‌های Sing-box و Xray**: پشتیبانی کامل از کانفیگ‌های VLESS، VMess، Trojan و Shadowsocks با قابلیت مالتی‌پلکسینگ و فرگمنت.
* **🔗 اتصالات زنجیره‌ای چندمرحله‌ای (Chained)**:
  - *سایفون روی وارپ*، *سایفون روی Foxy*، *سایفون روی Lantern* و *سایفون روی V2Ray*
  - *تور روی وارپ* و *تور روی V2Ray*
  - *Lantern روی وارپ* و *Veep روی وارپ*

#### ⚡ ۲. موتور پرسرعت چند‌هسته‌ای Wintun TUN و تفکیک ترافیک
* **تونل سیستم در سطح هسته**: بدون کوچک‌ترین نشت DNS با تکیه بر درایور `wintun.dll` و انتخاب بین هسته‌های **`Zeptun`**، **`Mihomo`** و **`tun2socks`**.
* **اسپلیت تانل برنامه‌ها و سایت‌ها**: تفکیک ترافیک بر اساس نرم‌افزارهای انتخابی یا لیست دامنه‌های خاص (Whitelist / Blacklist) با رهگیری آنی DNS.
* **کیل سوییچ و اشتراک LAN**: جلوگیری فوری از نشت IP در صورت قطع ناگهانی تانل و امکان اشتراک‌گذاری پروکسی در شبکه محلی.

#### 🎨 ۳. رابط کاربری مدرن Material Design 3 و عملکرد
* **کلاینت بومی و سبک**: انیمیشن‌های روان، مصرف بهینه و سرعت پاسخ‌دهی بالا.
* **استارتاپ سریع**: بهینه‌سازی بارگذاری اولیه با کامپایل Native از طریق PublishReadyToRun.
* **پشتیبانی از ۳ زبان در برنامه**: ترجمه کامل به زبان‌های **انگلیسی**، **روسی (Русский)** و **چینی (中文)**.
* **کنسول لاگ زنده و تله‌متری**: مانیتورینگ زنده سرعت ارسال/دریافت، پینگ، آمار داده‌های مصرفی و ترمینال لاگ دسته‌بندی‌شده.

---

### 💡 قدردانی و کردیت‌ها (Credits)

* **ایده و متد اتصال زنجیره‌ای و متد SHARD**: ایده اتصالات زنجیره‌ای (*سایفون/تور روی وارپ*) و همچنین **متد فرگمنت SHARD** برگرفته و الهام‌گرفته‌شده از پروژه اندرویدی ارزشمند [MSN-GUARD](https://github.com/mbm110/MSN-GUARD) توسعه‌داده‌شده توسط **[mbm110](https://github.com/mbm110)** است.
* **پروژه‌های مبدأ**: Psiphon-Labs، The Tor Project، Wintun، [Zeptun](https://github.com/Noisemux/zeptun)، Mihomo، Xray-core، Sing-box و MaterialDesignInXaml.

---

### 🛠️ نحوه بیلد و کامپایل

**پیش‌نیازها**:
* ویندوز 10 یا 11 (نسخه‌های 32 و 64 بیتی)
* [.NET SDK 8.0](https://dotnet.microsoft.com/download/dotnet/8.0)
* Inno Setup 6 (اختیاری، جهت ساخت فایل‌های Setup)

```powershell
# ۱. بیلد سریع سورس کد (نسخه 64 بیتی)
dotnet build Se7enPro\Se7enPro.csproj -c Release -r win-x64

# ۲. ساخت پکیج‌های کامل رسمی (شامل فایل‌های نصبی و پرتابل ۳۲ و ۶۴ بیتی با و بدون دات‌نت)
powershell -ExecutionPolicy Bypass -File .\tools\build-all.ps1 -Arch all -AllVariants -OutDir dist
```

---

### 📄 لایسنس

کدهای اختصاصی این مخزن تحت مجوز [MIT License](LICENSE) منتشر شده‌اند. باینری‌های جانبی موجود در پوشه `Se7enPro/Resources/` تحت لایسنس‌های رسمی پروژه‌های مبدأ بازنشر می‌شوند.

---

## 🇷🇺 Русский

### 📌 О проекте
**Se7en Pro** — это современный инструмент с открытым исходным кодом для обхода интернет-цензуры и универсальный VPN-клиент для Windows на базе нативной **однопроцессной архитектуры WPF (.NET 8)** с дизайном **Material Design 3**.

Приложение спроектировано для обеспечения максимальной скорости, безопасности и стабильности, обладает быстрым запуском за счет предварительной компиляции **ReadyToRun (PGO)** и минимальным потреблением системных ресурсов.

Клиент объединяет передовые протоколы маскировки: WireGuard Warp и Aether Core (v2.4.0 MASQUE HTTP/3 QUIC и HTTP/2), Foxy (Firefox VPN HTTP/2), Lantern, Veep, фрагментацию пакетов TLS ClientHello (SHARD), сеть Psiphon с CDN Fronting, сеть Tor с подключаемыми транспортами, а также многозвенные цепочки Sing-box и Xray.

В репозитории соблюдены строгие стандарты безопасности: **никаких приватных ключей, списков серверов или спонсорских данных в кодовой базе или истории Git**. Секреты внедряются на этапе сборки через GitHub Actions Secrets с шифрованием **AES-256-GCM**.

---

### ✨ Ключевые возможности и протоколы

#### 🌐 1. Многопротокольный хаб
* **WireGuard Warp и ядро Aether (v2.4.0)**: Высокоскоростной протокол на уровне ядра ОС, поддержка MASQUE (HTTP/3 QUIC over UDP и HTTP/2 TCP) с автоматическим откатом, цепочки Warp-on-Warp, автономное получение ключей WARP через активные протоколы приложения и сканер/настройка MTU.
* **Foxy (Firefox VPN HTTP/2)**: Туннель Fastly MASQUE HTTP/2 с обходом TLS через HelloSplit, поддержкой нескольких аккаунтов с автопереключением квот и прямым входом внутри приложения без отдельного VPN.
* **Lantern и Veep**: Встроенные движки протоколов Lantern и Veep с автоматическим выбором рабочих серверов.
* **Движок SHARD**: Фрагментация пакетов TLS ClientHello и разделение TCP-потоков для обхода DPI, а также настройка и сканирование MTU.
* **Сеть Psiphon**: Полноценная интеграция `psiphon-tunnel-core` с поддержкой CDN Fronting (Akamai, Fastly, Cloudflare) и пула чистых IP.
* **Пакет Tor Expert Bundle**: Нативная интеграция Tor с транспортами **Lyrebird** (obfs4, Snowflake, WebTunnel, Conjure) и выбором страны выхода.
* **Ядра Sing-box и Xray**: Поддержка VLESS, VMess, Trojan и Shadowsocks с фрагментацией и мультиплексированием.
* **🔗 Расширенные цепочки туннелей**: Psiphon поверх WARP / Foxy / Lantern / V2Ray, Tor поверх WARP / V2Ray, а также Lantern и Veep поверх WARP.

#### ⚡ 2. Высокопроизводительный Wintun TUN (Multi-Core)
* **Захват трафика на уровне ядра**: Полная изоляция через `wintun.dll` с выбором движка (**`Zeptun`**, **`Mihomo`** или **`tun2socks`**) без утечек DNS.
* **Раздельное туннелирование по профилям**: В каждом профиле три независимых списка — **в обход** (напрямую), **через туннель** (прокси) и **блокировка** (без сети) — плюс маршрут по умолчанию для остального трафика. Приложения, домены и IP-диапазоны принадлежат ровно одному режиму; перенос — это всегда явное удаление и добавление, поэтому правила не перетаскиваются между режимами.
* **Kill Switch и LAN**: Мгновенная блокировка утечки IP при сбое и возможность раздачи прокси в локальную сеть.

---

### 💡 Благодарности и источники

* **Архитектура цепочек и метод SHARD**: Архитектура цепочек туннелей (*Psiphon/Tor поверх WARP*), а также **метод фрагментации SHARD** вдохновлены и заимствованы из проекта для Android [MSN-GUARD](https://github.com/mbm110/MSN-GUARD) разработчика **[mbm110](https://github.com/mbm110)**.
* **Исходные проекты**: Psiphon-Labs, The Tor Project, Wintun, [Zeptun](https://github.com/Noisemux/zeptun), Mihomo, Xray-core, Sing-box и сообщество MaterialDesignInXaml.

---

### 🛠️ Сборка из исходников

```powershell
# Быстрая сборка x64
dotnet build Se7enPro\Se7enPro.csproj -c Release -r win-x64

# Полная сборка релизных пакетов (установщики и портативные архивы с .NET и без .NET для x64/x86)
powershell -ExecutionPolicy Bypass -File .\tools\build-all.ps1 -Arch all -AllVariants -OutDir dist
```

---

## 🇨🇳 中文

### 📌 项目简介
**Se7en Pro** 是一款现代、开源且强大的 Windows 桌面多协议抗封锁工具与全能 VPN 客户端，基于高性能 **原生单进程 WPF (.NET 8)** 架构与现代 **Material Design 3** 风格开发。

软件具备出色的运行效率、安全性与抗审查韧性，借助 **ReadyToRun (PGO)** 预编译技术实现毫秒级快速启动，界面帧率平稳流畅，内存占用极低。

Se7en Pro 集成了多项尖端网络抗封锁协议：包括 **WireGuard Warp 与 Aether 核心 (v2.4.0 MASQUE HTTP/3 QUIC 与 HTTP/2)**、**Foxy (Firefox VPN HTTP/2)**、**Lantern** 与 **Veep**、TLS 报文分片与分包打碎（SHARD 协议）、集成多 CDN 域前置的 Psiphon 核心、Tor 洋葱网络以及基于 Sing-box / Xray 的多跳链式代理。

本仓库遵循严格的安全解耦设计：**源代码与 Git 历史中绝不包含任何硬编码私有密钥、服务器列表或赞助商凭据**。构建秘密通过 GitHub Actions Secrets 在自动化流水线中经 **AES-256-GCM** 加密后安全注入。

---

### ✨ 核心特性与支持协议

#### 🌐 1. 多协议引擎中心
* **WireGuard Warp 与 Aether 核心 (v2.4.0)**：内核级虚拟网卡转发，支持 MASQUE 协议（HTTP/3 基于 UDP 的 QUIC 及 HTTP/2 TCP 传输）、自动回退、Warp-on-Warp 嵌套、通过任意已连接协议独立获取 WARP 密钥以及实时 MTU 扫描与调优。
* **Foxy (Firefox VPN HTTP/2)**：基于 Fastly MASQUE HTTP/2 的隧道，内置 HelloSplit TLS 绕过、多账号配额自动切换以及应用内直接登录（无需额外 VPN）。
* **Lantern 与 Veep**：内置 Lantern 与 Veep 协议引擎，支持节点健康检测与自动优选。
* **SHARD 协议引擎**：先进的 TLS ClientHello 深度分片与报文碎化技术，有效规避 DPI 深度包检测，并支持 MTU 配置与扫描。
* **Psiphon 核心网络**：升级版 `psiphon-tunnel-core`，集成 Akamai、Fastly、Cloudflare 等 CDN 前置节点调度与纯净 IP 优选。
* **Tor 专家包**：集成官方可插拔传输工具 (**Lyrebird**，支持 obfs4、Snowflake、WebTunnel 及 Conjure)，支持出口国家定向。
* **Sing-box 与 Xray**：全面支持 VLESS、VMess、Trojan、Shadowsocks 等配置。
* **🔗 多跳链式组合代理**：支持 Psiphon 经由 WARP / Foxy / Lantern / V2Ray、Tor 经由 WARP / V2Ray，以及 Lantern / Veep 经由 WARP。

#### ⚡ 2. 高性能多内核 Wintun TUN 驱动与分流
* **多内核接管网络**：通过 `wintun.dll` 搭配 **`Zeptun`**、**`Mihomo`** 或 **`tun2socks`** 内核实现全系统零 DNS 泄漏代理。
* **基于配置的分流 (Split Tunneling)**：每个配置包含三个完全独立的列表——**绕过**（直连）、**走隧道**（代理）、**阻断**（无网络），以及其余流量的默认出口。应用、域名与 IP 段只属于一个模式；移动规则始终是显式删除再添加，因此规则不会在模式之间被拖动。
* **Kill Switch 与局域网共享**：断网自动阻断流量防止 IP 泄漏，支持将节点共享至局域网（LAN）。

---

### 💡 开源鸣谢与致谢 (Credits)

* **链式代理架构与 SHARD 分片方法**：链式代理架构 (*Psiphon/Tor over WARP*) 以及 **SHARD TLS 分片方法** 的设计思路源自 **[mbm110](https://github.com/mbm110)** 开发的 Android 开源项目 [MSN-GUARD](https://github.com/mbm110/MSN-GUARD)。
* **上游开源项目**：Psiphon-Labs、The Tor Project、Wintun、[Zeptun](https://github.com/Noisemux/zeptun)、Mihomo、Xray-core、Sing-box 以及 MaterialDesignInXaml 社区。

---

### 🛠️ 源码构建指南

```powershell
# 1. 快速编译 x64 发行版
dotnet build Se7enPro\Se7enPro.csproj -c Release -r win-x64

# 2. 一键编译完整发布包 (包含 x64/x86 安装程序与便携版，支持含 .NET 及不含 .NET 版本)
powershell -ExecutionPolicy Bypass -File .\tools\build-all.ps1 -Arch all -AllVariants -OutDir dist
```

---

### 📄 许可证

本项目源码基于 [MIT License](LICENSE) 授权开源。`Se7enPro/Resources/` 目录中所包含的第三方二进制组件均保留其原版开源协议。

---

<div align="center">
  <i>Developed for freedom of access, extreme resilience, and modern circumvention.</i>
</div>
