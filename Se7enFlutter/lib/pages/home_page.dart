import 'dart:async';
import 'dart:math' as math;
import 'package:flutter/material.dart' hide ConnectionState;
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:country_flags/country_flags.dart';

import '../core/data/region_catalog.dart';
import '../core/i18n/app_strings.dart';
import '../core/models/connection_method.dart';
import '../core/models/connection_state.dart';
import '../core/models/tun_health.dart';
import '../core/models/tunnel_status.dart';
import '../core/services/providers.dart';
import '../core/utils/formatters.dart';
import '../theme/app_colors.dart';
import '../theme/app_theme.dart';
import '../widgets/admin_elevation_dialog.dart';
import '../widgets/aurora_orb_button.dart';
import '../widgets/glass_controls.dart';
import '../widgets/modern_region_picker.dart';
import '../widgets/v2ray_missing_config_dialog.dart';

class HomePage extends ConsumerStatefulWidget {
  const HomePage({super.key});

  @override
  ConsumerState<HomePage> createState() => _HomePageState();
}

class _HomePageState extends ConsumerState<HomePage> {
  void _handleToggle(
    BuildContext context,
    ConnectionMethod method,
    dynamic settings,
    ConnectionController controller,
    ConnectionState state,
  ) {
    if (state == ConnectionState.connected || state == ConnectionState.connecting) {
      controller.toggle();
      return;
    }

    if (method.usesV2Ray) {
      final configs = settings.v2rayConfigs as List<dynamic>;
      final hasConfigs = configs.isNotEmpty;
      final hasActiveConfig = hasConfigs && configs.any((c) => c.isActive == true);
      if (!hasConfigs || !hasActiveConfig) {
        V2RayMissingConfigDialog.show(
          context,
          hasConfigs: hasConfigs,
          method: method,
        );
        return;
      }
    }

    controller.toggle();
  }

  @override
  Widget build(BuildContext context) {

    final connectionState = ref.watch(connectionStateProvider);
    final settings = ref.watch(settingsProvider);
    final controller = ref.read(connectionControllerProvider);

    final method = ConnectionMethodX.parse(settings.connectionMethod);

    return Padding(
      padding: const EdgeInsets.all(16),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [

          Expanded(
            flex: 6,
            child: _HeroConnectionStage(
              state: connectionState,
              onToggle: () => _handleToggle(context, method, settings, controller, connectionState),
            ),
          ),

          const SizedBox(width: 14),

          Expanded(
            flex: 5,
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [

                _EngineMethodCard(
                  method: method,
                  state: connectionState,
                  onMethodChanged: (m) => controller.setMethod(m),
                ),
                const SizedBox(height: 8),

                if (method.supportsRegionPicker) ...[
                  const SizedBox(height: 8),
                  _EgressRegionCard(
                    method: method,
                    onRegionPicked: (code) => controller.setEgressRegion(code, method: method),
                  ),
                ],
                const SizedBox(height: 8),

                const Expanded(
                  child: _NetworkTelemetryHub(
                    key: ValueKey('network_telemetry_hub'),
                  ),
                ),
              ],
            ),
          ),
        ],
      ),
    );
  }
}

class _HeroUptimeBadge extends ConsumerStatefulWidget {
  const _HeroUptimeBadge();

  @override
  ConsumerState<_HeroUptimeBadge> createState() => _HeroUptimeBadgeState();
}

class _HeroUptimeBadgeState extends ConsumerState<_HeroUptimeBadge> {
  Timer? _timer;
  Duration _uptime = Duration.zero;

  @override
  void initState() {
    super.initState();
    _tick();
    _timer = Timer.periodic(const Duration(seconds: 1), (_) => _tick());
  }

  void _tick() {
    final start = ref.read(connectionStartTimeProvider);
    if (mounted) {
      final now = DateTime.now();
      setState(() {
        _uptime = start != null ? now.difference(start) : Duration.zero;
      });
    }
  }

  @override
  void dispose() {
    _timer?.cancel();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 4),
      decoration: BoxDecoration(
        color: const Color(0xFF10B981).withValues(alpha: 0.12),
        borderRadius: BorderRadius.circular(12),
        border: Border.all(color: const Color(0xFF10B981).withValues(alpha: 0.3)),
      ),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          const Icon(Icons.timer_outlined, size: 13, color: Color(0xFF10B981)),
          const SizedBox(width: 5),
          Text(
            formatUptime(_uptime),
            style: AppTheme.mono(const Color(0xFF10B981), size: 12, weight: FontWeight.w700),
          ),
        ],
      ),
    );
  }
}

class _HeroConnectionStage extends ConsumerWidget {
  const _HeroConnectionStage({
    required this.state,
    required this.onToggle,
  });

  final ConnectionState state;
  final VoidCallback onToggle;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final c = Theme.of(context).extension<AppColors>()!;
    final str = ref.watch(stringsProvider);
    final isConnected = state == ConnectionState.connected;
    final isConnecting = state == ConnectionState.connecting || state == ConnectionState.disconnecting;

    final progress = isConnecting
        ? ref.watch(tunnelStatusProvider.select((s) => (
            percent: s.valueOrNull?.connectProgressPercent ?? 0,
            text: s.valueOrNull?.connectProgressText ?? '',
          )))
        : const (percent: 0, text: '');

    final auraColor = switch (state) {
      ConnectionState.connected => const Color(0xFF10B981),
      ConnectionState.connecting || ConnectionState.disconnecting => const Color(0xFFF59E0B),
      ConnectionState.error => const Color(0xFFEF4444),
      _ => const Color(0xFF8B5CF6),
    };

    final statusText = switch (state) {
      ConnectionState.connected => str.statusConnected,
      ConnectionState.connecting || ConnectionState.disconnecting => str.statusConnecting,
      ConnectionState.error => str.statusError,
      _ => str.statusDisconnected,
    };

    return RepaintBoundary(
      child: GlassCard(
        padding: const EdgeInsets.all(20),
        borderRadius: 20,
        child: Column(
          children: [

            Row(
              mainAxisAlignment: MainAxisAlignment.spaceBetween,
              children: [
                Row(
                  children: [
                    Container(
                      width: 9,
                      height: 9,
                      decoration: BoxDecoration(
                        shape: BoxShape.circle,
                        color: auraColor,
                        boxShadow: [
                          BoxShadow(
                            color: auraColor.withValues(alpha: 0.6),
                            blurRadius: 8,
                            spreadRadius: 1,
                          ),
                        ],
                      ),
                    ),
                    const SizedBox(width: 8),
                    Text(
                      statusText,
                      style: TextStyle(
                        fontSize: 11.5,
                        fontWeight: FontWeight.w800,
                        letterSpacing: 1.0,
                        color: auraColor,
                      ),
                    ),
                  ],
                ),
                if (isConnected)
                  const _HeroUptimeBadge()
                else
                  Text(
                    'SE7EN CORE',
                    style: TextStyle(
                      fontSize: 10,
                      fontWeight: FontWeight.w700,
                      letterSpacing: 0.8,
                      color: c.textMuted,
                    ),
                  ),
              ],
            ),

          const Spacer(),

          Center(
            child: Stack(
              alignment: Alignment.center,
              children: [

                Container(
                  width: 210,
                  height: 210,
                  decoration: BoxDecoration(
                    shape: BoxShape.circle,
                    gradient: RadialGradient(
                      colors: [
                        auraColor.withValues(alpha: c.isDark ? 0.20 : 0.12),
                        Colors.transparent,
                      ],
                      stops: const [0.25, 0.95],
                    ),
                  ),
                ),

                AuroraOrbButton(
                  state: state,
                  percent: progress.percent,
                  onTap: onToggle,
                  size: 190,
                ),
              ],
            ),
          ),

          const SizedBox(height: 16),

          if (isConnecting)
            Text(
              (progress.text.isNotEmpty &&
                      !progress.text.trim().toLowerCase().startsWith('connected') &&
                      progress.text.trim().toLowerCase() != 'connected')
                  ? progress.text
                  : str.subtextConnecting,
              textAlign: TextAlign.center,
              style: const TextStyle(
                fontSize: 12.5,
                fontWeight: FontWeight.w700,
                color: Color(0xFFF59E0B),
              ),
            )
          else if (isConnected)
            Text(
              str.subtextConnected,
              textAlign: TextAlign.center,
              style: const TextStyle(
                fontSize: 12.5,
                fontWeight: FontWeight.w700,
                color: Color(0xFF10B981),
              ),
            )
          else if (state == ConnectionState.error)
            Text(
              str.subtextError,
              textAlign: TextAlign.center,
              style: const TextStyle(
                fontSize: 12.5,
                fontWeight: FontWeight.w700,
                color: Color(0xFFEF4444),
              ),
            )
          else
            Text(
              str.subtextDisconnected,
              textAlign: TextAlign.center,
              style: TextStyle(
                fontSize: 12,
                fontWeight: FontWeight.w500,
                color: c.textMuted,
              ),
            ),

          const Spacer(),

          const _HeroRoutingModes(),
        ],
      ),
    ),
  );
}
}

class _EngineMethodCard extends ConsumerWidget {
  const _EngineMethodCard({
    required this.method,
    required this.state,
    required this.onMethodChanged,
  });

  final ConnectionMethod method;
  final ConnectionState state;
  final ValueChanged<ConnectionMethod> onMethodChanged;

  IconData _iconFor(ConnectionMethod m) {
    if (m == ConnectionMethod.psiphon ||
        m == ConnectionMethod.psiphonOverWarp ||
        m == ConnectionMethod.psiphonOverV2Ray) {
      return Icons.vpn_lock_rounded;
    }
    if (m.isShard) {
      return Icons.hub_rounded;
    }
    return m.isAether ? Icons.bolt_rounded : Icons.shield_moon_rounded;
  }

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final c = Theme.of(context).extension<AppColors>()!;
    final str = ref.watch(stringsProvider);

    return GlassCard(
      padding: const EdgeInsets.all(12),
      borderRadius: 16,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [

          Row(
            children: [
              Container(
                padding: const EdgeInsets.all(5),
                decoration: BoxDecoration(
                  color: BrandColors.primary.withValues(alpha: 0.12),
                  borderRadius: BorderRadius.circular(7),
                ),
                child: const Icon(Icons.alt_route_rounded, size: 15, color: BrandColors.primary),
              ),
              const SizedBox(width: 8),
              Text(
                str.connectionProtocol,
                style: TextStyle(
                  fontSize: 10.5,
                  fontWeight: FontWeight.w800,
                  letterSpacing: 0.6,
                  color: c.textMuted,
                ),
              ),
            ],
          ),

          const SizedBox(height: 8),

          _MethodPopupButton(
            method: method,
            isBusy: state.isBusy,
            onMethodChanged: onMethodChanged,
            iconFor: _iconFor,
          ),

          const SizedBox(height: 7),

          _ProtocolSettingsButton(method: method),
        ],
      ),
    );
  }
}

class _ProtocolSettingsButton extends ConsumerStatefulWidget {
  const _ProtocolSettingsButton({required this.method});
  final ConnectionMethod method;

  @override
  ConsumerState<_ProtocolSettingsButton> createState() => _ProtocolSettingsButtonState();
}

class _ProtocolSettingsButtonState extends ConsumerState<_ProtocolSettingsButton> {
  bool _hovered = false;

  @override
  Widget build(BuildContext context) {
    final c = Theme.of(context).extension<AppColors>()!;

    final (targetTab, label, subMode) = switch (widget.method) {
      ConnectionMethod.psiphon =>
        (SettingsTab.psiphon, 'Configure Psiphon Settings', null),
      ConnectionMethod.masque =>
        (SettingsTab.aether, 'Configure MASQUE Settings', 'masque'),
      ConnectionMethod.wireguard =>
        (SettingsTab.aether, 'Configure WireGuard Settings', 'wireguard'),
      ConnectionMethod.warpOnWarp =>
        (SettingsTab.aether, 'Configure Double WARP Settings', 'warp'),
      ConnectionMethod.masqueOnMasque =>
        (SettingsTab.aether, 'Configure Masque on Masque Settings', 'masque_on_masque'),
      ConnectionMethod.tor =>
        (SettingsTab.tor, 'Configure Tor Circuit Settings', null),
      ConnectionMethod.psiphonOverWarp =>
        (SettingsTab.chained, 'Configure Psiphon over WARP Settings', 'psiphon_warp'),
      ConnectionMethod.torOverWarp =>
        (SettingsTab.chained, 'Configure Tor over WARP Settings', 'tor_warp'),
      ConnectionMethod.psiphonOverV2Ray =>
        (SettingsTab.chained, 'Configure Psiphon over V2Ray Settings', 'psiphon_v2ray'),
      ConnectionMethod.torOverV2Ray =>
        (SettingsTab.chained, 'Configure Tor over V2Ray Settings', 'tor_v2ray'),
      ConnectionMethod.shard =>
        (SettingsTab.shard, 'Configure SHARD Settings', null),
    };

    return MouseRegion(
      cursor: SystemMouseCursors.click,
      onEnter: (_) => setState(() => _hovered = true),
      onExit: (_) => setState(() => _hovered = false),
      child: GestureDetector(
        onTap: () {
          if (targetTab == SettingsTab.chained && subMode != null) {
            ref.read(settingsProvider.notifier).update((s) => s.chainedSubMode = subMode);
          } else if (targetTab == SettingsTab.aether && subMode != null) {
            ref.read(settingsProvider.notifier).update((s) => s.aetherProtocol = subMode);
          }
          ref.read(currentSettingsTabProvider.notifier).state = targetTab;
          ref.read(currentNavIndexProvider.notifier).state = 3;
        },
        child: AnimatedContainer(
          duration: const Duration(milliseconds: 140),
          padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 6.5),
          decoration: BoxDecoration(
            color: _hovered
                ? BrandColors.primary.withValues(alpha: 0.12)
                : c.input.withValues(alpha: 0.45),
            borderRadius: BorderRadius.circular(8),
            border: Border.all(
              color: _hovered
                  ? BrandColors.primary.withValues(alpha: 0.5)
                  : c.border.withValues(alpha: 0.45),
            ),
          ),
          child: Row(
            children: [
              Icon(
                Icons.tune_rounded,
                size: 13.5,
                color: _hovered ? BrandColors.primary : c.textSecondary,
              ),
              const SizedBox(width: 8),
              Expanded(
                child: Text(
                  label,
                  style: TextStyle(
                    fontSize: 11,
                    fontWeight: FontWeight.w600,
                    color: _hovered ? BrandColors.primary : c.textSecondary,
                  ),
                ),
              ),
              Icon(
                Icons.arrow_forward_rounded,
                size: 12.5,
                color: _hovered ? BrandColors.primary : c.textMuted,
              ),
            ],
          ),
        ),
      ),
    );
  }
}

class _HeroRoutingModes extends ConsumerWidget {
  const _HeroRoutingModes();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final c = Theme.of(context).extension<AppColors>()!;
    final str = ref.watch(stringsProvider);
    final settings = ref.watch(settingsProvider);
    final (isAdmin, tunStatusText, tunActive, tunLastError) = ref.watch(
      tunnelStatusProvider.select((s) {
        final val = s.valueOrNull;
        return (
          val?.isAdmin ?? false,
          val?.tunStatusText ?? 'Off',
          val?.tunActive ?? false,
          val?.tunLastError ?? '',
        );
      }),
    );
    final tun = TunHealth.of(
      TunnelStatus(
        isAdmin: isAdmin,
        tunStatusText: tunStatusText,
        tunActive: tunActive,
        tunLastError: tunLastError,
      ),
      settings.systemWideTunneling,
      'TUN — all system traffic',
    );
    Color tunColor;
    if (tun.failed) {
      tunColor = BrandColors.danger;
    } else if (tun.active) {
      tunColor = BrandColors.emerald;
    } else if (tun.wanted && tun.busy) {
      tunColor = BrandColors.warning;
    } else {
      tunColor = c.textMuted;
    }
    final isProxyActive = settings.setSystemProxy;
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 8),
      decoration: BoxDecoration(color: c.input.withValues(alpha: 0.5), borderRadius: BorderRadius.circular(12), border: Border.all(color: c.border.withValues(alpha: 0.4))),
      child: Column(children: [
        _RoutingToggleRow(
          icon: Icons.shield_rounded,
          iconColor: tunColor,
          title: str.tunMode,
          subtitle: tun.detail,
          subtitleTooltip: tun.errorTooltip,
          value: tun.wanted,
          trailingBadge: !isAdmin
              ? _RoutingBadge(label: str.adminReq, icon: Icons.lock_outline_rounded, color: BrandColors.warning)
              : tun.failed
                  ? _RoutingBadge(label: str.failed, icon: Icons.error_outline_rounded, color: BrandColors.danger)
                  : tun.wanted && tun.busy
                      ? _RoutingBadge(label: str.starting, icon: Icons.sync_rounded, color: BrandColors.warning)
                      : null,
          onChanged: (v) {
            if (!isAdmin && v) {
              showDialog(context: context, barrierDismissible: true, builder: (ctx) => AdminElevationDialog(onConfirm: () => ref.read(settingsProvider.notifier).update((s) => s.systemWideTunneling = true)));
            } else {
              ref.read(settingsProvider.notifier).update((s) => s.systemWideTunneling = v);
            }
          },
        ),
        Divider(height: 10, color: c.border.withValues(alpha: 0.35)),
        _RoutingToggleRow(icon: Icons.lan_rounded, iconColor: isProxyActive ? BrandColors.accentCyan : c.textMuted, title: str.proxyMode, subtitle: str.proxyModeSubtitle, value: isProxyActive, onChanged: (v) => ref.read(settingsProvider.notifier).update((s) => s.setSystemProxy = v)),
      ]),
    );
  }
}

class _RoutingBadge extends StatelessWidget {
  const _RoutingBadge({
    required this.label,
    required this.icon,
    required this.color,
  });

  final String label;
  final IconData icon;
  final Color color;

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 5, vertical: 1.5),
      margin: const EdgeInsets.only(right: 4),
      decoration: BoxDecoration(
        color: color.withValues(alpha: 0.14),
        borderRadius: BorderRadius.circular(5),
        border: Border.all(color: color.withValues(alpha: 0.3)),
      ),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          Icon(icon, size: 9.5, color: color),
          const SizedBox(width: 3),
          Text(
            label,
            style: TextStyle(fontSize: 9, fontWeight: FontWeight.w700, color: color),
          ),
        ],
      ),
    );
  }
}

class _RoutingToggleRow extends StatelessWidget {
  const _RoutingToggleRow({
    required this.icon,
    required this.iconColor,
    required this.title,
    required this.subtitle,
    required this.value,
    required this.onChanged,
    this.trailingBadge,
    this.subtitleTooltip,
  });

  final IconData icon;
  final Color iconColor;
  final String title;
  final String subtitle;
  final bool value;
  final ValueChanged<bool> onChanged;
  final Widget? trailingBadge;
  final String? subtitleTooltip;

  @override
  Widget build(BuildContext context) {
    final c = Theme.of(context).extension<AppColors>()!;

    Widget subtitleText = Text(
      subtitle,
      style: TextStyle(fontSize: 9, color: c.textMuted),
      maxLines: 1,
      overflow: TextOverflow.ellipsis,
    );
    if (subtitleTooltip != null) {
      subtitleText = Tooltip(
        message: subtitleTooltip!,
        waitDuration: const Duration(milliseconds: 300),
        child: subtitleText,
      );
    }

    return InkWell(
      onTap: () => onChanged(!value),
      borderRadius: BorderRadius.circular(8),
      hoverColor: c.hoverBg,
      child: Padding(
        padding: const EdgeInsets.symmetric(horizontal: 4, vertical: 3),
        child: Row(
          children: [
            Container(
              padding: const EdgeInsets.all(4.5),
              decoration: BoxDecoration(
                color: iconColor.withValues(alpha: value ? 0.15 : 0.08),
                borderRadius: BorderRadius.circular(6),
              ),
              child: Icon(icon, size: 13.5, color: iconColor),
            ),
            const SizedBox(width: 8),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                mainAxisSize: MainAxisSize.min,
                children: [
                  Row(
                    children: [
                      Text(
                        title,
                        style: TextStyle(
                          fontSize: 11,
                          fontWeight: FontWeight.w700,
                          color: value ? c.textPrimary : c.textSecondary,
                        ),
                      ),
                      if (trailingBadge != null) ...[
                        const SizedBox(width: 5),
                        trailingBadge!,
                      ],
                    ],
                  ),
                  subtitleText,
                ],
              ),
            ),
            Transform.scale(
              scale: 0.68,
              child: Switch(
                value: value,
                onChanged: onChanged,
              ),
            ),
          ],
        ),
      ),
    );
  }
}

class _MethodPopupButton extends StatefulWidget {
  const _MethodPopupButton({
    required this.method,
    required this.isBusy,
    required this.onMethodChanged,
    required this.iconFor,
  });

  final ConnectionMethod method;
  final bool isBusy;
  final ValueChanged<ConnectionMethod> onMethodChanged;
  final IconData Function(ConnectionMethod) iconFor;

  @override
  State<_MethodPopupButton> createState() => _MethodPopupButtonState();
}

class _MethodPopupButtonState extends State<_MethodPopupButton> {
  bool _hovered = false;

  @override
  Widget build(BuildContext context) {
    final c = Theme.of(context).extension<AppColors>()!;
    final isBusy = widget.isBusy;

    return MouseRegion(
      cursor: isBusy ? SystemMouseCursors.basic : SystemMouseCursors.click,
      onEnter: (_) => setState(() => _hovered = true),
      onExit: (_) => setState(() => _hovered = false),
      child: ClipRRect(
        borderRadius: BorderRadius.circular(10),
        child: Material(
          color: Colors.transparent,
          child: ModernPopupMenuButton<ConnectionMethod>(
            tooltip: 'Choose engine protocol',
            enabled: !isBusy,
            constraints: const BoxConstraints(maxHeight: 390, minWidth: 260),
            onSelected: widget.onMethodChanged,
            itemBuilder: (context) {
              final groups = [
                (
                  'AETHER (CLOUDFLARE WARP)',
                  Icons.bolt_rounded,
                  BrandColors.primary,
                  const [
                    ConnectionMethod.masque,
                    ConnectionMethod.wireguard,
                    ConnectionMethod.warpOnWarp,
                    ConnectionMethod.masqueOnMasque,
                  ],
                ),
                (
                  'PSIPHON PROTOCOL',
                  Icons.vpn_lock_rounded,
                  BrandColors.accentCyan,
                  const [
                    ConnectionMethod.psiphon,
                    ConnectionMethod.psiphonOverWarp,
                    ConnectionMethod.psiphonOverV2Ray,
                  ],
                ),
                (
                  'TOR ONION NETWORK',
                  Icons.shield_moon_rounded,
                  const Color(0xFFC084FC),
                  const [
                    ConnectionMethod.tor,
                    ConnectionMethod.torOverWarp,
                    ConnectionMethod.torOverV2Ray,
                  ],
                ),
                (
                  'SHARD (CF FRAGMENTATION)',
                  Icons.hub_rounded,
                  const Color(0xFF38BDF8),
                  const [
                    ConnectionMethod.shard,
                  ],
                ),
              ];

              final items = <PopupMenuEntry<ConnectionMethod>>[];
              for (var i = 0; i < groups.length; i++) {
                final (title, icon, color, methods) = groups[i];
                if (i > 0) {
                  items.add(
                    const PopupMenuDivider(height: 8),
                  );
                }
                items.add(
                  PopupMenuItem<ConnectionMethod>(
                    enabled: false,
                    height: 22,
                    padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 2),
                    child: Row(
                      children: [
                        Icon(icon, size: 11.5, color: color),
                        const SizedBox(width: 6),
                        Text(
                          title,
                          style: TextStyle(
                            fontSize: 9.5,
                            fontWeight: FontWeight.w800,
                            letterSpacing: 0.8,
                            color: color.withValues(alpha: 0.9),
                          ),
                        ),
                      ],
                    ),
                  ),
                );

                for (final m in methods) {
                  items.add(
                    PopupMenuItem<ConnectionMethod>(
                      value: m,
                      padding: EdgeInsets.zero,
                      height: 32,
                      child: ModernPopupHoverTile(
                        padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 5),
                        borderRadius: 6,
                        child: Row(
                          children: [
                            Icon(
                              widget.iconFor(m),
                              size: 15,
                              color: m == widget.method ? BrandColors.accentCyan : c.textSecondary,
                            ),
                            const SizedBox(width: 9),
                            Expanded(
                              child: Row(
                                children: [
                                  Flexible(
                                    child: Text(
                                      m.displayName,
                                      style: TextStyle(
                                        fontSize: 12,
                                        fontWeight: m == widget.method ? FontWeight.w700 : FontWeight.w500,
                                        color: m == widget.method ? BrandColors.accentCyan : c.textPrimary,
                                      ),
                                      overflow: TextOverflow.ellipsis,
                                    ),
                                  ),
                                ],
                              ),
                            ),
                            if (m == widget.method)
                              const Icon(Icons.check_rounded, size: 15, color: BrandColors.accentCyan),
                          ],
                        ),
                      ),
                    ),
                  );
                }
              }
              return items;
            },
            child: AnimatedContainer(
              duration: const Duration(milliseconds: 140),
              padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 9),
              decoration: BoxDecoration(
                color: _hovered && !isBusy
                    ? c.cardElevated.withValues(alpha: 0.9)
                    : c.input.withValues(alpha: 0.65),
                borderRadius: BorderRadius.circular(10),
                border: Border.all(
                  color: _hovered && !isBusy
                      ? BrandColors.accentCyan.withValues(alpha: 0.55)
                      : c.border.withValues(alpha: 0.6),
                ),
                boxShadow: _hovered && !isBusy
                    ? [
                        BoxShadow(
                          color: BrandColors.accentCyan.withValues(alpha: 0.10),
                          blurRadius: 8,
                          offset: const Offset(0, 2),
                        ),
                      ]
                    : null,
              ),
              child: Row(
                children: [
                  Container(
                    padding: const EdgeInsets.all(6),
                    decoration: BoxDecoration(
                      color: BrandColors.accentCyan.withValues(alpha: 0.14),
                      borderRadius: BorderRadius.circular(8),
                    ),
                    child: Icon(widget.iconFor(widget.method), size: 16, color: BrandColors.accentCyan),
                  ),
                  const SizedBox(width: 10),
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(
                          widget.method.displayName,
                          style: TextStyle(
                            fontSize: 13,
                            fontWeight: FontWeight.w700,
                            color: c.textPrimary,
                          ),
                        ),
                        Text(
                          widget.method.isChained
                              ? 'Multi-Hop DPI Resilience Core'
                              : (widget.method.isAether
                                  ? 'Cloudflare Network Mesh'
                                  : (widget.method.isTor
                                      ? 'Tor Onion Routing Mesh'
                                      : 'Psiphon Obfuscation Core')),
                          style: TextStyle(
                            fontSize: 10,
                            color: c.textMuted,
                            fontWeight: FontWeight.normal,
                          ),
                        ),
                      ],
                    ),
                  ),
                  Container(
                    padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 4),
                    decoration: BoxDecoration(
                      color: c.card.withValues(alpha: 0.8),
                      borderRadius: BorderRadius.circular(6),
                      border: Border.all(color: c.border.withValues(alpha: 0.5)),
                    ),
                    child: Row(
                      mainAxisSize: MainAxisSize.min,
                      children: [
                        Text(
                          'Switch',
                          style: TextStyle(fontSize: 10.5, fontWeight: FontWeight.w600, color: c.textSecondary),
                        ),
                        const SizedBox(width: 4),
                        Icon(Icons.unfold_more_rounded, size: 13, color: c.textMuted),
                      ],
                    ),
                  ),
                ],
              ),
            ),
          ),
        ),
      ),
    );
  }
}

class _EgressRegionCard extends ConsumerWidget {
  const _EgressRegionCard({
    required this.method,
    required this.onRegionPicked,
  });

  final ConnectionMethod method;
  final ValueChanged<String> onRegionPicked;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final c = Theme.of(context).extension<AppColors>()!;
    final str = ref.watch(stringsProvider);
    final selectedCode = ref.watch(settingsProvider.select((s) =>
        method.isTor ? s.torExitCountry : s.egressRegion)).toUpperCase();
    final supportsRegion = method.supportsRegionPicker;

    final regionInfo = ref.watch(tunnelStatusProvider.select((st) => (
      available: st.valueOrNull?.availableEgressRegions ?? const <String>[],
      connected: st.valueOrNull?.connectedServerRegion ?? '',
      isConnected: st.valueOrNull?.state == ConnectionState.connected,
    )));

    final available = method.isTor
        ? RegionCatalog.torSeedRegions
        : (regionInfo.available.isNotEmpty
            ? regionInfo.available
            : RegionCatalog.psiphonSeedRegions);

    final isConnected = regionInfo.isConnected;
    final hasActiveEgress = isConnected && regionInfo.connected.isNotEmpty;

    return GlassCard(
      padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
      borderRadius: 16,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Container(
                padding: const EdgeInsets.all(4.5),
                decoration: BoxDecoration(
                  color: BrandColors.accentCyan.withValues(alpha: 0.12),
                  borderRadius: BorderRadius.circular(6),
                ),
                child: const Icon(Icons.public_rounded, size: 14, color: BrandColors.accentCyan),
              ),
              const SizedBox(width: 8),
              Text(
                str.exitLocation,
                style: TextStyle(
                  fontSize: 10,
                  fontWeight: FontWeight.w800,
                  letterSpacing: 0.6,
                  color: c.textMuted,
                ),
              ),
              const Spacer(),
              if (hasActiveEgress)
                Container(
                  padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 2),
                  decoration: BoxDecoration(
                    color: BrandColors.emerald.withValues(alpha: 0.12),
                    borderRadius: BorderRadius.circular(6),
                    border: Border.all(color: BrandColors.emerald.withValues(alpha: 0.3)),
                  ),
                  child: Row(
                    mainAxisSize: MainAxisSize.min,
                    children: [
                      ClipRRect(
                        borderRadius: BorderRadius.circular(2),
                        child: CountryFlag.fromCountryCode(
                          regionInfo.connected,
                          height: 10,
                          width: 14,
                        ),
                      ),
                      const SizedBox(width: 4),
                      Text(
                        RegionCatalog.nameFor(regionInfo.connected),
                        style: const TextStyle(fontSize: 9.5, fontWeight: FontWeight.w700, color: BrandColors.emerald),
                      ),
                    ],
                  ),
                )
              else if (isConnected && method.isTor)
                Container(
                  padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 2),
                  decoration: BoxDecoration(
                    color: const Color(0xFFC084FC).withValues(alpha: 0.12),
                    borderRadius: BorderRadius.circular(6),
                    border: Border.all(color: const Color(0xFFC084FC).withValues(alpha: 0.3)),
                  ),
                  child: const Row(
                    mainAxisSize: MainAxisSize.min,
                    children: [
                      Icon(Icons.hub_rounded, size: 11, color: Color(0xFFC084FC)),
                      SizedBox(width: 4),
                      Text(
                        'Dynamic / Multi-Hop',
                        style: TextStyle(fontSize: 9.5, fontWeight: FontWeight.w700, color: Color(0xFFC084FC)),
                      ),
                    ],
                  ),
                )
              else if (!supportsRegion)
                Container(
                  padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 2),
                  decoration: BoxDecoration(
                    color: c.input.withValues(alpha: 0.5),
                    borderRadius: BorderRadius.circular(5),
                  ),
                  child: Text(
                    'Fixed by Protocol',
                    style: TextStyle(fontSize: 9.5, color: c.textMuted),
                  ),
                ),
            ],
          ),

          const SizedBox(height: 6),

          _RegionSelectButton(
            method: method,
            selectedCode: selectedCode,
            available: available,
            onRegionPicked: onRegionPicked,
          ),
        ],
      ),
    );
  }
}

class _RegionSelectButton extends StatefulWidget {
  const _RegionSelectButton({
    required this.method,
    required this.selectedCode,
    required this.available,
    required this.onRegionPicked,
  });

  final ConnectionMethod method;
  final String selectedCode;
  final Iterable<String> available;
  final ValueChanged<String> onRegionPicked;

  @override
  State<_RegionSelectButton> createState() => _RegionSelectButtonState();
}

class _RegionSelectButtonState extends State<_RegionSelectButton> {
  bool _hovered = false;

  @override
  Widget build(BuildContext context) {
    final c = Theme.of(context).extension<AppColors>()!;
    final selectedCode = widget.selectedCode;

    return MouseRegion(
      cursor: SystemMouseCursors.click,
      onEnter: (_) => setState(() => _hovered = true),
      onExit: (_) => setState(() => _hovered = false),
      child: GestureDetector(
        onTap: () async {
          final picked = await showModernRegionPicker(
            context: context,
            options: RegionCatalog.optionsFrom(widget.available),
            selectedCode: selectedCode,
          );
          if (picked != null) {
            widget.onRegionPicked(picked.code);
          }
        },
        child: AnimatedContainer(
          duration: const Duration(milliseconds: 140),
          padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 6.5),
          decoration: BoxDecoration(
            color: _hovered
                ? c.cardElevated.withValues(alpha: 0.9)
                : c.input.withValues(alpha: 0.65),
            borderRadius: BorderRadius.circular(9),
            border: Border.all(
              color: _hovered
                  ? BrandColors.accentCyan.withValues(alpha: 0.55)
                  : c.border.withValues(alpha: 0.6),
            ),
            boxShadow: _hovered
                ? [
                    BoxShadow(
                      color: BrandColors.accentCyan.withValues(alpha: 0.10),
                      blurRadius: 8,
                      offset: const Offset(0, 2),
                    ),
                  ]
                : null,
          ),
          child: Row(
            children: [

              Container(
                width: 25,
                height: 18,
                decoration: BoxDecoration(
                  borderRadius: BorderRadius.circular(3.5),
                  border: Border.all(color: c.border.withValues(alpha: 0.5)),
                ),
                child: selectedCode.isEmpty
                    ? const Icon(Icons.flash_on_rounded, size: 13, color: BrandColors.accentCyan)
                    : ClipRRect(
                        borderRadius: BorderRadius.circular(2.5),
                        child: CountryFlag.fromCountryCode(
                          selectedCode,
                          height: 18,
                          width: 25,
                        ),
                      ),
              ),
              const SizedBox(width: 8),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    Text(
                      selectedCode.isEmpty ? 'Optimal (Fastest Available)' : RegionCatalog.nameFor(selectedCode),
                      style: TextStyle(
                        fontSize: 11.5,
                        fontWeight: FontWeight.w700,
                        color: c.textPrimary,
                      ),
                    ),
                    Text(
                      selectedCode.isEmpty
                          ? (widget.method.isTor ? 'Tor Mesh (Dynamic multi-hop exits)' : 'Auto-routed via nearest POP')
                          : 'Exit Node Region: $selectedCode',
                      style: TextStyle(fontSize: 9.5, color: c.textMuted),
                    ),
                  ],
                ),
              ),
              Container(
                padding: const EdgeInsets.symmetric(horizontal: 7, vertical: 3),
                decoration: BoxDecoration(
                  color: c.card.withValues(alpha: 0.8),
                  borderRadius: BorderRadius.circular(6),
                  border: Border.all(color: c.border.withValues(alpha: 0.5)),
                ),
                child: Row(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    Text(
                      selectedCode.isEmpty ? 'AUTO' : selectedCode,
                      style: TextStyle(
                        fontSize: 10,
                        fontWeight: FontWeight.w700,
                        color: c.textSecondary,
                      ),
                    ),
                    const SizedBox(width: 3),
                    Icon(Icons.unfold_more_rounded, size: 12, color: c.textMuted),
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

class _NetworkTelemetryHub extends ConsumerStatefulWidget {
  const _NetworkTelemetryHub({
    super.key,
  });

  @override
  ConsumerState<_NetworkTelemetryHub> createState() => _NetworkTelemetryHubState();
}

class _NetworkTelemetryHubState extends ConsumerState<_NetworkTelemetryHub> {
  int _prevDl = 0;
  int _prevUl = 0;
  double _dlRate = 0;
  double _ulRate = 0;
  double _sessionPeakRate = 0;
  DateTime _lastRateTick = DateTime.now();
  DateTime _lastActiveDlTime = DateTime.now();
  DateTime _lastActiveUlTime = DateTime.now();
  Timer? _rateTicker;

  final List<double> _dlHistory = List.filled(30, 0.0);
  final List<double> _ulHistory = List.filled(30, 0.0);

  final ValueNotifier<double> _ambientPhase = ValueNotifier<double>(0.0);
  Timer? _ambientPacer;

  int _selectedMonitorView = 0;

  @override
  void initState() {
    super.initState();
    final now = DateTime.now();
    final initialStatus = ref.read(tunnelStatusProvider).valueOrNull ?? const TunnelStatus();
    _prevDl = initialStatus.bytesReceived;
    _prevUl = initialStatus.bytesSent;
    _lastRateTick = now;
    _lastActiveDlTime = now;
    _lastActiveUlTime = now;

    if (initialStatus.state == ConnectionState.connected &&
        (initialStatus.downSpeed > 0 || initialStatus.upSpeed > 0)) {
      _startAmbientPacer();
    }

    _rateTicker = Timer.periodic(const Duration(milliseconds: 1000), (_) => _tickDataRate());
  }

  void _startAmbientPacer() {
    if (_ambientPacer != null) return;
    _ambientPacer = Timer.periodic(const Duration(milliseconds: 60), (_) {
      _ambientPhase.value = (_ambientPhase.value + 0.08) % (2 * math.pi);
    });
  }

  void _stopAmbientPacer() {
    _ambientPacer?.cancel();
    _ambientPacer = null;
  }

  void _tickDataRate() {
    if (!mounted) return;
    final status = ref.read(tunnelStatusProvider).valueOrNull ?? const TunnelStatus();
    if (status.state != ConnectionState.connected) {
      if (_dlRate != 0 || _ulRate != 0) {
        setState(() {
          _dlRate = 0;
          _ulRate = 0;
          _dlHistory.fillRange(0, _dlHistory.length, 0.0);
          _ulHistory.fillRange(0, _ulHistory.length, 0.0);
        });
      }
      return;
    }

    final now = DateTime.now();
    final curDl = status.bytesReceived;
    final curUl = status.bytesSent;
    final dt = now.difference(_lastRateTick).inMilliseconds / 1000.0;

    final bDown = status.downSpeed;
    final bUp = status.upSpeed;

    if (bDown > 0) {
      _dlRate = bDown;
      _lastActiveDlTime = now;
    } else if (dt >= 0.2) {
      final dlDelta = (curDl >= _prevDl) ? (curDl - _prevDl) : 0;
      if (dlDelta > 0) {
        final instDl = dlDelta / dt;
        _dlRate = (_dlRate <= 0) ? instDl : (_dlRate * 0.35 + instDl * 0.65);
        _lastActiveDlTime = now;
      } else {
        final dlSilenceMs = now.difference(_lastActiveDlTime).inMilliseconds;
        if (dlSilenceMs > 700) {
          _dlRate *= 0.50;
          if (_dlRate < 64) _dlRate = 0;
        }
      }
    }

    if (bUp > 0) {
      _ulRate = bUp;
      _lastActiveUlTime = now;
    } else if (dt >= 0.2) {
      final ulDelta = (curUl >= _prevUl) ? (curUl - _prevUl) : 0;
      if (ulDelta > 0) {
        final instUl = ulDelta / dt;
        _ulRate = (_ulRate <= 0) ? instUl : (_ulRate * 0.35 + instUl * 0.65);
        _lastActiveUlTime = now;
      } else {
        final ulSilenceMs = now.difference(_lastActiveUlTime).inMilliseconds;
        if (ulSilenceMs > 700) {
          _ulRate *= 0.50;
          if (_ulRate < 64) _ulRate = 0;
        }
      }
    }

    if (dt >= 0.2) {
      _prevDl = curDl;
      _prevUl = curUl;
      _lastRateTick = now;
    }

    if (_dlRate > _sessionPeakRate) _sessionPeakRate = _dlRate;
    if (_ulRate > _sessionPeakRate) _sessionPeakRate = _ulRate;

    setState(() {
      _dlHistory.removeAt(0);
      _dlHistory.add(_dlRate);
      _ulHistory.removeAt(0);
      _ulHistory.add(_ulRate);
    });
  }

  @override
  void dispose() {
    _stopAmbientPacer();
    _ambientPhase.dispose();
    _rateTicker?.cancel();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    ref.listen<AsyncValue<TunnelStatus>>(tunnelStatusProvider, (previous, next) {
      final oldStatus = previous?.valueOrNull;
      final newStatus = next.valueOrNull ?? const TunnelStatus();
      final now = DateTime.now();
      if (oldStatus?.state != newStatus.state) {
        if (newStatus.state == ConnectionState.connected) {
          _prevDl = newStatus.bytesReceived;
          _prevUl = newStatus.bytesSent;
          _lastRateTick = now;
          _lastActiveDlTime = now;
          _lastActiveUlTime = now;
          _dlRate = newStatus.downSpeed;
          _ulRate = newStatus.upSpeed;
          _sessionPeakRate = math.max(_dlRate, _ulRate);
          _dlHistory.fillRange(0, _dlHistory.length, 0.0);
          _ulHistory.fillRange(0, _ulHistory.length, 0.0);
          if (_dlRate > 0 || _ulRate > 0) {
            _startAmbientPacer();
          } else {
            _stopAmbientPacer();
          }
        } else {
          _stopAmbientPacer();
          _dlRate = 0;
          _ulRate = 0;
          _sessionPeakRate = 0;
          _dlHistory.fillRange(0, _dlHistory.length, 0.0);
          _ulHistory.fillRange(0, _ulHistory.length, 0.0);
        }
      } else if (newStatus.state == ConnectionState.connected) {
        final ds = newStatus.downSpeed;
        final us = newStatus.upSpeed;
        if (ds > 0) {
          _dlRate = ds;
          _lastActiveDlTime = now;
          if (_dlRate > _sessionPeakRate) _sessionPeakRate = _dlRate;
        }
        if (us > 0) {
          _ulRate = us;
          _lastActiveUlTime = now;
          if (_ulRate > _sessionPeakRate) _sessionPeakRate = _ulRate;
        }
        if (_dlRate > 0 || _ulRate > 0) {
          _startAmbientPacer();
        } else {
          _stopAmbientPacer();
        }
        if (_dlHistory.isNotEmpty) _dlHistory[_dlHistory.length - 1] = _dlRate;
        if (_ulHistory.isNotEmpty) _ulHistory[_ulHistory.length - 1] = _ulRate;
      }
    });

    final c = Theme.of(context).extension<AppColors>()!;
    final str = ref.watch(stringsProvider);
    final settings = ref.watch(settingsProvider);
    final method = ConnectionMethodX.parse(settings.connectionMethod);
    final panelStatus = ref.watch(tunnelStatusProvider.select((s) {
      final v = s.valueOrNull;
      return (
        state: v?.state ?? ConnectionState.disconnected,
        httpProxyPort: v?.httpProxyPort ?? 0,
        socksProxyPort: v?.socksProxyPort ?? 0,
        currentRouteIp: v?.currentRouteIp ?? '',
        currentRouteSni: v?.currentRouteSni ?? '',

        volumeBucket: ((v?.bytesReceived ?? 0) + (v?.bytesSent ?? 0)) ~/ (64 * 1024),
        bytesReceived: v?.bytesReceived ?? 0,
        bytesSent: v?.bytesSent ?? 0,
      );
    }));
    final isConnected = panelStatus.state == ConnectionState.connected;
    final http = panelStatus.httpProxyPort > 0 ? '127.0.0.1:${panelStatus.httpProxyPort}' : '127.0.0.1:—';
    final socks = panelStatus.socksProxyPort > 0 ? '127.0.0.1:${panelStatus.socksProxyPort}' : '127.0.0.1:—';

    var maxRate = 1024.0;
    for (final v in _dlHistory) {
      if (v > maxRate) maxRate = v;
    }
    for (final v in _ulHistory) {
      if (v > maxRate) maxRate = v;
    }

    return GlassCard(
      padding: const EdgeInsets.all(14),
      borderRadius: 20,
      clipBehavior: Clip.antiAlias,
      child: LayoutBuilder(
        builder: (context, constraints) {
          final availableH = constraints.maxHeight;
          final availableW = constraints.maxWidth;
          final isShort = availableH < 340;
          final isCompact = availableW < 440 || isShort;
          final isTall = availableH >= 520;

          final content = <Widget>[

            Row(
              children: [
                Container(
                  padding: const EdgeInsets.all(5),
                  decoration: BoxDecoration(
                    color: (isConnected ? BrandColors.emerald : BrandColors.accentCyan).withValues(alpha: 0.14),
                    borderRadius: BorderRadius.circular(8),
                    border: Border.all(
                      color: (isConnected ? BrandColors.emerald : BrandColors.accentCyan).withValues(alpha: 0.25),
                    ),
                  ),
                  child: Icon(
                    isConnected ? Icons.sensors_rounded : Icons.stream_rounded,
                    size: 14,
                    color: isConnected ? BrandColors.emerald : BrandColors.accentCyan,
                  ),
                ),
                const SizedBox(width: 8),
                Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    Text(
                      str.networkTelemetry.toUpperCase(),
                      style: TextStyle(
                        fontSize: 10.5,
                        fontWeight: FontWeight.w800,
                        letterSpacing: 1.1,
                        color: c.textPrimary,
                      ),
                    ),
                    Text(
                      isConnected ? 'Real-time speed + gateway' : 'Idle — waiting for connection',
                      style: TextStyle(fontSize: 8.5, color: c.textMuted),
                    ),
                  ],
                ),
                const Spacer(),

                Container(
                  padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 3.5),
                  decoration: BoxDecoration(
                    color: (isConnected ? BrandColors.emerald : BrandColors.accentCyan).withValues(alpha: 0.12),
                    borderRadius: BorderRadius.circular(20),
                    border: Border.all(
                      color: (isConnected ? BrandColors.emerald : BrandColors.accentCyan).withValues(alpha: 0.35),
                    ),
                  ),
                  child: Row(
                    mainAxisSize: MainAxisSize.min,
                    children: [
                      Container(
                        width: 5.5,
                        height: 5.5,
                        decoration: BoxDecoration(
                          shape: BoxShape.circle,
                          color: isConnected ? BrandColors.emerald : BrandColors.accentCyan,
                          boxShadow: isConnected
                              ? [BoxShadow(color: BrandColors.emerald.withValues(alpha: 0.6), blurRadius: 4)]
                              : null,
                        ),
                      ),
                      const SizedBox(width: 5),
                      Text(
                        isConnected ? 'LIVE DUPLEX' : 'STANDBY',
                        style: TextStyle(
                          fontSize: 8.5,
                          fontWeight: FontWeight.w800,
                          letterSpacing: 0.5,
                          color: isConnected ? BrandColors.emerald : c.textMuted,
                        ),
                      ),
                    ],
                  ),
                ),
              ],
            ),

            SizedBox(height: isTall ? 10 : 7),

            Row(
              children: [
                Expanded(
                  child: _SpeedGaugeCard(
                    title: str.download,
                    icon: Icons.arrow_downward_rounded,
                    accentColor: BrandColors.accentCyan,
                    speedRate: _dlRate,
                    totalBytes: panelStatus.bytesReceived,
                    isConnected: isConnected,
                  ),
                ),
                const SizedBox(width: 8),
                Expanded(
                  child: _SpeedGaugeCard(
                    title: str.upload,
                    icon: Icons.arrow_upward_rounded,
                    accentColor: BrandColors.primary,
                    speedRate: _ulRate,
                    totalBytes: panelStatus.bytesSent,
                    isConnected: isConnected,
                  ),
                ),
              ],
            ),

            SizedBox(height: isTall ? 10 : 7),

            if (!isShort)
              Expanded(
                child: _buildActivityStationContainer(c, isConnected, maxRate, method, const TunnelStatus()),
              )
            else
              SizedBox(
                height: 120,
                child: _buildActivityStationContainer(c, isConnected, maxRate, method, const TunnelStatus()),
              ),

            SizedBox(height: isTall ? 10 : 7),

            Row(
              children: [
                Expanded(
                    child: _DiagnosticTile(
                    icon: Icons.cable_rounded,
                    iconColor: BrandColors.accentCyan,
                    label: isCompact ? 'SOCKS5' : 'SOCKS5 PROXY',
                    value: socks,
                    badgeText: isCompact
                        ? null
                        : (panelStatus.socksProxyPort > 0 ? 'PORT READY' : 'OFF'),
                    badgeColor: panelStatus.socksProxyPort > 0 ? BrandColors.emerald : c.textMuted,
                    canCopy: panelStatus.socksProxyPort > 0,
                    isCompact: isCompact,
                  ),
                ),
                const SizedBox(width: 6),
                Expanded(
                    child: _DiagnosticTile(
                    icon: Icons.http_rounded,
                    iconColor: BrandColors.accentCyan,
                    label: isCompact ? 'HTTP' : 'HTTP PROXY',
                    value: http,
                    badgeText: isCompact
                        ? null
                        : (panelStatus.httpProxyPort > 0 ? 'PORT READY' : 'OFF'),
                    badgeColor: panelStatus.httpProxyPort > 0 ? BrandColors.emerald : c.textMuted,
                    canCopy: panelStatus.httpProxyPort > 0,
                    isCompact: isCompact,
                  ),
                ),
              ],
            ),

            const SizedBox(height: 5),

            Row(
              children: [
                Expanded(
                  child: _DiagnosticTile(
                    icon: Icons.public_rounded,
                    iconColor: BrandColors.emerald,
                    label: isCompact ? 'GATEWAY' : 'ROUTE GATEWAY',
                    value: panelStatus.currentRouteIp.isNotEmpty
                        ? panelStatus.currentRouteIp
                        : (isConnected ? 'POP Mesh' : 'Mesh Standby'),
                    badgeText: isCompact ? null : (isConnected ? 'CONNECTED' : 'STANDBY'),
                    badgeColor: isConnected ? BrandColors.emerald : c.textMuted,
                    canCopy: panelStatus.currentRouteIp.isNotEmpty,
                    isCompact: isCompact,
                  ),
                ),
                const SizedBox(width: 6),
                Expanded(
                  child: _DiagnosticTile(
                    icon: Icons.security_rounded,
                    iconColor: BrandColors.primary,
                    label: isCompact ? 'CIPHER' : 'CIPHER & PROTOCOL',
                    value: panelStatus.currentRouteSni.isNotEmpty
                        ? panelStatus.currentRouteSni
                        : (isConnected ? method.displayName : 'Cipher Ready'),
                    badgeText: isCompact ? null : (isConnected ? 'ENCRYPTED' : 'IDLE'),
                    badgeColor: isConnected ? BrandColors.primary : c.textMuted,
                    canCopy: false,
                    isCompact: isCompact,
                  ),
                ),
              ],
            ),

            SizedBox(height: isTall ? 10 : 7),

              Container(
              padding: const EdgeInsets.symmetric(horizontal: 11, vertical: 7),
              decoration: BoxDecoration(
                color: c.input.withValues(alpha: 0.45),
                borderRadius: BorderRadius.circular(10),
                border: Border.all(color: c.border.withValues(alpha: 0.35)),
              ),
              child: Row(
                children: [
                  const Icon(Icons.donut_large_rounded, size: 12, color: BrandColors.emerald),
                  const SizedBox(width: 7),
                  Text(str.totalTraffic, style: TextStyle(fontSize: 9.5, fontWeight: FontWeight.w700, color: c.textSecondary)),
                  const Spacer(),
                  Text(formatBytes(panelStatus.bytesReceived + panelStatus.bytesSent), style: AppTheme.mono(BrandColors.emerald, size: 11, weight: FontWeight.w700)),
                ],
              ),
            ),
          ];

          if (isShort) {
            return SingleChildScrollView(
              physics: const BouncingScrollPhysics(),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                mainAxisSize: MainAxisSize.min,
                children: content,
              ),
            );
          }

          return Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: content,
          );
        },
      ),
    );
  }

  Widget _buildActivityStationContainer(
    AppColors c,
    bool isConnected,
    double maxRate,
    ConnectionMethod method,
    TunnelStatus status,
  ) {
    String modeTitle;
    switch (_selectedMonitorView) {
      case 1:
        modeTitle = isConnected ? 'NEON SPARKLINE' : 'STANDBY SPARKLINE';
        break;
      case 0:
      default:
        modeTitle = isConnected ? 'EQUALIZER SPECTRUM' : 'STANDBY SPECTRUM';
        break;
    }

    return Container(
      decoration: BoxDecoration(
        color: c.input.withValues(alpha: 0.35),
        borderRadius: BorderRadius.circular(12),
        border: Border.all(color: c.border.withValues(alpha: 0.3)),
      ),
      child: ClipRRect(
        borderRadius: BorderRadius.circular(12),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [

            Container(
              height: 26,
              padding: const EdgeInsets.symmetric(horizontal: 9),
              decoration: BoxDecoration(
                color: c.input.withValues(alpha: 0.25),
                border: Border(bottom: BorderSide(color: c.border.withValues(alpha: 0.2))),
              ),
              child: Row(
                children: [
                  Container(
                    width: 5,
                    height: 5,
                    decoration: BoxDecoration(
                      shape: BoxShape.circle,
                      color: isConnected ? BrandColors.emerald : c.textMuted.withValues(alpha: 0.6),
                      boxShadow: isConnected
                          ? [BoxShadow(color: BrandColors.emerald.withValues(alpha: 0.7), blurRadius: 4)]
                          : null,
                    ),
                  ),
                  const SizedBox(width: 5),
                  Text(
                    modeTitle,
                    style: TextStyle(
                      fontSize: 8,
                      fontWeight: FontWeight.w800,
                      letterSpacing: 0.6,
                      color: isConnected ? c.textPrimary : c.textMuted,
                    ),
                  ),

                  const Spacer(),
                  if (isConnected)
                    Padding(
                      padding: const EdgeInsets.only(right: 8),
                      child: Text(
                        'PEAK: ${_sessionPeakRate > 0 ? formatSpeed(_sessionPeakRate.toInt()) : "0.0 KB/s"}',
                        style: const TextStyle(
                          fontSize: 8,
                          fontWeight: FontWeight.w700,
                          color: BrandColors.accentCyan,
                          fontFamily: 'JetBrains Mono',
                        ),
                      ),
                    ),

                  Row(
                    mainAxisSize: MainAxisSize.min,
                    children: [
                      _buildModeButton(0, Icons.bar_chart_rounded, 'Equalizer Spectrum', c),
                      const SizedBox(width: 2.5),
                      _buildModeButton(1, Icons.show_chart_rounded, 'Neon Sparkline', c),
                    ],
                  ),
                ],
              ),
            ),

            Expanded(
              child: AnimatedSwitcher(
                duration: const Duration(milliseconds: 200),
                child: _buildStationContent(c, isConnected, maxRate, method, status),
              ),
            ),
          ],
        ),
      ),
    );
  }

  Widget _buildModeButton(int index, IconData icon, String tooltip, AppColors c) {
    final isSelected = _selectedMonitorView == index;
    return Tooltip(
      message: tooltip,
      child: InkWell(
        onTap: () => setState(() => _selectedMonitorView = index),
        borderRadius: BorderRadius.circular(4),
        child: Container(
          padding: const EdgeInsets.all(2.5),
          decoration: BoxDecoration(
            color: isSelected
                ? BrandColors.accentCyan.withValues(alpha: 0.18)
                : Colors.transparent,
            borderRadius: BorderRadius.circular(4),
            border: Border.all(
              color: isSelected
                  ? BrandColors.accentCyan.withValues(alpha: 0.5)
                  : Colors.transparent,
            ),
          ),
          child: Icon(
            icon,
            size: 11,
            color: isSelected ? BrandColors.accentCyan : c.textMuted.withValues(alpha: 0.7),
          ),
        ),
      ),
    );
  }

  Widget _buildStationContent(
    AppColors c,
    bool isConnected,
    double maxRate,
    ConnectionMethod method,
    TunnelStatus status,
  ) {
    if (_selectedMonitorView == 1) {
      return RepaintBoundary(
        child: ValueListenableBuilder<double>(
          key: const ValueKey('view_sparkline'),
          valueListenable: _ambientPhase,
          builder: (context, phase, _) {
            return CustomPaint(
              size: Size.infinite,
              painter: _NeonSparklinePainter(
                dlHistory: _dlHistory,
                ulHistory: _ulHistory,
                dlRate: _dlRate,
                ulRate: _ulRate,
                peakRate: _sessionPeakRate,
                isConnected: isConnected,
                ambientPhase: phase,
                accentCyan: BrandColors.accentCyan,
                accentPurple: BrandColors.primary,
                gridColor: c.border.withValues(alpha: 0.18),
                textMuted: c.textMuted,
              ),
            );
          },
        ),
      );
    }

    return RepaintBoundary(
      child: ValueListenableBuilder<double>(
        key: const ValueKey('view_equalizer'),
        valueListenable: _ambientPhase,
        builder: (context, phase, _) {
          return CustomPaint(
            size: Size.infinite,
            painter: _EqualizerSpectrumPainter(
              dlHistory: _dlHistory,
              ulHistory: _ulHistory,
              dlRate: _dlRate,
              ulRate: _ulRate,
              peakRate: _sessionPeakRate,
              isConnected: isConnected,
              ambientPhase: phase,
              accentCyan: BrandColors.accentCyan,
              accentPurple: BrandColors.primary,
              gridColor: c.border.withValues(alpha: 0.18),
              textMuted: c.textMuted,
            ),
          );
        },
      ),
    );
  }
}

class _SpeedGaugeCard extends StatelessWidget {
  const _SpeedGaugeCard({
    required this.title,
    required this.icon,
    required this.accentColor,
    required this.speedRate,
    required this.totalBytes,
    required this.isConnected,
  });

  final String title;
  final IconData icon;
  final Color accentColor;
  final double speedRate;
  final int totalBytes;
  final bool isConnected;

  @override
  Widget build(BuildContext context) {
    final c = Theme.of(context).extension<AppColors>()!;
    final safeIntSpeed = (speedRate.isFinite && !speedRate.isNegative) ? speedRate.toInt() : 0;
    final hasSpeed = isConnected && safeIntSpeed > 0;

    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 8),
      decoration: BoxDecoration(
        gradient: LinearGradient(
          begin: Alignment.topLeft,
          end: Alignment.bottomRight,
          colors: [
            accentColor.withValues(alpha: 0.10),
            accentColor.withValues(alpha: 0.02),
          ],
        ),
        borderRadius: BorderRadius.circular(12),
        border: Border.all(
          color: accentColor.withValues(alpha: 0.22),
        ),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        mainAxisSize: MainAxisSize.min,
        children: [
          Row(
            children: [
              Container(
                padding: const EdgeInsets.all(3.5),
                decoration: BoxDecoration(
                  color: accentColor.withValues(alpha: 0.16),
                  borderRadius: BorderRadius.circular(6),
                ),
                child: Icon(icon, size: 12, color: accentColor),
              ),
              const SizedBox(width: 6),
              Text(
                title.toUpperCase(),
                style: TextStyle(
                  fontSize: 9,
                  fontWeight: FontWeight.w800,
                  letterSpacing: 0.6,
                  color: c.textMuted,
                ),
              ),
            ],
          ),
          const SizedBox(height: 5),
          Row(
            crossAxisAlignment: CrossAxisAlignment.baseline,
            textBaseline: TextBaseline.alphabetic,
            children: [
              Text(
                hasSpeed ? formatSpeed(safeIntSpeed) : '0.0 KB/s',
                style: AppTheme.mono(
                  hasSpeed ? accentColor : c.textPrimary,
                  size: 14.5,
                  weight: FontWeight.w800,
                ),
              ),
            ],
          ),
          const SizedBox(height: 3),
          Row(
            children: [
              Icon(Icons.data_usage_rounded, size: 10, color: c.textMuted),
              const SizedBox(width: 4),
              Expanded(
                child: Text(
                  'Total: ${formatBytes(totalBytes)}',
                  style: AppTheme.mono(c.textSecondary, size: 9.5, weight: FontWeight.w600),
                  overflow: TextOverflow.ellipsis,
                ),
              ),
            ],
          ),
        ],
      ),
    );
  }
}

class _DiagnosticTile extends ConsumerStatefulWidget {
  const _DiagnosticTile({
    required this.icon,
    required this.iconColor,
    required this.label,
    required this.value,
    this.badgeText,
    this.badgeColor,
    this.canCopy = false,
    this.isCompact = false,
  });

  final IconData icon;
  final Color iconColor;
  final String label;
  final String value;
  final String? badgeText;
  final Color? badgeColor;
  final bool canCopy;
  final bool isCompact;

  @override
  ConsumerState<_DiagnosticTile> createState() => _DiagnosticTileState();
}

class _DiagnosticTileState extends ConsumerState<_DiagnosticTile> {
  bool _isHovered = false;

  @override
  Widget build(BuildContext context) {
    final c = Theme.of(context).extension<AppColors>()!;
    final isAvailable = widget.value != '—' && !widget.value.contains('—');
    final isCompact = widget.isCompact;

    return MouseRegion(
      onEnter: (_) => setState(() => _isHovered = true),
      onExit: (_) => setState(() => _isHovered = false),
      child: AnimatedContainer(
        duration: const Duration(milliseconds: 140),
        padding: EdgeInsets.symmetric(
          horizontal: isCompact ? 7 : 9,
          vertical: isCompact ? 5 : 6.5,
        ),
        decoration: BoxDecoration(
          color: _isHovered ? c.cardElevated.withValues(alpha: 0.65) : c.input.withValues(alpha: 0.45),
          borderRadius: BorderRadius.circular(9),
          border: Border.all(
            color: _isHovered ? widget.iconColor.withValues(alpha: 0.45) : c.border.withValues(alpha: 0.35),
          ),
        ),
        child: Row(
          children: [
            Container(
              padding: EdgeInsets.all(isCompact ? 3.5 : 4),
              decoration: BoxDecoration(
                color: widget.iconColor.withValues(alpha: 0.12),
                borderRadius: BorderRadius.circular(6),
              ),
              child: Icon(widget.icon, size: isCompact ? 11 : 12, color: widget.iconColor),
            ),
            SizedBox(width: isCompact ? 5 : 7),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                mainAxisAlignment: MainAxisAlignment.center,
                mainAxisSize: MainAxisSize.min,
                children: [
                  Row(
                    children: [
                      Flexible(
                        child: Text(
                          widget.label,
                          style: TextStyle(
                            fontSize: isCompact ? 7.8 : 8.5,
                            fontWeight: FontWeight.w800,
                            letterSpacing: isCompact ? 0.3 : 0.4,
                            color: c.textMuted,
                          ),
                          maxLines: 1,
                          overflow: TextOverflow.ellipsis,
                        ),
                      ),
                      if (isCompact && widget.badgeColor != null) ...[
                        const SizedBox(width: 4),
                        Container(
                          width: 5.5,
                          height: 5.5,
                          decoration: BoxDecoration(
                            shape: BoxShape.circle,
                            color: widget.badgeColor,
                            boxShadow: [
                              BoxShadow(
                                color: widget.badgeColor!.withValues(alpha: 0.6),
                                blurRadius: 4,
                              ),
                            ],
                          ),
                        ),
                      ] else if (widget.badgeText != null) ...[
                        const SizedBox(width: 4),
                        Container(
                          padding: const EdgeInsets.symmetric(horizontal: 3.5, vertical: 1),
                          decoration: BoxDecoration(
                            color: (widget.badgeColor ?? widget.iconColor).withValues(alpha: 0.15),
                            borderRadius: BorderRadius.circular(4),
                          ),
                          child: Text(
                            widget.badgeText!,
                            style: TextStyle(
                              fontSize: 7,
                              fontWeight: FontWeight.w800,
                              color: widget.badgeColor ?? widget.iconColor,
                            ),
                          ),
                        ),
                      ],
                    ],
                  ),
                  SizedBox(height: isCompact ? 1.5 : 2),
                  Text(
                    widget.value,
                    style: AppTheme.mono(
                      isAvailable ? c.textPrimary : c.textMuted,
                      size: isCompact ? 9.2 : 10,
                      weight: FontWeight.w600,
                    ),
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                  ),
                ],
              ),
            ),
            if (widget.canCopy && isAvailable)
              Tooltip(
                message: 'Copy to clipboard',
                child: MouseRegion(
                  cursor: SystemMouseCursors.click,
                  child: GestureDetector(
                    onTap: () {
                      Clipboard.setData(ClipboardData(text: widget.value));
                      final msg = ref.read(stringsProvider).copiedToClipboard('${widget.label}: ${widget.value}');
                      ScaffoldMessenger.of(context).showSnackBar(
                        SnackBar(
                          content: Text(msg),
                          duration: const Duration(seconds: 2),
                          behavior: SnackBarBehavior.floating,
                        ),
                      );
                    },
                    child: Padding(
                      padding: const EdgeInsets.all(2),
                      child: Icon(
                        Icons.copy_rounded,
                        size: isCompact ? 10.5 : 11,
                        color: _isHovered ? widget.iconColor : c.textMuted,
                      ),
                    ),
                  ),
                ),
              ),
          ],
        ),
      ),
    );
  }
}

class _EqualizerSpectrumPainter extends CustomPainter {
  _EqualizerSpectrumPainter({
    required this.dlHistory,
    required this.ulHistory,
    required this.dlRate,
    required this.ulRate,
    required this.peakRate,
    required this.isConnected,
    required this.ambientPhase,
    required this.accentCyan,
    required this.accentPurple,
    required this.gridColor,
    required this.textMuted,
  });

  static final Paint _whiteDotPaint = Paint()..color = Colors.white;

  final List<double> dlHistory;
  final List<double> ulHistory;
  final double dlRate;
  final double ulRate;
  final double peakRate;
  final bool isConnected;
  final double ambientPhase;
  final Color accentCyan;
  final Color accentPurple;
  final Color gridColor;
  final Color textMuted;

  @override
  void paint(Canvas canvas, Size size) {
    final w = size.width;
    final h = size.height;
    if (w <= 0 || h <= 0) return;

    final gridPaint = Paint()
      ..color = gridColor
      ..strokeWidth = 1.0
      ..style = PaintingStyle.stroke;

    canvas.drawLine(Offset(0, h * 0.33), Offset(w, h * 0.33), gridPaint);
    canvas.drawLine(Offset(0, h * 0.66), Offset(w, h * 0.66), gridPaint);

    final n = dlHistory.length;
    if (n < 2) return;

    if (!isConnected) {
      _paintStandbyEqualizer(canvas, size, n);
      return;
    }

    _paintLiveEqualizer(canvas, size, n);
  }

  void _paintStandbyEqualizer(Canvas canvas, Size size, int n) {
    final w = size.width;
    final h = size.height;
    const padX = 10.0;
    final slotW = (w - padX * 2) / n;
    final barW = (slotW * 0.65).clamp(2.5, 9.0);

    final bgPaint = Paint()
      ..shader = LinearGradient(
        begin: Alignment.topCenter,
        end: Alignment.bottomCenter,
        colors: [
          accentCyan.withValues(alpha: 0.32),
          accentPurple.withValues(alpha: 0.10),
        ],
      ).createShader(Rect.fromLTWH(0, 0, w, h));

    for (var i = 0; i < n; i++) {
      final x = padX + i * slotW + (slotW - barW) / 2;
      final wave = math.sin((i / n) * 3 * math.pi + ambientPhase) * 0.5 + 0.5;
      final barH = (h * 0.10) + wave * (h * 0.22);

      final rect = Rect.fromLTWH(x, h - barH - 3, barW, barH);
      final rrect = RRect.fromRectAndRadius(rect, const Radius.circular(2.5));
      canvas.drawRRect(rrect, bgPaint);
    }
  }

  void _paintLiveEqualizer(Canvas canvas, Size size, int n) {
    final w = size.width;
    final h = size.height;
    const padX = 10.0;
    final slotW = (w - padX * 2) / n;
    final usableH = h - 12;

    final effectivePeak = math.max(peakRate, math.max(dlRate, math.max(ulRate, 1024.0 * 256)));
    final dlRatio = (dlRate / effectivePeak).clamp(0.0, 1.0);
    final ulRatio = (ulRate / effectivePeak).clamp(0.0, 1.0);

    final dlSpeedFactor = dlRate <= 0 ? 0.0 : math.pow(dlRatio, 0.35).toDouble();
    final ulSpeedFactor = ulRate <= 0 ? 0.0 : math.pow(ulRatio, 0.35).toDouble();

    final barTotalW = (slotW * 0.78).clamp(3.0, 12.0);
    final subBarW = ((barTotalW - 1.2) / 2).clamp(1.2, 5.5);

    final dlPaint = Paint()
      ..shader = LinearGradient(
        begin: Alignment.topCenter,
        end: Alignment.bottomCenter,
        colors: [
          accentCyan,
          accentCyan.withValues(alpha: 0.35),
        ],
      ).createShader(Rect.fromLTWH(0, 0, w, h));

    final ulPaint = Paint()
      ..shader = LinearGradient(
        begin: Alignment.topCenter,
        end: Alignment.bottomCenter,
        colors: [
          accentPurple,
          accentPurple.withValues(alpha: 0.35),
        ],
      ).createShader(Rect.fromLTWH(0, 0, w, h));

    for (var i = 0; i < n; i++) {
      final slotStartX = padX + i * slotW + (slotW - barTotalW) / 2;
      final normX = (i + 0.5) / n;
      final bell = math.sin(normX * math.pi);

      final h1 = math.sin(ambientPhase * 3.6 + i * 0.45);
      final h2 = math.cos(ambientPhase * 2.2 - i * 0.70);
      final h3 = math.sin(ambientPhase * 5.5 + i * 1.15);
      final dlHarmonic = (0.50 + 0.30 * h1 + 0.12 * h2 + 0.08 * h3).clamp(0.20, 1.0);

      final uh1 = math.sin(-ambientPhase * 3.2 + i * 0.50);
      final uh2 = math.cos(ambientPhase * 1.9 + i * 0.60);
      final ulHarmonic = (0.50 + 0.30 * uh1 + 0.15 * uh2).clamp(0.20, 1.0);

      final histRatio = (i < dlHistory.length && dlHistory[i] > 0)
          ? (dlHistory[i] / effectivePeak).clamp(0.0, 1.0)
          : 0.0;
      final histBonus = math.pow(histRatio, 0.45) * 0.12;

      final dlNorm = dlRate > 0
          ? (dlSpeedFactor * (0.28 + 0.65 * bell * dlHarmonic) + histBonus).clamp(0.08, 0.96)
          : (0.035 + 0.025 * math.sin(ambientPhase * 2.0 + i * 0.4).abs());

      final ulNorm = ulRate > 0
          ? (ulSpeedFactor * (0.28 + 0.65 * bell * ulHarmonic)).clamp(0.08, 0.96)
          : (0.035 + 0.025 * math.sin(-ambientPhase * 2.0 + i * 0.4).abs());

      final dlH = dlNorm * usableH;
      final ulH = ulNorm * usableH;

      final dlX = slotStartX;
      final dlRect = Rect.fromLTWH(dlX, h - dlH - 3, subBarW, dlH);
      final dlRRect = RRect.fromRectAndRadius(dlRect, const Radius.circular(2.0));
      canvas.drawRRect(dlRRect, dlPaint);

      final ulX = slotStartX + subBarW + 1.2;
      final ulRect = Rect.fromLTWH(ulX, h - ulH - 3, subBarW, ulH);
      final ulRRect = RRect.fromRectAndRadius(ulRect, const Radius.circular(2.0));
      canvas.drawRRect(ulRRect, ulPaint);

      if (dlH > usableH * 0.35) {
        canvas.drawCircle(
          Offset(dlX + subBarW / 2, h - dlH - 5.5),
          1.5,
          _whiteDotPaint,
        );
      }
      if (ulH > usableH * 0.35) {
        canvas.drawCircle(
          Offset(ulX + subBarW / 2, h - ulH - 5.5),
          1.5,
          _whiteDotPaint,
        );
      }
    }
  }

  @override
  bool shouldRepaint(covariant _EqualizerSpectrumPainter old) {
    return ambientPhase != old.ambientPhase ||
        dlRate != old.dlRate ||
        ulRate != old.ulRate ||
        isConnected != old.isConnected ||
        peakRate != old.peakRate;
  }
}

class _NeonSparklinePainter extends CustomPainter {
  _NeonSparklinePainter({
    required this.dlHistory,
    required this.ulHistory,
    required this.dlRate,
    required this.ulRate,
    required this.peakRate,
    required this.isConnected,
    required this.ambientPhase,
    required this.accentCyan,
    required this.accentPurple,
    required this.gridColor,
    required this.textMuted,
  });

  final List<double> dlHistory;
  final List<double> ulHistory;
  final double dlRate;
  final double ulRate;
  final double peakRate;
  final bool isConnected;
  final double ambientPhase;
  final Color accentCyan;
  final Color accentPurple;
  final Color gridColor;
  final Color textMuted;

  @override
  void paint(Canvas canvas, Size size) {
    final w = size.width;
    final h = size.height;
    if (w <= 0 || h <= 0) return;

    final gridPaint = Paint()
      ..color = gridColor
      ..strokeWidth = 1.0
      ..style = PaintingStyle.stroke;

    canvas.drawLine(Offset(0, h * 0.33), Offset(w, h * 0.33), gridPaint);
    canvas.drawLine(Offset(0, h * 0.66), Offset(w, h * 0.66), gridPaint);

    var maxRate = math.max(peakRate, math.max(dlRate, ulRate));
    for (final v in dlHistory) {
      if (v > maxRate) maxRate = v;
    }
    for (final v in ulHistory) {
      if (v > maxRate) maxRate = v;
    }
    if (maxRate < 1024.0) maxRate = 1024.0;

    if (isConnected && maxRate > 1024) {
      _drawGridLabel(canvas, formatSpeed((maxRate * 0.66).toInt()), h * 0.33, w);
      _drawGridLabel(canvas, formatSpeed((maxRate * 0.33).toInt()), h * 0.66, w);
    }

    _paintCurve(canvas, size, dlHistory, maxRate, accentCyan, isDownload: true);
    _paintCurve(canvas, size, ulHistory, maxRate, accentPurple, isDownload: false);
  }

  static final Map<String, TextPainter> _labelCache = {};

  void _drawGridLabel(Canvas canvas, String text, double y, double w) {
    var tp = _labelCache[text];
    if (tp == null) {
      if (_labelCache.length > 60) _labelCache.clear();
      tp = TextPainter(
        text: TextSpan(
          text: text,
          style: TextStyle(
            fontSize: 8.0,
            fontWeight: FontWeight.w600,
            color: textMuted.withValues(alpha: 0.60),
            fontFamily: 'JetBrains Mono',
          ),
        ),
        textDirection: TextDirection.ltr,
      )..layout();
      _labelCache[text] = tp;
    }
    tp.paint(canvas, Offset(w - tp.width - 6, y - tp.height - 2));
  }

  void _paintCurve(
    Canvas canvas,
    Size size,
    List<double> data,
    double maxVal,
    Color color, {
    required bool isDownload,
  }) {
    final w = size.width;
    final h = size.height;
    final n = data.length;
    if (n < 2) return;

    final points = <Offset>[];
    for (var i = 0; i < n; i++) {
      final x = (i / (n - 1)) * w;
      final val = data[i];

      final ratio = (val / maxVal).clamp(0.0, 1.0);
      final powerScaled = math.pow(ratio, 0.45).toDouble();

      final carrierPhase = (i / (n - 1)) * 3 * math.pi + (isDownload ? ambientPhase : -ambientPhase);
      final baselinePulse = 0.06 + 0.03 * math.sin(carrierPhase);

      final normalized = val > 0
          ? (powerScaled * 0.88 + 0.06).clamp(0.06, 0.95)
          : (baselinePulse).clamp(0.03, 0.12);
      final y = h - (normalized * (h * 0.78)) - 4;
      points.add(Offset(x, y));
    }

    final path = Path();
    path.moveTo(points[0].dx, points[0].dy);

    for (var i = 0; i < points.length - 1; i++) {
      final p0 = points[i];
      final p1 = points[i + 1];
      final cpx = (p0.dx + p1.dx) / 2;
      path.cubicTo(cpx, p0.dy, cpx, p1.dy, p1.dx, p1.dy);
    }

    final fillPath = Path.from(path)
      ..lineTo(w, h)
      ..lineTo(0, h)
      ..close();

    final fillPaint = Paint()
      ..shader = LinearGradient(
        begin: Alignment.topCenter,
        end: Alignment.bottomCenter,
        colors: [
          color.withValues(alpha: isDownload ? 0.22 : 0.16),
          color.withValues(alpha: 0.0),
        ],
      ).createShader(Rect.fromLTWH(0, 0, w, h))
      ..style = PaintingStyle.fill;

    canvas.drawPath(fillPath, fillPaint);

    final strokePaint = Paint()
      ..color = color
      ..strokeWidth = isDownload ? 1.8 : 1.5
      ..style = PaintingStyle.stroke;

    canvas.drawPath(path, strokePaint);

    final last = points.last;
    canvas.drawCircle(
      last,
      5.0,
      Paint()..color = color.withValues(alpha: 0.25),
    );
    canvas.drawCircle(
      last,
      3.0,
      Paint()..color = color.withValues(alpha: 0.65),
    );
    canvas.drawCircle(
      last,
      1.8,
      _EqualizerSpectrumPainter._whiteDotPaint,
    );
  }

  @override
  bool shouldRepaint(covariant _NeonSparklinePainter old) {
    return ambientPhase != old.ambientPhase ||
        dlRate != old.dlRate ||
        ulRate != old.ulRate ||
        isConnected != old.isConnected ||
        peakRate != old.peakRate;
  }
}

