<div align="center">

# 📝 Se7en Pro — Changelog

[🇬🇧 English](#-english) | [🇮🇷 فارسی](#-فارسی) | [🇷🇺 Русский](#-русский) | [🇨🇳 中文](#-中文)

</div>

---

## 🇬🇧 English

## [1.0.7]

Automatic egress endpoint and geo-location resolution, live country flag indicator in dashboard, Tor pluggable transport path space fixes, and DNS resolver diagnostics UI repair.

### 🚀 Highlights & Features
- **Dynamic Egress Location & IP Resolver**: Multi-provider egress probe (`EndpointProbeHelper`) automatically querying via the active local SOCKS port (Cloudflare trace, Country.is, Ipwho.is, Ip-api.com) upon connection.
- **Live Country Flag Indicator**: Connected exit node's country flag and localized country name are now displayed directly inside the dashboard endpoint tile and telemetry badge.
- **Protocol-Adaptive Region Selector**: The region selection box in the dashboard now dynamically appears only for protocols supporting exit region customization (e.g. Psiphon, Tor).

### 🔧 Fixes & Improvements
- **Tor Pluggable Transport Path Fix**: Resolved issues with pluggable transport executable paths containing spaces on Windows by normalizing to 8.3 short paths without illegal manual quotes in `torrc`.
- **Custom DNS Diagnostics Button**: Fixed the `Test Resolvers` button in DNS settings which was previously disabled due to an incorrect inverse boolean converter binding.
- **Multi-Protocol Exit Synchronization**: Seamless exit endpoint probing wired into V2Ray, Tor, Psiphon, and Local SOCKS engine workflows.
- **In-App Updater ACL & Staging Fallback**: Hardened update staging directory permissions to include active user SID alongside Administrators and SYSTEM, adding reliable fallback staging directories for non-elevated downloads.
- **Accurate Update Flavor Selection**: In-app updater now accurately detects and downloads the exact runtime flavor matching the currently running instance (Setup vs Portable, x64 vs x86, with vs without bundled .NET runtime).

---

## 🇮🇷 فارسی

## [نسخه 1.0.7]

تشخیص خودکار موقعیت و آی‌پی خروجی تانل، نمایش پرچم کشور در داشبورد، اصلاح مسیر اجرایی ترنسپورت‌های Tor و رفع مشکل دکمه تست دی‌ان‌اس.

### 🚀 قابلیت‌ها و تغییرات کلیدی
- **تشخیص داینامیک IP و لوکیشن واقعی خروجی**: کاوشگر هوشمند چندلایه (`EndpointProbeHelper`) از طریق ساکس داخلی جهت شناسایی آی‌پی و کشور خروجی اتصال (Cloudflare trace، Country.is، Ipwho.is و Ip-api.com).
- **نمایش مستقیم پرچم کشور در کاشی اتصال**: آیکون کاشی اتصال و نشانگر وضعیت اکنون پرچم رسمی کشور خروجی و نام کشور را به طور زنده پس از اتصال نمایش می‌دهند.
- **سازگاری هوشمند باکس انتخاب کشور**: باکس انتخاب سرور/کشور تنها در پروتکل‌هایی که قابلیت انتخاب سرور دارند (مانند Psiphon و Tor) در داشبورد نمایش داده می‌شود.

### 🔧 بهینه‌سازی‌ها و رفع اشکالات
- **اصلاح مسیر ترنسپورت‌های Tor**: حل مشکل فاصله (Space) در مسیر فایل‌های اجرایی پلاگین‌های Tor با تبدیل به مسیر کوتاه 8.3 ویندوز در `torrc`.
- **فعال‌سازی دکمه تست DNS**: اصلاح بایندینگ دکمه `بررسی رزولورها` در تنظیمات DNS سفارشی که به دلیل اینورتر اشتباه غیرفعال مانده بود.
- **هماهنگ‌سازی سراسری لوکیشن خروجی**: اتصال ماژول کاوشگر خروجی به تمامی موتورها از جمله V2Ray، ساکس محلی، Tor و سایفون.
- **اصلاح دسترسی و دانلود آپدیت برنامه**: تنظیم سطح دسترسی کاربر جاری در دایرکتوری موقت دانلود آپدیت و تعبیه مسیر جایگزین امن (Fallback) جهت رفع کامل خطای دانلود آپدیت.
- **تطبیق هوشمند نسخه نصبی و پرتابل در آپدیتر**: سیستم به‌روزرسانی خودکار اکنون نسخه در حال اجرا (Setup در برابر Portable، معماری x64/x86 و وجود یا عدم وجود ران‌تایم دات‌نت) را به دقت شناسایی کرده و فایل متناسب را دریافت می‌کند.

---

## 🇷🇺 Русский

## [1.0.7]

Автоматическое определение выходного IP и геолокации, отображение флага страны на панели управления, исправление путей плагинов Tor и кнопки проверки DNS.

### 🚀 Ключевые изменения
- **Динамическое определение точки выхода**: Многоуровневый опрос (`EndpointProbeHelper`) через локальный SOCKS для определения внешнего IP и страны.
- **Индикация флага страны выхода**: Прямое отображение флага и названия страны узла выхода в карточке подключения и телеметрии.
- **Адаптивный селектор регионов**: Выбор региона теперь отображается только для протоколов, поддерживающих выбор выходного узла (Psiphon, Tor).

### 🔧 Исправления и улучшения
- **Исправление путей Pluggable Transports в Tor**: Корректное преобразование путей с пробелами в короткие пути формата 8.3 в `torrc`.
- **Кнопка проверки DNS-серверов**: Исправлено ошибочное отключение кнопки проверки в настройках пользовательских DNS.
- **Интеграция со всеми протоколами**: Синхронный опрос точки выхода для V2Ray, Tor, Psiphon и локальных SOCKS туنнелей.
- **Исправление прав промежуточного каталога обновлений**: Корректное добавление прав текущего пользователя (SID) и резервный каталог для надежной загрузки обновлений.
- **Точный выбор версии в автообновлении**: Автоматический выбор точного типа сборки (Installer vs Portable, x64 vs x86, с .NET или без него) в соответствии с текущей запущенной копией.

---

## 🇨🇳 中文

## [1.0.7]

出口节点 IP 与地理位置自动探测、仪表盘国旗图标实时展示、Tor 插件路径空格修复及 DNS 连通性测试按钮修复。

### 🚀 核心更新与亮点
- **动态出口位置与 IP 探测**：内置多数据源探针（`EndpointProbeHelper`），连接就绪后自动经由本地 SOCKS 端口解析真实出口 IP 及国家。
- **仪表盘出口国旗实时显示**：主界面连接卡片直接呈现出口节点所属国旗图标及本地化国家名称，遥测栏同步更新。
- **协议自适应地区选择器**：地区切换面板现在仅在当前协议支持指定出口（如 Psiphon、Tor）时动态呈现。

### 🔧 修复与优化
- **Tor 可插拔传输路径修复**：解决 Windows 下包含空格的插件路径解析异常问题，自动转为 8.3 短路径并在 `torrc` 中避免冲突引号。
- **自定义 DNS 诊断按钮修复**：修复 DNS 设置界面中“测试解析器”按钮因布尔转换器绑定错误导致常态置灰的问题。
- **全协议出口同步**：全面接入 V2Ray、Tor、Psiphon 及本地 SOCKS 核心的出口探测链路。
- **应用内更新暂存区权限修复**：在暂存目录 ACL 中显式加入当前用户权限，并增加隔离备用目录，彻底解决更新下载失败问题。
- **更新包版本精确匹配**：自动更新模块现在能够精准识别当前运行实例的具体构型（安装版 vs 便携版、x64 vs x86、内置运行时 vs 独立版），并下载对应资产。