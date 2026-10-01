<div align="center">

# 🛠️ Se7en Pro — Build & Release Guide

[🇬🇧 English](#-english) | [🇮🇷 فارسی](#-فارسی) | [🇷🇺 Русский](#-русский) | [🇨🇳 中文](#-中文)

</div>

---

## 🇬🇧 English

### 1. Prerequisites

| Requirement | Version | Purpose |
| :--- | :--- | :--- |
| **Windows** | 10 / 11 (x86 or x64) | Target OS for WPF Client |
| **[.NET SDK](https://dotnet.microsoft.com/download/dotnet/8.0)** | 8.0 or newer | Build compiler & runtime |
| **[Python](https://python.org/downloads/)** | 3.10+ | Secret injection script |
| **`cryptography`** (pip) | Latest | AES-256-GCM secret encryption |
| **[Inno Setup 6](https://jrsoftware.org/isinfo.php)** | 6.x | Installer packaging (`.exe`) |

```powershell
# Clone repository
git clone https://github.com/yesmaynameisO/Se7en-Pro.git
cd Se7en-Pro

# Install Python dependency for build secrets
python -m pip install cryptography
```

---

### 2. Everyday Development Build

Builds a fully functional client with placeholder Psiphon credentials. All other engines (Tor, WireGuard WARP, Aether MASQUE, Xray, Sing-box, SHARD) work out of the box without secrets.

```powershell
# Compile x64 Release
dotnet build Se7enPro\Se7enPro.csproj -c Release -r win-x64

# Compile x86 Release
dotnet build Se7enPro\Se7enPro.csproj -c Release -r win-x86
```

Output executable path:
`Se7enPro\bin\Release\net8.0-windows10.0.19041.0\win-x64\Se7enPro.exe`

---

### 3. Psiphon Secrets Configuration

1. Set the 10 environment variables in your PowerShell terminal:
```powershell
$env:SE7EN_PROPAGATION_CHANNEL_ID    = 'your-channel'
$env:SE7EN_SPONSOR_ID                = 'your-sponsor'
$env:SE7EN_CLIENT_VERSION            = '1'
$env:SE7EN_CLIENT_PLATFORM           = 'Windows'
$env:SE7EN_REMOTE_SERVER_LIST_SIGNATURE_PUBLIC_KEY = 'base64-DER-key'
$env:SE7EN_SERVER_ENTRY_SIGNATURE_PUBLIC_KEY        = 'base64-DER-key'
$env:SE7EN_FEEDBACK_ENCRYPTION_PUBLIC_KEY           = 'base64-key'
$env:SE7EN_REMOTE_SERVER_LIST_URLS_JSON       = '[{"URL":"...","OnlyAfterAttempts":0,"SkipVerify":false}]'
$env:SE7EN_OBFUSCATED_SERVER_LIST_ROOT_URLS_JSON = '[{"URL":"...","OnlyAfterAttempts":0,"SkipVerify":false}]'
$env:SE7EN_FEEDBACK_UPLOAD_URLS_JSON       = '[{"URL":"...","OnlyAfterAttempts":0,"SkipVerify":false}]'
```

2. Run secret generator and compile:
```powershell
python tools\generate_build_secrets.py
dotnet build Se7enPro\Se7enPro.csproj -c Release -r win-x64
```

3. Clean up encrypted secrets after build:
```powershell
Remove-Item Se7enPro\Services\BuildSecrets.g.cs -ErrorAction SilentlyContinue
```

---

### 4. Full Distribution Packaging (Local)

To build full installers and portable ZIP packages locally (both self-contained and framework-dependent variants for x64 and x86):

```powershell
# Build all architectures (x64 and x86) with installers and portables (all variants):
.\tools\build-all.ps1 -Arch all -AllVariants -OutDir dist
```

Outputs generated in `dist/`:
- `Se7enPro_v1.0.5_Setup_x64.exe` (Self-contained 64-bit installer, includes .NET)
- `Se7enPro_v1.0.5_Setup_x64_without_dotnet.exe` (Framework-dependent 64-bit installer)
- `Se7enPro_v1.0.5_Setup_x86.exe` (Self-contained 32-bit installer, includes .NET)
- `Se7enPro_v1.0.5_Setup_x86_without_dotnet.exe` (Framework-dependent 32-bit installer)
- `Se7enPro_v1.0.5_Portable_x64.zip` (Self-contained 64-bit portable archive)
- `Se7enPro_v1.0.5_Portable_x64_without_dotnet.zip` (Framework-dependent 64-bit portable archive)
- `Se7enPro_v1.0.5_Portable_x86.zip` (Self-contained 32-bit portable archive)
- `Se7enPro_v1.0.5_Portable_x86_without_dotnet.zip` (Framework-dependent 32-bit portable archive)
- `SHA256SUMS.txt` (Cryptographic verification checksums)

---

### 5. Automated GitHub Actions Release

1. Ensure the 10 secrets are defined in **Settings → Secrets and variables → Actions**.
2. Verify token permissions in **Settings → Actions → General → Workflow permissions** are set to **Read and write permissions**.
3. Push a version tag:
```powershell
git tag -a v1.0.5 -m "Se7en Pro v1.0.5"
git push origin v1.0.5
```
GitHub Actions automatically builds all 8 package variants, verifies secret encryption, calculates SHA-256 sums, and publishes the official release.

---

## 🇮🇷 فارسی

### ۱. پیش‌نیازهای ساخت و کامپایل

| پیش‌نیاز | نسخه | کاربرد |
| :--- | :--- | :--- |
| **ویندوز** | 10 یا 11 (نسخه 32 یا 64 بیتی) | سیستم‌عامل مقصد برنامه کلاینت WPF |
| **[.NET SDK](https://dotnet.microsoft.com/download/dotnet/8.0)** | 8.0 یا بالاتر | کامپایلر دات‌نت و ران‌تایم |
| **[پایتون](https://python.org/downloads/)** | 3.10 به بالا | اسکریپت تزریق سکرت‌ها در بیلد |
| **کتابخانه `cryptography`** | آخرین نسخه | رمزگذاری AES-256-GCM مقادیر سکرت |
| **[Inno Setup 6](https://jrsoftware.org/isinfo.php)** | نسخه 6.x | ساخت فایل‌های ستاپ نصبی (`.exe`) |

```powershell
# کلون کردن ریپازیتوری
git clone https://github.com/yesmaynameisO/Se7en-Pro.git
cd Se7en-Pro

# نصب کتابخانه پایتون برای رمزگذاری مقادیر
python -m pip install cryptography
```

---

### ۲. کامپایل معمولی و توسعه روزمره

این بیلد بدون نیاز به هیچ کلید یا سکرتی انجام می‌شود و کلاینتی کاملاً سالم تولید می‌کند. پروتکل‌های تور، WireGuard WARP، ماسک، Xray، Sing-box و SHARD بدون نیاز به سکرت کار می‌کنند:

```powershell
# کامپایل نسخه 64 بیتی Release
dotnet build Se7enPro\Se7enPro.csproj -c Release -r win-x64

# کامپایل نسخه 32 بیتی Release
dotnet build Se7enPro\Se7enPro.csproj -c Release -r win-x86
```

مسیر خروجی باینری:
`Se7enPro\bin\Release\net8.0-windows10.0.19041.0\win-x64\Se7enPro.exe`

---

### ۳. تنظیم سکرت‌های شبکه سایفون

۱. متغیرهای ده‌گانه را در پاورشل مقداردهی کنید:
```powershell
$env:SE7EN_PROPAGATION_CHANNEL_ID    = 'your-channel'
$env:SE7EN_SPONSOR_ID                = 'your-sponsor'
$env:SE7EN_CLIENT_VERSION            = '1'
$env:SE7EN_CLIENT_PLATFORM           = 'Windows'
$env:SE7EN_REMOTE_SERVER_LIST_SIGNATURE_PUBLIC_KEY = 'base64-DER-key'
$env:SE7EN_SERVER_ENTRY_SIGNATURE_PUBLIC_KEY        = 'base64-DER-key'
$env:SE7EN_FEEDBACK_ENCRYPTION_PUBLIC_KEY           = 'base64-key'
$env:SE7EN_REMOTE_SERVER_LIST_URLS_JSON       = '[{"URL":"...","OnlyAfterAttempts":0,"SkipVerify":false}]'
$env:SE7EN_OBFUSCATED_SERVER_LIST_ROOT_URLS_JSON = '[{"URL":"...","OnlyAfterAttempts":0,"SkipVerify":false}]'
$env:SE7EN_FEEDBACK_UPLOAD_URLS_JSON       = '[{"URL":"...","OnlyAfterAttempts":0,"SkipVerify":false}]'
```

۲. اجرای اسکریپت تزریق و کامپایل نهایی:
```powershell
python tools\generate_build_secrets.py
dotnet build Se7enPro\Se7enPro.csproj -c Release -r win-x64
```

۳. پاک‌سازی فایل رمزگذاری‌شده موقت پس از اتمام ساخت:
```powershell
Remove-Item Se7enPro\Services\BuildSecrets.g.cs -ErrorAction SilentlyContinue
```

---

### ۴. ساخت پکیج‌های کامل رسمی به صورت لوکال

برای ساخت تمامی فایل‌های نصبی Inno Setup و فایل‌های پرتابل ZIP در سیستم خود (شامل نسخه‌های همراه با دات‌نت و بدون دات‌نت برای هر دو معماری ۳۲ و ۶۴ بیتی):

```powershell
# بیلد کامل با تمام متغیرها (۸ پکیج نصبی و پرتابل):
.\tools\build-all.ps1 -Arch all -AllVariants -OutDir dist
```

فایل‌های تولیدشده در پوشه `dist/`:
- `Se7enPro_v1.0.5_Setup_x64.exe` (فایل نصبی ۶۴ بیتی مستقل همراه با دات‌نت)
- `Se7enPro_v1.0.5_Setup_x64_without_dotnet.exe` (فایل نصبی ۶۴ بیتی وابسته به ران‌تایم دات‌نت)
- `Se7enPro_v1.0.5_Setup_x86.exe` (فایل نصبی ۳۲ بیتی مستقل همراه با دات‌نت)
- `Se7enPro_v1.0.5_Setup_x86_without_dotnet.exe` (فایل نصبی ۳۲ بیتی وابسته به ران‌تایم دات‌نت)
- `Se7enPro_v1.0.5_Portable_x64.zip` (نسخه پرتابل ۶۴ بیتی همراه با دات‌نت)
- `Se7enPro_v1.0.5_Portable_x64_without_dotnet.zip` (نسخه پرتابل ۶۴ بیتی بدون دات‌نت)
- `Se7enPro_v1.0.5_Portable_x86.zip` (نسخه پرتابل ۳۲ بیتی همراه با دات‌نت)
- `Se7enPro_v1.0.5_Portable_x86_without_dotnet.zip` (نسخه پرتابل ۳۲ بیتی بدون دات‌نت)
- `SHA256SUMS.txt` (چک‌سام هش‌ها)

---

### ۵. انتشار خودکار نسخه از طریق گیت‌هاب (GitHub Actions)

۱. مطمئن شوید ۱۰ سکرت مربوطه در مسیر **Settings → Secrets and variables → Actions** ریپازیتوری ذخیره شده‌اند.
۲. مطمئن شوید در بخش **Settings → Actions → General → Workflow permissions** دسترسی به **Read and write permissions** تنظیم شده است.
۳. پوش کردن تگ نسخه:
```powershell
git tag -a v1.0.5 -m "Se7en Pro v1.0.5"
git push origin v1.0.5
```
اکشن گیت‌هاب به طور خودکار بیلد هر ۸ پکیج را آغاز کرده، سکرت‌ها را رمزنگاری می‌کند و همه فایل‌ها را در صفحه Releases منتشر می‌نماید.

---

## 🇷🇺 Русский

### 1. Системные требования для сборки

| Требование | Версия | Назначение |
| :--- | :--- | :--- |
| **Windows** | 10 / 11 (x86 или x64) | Целевая ОС для клиента WPF |
| **[.NET SDK](https://dotnet.microsoft.com/download/dotnet/8.0)** | 8.0 или выше | Компилятор и среда выполнения |
| **[Python](https://python.org/downloads/)** | 3.10+ | Скрипт внедрения секретов |
| **`cryptography`** (pip) | Последняя | Шифрование секретов AES-256-GCM |
| **[Inno Setup 6](https://jrsoftware.org/isinfo.php)** | 6.x | Сборка установщиков (`.exe`) |

```powershell
# Клонирование репозитория
git clone https://github.com/yesmaynameisO/Se7en-Pro.git
cd Se7en-Pro

# Установка зависимостей Python
python -m pip install cryptography
```

---

### 2. Повседневная сборка для разработки

Компилирует полностью работоспособный клиент с плейсхолдерами для Psiphon. Все остальные протоколы (Tor, WireGuard WARP, Aether MASQUE, Xray, Sing-box, SHARD) работают сразу без секретов:

```powershell
# Сборка x64 Release
dotnet build Se7enPro\Se7enPro.csproj -c Release -r win-x64

# Сборка x86 Release
dotnet build Se7enPro\Se7enPro.csproj -c Release -r win-x86
```

---

### 3. Настройка секретов Psiphon

1. Задайте переменные окружения в PowerShell:
```powershell
$env:SE7EN_PROPAGATION_CHANNEL_ID    = 'your-channel'
$env:SE7EN_SPONSOR_ID                = 'your-sponsor'
$env:SE7EN_CLIENT_VERSION            = '1'
$env:SE7EN_CLIENT_PLATFORM           = 'Windows'
$env:SE7EN_REMOTE_SERVER_LIST_SIGNATURE_PUBLIC_KEY = 'base64-DER-key'
$env:SE7EN_SERVER_ENTRY_SIGNATURE_PUBLIC_KEY        = 'base64-DER-key'
$env:SE7EN_FEEDBACK_ENCRYPTION_PUBLIC_KEY           = 'base64-key'
$env:SE7EN_REMOTE_SERVER_LIST_URLS_JSON       = '[{"URL":"...","OnlyAfterAttempts":0,"SkipVerify":false}]'
$env:SE7EN_OBFUSCATED_SERVER_LIST_ROOT_URLS_JSON = '[{"URL":"...","OnlyAfterAttempts":0,"SkipVerify":false}]'
$env:SE7EN_FEEDBACK_UPLOAD_URLS_JSON       = '[{"URL":"...","OnlyAfterAttempts":0,"SkipVerify":false}]'
```

2. Сгенерируйте зашифрованный файл секретов и соберите проект:
```powershell
python tools\generate_build_secrets.py
dotnet build Se7enPro\Se7enPro.csproj -c Release -r win-x64
```

3. Удалите временный сгенерированный файл:
```powershell
Remove-Item Se7enPro\Services\BuildSecrets.g.cs -ErrorAction SilentlyContinue
```

---

### 4. Полная локальная сборка дистрибутивов

```powershell
# Сборка x64 и x86 со всеми установщиками и портативными версиями (с .NET и без .NET):
.\tools\build-all.ps1 -Arch all -AllVariants -OutDir dist
```

Файлы в директории `dist/`:
- `Se7enPro_v1.0.5_Setup_x64.exe`
- `Se7enPro_v1.0.5_Setup_x64_without_dotnet.exe`
- `Se7enPro_v1.0.5_Setup_x86.exe`
- `Se7enPro_v1.0.5_Setup_x86_without_dotnet.exe`
- `Se7enPro_v1.0.5_Portable_x64.zip`
- `Se7enPro_v1.0.5_Portable_x64_without_dotnet.zip`
- `Se7enPro_v1.0.5_Portable_x86.zip`
- `Se7enPro_v1.0.5_Portable_x86_without_dotnet.zip`
- `SHA256SUMS.txt`

---

### 5. Автоматический релиз через GitHub Actions

1. Добавьте 10 секретов в **Settings → Secrets and variables → Actions**.
2. Включите **Read and write permissions** в **Settings → Actions → General → Workflow permissions**.
3. Создайте и отправьте тег:
```powershell
git tag -a v1.0.5 -m "Se7en Pro v1.0.5"
git push origin v1.0.5
```

---

## 🇨🇳 中文

### 1. 源码构建环境准备

| 依赖环境 | 推荐版本 | 作用说明 |
| :--- | :--- | :--- |
| **Windows** | 10 / 11 (x86 或 x64) | 客户端运行与编译目标操作系统 |
| **[.NET SDK](https://dotnet.microsoft.com/download/dotnet/8.0)** | 8.0 或更高版本 | C# / WPF 项目编译工具与运行时 |
| **[Python](https://python.org/downloads/)** | 3.10+ | 构建时密钥注入脚本解释器 |
| **`cryptography`** (pip) | 最新版 | 用于对敏感凭据进行 AES-256-GCM 加密 |
| **[Inno Setup 6](https://jrsoftware.org/isinfo.php)** | 6.x | 打包生成标准 `.exe` 安装程序 |

```powershell
# 克隆代码仓库
git clone https://github.com/yesmaynameisO/Se7en-Pro.git
cd Se7en-Pro

# 安装构建辅助 Python 模块
python -m pip install cryptography
```

---

### 2. 日常开发调试编译

该方式无需注入任何商业私钥或凭证，编译生成的客户端完全可用。Tor、WireGuard WARP、Aether MASQUE、Xray、Sing-box 和 SHARD 协议可直接开箱使用：

```powershell
# 编译 x64 发行版
dotnet build Se7enPro\Se7enPro.csproj -c Release -r win-x64

# 编译 x86 发行版
dotnet build Se7enPro\Se7enPro.csproj -c Release -r win-x86
```

---

### 3. 配置 Psiphon 商业接入密钥

1. 在 PowerShell 终端设置 10 项标准环境变量：
```powershell
$env:SE7EN_PROPAGATION_CHANNEL_ID    = 'your-channel'
$env:SE7EN_SPONSOR_ID                = 'your-sponsor'
$env:SE7EN_CLIENT_VERSION            = '1'
$env:SE7EN_CLIENT_PLATFORM           = 'Windows'
$env:SE7EN_REMOTE_SERVER_LIST_SIGNATURE_PUBLIC_KEY = 'base64-DER-key'
$env:SE7EN_SERVER_ENTRY_SIGNATURE_PUBLIC_KEY        = 'base64-DER-key'
$env:SE7EN_FEEDBACK_ENCRYPTION_PUBLIC_KEY           = 'base64-key'
$env:SE7EN_REMOTE_SERVER_LIST_URLS_JSON       = '[{"URL":"...","OnlyAfterAttempts":0,"SkipVerify":false}]'
$env:SE7EN_OBFUSCATED_SERVER_LIST_ROOT_URLS_JSON = '[{"URL":"...","OnlyAfterAttempts":0,"SkipVerify":false}]'
$env:SE7EN_FEEDBACK_UPLOAD_URLS_JSON       = '[{"URL":"...","OnlyAfterAttempts":0,"SkipVerify":false}]'
```

2. 运行加密注入并编译：
```powershell
python tools\generate_build_secrets.py
dotnet build Se7enPro\Se7enPro.csproj -c Release -r win-x64
```

3. 构建完成后可清理本地生成的临时文件：
```powershell
Remove-Item Se7enPro\Services\BuildSecrets.g.cs -ErrorAction SilentlyContinue
```

---

### 4. 本地完整发布包打包

```powershell
# 一键生成全部 8 种安装程序与便携版（含 .NET 与不含 .NET 版本）：
.\tools\build-all.ps1 -Arch all -AllVariants -OutDir dist
```

产物列表：
- `Se7enPro_v1.0.5_Setup_x64.exe`
- `Se7enPro_v1.0.5_Setup_x64_without_dotnet.exe`
- `Se7enPro_v1.0.5_Setup_x86.exe`
- `Se7enPro_v1.0.5_Setup_x86_without_dotnet.exe`
- `Se7enPro_v1.0.5_Portable_x64.zip`
- `Se7enPro_v1.0.5_Portable_x64_without_dotnet.zip`
- `Se7enPro_v1.0.5_Portable_x86.zip`
- `Se7enPro_v1.0.5_Portable_x86_without_dotnet.zip`
- `SHA256SUMS.txt`

---

### 5. GitHub Actions 自动化持续集成与发布

1. 在 GitHub 仓库设置 **Settings → Secrets and variables → Actions** 中配置这 10 个密钥。
2. 确保在 **Settings → Actions → General → Workflow permissions** 中勾选 **Read and write permissions**。
3. 推送版本标签以触发全自动构建：
```powershell
git tag -a v1.0.5 -m "Se7en Pro v1.0.5"
git push origin v1.0.5
```