import 'dart:io';
import 'package:flutter/material.dart' hide ConnectionState;
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:tray_manager/tray_manager.dart';
import 'package:window_manager/window_manager.dart';

import '../i18n/app_strings.dart';
import '../models/connection_state.dart';
import 'providers.dart';

class TrayService with TrayListener {
  TrayService(this._ref) {
    instance = this;
  }

  static TrayService? instance;
  final Ref _ref;
  bool _initialized = false;

  Future<void> showAppWindow() async {
    try {
      await windowManager.show();
      await windowManager.focus();
    } catch (_) {}
  }

  Future<void> init() async {
    if (_initialized || !Platform.isWindows) return;
    _initialized = true;

    try {
      trayManager.addListener(this);
      await trayManager.setIcon('assets/tray-disconnected.ico');
      await trayManager.setToolTip('Se7enPro - Disconnected');

      final str = _ref.read(stringsProvider);
      final menu = Menu(
        items: [
          MenuItem(key: 'show_window', label: str.trayOpen),
          MenuItem.separator(),
          MenuItem(key: 'disconnect', label: str.trayDisconnect),
          MenuItem.separator(),
          MenuItem(key: 'exit_app', label: str.trayExit),
        ],
      );
      await trayManager.setContextMenu(menu);
    } catch (e) {
      debugPrint('Tray initialization error: $e');
    }
  }

  Future<void> updateStatus(ConnectionState state) async {
    if (!_initialized || !Platform.isWindows) return;

    try {
      String iconPath;
      String tooltip;
      switch (state) {
        case ConnectionState.connected:
          iconPath = 'assets/tray-connected.ico';
          tooltip = 'Se7enPro - Connected';
          break;
        case ConnectionState.connecting:
        case ConnectionState.disconnecting:
          iconPath = 'assets/tray-connecting.ico';
          tooltip = 'Se7enPro - Connecting...';
          break;
        case ConnectionState.disconnected:
        case ConnectionState.error:
          iconPath = 'assets/tray-disconnected.ico';
          tooltip = 'Se7enPro - Disconnected';
          break;
      }
      await trayManager.setIcon(iconPath);
      await trayManager.setToolTip(tooltip);
    } catch (e) {
      debugPrint('Tray update error: $e');
    }
  }

  @override
  void onTrayIconMouseDown() {
    showAppWindow();
  }

  @override
  void onTrayIconMouseUp() {
    showAppWindow();
  }

  @override
  void onTrayIconRightMouseDown() {
    trayManager.popUpContextMenu();
  }

  @override
  void onTrayMenuItemClick(MenuItem menuItem) async {
    switch (menuItem.key) {
      case 'show_window':
        showAppWindow();
        break;
      case 'disconnect':
        _ref.read(connectionControllerProvider).disconnect();
        break;
      case 'exit_app':

        try {
          await windowManager.hide();
        } catch (_) {}
        try {
          await _ref.read(connectionControllerProvider).disconnect();
        } catch (_) {}
        try {
          await _ref.read(coreClientProvider).shutdown();
        } catch (_) {}
        await disposeAsync();
        exit(0);
    }
  }

  Future<void> disposeAsync() async {
    try {
      trayManager.removeListener(this);
      await trayManager.destroy();
      _initialized = false;
    } catch (_) {}
  }

  void dispose() {
    trayManager.removeListener(this);
    trayManager.destroy();
    _initialized = false;
  }
}

final trayServiceProvider = Provider<TrayService>((ref) {
  final service = TrayService(ref);
  ref.onDispose(service.dispose);
  return service;
});
