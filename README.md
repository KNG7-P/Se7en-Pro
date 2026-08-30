<div align="center">

<img src="Se7enPro/Assets/app-icon.png" width="140" alt="Se7en Pro Logo">

# 🛡️ Se7en Pro

**Modern Multi-Engine Windows Client & Anti-Censorship Suite**

[![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011%20(x86%20%7C%20x64)-blue.svg?style=flat-square)](https://microsoft.com/windows)
[![Framework](https://img.shields.io/badge/Framework-.NET%208.0%20%7C%20WPF-purple.svg?style=flat-square)](https://dotnet.microsoft.com/)
[![Version](https://img.shields.io/badge/Version-v1.0.3-orange.svg?style=flat-square)](https://github.com/KNG7-P/Se7en-Pro/releases)
[![License](https://img.shields.io/badge/License-MIT-green.svg?style=flat-square)](LICENSE)

[🇬🇧 English](#-english) | [🇮🇷 فارسی](#-فارسی)

</div>

---

## 🇬🇧 English

### 📌 About The Project
**Se7en Pro** is a modern, open-source Windows desktop application and censorship-circumvention orchestrator built with **C# / WPF / .NET 8**. 

Designed as a high-performance, standalone alternative to legacy clients, Se7en Pro unites multiple independent anti-censorship protocols and engines into a single, cohesive, obsidian-themed dashboard. It provides kernel-level tunneling, application and domain split tunneling, intelligent chained routing, and real-time network telemetry.

The repository is published as a clean, open framework — it does **not** hardcode any private credentials or sponsor configurations. You can easily supply your own values or use any of the supported multi-protocol engines right out of the box.

---

### ✨ Core Features & Architecture

#### 🌐 1. Multi-Protocol Engine Hub
Se7en Pro integrates four distinct tunneling cores and supports flexible chaining:
* **Psiphon Network**: Powered by `psiphon-tunnel-core`. Features **Auto**, **Direct**, and **CDN-Fronting** (Akamai, Cloudflare, Fastly) modes with custom SNI and clean IP injection.
* **Aether (WARP & MASQUE)**: Next-generation proxy engine supporting **MASQUE** (HTTP/3 QUIC & HTTP/2 TCP with packet fragmentation), **WireGuard Warp**, and **Warp-on-Warp (Gool)**.
* **Tor Expert Bundle**: Full Tor daemon integration with official Pluggable Transports (**Lyrebird** for obfs4 / Snowflake / WebTunnel / Conjure) and custom bridge presets.
* **Conduit (WebRTC Inproxy)**: Ephemeral browser-mediated WebRTC peer proxying with automatic censored-relay filtering and private compartment pairing.
* **🔗 Chained Tunneling (Psiphon-over-WARP & Tor-over-WARP)**: Routes outbound Psiphon or Tor handshakes through Cloudflare Warp/MASQUE before reaching the destination egress, creating multi-layered censorship resilience.

#### ⚡ 2. High-Performance Wintun TUN Engine
* **Kernel-Level Packet Routing**: Utilizes `wintun.dll` + `tun2socks.exe` with NetTunnelVIP virtual adapter routing.
* **App-Based Split Tunneling**: Route only specific applications or bypass selected Windows software directly.
* **Domain-Based Split Routing**: Real-time DNS and route interception for custom whitelists and blacklists.
* **Kill Switch**: Seamless route isolation protecting against IP leakage during abrupt disconnections.

#### 🎨 3. Obsidian Fluent User Experience
* **Minimalist Obsidian UI**: Crafted with Material Design 3 tokens, smooth hover glows, and dark dialog overlays.
* **Interactive Header Hub**: Switch engines on the fly directly from the top navigation bar.
* **Live Telemetry & Geolocation**: Displays real-time download/upload speeds, latency, session data usage, and egress country flags.
* **Built-in IP Scanner**: Multi-threaded scanner for finding clean CDN edge IPs with customizable latency tests.
* **Live Notice Terminal**: Real-time sanitized notice logs with search, filtering, and copy options.

---

### 💡 Acknowledgements & Credits

* **Chained Tunneling Architecture (Psiphon/Tor over WARP)**: Inspired by and credited to the Android [MSN-GUARD](https://github.com/mbm110/MSN-GUARD) project by **[mbm110](https://github.com/mbm110)**.
* **Psiphon Tunnel Core**: Developed and maintained by [Psiphon-Labs](https://github.com/Psiphon-Labs/psiphon-tunnel-core).
* **Tor Project & Lyrebird**: Official Tor Expert Bundle & Pluggable Transports by [The Tor Project](https://www.torproject.org/).
* **Wintun & tun2socks**: High-performance TUN drivers by [Wintun](https://www.wintun.net/) and [xjasonlyu/tun2socks](https://github.com/xjasonlyu/tun2socks).
* **Material Design In XAML**: [MaterialDesignInXamlToolkit](https://github.com/MaterialDesignInXAML/MaterialDesignInXamlToolkit).

---

### ⚙️ Configuration & Usage

#### Providing Your Own Psiphon Values
Open `Se7enPro/Services/EmbeddedValues.cs` and replace the placeholder constants with your network configuration:

```csharp
public const string PropagationChannelId = "YOUR_PROPAGATION_CHANNEL_ID";
public const string SponsorId           = "YOUR_SPONSOR_ID";
// Public keys, fronted URL lists, feedback endpoints...
```

*(Optional)* You can also place a plaintext `server_entries.txt` in `Se7enPro/Resources/` for offline server caching.

---

### 🛠️ Build

**Requirements**:
* Windows 10 / 11 (x86 / x64)
* [.NET SDK 8.0](https://dotnet.microsoft.com/download/dotnet/8.0)

```powershell
# Build framework-dependent release
dotnet build Se7enPro/Se7enPro.csproj -c Release -r win-x64 --self-contained false

# Build standalone single-file binary (~80 MB)
dotnet publish Se7enPro/Se7enPro.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

---

### 📄 Licensing & Bundled Components

The **C# / XAML source code** in this repository is licensed under the [MIT License](LICENSE).

Redistributable third-party binaries bundled under `Se7enPro/Resources/` remain under their respective upstream licenses:

| Component | Upstream Project | License |
| :--- | :--- | :--- |
| `psiphon-tunnel-core.exe` | [Psiphon-Labs/psiphon-tunnel-core](https://github.com/Psiphon-Labs/psiphon-tunnel-core) | GPLv3 |
| `tun2socks.exe` | [xjasonlyu/tun2socks](https://github.com/xjasonlyu/tun2socks) | GPLv3 |
| `wintun.dll` | [wintun.net](https://www.wintun.net/) | GPLv2 |
| `tor.exe`, `geoip`, `geoip6` | [The Tor Project](https://www.torproject.org/) | BSD-3-Clause |
| `lyrebird.exe`, `conjure-client.exe` | [Tor Pluggable Transports](https://gitlab.torproject.org/tpo/anti-censorship/pluggable-transports/lyrebird) | BSD-3-Clause |

---

## 🇮🇷 فارسی

### 📌 درباره پروژه
**سون پرو (Se7en Pro)** یک نرم‌افزار دسکتاپ متن‌باز، مدرن و قدرتمند برای سیستم‌عامل ویندوز است که با استفاده از **C# / WPF / .NET 8** برای عبور پایدار از محدودیت‌های اینترنت و فیلترینگ توسعه یافته است.

این برنامه به عنوان یک کلاینت یکپارچه و مدرن، پروتکل‌ها و هسته‌های مختلف ضدسانسور را در قالب یک داشبورد زیبا با تم Obsidian گرد هم آورده است. سون پرو قابلیت‌هایی نظیر تونل کل سیستم در سطح کرنل (Wintun TUN)، تونل تفکیکی برنامه‌ها و سایت‌ها (Split Tunneling)، اتصالات زنجیره‌ای هوشمند و مانیتورینگ زنده ترافیک را فراهم می‌سازد.

این مخزن به صورت خام و ساختاریافته منتشر شده و **فاقد هرگونه شناسه یا کلید محرمانه اختصاصی** است. شما می‌توانید به سادگی مقادیر دلخواه خود را در آن قرار داده یا از سایر پروتکل‌های موجود برنامه استفاده کنید.

---

### ✨ قابلیت‌های کلیدی و معماری پروژه

#### 🌐 ۱. پشتیبانی از چندین هسته و پروتکل مستقل
* **شبکه سایفون (Psiphon)**: مبتنی بر `psiphon-tunnel-core` با پشتیبانی از مدهای **Auto**، **Direct** و **CDN Fronting** (سرویس‌های Akamai، Cloudflare و Fastly) به همراه امکان وارد کردن SNI و IP تمیز.
* **پروتکل ایتر (Aether / WARP & MASQUE)**: نسل جدید پروکسی با پشتیبانی از پروتکل مدرن **MASQUE** (شامل HTTP/3 QUIC و HTTP/2 TCP با قابلیت Fragment)، پروتکل **WireGuard Warp** و **Warp-on-Warp (Gool)**.
* **شبکه تور (Tor Expert Bundle)**: ادغام کامل هسته Tor به همراه Pluggable Transportهای رسمی (**Lyrebird** برای پل‌های obfs4، Snowflake، WebTunnel و Conjure) و امکان استفاده از پل‌های سفارشی.
* **کانduit (WebRTC Inproxy)**: پروکسی واسط مبتنی بر WebRTC با اتصال همتا به همتا بدون نیاز به سرور مستقیم، به همراه فیلتر رله‌های سانسورشده و امکان Pair اختصاصی.
* **🔗 اتصال زنجیره‌ای (سایفون + وارپ و تور + وارپ)**: عبور ترافیک اولیه سایفون و تور از بستر امن وارپ کلودفلر پیش از رسیدن به مقصد که پایداری عبور از فیلترینگ را به شدت افزایش می‌دهد.

#### ⚡ ۲. موتور پرسرعت Wintun TUN
* **تونل در سطح کرنل**: استفاده از درایور `wintun.dll` و `tun2socks.exe` بر پایه معماری مدرن NetTunnelVIP.
* **اسپلیت تانل برنامه‌ها (App Split)**: امکان انتخاب برنامه‌های خاص ویندوز برای عبور از تونل یا مستثنی کردن آن‌ها.
* **اسپلیت تانل دامنه‌ها (Domain Split)**: تفکیک سایت‌های داخلی و خارجی با سیستم هوشمند رهگیری DNS.
* **کیل سوییچ (Kill Switch)**: جلوگیری از نشت IP و قطع امن ترافیک در صورت قطع اتصال ناگهانی.

#### 🎨 ۳. رابط کاربری مدرن Obsidian UI
* **طراحی مینیمال و شیک**: توسعه‌یافته با المان‌های Material Design 3، افکت‌های نوری، انیمیشن‌های نرم و پاپ‌آپ‌های دارک.
* **نوار بالای تعاملی**: تغییر سریع پروتکل و هسته اتصال به صورت زنده از نوار بالای صفحه.
* **اطلاعات زنده شبکه و ژئولوکیشن**: نمایش سرعت لحظه‌ای دانلود/آپلود، پینگ، حجم مصرفی سشن و پرچم کشور سرور خروجی.
* **اسکنر آی‌پی تمیز (IP Scanner)**: تست و اسکن چندنخی آی‌پی‌های تمیز CDNها با قابلیت تست پینگ.
* **ترمینال لاگ زنده**: نمایش رویدادها و لاگ‌های پاک‌سازی‌شده با قابلیت فیلتر، جستجو و کپی.

---

### 💡 قدردانی و کردیت‌ها (Credits)

* **ایده و متد اتصال زنجیره‌ای (سایفون/تور روی وارپ)**: برگرفته و الهام‌گرفته‌شده از پروژه اندرویدی ارزشمند [MSN-GUARD](https://github.com/mbm110/MSN-GUARD) توسعه‌داده‌شده توسط **[mbm110](https://github.com/mbm110)**.
* **هسته سایفون (Psiphon Tunnel Core)**: توسعه‌یافته توسط [Psiphon-Labs](https://github.com/Psiphon-Labs/psiphon-tunnel-core).
* **پروژه تور (The Tor Project)**: ارائه‌دهنده رسمی Tor Expert Bundle و Pluggable Transportها.
* **درایور Wintun و tun2socks**: ابزارهای قدرتمند روتینگ لایه ۳ توسط [Wintun](https://www.wintun.net/) و [xjasonlyu/tun2socks](https://github.com/xjasonlyu/tun2socks).
* **کتابخانه Material Design In XAML**: رابط بصری مدرن بر بستر WPF.

---

### ⚙️ تنظیمات و راه‌اندازی با دیتای اختصاصی

کافی است فایل `Se7enPro/Services/EmbeddedValues.cs` را باز کرده و مقادیر متنی خام خود را داخل ثابت‌ها قرار دهید:

```csharp
public const string PropagationChannelId = "YOUR_PROPAGATION_CHANNEL_ID";
public const string SponsorId           = "YOUR_SPONSOR_ID";
// کلیدها، لینک‌های سرور لیست و اندپوینت‌های فیدبک...
```

*(اختیاری)* همچنین می‌توانید فایل متنی ساده `server_entries.txt` را درون پوشه `Se7enPro/Resources/` قرار دهید تا برنامه از کش سرورهای آفلاین استفاده کند.

---

### 🛠️ نحوه بیلد و کامپایل

**پیش‌نیازها**:
* ویندوز 10 یا 11 (نسخه‌های 32 و 64 بیتی)
* [.NET SDK 8.0](https://dotnet.microsoft.com/download/dotnet/8.0)

```powershell
# کامپایل نسخه استاندارد
dotnet build Se7enPro/Se7enPro.csproj -c Release -r win-x64 --self-contained false

# خروجی فایل تک‌فایلی مستقل (بدون نیاز به نصب دات‌نت - حدود ۸۰ مگابایت)
dotnet publish Se7enPro/Se7enPro.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

---

### 📄 لایسنس و باینری‌های جانبی

کدهای **C# و XAML** این مخزن تحت مجوز [MIT License](LICENSE) منتشر شده‌اند.

باینری‌های شخص ثالث موجود در پوشه `Se7enPro/Resources/` تحت لایسنس‌های رسمی پروژه‌های مبدأ خود بازنشر می‌شوند:

| فایل / مؤلفه | پروژه مبدأ | لایسنس |
| :--- | :--- | :--- |
| `psiphon-tunnel-core.exe` | [Psiphon-Labs/psiphon-tunnel-core](https://github.com/Psiphon-Labs/psiphon-tunnel-core) | GPLv3 |
| `tun2socks.exe` | [xjasonlyu/tun2socks](https://github.com/xjasonlyu/tun2socks) | GPLv3 |
| `wintun.dll` | [wintun.net](https://www.wintun.net/) | GPLv2 |
| `tor.exe`, `geoip`, `geoip6` | [The Tor Project](https://www.torproject.org/) | BSD-3-Clause |
| `lyrebird.exe`, `conjure-client.exe` | [Tor Pluggable Transports](https://gitlab.torproject.org/tpo/anti-censorship/pluggable-transports/lyrebird) | BSD-3-Clause |

---

<div align="center">
  <i>Developed for freedom of access and modern censorship circumvention.</i>
</div>
