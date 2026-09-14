<div align="center">

<img src="Assets/app-icon.png" width="140" alt="Se7en Pro Logo">

# 🛡️ Se7en Pro

**Modern Flutter Multi-Protocol Windows Client & Anti-Censorship Suite**

[![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011%20(x86%20%7C%20x64)-blue.svg?style=flat-square)](https://microsoft.com/windows)
[![Framework](https://img.shields.io/badge/Framework-Flutter%20%7C%20.NET%208.0-purple.svg?style=flat-square)](https://flutter.dev/)
[![Version](https://img.shields.io/badge/Version-v1.0.4-orange.svg?style=flat-square)](https://github.com/KNG7-P/Se7en-Pro/releases)
[![License](https://img.shields.io/badge/License-MIT-green.svg?style=flat-square)](LICENSE)

[🇬🇧 English](#-english) | [🇮🇷 فارسی](#-فارسی) | [🇷🇺 Русский](#-русский) | [🇨🇳 中文](#-中文)

</div>

---

## 🏗️ Architecture

```
┌────────────────────────────────────────────────────────┐
│                   Se7en Flutter GUI                    │
│      (Glassmorphic Controls, Telemetry, Routing)       │
└───────────────────────────┬────────────────────────────┘
                            │ Named Pipes IPC
┌───────────────────────────▼────────────────────────────┐
│                    Se7enCore Daemon                    │
│      (.NET 8 Backend Engine, Process Controller)       │
├──────────────┬──────────────┬──────────────┬───────────┤
│    Aether    │   Psiphon    │     Tor      │   SHARD   │
│  (MASQUE v2/ │ (TunnelCore) │ (Onion Mesh) │  (Frag/   │
│  WireGuard)  │              │              │  Subnode) │
└──────────────┴──────────────┴──────────────┴───────────┘
```

---

## 🇬🇧 English

### 📌 About The Project
**Se7en Pro** is an open-source, next-generation Windows anti-censorship orchestrator and VPN suite. Re-engineered from the ground up, it features a fluid **60fps glassmorphic Flutter interface** backed by a high-performance **.NET 8 headless background daemon (`Se7enCore`)** communicating via high-speed IPC named pipes.

Se7en Pro aggregates multiple state-of-the-art circumvention protocols — from Cloudflare MASQUE and TLS ClientHello fragmentation (SHARD) to Psiphon, Tor Onion mesh, and V2Ray/Xray/Sing-box chained multi-hop transports — into a unified, lightweight, resource-efficient desktop client.

The repository is structured as a clean, open framework — it does **not** hardcode any private tokens, proprietary server lists, or sponsor configs. You can supply your own values or use any of the public multi-protocol engines right out of the box.

---

### ✨ Core Features & Supported Protocols

#### 🌐 1. Supported Protocols & Multi-Engine Hub
* **Aether Core (v2.0.0)**: Supports **MASQUE** (HTTP/3 QUIC & HTTP/2 TCP), **Masque-on-Masque (MoM)**, **WireGuard Warp**, and **Warp-on-Warp (Gool)**.
* **SHARD Engine**: Advanced TLS ClientHello fragmentation and IP-level packet shredding to bypass deep packet inspection (DPI) with automatic node pool discovery.
* **Psiphon Network**: Updated `psiphon-tunnel-core` with enhanced CDN Fronting (Akamai, Fastly, Cloudflare), custom SNI, and clean edge IP dial overrides.
* **Tor Expert Bundle**: Native Tor daemon integration with official Pluggable Transports (**Lyrebird** for obfs4 / Snowflake / WebTunnel / Conjure), strict country egress routing, and onion mesh circuits.
* **Sing-box & Xray Inbound Engines**: Direct VLESS, VMess, Trojan, and Shadowsocks configuration support with TLS fragmenting and multiplexing.
* **🔗 Chained Multi-Hop Tunnels**:
  - *Psiphon over WARP* & *Tor over WARP*
  - *Psiphon over V2Ray* & *Tor over V2Ray*

#### ⚡ 2. High-Performance Wintun TUN & Routing
* **Kernel-Level TUN Routing**: Seamless system-wide capture using `wintun.dll` + `tun2socks` with zero DNS leaks.
* **Application Split Tunneling**: Route only specific applications or bypass selected Windows software directly.
* **Domain-Based Split Routing**: Real-time split-aware DNS interception for custom whitelist and blacklist rules.
* **Kill Switch & Route Isolation**: Prevents IP leakage upon unexpected connection termination.
* **LAN Sharing**: Optional local network proxy sharing with optional credentials.

#### 🎨 3. Modern Flutter Glassmorphic Interface
* **Ultra-Smooth 60fps Rendering**: Completely rewritten with Flutter, eliminating legacy WPF UI lag and dramatically reducing CPU/RAM footprint.
* **Tri-Language Support**: Fully localized in **English**, **Russian (Русский)**, and **Chinese (中文)**.
* **Live Telemetry & Logs**: Real-time upload/download speeds, round-trip latency, session counters, and searchable categorized log console.

---

### 💡 Acknowledgements & Credits

* **Chained Tunneling & SHARD Methods**: The chained transport architecture (*Psiphon/Tor over WARP*) and the **SHARD fragmentation method** are inspired by and credited to the Android [MSN-GUARD](https://github.com/mbm110/MSN-GUARD) project by **[mbm110](https://github.com/mbm110)**.
* **Core Upstreams**:
  - [Psiphon-Labs/psiphon-tunnel-core](https://github.com/Psiphon-Labs/psiphon-tunnel-core)
  - [The Tor Project](https://www.torproject.org/)
  - [Wintun](https://www.wintun.net/) & [xjasonlyu/tun2socks](https://github.com/xjasonlyu/tun2socks)
  - [XTLS/Xray-core](https://github.com/XTLS/Xray-core) & [SagerNet/sing-box](https://github.com/SagerNet/sing-box)

---

### ⚙️ Configuration & Usage

#### Providing Your Own Psiphon Values
Open `Se7enPro/Services/EmbeddedValues.cs` and replace the placeholder constants with your network configuration:

```csharp
public const string PropagationChannelId = "YOUR_PROPAGATION_CHANNEL_ID";
public const string SponsorId           = "YOUR_SPONSOR_ID";
// Public keys, fronted URL lists, feedback endpoints...
```

*(Optional)* Place a plaintext `server_entries.txt` in `Se7enPro/Resources/` for offline server caching.

---

### 🛠️ Building from Source

**Requirements**:
* Windows 10 / 11 (x86 / x64)
* [.NET SDK 8.0](https://dotnet.microsoft.com/download/dotnet/8.0)
* [Flutter SDK (3.x or newer)](https://flutter.dev/docs/get-started/install/windows)
* Visual Studio 2026+ with *Desktop development with C++*

```powershell
# 1. Build .NET Daemon (Se7enCore)
cd Se7enPro
dotnet build -c Release -r win-x64 --self-contained false

# 2. Build Flutter Client (Se7enFlutter)
cd ../Se7enFlutter
flutter pub get
flutter build windows --release
```

---

### 📄 Licensing & Bundled Components

The **source code** in this repository is licensed under the [MIT License](LICENSE). Third-party binaries bundled under `Resources/` retain their respective upstream licenses:

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
**سون پرو (Se7en Pro)** یک نرم‌افزار متن‌باز، نوین و بسیار قدرتمند برای دورزدن سانسور اینترنت در سیستم‌عامل ویندوز است. در این نسخه ساختار برنامه بازطراحی شده و شامل یک **رابط کاربری فوق‌العاده نرم و ۶۰ فریم با فلاتر (Flutter)** به همراه **دیمون پس‌زمینه قدرتمند دات‌نت ۸ (`Se7enCore`)** است که از طریق Named Pipes IPC با یکدیگر تبادل داده می‌کنند.

سون پرو پروتکل‌های متعددی از جمله MASQUE، Masque-on-Masque، فرگمنت TLS (پروتکل Shard)، هسته بهینه‌شده سایفون، شبکه پیازی تور و هسته‌های Sing-box و Xray برای اتصالات زنجیره‌ای را در قالب یک نرم‌افزار یکپارچه، سبک و بدون لگ در اختیار شما قرار می‌دهد.

این مخزن به صورت خام و ساختاریافته منتشر شده و **فاقد هرگونه شناسه یا کلید محرمانه اختصاصی** است.

---

### ✨ قابلیت‌های کلیدی و معماری پروژه

#### 🌐 ۱. پشتیبانی از پروتکل‌ها و هسته‌های ارتباطی
* **هسته Aether (نسخه 2.0.0)**: پشتیبانی از پروتکل‌های **MASQUE** (شامل HTTP/3 QUIC و HTTP/2 TCP)، پروتکل پیشرفته **Masque-on-Masque (MoM)**، پروتکل **WireGuard Warp** و **Warp-on-Warp**.
* **پروتکل قدرتمند SHARD**: فرگمنت پیشرفته بسته‌های TLS ClientHello و خردسازی ترافیک TCP جهت عبور از فیلترینگ عمیق (DPI) به همراه پایش خودکار سلامت سرورها.
* **شبکه سایفون (Psiphon Core)**: به‌روزرسانی هسته سایفون با پشتیبانی کامل از CDN Fronting (سرویس‌های Akamai، Fastly و Cloudflare) و امکان وارد کردن SNI و IP تمیز.
* **شبکه تور (Tor Expert Bundle)**: ادغام رسمی هسته Tor با پلاگین‌های رسمی Lyrebird (پل‌های obfs4، Snowflake، WebTunnel و Conjure)، انتخاب کشور خروجی و ساختار مش چندمرحله‌ای.
* **هسته‌های Sing-box و Xray**: پشتیبانی از کانفیگ‌های VLESS، VMess، Trojan و Shadowsocks با قابلیت فرگمنت و مالتی‌پلکس.
* **🔗 اتصالات زنجیره‌ای چندمرحله‌ای**:
  - *سایفون روی وارپ* و *تور روی وارپ*
  - *سایفون روی V2Ray* و *تور روی V2Ray*

#### ⚡ ۲. موتور پرسرعت Wintun TUN و روتینگ
* **تونل در سطح کرنل**: بدون نشت DNS با بهره‌گیری از درایور `wintun.dll` و `tun2socks`.
* **اسپلیت تانل برنامه‌ها و دامنه‌ها**: تفکیک ترافیک بر اساس نرم‌افزارهای دلخواه یا دامنه‌های خاص (Whitelist / Blacklist).
* **کیل سوییچ و اشتراک LAN**: قطع خودکار ترافیک در زمان قطع اتصال جهت جلوگیری از نشت IP و قابلیت اشتراک‌گذاری پروکسی در شبکه محلی.

#### 🎨 ۳. رابط کاربری مدرن فلاتر (Flutter UI)
* **عملکرد فوق‌العاده سریع و سبک**: حذف کامل لگ و کاهش چشمگیر مصرف منابع سیستم و رم با مهاجرت کامل به فلاتر.
* **پشتیبانی از ۳ زبان در برنامه**: رابط کاربری برنامه به صورت پیش‌فرض از زبان‌های **انگلیسی**، **روسی (Русский)** و **چینی (中文)** پشتیبانی می‌کند.
* **کنسول لاگ زنده و آمار دقیق**: نمایش لحظه‌ای سرعت، پینگ و ترافیک مصرفی با ترمینال لاگ پیشرفته.

---

### 💡 قدردانی و کردیت‌ها (Credits)

* **ایده و متد اتصال زنجیره‌ای و متد SHARD**: ایده اتصالات زنجیره‌ای (*سایفون/تور روی وارپ*) و همچنین **متد فرگمنت SHARD** برگرفته و الهام‌گرفته‌شده از پروژه اندرویدی ارزشمند [MSN-GUARD](https://github.com/mbm110/MSN-GUARD) توسعه‌داده‌شده توسط **[mbm110](https://github.com/mbm110)** است.
* **پروژه‌های مبدأ**: Psiphon-Labs، The Tor Project، Wintun، Xray-core، Sing-box و Flutter.

---

### ⚙️ تنظیمات و راه‌اندازی با دیتای اختصاصی

کافی است فایل `Se7enPro/Services/EmbeddedValues.cs` را باز کرده و مقادیر متنی خام خود را داخل ثابت‌ها قرار دهید:

```csharp
public const string PropagationChannelId = "YOUR_PROPAGATION_CHANNEL_ID";
public const string SponsorId           = "YOUR_SPONSOR_ID";
// کلیدها، لینک‌های سرور لیست و اندپوینت‌های فیدبک...
```

---

### 🛠️ نحوه بیلد و کامپایل

**پیش‌نیازها**:
* ویندوز 10 یا 11 (32 و 64 بیتی)
* [.NET SDK 8.0](https://dotnet.microsoft.com/download/dotnet/8.0)
* [Flutter SDK 3.x+](https://flutter.dev/docs/get-started/install/windows)
* Visual Studio 2026 با افزونه *Desktop development with C++*

```powershell
# ۱. کامپایل دیمون سی‌شارپ (Se7enCore)
cd Se7enPro
dotnet build -c Release -r win-x64 --self-contained false

# ۲. کامپایل رابط کاربری فلاتر (Se7enFlutter)
cd ../Se7enFlutter
flutter pub get
flutter build windows --release
```

---

### 📄 لایسنس

کدهای این مخزن تحت مجوز [MIT License](LICENSE) منتشر شده‌اند. باینری‌های جانبی موجود در پوشه `Resources/` تحت لایسنس‌های رسمی پروژه‌های مبدأ بازنشر می‌شوند.

---

## 🇷🇺 Русский

### 📌 О проекте
**Se7en Pro** — это современный инструмент с открытым исходным кодом для обхода интернет-цензуры и VPN-клиент для операционной системы Windows. Проект полностью переработан и сочетает в себе **плавный интерфейс на Flutter (60 FPS, стиль Glassmorphism)** и высокопроизводительный **фоновый демон .NET 8 (`Se7enCore`)**, взаимодействующий через быстрые именованные каналы (Named Pipes IPC).

Se7en Pro объединяет передовые протоколы маскировки трафика — от Cloudflare MASQUE и фрагментации пакетов TLS ClientHello (SHARD) до сети Psiphon, луковой сети Tor и многозвенных цепочек через ядра Sing-box и Xray.

Репозиторий оформлен как чистый исходный фреймворк — он **не содержит** приватных ключей, списков серверов или спонсорских настроек.

---

### ✨ Ключевые возможности и протоколы

#### 🌐 1. Многопротокольный центр
* **Ядро Aether (v2.0.0)**: Поддержка **MASQUE** (HTTP/3 QUIC и HTTP/2 TCP), протокола **Masque-on-Masque (MoM)**, **WireGuard Warp** и **Warp-on-Warp**.
* **Движок SHARD**: Продвинутая фрагментация пакетов TLS ClientHello и разделение TCP-потоков для обхода глубокого анализа пакетов (DPI) с автообнаружением узлов.
* **Сеть Psiphon**: Обновленное ядро `psiphon-tunnel-core` с поддержкой CDN Fronting (Akamai, Fastly, Cloudflare), кастомных SNI и пула чистых IP-адресов.
* **Пакет Tor Expert Bundle**: Нативная интеграция Tor с официальными подключаемыми транспортами (**Lyrebird** для obfs4 / Snowflake / WebTunnel / Conjure) и строгой маршрутизацией по странам выхода.
* **Ядра Sing-box и Xray**: Прямая поддержка конфигураций VLESS, VMess, Trojan и Shadowsocks с фрагментацией и мультиплексированием.
* **🔗 Многозвенные цепочки туннелей**:
  - *Psiphon поверх WARP* и *Tor поверх WARP*
  - *Psiphon поверх V2Ray* и *Tor поверх V2Ray*

#### ⚡ 2. Высокопроизводительный драйвер Wintun TUN
* **Туннелирование на уровне ядра**: Полный захват системного трафика через `wintun.dll` + `tun2socks` с защитой от утечек DNS.
* **Раздельное туннелирование (Split Tunneling)**: Маршрутизация конкретных приложений или доменов (режимы белого/черного списков).
* **Kill Switch и локальный доступ**: Защита от утечки реального IP при обрыве соединения и возможность раздачи прокси в локальной сети (LAN).

#### 🎨 3. Стеклянный интерфейс Flutter и 3 языка
* **Плавная работа (60 FPS)**: Полный отказ от устаревшего WPF, отсутствие задержек и минимальное потребление ресурсов CPU/RAM.
* **Поддержка 3 языков**: Интерфейс приложения полностью переведен на **английский**, **русский (Русский)** и **китайский (中文)** языки.
* **Живая телеметрия и журнал событий**: Отображение скорости приема/передачи, пинга, объема трафика и удобная консоль логов с фильтрацией.

---

### 💡 Благодарности и источники

* **Архитектура цепочек и метод SHARD**: Архитектура цепочек туннелей (*Psiphon/Tor поверх WARP*), а также **метод фрагментации SHARD** заимствованы из проекта для Android [MSN-GUARD](https://github.com/mbm110/MSN-GUARD) разработчика **[mbm110](https://github.com/mbm110)**.
* **Исходные проекты**: Psiphon-Labs, The Tor Project, Wintun, Xray-core, Sing-box и Flutter.

---

### 🛠️ Сборка из исходников

**Требования**:
* Windows 10 / 11 (x86 / x64)
* [.NET SDK 8.0](https://dotnet.microsoft.com/download/dotnet/8.0)
* [Flutter SDK (3.x+)](https://flutter.dev/docs/get-started/install/windows)
* Visual Studio 2026 с компонентом *Разработка классических приложений на C++*

```powershell
# 1. Сборка .NET демона (Se7enCore)
cd Se7enPro
dotnet build -c Release -r win-x64 --self-contained false

# 2. Сборка интерфейса Flutter (Se7enFlutter)
cd ../Se7enFlutter
flutter pub get
flutter build windows --release
```

---

### 📄 Лицензия

Исходный код распространяется под лицензией [MIT License](LICENSE). Сторонние бинарные файлы в папке `Resources/` сохраняют свои исходные лицензии.

---

## 🇨🇳 中文

### 📌 项目简介
**Se7en Pro** 是一款开源、现代且强大的 Windows 桌面多协议抗封锁工具与 VPN 客户端。该项目经过全面重构，采用了 **60 FPS 流畅的 Flutter 毛玻璃界面** 与高性能的 **.NET 8 后台常驻守护进程 (`Se7enCore`)**，二者通过高速 IPC 命名管道无缝通信。

Se7en Pro 集成了多项先进的网络穿透与抗审查协议：涵盖 Cloudflare MASQUE、TLS ClientHello 分片（SHARD 协议）、优化版 Psiphon 核心、Tor 洋葱网络以及基于 Sing-box / Xray 的多跳链式代理。

本仓库以纯净的开源框架形式发布，**不包含**任何硬编码的私有密钥、服务器列表或赞助商配置。

---

### ✨ 核心特性与支持协议

#### 🌐 1. 多协议引擎中心
* **Aether 核心 (v2.0.0)**：支持 **MASQUE** (HTTP/3 QUIC 与 HTTP/2 TCP)、**Masque-on-Masque (MoM)**、**WireGuard Warp** 及 **Warp-on-Warp**。
* **SHARD 协议引擎**：先进的 TLS ClientHello 报文分片与 TCP 分包技术，绕过深度包检测 (DPI)，并具备自动节点池探测与健康检测。
* **Psiphon 网络**：升级版 `psiphon-tunnel-core`，增强了 CDN 域前置 (Akamai, Fastly, Cloudflare)、自定义 SNI 与纯净 IP 调度。
* **Tor 专家包**：原生集成 Tor 守护进程及官方可插拔传输工具 (**Lyrebird** 用于 obfs4 / Snowflake / WebTunnel / Conjure)，支持严格的出口国家选择与洋葱网状路由。
* **Sing-box 与 Xray 引擎**：原生支持 VLESS、VMess、Trojan、Shadowsocks 等配置的分片与多路复用。
* **🔗 多跳链式组合代理**：
  - *Psiphon 经由 WARP* 及 *Tor 经由 WARP*
  - *Psiphon 经由 V2Ray* 及 *Tor 经由 V2Ray*

#### ⚡ 2. 高性能 Wintun TUN 驱动与分流路由
* **内核级 TUN 虚拟网卡**：采用 `wintun.dll` + `tun2socks` 实现全系统网络接管，杜绝 DNS 泄漏。
* **应用级与域名级分流 (Split Tunneling)**：可精确指定特定软件或域名走代理/直连（支持黑白名单）。
* **Kill Switch 与局域网共享**：在异常断连时自动阻断流量防止真实 IP 泄漏，支持将代理共享至局域网（LAN）。

#### 🎨 3. Flutter 流畅毛玻璃界面与三国语言
* **极速流畅体验 (60 FPS)**：完全告别老旧 WPF 界面的卡顿，显著降低 CPU 与内存占用。
* **支持 3 种语言**：客户端原生支持 **英语**、**俄语 (Русский)** 及 **简体中文 (中文)**。
* **实时网络监控与日志**：毫秒级延迟、上下行速度图表、流量统计及支持实时分类检索的终端日志。

---

### 💡 开源鸣谢与致谢 (Credits)

* **链式代理架构与 SHARD 分片方法**：链式代理架构 (*Psiphon/Tor over WARP*) 以及 **SHARD TLS 分片方法** 的设计思路源自 **[mbm110](https://github.com/mbm110)** 开发的 Android 开源项目 [MSN-GUARD](https://github.com/mbm110/MSN-GUARD)。
* **上游开源项目**：Psiphon-Labs、The Tor Project、Wintun、Xray-core、Sing-box 以及 Flutter 团队。

---

### 🛠️ 源码构建指南

**环境要求**：
* Windows 10 / 11 (32 位或 64 位)
* [.NET SDK 8.0](https://dotnet.microsoft.com/download/dotnet/8.0)
* [Flutter SDK (3.x+)](https://flutter.dev/docs/get-started/install/windows)
* Visual Studio 2026 并安装 *使用 C++ 的桌面开发* 工作负载

```powershell
# 1. 编译 .NET 守护进程 (Se7enCore)
cd Se7enPro
dotnet build -c Release -r win-x64 --self-contained false

# 2. 编译 Flutter 客户端 (Se7enFlutter)
cd ../Se7enFlutter
flutter pub get
flutter build windows --release
```

---

### 📄 许可证

本项目源码基于 [MIT License](LICENSE) 授权开源。`Resources/` 目录中打包的第三方二进制文件均遵循其各自的原版开源协议。

---

<div align="center">
  <i>Developed for freedom of access, extreme resilience, and modern circumvention.</i>
</div>
