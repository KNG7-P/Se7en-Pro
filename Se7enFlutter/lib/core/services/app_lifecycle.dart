import 'dart:io';

import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:window_manager/window_manager.dart';

import 'providers.dart';
import 'tray_service.dart';

ProviderContainer? _appContainer;

void registerAppContainer(ProviderContainer container) => _appContainer = container;

Future<Never> shutdownAndExit({ProviderContainer? container}) async {
  final c = container ?? _appContainer;

  try {
    await windowManager.hide();
  } catch (_) {}

  if (c != null) {
    try {
      await c.read(connectionControllerProvider).disconnect();
    } catch (_) {}
    try {
      await c.read(coreClientProvider).shutdown();
    } catch (_) {}
  }

  try {
    await TrayService.instance?.disposeAsync();
  } catch (_) {}

  try {
    c?.dispose();
  } catch (_) {}

  exit(0);
}
