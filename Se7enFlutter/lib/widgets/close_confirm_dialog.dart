import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:window_manager/window_manager.dart';

import '../core/i18n/app_strings.dart';
import '../core/services/app_lifecycle.dart';
import '../core/services/providers.dart';
import '../theme/app_colors.dart';

enum CloseAction {
  tray,
  exit,
}

class CloseConfirmDialog extends ConsumerStatefulWidget {
  const CloseConfirmDialog({super.key});

  static Future<void> show(BuildContext context) async {
    final settings = ProviderScope.containerOf(context).read(settingsProvider);
    final action = settings.onCloseAction;

    if (action == 'tray') {
      await windowManager.hide();
      return;
    } else if (action == 'exit') {
      await _doExit(context);
      return;
    }

    if (!context.mounted) return;
    await showDialog<void>(
      context: context,
      barrierDismissible: true,
      builder: (ctx) => const CloseConfirmDialog(),
    );
  }

  static Future<void> _doExit(BuildContext context) async {
    final container = ProviderScope.containerOf(context);
    await shutdownAndExit(container: container);
  }

  @override
  ConsumerState<CloseConfirmDialog> createState() => _CloseConfirmDialogState();
}

class _CloseConfirmDialogState extends ConsumerState<CloseConfirmDialog> {
  CloseAction _selected = CloseAction.tray;
  bool _remember = false;

  void _onConfirm() async {
    if (_remember) {
      final token = _selected == CloseAction.tray ? 'tray' : 'exit';
      await ref.read(settingsProvider.notifier).update((s) {
        s.onCloseAction = token;
      });
    }

    if (!mounted) return;
    Navigator.of(context).pop();

    if (_selected == CloseAction.tray) {
      await windowManager.hide();
    } else {
      await CloseConfirmDialog._doExit(context);
    }
  }

  @override
  Widget build(BuildContext context) {
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
                    padding: const EdgeInsets.all(8),
                    decoration: BoxDecoration(
                      color: BrandColors.accentCyan.withValues(alpha: 0.12),
                      borderRadius: BorderRadius.circular(10),
                    ),
                    child: const Icon(
                      Icons.power_settings_new_rounded,
                      size: 20,
                      color: BrandColors.accentCyan,
                    ),
                  ),
                  const SizedBox(width: 12),
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(
                          str.exitDialogTitle,
                          style: TextStyle(
                            fontSize: 14.5,
                            fontWeight: FontWeight.w700,
                            color: c.textPrimary,
                          ),
                        ),
                        const SizedBox(height: 2),
                        Text(
                          str.exitDialogDesc,
                          style: TextStyle(
                            fontSize: 11.5,
                            color: c.textSecondary,
                          ),
                        ),
                      ],
                    ),
                  ),
                ],
              ),

              const SizedBox(height: 18),

              _ActionTile(
                icon: Icons.filter_none_rounded,
                title: str.exitOptionTray,
                description: str.minimizeToTrayDesc,
                selected: _selected == CloseAction.tray,
                onTap: () => setState(() => _selected = CloseAction.tray),
              ),

              const SizedBox(height: 10),

              _ActionTile(
                icon: Icons.exit_to_app_rounded,
                title: str.exitOptionExit,
                description: str.closeActionExit,
                selected: _selected == CloseAction.exit,
                onTap: () => setState(() => _selected = CloseAction.exit),
              ),

              const SizedBox(height: 14),

              InkWell(
                borderRadius: BorderRadius.circular(8),
                onTap: () => setState(() => _remember = !_remember),
                child: Padding(
                  padding: const EdgeInsets.symmetric(vertical: 4, horizontal: 2),
                  child: Row(
                    children: [
                      SizedBox(
                        width: 22,
                        height: 22,
                        child: Checkbox(
                          value: _remember,
                          onChanged: (v) => setState(() => _remember = v ?? false),
                          activeColor: BrandColors.accentCyan,
                          shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(5)),
                        ),
                      ),
                      const SizedBox(width: 8),
                      Text(
                        str.rememberChoice,
                        style: TextStyle(
                          fontSize: 12,
                          color: c.textPrimary,
                        ),
                      ),
                    ],
                  ),
                ),
              ),

              const SizedBox(height: 18),

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
                  const SizedBox(width: 8),
                  FilledButton(
                    onPressed: _onConfirm,
                    style: FilledButton.styleFrom(
                      backgroundColor: _selected == CloseAction.exit
                          ? BrandColors.danger
                          : BrandColors.accentCyan,
                      foregroundColor: Colors.white,
                      shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(10)),
                      padding: const EdgeInsets.symmetric(horizontal: 20, vertical: 10),
                    ),
                    child: Text(
                      str.confirm,
                      style: const TextStyle(fontWeight: FontWeight.w700),
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

class _ActionTile extends StatelessWidget {
  const _ActionTile({
    required this.icon,
    required this.title,
    required this.description,
    required this.selected,
    required this.onTap,
  });

  final IconData icon;
  final String title;
  final String description;
  final bool selected;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final c = Theme.of(context).extension<AppColors>()!;

    return InkWell(
      borderRadius: BorderRadius.circular(12),
      onTap: onTap,
      child: AnimatedContainer(
        duration: const Duration(milliseconds: 140),
        padding: const EdgeInsets.all(12),
        decoration: BoxDecoration(
          color: selected
              ? BrandColors.accentCyan.withValues(alpha: 0.12)
              : c.input.withValues(alpha: 0.5),
          borderRadius: BorderRadius.circular(12),
          border: Border.all(
            color: selected
                ? BrandColors.accentCyan
                : c.border.withValues(alpha: 0.4),
            width: selected ? 1.5 : 1,
          ),
        ),
        child: Row(
          children: [
            Icon(
              selected ? Icons.radio_button_checked_rounded : Icons.radio_button_off_rounded,
              size: 20,
              color: selected ? BrandColors.accentCyan : c.textMuted,
            ),
            const SizedBox(width: 12),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(
                    title,
                    style: TextStyle(
                      fontSize: 12.5,
                      fontWeight: FontWeight.w700,
                      color: selected ? c.textPrimary : c.textSecondary,
                    ),
                  ),
                  const SizedBox(height: 2),
                  Text(
                    description,
                    style: TextStyle(
                      fontSize: 11,
                      color: c.textMuted,
                    ),
                  ),
                ],
              ),
            ),
          ],
        ),
      ),
    );
  }
}
