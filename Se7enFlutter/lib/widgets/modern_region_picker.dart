import 'package:country_flags/country_flags.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../core/i18n/app_strings.dart';
import '../core/models/region_option.dart';
import '../theme/app_colors.dart';

Future<RegionOption?> showModernRegionPicker({
  required BuildContext context,
  required List<RegionOption> options,
  required String selectedCode,
}) {
  return showDialog<RegionOption>(
    context: context,
    barrierColor: Colors.black54,
    builder: (ctx) => Dialog(
      backgroundColor: Colors.transparent,
      insetPadding: const EdgeInsets.symmetric(horizontal: 24, vertical: 32),
      child: _ModernRegionSheet(
        options: options,
        selectedCode: selectedCode,
      ),
    ),
  );
}

class _ModernRegionSheet extends ConsumerStatefulWidget {
  const _ModernRegionSheet({
    required this.options,
    required this.selectedCode,
  });

  final List<RegionOption> options;
  final String selectedCode;

  @override
  ConsumerState<_ModernRegionSheet> createState() => _ModernRegionSheetState();
}

class _ModernRegionSheetState extends ConsumerState<_ModernRegionSheet> {
  String _query = '';

  @override
  Widget build(BuildContext context) {
    final c = Theme.of(context).extension<AppColors>()!;
    final str = ref.watch(stringsProvider);
    final q = _query.trim().toLowerCase();
    final filtered = q.isEmpty
        ? widget.options
        : widget.options
            .where((o) =>
                o.name.toLowerCase().contains(q) ||
                o.code.toLowerCase().contains(q))
            .toList();

    return Center(
      child: Container(
        width: 520,
        height: 540,
        margin: const EdgeInsets.symmetric(horizontal: 24, vertical: 32),
        decoration: BoxDecoration(
          color: c.cardElevated,
          borderRadius: BorderRadius.circular(20),
          border: Border.all(color: c.glassBorder, width: 1.2),
          boxShadow: [
            BoxShadow(
              color: Colors.black.withValues(alpha: c.isDark ? 0.6 : 0.25),
              blurRadius: 40,
              offset: const Offset(0, 12),
            ),
          ],
        ),
        child: Column(
          children: [

            Padding(
              padding: const EdgeInsets.fromLTRB(20, 18, 16, 12),
              child: Row(
                children: [
                  Container(
                    padding: const EdgeInsets.all(8),
                    decoration: BoxDecoration(
                      color: BrandColors.accentCyan.withValues(alpha: 0.15),
                      borderRadius: BorderRadius.circular(10),
                    ),
                    child: const Icon(Icons.public_rounded, size: 18, color: BrandColors.accentCyan),
                  ),
                  const SizedBox(width: 12),
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(
                          str.selectEgressRegionTitle,
                          style: TextStyle(
                            fontSize: 14,
                            fontWeight: FontWeight.w700,
                            color: c.textPrimary,
                          ),
                        ),
                        Text(
                          str.selectEgressRegionSubtitle,
                          style: TextStyle(fontSize: 11, color: c.textMuted),
                        ),
                      ],
                    ),
                  ),
                  IconButton(
                    icon: Icon(Icons.close_rounded, size: 20, color: BrandColors.danger.withValues(alpha: 0.85)),
                    onPressed: () => Navigator.of(context).pop(),
                  ),
                ],
              ),
            ),

            Divider(height: 1, color: c.border.withValues(alpha: 0.5)),

            Padding(
              padding: const EdgeInsets.fromLTRB(18, 14, 18, 10),
              child: TextField(
                autofocus: true,
                onChanged: (v) => setState(() => _query = v),
                style: TextStyle(fontSize: 13, color: c.textPrimary),
                decoration: InputDecoration(
                  hintText: str.searchRegionsHint,
                  hintStyle: TextStyle(fontSize: 12.5, color: c.textMuted),
                  prefixIcon: Icon(Icons.search_rounded, size: 18, color: c.textSecondary),
                  isDense: true,
                  filled: true,
                  fillColor: c.input.withValues(alpha: 0.7),
                  contentPadding: const EdgeInsets.symmetric(horizontal: 14, vertical: 10),
                  border: OutlineInputBorder(
                    borderRadius: BorderRadius.circular(12),
                    borderSide: BorderSide(color: c.border.withValues(alpha: 0.6)),
                  ),
                  enabledBorder: OutlineInputBorder(
                    borderRadius: BorderRadius.circular(12),
                    borderSide: BorderSide(color: c.border.withValues(alpha: 0.6)),
                  ),
                  focusedBorder: OutlineInputBorder(
                    borderRadius: BorderRadius.circular(12),
                    borderSide: const BorderSide(color: BrandColors.accentCyan, width: 1.4),
                  ),
                ),
              ),
            ),

            Expanded(
              child: filtered.isEmpty
                  ? Center(
                      child: Text(str.noRegionsFound,
                          style: TextStyle(fontSize: 12.5, color: c.textMuted)),
                    )
                  : ListView.builder(
                      padding: const EdgeInsets.fromLTRB(14, 4, 14, 14),
                      itemCount: filtered.length,
                      itemBuilder: (context, i) {
                        final option = filtered[i];
                        final isSelected = option.code.toUpperCase() ==
                            widget.selectedCode.toUpperCase();

                        return _ModernRegionTile(
                          option: option,
                          isSelected: isSelected,
                          onTap: () => Navigator.of(context).pop(option),
                        );
                      },
                    ),
            ),
          ],
        ),
      ),
    );
  }
}

class _ModernRegionTile extends StatelessWidget {
  const _ModernRegionTile({
    required this.option,
    required this.isSelected,
    required this.onTap,
  });

  final RegionOption option;
  final bool isSelected;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final c = Theme.of(context).extension<AppColors>()!;

    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 3),
      child: Material(
        color: Colors.transparent,
        child: InkWell(
          borderRadius: BorderRadius.circular(12),
          onTap: onTap,
          child: AnimatedContainer(
            duration: const Duration(milliseconds: 140),
            padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 10),
            decoration: BoxDecoration(
              color: isSelected
                  ? (c.isDark
                      ? BrandColors.accentCyan.withValues(alpha: 0.16)
                      : BrandColors.primary.withValues(alpha: 0.10))
                  : c.card.withValues(alpha: 0.4),
              borderRadius: BorderRadius.circular(12),
              border: Border.all(
                color: isSelected
                    ? BrandColors.accentCyan.withValues(alpha: 0.5)
                    : c.border.withValues(alpha: 0.4),
              ),
            ),
            child: Row(
              children: [

                Container(
                  width: 34,
                  height: 24,
                  alignment: Alignment.center,
                  decoration: BoxDecoration(
                    color: c.input,
                    borderRadius: BorderRadius.circular(6),
                    border: Border.all(color: c.border.withValues(alpha: 0.5)),
                  ),
                  child: option.code.isEmpty
                      ? const Icon(Icons.auto_awesome_rounded, size: 15, color: BrandColors.accentCyan)
                      : ClipRRect(
                          borderRadius: BorderRadius.circular(4),
                          child: CountryFlag.fromCountryCode(
                            option.code,
                            height: 18,
                            width: 26,
                          ),
                        ),
                ),
                const SizedBox(width: 14),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        option.name,
                        style: TextStyle(
                          fontSize: 13,
                          fontWeight: isSelected ? FontWeight.w700 : FontWeight.w500,
                          color: isSelected ? BrandColors.accentCyan : c.textPrimary,
                        ),
                      ),
                      const SizedBox(height: 2),
                      Row(
                        children: [
                          if (option.code.isNotEmpty) ...[
                            Container(
                              padding: const EdgeInsets.symmetric(horizontal: 5, vertical: 1.5),
                              decoration: BoxDecoration(
                                color: c.input.withValues(alpha: 0.8),
                                borderRadius: BorderRadius.circular(4),
                                border: Border.all(color: c.border.withValues(alpha: 0.5)),
                              ),
                              child: Text(
                                option.code.toUpperCase(),
                                style: TextStyle(
                                  fontSize: 9.5,
                                  fontWeight: FontWeight.w700,
                                  color: c.textSecondary,
                                ),
                              ),
                            ),
                            const SizedBox(width: 6),
                          ],
                          Text(
                            option.code.isEmpty ? 'Optimal low-latency server' : 'Encrypted egress location',
                            style: TextStyle(fontSize: 10.5, color: c.textMuted),
                          ),
                        ],
                      ),
                    ],
                  ),
                ),
                if (isSelected)
                  Container(
                    padding: const EdgeInsets.all(4),
                    decoration: const BoxDecoration(
                      shape: BoxShape.circle,
                      color: BrandColors.accentCyan,
                    ),
                    child: const Icon(Icons.check_rounded, size: 12, color: Colors.black),
                  ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}
