import 'package:flutter/material.dart' hide ConnectionState;
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:window_manager/window_manager.dart';

import 'core/models/connection_state.dart';
import 'core/services/app_lifecycle.dart';
import 'core/services/providers.dart';
import 'core/services/theme_controller.dart';
import 'core/services/tray_service.dart';
import 'core/services/window_chrome.dart';
import 'pages/app_shell.dart';
import 'theme/app_theme.dart';

Future<void> main() async {
  WidgetsFlutterBinding.ensureInitialized();
  await windowManager.ensureInitialized();

  const initialSize = Size(1000, 680);
  const windowOptions = WindowOptions(
    size: initialSize,
    minimumSize: initialSize,
    center: true,

    backgroundColor: Colors.transparent,
    skipTaskbar: false,
    titleBarStyle: TitleBarStyle.hidden,
    windowButtonVisibility: false,
    title: 'Se7enPro',
  );

  final container = ProviderContainer();
  registerAppContainer(container);
  await container.read(settingsProvider.notifier).load();

  await windowManager.waitUntilReadyToShow(windowOptions, () async {
    await windowManager.setPreventClose(true);
    await windowManager.setAsFrameless();
    await windowManager.show();
    await windowManager.focus();
  });

  runApp(
    UncontrolledProviderScope(
      container: container,
      child: const Se7enApp(),
    ),
  );
}

class Se7enApp extends ConsumerStatefulWidget {
  const Se7enApp({super.key});

  @override
  ConsumerState<Se7enApp> createState() => _Se7enAppState();
}

class _Se7enAppState extends ConsumerState<Se7enApp> {
  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addPostFrameCallback((_) async {
      final tray = ref.read(trayServiceProvider);
      await tray.init();
      ref.read(connectionControllerProvider).initialize();
    });
  }

  @override
  Widget build(BuildContext context) {
    ref.listen<ConnectionState>(connectionStateProvider, (prev, next) {
      ref.read(trayServiceProvider).updateStatus(next);
    });
    final mode = ref.watch(themeModeProvider);
    return MaterialApp(
      title: 'Se7enPro',
      debugShowCheckedModeBanner: false,
      theme: AppTheme.light(),
      darkTheme: AppTheme.dark(),
      themeMode: mode,

      builder: (context, child) => WindowChrome(child: child!),
      home: const AppShell(),
    );
  }
}
