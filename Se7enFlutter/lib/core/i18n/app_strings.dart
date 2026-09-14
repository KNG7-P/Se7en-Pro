import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../services/providers.dart';

class AppStrings {
  const AppStrings(this.lang);
  final String lang;

  bool get isRu => lang == 'ru';
  bool get isZh => lang == 'zh';
  bool get isFa => lang == 'fa';
  bool get isEn => !isRu && !isZh && !isFa;

  TextDirection get textDirection => isFa ? TextDirection.rtl : TextDirection.ltr;

  String get navDashboard => isZh ? '仪表盘' : (isRu ? 'Панель' : 'Dashboard');
  String get navSplitTunnel => isZh ? '分流规则' : (isRu ? 'Раздельный туннель' : 'Split Tunnel');
  String get navLogs => isZh ? '日志与通知' : (isRu ? 'Журнал и логи' : 'Notices & Logs');
  String get navSettings => isZh ? '设置' : (isRu ? 'Настройки' : 'Settings');
  String get navAbout => isZh ? '关于' : (isRu ? 'О программе' : 'About');

  String get titleDashboard => isZh ? 'VPN 控制面板' : (isRu ? 'Панель управления VPN' : 'VPN Dashboard');
  String get titleSplitTunnel => isZh ? '路由与分流规则' : (isRu ? 'Маршрутизация и раздельный туннель' : 'Split Tunnel Rules & Routing');
  String get titleLogs => isZh ? '连接日志与活动' : (isRu ? 'Журнал подключений и активности' : 'Connection Notices & Activity');
  String get titleSettings => isZh ? '高级设置' : (isRu ? 'Расширенные настройки' : 'Advanced Preferences');
  String get titleAbout => isZh ? '关于与鸣谢' : (isRu ? 'О программе и разработчиках' : 'About & Credits');

  String get connect => isZh ? '连接' : (isRu ? 'ПОДКЛЮЧИТЬ' : 'CONNECT');
  String get disconnect => isZh ? '断开' : (isRu ? 'ОТКЛЮЧИТЬ' : 'DISCONNECT');
  String get statusDisconnected => isZh ? '未连接' : (isRu ? 'ОТКЛЮЧЕНО' : 'DISCONNECTED');
  String get statusConnecting => isZh ? '连接中...' : (isRu ? 'ПОДКЛЮЧЕНИЕ...' : 'CONNECTING...');
  String get statusConnected => isZh ? '已连接' : (isRu ? 'ПОДКЛЮЧЕНО' : 'CONNECTED');
  String get statusError => isZh ? '错误' : (isRu ? 'ОШИБКА' : 'ERROR');

  String get subtextDisconnected => isZh
      ? '点击启动受保护的多跳加密隧道'
      : (isRu ? 'Нажмите для запуска защищенного туннеля' : 'Click to initiate protected circumvention tunnel');
  String get subtextConnecting => isZh
      ? '正在建立与安全出口节点的连接...'
      : (isRu ? 'Установка соединения с защищенным узлом...' : 'Connecting to secure exit nodes...');
  String get subtextConnected => isZh
      ? '多跳加密隧道已激活并受保护'
      : (isRu ? 'Защищенный туннель успешно активен' : 'Encrypted multi-hop tunnel active');
  String get subtextError => isZh
      ? '无法建立隧道连接'
      : (isRu ? 'Сбой установки соединения' : 'Failed to establish tunnel connection');

  String get connectionProtocol => isZh ? '连接协议' : (isRu ? 'ПРОТОКОЛ ПОДКЛЮЧЕНИЯ' : 'CONNECTION PROTOCOL');
  String get tunAdapter => isZh ? 'TUN 虚拟网卡' : (isRu ? 'TUN-адаптер' : 'TUN Adapter');
  String get exitLocation => isZh ? '出口地区选择' : (isRu ? 'ВЫБОР РЕГИОНА ВЫХОДА' : 'EXIT LOCATION & EGRESS');
  String get networkTelemetry => isZh ? '网络遥测' : (isRu ? 'ТЕЛЕМЕТРИЯ' : 'NETWORK TELEMETRY');
  String get download => isZh ? '下载' : (isRu ? 'СКАЧИВАНИЕ' : 'DOWNLOAD');
  String get upload => isZh ? '上传' : (isRu ? 'ОТПРАВКА' : 'UPLOAD');
  String get latency => isZh ? '延迟' : (isRu ? 'ЗАДЕРЖКА' : 'LATENCY');
  String get liveTrafficMonitor => isZh ? '实时流量监控' : (isRu ? 'Мониторинг трафика в реальном времени' : 'Live Traffic Monitor');
  String get encryptionStandby => isZh ? '加密待机中' : (isRu ? 'Шифрование в режиме ожидания' : 'Encryption Standby');
  String get trafficRouted => isZh ? '所有流量通过所选协议路由' : (isRu ? 'Весь трафик направляется через выбранный протокол' : 'All traffic routes via chosen protocol');

  String get tabGeneral => isZh ? '常规' : (isRu ? 'Общие' : 'General');
  String get tabNetwork => isZh ? '网络' : (isRu ? 'Сеть' : 'Network');
  String get tabPsiphon => 'Psiphon';
  String get tabAether => 'Aether';
  String get tabWarp => 'Warp';
  String get tabTor => 'Tor';
  String get tabUltrasurf => 'Ultrasurf';
  String get tabShard => 'SHARD';
  String get tabChained => isZh ? '多跳链' : (isRu ? 'Цепочки' : 'Chained');
  String get tabSplit => isZh ? '分流' : (isRu ? 'Раздельный туннель' : 'Split Tunnel');

  String get sectionAppearanceLocale => isZh ? '界面外观与语言' : (isRu ? 'Интерфейс и язык' : 'App Interface & Locale');
  String get appearanceMode => isZh ? '主题模式' : (isRu ? 'Тема оформления' : 'Appearance Mode');
  String get appearanceModeDesc => isZh ? '在深色与浅色主题之间切换' : (isRu ? 'Переключение между темной и светлой темой' : 'Switch between sleek Dark and crisp Light theme');
  String get themeDark => isZh ? '午夜深黑' : (isRu ? 'Темная полночь' : 'Midnight Dark');
  String get themeLight => isZh ? '霜雪浅白' : (isRu ? 'Светлый иней' : 'Frost Light');

  String get languageLabel => isZh ? '显示语言' : (isRu ? 'Язык интерфейса' : 'Language');
  String get languageDesc => isZh ? '选择应用程序显示语言' : (isRu ? 'Выберите язык интерфейса приложения' : 'Application display language');

  String get sectionSystemStartup => isZh ? '系统启动与后台行为' : (isRu ? 'Поведение и автозапуск' : 'System Startup & Behavior');
  String get autoConnectLaunch => isZh ? '开机自连' : (isRu ? 'Автоподключение при запуске' : 'Auto-connect on launch');
  String get autoConnectDesc => isZh ? '应用启动时自动连接到首选地区' : (isRu ? 'Мгновенное подключение к выбранному региону' : 'Instantly connect to preferred region on boot');
  String get startWithWindows => isZh ? '随 Windows 启动' : (isRu ? 'Автозапуск с Windows' : 'Start with Windows');
  String get startWithWindowsDesc => isZh ? '在系统登录后静默启动 Se7en 客户端' : (isRu ? 'Запуск клиента Se7en в фоновом режиме при старте системы' : 'Launch Se7en client silently at system startup');
  String get minimizeToTray => isZh ? '最小化到系统托盘' : (isRu ? 'Сворачивать в системный трей' : 'Minimize to System Tray');
  String get minimizeToTrayDesc => isZh ? '关闭主窗口后保持在托盘区运行' : (isRu ? 'Оставлять приложение работать в области уведомлений' : 'Keep running in notification area when closed');

  String get sectionCloseAction => isZh ? '窗口关闭行为' : (isRu ? 'Действие при закрытии окна' : 'Window Close Action');
  String get closeActionLabel => isZh ? '点击关闭按钮时' : (isRu ? 'При нажатии на кнопку закрытия' : 'On Close Button Pressed');
  String get closeActionDesc => isZh ? '点击主界面右上角关闭按钮时的操作' : (isRu ? 'Поведение при закрытии главного окна' : 'Behavior when closing the main window');
  String get closeActionAsk => isZh ? '每次询问' : (isRu ? 'Спрашивать каждый раз' : 'Ask Every Time');
  String get closeActionTray => isZh ? '最小化到托盘' : (isRu ? 'Сворачивать в трей' : 'Minimize to System Tray');
  String get closeActionExit => isZh ? '彻底退出程序' : (isRu ? 'Полный выход из программы' : 'Exit Application Completely');

  String get splitTunnelTitle => isZh ? '分流设置' : (isRu ? 'Раздельное туннелирование' : 'Split Tunneling');
  String get splitTunnelDesc => isZh
      ? '指定特定网站、IP 地址或应用程序绕过或通过 VPN 隧道。'
      : (isRu ? 'Маршрутизация трафика определенных сайтов, IP-адресов или приложений в обход или через VPN' : 'Route specific websites, IP addresses, or applications through or around the VPN tunnel.');
  String get splitTunnelMode => isZh ? '分流模式' : (isRu ? 'Режим разделения' : 'Split Mode');
  String get splitTunnelModeDesc => isZh ? '选择匹配规则流量的处理策略' : (isRu ? 'Выберите способ фильтрации трафика' : 'Select how filtered traffic should be processed');
  String get splitModeBypass => isZh ? '绕过 VPN（直连）' : (isRu ? 'Прямой доступ (В обход VPN)' : 'Bypass VPN (Direct Connection)');
  String get splitModeRoute => isZh ? '仅代理所选目标（其余直连）' : (isRu ? 'Только через VPN (Остальное напрямую)' : 'Route via VPN (Only Listed Targets)');

  String get splitAdminPrivilegeRequired => isZh
      ? '添加、编辑或删除分流规则需要管理员特权。'
      : (isRu ? 'Для добавления, изменения и удаления правил требуются права Администратора.' : 'Administrator privileges required to add, edit or remove split-tunnel rules.');
  String get splitElevateToAdmin => isZh ? '以管理员身份提权' : (isRu ? 'Повысить до Администратора' : 'Elevate to Admin');
  String get splitConfigTitle => isZh ? '分流规则配置' : (isRu ? 'Конфигурация раздельного туннеля' : 'Split Tunnel Configuration');
  String get splitEnableLabel => isZh ? '启用分流功能' : (isRu ? 'Включить раздельный туннель' : 'Enable Split Tunneling');
  String get splitEnableDesc => isZh
      ? '选择特定应用程序、域名或 IP 网段绕过或进入隧道'
      : (isRu ? 'Маршрутизация или пропуск выбранных приложений, доменов и подсетей' : 'Bypass or route specific applications, domains and IP subnets');
  String get splitRoutingPolicy => isZh ? '分流路由策略' : (isRu ? 'Политика маршрутизации' : 'Routing Mode');
  String get splitRoutingPolicyDesc => isZh ? '定义匹配流量的处理策略' : (isRu ? 'Определить правило для выбранных объектов' : 'Define policy for matched traffic items');
  String get splitModeBypassDetailed => isZh
      ? '绕过 VPN（本地直连）'
      : (isRu ? 'В обход туннеля (Прямое локальное подключение)' : 'Bypass Tunnel (Direct Local Connection)');
  String get splitModeRouteDetailed => isZh
      ? '仅代理所选目标通过隧道'
      : (isRu ? 'Маршрутизировать только выбранные объекты' : 'Route Only Selected Items Through Tunnel');
  String get splitTabWebsites => isZh ? '网站与网络' : (isRu ? 'Веб-сайты и сети' : 'Websites & Networks');
  String get splitTabApps => isZh ? '应用程序' : (isRu ? 'Приложения' : 'Applications');
  String get splitDomainRulesTitle => isZh ? '域名与子网规则' : (isRu ? 'Правила доменов и подсетей' : 'Domain & Subnet Rules');
  String splitItemsConfigured(int n) => isZh ? '已配置 $n 项' : (isRu ? 'Настроено элементов: $n' : '$n items configured');
  String get splitAddSiteOrIp => isZh ? '添加网站 / IP' : (isRu ? 'Добавить сайт / IP' : 'Add Site / IP');
  String get splitQuickPresets => isZh ? '快捷预设：' : (isRu ? 'Быстрые пресеты: ' : 'Quick presets: ');
  String get splitNoWebsitesYet => isZh ? '尚未添加任何网站或网段规则' : (isRu ? 'Правила сайтов и подсетей еще не добавлены' : 'No website or subnet rules added yet');
  String get splitAppsTitle => isZh ? '应用程序分流' : (isRu ? 'Разделение для приложений' : 'Application Split Tunneling');
  String get splitAppsSubtitleBypass => isZh
      ? '所选应用将绕过 VPN 采用本地直连'
      : (isRu ? 'Выбранные приложения будут работать напрямую в обход VPN' : 'Selected apps will bypass VPN and connect directly');
  String get splitAppsSubtitleRoute => isZh
      ? '仅所选应用将通过加密隧道'
      : (isRu ? 'Только выбранные приложения будут направляться через туннель' : 'Only selected apps will route through the encrypted tunnel');
  String get splitAddApp => isZh ? '添加应用程序' : (isRu ? 'Добавить приложение' : 'Add Application');
  String get splitNoAppsYet => isZh ? '尚未添加任何应用程序' : (isRu ? 'Приложения еще не добавлены' : 'No Applications Added Yet');
  String get splitNoAppsDesc => isZh
      ? '点击「添加应用程序」从系统程序列表中选取或添加任意可执行文件。'
      : (isRu ? 'Нажмите «Добавить приложение», чтобы выбрать установленные программы или добавить exe-файл.' : 'Click "Add Application" to pick installed or running programs to route or bypass.');
  String get splitAppPickerTitle => isZh ? '添加应用程序至分流规则' : (isRu ? 'Добавить приложение в раздельный туннель' : 'Add Applications to Split Tunnel');
  String get splitAppPickerSubtitle => isZh
      ? '从已安装列表中选择或输入自定义二进制可执行程序'
      : (isRu ? 'Выберите из списка установленных или введите путь к любому exe-файлу' : 'Select apps from the catalog or add any custom executable binary');
  String get splitSearchAppsHint => isZh ? '按名称或 .exe 搜索应用...' : (isRu ? 'Поиск приложений по имени или .exe...' : 'Search applications by name or .exe...');
  String get splitScanningApps => isZh ? '正在扫描已安装的应用程序...' : (isRu ? 'Сканирование установленных приложений...' : 'Scanning installed applications...');
  String get splitNoMatchingApps => isZh ? '未找到匹配的应用程序' : (isRu ? 'Совпадающих приложений не найдено' : 'No matching applications found');
  String get splitCustomExeHintBelow => isZh
      ? '您可以在下方输入可执行程序文件名进行添加。'
      : (isRu ? 'Вы можете добавить любую программу, введя имя файла ниже.' : 'You can add any binary by typing its filename below.');
  String get splitCustomExeInputHint => isZh ? '或输入自定义进程名（例如 MyGame.exe）' : (isRu ? 'Или введите имя процесса (напр. MyGame.exe)' : 'Or enter custom process (e.g. MyGame.exe)');
  String get splitAddCustom => isZh ? '添加自定义' : (isRu ? 'Добавить свое' : 'Add Custom');
  String splitAppsSelected(int n) => isZh ? '已选择 $n 个应用' : (isRu ? 'Выбрано приложений: $n' : '$n apps selected');
  String splitAddSelected(int n) => isZh ? '添加已选 ($n)' : (isRu ? 'Добавить ($n)' : 'Add Selected ($n)');
  String get splitBadgeAdded => isZh ? '已添加' : (isRu ? 'Добавлено' : 'Added');
  String get splitAddRoutingRule => isZh ? '添加路由规则' : (isRu ? 'Добавить правило маршрутизации' : 'Add Routing Rule');
  String get splitAddRoutingRuleDesc => isZh ? '通过隧道路由指定域名或网段 CIDR' : (isRu ? 'Маршрутизация доменов или подсетей CIDR через туннель' : 'Route specific domains or subnet CIDRs via tunnel');
  String get splitDomainFqdn => isZh ? '域名 / FQDN' : (isRu ? 'Домен / FQDN' : 'Domain / FQDN');
  String get splitIpCidr => isZh ? 'IP / CIDR 网段' : (isRu ? 'IP / CIDR подсеть' : 'IP / CIDR Subnet');
  String get splitEnterTargetAddress => isZh ? '请输入目标地址' : (isRu ? 'Пожалуйста, введите адрес' : 'Please enter a target address');
  String get splitInvalidDomain => isZh ? '域名格式不正确（例如 example.com）' : (isRu ? 'Неверный формат домена (напр. example.com)' : 'Invalid domain format (e.g. example.com)');
  String get splitInvalidIp => isZh ? 'IP/CIDR 格式不正确（例如 192.168.1.1 或 10.0.0.0/8）' : (isRu ? 'Неверный формат IP/CIDR (напр. 192.168.1.1 или 10.0.0.0/8)' : 'Invalid IP/CIDR format (e.g. 192.168.1.1 or 10.0.0.0/8)');
  String get splitIncludeSubdomains => isZh ? '包含所有二级与子域名' : (isRu ? 'Включать все поддомены' : 'Include all subdomains');
  String get splitIncludeSubdomainsDesc => isZh ? '自动匹配 *. 通配符前缀（例如 *.example.com）' : (isRu ? 'Автоматически применять префикс *. (напр. *.example.com)' : 'Automatically applies *. wildcard prefix (e.g. *.example.com)');
  String get splitExactOnly => isZh ? '仅精确匹配' : (isRu ? 'Точное совпадение' : 'exact only');
  String get splitWildcardBadge => isZh ? '*.域名' : (isRu ? '*.домен' : '*.domain');
  String get splitResolvingDns => isZh ? '正在解析域名 IP 地址…' : (isRu ? 'Определение IP-адреса домена…' : 'Resolving domain IP address…');
  String get splitResolvedIp => isZh ? '解析目标 IP：' : (isRu ? 'Определен IP-адрес: ' : 'Resolved Target IP: ');
  String get splitResolveDns => isZh ? 'DNS 解析' : (isRu ? 'Определить DNS' : 'Resolve DNS');
  String get splitAddRuleBtn => isZh ? '添加规则' : (isRu ? 'Добавить правило' : 'Add Rule');

  String get addRule => isZh ? '添加规则' : (isRu ? 'Добавить правило' : 'Add Rule');
  String get importRules => isZh ? '导入规则' : (isRu ? 'Импорт правил' : 'Import Rules');
  String get exportRules => isZh ? '导出规则' : (isRu ? 'Экспорт правил' : 'Export Rules');
  String get clearAllRules => isZh ? '清空全部' : (isRu ? 'Очистить все' : 'Clear All');
  String get searchRules => isZh ? '搜索规则...' : (isRu ? 'Поиск правил...' : 'Search rules...');
  String get noRulesFound => isZh ? '尚未配置任何规则' : (isRu ? 'Правил не найдено' : 'No rules configured yet');

  String get logsTitle => isZh ? '活动日志' : (isRu ? 'Журнал событий' : 'Activity Logs');
  String get logsDesc => isZh ? '查看实时系统事件、协议诊断及网络连接遥测。' : (isRu ? 'Просмотр системных событий, диагностики и статусов протоколов' : 'Real-time diagnostic events, protocol notices, and connection telemetry.');
  String get clearLogs => isZh ? '清空' : (isRu ? 'Очистить' : 'Clear');
  String get copyLogs => isZh ? '复制' : (isRu ? 'Копировать' : 'Copy');
  String get exportLogs => isZh ? '导出' : (isRu ? 'Экспорт' : 'Export');
  String get searchLogs => isZh ? '搜索日志...' : (isRu ? 'Поиск в журнале...' : 'Search logs...');
  String get noLogsYet => isZh ? '暂无日志记录' : (isRu ? 'Журнал пуст' : 'No log entries recorded yet');

  String get aboutTitle => 'Se7en Pro';
  String get aboutVersion => 'v1.0.4';
  String get windowsEdition => isZh ? 'Windows 版' : (isRu ? 'Версия для Windows' : 'Windows Edition');
  String get aboutTagline => isZh ? '高性能现代多跳抗封锁与隐私保护客户端' : (isRu ? 'Современный защищенный мультипротокольный клиент обхода блокировок' : 'High-Performance Multi-Hop Circumvention & Privacy Client');
  String get developerLabel => isZh ? '开发者与社区' : (isRu ? 'Разработчик и сообщество' : 'Developer & Community');
  String get telegramChannel => isZh ? 'Telegram 频道' : (isRu ? 'Telegram канал' : 'Telegram Channel');
  String get aboutTelegramSubtitle => isZh ? '官方更新、配置与技术交流频道' : (isRu ? 'Официальный канал обновлений, настроек и обсуждения' : 'Official updates, configurations & discussion channel');
  String get aboutPsiphonTitle => isZh ? 'Psiphon 核心引擎代码仓库' : (isRu ? 'Репозиторий движка Psiphon Core' : 'Psiphon Core Engine Repository');
  String get aboutTechStack => isZh ? '底层核心引擎技术' : (isRu ? 'Базовые технологии движка' : 'Underlying Engine Technologies');

  String get sectionNetworkRouting => isZh ? '虚拟网卡与全局路由' : (isRu ? 'Системный адаптер и маршрутизация' : 'System Adapter & Routing');
  String get systemTunAdapter => isZh ? '系统 TUN 网卡' : (isRu ? 'Системный TUN-адаптер' : 'System TUN Adapter');
  String get systemTunDesc => isZh ? '通过 Wintun 虚拟网卡接管全部操作系统的 TCP/UDP 流量' : (isRu ? 'Маршрутизация всего сетевого трафика через виртуальный адаптер Wintun' : 'Route all OS TCP/UDP traffic through virtual network adapter');
  String get killSwitch => isZh ? '紧急网络开关 (Kill Switch)' : (isRu ? 'Аварийная блокировка (Kill Switch)' : 'Active Kill Switch');
  String get killSwitchDesc => isZh ? '当 VPN 连接意外中断时切断所有网络以防泄漏' : (isRu ? 'Блокировать интернет при неожиданном разрыве соединения' : 'Block all internet connectivity if connection drops');
  String get setSystemProxyLabel => isZh ? '设置 Windows 系统代理' : (isRu ? 'Системный прокси Windows' : 'Set Windows System Proxy');
  String get setSystemProxyDesc => isZh ? '自动配置 WinINet 全局 HTTP/HTTPS 代理' : (isRu ? 'Автоматическая настройка системного HTTP/HTTPS прокси WinINet' : 'Configure WinINet system-wide HTTP/HTTPS proxy');

  String get sectionLocalProxy => isZh ? '本地入站端口与局域网共享 (LAN)' : (isRu ? 'Локальные порты прокси и общий доступ (LAN)' : 'Local Proxy Ports & LAN Sharing');
  String get localProxySubtitle => isZh ? '配置本地 SOCKS5/HTTP 监听端口并允许局域网设备共享代理。' : (isRu ? 'Настройка портов локального прокси (SOCKS5/HTTP) и доступ для устройств в локальной сети' : 'Configure local proxy listening ports and allow other devices on your LAN to connect.');
  String get customProxyPorts => isZh ? '自定义本地端口' : (isRu ? 'Свои порты прокси' : 'Custom Local Proxy Ports');
  String get customProxyPortsDesc => isZh
      ? '手动设置端口（关闭则使用动态自动分配）'
      : (isRu ? 'Ручной выбор портов (выкл = авто)' : 'Manual SOCKS5/HTTP ports (off = auto dynamic)');
  String get dynamicAutoPort => isZh ? '动态（自动分配可用端口）' : (isRu ? 'Автоматический (динамический)' : 'Dynamic (Auto-allocated)');
  String get dynamicPortsAllocated => isZh ? '由底层引擎自动分配无冲突端口' : (isRu ? 'Динамические бесконфликтные порты движка' : 'Dynamic collision-free ports allocated by engine');
  String get customPortAssignment => isZh ? '自定义端口分配' : (isRu ? 'Назначение своих портов' : 'Custom Port Assignment');
  String get autoAssignZero => isZh ? '0 = 自动分配' : (isRu ? '0 = Автовыбор' : '0 = Auto-assign');
  String get socks5Port => isZh ? 'SOCKS5 代理端口' : (isRu ? 'Порт SOCKS5' : 'SOCKS5 Proxy Port');
  String get httpPort => isZh ? 'HTTP 代理端口' : (isRu ? 'Порт HTTP' : 'HTTP Proxy Port');
  String get portCollisionWarning => isZh
      ? 'SOCKS5 与 HTTP 端口不能相同，请分别指定不同端口。'
      : (isRu ? 'Порты SOCKS5 и HTTP не могут совпадать. Укажите разные порты.' : 'SOCKS5 and HTTP ports cannot be identical. Please assign different ports.');
  String get lanAccessAuth => isZh ? '局域网访问身份认证' : (isRu ? 'Аутентификация доступа в LAN' : 'LAN Access Authentication');
  String get optionalBadge => isZh ? '可选' : (isRu ? 'Необязательно' : 'Optional');
  String get lanAuthLeaveBlank => isZh
      ? '留空则允许来自可信局域网设备的免密无限制访问。'
      : (isRu ? 'Оставьте пустым для свободного доступа доверенных устройств в сети.' : 'Leave blank for unrestricted access from trusted LAN devices.');
  String get portAutoHint => isZh ? '1080 (0 = 自动)' : (isRu ? '1080 (0 = Авто)' : '1080 (0 = Auto)');
  String get portAutoHintHttp => isZh ? '8080 (0 = 自动)' : (isRu ? '8080 (0 = Авто)' : '8080 (0 = Auto)');
  String get allowLan => isZh ? '允许局域网连接 (LAN 共享)' : (isRu ? 'Доступ из сети (LAN)' : 'LAN Sharing (Listen on LAN)');
  String get allowLanDesc => isZh
      ? '允许同一局域网内的其他设备接入此代理'
      : (isRu ? 'Доступ к прокси для устройств в локальной сети' : 'Allow other devices on your local network to connect');
  String get lanAuthOptional => isZh ? '局域网身份验证（可选）' : (isRu ? 'Аутентификация для LAN (необязательно)' : 'LAN Authentication (Optional)');
  String get lanUsername => isZh ? '局域网用户名' : (isRu ? 'Имя пользователя LAN' : 'LAN Auth Username');
  String get lanPassword => isZh ? '局域网密码' : (isRu ? 'Пароль LAN' : 'LAN Auth Password');
  String get lanAuthNote => isZh ? '设置后，局域网客户端必须提供此账号密码才能使用代理。' : (isRu ? 'Если указано, клиенты в локальной сети должны пройти авторизацию' : 'If set, LAN clients must authenticate with these credentials.');
  String get portRangeBadge => isZh ? '1–65535 (0=自动)' : (isRu ? '1–65535 (0=Авто)' : '1–65535 (0=Auto)');
  String get portMaxExceeded => isZh ? '最大 65535' : (isRu ? 'Макс 65535' : 'Max 65535');
  String get portRangeGuide => isZh
      ? '有效 TCP 端口范围：1–65535（最大 65535）。填 0 或留空表示自动分配。'
      : (isRu ? 'Допустимый диапазон портов TCP: 1–65535 (максимум 65535). Оставьте пустым или 0 для автовыбора.' : 'Valid TCP port range: 1–65535 (maximum 65535). Enter 0 or leave empty for automatic dynamic port assignment.');

  String get ultraTunWarningTitle => isZh
      ? 'UltraSurf TUN 模式兼容性警告'
      : (isRu ? 'Предупреждение: TUN для UltraSurf' : 'UltraSurf TUN Compatibility Warning');
  String get ultraTunWarningBadge => isZh
      ? '实验性 / 可能不稳定'
      : (isRu ? 'Экспериментально / Нестабильно' : 'Experimental / Unstable');
  String get ultraTunWarningDesc => isZh
      ? 'TUN 模式（系统全局网卡接管）在 UltraSurf 协议下可能出现连接中断、数据丢包或 DNS 异常。'
      : (isRu ? 'Режим TUN (общесистемный Wintun) нестабилен с протоколом UltraSurf и может приводить к разрывам связи или утечкам DNS.' : 'TUN mode (system-wide packet capture) is unstable with the UltraSurf protocol and may cause connection drops or DNS leaks.');
  String get ultraTunWarningTip => isZh
      ? '强烈建议使用「Windows 系统代理」模式以确保最稳定的连接和最快的速度。'
      : (isRu ? 'Настоятельно рекомендуется использовать режим «Системный прокси Windows» для стабильности и максимальной скорости.' : 'It is strongly recommended to use Windows System Proxy mode for reliable stability and full throughput.');
  String get ultraTunUseProxyRecommended => isZh
      ? '使用系统代理模式 (推荐)'
      : (isRu ? 'Использовать системный прокси (Рекомендуется)' : 'Use System Proxy (Recommended)');
  String get ultraTunEnableAnyway => isZh
      ? '仍要开启 TUN'
      : (isRu ? 'Всё равно включить TUN' : 'Enable TUN Anyway');

  String get v2rayMissingConfigTitle => isFa
      ? 'پیکربندی V2Ray انتخاب نشده است'
      : (isZh
          ? '未配置或未选择 V2Ray 节点'
          : (isRu ? 'Конфигурация V2Ray не выбрана' : 'No V2Ray Configuration Selected'));

  String get v2rayMissingConfigBadge => isFa
      ? 'پیکربندی الزامی است'
      : (isZh ? '需要活跃节点' : (isRu ? 'Требуется конфигурация' : 'Active Config Required'));

  String get v2rayMissingConfigDescNoConfigs => isFa
      ? 'برای اتصال با این پروتکل، لطفاً حداقل یک کانفیگ معتبر V2Ray در تنظیمات اضافه و فعال کنید.'
      : (isZh
          ? '要使用该链式协议连接，请先在设置中添加并激活至少一个有效的 V2Ray 节点。'
          : (isRu
              ? 'Для подключения через этот протокол добавьте и активируйте хотя бы одну конфигурацию V2Ray.'
              : 'To connect with this chained protocol, please add and activate at least one valid V2Ray configuration in settings.'));

  String get v2rayMissingConfigDescNoneActive => isFa
      ? 'کانفیگ‌های V2Ray در لیست وجود دارند، اما هیچ‌کدام برای اتصال انتخاب نشده‌اند. لطفاً وارد تنظیمات شوید و یکی از کانفیگ‌ها را فعال کنید.'
      : (isZh
          ? '列表中已有 V2Ray 节点，但当前未选择任何活跃配置。请前往设置激活所需节点。'
          : (isRu
              ? 'В списке есть узлы V2Ray, но ни один не выбран активным. Пожалуйста, активируйте узел в настройках.'
              : 'V2Ray configurations exist, but none is selected as active. Please go to settings and activate a config.'));

  String get v2rayMissingConfigAction => isFa
      ? 'مدیریت و افزودن کانفیگ'
      : (isZh ? '管理与添加节点' : (isRu ? 'Настроить конфигурации' : 'Manage Configurations'));

  String get shardEngineTitle => isZh ? 'SHARD 协议引擎' : (isRu ? 'Движок протокола SHARD' : 'SHARD Protocol Engine');
  String get shardEngineDesc => isZh
      ? '新一代基于 Cloudflare 的 TLS 记录分片引擎 (finalmask)，结合多边缘 CDN 竞速与密码套件锁定。'
      : (isRu ? 'Движок фрагментации записей TLS (finalmask) через Cloudflare с гонками CDN и фиксацией шифров.' : 'Next-generation Cloudflare TLS record fragmentation engine (finalmask) combined with multi-edge CDN racing and cipher suite pinning.');
  String get shardPoolTitle => isZh ? '订阅节点池与连接策略' : (isRu ? 'Пул узлов подписки и политика' : 'Subscription Node Pool & Policy');
  String get shardUpdateFromCloud => isZh ? '从云端同步' : (isRu ? 'Обновить из облака' : 'Update from Cloud');
  String get shardUpdating => isZh ? '正在更新...' : (isRu ? 'Обновление...' : 'Updating...');
  String get shardActiveNodes => isZh ? '活跃节点' : (isRu ? 'Активные узлы' : 'Active Nodes');
  String shardNodesCount(int n) => isZh ? '$n 个节点' : (isRu ? '$n узлов' : '$n Nodes');
  String get shardVlessSeed => isZh ? 'VLESS 与 Trojan 种子节点' : (isRu ? 'Узлы VLESS и Trojan' : 'VLESS & Trojan seed');
  String get shardEdgeMultiplier => isZh ? '边缘倍增' : (isRu ? 'Множитель Edge' : 'Edge Multiplier');
  String get shardCustomEdge => isZh ? '1 个自定义边缘' : (isRu ? '1 свой Edge-узел' : '1 Custom Edge');
  String get shard6EdgeIps => isZh ? '6 个边缘 IP' : (isRu ? '6 IP-адресов Edge' : '6 Edge IPs');
  String get shardDirectUserRouting => isZh ? '用户自定义直接路由' : (isRu ? 'Прямая маршрутизация' : 'Direct user routing');
  String get shardCdnPool => isZh ? 'Cloudflare CDN 节点池' : (isRu ? 'Пул CDN Cloudflare' : 'Cloudflare CDN pool');
  String get shardTotalPaths => isZh ? '并发总路径' : (isRu ? 'Всего путей' : 'Total Paths');
  String shardPathsCount(int n) => isZh ? '$n 条路径' : (isRu ? '$n путей' : '$n Paths');
  String get shardBuiltInSeed => isZh ? '内置种子已激活' : (isRu ? 'Встроенные семена активны' : 'Built-in seed active');

  String get shardRoutingTitle => isZh ? '路由模式与 IP 管理 (Smart Split)' : (isRu ? 'Режим маршрутизации и управление IP' : 'Routing Mode & IP Management');
  String get shardRoutingDesc => isZh
      ? '管理出站流量的路由策略。开启 Smart Split 保留直连速度；关闭则全网统一使用境外节点 IP。'
      : (isRu ? 'Управление маршрутизацией. Smart Split сохраняет прямую скорость; Full Tunnel меняет IP на всех сайтах.' : 'Control outbound traffic routing. Smart Split optimizes line speed; Full Tunnel changes your IP everywhere.');
  String get shardFullTunnelTitle => isZh ? '全流量隧道 (Full Tunnel)' : (isRu ? 'Полный туннель (Full Tunnel)' : 'Full Tunnel');
  String get shardFullTunnelSubtitle => isZh ? '所有网站均使用境外出口 IP' : (isRu ? 'Зарубежный IP на всех сайтах' : 'Foreign Exit IP Everywhere');
  String get shardFullTunnelDesc => isZh
      ? '所有网页和应用均通过远程安全节点。全面更改真实公网 IP（推荐用于隐私保护与跨境业务）。'
      : (isRu ? 'Весь трафик приложений и браузеров направляется через удалённый узел с зарубежным IP.' : 'All websites and applications route through the remote node with a foreign public IP.');
  String get shardFullTunnelBadge => isZh ? '全流量境外 IP' : (isRu ? 'Весь трафик через зарубежный IP' : 'All Traffic via Foreign IP');

  String get shardSmartSplitTitle => isZh ? '智能分流 (Smart Split)' : (isRu ? 'Умное разделение (Smart Split)' : 'Smart Split');
  String get shardSmartSplitSubtitle => isZh ? '直接 TLS 分片直连加速' : (isRu ? 'Прямая TLS-фрагментация' : 'Direct TLS Fragmentation');
  String get shardSmartSplitDesc => isZh
      ? '受阻站点（如 YouTube、Instagram）通过 TLS 分片极速直连；常规及国内站点保留原有本地直连。'
      : (isRu ? 'Заблокированные сервисы открываются на полной скорости через фрагментацию; локальные сайты идут напрямую.' : 'Filtered sites open directly via TLS fragmentation at line speed; clean sites route direct.');
  String get shardSmartSplitBadge => isZh ? '极速直连 • 清洁分流' : (isRu ? 'Макс. скорость • Прямой маршрут' : 'Maximum Speed • Direct Clean Routes');

  String get shardRotateIpTitle => isZh ? '重连时自动轮换 IP' : (isRu ? 'Автосмена IP при переподключении' : 'Auto-Rotate IP on Reconnect');
  String get shardRotateIpDesc => isZh
      ? '每次断开重连时自动切换到不同的优质候选节点及出口 IP。'
      : (isRu ? 'Переключение на новый кандидатный узел и IP-адрес при каждом переподключении.' : 'Cycle to a different candidate node and exit IP whenever you disconnect and reconnect.');
  String get shardRotateIpButton => isZh ? '立即轮换至下一节点' : (isRu ? 'Сменить узел сейчас' : 'Rotate Node / Next IP');
  String get shardActiveNode => isZh ? '已连接活跃节点' : (isRu ? 'Активный узел' : 'Active Node');
  String get shardRotateReady => isZh ? '节点与 IP 轮换就绪' : (isRu ? 'Ротация узлов и IP готова' : 'Node / IP Rotation Ready');
  String get shardRotateConnectedDesc => isZh
      ? '点击下方立即轮换到下一个境外节点并获取全新海外 IP。'
      : (isRu ? 'Нажмите ниже, чтобы переключиться на следующий узел и получить новый зарубежный IP.' : 'Click below to instantly rotate to the next server node and get a new foreign IP.');
  String get shardRotateDisconnectedDesc => isZh
      ? '点击下方提前轮换节点批次，以便在下次连接时启用。'
      : (isRu ? 'Нажмите ниже, чтобы подготовить новый узел для следующего подключения.' : 'Click below to advance to the next node batch for your next connection.');
  String get shardRotateIpNow => isZh ? '立即轮换 IP' : (isRu ? 'Сменить IP сейчас' : 'Rotate IP Now');
  String get shardRotating => isZh ? '正在轮换...' : (isRu ? 'Ротация...' : 'Rotating...');
  String get shardCleanIpTitle => isZh ? 'Cloudflare 优质优选 IP / 自定义边缘节点' : (isRu ? 'Чистый IP Cloudflare / Свой узел Edge' : 'Cloudflare Clean IP / Custom Edge Endpoint');
  String get shardCleanIpDesc => isZh
      ? '用指定的优选 Clean IP 或域名覆盖 Cloudflare CDN 边缘节点。设置后，所有 SHARD 配置将直接通过此端点连接。'
      : (isRu ? 'Замена стандартных CDN-адресов Cloudflare на чистый IP или домен. Все конфиги SHARD пойдут через него.' : 'Override Cloudflare CDN edge addresses with your preferred clean IP or domain. If set, all SHARD configurations will tunnel directly through this endpoint.');
  String get shardCleanIpLabel => isZh ? '优选 Cloudflare IP 或域名' : (isRu ? 'Чистый IP или домен Cloudflare' : 'Clean Cloudflare IP or Domain');
  String get shardCleanIpHint => isZh
      ? '例如 104.16.0.1 或 172.67.0.1 或 ts.hsc.im (留空为自动)'
      : (isRu ? 'напр. 104.16.0.1 или 172.67.0.1 (пусто = авто)' : 'e.g. 104.16.0.1 or 172.67.0.1 or ts.hsc.im (Leave empty for automatic)');
  String get shardPresetsLabel => isZh ? '预设：' : (isRu ? 'Пресеты:' : 'Presets:');
  String get shardClear => isZh ? '清除' : (isRu ? 'Сброс' : 'Clear');

  String get chainedArchitecture => isZh ? '多跳链式隧道架构' : (isRu ? 'Архитектура цепочечного туннеля' : 'Chained Tunnel Architecture');
  String get chainedMode => isZh ? '链式组合模式' : (isRu ? 'Режим цепочки' : 'Chained Mode');
  String get chainedModeDesc => isZh ? '选择多跳封装或上游代理链式级联方法' : (isRu ? 'Выберите способ каскадирования или многозвенного туннелирования' : 'Select multi-hop encapsulation or proxy upstream chaining method');

  String get chainedPsiphonWarpTitle => isZh ? 'Psiphon over WARP 链式设置' : (isRu ? 'Настройки Psiphon поверх WARP' : 'Psiphon over WARP Settings');
  String get chainedPsiphonWarpDesc => isZh
      ? '流量在进入 Psiphon 隧道前被封装在 Cloudflare WARP 隧道内，有效规避深度数据包检测 (DPI)。'
      : (isRu ? 'Трафик инкапсулируется внутри Cloudflare WARP перед отправкой в Psiphon, скрывая соединение от DPI.' : 'Traffic is encapsulated inside Cloudflare WARP before entering Psiphon tunnels, masking your connection from deep packet inspection.');
  String get chainedOuterTransport => isZh ? '外层隧道封装传输方式' : (isRu ? 'Транспорт внешнего туннеля' : 'Outer Leg Transport');
  String get chainedOuterTransportDesc => isZh ? 'WARP 隧道外层的传输协议封装方案' : (isRu ? 'Метод инкапсуляции внешнего плеча туннеля WARP' : 'Encapsulation method for WARP tunnel leg');
  String get transportAuto => isZh ? '自动' : (isRu ? 'Авто' : 'Automatic');
  String get transportMasque => 'MASQUE';
  String get transportWireguard => 'WireGuard';
  String get transportDoubleWarp => isZh ? 'WARP on WARP' : (isRu ? 'WARP on WARP' : 'WARP on WARP');

  String get chainedTorWarpTitle => isZh ? 'Tor over WARP 链式设置' : (isRu ? 'Настройки Tor поверх WARP' : 'Tor over WARP Settings');
  String get chainedTorWarpDesc => isZh
      ? 'Tor 洋葱电路通过 Cloudflare WARP 发送，向本地网络和 ISP 彻底隐匿 Tor 守护入口节点。'
      : (isRu ? 'Цепочки Tor направляются через Cloudflare WARP, скрывая входные узлы Tor от локального провайдера.' : 'Tor onion circuits are sent through Cloudflare WARP, hiding Tor entry guard connections from local networks and ISPs.');

  String get chainedUltrasurfWarpTitle => isZh ? 'Ultrasurf over WARP 链式设置' : (isRu ? 'Настройки Ultrasurf поверх WARP' : 'Ultrasurf over WARP Settings');
  String get chainedUltrasurfWarpDesc => isZh
      ? 'Ultrasurf (UltraCore) 流量在对外拨号前封装在 Cloudflare WARP 中，提供双重混淆抗封锁防护。'
      : (isRu ? 'Трафик Ultrasurf (UltraCore) инкапсулируется внутри Cloudflare WARP, обеспечивая двойную маскировку.' : 'Ultrasurf (UltraCore) traffic is encapsulated inside Cloudflare WARP before dialling out, providing double obfuscation against censorship.');

  String get v2rayTorTitle => isZh ? 'Tor over V2Ray / Xray / Sing-Box' : (isRu ? 'Tor поверх V2Ray / Xray / Sing-Box' : 'Tor over V2Ray / Xray / Sing-Box');
  String get v2rayUltrasurfTitle => isZh ? 'Ultrasurf over V2Ray / Xray / Sing-Box' : (isRu ? 'Ultrasurf поверх V2Ray / Xray / Sing-Box' : 'Ultrasurf over V2Ray / Xray / Sing-Box');
  String get v2rayPsiphonTitle => isZh ? 'Psiphon over V2Ray / Xray / Sing-Box' : (isRu ? 'Psiphon поверх V2Ray / Xray / Sing-Box' : 'Psiphon over V2Ray / Xray / Sing-Box');
  String get v2rayTorDesc => isZh
      ? '通过高性能 Xray/Sing-Box 出站路由 Tor 洋葱链路与目录握手，强力规避深度包检测 (DPI)。'
      : (isRu ? 'Маршрутизация Tor через высокоскоростные выходы Xray/Sing-Box для обхода DPI.' : 'Route Tor onion circuits and directory handshakes through high-performance Xray/Sing-Box outbounds for deep packet inspection evasion.');
  String get v2rayUltrasurfDesc => isZh
      ? '将 Ultrasurf (UltraCore) 上游隧道级联至高性能 Xray/Sing-Box 出站，强力规避深度包检测。'
      : (isRu ? 'Каскадирование туннеля Ultrasurf (UltraCore) через Xray/Sing-Box для обхода глубокого анализа пакетов.' : 'Chain Ultrasurf (UltraCore) upstream tunnel through high-performance Xray/Sing-Box outbounds for deep packet inspection evasion.');
  String get v2rayPsiphonDesc => isZh
      ? '将 Psiphon 上游隧道级联至高性能 Xray/Sing-Box 出站，兼具深度抗封锁与 CDN 域名前置优势。'
      : (isRu ? 'Каскадирование туннеля Psiphon через Xray/Sing-Box для устойчивого обхода блокировок и фронтинга.' : 'Chain Psiphon upstream tunnel through high-performance Xray/Sing-Box outbounds for deep packet inspection evasion and CDN fronting.');
  String get v2rayEngineCore => isZh ? '代理核心引擎' : (isRu ? 'Ядро прокси-движка' : 'Proxy Engine Core');
  String get v2rayXrayActive => isZh ? 'Xray 已激活' : (isRu ? 'Xray активен' : 'Xray Active');
  String get v2raySingBoxActive => isZh ? 'Sing-Box 已激活' : (isRu ? 'Sing-Box активен' : 'Sing-Box Active');
  String get v2rayAddConfig => isZh ? '添加节点配置' : (isRu ? 'Добавить узел' : 'Add Configuration');
  String get v2rayImportLink => isZh ? '导入链接 / 剪贴板' : (isRu ? 'Импорт ссылки / Буфер' : 'Import Link / Clipboard');
  String get v2rayTestAllLatency => isZh ? '全部节点测速' : (isRu ? 'Тест всех узлов' : 'Test Latency (All)');
  String get v2rayTesting => isZh ? '测速中...' : (isRu ? 'Тестирование...' : 'Testing...');
  String get v2rayNoConfigs => isZh ? '尚未添加任何 V2Ray / Xray 节点配置' : (isRu ? 'Конфигурации узлов еще не добавлены' : 'No V2Ray / Xray Configurations Added');
  String get v2rayDeletePrompt => isZh ? '删除配置？' : (isRu ? 'Удалить конфигурацию?' : 'Delete Configuration?');
  String v2rayDeleteConfirm(String name) => isZh ? '确定要删除节点配置 "$name" 吗？' : (isRu ? 'Вы уверены, что хотите удалить "$name"?' : 'Are you sure you want to remove "$name"?');
  String get delete => isZh ? '删除' : (isRu ? 'Удалить' : 'Delete');
  String get activeBadge => isZh ? '已激活' : (isRu ? 'Активен' : 'Active');

  String get psiphonSectionTitle => isZh ? 'Psiphon 与 Conduit 引擎控制' : (isRu ? 'Управление движком Psiphon и Conduit' : 'Psiphon & Conduit Engine Controls');
  String get psiphonControlsTitle => psiphonSectionTitle;
  String get protocolModeLabel => isZh ? '协议模式' : (isRu ? 'Режим протокола' : 'Protocol Mode');
  String get protocolModeAuto => isZh ? '自动协商' : (isRu ? 'Автоматическое согласование' : 'Automatic Negotiation');
  String get protocolModeDirect => isZh ? '直连模式 (Direct)' : (isRu ? 'Прямое подключение (Direct)' : 'Direct Connection');
  String get protocolModeCdn => isZh ? 'CDN 域名前置 (Domain Fronting)' : (isRu ? 'Фронтинг доменов CDN' : 'CDN Domain Fronting');
  String get protocolModeConduit => isZh ? 'Conduit 中继 (WebRTC)' : (isRu ? 'Ретранслятор Conduit (WebRTC)' : 'Conduit Relay');

  String get beastModeLabel => isZh ? 'Beast Mode (野兽极速对抗模式)' : (isRu ? 'Режим Beast Mode' : 'Beast Mode');
  String get beastModeDesc => isZh
      ? '针对重度网络封锁的高并发多流竞速算法'
      : (isRu ? 'Агрессивный алгоритм многопоточных гонок для жестких блокировок' : 'Ultra-aggressive multi-stream racing algorithm for heavy censorship');
  String get skipCertVerifyLabel => isZh ? '跳过 TLS 证书验证' : (isRu ? 'Пропустить проверку TLS-сертификатов' : 'Skip TLS Certificate Verification');
  String get skipCertVerifyDesc => isZh
      ? '跳过 CDN 边缘连接证书主机名校验（SNI 伪造必需）'
      : (isRu ? 'Отключить проверку сертификата для CDN (необходимо для спуфинга SNI)' : 'Bypass certificate hostname validation for CDN edge connections (essential for SNI spoofing)');
  String get autoFindCleanIpLabel => isZh ? '自动测速并寻找可用 CDN IP 与 SNI' : (isRu ? 'Автопоиск чистых IP и SNI для CDN' : 'Auto-find Clean IPs & SNI');
  String get autoFindCleanIpDesc => isZh
      ? '持续对边缘 CDN 地址进行基准测速并自动轮换'
      : (isRu ? 'Постоянный тест скорости и ротация рабочих CDN-адресов' : 'Continuously benchmark and rotate working edge CDN addresses');
  String get saveFoundIpLabel => isZh ? '自动保存发现的可用 IP 与 SNI' : (isRu ? 'Сохранять найденные IP и SNI' : 'Save Found IPs & SNI');
  String get saveFoundIpDesc => isZh
      ? '自动将测速可用的 CDN 边缘 IP 与 SNI 保存至自定义列表'
      : (isRu ? 'Автоматически сохранять рабочие IP и SNI в пользовательский список' : 'Automatically persist discovered working CDN edge IPs and SNIs into custom fields');
  String get establishTimeoutLabel => isZh ? '隧道建立超时时间' : (isRu ? 'Тайм-аут установки туннеля' : 'Establish Timeout');
  String get establishTimeoutDesc => isZh ? '首次建立隧道放弃前等待的最大时间' : (isRu ? 'Время ожидания первого туннеля перед отменой' : 'How long to wait for the first tunnel before giving up');
  String get timeoutForever => isZh ? '永久等待 (无超时)' : (isRu ? 'Бесконечно (без тайм-аута)' : 'Forever (no timeout)');
  String get timeout2Min => isZh ? '2 分钟' : (isRu ? '2 минуты' : '2 minutes');
  String get timeout5Min => isZh ? '5 分钟 (推荐)' : (isRu ? '5 минут (рекомендуется)' : '5 minutes (recommended)');
  String get timeout10Min => isZh ? '10 分钟' : (isRu ? '10 минут' : '10 minutes');
  String get timeout30Min => isZh ? '30 分钟' : (isRu ? '30 минут' : '30 minutes');
  String get customCdnIpsLabel => isZh ? '自定义 CDN 边缘 IP 列表' : (isRu ? 'Свои IP-адреса CDN фронтинга' : 'Custom CDN Fronting IPs');
  String get customCdnIpsDesc => isZh
      ? '每行一个 IP（由自动扫描填入或手动输入）'
      : (isRu ? 'По одному IP на строку (заполняется автоматически или вручную)' : 'One IP per line (populated automatically or entered manually)');
  String get customSniLabel => isZh ? '自定义 SNI 域名' : (isRu ? 'Свои имена хостов SNI' : 'Custom SNI Hostnames');
  String get customSniDesc => isZh ? '每行一个主机名' : (isRu ? 'По одному имени хоста на строку' : 'One hostname per line');
  String get conduitRejectCensored => isZh ? '拒绝受审查地区中继节点' : (isRu ? 'Отклонять реле из цензурируемых стран' : 'Reject Censored Relays');
  String get conduitRejectCensoredDesc => isZh
      ? '排除位于网络审查严格地区或高风险管辖区的中继节点'
      : (isRu ? 'Исключить узлы ретрансляции в странах с жесткой цензурой' : 'Exclude relay nodes located in censored jurisdictions or high-risk regions');
  String get conduitStationSelection => isZh ? 'Conduit 中继拓扑' : (isRu ? 'Топология станций Conduit' : 'Conduit Station Selection');
  String get conduitStationDesc => isZh ? '中继节点发现与连接拓扑策略' : (isRu ? 'Обнаружение реле и топология подключения' : 'Relay discovery and connection topology');
  String get conduitAuto => isZh ? '自动中继' : (isRu ? 'Авто-реле' : 'Auto Relay');
  String get conduitPublic => isZh ? '公共网络' : (isRu ? 'Публичная сеть' : 'Public Network');
  String get conduitPeer => isZh ? '私人对等互联 (Peer)' : (isRu ? 'Частный пиринг' : 'Personal Peering');
  String get conduitPeerToken => isZh ? '私人对等互联 / 分区 ID' : (isRu ? 'Токен частного пиринга / ID отсека' : 'Personal Peering / Compartment ID');
  String get conduitPeerTokenHint => isZh ? '输入您的专属 Peer Token 或分区 UUID' : (isRu ? 'Введите персональный токен или UUID отсека' : 'Enter your personal peer token or compartment UUID');

  String get upstreamProxyTitle => isZh ? '上游中间代理中继' : (isRu ? 'Промежуточный прокси-ретранслятор' : 'Upstream Proxy Relay');
  String get upstreamNotSupportedConduit => isZh
      ? 'Conduit (Inproxy WebRTC) 模式不支持上游代理，该模式要求直连对等节点。'
      : (isRu ? 'Промежуточный прокси не поддерживается в режиме Conduit (WebRTC), требующем прямых P2P-соединений.' : 'Upstream proxy is not supported in Conduit (Inproxy WebRTC) mode. Conduit requires direct peer connections.');
  String get upstreamNotSupportedCdn => isZh
      ? 'CDN Fronting 模式不支持上游代理，该模式需要直连 CDN 边缘 IP。'
      : (isRu ? 'Промежуточный прокси не поддерживается в режиме CDN Fronting, требующем прямого подключения к IP CDN.' : 'Upstream proxy is not supported in CDN Fronting mode. CDN Fronting requires direct connections to CDN edge IP addresses.');
  String get upstreamProxyEnable => isZh ? '启用上游中间代理' : (isRu ? 'Включить промежуточный прокси' : 'Enable Upstream Proxy');
  String get upstreamProxyEnableDescPsiphon => isZh
      ? '通过现有中间代理转发 Psiphon 隧道流量'
      : (isRu ? 'Маршрутизация трафика Psiphon через существующий прокси' : 'Route Psiphon tunnel traffic through an existing intermediate proxy');
  String get upstreamProxyEnableDescUltrasurf => isZh
      ? '通过中间 SOCKS5 或 HTTP 代理中继 Ultrasurf 拨号'
      : (isRu ? 'Маршрутизация соединений Ultrasurf через промежуточный SOCKS5 или HTTP прокси' : 'Route Ultrasurf tunnel dials through an intermediate SOCKS5 or HTTP proxy');
  String get upstreamProxyScheme => isZh ? '中间代理协议类型' : (isRu ? 'Тип протокола прокси' : 'Proxy Protocol Type');
  String get upstreamProxySchemeDesc => isZh ? '选择中间代理协议方案' : (isRu ? 'Выберите протокол промежуточного прокси' : 'Select protocol scheme for intermediate proxy');
  String get upstreamSocks5 => isZh ? 'SOCKS5 代理 (推荐)' : (isRu ? 'Прокси SOCKS5 (Рекомендуется)' : 'SOCKS5 Proxy (Recommended)');
  String get upstreamHttp => isZh ? 'HTTP / HTTPS 代理' : (isRu ? 'Прокси HTTP / HTTPS' : 'HTTP / HTTPS Proxy');
  String get upstreamHost => isZh ? '代理主机 / IP' : (isRu ? 'Хост / IP прокси' : 'Proxy Host / IP');
  String get upstreamPort => isZh ? '代理端口' : (isRu ? 'Порт прокси' : 'Proxy Port');
  String get upstreamAuthUser => isZh ? '代理认证用户名' : (isRu ? 'Имя пользователя прокси' : 'Proxy Auth Username');
  String get upstreamAuthPass => isZh ? '代理认证密码' : (isRu ? 'Пароль прокси' : 'Proxy Auth Password');

  String get aetherSectionTitle => isZh ? 'Aether 与 Cloudflare Warp' : (isRu ? 'Aether и Cloudflare Warp' : 'Aether & Cloudflare Warp');
  String get aetherProtocolType => isZh ? '协议类型' : (isRu ? 'Тип протокола' : 'Protocol Type');
  String get aetherProtocolDesc => isZh ? 'Aether 核心底层隧道协议' : (isRu ? 'Базовый протокол туннелирования Aether' : 'Aether core tunnelling protocol');
  String get aetherIpVersion => isZh ? 'IP 版本' : (isRu ? 'Версия IP' : 'IP Version');
  String get aetherIpVersionDesc => isZh ? 'Cloudflare 边缘节点网络协议栈' : (isRu ? 'Семейство адресов Cloudflare' : 'Cloudflare endpoint address family');
  String get aetherScanMode => isZh ? '边缘 IP 扫描模式' : (isRu ? 'Режим сканирования узлов' : 'Edge IP Scan Mode');
  String get aetherScanModeDesc => isZh ? 'Cloudflare 边缘节点发现算法' : (isRu ? 'Алгоритм поиска конечных узлов Cloudflare' : 'Cloudflare endpoint discovery algorithm');
  String get aetherScanTurbo => isZh ? '极速 Turbo (最快握手)' : (isRu ? 'Турбо (быстрое рукопожатие)' : 'Turbo (Fastest Handshake)');
  String get aetherScanBalanced => isZh ? '均衡模式 (推荐)' : (isRu ? 'Сбалансированный (Рекомендуется)' : 'Balanced (Recommended)');
  String get aetherScanThorough => isZh ? '深度基准测试' : (isRu ? 'Полный бенчмарк' : 'Thorough Benchmark');
  String get aetherScanStealth => isZh ? '隐蔽抗 DPI 审查' : (isRu ? 'Скрытный (обход DPI)' : 'Stealth DPI Evasion');
  String get aetherScanIronclad => isZh ? '铁壁高抗封锁 (极高韧性)' : (isRu ? 'Непробиваемый (ультра-стойкий)' : 'Ironclad (Ultra Resilient)');
  String get aetherNoise => isZh ? '噪声 / 抖动混淆 (Noise / Jitter)' : (isRu ? 'Шумовая обфускация / джиттер' : 'Noise / Jitter Obfuscation');
  String get aetherNoiseDesc => isZh ? '注入伪装随机干扰数据包以混淆流量特征' : (isRu ? 'Внедрение случайных пакетов для маскировки сигнатур трафика' : 'Inject random junk packets to disguise traffic signatures');
  String get aetherNoiseDisabled => isZh ? '禁用' : (isRu ? 'Отключено' : 'Disabled');
  String get aetherNoiseLight => isZh ? '轻微 (极低 CPU 消耗)' : (isRu ? 'Легкая (низкая нагрузка CPU)' : 'Light (Low CPU)');
  String get aetherNoiseBalanced => isZh ? '均衡' : (isRu ? 'Сбалансированная' : 'Balanced');
  String get aetherNoiseAggressive => isZh ? '激进 (强力穿透深度 DPI)' : (isRu ? 'Агрессивная (глубокий обход DPI)' : 'Aggressive (Deep DPI Bypass)');
  String get aetherPerfProfile => isZh ? '性能配置预设' : (isRu ? 'Профиль производительности' : 'Performance Profile');
  String get aetherPerfProfileDesc => isZh ? '资源调度与后台扫描并发线程' : (isRu ? 'Выделение ресурсов и параллелизм фонового сканирования' : 'Resource allocation & background scan concurrency');
  String get aetherPerfAuto => isZh ? '自动 (硬件自适应)' : (isRu ? 'Авто (адаптация к оборудованию)' : 'Auto (Hardware Adaptive)');
  String get aetherPerfLow => isZh ? '低功耗 (极低 CPU/内存占用)' : (isRu ? 'Низкий (минимум памяти и CPU)' : 'Low (Minimal CPU & RAM Usage)');
  String get aetherPerfMedium => isZh ? '中等 (平衡并发)' : (isRu ? 'Средний (сбалансированный)' : 'Medium (Balanced Concurrency)');
  String get aetherPerfHigh => isZh ? '高性能 (最大并发与吞吐缓存)' : (isRu ? 'Высокий (максимальная скорость и буферы)' : 'High (Maximum Concurrency & Buffers)');
  String get aetherFragment => isZh ? 'Client Hello 数据包分片' : (isRu ? 'Фрагментация Client Hello' : 'Fragment Client Hello');
  String get aetherFragmentDesc => isZh ? '拆分 TLS Client Hello 握手包以规避基于 SNI 的防火墙拦截' : (isRu ? 'Разбиение пакетов TLS Client Hello для обхода блокировок по SNI' : 'Split TLS Client Hello packets to defeat SNI firewalls');
  String get aetherTransport => isZh ? '传输层协议' : (isRu ? 'Транспортный протокол' : 'Transport Protocol');
  String get aetherManualEndpoint => isZh ? '手动指定接入节点' : (isRu ? 'Ручной выбор конечной точки' : 'Manual Endpoint Override');
  String aetherManualEndpointDesc(String p) => isZh ? '$p 协议的自定义接入节点' : (isRu ? 'Свой узел для протокола $p' : 'Custom endpoint for $p protocol');

  String get torOptionsTitle => isZh ? 'Tor 选项与网桥' : (isRu ? 'Параметры и мосты Tor' : 'Tor Options');
  String get torOptionsDesc => isZh ? '用于 Tor 引擎的网桥节点与抗审查中继。' : (isRu ? 'Мосты и ретрансляторы обхода блокировок для движка Tor.' : 'Bridges and circumvention relays for the Tor engine.');
  String get torPresetsTitle => isZh ? '网桥快速预设（可选）' : (isRu ? 'ПРЕСЕТЫ МОСТОВ (НЕОБЯЗАТЕЛЬНО)' : 'BRIDGES PRESETS (OPTIONAL)');
  String get torClearBridges => isZh ? '清空' : (isRu ? 'Очистить' : 'Clear');
  String get torBridgesInputHint => isZh
      ? '每行输入一个网桥配置 — 例如 obfs4 1.2.3.4:443 FINGERPRINT cert=... iat-mode=0'
      : (isRu ? 'одна строка моста на строку — напр. obfs4 1.2.3.4:443 FINGERPRINT cert=... iat-mode=0' : 'one bridge line per row — e.g. obfs4 1.2.3.4:443 FINGERPRINT cert=... iat-mode=0');
  String get torBridgesFootnote => isZh
      ? '粘贴 obfs4 / meek_lite / webtunnel / conjure 网桥以在受限网络下连接 Tor。留空表示直连。'
      : (isRu ? 'Вставьте мосты obfs4 / meek_lite / webtunnel / conjure для обхода цензуры Tor. Оставьте пустым для прямого доступа.' : 'Paste obfs4 / meek_lite / webtunnel / conjure bridge lines to reach Tor on censored networks. Leave empty to connect directly.');
  String get torCoreTitle => isZh ? 'Tor 核心引擎' : (isRu ? 'Движок Tor Core' : 'Tor Core');
  String get torCoreSubtitle => isZh ? '洋葱路由、Lyrebird 与 Conjure 可插拔传输' : (isRu ? 'Луковая маршрутизация, транспорты Lyrebird и Conjure' : 'Onion Routing, Lyrebird & Conjure Pluggable Transports');
  String get torVerifyTooltip => isZh ? '检查 Tor 引擎版本' : (isRu ? 'Проверить версию движка Tor' : 'Verify Tor engine version');

  String get ultrasurfEngineCardTitle => isZh ? 'Ultrasurf (UltraCore) 引擎' : (isRu ? 'Движок Ultrasurf (UltraCore)' : 'Ultrasurf (UltraCore) Engine');
  String get ultrasurfEngineCardDesc => isZh ? '抗封锁核心 — 连接至 Ultrareach 私有专有反审查云。' : (isRu ? 'Ядро обхода блокировок — подключение к облаку Ultrareach.' : 'Anti-censorship core — connects to the Ultrareach private proxy cloud.');

  String get protectorBanner => isZh ? '安全受保护的连接' : (isRu ? 'Защищенное соединение' : 'Protected Connection');
  String get fixedInboundPorts => isZh ? '固定入站代理端口' : (isRu ? 'Фиксированные входящие порты' : 'Fixed Inbound Proxy Ports');
  String get portCollisionMsg => isZh ? 'SOCKS5 与 HTTP 端口冲突，请选择不同端口。' : (isRu ? 'Конфликт портов: SOCKS5 и HTTP не могут использовать один порт.' : 'SOCKS5 and HTTP cannot share the same port. Please change one.');
  String get inboundPortLabel => isFa ? 'پورت ورودی' : (isZh ? '入站端口' : (isRu ? 'Входящий порт' : 'Inbound Port'));
  String get routeDnsLabel => isFa ? 'مسیریابی DNS از طریق V2Ray' : (isZh ? '通过 V2Ray 路由 DNS' : (isRu ? 'Маршрутизировать DNS через V2Ray' : 'Route DNS via V2Ray'));
  String get enableMuxLabel => isFa ? 'فعال‌سازی Mux (تسهیم اتصالات TCP)' : (isZh ? '启用 Mux (TCP 多路复用)' : (isRu ? 'Включить Mux (мультиплексирование TCP)' : 'Enable Mux (TCP Multiplexing)'));
  String get enableMuxDesc => isFa
      ? 'ترکیب چندین اتصال TCP در یک نشست TLS جهت کاهش تاخیر دست‌تکانی'
      : (isZh
          ? '在单一 TLS 会话中复用多个 TCP 连接以降低握手开销'
          : (isRu ? 'Мультиплексирование нескольких TCP-соединений через один поток TLS для снижения задержек' : 'Multiplex multiple TCP connections over a single TLS stream for reduced handshake overhead'));
  String get v2rayFragmentLabel => isFa
      ? 'فرگمنت TLS (تکه‌تکه کردن پکت‌ها - ضد فیلترینگ)'
      : (isZh
          ? 'TLS 分片 (Fragment 防封锁)'
          : (isRu ? 'Фрагментация TLS (Fragment)' : 'TLS Fragmentation (Bypass DPI)'));
  String get v2rayFragmentDesc => isFa
      ? 'تکه‌تکه کردن پکت‌های TLS ClientHello جهت دور زدن فیلترینگ شدید و DPI'
      : (isZh
          ? '拆分 TLS ClientHello 数据包以绕过深度包检测 (DPI)'
          : (isRu
              ? 'Дробление пакетов TLS ClientHello для обхода блокировок DPI'
              : 'Split TLS ClientHello packets into fragments to bypass deep packet inspection (DPI)'));
  String get v2rayFragmentPacketsLabel => isFa ? 'بسته‌های هدف' : (isZh ? '目标数据包' : (isRu ? 'Пакеты' : 'Target Packets'));
  String get v2rayFragmentLengthLabel => isFa ? 'طول قطعات' : (isZh ? '分片长度' : (isRu ? 'Длина' : 'Fragment Length'));
  String get v2rayFragmentIntervalLabel => isFa ? 'فاصله زمانی' : (isZh ? '发送间隔' : (isRu ? 'Интервал' : 'Interval'));

  String get exitDialogTitle => isZh ? '退出 Se7en Pro？' : (isRu ? 'Закрыть Se7en Pro?' : 'Exit Se7en Pro?');
  String get exitDialogDesc => isZh ? '选择关闭应用程序时的行为：' : (isRu ? 'Выберите действие при закрытии приложения:' : 'Choose what happens when closing the application:');
  String get exitOptionTray => isZh ? '最小化到系统托盘（在后台继续运行）' : (isRu ? 'Свернуть в системный трей (продолжать работу)' : 'Minimize to System Tray (Keep running)');
  String get exitOptionExit => isZh ? '彻底退出应用程序' : (isRu ? 'Полностью закрыть приложение' : 'Exit completely');
  String get rememberChoice => isZh ? '记住我的选择' : (isRu ? 'Запомнить мой выбор' : 'Remember my choice');
  String get cancel => isZh ? '取消' : (isRu ? 'Отмена' : 'Cancel');
  String get confirm => isZh ? '确定' : (isRu ? 'Подтвердить' : 'Confirm');

  String get adminElevationTitle => isZh ? '需要管理员特权' : (isRu ? 'Требуются права Администратора' : 'Administrator Privileges Required');
  String get adminElevationDesc => isZh
      ? 'TUN 虚拟网卡需要管理员权限来配置系统路由和数据包捕获。是否以管理员身份重启 Se7en Pro？'
      : (isRu ? 'Для работы TUN-адаптера и маршрутизации трафика требуются права администратора. Перезапустить приложение от имени Администратора?' : 'TUN adapter requires administrator rights to configure system routes and packet filtering. Would you like to restart Se7en Pro as Administrator?');
  String get restartAsAdmin => isZh ? '以管理员权限重启' : (isRu ? 'Перезапустить с правами админа' : 'Restart as Administrator');

  String get chainedPsiphonWarp => isZh ? 'Psiphon over WARP (Cloudflare)' : (isRu ? 'Psiphon поверх WARP (Cloudflare)' : 'Psiphon over WARP (Cloudflare)');
  String get chainedPsiphonV2ray => isZh ? 'Psiphon over V2Ray / Xray' : (isRu ? 'Psiphon поверх V2Ray / Xray' : 'Psiphon over V2Ray / Xray');
  String get chainedTorWarp => isZh ? 'Tor over WARP (Cloudflare)' : (isRu ? 'Tor поверх WARP (Cloudflare)' : 'Tor over WARP (Cloudflare)');
  String get chainedTorV2ray => isZh ? 'Tor over V2Ray / Xray' : (isRu ? 'Tor поверх V2Ray / Xray' : 'Tor over V2Ray / Xray');
  String get chainedUltrasurfWarp => isZh ? 'Ultrasurf over WARP (Cloudflare)' : (isRu ? 'Ultrasurf поверх WARP (Cloudflare)' : 'Ultrasurf over WARP (Cloudflare)');
  String get chainedUltrasurfV2ray => isZh ? 'Ultrasurf over V2Ray / Xray' : (isRu ? 'Ultrasurf поверх V2Ray / Xray' : 'Ultrasurf over V2Ray / Xray');

  String get v2rayBridgeTitle => isZh ? 'Psiphon 上游桥接配置' : (isRu ? 'Интерфейс моста Psiphon к прокси' : 'Psiphon Upstream Bridge Interface');
  String get v2rayEditConfig => isZh ? '编辑节点配置' : (isRu ? 'Редактировать узел' : 'Edit Configuration');
  String get v2rayEditConfigDesc => isZh ? '修改代理服务器参数与连接凭证' : (isRu ? 'Изменить параметры сервера и учетные данные' : 'Update proxy server details and parameters');
  String get v2rayAddConfigDesc => isZh ? '手动输入服务器地址或出站连接凭证' : (isRu ? 'Введите параметры сервера или данные узла' : 'Enter manual server details or outbound credentials');
  String get v2rayProtocol => isZh ? '协议' : (isRu ? 'Прокотол' : 'Protocol');
  String get v2rayProfileName => isZh ? '节点 / 备注名称' : (isRu ? 'Имя профиля / Сервера' : 'Profile / Server Name');
  String get v2rayProfileNameHint => isZh ? '例如：法兰克福高速节点' : (isRu ? 'напр. Франкфурт Быстрый узел' : 'e.g. Frankfurt Fast Node');
  String get v2rayServerAddress => isZh ? '服务器地址 / 主机名' : (isRu ? 'Адрес сервера / Хост' : 'Server Address / Host');
  String get v2rayServerAddressHint => isZh ? '例如：198.51.100.1 或 node.example.com' : (isRu ? 'напр. 198.51.100.1 или node.example.com' : 'e.g. 198.51.100.1 or node.example.com');
  String get v2rayPort => isZh ? '端口' : (isRu ? 'Порт' : 'Port');
  String get v2rayPortRequired => isZh ? '端口号为必填项' : (isRu ? 'Требуется указать порт' : 'Port required');
  String get v2rayUserId => isZh ? '用户 ID (UUID)' : (isRu ? 'ID пользователя (UUID)' : 'User ID (UUID)');
  String get v2rayPasswordHint => isZh ? '输入密码或预共享密钥' : (isRu ? 'Введите пароль или общий ключ' : 'Enter password or pre-shared secret');
  String get v2rayTransportNetwork => isZh ? '传输协议网络' : (isRu ? 'Транспортная сеть' : 'Transport Network');
  String get v2raySecurityLayer => isZh ? '安全传输层' : (isRu ? 'Уровень безопасности' : 'Security Layer');
  String get v2raySecurityReality => isZh ? 'Reality (VLESS 专用)' : (isRu ? 'Reality (VLESS)' : 'Reality (VLESS)');
  String get v2raySecurityTls => isZh ? '标准 TLS' : (isRu ? 'Стандартный TLS' : 'Standard TLS');
  String get v2raySecurityNone => isZh ? '无加密 / 明文' : (isRu ? 'Без шифрования' : 'None / Plain');
  String get v2raySni => isZh ? 'SNI 伪装域名' : (isRu ? 'SNI (Имя сервера)' : 'SNI (Server Name Indication)');
  String get v2raySniHint => isZh ? '例如：speed.cloudflare.com 或 example.com' : (isRu ? 'напр. speed.cloudflare.com или example.com' : 'e.g. speed.cloudflare.com or example.com');
  String get v2rayPath => isZh ? 'WebSocket / HTTP 路径' : (isRu ? 'Путь WebSocket / HTTP' : 'WebSocket / HTTP Path');
  String get v2rayPathHint => isZh ? '例如：/ws 或 /v2ray' : (isRu ? 'напр. /ws или /v2ray' : 'e.g. /ws or /v2ray');
  String get v2rayPublicKey => isZh ? 'Reality 公钥 (pbk)' : (isRu ? 'Публичный ключ Reality (pbk)' : 'Reality Public Key (pbk)');
  String get v2rayPublicKeyHint => isZh ? 'Base64 格式公钥 (例如：q8d4-E3...)' : (isRu ? 'Публичный ключ Base64 (напр. q8d4-E3...)' : 'Base64 public key (e.g. q8d4-E3...)');
  String get v2rayShortId => isZh ? 'Short ID (sid)' : (isRu ? 'Короткий ID (sid)' : 'Short ID (sid)');
  String get v2rayShortIdHint => isZh ? '例如：0123456789abcdef' : (isRu ? 'напр. 0123456789abcdef' : 'e.g. 0123456789abcdef');
  String get v2raySaveChanges => isZh ? '保存修改' : (isRu ? 'Сохранить изменения' : 'Save Changes');
  String get v2rayImportDialogTitle => isZh ? '导入代理节点链接或订阅' : (isRu ? 'Импорт ссылок или подписки' : 'Import Proxy Links or Subscription');
  String get v2rayImportDialogSubtitle => isZh ? '粘贴 HTTP/HTTPS 订阅链接、Base64 列表或 vless://、vmess://、trojan:// 链接' : (isRu ? 'Вставьте ссылку на подписку HTTP/HTTPS, список base64 или ссылки vless://, vmess://, trojan://' : 'Paste HTTP/HTTPS subscription URL, base64 list, or vless://, vmess://, trojan:// links');
  String get v2rayImportInputHint => isZh ? '订阅 URL 或节点链接 (vmess://, vless://, ss://, trojan://)' : (isRu ? 'URL подписки или ссылки (vmess://, vless://, ss://, trojan://)' : 'Subscription URL or Configuration Links');
  String get v2rayPasteClipboard => isZh ? '粘贴剪贴板' : (isRu ? 'Вставить из буфера' : 'Paste Clipboard');
  String get v2rayImportAction => isZh ? '导入节点' : (isRu ? 'Импортировать' : 'Import Configs');
  String v2rayImportSuccess(int count) => isZh ? '成功导入 $count 个节点配置！' : (isRu ? 'Успешно импортировано $count конфигураций!' : 'Successfully imported $count config(s)!');
  String get v2rayImportEmptyError => isZh ? '请输入订阅 URL 或粘贴节点链接' : (isRu ? 'Пожалуйста, введите URL подписки или вставьте ссылки' : 'Please enter a subscription URL or paste configuration links');
  String get v2rayImportNoValid => isZh ? '未能在内容中识别到有效的节点配置。' : (isRu ? 'Не найдены рабочие конфигурации в содержимом.' : 'No valid configurations found in subscription payload.');

  String get configListProfilesTitle => isZh ? '上游代理配置文件列表' : (isRu ? 'Профили промежуточных прокси' : 'Upstream Proxy Profiles');
  String get configListProfilesSubtitle => isZh ? '已配置的 Xray 与 Sing-Box 节点' : (isRu ? 'Настроенные серверы Xray и Sing-Box' : 'Configured Xray & Sing-Box servers');
  String get configListPingAll => isZh ? '全部测速' : (isRu ? 'Тест всех' : 'Ping All');
  String get configListImportLink => isZh ? '导入链接' : (isRu ? 'Импорт ссылки' : 'Import Link');
  String get configListAddConfig => isZh ? '添加配置' : (isRu ? 'Добавить узел' : 'Add Config');
  String get configListNoConfigs => isZh ? '尚未添加代理配置' : (isRu ? 'Нет добавленных прокси-узлов' : 'No Proxy Configs Added');
  String get configListNoConfigsDesc => isZh ? '手动添加 Xray 或 Sing-Box 节点，或粘贴订阅/配置链接。' : (isRu ? 'Добавьте узел Xray/Sing-Box вручную или вставьте ссылку на подписку.' : 'Add an Xray or Sing-Box node manually or paste a subscription/config URL.');
  String get configListImportFromLink => isZh ? '从链接导入' : (isRu ? 'Импорт из ссылки' : 'Import from Link');
  String get configListManualEntry => isZh ? '手动输入' : (isRu ? 'Вручную' : 'Manual Entry');
  String get configListCopyLink => isZh ? '复制分享链接' : (isRu ? 'Копировать ссылку' : 'Copy share link');
  String get configListCopied => isZh ? '已复制到剪贴板！' : (isRu ? 'скопировано в буфер!' : 'copied to clipboard!');
  String get configListEdit => isZh ? '编辑配置' : (isRu ? 'Редактировать конфигурацию' : 'Edit configuration');
  String get configListDelete => isZh ? '删除配置' : (isRu ? 'Удалить конфигурацию' : 'Delete configuration');

  String get searchRegionsHint => isZh ? '搜索国家和地区…' : (isRu ? 'Поиск стран и регионов…' : 'Search countries and regions…');
  String get noRegionsFound => isZh ? '未找到匹配的地区' : (isRu ? 'Регионы не найдены' : 'No matching regions found');

  String get logsAll => isZh ? '全部日志' : (isRu ? 'Все логи' : 'All Logs');
  String get logsErrors => isZh ? '错误与警告' : (isRu ? 'Ошибки и предупреждения' : 'Errors & Warnings');
  String get logsPsiphon => isZh ? 'Psiphon 核心' : (isRu ? 'Ядро Psiphon' : 'Psiphon Core');
  String get logsV2ray => isZh ? 'V2Ray / Xray' : (isRu ? 'V2Ray / Xray' : 'V2Ray / Xray');
  String get logsWarp => isZh ? 'Cloudflare WARP' : (isRu ? 'Cloudflare WARP' : 'Cloudflare WARP');
  String get logsTor => isZh ? 'Tor 洋葱网络' : (isRu ? 'Сеть Tor' : 'Tor Network');
  String get logsUltrasurf => 'Ultrasurf';
  String get logsShard => 'SHARD';
  String get logsTun => isZh ? 'TUN 虚拟网卡与路由' : (isRu ? 'TUN и маршрутизация' : 'TUN & Routing');
  String get logsFilterTooltip => isZh ? '按引擎来源过滤日志' : (isRu ? 'Фильтр логов по источнику' : 'Filter logs by engine source');

  String get splitClearAll => isZh ? '清空全部' : (isRu ? 'Очистить всё' : 'Clear All');
  String get splitClearAllWebsitesConfirmTitle => isZh ? '清空所有网站与 IP 规则？' : (isRu ? 'Очистить все правила сайтов и IP?' : 'Clear All Website & IP Rules?');
  String get splitClearAllWebsitesConfirmDesc => isZh ? '确定要移除所有已添加的网站域名和 IP 分流规则吗？此操作无法撤销。' : (isRu ? 'Удалить все добавленные домены и IP из правил раздельного туннелирования? Это действие необратимо.' : 'Are you sure you want to remove all configured website domains and IP routing rules? This action cannot be undone.');
  String get splitClearAllAppsConfirmTitle => isZh ? '清空所有应用分流规则？' : (isRu ? 'Очистить все правила приложений?' : 'Clear All Application Rules?');
  String get splitClearAllAppsConfirmDesc => isZh ? '确定要移除所有已添加的分流应用程序吗？此操作无法撤销。' : (isRu ? 'Удалить все добавленные приложения из правил раздельного туннелирования? Это действие необратимо.' : 'Are you sure you want to remove all configured applications from the split tunnel list? This action cannot be undone.');
  String get v2rayClearAll => isZh ? '清空全部' : (isRu ? 'Очистить всё' : 'Clear All');
  String get v2rayClearAllConfirmTitle => isZh ? '清空所有 V2Ray 节点配置？' : (isRu ? 'Удалить все конфигурации V2Ray?' : 'Clear All V2Ray Configurations?');
  String get v2rayClearAllConfirmDesc => isZh ? '确定要删除所有 Xray 和 Sing-Box 节点配置吗？此操作无法撤销。' : (isRu ? 'Вы уверены, что хотите удалить все профили узлов Xray и Sing-Box? Это действие необратимо.' : 'Are you sure you want to remove all Xray and Sing-Box server profiles? This action cannot be undone.');
  String get selectEgressRegionTitle => isZh ? '选择出口服务器地区' : (isRu ? 'Выбор региона выхода' : 'Select Server Egress Region');
  String get selectEgressRegionSubtitle => isZh ? '选择加密流量的出口地理位置' : (isRu ? 'Выберите регион для выхода зашифрованного трафика' : 'Choose the location to exit your encrypted traffic');

  String get shardSwitchedIpToast => isZh ? '已切换至下一个 SHARD 候选节点，已分配新的出口 IP。' : (isRu ? 'Переключено на следующий узел SHARD. Назначен новый исходящий IP.' : 'Switched to next SHARD candidate node. New exit IP assigned.');
  String shardRefreshedToast(int nodes, int paths) => isZh ? 'SHARD 节点池已刷新：$nodes 个节点，$paths 条动态路径。' : (isRu ? 'Пул SHARD обновлен: $nodes узлов, $paths дин. путей.' : 'SHARD pool refreshed: $nodes nodes, $paths dynamic paths.');
  String get lanEndpointsTitle => isZh ? '局域网共享端点与连接信息' : (isRu ? 'Точки подключения и обмен в LAN' : 'LAN Sharing Endpoints & Connection Info');
  String get lanAuthLabel => isZh ? '身份认证' : (isRu ? 'Аутентификация' : 'Authentication');
  String get lanWifiClients => isZh ? '局域网 Wi-Fi / 以太网客户端' : (isRu ? 'Клиенты локальной сети Wi-Fi / Ethernet' : 'Wi-Fi / Ethernet Clients');
  String get lanHostIp => isZh ? '主机局域网 IP' : (isRu ? 'Локальный IP хоста' : 'Host LAN IP');
  String get lanSocks5Endpoint => isZh ? 'SOCKS5 代理端点' : (isRu ? 'Точка подключения SOCKS5' : 'SOCKS5 Endpoint');
  String get lanHttpEndpoint => isZh ? 'HTTP 代理端点' : (isRu ? 'Точка подключения HTTP' : 'HTTP Proxy Endpoint');
  String get lanAuthUserPass => isZh ? '局域网认证 (用户名 : 密码)' : (isRu ? 'Авторизация LAN (Логин : Пароль)' : 'LAN Auth (User : Pass)');
  String get lanNoAuth => isZh ? '无认证 (局域网内免密开放)' : (isRu ? 'Без авторизации (открыто в LAN)' : 'No Auth (Open on LAN)');
  String get lanOptionalUsername => isZh ? '可选用户名' : (isRu ? 'Необязательное имя' : 'Optional username');
  String get lanOptionalPassword => isZh ? '可选密码' : (isRu ? 'Необязательный пароль' : 'Optional password');
  String get networkEngineUpdates => isZh ? '网络引擎核心更新' : (isRu ? 'Обновление ядер сетевых движков' : 'Network Engine Core Updates');
  String get checkUpdates => isZh ? '检查更新' : (isRu ? 'Проверить обновления' : 'Check Updates');
  String get aetherCoreDesc => isZh ? 'Cloudflare WARP、MASQUE 与 WireGuard 客户端引擎' : (isRu ? 'Клиент Cloudflare WARP, MASQUE и WireGuard' : 'Cloudflare WARP, MASQUE, and WireGuard Client');
  String get updateNow => isZh ? '立即更新' : (isRu ? 'Обновить сейчас' : 'Update Now');
  String get reinstall => isZh ? '重新安装' : (isRu ? 'Переустановить' : 'Reinstall');
  String get aetherCoreUpdated => isZh ? 'Aether 核心已更新' : (isRu ? 'Ядро Aether обновлено' : 'Aether Core Updated');
  String get restartNow => isZh ? '立即重启' : (isRu ? 'Перезапустить сейчас' : 'Restart Now');
  String get close => isZh ? '关闭' : (isRu ? 'Закрыть' : 'Close');
  String get cdnCorpus => isZh ? 'CDN 语料集' : (isRu ? 'Наборы провайдеров CDN' : 'CDN Corpus');
  String get cdnClearAuto => isZh ? '清除 (全选自动)' : (isRu ? 'Сброс (Авто-все)' : 'Clear (Auto All)');
  String allCount(int n) => isZh ? '全选 $n 个' : (isRu ? 'Все $n' : 'All $n');
  String copiedToClipboard(String text) => isZh ? '已复制 "$text" 到剪贴板' : (isRu ? 'Скопировано "$text" в буфер обмена' : 'Copied "$text" to clipboard');

  String get tunMode => isZh ? 'TUN 虚拟网卡' : (isRu ? 'Режим TUN' : 'TUN Mode');
  String get proxyMode => isZh ? '系统代理模式' : (isRu ? 'Режим системного прокси' : 'Proxy Mode');
  String get proxyModeSubtitle => isZh ? 'Windows 系统代理 — 针对浏览器和应用程序' : (isRu ? 'Системный прокси Windows — браузеры и приложения' : 'Windows System Proxy — browsers & apps');
  String get adminReq => isZh ? '需管理员' : (isRu ? 'Нужен админ' : 'Admin Req.');
  String get starting => isZh ? '启动中' : (isRu ? 'Запуск' : 'Starting');
  String get failed => isZh ? '失败' : (isRu ? 'Ошибка' : 'Failed');
  String get totalTraffic => isZh ? '总量' : (isRu ? 'Всего' : 'Total');

  String get trayOpen => isZh ? '打开 Se7en Pro' : (isRu ? 'Открыть Se7en Pro' : 'Open Se7enPro');
  String get trayDisconnect => isZh ? '断开连接' : (isRu ? 'Отключить' : 'Disconnect');
  String get trayExit => isZh ? '退出' : (isRu ? 'Выход' : 'Exit');
}

final stringsProvider = Provider<AppStrings>((ref) {
  final lang = ref.watch(settingsProvider.select((s) => s.language));
  return AppStrings(lang);
});
