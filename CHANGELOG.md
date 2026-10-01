<div align="center">

# 📝 Se7en Pro — Changelog

[🇬🇧 English](#-english) | [🇮🇷 فارسی](#-فارسی) | [🇷🇺 Русский](#-русский) | [🇨🇳 中文](#-中文)

</div>

---

## 🇬🇧 English

## [1.0.5]

High-performance native single-process WPF application (.NET 8) with Material Design 3.

### 🚀 Highlights & Features
- **Single-Process Native Architecture**: Built on .NET 8 WPF with Material Design 3, streamlined execution paths, and minimal memory usage.
- **Protocol Configuration**: **WireGuard Warp** is configured as the primary out-of-the-box connection protocol.
- **Aether Engine Upgrade**: MASQUE transport with dual HTTP/3 (QUIC over UDP) and HTTP/2 (TCP) support, automatic fallback, and Warp-on-Warp chaining.
- **Complete Packaging Suite**: Official releases now provide both Self-Contained (with .NET) and Framework-Dependent (without .NET) installers and portable ZIPs for x64 and x86 (8 packages total).
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

کلاینت نِیتیو تک‌پروسسی پرسرعت بر پایه WPF (.NET 8) و طراحی مدرن Material Design 3.

### 🚀 قابلیت‌ها و تغییرات کلیدی
- **معماری یکپارچه تک‌پروسسی (Single-Process)**: توسعه‌یافته بر بستر دات‌نت ۸ با متریال دیزاین ۳ با حداقل مصرف حافظه رم و سرعت پاسخ‌دهی بالا.
- **پروتکل ارتباطی اصلی**: پروتکل پرسرعت **WireGuard Warp** به عنوان اتصال پیش‌فرض و اصلی برنامه تنظیم شد.
- **ارتقای هسته Aether**: پشتیبانی از پروتکل MASQUE بر بستر هر دو استاندارد HTTP/3 (QUIC over UDP) و HTTP/2 (TCP) با قابلیت برگشت خودکار و زنجیره Warp-on-Warp.
- **پکیج‌های متنوع ریلیز**: انتشار رسمی ۸ بسته مختلف شامل نسخه‌های نصبی و پرتابل ۳۲ و ۶۴ بیتی در دو حالت همراه با دات‌نت و بدون دات‌نت.
- **امنیت کامل سورس و بیلد خودکار**: هیچ کلید یا لینکی در سورس قرار ندارد و مقادیر حساس از طریق GitHub Actions با استاندارد AES-256-GCM رمزگذاری می‌شوند.

### 🔧 بهینه‌سازی‌ها و رفع اشکالات
- **کیل سوییچ سخت‌گیرانه (Kill Switch)**: فعال‌سازی آنی ایزولاسیون شبکه در صورت قطعی غیرمنتظره تانل به منظور جلوگیری از نشت IP.
- **رفع خطای DNS در Wintun TUN**: اصلاح تداخل زمانی بین بسته‌شدن کارت شبکه مجازی و بازگردانی DNS سیستم.
- **بهبود چرخه حیات هسته‌های SHARD و Tor**: رفع مشکل نشت حافظه و باگ‌های اتصال مجدد در ترنسپورت‌های Lyrebird.
- **استارتاپ سریع با ReadyToRun**: فعال‌سازی کامپایل زودهنگام کدهای IL برای حذف مکث صفحه آغازین.
- **سیستم ریلیز خودکار**: ساخت خودکار فایل‌های نصبی و پرتابل ۳۲ و ۶۴ بیتی و هش‌های SHA-256 در گیت‌هاب.

---

## 🇷🇺 Русский

## [1.0.5]

Нативное высокопроизводительное однопроцессное приложение на WPF (.NET 8) с дизайном Material Design 3.

### 🚀 Ключевые изменения
- **Однопроцессная архитектура**: Разработано на .NET 8 WPF с Material Design 3, оптимизированным расходом памяти и плавным откликом.
- **Основной протокол**: **WireGuard Warp** настроен как основной протокол соединения из коробки.
- **Обновление ядра Aether**: Протокол MASQUE с поддержкой HTTP/3 (QUIC over UDP) и HTTP/2 (TCP), автоматическим переключением и Warp-on-Warp.
- **Расширенный набор дистрибутивов**: Официальный выпуск включает 8 пакетов (установщики и портативные версии с .NET и без .NET для x64 и x86).
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

基于 WPF (.NET 8) 与 Material Design 3 开发的高性能原生单进程应用程序。

### 🚀 核心更新与亮点
- **单进程原生架构**：采用 .NET 8 WPF 与 Material Design 3，深度优化运行路径，降低内存开销。
- **默认连接协议**：将 **WireGuard Warp** 配置为开箱即用的第一主协议。
- **Aether 引擎全面升级**：MASQUE 协议支持 HTTP/3 (基于 UDP 的 QUIC) 与 HTTP/2 (TCP) 双通道自动回退及 Warp-on-Warp 嵌套。
- **全系分发包支持**：官方发布提供共计 8 款安装包（包含含 .NET 独立版与免 .NET 框架依赖版的 x64/x86 安装程序及免安装便携版）。
- **构建凭据安全解耦**：所有接入私钥经由 GitHub Actions 并在构建时以 AES-256-GCM 加密注入，代码库完全公开且安全。

### 🔧 修复与优化
- **强制 Kill Switch**：当隧道异常中断时立即切断流量，确保零 IP 泄漏。
- **修复 Wintun DNS 竞争**：解决虚拟网卡关闭与系统 DNS 恢复时的冲突。
- **完善 SHARD 与 Tor 生命周期**：修复节点热重载与 Lyrebird 可插拔传输时的资源释放问题。
- **ReadyToRun 启动优化**：消除冷启动等待，实现毫秒级快速启动。
- **全自动 CI/CD**：一键生成经过校验的 x64/x86 安装程序、便携版压缩包及 SHA-256 校验文件。