import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'providers.dart';

final themeModeProvider =
    StateNotifierProvider<ThemeModeController, ThemeMode>((ref) {
  final token = ref.watch(settingsProvider.select((s) => s.theme));
  return ThemeModeController(
    ref,
    token == 'light' ? ThemeMode.light : ThemeMode.dark,
  );
});

class ThemeModeController extends StateNotifier<ThemeMode> {
  ThemeModeController(this._ref, super.initial);
  final Ref _ref;

  Future<void> toggle() async {
    final next = state == ThemeMode.dark ? ThemeMode.light : ThemeMode.dark;
    state = next;
    await _ref
        .read(settingsProvider.notifier)
        .update((s) => s.theme = next == ThemeMode.light ? 'light' : 'dark');
  }

  Future<void> set(ThemeMode mode) async {
    state = mode;
    await _ref
        .read(settingsProvider.notifier)
        .update((s) => s.theme = mode == ThemeMode.light ? 'light' : 'dark');
  }
}
