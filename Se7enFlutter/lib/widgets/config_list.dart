import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../core/i18n/app_strings.dart';

import '../theme/app_colors.dart';
import '../widgets/glass_controls.dart';
import 'psiphon_over_v2ray_section.dart';

class ConfigList extends ConsumerWidget {
  const ConfigList({
    super.key,
    required this.configs,
    required this.selectedCore,
    required this.onAddConfig,
    required this.onImportConfig,
    required this.onPingAll,
    required this.onEditConfig,
    required this.onDeleteConfig,
    required this.onTestLatency,
    required this.onToggleActive,
    this.onClearAll,
    this.isTestingAll = false,
  });

  final List<V2RayConfig> configs;
  final CoreType selectedCore;
  final VoidCallback onAddConfig;
  final VoidCallback onImportConfig;
  final VoidCallback onPingAll;
  final VoidCallback? onClearAll;
  final Function(V2RayConfig) onEditConfig;
  final Function(V2RayConfig) onDeleteConfig;
  final Function(V2RayConfig) onTestLatency;
  final Function(V2RayConfig) onToggleActive;
  final bool isTestingAll;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final c = Theme.of(context).extension<AppColors>()!;
    final str = ref.watch(stringsProvider);

    return GlassCard(
      borderRadius: 14,
      padding: const EdgeInsets.all(16),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [

          Row(
            children: [
              Container(
                padding: const EdgeInsets.all(7),
                decoration: BoxDecoration(
                  color: BrandColors.accentCyan.withValues(alpha: 0.15),
                  borderRadius: BorderRadius.circular(8),
                ),
                child: const Icon(
                  Icons.dns_rounded,
                  size: 17,
                  color: BrandColors.accentCyan,
                ),
              ),
              const SizedBox(width: 10),
              Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Row(
                    children: [
                      Text(
                        str.configListProfilesTitle,
                        style: TextStyle(
                          fontSize: 13,
                          fontWeight: FontWeight.w700,
                          color: c.textPrimary,
                        ),
                      ),
                      const SizedBox(width: 8),
                      Container(
                        padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 1.5),
                        decoration: BoxDecoration(
                          color: c.input.withValues(alpha: 0.6),
                          borderRadius: BorderRadius.circular(10),
                        ),
                        child: Text(
                          '${configs.length}',
                          style: TextStyle(
                            fontSize: 10.5,
                            fontWeight: FontWeight.w700,
                            color: c.textSecondary,
                          ),
                        ),
                      ),
                    ],
                  ),
                  Text(
                    str.configListProfilesSubtitle,
                    style: TextStyle(
                      fontSize: 11,
                      color: c.textMuted,
                    ),
                  ),
                ],
              ),
              const Spacer(),

              if (configs.isNotEmpty && onClearAll != null) ...[
                _ActionButton(
                  icon: Icons.delete_sweep_rounded,
                  label: str.v2rayClearAll,
                  color: BrandColors.danger,
                  onTap: onClearAll!,
                ),
                const SizedBox(width: 8),
              ],

              if (configs.isNotEmpty) ...[
                _ActionButton(
                  icon: isTestingAll ? Icons.hourglass_top_rounded : Icons.speed_rounded,
                  label: isTestingAll ? str.v2rayTesting : str.configListPingAll,
                  color: BrandColors.success,
                  isLoading: isTestingAll,
                  onTap: isTestingAll ? () {} : onPingAll,
                ),
                const SizedBox(width: 8),
              ],

              _ActionButton(
                icon: Icons.link_rounded,
                label: str.configListImportLink,
                color: BrandColors.accentPurple,
                onTap: onImportConfig,
              ),

              const SizedBox(width: 8),

              _ActionButton(
                icon: Icons.add_rounded,
                label: str.configListAddConfig,
                color: BrandColors.accentCyan,
                isPrimary: true,
                onTap: onAddConfig,
              ),
            ],
          ),

          const SizedBox(height: 14),

          if (configs.isEmpty)
            Center(
              child: Padding(
                padding: const EdgeInsets.symmetric(vertical: 36),
                child: Column(
                  children: [
                    Container(
                      padding: const EdgeInsets.all(14),
                      decoration: BoxDecoration(
                        color: c.input.withValues(alpha: 0.5),
                        shape: BoxShape.circle,
                      ),
                      child: Icon(
                        Icons.settings_ethernet_rounded,
                        size: 32,
                        color: c.textMuted,
                      ),
                    ),
                    const SizedBox(height: 12),
                    Text(
                      str.configListNoConfigs,
                      style: TextStyle(
                        fontSize: 13,
                        fontWeight: FontWeight.w600,
                        color: c.textPrimary,
                      ),
                    ),
                    const SizedBox(height: 4),
                    Text(
                      str.configListNoConfigsDesc,
                      style: TextStyle(
                        fontSize: 11.5,
                        color: c.textMuted,
                      ),
                    ),
                    const SizedBox(height: 14),
                    Row(
                      mainAxisSize: MainAxisSize.min,
                      children: [
                        ElevatedButton.icon(
                          onPressed: onImportConfig,
                          icon: const Icon(Icons.paste_rounded, size: 14),
                          label: Text(str.configListImportFromLink, style: const TextStyle(fontSize: 11.5)),
                          style: ElevatedButton.styleFrom(
                            backgroundColor: BrandColors.accentPurple,
                            foregroundColor: Colors.white,
                            padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 8),
                            shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(8)),
                          ),
                        ),
                        const SizedBox(width: 10),
                        OutlinedButton.icon(
                          onPressed: onAddConfig,
                          icon: const Icon(Icons.add_rounded, size: 14),
                          label: Text(str.configListManualEntry, style: const TextStyle(fontSize: 11.5)),
                          style: OutlinedButton.styleFrom(
                            foregroundColor: c.textPrimary,
                            side: BorderSide(color: c.border),
                            padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 8),
                            shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(8)),
                          ),
                        ),
                      ],
                    ),
                  ],
                ),
              ),
            ),

          if (configs.isNotEmpty)
            ListView.separated(
              shrinkWrap: true,
              physics: const NeverScrollableScrollPhysics(),
              itemCount: configs.length,
              separatorBuilder: (context, index) => const SizedBox(height: 8),
              itemBuilder: (context, index) {
                final cfg = configs[index];
                return ConfigItem(
                  key: ValueKey(cfg.id),
                  config: cfg,
                  onEdit: () => onEditConfig(cfg),
                  onDelete: () => onDeleteConfig(cfg),
                  onTestLatency: () => onTestLatency(cfg),
                  onToggleActive: () => onToggleActive(cfg),
                );
              },
            ),
        ],
      ),
    );
  }
}

class _ActionButton extends StatefulWidget {
  const _ActionButton({
    required this.icon,
    required this.label,
    required this.color,
    this.isPrimary = false,
    this.isLoading = false,
    required this.onTap,
  });

  final IconData icon;
  final String label;
  final Color color;
  final bool isPrimary;
  final bool isLoading;
  final VoidCallback onTap;

  @override
  State<_ActionButton> createState() => _ActionButtonState();
}

class _ActionButtonState extends State<_ActionButton> {
  bool _hovered = false;

  @override
  Widget build(BuildContext context) {
    final c = Theme.of(context).extension<AppColors>()!;

    return MouseRegion(
      cursor: SystemMouseCursors.click,
      onEnter: (_) => setState(() => _hovered = true),
      onExit: (_) => setState(() => _hovered = false),
      child: GestureDetector(
        onTap: widget.onTap,
        child: AnimatedContainer(
          duration: const Duration(milliseconds: 140),
          padding: const EdgeInsets.symmetric(horizontal: 11, vertical: 6.5),
          decoration: BoxDecoration(
            color: widget.isPrimary
                ? widget.color
                : _hovered
                    ? widget.color.withValues(alpha: 0.18)
                    : widget.color.withValues(alpha: 0.09),
            borderRadius: BorderRadius.circular(8),
            border: Border.all(
              color: widget.isPrimary
                  ? widget.color
                  : widget.color.withValues(alpha: _hovered ? 0.5 : 0.25),
            ),
          ),
          child: Row(
            mainAxisSize: MainAxisSize.min,
            children: [
              if (widget.isLoading)
                SizedBox(
                  width: 12,
                  height: 12,
                  child: CircularProgressIndicator(
                    strokeWidth: 2,
                    valueColor: AlwaysStoppedAnimation<Color>(
                      widget.isPrimary ? Colors.white : widget.color,
                    ),
                  ),
                )
              else
                Icon(
                  widget.icon,
                  size: 13.5,
                  color: widget.isPrimary ? Colors.white : widget.color,
                ),
              const SizedBox(width: 5),
              Text(
                widget.label,
                style: TextStyle(
                  fontSize: 11,
                  fontWeight: FontWeight.w600,
                  color: widget.isPrimary ? Colors.white : c.textPrimary,
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

class ConfigItem extends ConsumerStatefulWidget {
  const ConfigItem({
    super.key,
    required this.config,
    required this.onEdit,
    required this.onDelete,
    required this.onTestLatency,
    required this.onToggleActive,
  });

  final V2RayConfig config;
  final VoidCallback onEdit;
  final VoidCallback onDelete;
  final VoidCallback onTestLatency;
  final VoidCallback onToggleActive;

  @override
  ConsumerState<ConfigItem> createState() => _ConfigItemState();
}

class _ConfigItemState extends ConsumerState<ConfigItem> {
  bool _hovered = false;

  Color _getProtocolColor(String protocol) {
    switch (protocol.toLowerCase()) {
      case 'vless':
        return BrandColors.accentCyan;
      case 'vmess':
        return BrandColors.accentPurple;
      case 'trojan':
        return Colors.orangeAccent;
      case 'shadowsocks':
        return Colors.greenAccent;
      case 'hysteria2':
        return Colors.pinkAccent;
      default:
        return Colors.tealAccent;
    }
  }

  @override
  Widget build(BuildContext context) {
    final c = Theme.of(context).extension<AppColors>()!;
    final cfg = widget.config;
    final protoColor = _getProtocolColor(cfg.protocol);

    return MouseRegion(
      cursor: SystemMouseCursors.click,
      onEnter: (_) => setState(() => _hovered = true),
      onExit: (_) => setState(() => _hovered = false),
      child: GestureDetector(
        behavior: HitTestBehavior.opaque,
        onTap: widget.onToggleActive,
        child: AnimatedContainer(
        duration: const Duration(milliseconds: 160),
        padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 11),
        decoration: BoxDecoration(
          color: cfg.isActive
              ? BrandColors.accentCyan.withValues(alpha: 0.08)
              : _hovered
                  ? c.cardElevated.withValues(alpha: 0.7)
                  : c.card.withValues(alpha: 0.35),
          borderRadius: BorderRadius.circular(10),
          border: Border.all(
            color: cfg.isActive
                ? BrandColors.accentCyan.withValues(alpha: 0.5)
                : _hovered
                    ? c.border
                    : c.border.withValues(alpha: 0.35),
            width: cfg.isActive ? 1.4 : 1,
          ),
        ),
        child: Row(
          children: [

            GestureDetector(
              onTap: widget.onToggleActive,
              child: AnimatedContainer(
                duration: const Duration(milliseconds: 140),
                width: 22,
                height: 22,
                decoration: BoxDecoration(
                  shape: BoxShape.circle,
                  color: cfg.isActive
                      ? BrandColors.accentCyan.withValues(alpha: 0.2)
                      : Colors.transparent,
                  border: Border.all(
                    color: cfg.isActive ? BrandColors.accentCyan : c.textMuted.withValues(alpha: 0.6),
                    width: cfg.isActive ? 2 : 1.5,
                  ),
                ),
                child: cfg.isActive
                    ? Center(
                        child: Container(
                          width: 10,
                          height: 10,
                          decoration: const BoxDecoration(
                            shape: BoxShape.circle,
                            color: BrandColors.accentCyan,
                          ),
                        ),
                      )
                    : null,
              ),
            ),

            const SizedBox(width: 12),

            Container(
              padding: const EdgeInsets.symmetric(horizontal: 7, vertical: 3),
              decoration: BoxDecoration(
                color: protoColor.withValues(alpha: 0.15),
                borderRadius: BorderRadius.circular(6),
                border: Border.all(color: protoColor.withValues(alpha: 0.4)),
              ),
              child: Text(
                cfg.protocol.toUpperCase(),
                style: TextStyle(
                  fontSize: 10,
                  fontWeight: FontWeight.w700,
                  color: protoColor,
                  letterSpacing: 0.4,
                ),
              ),
            ),

            const SizedBox(width: 10),

            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Row(
                    children: [
                      Flexible(
                        child: Text(
                          cfg.name,
                          overflow: TextOverflow.ellipsis,
                          style: TextStyle(
                            fontSize: 12.5,
                            fontWeight: FontWeight.w600,
                            color: c.textPrimary,
                          ),
                        ),
                      ),
                      if (cfg.isActive) ...[
                        const SizedBox(width: 6),
                        Container(
                          padding: const EdgeInsets.symmetric(horizontal: 5, vertical: 1),
                          decoration: BoxDecoration(
                            color: BrandColors.accentCyan.withValues(alpha: 0.2),
                            borderRadius: BorderRadius.circular(4),
                          ),
                          child: Text(
                            ref.watch(stringsProvider).activeBadge,
                            style: const TextStyle(
                              fontSize: 9,
                              fontWeight: FontWeight.w700,
                              color: BrandColors.accentCyan,
                            ),
                          ),
                        ),
                      ],
                    ],
                  ),
                  const SizedBox(height: 3),
                  Row(
                    children: [
                      Text(
                        '${cfg.address}:${cfg.port}',
                        style: TextStyle(
                          fontSize: 11,
                          fontFamily: 'monospace',
                          color: c.textMuted,
                        ),
                      ),
                      const SizedBox(width: 8),

                      Container(
                        padding: const EdgeInsets.symmetric(horizontal: 5, vertical: 1),
                        decoration: BoxDecoration(
                          color: c.input.withValues(alpha: 0.5),
                          borderRadius: BorderRadius.circular(4),
                        ),
                        child: Text(
                          cfg.network.toUpperCase(),
                          style: TextStyle(
                            fontSize: 9,
                            fontWeight: FontWeight.w600,
                            color: c.textSecondary,
                          ),
                        ),
                      ),
                      if (cfg.security.isNotEmpty && cfg.security != 'none') ...[
                        const SizedBox(width: 5),
                        Container(
                          padding: const EdgeInsets.symmetric(horizontal: 5, vertical: 1),
                          decoration: BoxDecoration(
                            color: (cfg.security == 'reality' ? BrandColors.accentPurple : BrandColors.accentCyan)
                                .withValues(alpha: 0.12),
                            borderRadius: BorderRadius.circular(4),
                          ),
                          child: Text(
                            cfg.security.toUpperCase(),
                            style: TextStyle(
                              fontSize: 9,
                              fontWeight: FontWeight.w600,
                              color: cfg.security == 'reality' ? BrandColors.accentPurple : BrandColors.accentCyan,
                            ),
                          ),
                        ),
                      ],
                    ],
                  ),
                ],
              ),
            ),

            const SizedBox(width: 8),

            _LatencyBadge(
              latency: cfg.latency,
              onTest: widget.onTestLatency,
            ),

            const SizedBox(width: 8),

            Row(
              mainAxisSize: MainAxisSize.min,
              children: [

                _IconActionButton(
                  icon: Icons.copy_rounded,
                  tooltip: ref.watch(stringsProvider).configListCopyLink,
                  onTap: () {
                    Clipboard.setData(ClipboardData(
                      text: '${cfg.protocol}://${cfg.userId}@${cfg.address}:${cfg.port}#${Uri.encodeComponent(cfg.name)}',
                    ));
                    ScaffoldMessenger.of(context).showSnackBar(
                      SnackBar(
                        content: Text('${cfg.name} ${ref.read(stringsProvider).configListCopied}'),
                        duration: const Duration(seconds: 2),
                        behavior: SnackBarBehavior.floating,
                      ),
                    );
                  },
                ),
                const SizedBox(width: 4),

                _IconActionButton(
                  icon: Icons.edit_rounded,
                  tooltip: ref.watch(stringsProvider).configListEdit,
                  onTap: widget.onEdit,
                ),
                const SizedBox(width: 4),

                _IconActionButton(
                  icon: Icons.delete_outline_rounded,
                  hoverColor: Colors.redAccent,
                  tooltip: ref.watch(stringsProvider).configListDelete,
                  onTap: widget.onDelete,
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

class _LatencyBadge extends StatefulWidget {
  const _LatencyBadge({
    required this.latency,
    required this.onTest,
  });

  final int latency;
  final VoidCallback onTest;

  @override
  State<_LatencyBadge> createState() => _LatencyBadgeState();
}

class _LatencyBadgeState extends State<_LatencyBadge> {
  bool _hovered = false;

  Color _getLatencyColor(int lat) {
    if (lat < 0) return Colors.grey;
    if (lat < 120) return const Color(0xFF10B981);
    if (lat < 280) return const Color(0xFFF59E0B);
    return const Color(0xFFEF4444);
  }

  @override
  Widget build(BuildContext context) {
    final c = Theme.of(context).extension<AppColors>()!;
    final lat = widget.latency;
    final isTesting = lat == -2;
    final color = _getLatencyColor(lat);

    return MouseRegion(
      cursor: SystemMouseCursors.click,
      onEnter: (_) => setState(() => _hovered = true),
      onExit: (_) => setState(() => _hovered = false),
      child: GestureDetector(
        onTap: isTesting ? null : widget.onTest,
        child: AnimatedContainer(
          duration: const Duration(milliseconds: 140),
          padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 4),
          decoration: BoxDecoration(
            color: _hovered
                ? color.withValues(alpha: 0.2)
                : color.withValues(alpha: 0.1),
            borderRadius: BorderRadius.circular(6),
            border: Border.all(color: color.withValues(alpha: _hovered ? 0.6 : 0.3)),
          ),
          child: Row(
            mainAxisSize: MainAxisSize.min,
            children: [
              if (isTesting) ...[
                SizedBox(
                  width: 10,
                  height: 10,
                  child: CircularProgressIndicator(
                    strokeWidth: 1.5,
                    valueColor: AlwaysStoppedAnimation<Color>(color),
                  ),
                ),
                const SizedBox(width: 5),
                Text(
                  'Ping...',
                  style: TextStyle(
                    fontSize: 10.5,
                    fontWeight: FontWeight.w600,
                    color: c.textSecondary,
                  ),
                ),
              ] else if (lat > 0) ...[
                Container(
                  width: 6,
                  height: 6,
                  decoration: BoxDecoration(
                    shape: BoxShape.circle,
                    color: color,
                  ),
                ),
                const SizedBox(width: 5),
                Text(
                  '$lat ms',
                  style: TextStyle(
                    fontSize: 10.5,
                    fontFamily: 'monospace',
                    fontWeight: FontWeight.w700,
                    color: color,
                  ),
                ),
              ] else if (lat == -3) ...[
                const Icon(Icons.error_outline_rounded, size: 12, color: Colors.redAccent),
                const SizedBox(width: 4),
                const Text(
                  'Timeout',
                  style: TextStyle(
                    fontSize: 10,
                    fontWeight: FontWeight.w600,
                    color: Colors.redAccent,
                  ),
                ),
              ] else ...[
                Icon(Icons.speed_rounded, size: 12, color: c.textMuted),
                const SizedBox(width: 4),
                Text(
                  'Ping',
                  style: TextStyle(
                    fontSize: 10.5,
                    fontWeight: FontWeight.w600,
                    color: c.textMuted,
                  ),
                ),
              ],
            ],
          ),
        ),
      ),
    );
  }
}

class _IconActionButton extends StatefulWidget {
  const _IconActionButton({
    required this.icon,
    required this.onTap,
    this.hoverColor,
    this.tooltip,
  });

  final IconData icon;
  final VoidCallback onTap;
  final Color? hoverColor;
  final String? tooltip;

  @override
  State<_IconActionButton> createState() => _IconActionButtonState();
}

class _IconActionButtonState extends State<_IconActionButton> {
  bool _hovered = false;

  @override
  Widget build(BuildContext context) {
    final c = Theme.of(context).extension<AppColors>()!;
    final effectiveColor = widget.hoverColor ?? BrandColors.accentCyan;

    final btn = MouseRegion(
      cursor: SystemMouseCursors.click,
      onEnter: (_) => setState(() => _hovered = true),
      onExit: (_) => setState(() => _hovered = false),
      child: GestureDetector(
        onTap: widget.onTap,
        child: AnimatedContainer(
          duration: const Duration(milliseconds: 140),
          padding: const EdgeInsets.all(6),
          decoration: BoxDecoration(
            color: _hovered
                ? effectiveColor.withValues(alpha: 0.15)
                : Colors.transparent,
            borderRadius: BorderRadius.circular(6),
          ),
          child: Icon(
            widget.icon,
            size: 15,
            color: _hovered ? effectiveColor : c.textMuted,
          ),
        ),
      ),
    );

    if (widget.tooltip != null) {
      return Tooltip(message: widget.tooltip!, child: btn);
    }
    return btn;
  }
}

