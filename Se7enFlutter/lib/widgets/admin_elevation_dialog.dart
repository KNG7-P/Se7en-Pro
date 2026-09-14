import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../core/i18n/app_strings.dart';
import '../core/services/providers.dart';
import '../theme/app_colors.dart';

class AdminElevationDialog extends ConsumerWidget {
  const AdminElevationDialog({super.key, this.onConfirm});

  final VoidCallback? onConfirm;

  static Future<void> show(BuildContext context) async {
    if (!context.mounted) return;
    await showDialog<void>(
      context: context,
      barrierDismissible: true,
      builder: (ctx) => const AdminElevationDialog(),
    );
  }

  void _onRestartAsAdmin(BuildContext context, WidgetRef ref) async {

    try {
      onConfirm?.call();
    } catch (_) {}

    try {
      ref.read(settingsProvider.notifier).update((s) => s.systemWideTunneling = true);
    } catch (_) {}

    final exePath = Platform.resolvedExecutable;
    final isFlutterApp = exePath.toLowerCase().endsWith('se7enpro.exe');

    if (isFlutterApp && File(exePath).existsSync()) {
      try {
        final safePath = exePath.replaceAll("'", "''");

        final cmd = 'try { Start-Process -FilePath \'$safePath\' -Verb RunAs -ErrorAction Stop; exit 0 } catch { exit 1 }';
        final result = await Process.run('powershell', [
          '-NoProfile',
          '-NonInteractive',
          '-WindowStyle',
          'Hidden',
          '-Command',
          cmd,
        ]);

        if (result.exitCode == 0) {

          try {
            await ref.read(coreClientProvider).shutdown();
          } catch (_) {}
          exit(0);
        } else {

          if (context.mounted) {
            Navigator.of(context).pop();
          }
          try {
            ref.read(settingsProvider.notifier).update((s) => s.systemWideTunneling = false);
          } catch (_) {}
          return;
        }
      } catch (e) {
        debugPrint('Elevation via Start-Process failed: $e');
      }
    }

    if (context.mounted) {
      Navigator.of(context).pop();
    }
    try {
      await ref.read(coreClientProvider).restartAsAdmin();
    } catch (e) {
      debugPrint('restartAsAdmin IPC failed: $e');
    }
  }

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final c = Theme.of(context).extension<AppColors>()!;
    final str = ref.watch(stringsProvider);

    return Dialog(
      backgroundColor: c.cardElevated,
      shape: RoundedRectangleBorder(
        borderRadius: BorderRadius.circular(18),
        side: BorderSide(color: c.border.withValues(alpha: 0.6)),
      ),
      insetPadding: const EdgeInsets.symmetric(horizontal: 24, vertical: 24),
      child: ConstrainedBox(
        constraints: const BoxConstraints(maxWidth: 440),
        child: Padding(
          padding: const EdgeInsets.all(22),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [

              Row(
                children: [
                  Container(
                    padding: const EdgeInsets.all(10),
                    decoration: BoxDecoration(
                      color: const Color(0xFFF59E0B).withValues(alpha: 0.15),
                      borderRadius: BorderRadius.circular(12),
                    ),
                    child: const Icon(
                      Icons.admin_panel_settings_rounded,
                      size: 24,
                      color: Color(0xFFF59E0B),
                    ),
                  ),
                  const SizedBox(width: 14),
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(
                          str.adminElevationTitle,
                          style: TextStyle(
                            fontSize: 14.5,
                            fontWeight: FontWeight.w700,
                            color: c.textPrimary,
                          ),
                        ),
                      ],
                    ),
                  ),
                ],
              ),

              const SizedBox(height: 16),

              Container(
                padding: const EdgeInsets.all(14),
                decoration: BoxDecoration(
                  color: c.input.withValues(alpha: 0.5),
                  borderRadius: BorderRadius.circular(12),
                  border: Border.all(color: c.border.withValues(alpha: 0.4)),
                ),
                child: Text(
                  str.adminElevationDesc,
                  style: TextStyle(
                    fontSize: 12,
                    height: 1.5,
                    color: c.textPrimary,
                  ),
                ),
              ),

              const SizedBox(height: 20),

              Row(
                mainAxisAlignment: MainAxisAlignment.end,
                children: [
                  TextButton(
                    onPressed: () => Navigator.of(context).pop(),
                    style: TextButton.styleFrom(
                      foregroundColor: BrandColors.danger,
                      padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 10),
                    ),
                    child: Text(str.cancel, style: const TextStyle(fontWeight: FontWeight.w600)),
                  ),
                  const SizedBox(width: 10),
                  FilledButton.icon(
                    onPressed: () => _onRestartAsAdmin(context, ref),
                    icon: const Icon(Icons.shield_rounded, size: 16),
                    label: Text(str.restartAsAdmin),
                    style: FilledButton.styleFrom(
                      backgroundColor: const Color(0xFFF59E0B),
                      foregroundColor: Colors.black87,
                      shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(10)),
                      padding: const EdgeInsets.symmetric(horizontal: 18, vertical: 10),
                      textStyle: const TextStyle(fontWeight: FontWeight.w700),
                    ),
                  ),
                ],
              ),
            ],
          ),
        ),
      ),
    );
  }
}
