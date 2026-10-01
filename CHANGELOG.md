<div align="center">

# 📝 Se7en Pro — Changelog

[🇬🇧 English](#-english) | [🇮🇷 فارسی](#-فارسی) | [🇷🇺 Русский](#-русский) | [🇨🇳 中文](#-中文)

</div>

---

## 🇬🇧 English

## [1.0.5]

The WPF client replaces the previous Flutter-based architecture as a high-performance single-process native application.

### 🚀 Highlights & Features
- **Single-Process Native Architecture**: Complete migration to .NET 8 WPF with Material Design 3, completely eliminating IPC latency and multi-process overhead.
- **Protocol Defaults**: **WireGuard Warp** is now set as the primary default protocol out of the box.
- **Aether QUIC Default**: Cloudflare MASQUE now enables **HTTP/3 (QUIC over UDP)** by default for enhanced speed and censorship evasion.
- **Security Decoupling**: Build secrets and Psiphon credentials are encrypted via AES-256-GCM; source code contains zero embedded keys.

### 🔧 Fixes & Improvements
- **Enforced Kill Switch**: Leak protection is triggered immediately on unexpected tunnel collapse, eliminating route drop leaks.
- **TUN DNS Race Condition**: Fixed a race condition between Wintun teardown and system DNS restoration.
- **SHARD & Tor Lifecycles**: Resolved disposal memory leaks and reconnection state tracking.
- **ReadyToRun PGO**: Enabled ahead-of-time compilation for near-instant client startup.
- **Automated CI/CD**: Automated release workflow produces certified installers, portables, and SHA-256 checksums.

---

## 🇮🇷 فارسی

## [نسخه 1.0.5]

در این نسخه کلاینت نِیتیو WPF به عنوان برنامه اصلی جایگزین نسخه فلاتر قبلی شده و برنامه به صورت تک‌پروسس و فوق‌العاده سریع اجرا می‌شود.

### 🚀 قابلیت‌ها و تغییرات کلیدی
- **معماری یکپارچه تک‌پروسسی (Single-Process)**: بازنویسی کامل با WPF (.NET 8) و Material Design 3 با حذف کامل لایه‌های تاخیردار IPC و دیمون‌های مجزا.
- **پروتکل پیش‌فرض برنامه**: پروتکل پرسرعت **WireGuard Warp** به عنوان اتصال اصلی و پیش‌فرض برنامه تعیین شد.
- **فعال‌سازی پیش‌فرض QUIC در پروتکل اتر**: پروتکل Aether MASQUE اکنون به صورت پیش‌فرض با **HTTP/3 (QUIC over UDP)** کار می‌کند.
- **امنیت کامل سورس و بیلد خودکار**: هیچ کلید یا لینکی در سورس قرار ندارد و مقادیر حساس از طریق GitHub Actions با استاندارد AES-256-GCM رمزگذاری می‌شوند.

### 🔧 بهینه‌سازی‌ها و رفع اشکالات
- **کیل سوییچ سخت‌گیرانه (Kill Switch)**: فعال‌سازی آنی ایزولاسیون شبکه در صورت قطعی غیرمنتظره تانل به منظور جلوگیری از نشت IP.
- **رفع خطای DNS در Wintun TUN**: اصلاح تداخل زمانی بین بسته‌شدن کارت شبکه مجازی و بازگردانی DNS سیستم.
- **بهبود چرخه حیات هسته‌های SHARD و Tor**: رفع مشکل نشت حافظه و باگ‌های اتصال مجدد در ترنسپورت‌های Lyrebird.
- **استارتاپ آنی با ReadyToRun**: فعال‌سازی کامپایل زودهنگام کدهای IL برای حذف مکث صفحه آغازین.
- **سیستم ریلیز خودکار**: ساخت خودکار فایل‌های نصبی ۳۲ و ۶۴ بیتی، نسخه‌های پرتابل و هش‌های SHA-256 در گیت‌هاب.

---

## 🇷🇺 Русский

## [1.0.5]

Нативный клиент WPF (.NET 8) заменяет предыдущую версию на Flutter в качестве высокопроизводительного единого приложения.

### 🚀 Ключевые изменения
- **Однопроцессная архитектура**: Полный переход на WPF и Material Design 3 без задержек IPC и лишних фоновых процессов.
- **Протокол по умолчанию**: **WireGuard Warp** установлен в качестве основного протокола соединения из коробки.
- **QUIC по умолчанию в Aether**: Протокол Cloudflare MASQUE теперь по умолчанию использует **HTTP/3 (QUIC over UDP)**.
- **Безопасность сборки**: Секреты Psiphon внедряются через GitHub Actions с шифрованием AES-256-GCM; исходный код чист от ключей.

### 🔧 Исправления и улучшения
- **Усиленный Kill Switch**: Мгновенная блокировка утечки IP при аварийном разрыве туннеля.
- **Стабильность Wintun TUN**: Устранена гонка потоков при очистке DNS и демонтаже виртуального адаптера.
- **Оптимизация SHARD и Tor**: Устранены утечки памяти при переподключении и перезапуске мостов Lyrebird.
- **Ускорение запуска (ReadyToRun)**: Использование pre-JIT компиляции для мгновенного отклика интерфейса.
- **Автоматизация релизов**: Автоматическая сборка установщиков x64/x86, портативных архивов и хэшей SHA-256.

---

## 🇨🇳 中文

## [1.0.5]

原生单进程 WPF (.NET 8) 客户端正式取代旧版 Flutter 架构，提供极致流畅与快速的桌面体验。

### 🚀 核心更新与亮点
- **单进程原生架构**：全面采用 WPF 与 Material Design 3，彻底消除 IPC 进程间通信延迟与额外系统开销。
- **默认连接协议**：将 **WireGuard Warp** 设为开箱即用的第一默认主协议。
- **Aether 默认开启 QUIC**：Cloudflare MASQUE 协议默认启用 **HTTP/3 (QUIC over UDP)** 高速传输。
- **构建凭据安全解耦**：所有接入私钥经由 GitHub Actions 并在构建时以 AES-256-GCM 加密注入，代码库完全公开且安全。

### 🔧 修复与优化
- **强制 Kill Switch**：当隧道异常中断时立即切断流量，确保零 IP 泄漏。
- **修复 Wintun DNS 竞争**：解决虚拟网卡关闭与系统 DNS 恢复时的冲突。
- **完善 SHARD 与 Tor 生命周期**：修复节点热重载与 Lyrebird 可插拔传输时的资源释放问题。
- **ReadyToRun 启动优化**：消除冷启动黑屏等待，实现毫秒级快速启动。
- **全自动 CI/CD**：一键生成经过签名与校验的 x64/x86 安装程序、便携版压缩包及 SHA-256 校验文件。