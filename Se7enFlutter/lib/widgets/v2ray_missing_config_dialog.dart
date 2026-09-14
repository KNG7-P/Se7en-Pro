import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../core/i18n/app_strings.dart';
import '../core/models/connection_method.dart';
import '../core/services/providers.dart';
import '../theme/app_colors.dart';

class V2RayMissingConfigDialog extends ConsumerWidget {
  const V2RayMissingConfigDialog({
    super.key,
    required this.hasConfigs,
    required this.method,
  });

  final bool hasConfigs;
  final ConnectionMethod method;

  static Future<void> show(
    BuildContext context, {
    required bool hasConfigs,
    required ConnectionMethod method,
  }) async {
    if (!context.mounted) return;
    await showDialog<void>(
      context: context,
      barrierDismissible: true,
      builder: (ctx) => V2RayMissingConfigDialog(
        hasConfigs: hasConfigs,
        method: method,
      ),
    );
  }

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final c = Theme.of(context).extension<AppColors>()!;
    final str = ref.watch(stringsProvider);

    return Directionality(
      textDirection: str.textDirection,
      child: Dialog(
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
                        Icons.warning_amber_rounded,
                        size: 26,
                        color: Color(0xFFF59E0B),
                      ),
                    ),
                    const SizedBox(width: 14),
                    Expanded(
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          Text(
                            str.v2rayMissingConfigTitle,
                            style: TextStyle(
                              fontSize: 14.5,
                              fontWeight: FontWeight.w700,
                              color: c.textPrimary,
                            ),
                          ),
                          const SizedBox(height: 3),
                          Container(
                            padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 1.5),
                            decoration: BoxDecoration(
                              color: const Color(0xFFF59E0B).withValues(alpha: 0.12),
                              borderRadius: BorderRadius.circular(4),
                            ),
                            child: Text(
                              str.v2rayMissingConfigBadge,
                              style: const TextStyle(
                                fontSize: 10.5,
                                fontWeight: FontWeight.w700,
                                color: Color(0xFFF59E0B),
                              ),
                            ),
                          ),
                        ],
                      ),
                    ),
                  ],
                ),

                const SizedBox(height: 16),

                Text(
                  hasConfigs
                      ? str.v2rayMissingConfigDescNoneActive
                      : str.v2rayMissingConfigDescNoConfigs,
                  style: TextStyle(
                    fontSize: 12.5,
                    height: 1.5,
                    color: c.textSecondary,
                  ),
                ),

                const SizedBox(height: 22),

                Row(
                  mainAxisAlignment: MainAxisAlignment.end,
                  children: [
                    TextButton(
                      style: TextButton.styleFrom(
                        foregroundColor: c.textMuted,
                        padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 10),
                      ),
                      onPressed: () => Navigator.of(context).pop(),
                      child: Text(
                        str.cancel,
                        style: const TextStyle(fontWeight: FontWeight.w600),
                      ),
                    ),
                    const SizedBox(width: 8),
                    ElevatedButton.icon(
                      style: ElevatedButton.styleFrom(
                        backgroundColor: BrandColors.primary,
                        foregroundColor: Colors.white,
                        padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 10),
                        shape: RoundedRectangleBorder(
                          borderRadius: BorderRadius.circular(8),
                        ),
                      ),
                      icon: const Icon(Icons.settings_rounded, size: 16),
                      label: Text(
                        str.v2rayMissingConfigAction,
                        style: const TextStyle(fontWeight: FontWeight.w700),
                      ),
                      onPressed: () {
                        Navigator.of(context).pop();

                        if (method == ConnectionMethod.psiphonOverV2Ray) {
                          ref.read(settingsProvider.notifier).update((s) => s.chainedSubMode = 'psiphon_v2ray');
                        } else if (method == ConnectionMethod.torOverV2Ray) {
                          ref.read(settingsProvider.notifier).update((s) => s.chainedSubMode = 'tor_v2ray');
                        }

                        ref.read(currentSettingsTabProvider.notifier).state = SettingsTab.chained;
                        ref.read(currentNavIndexProvider.notifier).state = 3;
                      },
                    ),
                  ],
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}
