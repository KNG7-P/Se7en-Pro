<div align="center">

<img src="Se7enPro/Assets/app-icon.png" width="130" alt="Se7en Pro Logo">

# 🛡️ Se7en Pro

**Modern WPF Multi-Protocol Windows Client & Anti-Censorship Suite**

[![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011%20(x86%20%7C%20x64)-blue.svg?style=flat-square)](https://microsoft.com/windows)
[![Framework](https://img.shields.io/badge/Framework-WPF%20%7C%20.NET%208.0-purple.svg?style=flat-square)](https://dotnet.microsoft.com/)
[![UI](https://img.shields.io/badge/UI-Material%20Design%203-informational.svg?style=flat-square)](https://github.com/MaterialDesignInXAML/MaterialDesignInXamlToolkit)
[![Release](https://img.shields.io/badge/Release-Passing-brightgreen.svg?style=flat-square)](https://github.com/KNG7-P/Se7en-Pro/actions/workflows/release.yml)
[![Version](https://img.shields.io/badge/Version-v1.0.7-orange.svg?style=flat-square)](https://github.com/KNG7-P/Se7en-Pro/releases)
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
│      Aether       │      Psiphon      │      Tor       │     SHARD     │
│  (MASQUE HTTP/3 / │   (TunnelCore)    │  (Onion Mesh   │ (TLS Frag /   │
│  WireGuard Warp)  │                   │   Lyrebird PT) │  IP Shredder) │
├───────────────────┴───────────────────┴────────────────┴───────────────┤
│                     Sing-box & Xray Multi-Hop Hub                      │
│             (VLESS · VMess · Trojan · Shadowsocks Multiplex)           │
├────────────────────────────────────────────────────────────────────────┤
│                         Wintun TUN & tun2socks                         │
│             Kernel-Level Routing · Per-App Split · Zero Leak           │
└────────────────────────────────────────────────────────────────────────┘
```

---

## 🇬🇧 English

### 📌 About The Project
**Se7en Pro** is an open-source, next-generation Windows anti-censorship orchestrator and multi-protocol VPN suite built on a modern, high-performance **single-process native WPF (.NET 8)** architecture featuring **Material Design 3**.

Engineered for extreme performance, security, and resilience, Se7en Pro features near-instant startup via **ReadyToRun (PGO)** pre-compilation, fluid 60fps animations, and minimal memory footprint.

Se7en Pro unifies state-of-the-art circumvention protocols — WireGuard Warp (primary connection protocol), Cloudflare MASQUE (HTTP/3 QUIC & HTTP/2), TLS ClientHello packet shredding (SHARD), Psiphon Network with CDN fronting, Tor Onion mesh with pluggable transports, and Xray/Sing-box chained multi-hop transports — into an intuitive, transparent desktop client.

The repository follows strict security decoupling: **no private credentials, server lists, or sponsor keys are stored in source code or Git history**. Build secrets are injected securely via GitHub Actions Secrets using AES-256-GCM encryption, allowing forks and public checkouts to build cleanly with safe placeholders.

---

### ✨ Core Features & Supported Protocols

#### 🌐 1. Supported Protocols & Multi-Engine Hub
* **WireGuard Warp (Primary Protocol)**: High-speed, modern kernel-level tunnel set as the primary out-of-the-box connection method.
* **Aether Core (v2.0.0)**: Advanced MASQUE transport supporting HTTP/3 (QUIC over UDP) and HTTP/2 (TCP) with automatic fallback and Warp-on-Warp chaining.
* **SHARD Engine**: Advanced TLS ClientHello fragmentation and IP-level packet shredding to defeat Deep Packet Inspection (DPI) with automatic latency-based node discovery.
* **Psiphon Network**: Hardened `psiphon-tunnel-core` integration with multi-CDN fronting (Akamai, Fastly, Cloudflare), custom SNI override, and clean edge-IP dials.
* **Tor Expert Bundle**: Native Tor daemon integration with official Pluggable Transports (**Lyrebird** for obfs4 / Snowflake / WebTunnel / Conjure), strict country egress routing, and onion mesh circuits.
* **Sing-box & Xray Engines**: Native VLESS, VMess, Trojan, and Shadowsocks inbound support with TLS fragmenting and multiplexing.
* **🔗 Chained Multi-Hop Tunnels**:
  - *Psiphon over WARP* & *Tor over WARP*
  - *Psiphon over V2Ray* & *Tor over V2Ray*

#### ⚡ 2. High-Performance Wintun TUN & Routing
* **Kernel-Level TUN Routing**: Seamless system-wide capture using `wintun.dll` + `tun2socks` with zero DNS leaks.
* **Application Split Tunneling**: Route only specific applications or bypass selected Windows software directly.
* **Domain-Based Split Routing**: Real-time split-aware DNS interception for custom whitelist and blacklist rules.
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
  - [Wintun](https://www.wintun.net/) & [xjasonlyu/tun2socks](https://github.com/xjasonlyu/tun2socks)
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
| `tun2socks.exe` | [xjasonlyu/tun2socks](https://github.com/xjasonlyu/tun2socks) | GPLv3 |
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

سون پرو مجموعه‌ای از پیشرفته‌ترین پروتکل‌های ارتباطی نظیر WireGuard Warp (به عنوان اتصال اصلی)، پروتکل Cloudflare MASQUE (با پشتیبانی از استانداردهای HTTP/3 QUIC و HTTP/2)، فناوری خردسازی بسته‌های TLS (پروتکل SHARD)، هسته ارتقایافته سایفون، شبکه تور و ترانزیت‌های چندمرحله‌ای Sing-box و Xray را در قالب کلاینتی یکپارچه ارائه می‌دهد.

این مخزن به گونه‌ای معماری شده که **فاقد هرگونه شناسه، کلید محرمانه یا لیست سرور اختصاصی در متن کدها یا تاریخچه گیت** است. داده‌های حساس در زمان بیلد از طریق GitHub Actions Secrets با الگوریتم قدرتمند **AES-256-GCM** رمزگذاری شده و داخل باینری قرار می‌گیرند.

---

### ✨ قابلیت‌های کلیدی و معماری پروژه

#### 🌐 ۱. پشتیبانی از پروتکل‌ها و هسته‌های ارتباطی
* **پروتکل WireGuard Warp (پروتکل اصلی)**: اتصال پرسرعت و پایدار در سطح کرنل سیستم‌عامل.
* **هسته Aether (نسخه 2.0.0)**: پشتیبانی پیشرفته از پروتکل **MASQUE** بر بستر HTTP/3 (QUIC over UDP) و HTTP/2 (TCP) به همراه امکان برگشت خودکار و قابلیت Warp-on-Warp.
* **پروتکل قدرتمند SHARD**: فرگمنت پیشرفته بسته‌های TLS ClientHello و تکه‌تکه‌کردن ترافیک برای دورزدن فیلترینگ عمیق (DPI) همراه با اسکنر خودکار نزدیک‌ترین آی‌پی‌های لبه.
* **شبکه سایفون (Psiphon Core)**: هسته اختصاصی سایفون با پشتیبانی کامل از CDN Fronting (سرویس‌های Akamai، Fastly و Cloudflare) و امکان وارد کردن SNI و IP تمیز.
* **شبکه تور (Tor Expert Bundle)**: ادغام کامل هسته Tor با ترنسپورت‌های رسمی Lyrebird (پل‌های obfs4، Snowflake، WebTunnel و Conjure)، قابلیت انتخاب کشور خروجی و ارتباط شبکه‌ای پیازی.
* **هسته‌های Sing-box و Xray**: پشتیبانی کامل از کانفیگ‌های VLESS، VMess، Trojan و Shadowsocks با قابلیت مالتی‌پلکسینگ و فرگمنت.
* **🔗 اتصالات زنجیره‌ای چندمرحله‌ای (Chained)**:
  - *سایفون روی وارپ* و *تور روی وارپ*
  - *سایفون روی V2Ray* و *تور روی V2Ray*

#### ⚡ ۲. موتور پرسرعت Wintun TUN و تفکیک ترافیک
* **تونل سیستم در سطح هسته**: بدون کوچک‌ترین نشت DNS با تکیه بر درایور `wintun.dll` و `tun2socks`.
* **اسپلیت تانل برنامه‌ها و سایت‌ها**: تفکیک ترافیک بر اساس نرم‌افزارهای انتخابی یا لیست دامنه‌های خاص (Whitelist / Blacklist) با رهگیری آنی DNS.
* **کیل سوییچ و اشتراک LAN**: جلوگیری فوری از نشت IP در صورت قطع ناگهانی تانل و امکان اشتراک‌گذاری پروکسی در شبکه محلی.

#### 🎨 ۳. رابط کاربری مدرن Material Design 3 و عملکرد
* **کلاینت بومی و سبک**: انیمیشن‌های روان، مصرف بهینه و سرعت پاسخ‌دهی بالا.
* **استارتاپ سریع**: بهینه‌سازی بارگذاری اولیه با کامپایل Native از طریق PublishReadyToRun.
* **پشتیبانی از ۳ زبان در برنامه**: ترجمه کامل به زبان‌های **انگلیسی**، **روسی (Русский)** و **چینی (中文)**.
* **کنسول لاگ زنده و تلکتری**: مانیتورینگ زنده سرعت ارسال/دریافت، پینگ، آمار داده‌های مصرفی و ترمینال لاگین دسته‌بندی‌شده.

---

### 💡 قدردانی و کردیت‌ها (Credits)

* **ایده و متد اتصال زنجیره‌ای و متد SHARD**: ایده اتصالات زنجیره‌ای (*سایفون/تور روی وارپ*) و همچنین **متد فرگمنت SHARD** برگرفته و الهام‌گرفته‌شده از پروژه اندرویدی ارزشمند [MSN-GUARD](https://github.com/mbm110/MSN-GUARD) توسعه‌داده‌شده توسط **[mbm110](https://github.com/mbm110)** است.
* **پروژه‌های مبدأ**: Psiphon-Labs، The Tor Project، Wintun، Xray-core، Sing-box و MaterialDesignInXaml.

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

Клиент объединяет передовые протоколы маскировки: WireGuard Warp (основной протокол), Cloudflare MASQUE (HTTP/3 QUIC и HTTP/2), фрагментацию пакетов TLS ClientHello (SHARD), сеть Psiphon с CDN Fronting, сеть Tor с подключаемыми транспортами, а также многозвенные цепочки Sing-box и Xray.

В репозитории соблюдены строгие стандарты безопасности: **никаких приватных ключей, списков серверов или спонсорских данных в кодовой базе или истории Git**. Секреты внедряются на этапе сборки через GitHub Actions Secrets с шифрованием **AES-256-GCM**.

---

### ✨ Ключевые возможности и протоколы

#### 🌐 1. Многопротокольный хаб
* **WireGuard Warp (Основной протокол)**: Современный высокоскоростной протокол на уровне ядра операционной системы.
* **Ядро Aether (v2.0.0)**: Поддержка протокола MASQUE (HTTP/3 QUIC over UDP и HTTP/2 TCP) с автоматическим откатом и цепочками Warp-on-Warp.
* **Движок SHARD**: Фрагментация пакетов TLS ClientHello и разделение TCP-потоков для обхода глубокого анализа пакетов (DPI).
* **Сеть Psiphon**: Полноценная интеграция `psiphon-tunnel-core` с поддержкой CDN Fronting (Akamai, Fastly, Cloudflare) и пула чистых IP.
* **Пакет Tor Expert Bundle**: Нативная интеграция Tor с транспортами **Lyrebird** (obfs4, Snowflake, WebTunnel, Conjure) и выбором страны выхода.
* **Ядра Sing-box и Xray**: Поддержка VLESS, VMess, Trojan и Shadowsocks с фрагментацией и мультиплексированием.
* **🔗 Цепочки туннелей**: Psiphon поверх WARP/V2Ray и Tor поверх WARP/V2Ray.

#### ⚡ 2. Высокопроизводительный Wintun TUN
* **Захват трафика на уровне ядра**: Полная изоляция через `wintun.dll` + `tun2socks` без утечек DNS.
* **Раздельное туннелирование**: Маршрутизация по приложениям или по доменным именам (Split Tunneling).
* **Kill Switch и LAN**: Мгновенная блокировка утечки IP при сбое и возможность раздачи прокси в локальную сеть.

---

### 💡 Благодарности и источники

* **Архитектура цепочек и метод SHARD**: Архитектура цепочек туннелей (*Psiphon/Tor поверх WARP*), а также **метод фрагментации SHARD** вдохновлены и заимствованы из проекта для Android [MSN-GUARD](https://github.com/mbm110/MSN-GUARD) разработчика **[mbm110](https://github.com/mbm110)**.
* **Исходные проекты**: Psiphon-Labs, The Tor Project, Wintun, Xray-core, Sing-box и сообщество MaterialDesignInXaml.

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

Se7en Pro 集成了多项尖端网络抗封锁协议：包括 **WireGuard Warp（主连接协议）**、Cloudflare **MASQUE（支持 HTTP/3 QUIC 与 HTTP/2）**、TLS 报文分片与分包打碎（SHARD 协议）、集成多 CDN 域前置的 Psiphon 核心、Tor 洋葱网络以及基于 Sing-box / Xray 的多跳链式代理。

本仓库遵循严格的安全解耦设计：**源代码与 Git 历史中绝不包含任何硬编码私有密钥、服务器列表或赞助商凭据**。构建秘密通过 GitHub Actions Secrets 在自动化流水线中经 **AES-256-GCM** 加密后安全注入。

---

### ✨ 核心特性与支持协议

#### 🌐 1. 多协议引擎中心
* **WireGuard Warp（主协议）**：内核级虚拟网卡转发，速度快、延迟低、连接稳固。
* **Aether 核心 (v2.0.0)**：支持 MASQUE 协议（HTTP/3 基于 UDP 的 QUIC 及 HTTP/2 TCP 传输），支持自动回退与 Warp-on-Warp 嵌套。
* **SHARD 协议引擎**：先进的 TLS ClientHello 深度分片与报文碎化技术，有效规避 DPI 深度包检测。
* **Psiphon 核心网络**：升级版 `psiphon-tunnel-core`，集成 Akamai、Fastly、Cloudflare 等 CDN 前置节点调度与纯净 IP 优选。
* **Tor 专家包**：集成官方可插拔传输工具 (**Lyrebird**，支持 obfs4、Snowflake、WebTunnel 及 Conjure)，支持出口国家定向。
* **Sing-box 与 Xray**：全面支持 VLESS、VMess、Trojan、Shadowsocks 等配置。
* **🔗 多跳链式组合代理**：支持 Psiphon 经由 WARP/V2Ray 以及 Tor 经由 WARP/V2Ray。

#### ⚡ 2. 高性能 Wintun TUN 驱动与分流
* **内核级网络接管**：通过 `wintun.dll` + `tun2socks` 实现全系统零 DNS 泄漏代理。
* **应用级与域名级分流 (Split Tunneling)**：按需指定软件或域名走直连/代理。
* **Kill Switch 与局域网共享**：断网自动阻断流量防止 IP 泄漏，支持将节点共享至局域网（LAN）。

---

### 💡 开源鸣谢与致谢 (Credits)

* **链式代理架构与 SHARD 分片方法**：链式代理架构 (*Psiphon/Tor over WARP*) 以及 **SHARD TLS 分片方法** 的设计思路源自 **[mbm110](https://github.com/mbm110)** 开发的 Android 开源项目 [MSN-GUARD](https://github.com/mbm110/MSN-GUARD)。
* **上游开源项目**：Psiphon-Labs、The Tor Project、Wintun、Xray-core、Sing-box 以及 MaterialDesignInXaml 社区。

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
