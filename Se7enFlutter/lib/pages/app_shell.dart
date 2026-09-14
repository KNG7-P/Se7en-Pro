import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:window_manager/window_manager.dart';

import '../core/i18n/app_strings.dart';
import '../core/services/providers.dart';
import '../core/services/theme_controller.dart';
import '../theme/app_colors.dart';
import '../widgets/app_sidebar.dart';
import '../widgets/app_title_bar.dart';
import '../widgets/close_confirm_dialog.dart';
import 'about_page.dart';
import 'home_page.dart';
import 'logs_page.dart';
import 'settings_page.dart';
import 'split_tunnel_page.dart';

class AppShell extends ConsumerStatefulWidget {
  const AppShell({super.key});

  @override
  ConsumerState<AppShell> createState() => _AppShellState();
}

class _AppShellState extends ConsumerState<AppShell> with WindowListener {
  bool _isMaximized = false;
  bool _isMinimized = false;

  @override
  void initState() {
    super.initState();
    windowManager.addListener(this);
    _checkMaximized();
  }

  @override
  void dispose() {
    windowManager.removeListener(this);
    super.dispose();
  }

  @override
  void onWindowMaximize() {
    setState(() {
      _isMaximized = true;
      _isMinimized = false;
    });
  }

  @override
  void onWindowUnmaximize() {
    setState(() {
      _isMaximized = false;
      _isMinimized = false;
    });
  }

  @override
  void onWindowMinimize() {
    setState(() => _isMinimized = true);
  }

  @override
  void onWindowRestore() {
    setState(() {
      _isMaximized = false;
      _isMinimized = false;
    });
  }

  @override
  void onWindowClose() async {
    final isPreventClose = await windowManager.isPreventClose();
    if (isPreventClose && mounted) {
      CloseConfirmDialog.show(context);
    }
  }

  void _checkMaximized() async {
    try {
      final max = await windowManager.isMaximized();
      if (mounted && _isMaximized != max) {
        setState(() => _isMaximized = max);
      }
    } catch (_) {}
  }

  static const _pages = <Widget>[
    HomePage(),
    SplitTunnelPage(),
    LogsPage(),
    SettingsPage(),
    AboutPage(),
  ];

  @override
  Widget build(BuildContext context) {
    final c = Theme.of(context).extension<AppColors>()!;
    final str = ref.watch(stringsProvider);

    final destinations = <NavDestination>[
      NavDestination(Icons.shield_outlined, Icons.shield_rounded, str.navDashboard),
      NavDestination(Icons.call_split_rounded, Icons.call_split_rounded, str.navSplitTunnel),
      NavDestination(Icons.terminal_outlined, Icons.terminal_rounded, str.navLogs),
      NavDestination(Icons.tune_outlined, Icons.tune_rounded, str.navSettings),
      NavDestination(Icons.info_outline_rounded, Icons.info_rounded, str.navAbout),
    ];

    final titles = [
      str.titleDashboard,
      str.titleSplitTunnel,
      str.titleLogs,
      str.titleSettings,
      str.titleAbout,
    ];

    final index = ref.watch(currentNavIndexProvider);

    return Directionality(
      textDirection: TextDirection.ltr,
      child: Container(
        color: c.appBg,
        child: Scaffold(
          backgroundColor: c.appBg,
          body: Column(
            children: [
              const AppTitleBar(),
              Expanded(
                child: Row(
                  children: [
                    AppSidebar(
                      destinations: destinations,
                      selectedIndex: index,
                      onSelect: (i) => ref.read(currentNavIndexProvider.notifier).state = i,
                    ),
                    Expanded(
                      child: Container(
                        color: c.appBg,
                        child: Column(
                          crossAxisAlignment: CrossAxisAlignment.stretch,
                          children: [
                            _ModernHeader(title: titles[index]),
                            Expanded(
                              child: IndexedStack(
                                index: index,
                                children: [
                                  for (int i = 0; i < _pages.length; i++)
                                    TickerMode(
                                      enabled: !_isMinimized && index == i,
                                      child: _pages[i],
                                    ),
                                ],
                              ),
                            ),
                          ],
                        ),
                      ),
                    ),
                  ],
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

class _ModernHeader extends StatelessWidget {
  const _ModernHeader({required this.title});
  final String title;

  @override
  Widget build(BuildContext context) {
    final c = Theme.of(context).extension<AppColors>()!;

    return Container(
      height: 48,
      padding: const EdgeInsets.symmetric(horizontal: 24),
      alignment: Alignment.center,
      child: Row(
        children: [
          Text(
            title,
            style: TextStyle(
              fontSize: 15,
              fontWeight: FontWeight.w800,
              color: c.textPrimary,
            ),
          ),
          const Spacer(),

          const _QuickThemeToggleBtn(),
        ],
      ),
    );
  }
}

class _QuickThemeToggleBtn extends ConsumerStatefulWidget {
  const _QuickThemeToggleBtn();

  @override
  ConsumerState<_QuickThemeToggleBtn> createState() => _QuickThemeToggleBtnState();
}

class _QuickThemeToggleBtnState extends ConsumerState<_QuickThemeToggleBtn> {
  bool _hovered = false;

  @override
  Widget build(BuildContext context) {
    final c = Theme.of(context).extension<AppColors>()!;
    final isDark = Theme.of(context).brightness == Brightness.dark;

    return Tooltip(
      message: isDark ? 'Switch to Light Theme' : 'Switch to Dark Theme',
      child: MouseRegion(
        cursor: SystemMouseCursors.click,
        onEnter: (_) => setState(() => _hovered = true),
        onExit: (_) => setState(() => _hovered = false),
        child: GestureDetector(
          onTap: () => ref.read(themeModeProvider.notifier).toggle(),
          child: AnimatedContainer(
            duration: const Duration(milliseconds: 140),
            width: 34,
            height: 34,
            decoration: BoxDecoration(
              color: _hovered
                  ? c.cardElevated.withValues(alpha: 0.9)
                  : c.card.withValues(alpha: 0.6),
              borderRadius: BorderRadius.circular(9),
              border: Border.all(
                color: _hovered
                    ? (isDark ? BrandColors.warning.withValues(alpha: 0.6) : BrandColors.primary.withValues(alpha: 0.6))
                    : c.border.withValues(alpha: 0.6),
              ),
              boxShadow: _hovered
                  ? [
                      BoxShadow(
                        color: (isDark ? BrandColors.warning : BrandColors.primary).withValues(alpha: 0.2),
                        blurRadius: 8,
                        offset: const Offset(0, 1),
                      ),
                    ]
                  : null,
            ),
            child: Icon(
              isDark ? Icons.light_mode_rounded : Icons.dark_mode_rounded,
              size: 16,
              color: isDark ? BrandColors.warning : BrandColors.primary,
            ),
          ),
        ),
      ),
    );
  }
}
