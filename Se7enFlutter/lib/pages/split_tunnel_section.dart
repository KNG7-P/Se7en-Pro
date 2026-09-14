import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../core/i18n/app_strings.dart';
import '../core/ipc/pipe_core_client.dart';
import '../core/models/user_settings.dart';
import '../core/services/providers.dart';
import '../theme/app_colors.dart';
import '../theme/app_theme.dart';
import '../widgets/admin_elevation_dialog.dart';
import '../widgets/glass_controls.dart';

class KnownApp {
  const KnownApp({
    required this.name,
    required this.exe,
    required this.category,
    required this.icon,
    this.iconBase64,
  });

  final String name;
  final String exe;
  final String category;
  final IconData icon;
  final String? iconBase64;
}

const kPopularApps = <KnownApp>[
  KnownApp(name: 'Google Chrome', exe: 'chrome.exe', category: 'Browser', icon: Icons.travel_explore_rounded),
  KnownApp(name: 'Telegram Desktop', exe: 'Telegram.exe', category: 'Messaging', icon: Icons.send_rounded),
  KnownApp(name: 'Discord', exe: 'Discord.exe', category: 'Social', icon: Icons.forum_rounded),
  KnownApp(name: 'Spotify', exe: 'Spotify.exe', category: 'Music & Audio', icon: Icons.music_note_rounded),
  KnownApp(name: 'Steam', exe: 'steam.exe', category: 'Gaming', icon: Icons.sports_esports_rounded),
  KnownApp(name: 'Microsoft Edge', exe: 'msedge.exe', category: 'Browser', icon: Icons.public_rounded),
  KnownApp(name: 'Mozilla Firefox', exe: 'firefox.exe', category: 'Browser', icon: Icons.explore_rounded),
  KnownApp(name: 'Visual Studio Code', exe: 'Code.exe', category: 'Developer', icon: Icons.code_rounded),
  KnownApp(name: 'OBS Studio', exe: 'obs64.exe', category: 'Streaming', icon: Icons.videocam_rounded),
  KnownApp(name: 'Internet Download Manager', exe: 'IDMan.exe', category: 'Utility', icon: Icons.download_rounded),
  KnownApp(name: 'Epic Games Launcher', exe: 'EpicGamesLauncher.exe', category: 'Gaming', icon: Icons.games_rounded),
  KnownApp(name: 'Brave Browser', exe: 'brave.exe', category: 'Browser', icon: Icons.shield_rounded),
  KnownApp(name: 'AnyDesk', exe: 'AnyDesk.exe', category: 'Remote Desktop', icon: Icons.desktop_windows_rounded),
  KnownApp(name: 'Slack', exe: 'slack.exe', category: 'Productivity', icon: Icons.chat_bubble_rounded),
  KnownApp(name: 'Zoom Workplace', exe: 'Zoom.exe', category: 'Meetings', icon: Icons.video_call_rounded),
  KnownApp(name: 'Notion', exe: 'Notion.exe', category: 'Productivity', icon: Icons.notes_rounded),
  KnownApp(name: 'WhatsApp', exe: 'WhatsApp.exe', category: 'Messaging', icon: Icons.chat_rounded),
  KnownApp(name: 'BitTorrent / uTorrent', exe: 'uTorrent.exe', category: 'P2P Transfer', icon: Icons.cloud_download_rounded),
];

class SplitTunnelSection extends ConsumerStatefulWidget {
  const SplitTunnelSection({super.key});

  @override
  ConsumerState<SplitTunnelSection> createState() => _SplitTunnelSectionState();
}

class _SplitTunnelSectionState extends ConsumerState<SplitTunnelSection> {
  int _activeTab = 0;

  @override
  Widget build(BuildContext context) {
    final s = ref.watch(settingsProvider);
    final str = ref.watch(stringsProvider);
    final c = Theme.of(context).extension<AppColors>()!;
    void set(void Function(UserSettings) edit) =>
        ref.read(settingsProvider.notifier).update(edit);

    final domainEntries = s.splitTunnelEntries.where((e) => e.kind != 'app').toList();
    final appEntries = s.splitTunnelEntries.where((e) => e.kind == 'app').toList();

    final status = ref.watch(tunnelStatusProvider).valueOrNull;
    final isAdmin = status?.isAdmin ?? false;

    void requestAdmin() {
      showDialog(
        context: context,
        barrierDismissible: true,
        builder: (ctx) => const AdminElevationDialog(),
      );
    }

    return Column(
      children: [

        if (s.splitTunnelEnabled && !isAdmin) ...[
          Container(
            padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 10),
            decoration: BoxDecoration(
              color: BrandColors.warning.withValues(alpha: 0.12),
              borderRadius: BorderRadius.circular(12),
              border: Border.all(color: BrandColors.warning.withValues(alpha: 0.35)),
            ),
            child: Row(
              children: [
                const Icon(Icons.lock_rounded, size: 18, color: BrandColors.warning),
                const SizedBox(width: 10),
                Expanded(
                  child: Text(
                    str.splitAdminPrivilegeRequired,
                    style: const TextStyle(fontSize: 12, fontWeight: FontWeight.w600, color: BrandColors.warning),
                  ),
                ),
                TextButton.icon(
                  onPressed: requestAdmin,
                  icon: const Icon(Icons.shield_rounded, size: 14, color: BrandColors.warning),
                  label: Text(
                    str.splitElevateToAdmin,
                    style: const TextStyle(fontSize: 11.5, fontWeight: FontWeight.w700, color: BrandColors.warning),
                  ),
                  style: TextButton.styleFrom(
                    padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 6),
                    backgroundColor: BrandColors.warning.withValues(alpha: 0.18),
                    shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(8)),
                  ),
                ),
              ],
            ),
          ),
          const SizedBox(height: 12),
        ],

        GlassCard(
          borderRadius: 14,
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Row(
                children: [
                  const Icon(Icons.call_split_rounded, size: 16, color: BrandColors.accentCyan),
                  const SizedBox(width: 8),
                  Text(
                    str.splitConfigTitle,
                    style: TextStyle(
                      fontSize: 13,
                      fontWeight: FontWeight.w700,
                      color: c.textPrimary,
                    ),
                  ),
                ],
              ),
              const SizedBox(height: 8),
              GlassSwitchRow(
                label: str.splitEnableLabel,
                description: str.splitEnableDesc,
                value: s.splitTunnelEnabled,
                onChanged: (v) {
                  if (v && !isAdmin) {
                    showDialog(
                      context: context,
                      barrierDismissible: true,
                      builder: (ctx) => AdminElevationDialog(
                        onConfirm: () {
                          set((x) {
                            x.splitTunnelEnabled = true;
                            x.systemWideTunneling = true;
                          });
                        },
                      ),
                    );
                  } else {
                    set((x) => x.splitTunnelEnabled = v);
                  }
                },
              ),
              GlassDropdownRow<String>(
                label: str.splitRoutingPolicy,
                description: str.splitRoutingPolicyDesc,
                value: s.splitTunnelMode,
                items: {
                  'exclude': str.splitModeBypassDetailed,
                  'include': str.splitModeRouteDetailed,
                },
                onChanged: (v) {
                  if (!isAdmin) {
                    requestAdmin();
                  } else {
                    set((x) => x.splitTunnelMode = v);
                  }
                },
              ),
            ],
          ),
        ),

        const SizedBox(height: 14),

        Row(
          children: [
            Expanded(
              child: _SplitTabButton(
                icon: Icons.language_rounded,
                label: str.splitTabWebsites,
                count: domainEntries.length,
                isSelected: _activeTab == 0,
                onTap: () => setState(() => _activeTab = 0),
              ),
            ),
            const SizedBox(width: 10),
            Expanded(
              child: _SplitTabButton(
                icon: Icons.apps_rounded,
                label: str.splitTabApps,
                count: appEntries.length,
                isSelected: _activeTab == 1,
                onTap: () => setState(() => _activeTab = 1),
              ),
            ),
          ],
        ),

        const SizedBox(height: 14),

        if (_activeTab == 0)
          _WebsitesRulesCard(
            key: const ValueKey('websites_tab'),
            str: str,
            enabled: s.splitTunnelEnabled,
            isAdmin: isAdmin,
            entries: domainEntries,
            onAdd: (kind, value) {
              if (!isAdmin) {
                requestAdmin();
                return;
              }
              set((x) => x.splitTunnelEntries
                  .add(SplitTunnelEntry(kind: kind, value: value)));
            },
            onRemove: (entry) {
              if (!isAdmin) {
                requestAdmin();
                return;
              }
              set((x) => x.splitTunnelEntries
                  .removeWhere((e) => e.kind == entry.kind && e.value.toLowerCase() == entry.value.toLowerCase()));
            },
            onClearAll: () {
              if (!isAdmin) {
                requestAdmin();
                return;
              }
              set((x) => x.splitTunnelEntries.removeWhere((e) => e.kind != 'app'));
            },
          )
        else
          _ApplicationsRulesCard(
            key: const ValueKey('apps_tab'),
            str: str,
            enabled: s.splitTunnelEnabled,
            isAdmin: isAdmin,
            mode: s.splitTunnelMode,
            entries: appEntries,
            onAddApps: (apps) {
              if (!isAdmin) {
                requestAdmin();
                return;
              }
              set((x) {
                for (final exe in apps) {
                  if (!x.splitTunnelEntries.any((e) => e.kind == 'app' && e.value.toLowerCase() == exe.toLowerCase())) {
                    x.splitTunnelEntries.add(SplitTunnelEntry(kind: 'app', value: exe));
                  }
                }
              });
            },
            onRemove: (entry) {
              if (!isAdmin) {
                requestAdmin();
                return;
              }
              set((x) => x.splitTunnelEntries
                  .removeWhere((e) => e.kind == entry.kind && e.value.toLowerCase() == entry.value.toLowerCase()));
            },
            onClearAll: () {
              if (!isAdmin) {
                requestAdmin();
                return;
              }
              set((x) => x.splitTunnelEntries.removeWhere((e) => e.kind == 'app'));
            },
          ),
      ],
    );
  }
}

class _SplitTabButton extends StatefulWidget {
  const _SplitTabButton({
    required this.icon,
    required this.label,
    required this.count,
    required this.isSelected,
    required this.onTap,
  });

  final IconData icon;
  final String label;
  final int count;
  final bool isSelected;
  final VoidCallback onTap;

  @override
  State<_SplitTabButton> createState() => _SplitTabButtonState();
}

class _SplitTabButtonState extends State<_SplitTabButton> {
  bool _hovered = false;

  @override
  Widget build(BuildContext context) {
    final c = Theme.of(context).extension<AppColors>()!;
    final skyBlue = BrandColors.accentCyan;

    return MouseRegion(
      cursor: SystemMouseCursors.click,
      onEnter: (_) => setState(() => _hovered = true),
      onExit: (_) => setState(() => _hovered = false),
      child: GestureDetector(
        onTap: widget.onTap,
        child: AnimatedContainer(
          duration: const Duration(milliseconds: 160),
          height: 44,
          padding: const EdgeInsets.symmetric(horizontal: 14),
          decoration: BoxDecoration(
            color: widget.isSelected
                ? (c.isDark ? skyBlue.withValues(alpha: 0.18) : skyBlue.withValues(alpha: 0.12))
                : (_hovered ? c.cardElevated.withValues(alpha: 0.8) : c.card.withValues(alpha: 0.4)),
            borderRadius: BorderRadius.circular(12),
            border: Border.all(
              color: widget.isSelected
                  ? skyBlue.withValues(alpha: c.isDark ? 0.6 : 0.5)
                  : (_hovered ? c.border : c.border.withValues(alpha: 0.4)),
              width: widget.isSelected ? 1.4 : 1,
            ),
          ),
          child: Row(
            mainAxisAlignment: MainAxisAlignment.center,
            children: [
              Icon(
                widget.icon,
                size: 17,
                color: widget.isSelected
                    ? skyBlue
                    : (_hovered ? c.textPrimary : c.textSecondary),
              ),
              const SizedBox(width: 8),
              Text(
                widget.label,
                style: TextStyle(
                  fontSize: 12.5,
                  fontWeight: widget.isSelected ? FontWeight.w700 : FontWeight.w500,
                  color: widget.isSelected
                      ? (c.isDark ? Colors.white : skyBlue)
                      : (_hovered ? c.textPrimary : c.textSecondary),
                ),
              ),
              if (widget.count > 0) ...[
                const SizedBox(width: 8),
                Container(
                  padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 2),
                  decoration: BoxDecoration(
                    color: widget.isSelected
                        ? skyBlue.withValues(alpha: 0.25)
                        : c.input.withValues(alpha: 0.8),
                    borderRadius: BorderRadius.circular(10),
                  ),
                  child: Text(
                    '${widget.count}',
                    style: TextStyle(
                      fontSize: 11,
                      fontWeight: FontWeight.w700,
                      color: widget.isSelected ? skyBlue : c.textMuted,
                    ),
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

class _WebsitesRulesCard extends StatefulWidget {
  const _WebsitesRulesCard({
    super.key,
    required this.str,
    required this.enabled,
    this.isAdmin = false,
    required this.entries,
    required this.onAdd,
    required this.onRemove,
    required this.onClearAll,
  });

  final AppStrings str;
  final bool enabled;
  final bool isAdmin;
  final List<SplitTunnelEntry> entries;
  final void Function(String kind, String value) onAdd;
  final void Function(SplitTunnelEntry entry) onRemove;
  final VoidCallback onClearAll;

  @override
  State<_WebsitesRulesCard> createState() => _WebsitesRulesCardState();
}

class _WebsitesRulesCardState extends State<_WebsitesRulesCard> {

  bool _isValidDomain(String v) {
    if (v.isEmpty) return false;
    if (v.startsWith('*.')) v = v.substring(2);
    if (v.isEmpty || v.length > 253 || v.contains('..') || v.endsWith('.')) return false;
    if (v == 'localhost') return true;
    final labels = v.split('.');
    if (labels.length == 1) {

      return RegExp(r'^[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?$').hasMatch(v);
    }
    final labelRe = RegExp(r'^[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?$');
    for (final l in labels) {
      if (l.isEmpty || l.length > 63 || !labelRe.hasMatch(l)) return false;
    }

    if (!RegExp(r'^[A-Za-z]{2,}$').hasMatch(labels.last)) return false;
    return true;
  }

  bool _isValidIpOrCidr(String v) {
    if (v.contains('/')) {
      final parts = v.split('/');
      if (parts.length != 2) return false;
      final ip = parts[0]; final mask = int.tryParse(parts[1]);
      if (mask == null) return false;
      final isV4 = RegExp(r'^(\d{1,3}\.){3}\d{1,3}$').hasMatch(ip);
      final isV6 = ip.contains(':');
      if (isV4) {
        if (mask < 0 || mask > 32) return false;
        for (final p in ip.split('.')) { final n = int.tryParse(p); if (n == null || n < 0 || n > 255) return false; }
        return true;
      } else if (isV6) {
        if (mask < 0 || mask > 128) return false;
        return _isValidIpv6(ip);
      }
      return false;
    }

    if (RegExp(r'^(\d{1,3}\.){3}\d{1,3}$').hasMatch(v)) {
      return v.split('.').every((p) { final n=int.tryParse(p); return n!=null&&n>=0&&n<=255; });
    }
    if (v.contains(':')) return _isValidIpv6(v);
    return false;
  }

  bool _isValidIpv6(String v) {

    if (!RegExp(r'^[0-9a-fA-F:]+$').hasMatch(v)) return false;
    if (v.contains(':::')) return false;
    final doubleColon = '::'.allMatches(v).length;
    if (doubleColon > 1) return false;

    try {
      final addr = InternetAddress.tryParse(v);
      return addr != null && addr.type == InternetAddressType.IPv6;
    } catch (_) {

      final parts = v.split(':');
      if (doubleColon == 1) return parts.length <= 8;
      return parts.length == 8;
    }
  }

  void _openAddSiteModal() {
    if (!widget.isAdmin) { showDialog(context: context, barrierDismissible: true, builder: (ctx) => const AdminElevationDialog()); return; }
    showDialog(context: context, barrierDismissible: true, builder: (ctx) => _AddSiteModal(str: widget.str, onAdd: widget.onAdd, isValidDomain: _isValidDomain, isValidIpOrCidr: _isValidIpOrCidr));
  }

  void _confirmClearAll() {
    if (!widget.isAdmin) {
      showDialog(context: context, barrierDismissible: true, builder: (ctx) => const AdminElevationDialog());
      return;
    }
    showDialog(
      context: context,
      builder: (ctx) => AlertDialog(
        backgroundColor: Theme.of(ctx).extension<AppColors>()!.cardElevated,
        shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(16)),
        title: Row(
          children: [
            Container(
              padding: const EdgeInsets.all(7),
              decoration: BoxDecoration(
                color: BrandColors.danger.withValues(alpha: 0.15),
                borderRadius: BorderRadius.circular(8),
              ),
              child: const Icon(Icons.delete_sweep_rounded, size: 18, color: BrandColors.danger),
            ),
            const SizedBox(width: 10),
            Expanded(
              child: Text(
                widget.str.splitClearAllWebsitesConfirmTitle,
                style: const TextStyle(fontSize: 14, fontWeight: FontWeight.w700),
              ),
            ),
          ],
        ),
        content: Text(
          widget.str.splitClearAllWebsitesConfirmDesc,
          style: TextStyle(fontSize: 12, color: Theme.of(ctx).extension<AppColors>()!.textSecondary),
        ),
        actions: [
          OutlinedButton(
            onPressed: () => Navigator.pop(ctx),
            style: OutlinedButton.styleFrom(
              foregroundColor: BrandColors.danger,
              side: BorderSide(color: BrandColors.danger.withValues(alpha: 0.4)),
              shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(8)),
            ),
            child: Text(widget.str.cancel),
          ),
          ElevatedButton(
            style: ElevatedButton.styleFrom(
              backgroundColor: BrandColors.danger,
              foregroundColor: Colors.white,
              shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(8)),
            ),
            onPressed: () {
              Navigator.pop(ctx);
              widget.onClearAll();
            },
            child: Text(widget.str.delete, style: const TextStyle(fontWeight: FontWeight.w700)),
          ),
        ],
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final c = Theme.of(context).extension<AppColors>()!;

    return GlassCard(
      borderRadius: 14,
      child: Opacity(
        opacity: widget.enabled ? 1 : 0.45,
        child: IgnorePointer(
          ignoring: !widget.enabled,
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [

              Row(
                mainAxisAlignment: MainAxisAlignment.spaceBetween,
                children: [
                  Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        widget.str.splitDomainRulesTitle,
                        style: TextStyle(
                          fontSize: 13,
                          fontWeight: FontWeight.w700,
                          color: c.textPrimary,
                        ),
                      ),
                      const SizedBox(height: 2),
                      Text(
                        widget.str.splitItemsConfigured(widget.entries.length),
                        style: TextStyle(fontSize: 11.5, color: c.textMuted),
                      ),
                    ],
                  ),
                  Row(
                    mainAxisSize: MainAxisSize.min,
                    children: [
                      if (widget.entries.isNotEmpty) ...[
                        MouseRegion(
                          cursor: SystemMouseCursors.click,
                          child: Material(
                            color: Colors.transparent,
                            child: InkWell(
                              borderRadius: BorderRadius.circular(8),
                              onTap: _confirmClearAll,
                              child: Container(
                                height: 34,
                                padding: const EdgeInsets.symmetric(horizontal: 10),
                                decoration: BoxDecoration(
                                  color: BrandColors.danger.withValues(alpha: 0.1),
                                  borderRadius: BorderRadius.circular(8),
                                  border: Border.all(color: BrandColors.danger.withValues(alpha: 0.35)),
                                ),
                                child: Row(
                                  mainAxisSize: MainAxisSize.min,
                                  children: [
                                    const Icon(Icons.delete_sweep_rounded, size: 15, color: BrandColors.danger),
                                    const SizedBox(width: 5),
                                    Text(
                                      widget.str.splitClearAll,
                                      style: const TextStyle(
                                        fontSize: 11.5,
                                        fontWeight: FontWeight.w600,
                                        color: BrandColors.danger,
                                      ),
                                    ),
                                  ],
                                ),
                              ),
                            ),
                          ),
                        ),
                        const SizedBox(width: 8),
                      ],
                      MouseRegion(
                        cursor: SystemMouseCursors.click,
                        child: Material(
                          color: Colors.transparent,
                          child: InkWell(
                            borderRadius: BorderRadius.circular(8),
                            onTap: _openAddSiteModal,
                            child: Container(
                              height: 34,
                              padding: const EdgeInsets.symmetric(horizontal: 12),
                              decoration: BoxDecoration(
                                gradient: const LinearGradient(colors: BrandColors.accentGradient),
                                borderRadius: BorderRadius.circular(8),
                                boxShadow: [
                                  BoxShadow(
                                    color: BrandColors.primary.withValues(alpha: 0.25),
                                    blurRadius: 6,
                                    offset: const Offset(0, 2),
                                  ),
                                ],
                              ),
                              child: Row(
                                mainAxisSize: MainAxisSize.min,
                                children: [
                                  const Icon(Icons.add_rounded, size: 16, color: Colors.white),
                                  const SizedBox(width: 5),
                                  Text(
                                    widget.str.splitAddSiteOrIp,
                                    style: const TextStyle(
                                      fontSize: 11.5,
                                      fontWeight: FontWeight.w700,
                                      color: Colors.white,
                                    ),
                                  ),
                                ],
                              ),
                            ),
                          ),
                        ),
                      ),
                    ],
                  ),
                ],
              ),
              const SizedBox(height: 14),

              Row(
                children: [
                  Text(widget.str.splitQuickPresets, style: TextStyle(fontSize: 11, color: c.textMuted)),
                  const SizedBox(width: 6),
                  _PresetChip(label: '*.ir', onTap: () => widget.onAdd('domain', '*.ir')),
                  const SizedBox(width: 6),
                  _PresetChip(label: '192.168.0.0/16', onTap: () => widget.onAdd('ip', '192.168.0.0/16')),
                  const SizedBox(width: 6),
                  _PresetChip(label: '10.0.0.0/8', onTap: () => widget.onAdd('ip', '10.0.0.0/8')),
                  const SizedBox(width: 6),
                  _PresetChip(label: 'localhost', onTap: () => widget.onAdd('domain', 'localhost')),
                ],
              ),

              const SizedBox(height: 14),

              if (widget.entries.isEmpty)
                Container(
                  padding: const EdgeInsets.all(20),
                  alignment: Alignment.center,
                  decoration: BoxDecoration(
                    color: c.input.withValues(alpha: 0.35),
                    borderRadius: BorderRadius.circular(10),
                    border: Border.all(color: c.border.withValues(alpha: 0.4)),
                  ),
                  child: Column(
                    children: [
                      Icon(Icons.public_off_rounded, size: 28, color: c.textMuted),
                      const SizedBox(height: 6),
                      Text(widget.str.splitNoWebsitesYet,
                          style: TextStyle(fontSize: 12, color: c.textMuted)),
                    ],
                  ),
                )
              else
                ConstrainedBox(
                  constraints: const BoxConstraints(maxHeight: 220),
                  child: Scrollbar(
                    thumbVisibility: widget.entries.length > 6,
                    child: GridView.builder(
                      shrinkWrap: true,
                      itemCount: widget.entries.length,
                      gridDelegate: const SliverGridDelegateWithFixedCrossAxisCount(
                        crossAxisCount: 2,
                        mainAxisExtent: 42,
                        crossAxisSpacing: 10,
                        mainAxisSpacing: 8,
                      ),
                      itemBuilder: (context, i) => _SplitWebsiteTile(
                        entry: widget.entries[i],
                        onRemove: () => widget.onRemove(widget.entries[i]),
                      ),
                    ),
                  ),
                ),
            ],
          ),
        ),
      ),
    );
  }
}

class _ApplicationsRulesCard extends StatefulWidget {
  const _ApplicationsRulesCard({
    super.key,
    required this.str,
    required this.enabled,
    this.isAdmin = false,
    required this.mode,
    required this.entries,
    required this.onAddApps,
    required this.onRemove,
    required this.onClearAll,
  });

  final AppStrings str;
  final bool enabled;
  final bool isAdmin;
  final String mode;
  final List<SplitTunnelEntry> entries;
  final void Function(List<String> apps) onAddApps;
  final void Function(SplitTunnelEntry entry) onRemove;
  final VoidCallback onClearAll;

  @override
  State<_ApplicationsRulesCard> createState() => _ApplicationsRulesCardState();
}

class _ApplicationsRulesCardState extends State<_ApplicationsRulesCard> {
  final _scrollController = ScrollController();

  @override
  void dispose() {
    _scrollController.dispose();
    super.dispose();
  }

  void _openAppPickerModal(BuildContext context) {
    if (!widget.isAdmin) {
      showDialog(
        context: context,
        barrierDismissible: true,
        builder: (ctx) => const AdminElevationDialog(),
      );
      return;
    }
    showDialog(
      context: context,
      barrierDismissible: true,
      builder: (ctx) => _AppPickerModal(
        str: widget.str,
        alreadyAdded: widget.entries.map((e) => e.value.toLowerCase()).toSet(),
        onSelected: widget.onAddApps,
      ),
    );
  }

  void _confirmClearAll() {
    if (!widget.isAdmin) {
      showDialog(context: context, barrierDismissible: true, builder: (ctx) => const AdminElevationDialog());
      return;
    }
    showDialog(
      context: context,
      builder: (ctx) => AlertDialog(
        backgroundColor: Theme.of(ctx).extension<AppColors>()!.cardElevated,
        shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(16)),
        title: Row(
          children: [
            Container(
              padding: const EdgeInsets.all(7),
              decoration: BoxDecoration(
                color: BrandColors.danger.withValues(alpha: 0.15),
                borderRadius: BorderRadius.circular(8),
              ),
              child: const Icon(Icons.delete_sweep_rounded, size: 18, color: BrandColors.danger),
            ),
            const SizedBox(width: 10),
            Expanded(
              child: Text(
                widget.str.splitClearAllAppsConfirmTitle,
                style: const TextStyle(fontSize: 14, fontWeight: FontWeight.w700),
              ),
            ),
          ],
        ),
        content: Text(
          widget.str.splitClearAllAppsConfirmDesc,
          style: TextStyle(fontSize: 12, color: Theme.of(ctx).extension<AppColors>()!.textSecondary),
        ),
        actions: [
          OutlinedButton(
            onPressed: () => Navigator.pop(ctx),
            style: OutlinedButton.styleFrom(
              foregroundColor: BrandColors.danger,
              side: BorderSide(color: BrandColors.danger.withValues(alpha: 0.4)),
              shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(8)),
            ),
            child: Text(widget.str.cancel),
          ),
          ElevatedButton(
            style: ElevatedButton.styleFrom(
              backgroundColor: BrandColors.danger,
              foregroundColor: Colors.white,
              shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(8)),
            ),
            onPressed: () {
              Navigator.pop(ctx);
              widget.onClearAll();
            },
            child: Text(widget.str.delete, style: const TextStyle(fontWeight: FontWeight.w700)),
          ),
        ],
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final c = Theme.of(context).extension<AppColors>()!;
    final isBypass = widget.mode == 'exclude';

    return GlassCard(
      borderRadius: 14,
      child: Opacity(
        opacity: widget.enabled ? 1 : 0.45,
        child: IgnorePointer(
          ignoring: !widget.enabled,
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [

              Row(
                mainAxisAlignment: MainAxisAlignment.spaceBetween,
                children: [
                  Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        widget.str.splitAppsTitle,
                        style: TextStyle(
                          fontSize: 13,
                          fontWeight: FontWeight.w700,
                          color: c.textPrimary,
                        ),
                      ),
                      const SizedBox(height: 2),
                      Text(
                        isBypass
                            ? widget.str.splitAppsSubtitleBypass
                            : widget.str.splitAppsSubtitleRoute,
                        style: TextStyle(fontSize: 11.5, color: c.textMuted),
                      ),
                    ],
                  ),
                  Row(
                    mainAxisSize: MainAxisSize.min,
                    children: [
                      if (widget.entries.isNotEmpty) ...[
                        MouseRegion(
                          cursor: SystemMouseCursors.click,
                          child: Material(
                            color: Colors.transparent,
                            child: InkWell(
                              borderRadius: BorderRadius.circular(8),
                              onTap: _confirmClearAll,
                              child: Container(
                                height: 34,
                                padding: const EdgeInsets.symmetric(horizontal: 10),
                                decoration: BoxDecoration(
                                  color: BrandColors.danger.withValues(alpha: 0.1),
                                  borderRadius: BorderRadius.circular(8),
                                  border: Border.all(color: BrandColors.danger.withValues(alpha: 0.35)),
                                ),
                                child: Row(
                                  mainAxisSize: MainAxisSize.min,
                                  children: [
                                    const Icon(Icons.delete_sweep_rounded, size: 15, color: BrandColors.danger),
                                    const SizedBox(width: 5),
                                    Text(
                                      widget.str.splitClearAll,
                                      style: const TextStyle(
                                        fontSize: 11.5,
                                        fontWeight: FontWeight.w600,
                                        color: BrandColors.danger,
                                      ),
                                    ),
                                  ],
                                ),
                              ),
                            ),
                          ),
                        ),
                        const SizedBox(width: 8),
                      ],
                      MouseRegion(
                        cursor: SystemMouseCursors.click,
                        child: Material(
                          color: Colors.transparent,
                          child: InkWell(
                            borderRadius: BorderRadius.circular(8),
                            onTap: () => _openAppPickerModal(context),
                            child: Container(
                              height: 34,
                              padding: const EdgeInsets.symmetric(horizontal: 12),
                              decoration: BoxDecoration(
                                gradient: const LinearGradient(colors: BrandColors.accentGradient),
                                borderRadius: BorderRadius.circular(8),
                                boxShadow: [
                                  BoxShadow(
                                    color: BrandColors.primary.withValues(alpha: 0.25),
                                    blurRadius: 6,
                                    offset: const Offset(0, 2),
                                  ),
                                ],
                              ),
                              child: Row(
                                mainAxisSize: MainAxisSize.min,
                                children: [
                                  const Icon(Icons.add_circle_outline_rounded, size: 15, color: Colors.white),
                                  const SizedBox(width: 5),
                                  Text(
                                    widget.str.splitAddApp,
                                    style: const TextStyle(fontSize: 11.5, fontWeight: FontWeight.w700, color: Colors.white),
                                  ),
                                ],
                              ),
                            ),
                          ),
                        ),
                      ),
                    ],
                  ),
                ],
              ),

              const SizedBox(height: 16),

              if (widget.entries.isEmpty)
                Container(
                  padding: const EdgeInsets.symmetric(vertical: 36, horizontal: 24),
                  alignment: Alignment.center,
                  decoration: BoxDecoration(
                    color: c.input.withValues(alpha: 0.35),
                    borderRadius: BorderRadius.circular(12),
                    border: Border.all(color: c.border.withValues(alpha: 0.4)),
                  ),
                  child: Column(
                    children: [
                      Container(
                        padding: const EdgeInsets.all(12),
                        decoration: BoxDecoration(
                          color: BrandColors.accentCyan.withValues(alpha: 0.12),
                          shape: BoxShape.circle,
                        ),
                        child: const Icon(Icons.apps_rounded, size: 30, color: BrandColors.accentCyan),
                      ),
                      const SizedBox(height: 10),
                      Text(
                        widget.str.splitNoAppsYet,
                        style: TextStyle(fontSize: 13, fontWeight: FontWeight.w600, color: c.textPrimary),
                      ),
                      const SizedBox(height: 4),
                      Text(
                        widget.str.splitNoAppsDesc,
                        style: TextStyle(fontSize: 11.5, color: c.textMuted),
                        textAlign: TextAlign.center,
                      ),
                    ],
                  ),
                )
              else
                ConstrainedBox(
                  constraints: const BoxConstraints(maxHeight: 220),
                  child: Scrollbar(
                    controller: _scrollController,
                    thumbVisibility: widget.entries.length > 6,
                    child: GridView.builder(
                      controller: _scrollController,
                      shrinkWrap: true,
                      itemCount: widget.entries.length,
                      gridDelegate: const SliverGridDelegateWithFixedCrossAxisCount(
                        crossAxisCount: 2,
                        mainAxisExtent: 42,
                        crossAxisSpacing: 10,
                        mainAxisSpacing: 8,
                      ),
                      itemBuilder: (context, i) => _SplitAppTile(
                        entry: widget.entries[i],
                        onRemove: () => widget.onRemove(widget.entries[i]),
                      ),
                    ),
                  ),
                ),
            ],
          ),
        ),
      ),
    );
  }
}

class _AppPickerModal extends ConsumerStatefulWidget {
  const _AppPickerModal({
    required this.str,
    required this.alreadyAdded,
    required this.onSelected,
  });

  final AppStrings str;
  final Set<String> alreadyAdded;
  final void Function(List<String> apps) onSelected;

  @override
  ConsumerState<_AppPickerModal> createState() => _AppPickerModalState();
}

class _AppPickerModalState extends ConsumerState<_AppPickerModal> {
  final _searchCtrl = TextEditingController();
  final _customExeCtrl = TextEditingController();
  final _scrollCtrl = ScrollController();
  final Set<String> _selectedExes = {};
  String _filter = '';
  List<KnownApp> _apps = const [];
  bool _loading = true;
  StreamSubscription? _appsSub;

  static List<KnownApp> _parseApps(List<Map<String, dynamic>> rawList) {
    final list = rawList.map((m) {
      final exe = m['exe']?.toString() ?? '';
      final name = m['name']?.toString() ?? exe;
      final iconBase64 = m['icon']?.toString();
      return KnownApp(
        name: name,
        exe: exe,
        category: 'Installed App',
        icon: Icons.apps_rounded,
        iconBase64: (iconBase64 != null && iconBase64.isNotEmpty) ? iconBase64 : null,
      );
    }).where((a) => a.exe.isNotEmpty).toList();

    list.sort((a, b) => a.name.toLowerCase().compareTo(b.name.toLowerCase()));
    return list;
  }

  @override
  void initState() {
    super.initState();
    final client = ref.read(coreClientProvider);
    if (client is PipeCoreClient) {
      if (client.cachedInstalledApps != null && client.cachedInstalledApps!.isNotEmpty) {
        _apps = _parseApps(client.cachedInstalledApps!);
        _loading = false;
      }

      _appsSub = client.installedAppsStream.listen((rawList) {
        if (!mounted) return;
        final list = _parseApps(rawList);
        setState(() {
          _apps = list;
          _loading = false;
        });
      });

      if (_apps.isEmpty) {
        client.fetchInstalledApps();
      }
    } else {
      _loading = false;
    }
  }

  @override
  void dispose() {
    _appsSub?.cancel();
    _searchCtrl.dispose();
    _customExeCtrl.dispose();
    _scrollCtrl.dispose();
    super.dispose();
  }

  void _addCustomExe() {
    final text = _customExeCtrl.text.trim();
    if (text.isEmpty) return;
    final exeName = text.endsWith('.exe') ? text : '$text.exe';
    setState(() {
      _selectedExes.add(exeName);
      _customExeCtrl.clear();
    });
  }

  @override
  Widget build(BuildContext context) {
    final c = Theme.of(context).extension<AppColors>()!;
    final mediaQuery = MediaQuery.of(context);
    final screenHeight = mediaQuery.size.height;
    final screenWidth = mediaQuery.size.width;
    final dialogMaxHeight = (screenHeight * 0.82).clamp(380.0, 580.0);
    final dialogMaxWidth = (screenWidth * 0.90).clamp(360.0, 560.0);

    final filteredApps = _apps.where((app) {
      if (_filter.isEmpty) return true;
      final q = _filter.toLowerCase();
      return app.name.toLowerCase().contains(q) || app.exe.toLowerCase().contains(q);
    }).toList();

    return Dialog(
      backgroundColor: c.cardElevated,
      shape: RoundedRectangleBorder(
        borderRadius: BorderRadius.circular(18),
        side: BorderSide(color: c.border.withValues(alpha: 0.6)),
      ),
      insetPadding: const EdgeInsets.symmetric(horizontal: 20, vertical: 20),
      child: ConstrainedBox(
        constraints: BoxConstraints(maxWidth: dialogMaxWidth, maxHeight: dialogMaxHeight),
        child: Padding(
          padding: const EdgeInsets.all(22),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [

              Row(
                children: [
                  Container(
                    padding: const EdgeInsets.all(8),
                    decoration: BoxDecoration(
                      color: BrandColors.accentCyan.withValues(alpha: 0.15),
                      borderRadius: BorderRadius.circular(10),
                    ),
                    child: const Icon(Icons.dashboard_customize_rounded, size: 20, color: BrandColors.accentCyan),
                  ),
                  const SizedBox(width: 12),
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(
                          widget.str.splitAppPickerTitle,
                          style: TextStyle(fontSize: 14.5, fontWeight: FontWeight.w700, color: c.textPrimary),
                        ),
                        const SizedBox(height: 2),
                        Text(
                          widget.str.splitAppPickerSubtitle,
                          style: TextStyle(fontSize: 11.5, color: c.textMuted),
                        ),
                      ],
                    ),
                  ),
                  MouseRegion(
                    cursor: SystemMouseCursors.click,
                    child: IconButton(
                      icon: const Icon(Icons.close_rounded, size: 18),
                      onPressed: () => Navigator.of(context).pop(),
                      color: BrandColors.danger.withValues(alpha: 0.85),
                    ),
                  ),
                ],
              ),

              const SizedBox(height: 16),

              SizedBox(
                height: 40,
                child: TextField(
                  controller: _searchCtrl,
                  onChanged: (v) {
                    setState(() => _filter = v);
                    if (_scrollCtrl.hasClients) {
                      _scrollCtrl.jumpTo(0);
                    }
                  },
                  style: TextStyle(fontSize: 12.5, color: c.textPrimary),
                  textAlignVertical: TextAlignVertical.center,
                  decoration: InputDecoration(
                    prefixIcon: Icon(Icons.search_rounded, size: 18, color: c.textSecondary),
                    hintText: widget.str.splitSearchAppsHint,
                    hintStyle: TextStyle(fontSize: 12, color: c.textMuted),
                    filled: true,
                    fillColor: c.input.withValues(alpha: 0.65),
                    isDense: true,
                    contentPadding: const EdgeInsets.symmetric(horizontal: 12),
                    border: OutlineInputBorder(
                      borderRadius: BorderRadius.circular(10),
                      borderSide: BorderSide(color: c.border.withValues(alpha: 0.6)),
                    ),
                    enabledBorder: OutlineInputBorder(
                      borderRadius: BorderRadius.circular(10),
                      borderSide: BorderSide(color: c.border.withValues(alpha: 0.6)),
                    ),
                    focusedBorder: OutlineInputBorder(
                      borderRadius: BorderRadius.circular(10),
                      borderSide: const BorderSide(color: BrandColors.accentCyan, width: 1.4),
                    ),
                  ),
                ),
              ),

              const SizedBox(height: 12),

              Expanded(
                child: Container(
                  clipBehavior: Clip.antiAlias,
                  decoration: BoxDecoration(
                    color: c.input.withValues(alpha: 0.3),
                    borderRadius: BorderRadius.circular(12),
                    border: Border.all(color: c.border.withValues(alpha: 0.5)),
                  ),
                  child: _loading && filteredApps.isEmpty
                      ? Center(
                          child: Column(
                            mainAxisSize: MainAxisSize.min,
                            children: [
                              const SizedBox(
                                width: 24,
                                height: 24,
                                child: CircularProgressIndicator(strokeWidth: 2, color: BrandColors.accentCyan),
                              ),
                              const SizedBox(height: 12),
                              Text(widget.str.splitScanningApps, style: TextStyle(fontSize: 12, color: c.textMuted)),
                            ],
                          ),
                        )
                      : filteredApps.isEmpty
                          ? Center(
                              child: Column(
                                mainAxisSize: MainAxisSize.min,
                                children: [
                                  Icon(Icons.search_off_rounded, size: 32, color: c.textMuted.withValues(alpha: 0.6)),
                                  const SizedBox(height: 8),
                                  Text(widget.str.splitNoMatchingApps, style: TextStyle(fontSize: 12.5, fontWeight: FontWeight.w600, color: c.textSecondary)),
                                  const SizedBox(height: 4),
                                  Text(widget.str.splitCustomExeHintBelow, style: TextStyle(fontSize: 11, color: c.textMuted)),
                                ],
                              ),
                            )
                          : Theme(
                              data: Theme.of(context).copyWith(
                                scrollbarTheme: ScrollbarThemeData(
                                  thumbColor: WidgetStateProperty.all(BrandColors.accentCyan.withValues(alpha: 0.45)),
                                  trackColor: WidgetStateProperty.all(Colors.transparent),
                                  thickness: WidgetStateProperty.all(6),
                                  radius: const Radius.circular(3),
                                  thumbVisibility: WidgetStateProperty.all(true),
                                  interactive: true,
                                ),
                              ),
                              child: ClipRRect(
                                borderRadius: BorderRadius.circular(12),
                                child: Scrollbar(
                                  controller: _scrollCtrl,
                                  thumbVisibility: true,
                                  child: ListView.separated(
                                    controller: _scrollCtrl,
                                    physics: const AlwaysScrollableScrollPhysics(),
                                    padding: const EdgeInsets.symmetric(vertical: 4),
                                    itemCount: filteredApps.length,
                                    separatorBuilder: (context, index) => Divider(height: 1, color: c.border.withValues(alpha: 0.25)),
                                    itemBuilder: (context, i) {
                                      final app = filteredApps[i];
                                      final isAlreadyAdded = widget.alreadyAdded.contains(app.exe.toLowerCase());
                                      final isSelected = _selectedExes.contains(app.exe);

                                      return _AppPickerItemRow(
                                        str: widget.str,
                                        app: app,
                                        isAlreadyAdded: isAlreadyAdded,
                                        isSelected: isSelected,
                                        onToggle: () {
                                          setState(() {
                                            if (isSelected) {
                                              _selectedExes.remove(app.exe);
                                            } else {
                                              _selectedExes.add(app.exe);
                                            }
                                          });
                                        },
                                      );
                                    },
                                  ),
                                ),
                              ),
                            ),
                ),
              ),

              const SizedBox(height: 12),

              Row(
                crossAxisAlignment: CrossAxisAlignment.center,
                children: [
                  Expanded(
                    child: Container(
                      height: 44,
                      alignment: Alignment.center,
                      decoration: BoxDecoration(
                        color: c.input.withValues(alpha: 0.65),
                        borderRadius: BorderRadius.circular(10),
                        border: Border.all(color: c.border.withValues(alpha: 0.6)),
                      ),
                      child: TextField(
                        controller: _customExeCtrl,
                        onSubmitted: (_) => _addCustomExe(),
                        style: TextStyle(fontSize: 12.5, color: c.textPrimary),
                        textAlignVertical: TextAlignVertical.center,
                        decoration: InputDecoration(
                          hintText: widget.str.splitCustomExeInputHint,
                          hintStyle: TextStyle(fontSize: 12, color: c.textMuted),
                          isDense: true,
                          isCollapsed: true,
                          contentPadding: const EdgeInsets.symmetric(horizontal: 14),
                          border: InputBorder.none,
                          enabledBorder: InputBorder.none,
                          focusedBorder: InputBorder.none,
                        ),
                      ),
                    ),
                  ),
                  const SizedBox(width: 8),
                  MouseRegion(
                    cursor: SystemMouseCursors.click,
                    child: Material(
                      color: Colors.transparent,
                      child: InkWell(
                        borderRadius: BorderRadius.circular(10),
                        onTap: _addCustomExe,
                        child: Container(
                          height: 44,
                          padding: const EdgeInsets.symmetric(horizontal: 14),
                          decoration: BoxDecoration(
                            color: c.cardElevated,
                            borderRadius: BorderRadius.circular(10),
                            border: Border.all(color: c.border),
                          ),
                          child: Row(
                            children: [
                              const Icon(Icons.add_rounded, size: 17, color: BrandColors.accentCyan),
                              const SizedBox(width: 5),
                              Text(widget.str.splitAddCustom, style: TextStyle(fontSize: 12, color: c.textPrimary, fontWeight: FontWeight.w600)),
                            ],
                          ),
                        ),
                      ),
                    ),
                  ),
                ],
              ),

              const SizedBox(height: 16),

              Row(
                mainAxisAlignment: MainAxisAlignment.spaceBetween,
                children: [
                  Text(
                    widget.str.splitAppsSelected(_selectedExes.length),
                    style: TextStyle(fontSize: 12, fontWeight: FontWeight.w600, color: c.textSecondary),
                  ),
                  Row(
                    children: [
                      TextButton(
                        onPressed: () => Navigator.of(context).pop(),
                        style: TextButton.styleFrom(
                          foregroundColor: BrandColors.danger,
                          padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 8),
                        ),
                        child: Text(widget.str.cancel, style: const TextStyle(fontSize: 12.5, fontWeight: FontWeight.w600)),
                      ),
                      const SizedBox(width: 8),
                      ElevatedButton(
                        onPressed: _selectedExes.isEmpty
                            ? null
                            : () {
                                widget.onSelected(_selectedExes.toList());
                                Navigator.of(context).pop();
                              },
                        style: ElevatedButton.styleFrom(
                          backgroundColor: BrandColors.primary,
                          foregroundColor: Colors.white,
                          elevation: 4,
                          padding: const EdgeInsets.symmetric(horizontal: 18, vertical: 10),
                          shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(10)),
                        ),
                        child: Text(
                          widget.str.splitAddSelected(_selectedExes.length),
                          style: const TextStyle(fontSize: 12.5, fontWeight: FontWeight.w700),
                        ),
                      ),
                    ],
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

class _AppPickerItemRow extends StatefulWidget {
  const _AppPickerItemRow({
    required this.str,
    required this.app,
    required this.isAlreadyAdded,
    required this.isSelected,
    required this.onToggle,
  });

  final AppStrings str;
  final KnownApp app;
  final bool isAlreadyAdded;
  final bool isSelected;
  final VoidCallback onToggle;

  @override
  State<_AppPickerItemRow> createState() => _AppPickerItemRowState();
}

class _AppPickerItemRowState extends State<_AppPickerItemRow> {
  bool _hovered = false;

  @override
  Widget build(BuildContext context) {
    final c = Theme.of(context).extension<AppColors>()!;
    final isAlreadyAdded = widget.isAlreadyAdded;
    final isSelected = widget.isSelected;
    final app = widget.app;

    return MouseRegion(
      cursor: isAlreadyAdded ? SystemMouseCursors.basic : SystemMouseCursors.click,
      onEnter: (_) {
        if (!isAlreadyAdded) setState(() => _hovered = true);
      },
      onExit: (_) {
        if (_hovered) setState(() => _hovered = false);
      },
      child: GestureDetector(
        behavior: HitTestBehavior.opaque,
        onTap: isAlreadyAdded ? null : widget.onToggle,
        child: AnimatedContainer(
          duration: const Duration(milliseconds: 120),
          padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 8),
          decoration: BoxDecoration(
            color: isAlreadyAdded
                ? Colors.transparent
                : isSelected
                    ? BrandColors.accentCyan.withValues(alpha: 0.12)
                    : _hovered
                        ? c.cardElevated.withValues(alpha: 0.75)
                        : Colors.transparent,
          ),
          child: Row(
            children: [
              Container(
                width: 32,
                height: 32,
                alignment: Alignment.center,
                decoration: BoxDecoration(
                  color: isAlreadyAdded
                      ? c.border.withValues(alpha: 0.2)
                      : isSelected
                          ? BrandColors.accentCyan.withValues(alpha: 0.2)
                          : BrandColors.accentCyan.withValues(alpha: 0.1),
                  borderRadius: BorderRadius.circular(8),
                ),
                child: (app.iconBase64 != null && app.iconBase64!.isNotEmpty)
                    ? Image.memory(
                        base64Decode(app.iconBase64!),
                        width: 20,
                        height: 20,
                        fit: BoxFit.contain,
                        errorBuilder: (ctx, err, stack) => Icon(
                          app.icon,
                          size: 17,
                          color: isAlreadyAdded ? c.textMuted : BrandColors.accentCyan,
                        ),
                      )
                    : Icon(
                        app.icon,
                        size: 17,
                        color: isAlreadyAdded ? c.textMuted : BrandColors.accentCyan,
                      ),
              ),
              const SizedBox(width: 12),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    Text(
                      app.name,
                      style: TextStyle(
                        fontSize: 12.5,
                        fontWeight: FontWeight.w600,
                        color: isAlreadyAdded ? c.textMuted : c.textPrimary,
                      ),
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                    ),
                    const SizedBox(height: 2),
                    Text(
                      '${app.exe} • ${app.category}',
                      style: TextStyle(fontSize: 10.5, color: c.textMuted),
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                    ),
                  ],
                ),
              ),
              const SizedBox(width: 8),
              if (isAlreadyAdded)
                Container(
                  padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 3),
                  decoration: BoxDecoration(
                    color: c.input.withValues(alpha: 0.8),
                    borderRadius: BorderRadius.circular(6),
                  ),
                  child: Text(
                    widget.str.splitBadgeAdded,
                    style: TextStyle(fontSize: 10.5, color: c.textMuted),
                  ),
                )
              else
                _CustomCheckbox(
                  checked: isSelected,
                  onChanged: (v) => widget.onToggle(),
                ),
            ],
          ),
        ),
      ),
    );
  }
}

class _CustomCheckbox extends StatefulWidget {
  const _CustomCheckbox({
    required this.checked,
    required this.onChanged,
  });

  final bool checked;
  final ValueChanged<bool>? onChanged;

  @override
  State<_CustomCheckbox> createState() => _CustomCheckboxState();
}

class _CustomCheckboxState extends State<_CustomCheckbox> {
  bool _hovered = false;

  @override
  Widget build(BuildContext context) {
    final c = Theme.of(context).extension<AppColors>()!;
    final checked = widget.checked;

    return MouseRegion(
      cursor: SystemMouseCursors.click,
      onEnter: (_) => setState(() => _hovered = true),
      onExit: (_) => setState(() => _hovered = false),
      child: GestureDetector(
        onTap: () => widget.onChanged?.call(!checked),
        child: AnimatedContainer(
          duration: const Duration(milliseconds: 140),
          width: 20,
          height: 20,
          decoration: BoxDecoration(
            color: checked
                ? BrandColors.accentCyan
                : _hovered
                    ? BrandColors.accentCyan.withValues(alpha: 0.15)
                    : c.input.withValues(alpha: 0.6),
            borderRadius: BorderRadius.circular(5),
            border: Border.all(
              color: checked
                  ? BrandColors.accentCyan
                  : _hovered
                      ? BrandColors.accentCyan.withValues(alpha: 0.7)
                      : c.border.withValues(alpha: 0.7),
              width: 1.5,
            ),
            boxShadow: checked
                ? [
                    BoxShadow(
                      color: BrandColors.accentCyan.withValues(alpha: 0.35),
                      blurRadius: 6,
                      offset: const Offset(0, 1),
                    )
                  ]
                : null,
          ),
          child: Center(
            child: AnimatedScale(
              duration: const Duration(milliseconds: 140),
              scale: checked ? 1.0 : 0.0,
              child: const Icon(Icons.check_rounded, size: 14, color: Colors.black),
            ),
          ),
        ),
      ),
    );
  }
}

class _PresetChip extends StatelessWidget {
  const _PresetChip({required this.label, required this.onTap});
  final String label;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final c = Theme.of(context).extension<AppColors>()!;
    return MouseRegion(
      cursor: SystemMouseCursors.click,
      child: InkWell(
        borderRadius: BorderRadius.circular(6),
        onTap: onTap,
        child: Container(
          padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 3),
          decoration: BoxDecoration(
            color: c.input.withValues(alpha: 0.6),
            borderRadius: BorderRadius.circular(6),
            border: Border.all(color: c.border.withValues(alpha: 0.4)),
          ),
          child: Text(
            label,
            style: AppTheme.mono(c.textSecondary, size: 10.5, weight: FontWeight.w500),
          ),
        ),
      ),
    );
  }
}

class _SplitWebsiteTile extends StatefulWidget {
  const _SplitWebsiteTile({required this.entry, required this.onRemove});
  final SplitTunnelEntry entry;
  final VoidCallback onRemove;

  @override
  State<_SplitWebsiteTile> createState() => _SplitWebsiteTileState();
}

class _SplitWebsiteTileState extends State<_SplitWebsiteTile> {
  bool _hovered = false;

  IconData get _icon => switch (widget.entry.kind) {
        'ip' => Icons.numbers_rounded,
        _ => Icons.language_rounded,
      };

  @override
  Widget build(BuildContext context) {
    final c = Theme.of(context).extension<AppColors>()!;

    return MouseRegion(
      onEnter: (_) => setState(() => _hovered = true),
      onExit: (_) => setState(() => _hovered = false),
      child: AnimatedContainer(
        duration: const Duration(milliseconds: 140),
        height: 42,
        padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 6),
        decoration: BoxDecoration(
          color: _hovered
              ? c.cardElevated.withValues(alpha: 0.95)
              : c.input.withValues(alpha: 0.7),
          borderRadius: BorderRadius.circular(10),
          border: Border.all(
            color: _hovered
                ? BrandColors.accentCyan.withValues(alpha: 0.5)
                : c.border.withValues(alpha: 0.55),
          ),
          boxShadow: _hovered
              ? [
                  BoxShadow(
                    color: Colors.black.withValues(alpha: 0.05),
                    blurRadius: 4,
                    offset: const Offset(0, 1),
                  ),
                ]
              : null,
        ),
        child: Row(
          children: [
            Container(
              width: 24,
              height: 24,
              decoration: BoxDecoration(
                color: BrandColors.accentCyan.withValues(alpha: 0.12),
                borderRadius: BorderRadius.circular(6),
              ),
              child: Icon(_icon, size: 14, color: BrandColors.accentCyan),
            ),
            const SizedBox(width: 8),
            Expanded(
              child: Text(
                widget.entry.value,
                style: AppTheme.mono(c.textPrimary, size: 12, weight: FontWeight.w500),
                maxLines: 1,
                overflow: TextOverflow.ellipsis,
              ),
            ),
            const SizedBox(width: 6),
            MouseRegion(
              cursor: SystemMouseCursors.click,
              child: InkWell(
                borderRadius: BorderRadius.circular(6),
                onTap: widget.onRemove,
                child: Padding(
                  padding: const EdgeInsets.all(3),
                  child: Icon(
                    Icons.close_rounded,
                    size: 14,
                    color: _hovered ? BrandColors.danger : c.textMuted,
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

class _AddSiteModal extends StatefulWidget {
  const _AddSiteModal({
    required this.str,
    required this.onAdd,
    required this.isValidDomain,
    required this.isValidIpOrCidr,
  });

  final AppStrings str;
  final void Function(String kind, String value) onAdd;
  final bool Function(String) isValidDomain;
  final bool Function(String) isValidIpOrCidr;

  @override
  State<_AddSiteModal> createState() => _AddSiteModalState();
}

class _AddSiteModalState extends State<_AddSiteModal> {
  final _ctrl = TextEditingController();
  final _focusNode = FocusNode();
  bool _includeSub = true;
  bool _isIp = false;
  String? _resolvedIp;
  String? _err;
  bool _resolving = false;

  @override
  void dispose() {
    _ctrl.dispose();
    _focusNode.dispose();
    super.dispose();
  }

  Future<void> _resolve() async {
    final v = _ctrl.text.trim();
    if (v.isEmpty || v.contains('/')) {
      setState(() => _resolvedIp = null);
      return;
    }
    String host = v;
    if (host.startsWith('*.')) host = host.substring(2);
    if (widget.isValidIpOrCidr(host)) {
      setState(() => _resolvedIp = null);
      return;
    }
    if (!mounted) return;
    setState(() => _resolving = true);
    try {
      final list = await InternetAddress.lookup(host);
      if (!mounted) return;
      setState(() => _resolvedIp = list.isNotEmpty ? list.first.address : null);
    } catch (_) {
      if (!mounted) return;
      setState(() => _resolvedIp = null);
    }
    if (!mounted) return;
    setState(() => _resolving = false);
  }

  void _submit() {
    var v = _ctrl.text.trim();
    if (v.isEmpty) {
      setState(() => _err = widget.str.splitEnterTargetAddress);
      return;
    }
    if (!_isIp) {
      if (v.startsWith('*.')) v = v.substring(2);
      if (!widget.isValidDomain(v)) {
        setState(() => _err = widget.str.splitInvalidDomain);
        return;
      }
      if (_includeSub) v = '*.$v';
      widget.onAdd('domain', v);
    } else {
      if (!widget.isValidIpOrCidr(v)) {
        setState(() => _err = widget.str.splitInvalidIp);
        return;
      }
      widget.onAdd('ip', v);
    }
    Navigator.of(context).pop();
  }

  @override
  Widget build(BuildContext context) {
    final c = Theme.of(context).extension<AppColors>()!;

    return Dialog(
      backgroundColor: c.cardElevated,
      shape: RoundedRectangleBorder(
        borderRadius: BorderRadius.circular(18),
        side: BorderSide(color: c.border.withValues(alpha: 0.6)),
      ),
      insetPadding: const EdgeInsets.symmetric(horizontal: 20, vertical: 24),
      child: ConstrainedBox(
        constraints: const BoxConstraints(maxWidth: 480),
        child: Padding(
          padding: const EdgeInsets.all(22),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [

              Row(
                children: [
                  Container(
                    width: 36,
                    height: 36,
                    alignment: Alignment.center,
                    decoration: BoxDecoration(
                      color: BrandColors.accentCyan.withValues(alpha: 0.12),
                      borderRadius: BorderRadius.circular(10),
                      border: Border.all(color: BrandColors.accentCyan.withValues(alpha: 0.25)),
                    ),
                    child: const Icon(Icons.add_link_rounded, size: 20, color: BrandColors.accentCyan),
                  ),
                  const SizedBox(width: 12),
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(
                          widget.str.splitAddRoutingRule,
                          style: TextStyle(
                            fontSize: 15,
                            fontWeight: FontWeight.w700,
                            color: c.textPrimary,
                          ),
                        ),
                        const SizedBox(height: 2),
                        Text(
                          widget.str.splitAddRoutingRuleDesc,
                          style: TextStyle(fontSize: 11.5, color: c.textMuted),
                        ),
                      ],
                    ),
                  ),
                  MouseRegion(
                    cursor: SystemMouseCursors.click,
                    child: GestureDetector(
                      onTap: () => Navigator.pop(context),
                      child: Container(
                        padding: const EdgeInsets.all(6),
                        decoration: BoxDecoration(
                          color: BrandColors.danger.withValues(alpha: 0.12),
                          borderRadius: BorderRadius.circular(8),
                          border: Border.all(color: BrandColors.danger.withValues(alpha: 0.3)),
                        ),
                        child: const Icon(Icons.close_rounded, size: 16, color: BrandColors.danger),
                      ),
                    ),
                  ),
                ],
              ),

              const SizedBox(height: 18),

              _RuleTypeSegmentButton(
                str: widget.str,
                isIp: _isIp,
                onChanged: (val) {
                  setState(() {
                    _isIp = val;
                    _err = null;
                    _resolvedIp = null;
                  });
                },
              ),

              const SizedBox(height: 16),

              Text(
                _isIp ? widget.str.splitIpCidr : widget.str.splitDomainFqdn,
                style: TextStyle(fontSize: 11.5, fontWeight: FontWeight.w600, color: c.textSecondary),
              ),
              const SizedBox(height: 6),
              Container(
                decoration: BoxDecoration(
                  color: c.input.withValues(alpha: 0.5),
                  borderRadius: BorderRadius.circular(10),
                  border: Border.all(
                    color: _err != null
                        ? BrandColors.danger.withValues(alpha: 0.7)
                        : c.border.withValues(alpha: 0.6),
                  ),
                ),
                child: TextField(
                  controller: _ctrl,
                  focusNode: _focusNode,
                  onChanged: (_) => setState(() => _err = null),
                  onSubmitted: (_) {
                    if (!_isIp) _resolve();
                  },
                  style: TextStyle(
                    fontSize: 13,
                    color: c.textPrimary,
                    fontFamily: 'Consolas',
                  ),
                  decoration: InputDecoration(
                    prefixIcon: Icon(
                      _isIp ? Icons.dns_rounded : Icons.link_rounded,
                      size: 16,
                      color: BrandColors.accentCyan.withValues(alpha: 0.8),
                    ),
                    suffixIcon: _ctrl.text.isNotEmpty
                        ? MouseRegion(
                            cursor: SystemMouseCursors.click,
                            child: IconButton(
                              icon: const Icon(Icons.clear_rounded, size: 16),
                              color: c.textMuted,
                              onPressed: () {
                                _ctrl.clear();
                                setState(() {
                                  _err = null;
                                  _resolvedIp = null;
                                });
                              },
                            ),
                          )
                        : null,
                    hintText: _isIp ? 'e.g. 192.168.1.0/24 or 1.1.1.1' : 'e.g. example.com or api.github.com',
                    hintStyle: TextStyle(fontSize: 12, color: c.textMuted.withValues(alpha: 0.6), fontFamily: 'Segoe UI'),
                    border: InputBorder.none,
                    contentPadding: const EdgeInsets.symmetric(horizontal: 12, vertical: 12),
                  ),
                ),
              ),

              if (!_isIp) ...[
                const SizedBox(height: 12),
                _ModernSubdomainCheckboxCard(
                  str: widget.str,
                  checked: _includeSub,
                  onChanged: (v) => setState(() => _includeSub = v),
                ),
              ],

              if (_resolving)
                Padding(
                  padding: const EdgeInsets.only(top: 10),
                  child: Row(
                    children: [
                      const SizedBox(
                        width: 14,
                        height: 14,
                        child: CircularProgressIndicator(strokeWidth: 2, color: BrandColors.accentCyan),
                      ),
                      const SizedBox(width: 8),
                      Text(widget.str.splitResolvingDns, style: TextStyle(fontSize: 11, color: c.textMuted)),
                    ],
                  ),
                ),

              if (_resolvedIp != null)
                Padding(
                  padding: const EdgeInsets.only(top: 10),
                  child: Container(
                    padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 7),
                    decoration: BoxDecoration(
                      color: BrandColors.emerald.withValues(alpha: 0.1),
                      borderRadius: BorderRadius.circular(8),
                      border: Border.all(color: BrandColors.emerald.withValues(alpha: 0.3)),
                    ),
                    child: Row(
                      children: [
                        const Icon(Icons.dns_rounded, size: 14, color: BrandColors.emerald),
                        const SizedBox(width: 8),
                        Text(
                          widget.str.splitResolvedIp,
                          style: TextStyle(fontSize: 11, color: c.textMuted),
                        ),
                        Text(
                          _resolvedIp!,
                          style: const TextStyle(
                            fontSize: 11.5,
                            fontWeight: FontWeight.w600,
                            color: BrandColors.emerald,
                            fontFamily: 'Consolas',
                          ),
                        ),
                      ],
                    ),
                  ),
                ),

              if (_err != null)
                Padding(
                  padding: const EdgeInsets.only(top: 10),
                  child: Container(
                    padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 7),
                    decoration: BoxDecoration(
                      color: BrandColors.danger.withValues(alpha: 0.1),
                      borderRadius: BorderRadius.circular(8),
                      border: Border.all(color: BrandColors.danger.withValues(alpha: 0.3)),
                    ),
                    child: Row(
                      children: [
                        const Icon(Icons.error_outline_rounded, size: 14, color: BrandColors.danger),
                        const SizedBox(width: 8),
                        Expanded(
                          child: Text(
                            _err!,
                            style: const TextStyle(fontSize: 11, color: BrandColors.danger),
                          ),
                        ),
                      ],
                    ),
                  ),
                ),

              const SizedBox(height: 20),

              Row(
                children: [
                  if (!_isIp)
                    OutlinedButton.icon(
                      onPressed: _resolving ? null : _resolve,
                      icon: const Icon(Icons.travel_explore_rounded, size: 14),
                      label: Text(widget.str.splitResolveDns, style: const TextStyle(fontSize: 12)),
                      style: OutlinedButton.styleFrom(
                        foregroundColor: c.textSecondary,
                        side: BorderSide(color: c.border.withValues(alpha: 0.6)),
                        padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 10),
                        shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(8)),
                      ),
                    ),
                  const Spacer(),
                  TextButton(
                    onPressed: () => Navigator.pop(context),
                    style: TextButton.styleFrom(
                      foregroundColor: BrandColors.danger,
                      padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 10),
                    ),
                    child: Text(widget.str.cancel, style: const TextStyle(fontSize: 12.5, fontWeight: FontWeight.w600)),
                  ),
                  const SizedBox(width: 8),
                  FilledButton.icon(
                    onPressed: _submit,
                    icon: const Icon(Icons.add_rounded, size: 16),
                    label: Text(widget.str.splitAddRuleBtn, style: const TextStyle(fontSize: 12.5, fontWeight: FontWeight.w600)),
                    style: FilledButton.styleFrom(
                      backgroundColor: BrandColors.accentCyan,
                      foregroundColor: Colors.black,
                      padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 10),
                      shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(8)),
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

class _ModernSubdomainCheckboxCard extends StatefulWidget {
  const _ModernSubdomainCheckboxCard({
    required this.str,
    required this.checked,
    required this.onChanged,
  });

  final AppStrings str;
  final bool checked;
  final ValueChanged<bool> onChanged;

  @override
  State<_ModernSubdomainCheckboxCard> createState() => _ModernSubdomainCheckboxCardState();
}

class _ModernSubdomainCheckboxCardState extends State<_ModernSubdomainCheckboxCard> {
  bool _hovered = false;

  @override
  Widget build(BuildContext context) {
    final c = Theme.of(context).extension<AppColors>()!;
    final checked = widget.checked;

    return MouseRegion(
      cursor: SystemMouseCursors.click,
      onEnter: (_) => setState(() => _hovered = true),
      onExit: (_) => setState(() => _hovered = false),
      child: GestureDetector(
        behavior: HitTestBehavior.opaque,
        onTap: () => widget.onChanged(!checked),
        child: AnimatedContainer(
          duration: const Duration(milliseconds: 150),
          padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 11),
          decoration: BoxDecoration(
            color: checked
                ? BrandColors.accentCyan.withValues(alpha: _hovered ? 0.08 : 0.05)
                : _hovered
                    ? c.cardElevated.withValues(alpha: 0.6)
                    : c.input.withValues(alpha: 0.3),
            borderRadius: BorderRadius.circular(10),
            border: Border.all(
              color: checked
                  ? BrandColors.accentCyan.withValues(alpha: _hovered ? 0.55 : 0.35)
                  : _hovered
                      ? c.border.withValues(alpha: 0.8)
                      : c.border.withValues(alpha: 0.4),
              width: 1.2,
            ),
          ),
          child: Row(
            children: [
              _CustomCheckbox(
                checked: checked,
                onChanged: (v) => widget.onChanged(v),
              ),
              const SizedBox(width: 12),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    Text(
                      widget.str.splitIncludeSubdomains,
                      style: TextStyle(
                        fontSize: 12.5,
                        fontWeight: FontWeight.w600,
                        color: checked ? c.textPrimary : c.textSecondary,
                      ),
                    ),
                    const SizedBox(height: 2),
                    Text(
                      widget.str.splitIncludeSubdomainsDesc,
                      style: TextStyle(
                        fontSize: 11,
                        color: c.textMuted,
                      ),
                    ),
                  ],
                ),
              ),
              const SizedBox(width: 8),
              Container(
                padding: const EdgeInsets.symmetric(horizontal: 7, vertical: 2.5),
                decoration: BoxDecoration(
                  color: checked
                      ? BrandColors.accentCyan.withValues(alpha: 0.15)
                      : c.input.withValues(alpha: 0.5),
                  borderRadius: BorderRadius.circular(5),
                  border: Border.all(
                    color: checked
                        ? BrandColors.accentCyan.withValues(alpha: 0.35)
                        : c.border.withValues(alpha: 0.3),
                  ),
                ),
                child: Text(
                  checked ? widget.str.splitWildcardBadge : widget.str.splitExactOnly,
                  style: TextStyle(
                    fontSize: 10,
                    fontWeight: FontWeight.w600,
                    fontFamily: 'Consolas',
                    color: checked ? BrandColors.accentCyan : c.textMuted,
                  ),
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

class _RuleTypeSegmentButton extends StatelessWidget {
  const _RuleTypeSegmentButton({
    required this.str,
    required this.isIp,
    required this.onChanged,
  });

  final AppStrings str;
  final bool isIp;
  final ValueChanged<bool> onChanged;

  @override
  Widget build(BuildContext context) {
    final c = Theme.of(context).extension<AppColors>()!;
    return Container(
      padding: const EdgeInsets.all(3),
      decoration: BoxDecoration(
        color: c.input.withValues(alpha: 0.4),
        borderRadius: BorderRadius.circular(10),
        border: Border.all(color: c.border.withValues(alpha: 0.4)),
      ),
      child: Row(
        children: [
          Expanded(
            child: _SegmentItem(
              icon: Icons.language_rounded,
              title: str.splitDomainFqdn,
              isSelected: !isIp,
              onTap: () => onChanged(false),
            ),
          ),
          const SizedBox(width: 4),
          Expanded(
            child: _SegmentItem(
              icon: Icons.alt_route_rounded,
              title: str.splitIpCidr,
              isSelected: isIp,
              onTap: () => onChanged(true),
            ),
          ),
        ],
      ),
    );
  }
}

class _SegmentItem extends StatefulWidget {
  const _SegmentItem({
    required this.icon,
    required this.title,
    required this.isSelected,
    required this.onTap,
  });

  final IconData icon;
  final String title;
  final bool isSelected;
  final VoidCallback onTap;

  @override
  State<_SegmentItem> createState() => _SegmentItemState();
}

class _SegmentItemState extends State<_SegmentItem> {
  bool _hovered = false;

  @override
  Widget build(BuildContext context) {
    final c = Theme.of(context).extension<AppColors>()!;
    final isSelected = widget.isSelected;

    return MouseRegion(
      cursor: SystemMouseCursors.click,
      onEnter: (_) => setState(() => _hovered = true),
      onExit: (_) => setState(() => _hovered = false),
      child: GestureDetector(
        onTap: widget.onTap,
        child: AnimatedContainer(
          duration: const Duration(milliseconds: 140),
          padding: const EdgeInsets.symmetric(vertical: 8),
          decoration: BoxDecoration(
            color: isSelected
                ? BrandColors.accentCyan.withValues(alpha: 0.15)
                : _hovered
                    ? c.cardElevated.withValues(alpha: 0.7)
                    : Colors.transparent,
            borderRadius: BorderRadius.circular(7),
            border: Border.all(
              color: isSelected
                  ? BrandColors.accentCyan.withValues(alpha: 0.5)
                  : Colors.transparent,
            ),
          ),
          child: Row(
            mainAxisAlignment: MainAxisAlignment.center,
            children: [
              Icon(
                widget.icon,
                size: 15,
                color: isSelected ? BrandColors.accentCyan : (_hovered ? c.textPrimary : c.textMuted),
              ),
              const SizedBox(width: 7),
              Text(
                widget.title,
                style: TextStyle(
                  fontSize: 12,
                  fontWeight: isSelected ? FontWeight.w600 : FontWeight.w500,
                  color: isSelected ? BrandColors.accentCyan : (_hovered ? c.textPrimary : c.textSecondary),
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

class _SplitAppTile extends ConsumerStatefulWidget {
  const _SplitAppTile({required this.entry, required this.onRemove});
  final SplitTunnelEntry entry;
  final VoidCallback onRemove;

  @override
  ConsumerState<_SplitAppTile> createState() => _SplitAppTileState();
}

class _SplitAppTileState extends ConsumerState<_SplitAppTile> {
  bool _hovered = false;

  @override
  Widget build(BuildContext context) {
    final c = Theme.of(context).extension<AppColors>()!;
    final client = ref.watch(coreClientProvider);

    String displayName = widget.entry.value;
    String? iconBase64;
    IconData fallbackIcon = Icons.extension_rounded;

    if (client is PipeCoreClient && client.cachedInstalledApps != null) {
      final match = client.cachedInstalledApps!.firstWhere(
        (m) => (m['exe']?.toString().toLowerCase() ?? '') == widget.entry.value.toLowerCase(),
        orElse: () => const {},
      );
      if (match.isNotEmpty) {
        displayName = match['name']?.toString() ?? widget.entry.value;
        iconBase64 = match['icon']?.toString();
      }
    }

    if (iconBase64 == null || iconBase64.isEmpty) {
      final known = kPopularApps.cast<KnownApp?>().firstWhere(
        (a) => a!.exe.toLowerCase() == widget.entry.value.toLowerCase(),
        orElse: () => null,
      );
      if (known != null) {
        if (displayName == widget.entry.value) displayName = known.name;
        fallbackIcon = known.icon;
      }
    }

    return MouseRegion(
      onEnter: (_) => setState(() => _hovered = true),
      onExit: (_) => setState(() => _hovered = false),
      child: AnimatedContainer(
        duration: const Duration(milliseconds: 140),
        height: 42,
        padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 6),
        decoration: BoxDecoration(
          color: _hovered
              ? c.cardElevated.withValues(alpha: 0.95)
              : c.input.withValues(alpha: 0.7),
          borderRadius: BorderRadius.circular(10),
          border: Border.all(
            color: _hovered
                ? BrandColors.accentCyan.withValues(alpha: 0.5)
                : c.border.withValues(alpha: 0.55),
          ),
          boxShadow: _hovered
              ? [
                  BoxShadow(
                    color: Colors.black.withValues(alpha: 0.05),
                    blurRadius: 4,
                    offset: const Offset(0, 1),
                  ),
                ]
              : null,
        ),
        child: Row(
          children: [
            Container(
              width: 24,
              height: 24,
              decoration: BoxDecoration(
                color: BrandColors.accentCyan.withValues(alpha: 0.12),
                borderRadius: BorderRadius.circular(6),
              ),
              child: (iconBase64 != null && iconBase64.isNotEmpty)
                  ? Image.memory(
                      base64Decode(iconBase64),
                      width: 16,
                      height: 16,
                      fit: BoxFit.contain,
                      errorBuilder: (ctx, err, stack) => Icon(
                        fallbackIcon,
                        size: 14,
                        color: BrandColors.accentCyan,
                      ),
                    )
                  : Icon(
                      fallbackIcon,
                      size: 14,
                      color: BrandColors.accentCyan,
                    ),
            ),
            const SizedBox(width: 8),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                mainAxisAlignment: MainAxisAlignment.center,
                children: [
                  Text(
                    displayName,
                    style: TextStyle(
                      fontSize: 12,
                      fontWeight: FontWeight.w600,
                      color: c.textPrimary,
                    ),
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                  ),
                  Text(
                    widget.entry.value,
                    style: AppTheme.mono(c.textMuted, size: 9.5, weight: FontWeight.w400),
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                  ),
                ],
              ),
            ),
            const SizedBox(width: 6),
            MouseRegion(
              cursor: SystemMouseCursors.click,
              child: InkWell(
                borderRadius: BorderRadius.circular(6),
                onTap: widget.onRemove,
                child: Padding(
                  padding: const EdgeInsets.all(3),
                  child: Icon(
                    Icons.close_rounded,
                    size: 14,
                    color: _hovered ? BrandColors.danger : c.textMuted,
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
