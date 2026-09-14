import 'package:country_flags/country_flags.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../core/data/region_catalog.dart';
import '../core/models/connection_method.dart';
import '../core/models/tunnel_status.dart';
import '../core/services/providers.dart';
import '../theme/app_colors.dart';
import 'glass_controls.dart';
import 'modern_region_picker.dart';

class RegionPickerCard extends ConsumerWidget {
  const RegionPickerCard({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final c = Theme.of(context).extension<AppColors>()!;
    final status = ref.watch(tunnelStatusProvider).valueOrNull ?? const TunnelStatus();
    final method = ref.watch(selectedMethodProvider);
    final selectedCode = ref.watch(settingsProvider.select((s) =>
        method.isTor ? s.torExitCountry : s.egressRegion)).toUpperCase();
    final controller = ref.read(connectionControllerProvider);

    final available = method.isTor
        ? RegionCatalog.torSeedRegions
        : (status.availableEgressRegions.isNotEmpty
            ? status.availableEgressRegions
            : RegionCatalog.psiphonSeedRegions);

    return GlassCard(
      padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 12),
      borderRadius: 14,
      child: Material(
        color: Colors.transparent,
        child: InkWell(
          borderRadius: BorderRadius.circular(10),
          onTap: () async {
            final picked = await showModernRegionPicker(
              context: context,
              options: RegionCatalog.optionsFrom(available),
              selectedCode: selectedCode,
            );
            if (picked != null) {
              await controller.setEgressRegion(picked.code, method: method);
            }
          },
          child: Row(
            children: [
              Container(
                width: 36,
                height: 36,
                alignment: Alignment.center,
                decoration: BoxDecoration(
                  color: BrandColors.accentCyan.withValues(alpha: 0.12),
                  borderRadius: BorderRadius.circular(10),
                  border: Border.all(
                    color: selectedCode.isNotEmpty
                        ? BrandColors.accentCyan.withValues(alpha: 0.3)
                        : Colors.transparent,
                  ),
                ),
                child: selectedCode.isNotEmpty && selectedCode.length == 2
                    ? ClipRRect(
                        borderRadius: BorderRadius.circular(4),
                        child: CountryFlag.fromCountryCode(
                          selectedCode,
                          height: 20,
                          width: 28,
                        ),
                      )
                    : const Icon(Icons.public_rounded, size: 20, color: BrandColors.accentCyan),
              ),
              const SizedBox(width: 10),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      'Target Egress Region',
                      style: TextStyle(fontSize: 10.5, color: c.textMuted),
                    ),
                    const SizedBox(height: 1),
                    Text(
                      selectedCode.isEmpty
                          ? 'Auto-Select (Best)'
                          : RegionCatalog.nameFor(selectedCode),
                      style: TextStyle(
                        fontSize: 12.5,
                        fontWeight: FontWeight.w700,
                        color: c.textPrimary,
                      ),
                      overflow: TextOverflow.ellipsis,
                    ),
                  ],
                ),
              ),
              Container(
                padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 4),
                decoration: BoxDecoration(
                  color: c.input.withValues(alpha: 0.6),
                  borderRadius: BorderRadius.circular(6),
                  border: Border.all(color: c.border.withValues(alpha: 0.4)),
                ),
                child: Row(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    if (selectedCode.isNotEmpty && selectedCode.length == 2) ...[
                      ClipRRect(
                        borderRadius: BorderRadius.circular(2),
                        child: CountryFlag.fromCountryCode(selectedCode, height: 10, width: 14),
                      ),
                      const SizedBox(width: 4),
                    ],
                    Text(
                      selectedCode.isEmpty ? 'AUTO' : selectedCode,
                      style: TextStyle(fontSize: 10.5, fontWeight: FontWeight.w700, color: c.textSecondary),
                    ),
                    const SizedBox(width: 4),
                    Icon(Icons.unfold_more_rounded, size: 14, color: c.textSecondary),
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
