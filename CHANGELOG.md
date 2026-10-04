<div align="center">

# 📝 Se7en Pro — Changelog

[🇬🇧 English](#-english) | [🇮🇷 فارسی](#-فارسی) | [🇷🇺 Русский](#-русский) | [🇨🇳 中文](#-中文)

</div>

---

## 🇬🇧 English

## [1.0.6]

Comprehensive upgrade to Aether 2.3.0, custom DNS resolvers, pre-provisioned identity pooling, ECH & TLS fingerprint shaping, and critical security hardening.

### 🚀 Highlights & Features
- **Aether 2.3.0 Core & MASQUE in MASQUE (MIM)**: Integrated latest Aether core with native support for dual-nested MASQUE in MASQUE tunneling and turbo scanning mode.
- **ECH & TLS Fingerprint Shaping**: Added Encrypted Client Hello (ECH) support with automated fallback, plus customizable TLS fingerprint profiles (Chrome, Safari, iOS, Random, Off).
- **Identity Pool & Bootstrap Relays**: Background pre-provisioned Cloudflare identity pool with decentralized bootstrap relay fallbacks, guaranteeing zero-bootstrapping connectivity even on restricted networks without an active secondary VPN.
- **Custom DNS Subsystem**: Added fine-grained resolver management supporting Plain DNS (UDP), DNS over TLS (DoT), and DNS over HTTPS (DoH), with strict-resolver enforcement and live diagnostics probe.
- **Chained Multi-Hop Progress & Isolation**: Clear multi-step status feedback (`[1/2] Outer -> [2/2] Inner`), instant cancellation during tunnel establishment, and isolated transport settings per protocol.

### 🔧 Fixes & Improvements
- **Route Table Zero-Leak Protection**: Restructured route reapplication logic to preserve catch-all default routes during dynamic route updates, eliminating full-tunnel leak windows.
- **WFP Kill Switch Synchronization**: Hardened persistent WFP firewall filter state handling, preventing DNS or route leakage across reconnects and tunnel interruptions.
- **Identity ACL Hardening**: Enforced strict Windows ACL security permissions restricting stored identity files exclusively to SYSTEM, Administrators, and the active user.
- **Offline Reliability & Test Harness**: Added comprehensive offline test harness (371 checks) validating contract integrity, route invariants, DNS resolution policies, and identity failover.

---

## 🇮🇷 فارسی

## [نسخه 1.0.6]

ارتقای جامع هسته به Aether 2.3.0، دی‌ان‌اس اختصاصی (Custom DNS)، استخر پیش‌ساخته هویت وارپ، ECH و فرم‌دهی TLS، و ایمن‌سازی عمیق ارتباطات و تانلینگ.

### 🚀 قابلیت‌ها و تغییرات کلیدی
- **ارتقا به هسته Aether 2.3.0 و Masque in Masque (MIM)**: پشتیبانی از پروتکل جدید تانل تو در توی مسک، اسکن بسیار سریع Turbo و گزینه‌های بهینه‌سازی پیشرفته.
- **پشتیبانی از ECH و جعل اثرانگشت TLS**: ثبت‌نام امن از طریق Encrypted Client Hello با مکانیزم بازگشت خودکار، و پروفایل‌های شبیه‌سازی اثرانگشت TLS (کروم، سافاری، iOS و تصادفی).
- **استخر هویت و رله‌های پشتیبان (Identity Pool & Relays)**: ساخت خودکار مخزن هویت‌های وارپ در پس‌زمینه و رله‌های مستقل جهت عبور از فیلترینگ بدون نیاز به فیلترشکن اولیه برای دریافت کلید.
- **مدیریت پیشرفته DNS اختصاصی**: پشتیبانی کامل از UDP معمولی، DNS over TLS (DoT) و DNS over HTTPS (DoH) با امکان تست زنده اتصال و حالت ایزوله سخت‌گیرانه (Strict).
- **نمایش گام‌به‌گام اتصال Chained و لغو سریع**: ارائه جزئیات اتصال در هر مرحله (`[1/2] لایه بیرونی -> [2/2] لایه درونی`) و امکان لغو فوری در حال اتصال.

### 🔧 بهینه‌سازی‌ها و رفع اشکالات
- **حذف نشت ترافیک در روتینگ**: بازنویسی الگوی اعمال روت‌های ویندوز بدون حذف روت پیش‌فرض در حین به‌روزرسانی (Zero-Leak).
- **پایداری کیل‌سوییچ WFP**: هماهنگ‌سازی ایزولاسیون فایروال در قطع و وصل مکرر و جلوگیری قطعی از هرگونه نشت DNS یا ترافیک.
- **امن‌سازی دسترسی فایل‌های هویت**: اعمال سطوح دسترسی امنیتی ACL ویندوز به منظور دسترسی انحصاری کلاینت و جلوگیری از خوانده شدن هویت‌ها توسط سایر پروسس‌ها.
- **آزمون‌های جامع اعتبارسنجی**: افزودن ۳۷۱ تست آفلاین مستقل برای راستی‌آزمایی دقیق الگوریتم‌های تانل، روت، دی‌ان‌اس و مدیریت سوییچینگ.

---

## 🇷🇺 Русский

## [1.0.6]

Масштабное обновление: ядро Aether 2.3.0, поддержка пользовательских DNS, пул идентификаторов Cloudflare, ECH и маскировка TLS, а также устранение утечек трафика.

### 🚀 Ключевые изменения
- **Обновление ядра Aether 2.3.0 и MASQUE in MASQUE (MIM)**: Поддержка вложенного туннелирования MIM, турбо-сканирование и обновленный стек обхода блокировок.
- **Поддержка ECH и профилирование отпечатков TLS**: Шифрование приветствия клиента (ECH) с автопереключением и шаблоны маскировки TLS (Chrome, Safari, iOS, Random, Off).
- **Пул идентификаторов и резервные реле**: Фоновый пул готовых ключей WARP и независимые реле для первого запуска без стороннего VPN.
- **Пользовательские DNS-серверы**: Поддержка Plain UDP, DoT и DoH с проверкой доступности в реальном времени и строгой изоляцией.
- **Индикация многоэтапного подключения**: Пошаговый статус цепочек (`[1/2] Внешний -> [2/2] Внутренний`) и возможность мгновенной отмены подключения.

### 🔧 Исправления и улучшения
- **Устранение утечек маршрутизации (Zero-Leak)**: Безопасное обновление таблицы маршрутизации без временного сброса шлюза по умолчанию.
- **Надежность Kill Switch**: Синхронизация правил WFP при разрывах связи и предотвращение утечек DNS.
- **Защита файлов учетных данных**: Ограничение прав доступа Windows ACL только для пользователя и системы.
- **Тестовый комплекс**: 371 автоматический тест валидации логики туннелирования, маршрутизации и отказоустойчивости.

---

## 🇨🇳 中文

## [1.0.6]

全面升级至 Aether 2.3.0 核心、自定义 DNS 解析器子系统、WARP 身份池与引导中继、ECH 与 TLS 指纹伪装，以及多项底层安全防护加固。

### 🚀 核心更新与亮点
- **升级至 Aether 2.3.0 及 MASQUE in MASQUE (MIM)**：原生集成双层嵌套 MIM 协议，引入 Turbo 高速并发探测模式。
- **ECH 与 TLS 指纹模拟**：支持 Encrypted Client Hello (ECH) 安全注册与多级回退机制，提供多种主流浏览器 TLS 指纹模拟策略。
- **预热身份池与自建引导中继**：后台自动储备 Cloudflare WARP 节点身份凭据，配合去中心化中继，首次启动无需前置网络工具辅助。
- **自定义 DNS 子系统**：全面支持标准 UDP、DoT 及 DoH 协议，内置在线延迟与连通性测试及严格隔离模式。
- **链式多跳状态优化与即时取消**：清晰展示双层节点就绪进度（`[1/2] 外层 -> [2/2] 内层`），支持在任意连接握手阶段即时取消。

### 🔧 修复与优化
- **路由无泄漏保护 (Zero-Leak)**：重构路由表增量更新机制，杜绝动态路由切换过程中的瞬时流量旁路泄露。
- **WFP 阻断机制强化**：完善重连与异常中断场景下的防火墙规则隔离，彻底杜绝 DNS 污染及真实 IP 暴露。
- **凭据文件访问控制强化**：为本地身份凭据文件严格应用 Windows ACL 权限，仅允许当前系统和用户访问。
- **完备的离线测试验证**：新增 371 项自动化离线决策测试，确保路由状态、DNS 转发和故障转移逻辑高度可靠。