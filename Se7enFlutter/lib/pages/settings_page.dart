import 'dart:io';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:window_manager/window_manager.dart';

import '../core/i18n/app_strings.dart';
import '../core/models/connection_method.dart';
import '../core/models/user_settings.dart';
import '../core/services/core_update_service.dart';
import '../core/services/providers.dart';
import '../core/services/tray_service.dart';
import '../theme/app_colors.dart';
import '../theme/app_theme.dart';
import '../widgets/glass_controls.dart';
import '../widgets/psiphon_over_v2ray_section.dart';

class SettingsPage extends ConsumerStatefulWidget {
  const SettingsPage({super.key});

  @override
  ConsumerState<SettingsPage> createState() => _SettingsPageState();
}

class _SettingsPageState extends ConsumerState<SettingsPage> {
  @override
  Widget build(BuildContext context) {
    final s = ref.watch(settingsProvider);
    final str = ref.watch(stringsProvider);
    final c = Theme.of(context).extension<AppColors>()!;
    final currentTab = ref.watch(currentSettingsTabProvider);
    void set(void Function(UserSettings) edit) =>
        ref.read(settingsProvider.notifier).update(edit);

    final tabs = [
      (SettingsTab.general, str.tabGeneral, Icons.tune_rounded),
      (SettingsTab.network, str.tabNetwork, Icons.lan_outlined),
      (SettingsTab.psiphon, str.tabPsiphon, Icons.vpn_lock_rounded),
      (SettingsTab.aether, str.tabAether, Icons.bolt_rounded),
      (SettingsTab.tor, str.tabTor, Icons.shield_moon_rounded),
      (SettingsTab.shard, str.tabShard, Icons.hub_rounded),
      (SettingsTab.chained, str.tabChained, Icons.alt_route_rounded),
    ];

    return Column(
      children: [

        Padding(
          padding: const EdgeInsets.symmetric(horizontal: 24, vertical: 8),
          child: Row(
            children: [
              for (var i = 0; i < tabs.length; i++) ...[
                if (i > 0) const SizedBox(width: 8),
                Expanded(
                  child: _TabButton(
                    label: tabs[i].$2,
                    icon: tabs[i].$3,
                    selected: currentTab == tabs[i].$1,
                    onTap: () => ref.read(currentSettingsTabProvider.notifier).state = tabs[i].$1,
                  ),
                ),
              ],
            ],
          ),
        ),

        const SizedBox(height: 6),

        Expanded(
          child: ListView(
            padding: const EdgeInsets.fromLTRB(24, 6, 24, 24),
            children: [
              switch (currentTab) {
                SettingsTab.general => _GeneralTab(s: s, set: set, c: c),
                SettingsTab.network => _NetworkTab(s: s, set: set, c: c),
                SettingsTab.psiphon => _PsiphonTab(s: s, set: set, c: c, str: str),
                SettingsTab.aether => _AetherTab(s: s, set: set, c: c, str: str),
                SettingsTab.tor => _TorTab(s: s, set: set, c: c, str: str),
                SettingsTab.shard => _ShardTab(s: s, set: set, c: c),
                SettingsTab.chained => _ChainedTab(s: s, set: set, c: c, str: str),
              },
            ],
          ),
        ),
      ],
    );
  }
}

class _TabButton extends StatefulWidget {
  const _TabButton({
    required this.label,
    required this.icon,
    required this.selected,
    required this.onTap,
  });

  final String label;
  final IconData icon;
  final bool selected;
  final VoidCallback onTap;

  @override
  State<_TabButton> createState() => _TabButtonState();
}

class _TabButtonState extends State<_TabButton> {
  bool _hovered = false;

  @override
  Widget build(BuildContext context) {
    final c = Theme.of(context).extension<AppColors>()!;
    final isSelected = widget.selected;

    return MouseRegion(
      cursor: SystemMouseCursors.click,
      onEnter: (_) => setState(() => _hovered = true),
      onExit: (_) => setState(() => _hovered = false),
      child: GestureDetector(
        onTap: widget.onTap,
        child: AnimatedContainer(
          duration: const Duration(milliseconds: 160),
          height: 38,
          padding: const EdgeInsets.symmetric(horizontal: 8),
          decoration: BoxDecoration(
            color: isSelected
                ? (c.isDark ? BrandColors.accentCyan.withValues(alpha: 0.20) : BrandColors.accentCyan)
                : (_hovered
                    ? (c.isDark ? c.cardElevated.withValues(alpha: 0.8) : BrandColors.accentCyan.withValues(alpha: 0.08))
                    : c.card.withValues(alpha: 0.45)),
            borderRadius: BorderRadius.circular(10),
            border: Border.all(
              color: isSelected
                  ? (c.isDark ? BrandColors.accentCyan.withValues(alpha: 0.55) : BrandColors.accentCyan)
                  : (_hovered
                      ? (c.isDark ? c.border.withValues(alpha: 0.8) : BrandColors.accentCyan.withValues(alpha: 0.30))
                      : c.border.withValues(alpha: 0.4)),
              width: 1,
            ),
            boxShadow: isSelected
                ? [
                    BoxShadow(
                      color: BrandColors.accentCyan.withValues(alpha: c.isDark ? 0.18 : 0.25),
                      blurRadius: 8,
                      offset: const Offset(0, 2),
                    ),
                  ]
                : null,
          ),
          child: Row(
            mainAxisAlignment: MainAxisAlignment.center,
            children: [
              Icon(
                widget.icon,
                size: 15,
                color: isSelected
                    ? (c.isDark ? BrandColors.accentCyan : Colors.white)
                    : (_hovered ? BrandColors.accentCyan : c.textSecondary),
              ),
              const SizedBox(width: 7),
              Flexible(
                child: Text(
                  widget.label,
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: TextStyle(
                    fontSize: 11.5,
                    fontWeight: isSelected ? FontWeight.w700 : FontWeight.w500,
                    color: isSelected
                        ? Colors.white
                        : (_hovered
                            ? (c.isDark ? Colors.white : BrandColors.accentCyan)
                            : c.textSecondary),
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

class _GeneralTab extends ConsumerWidget {
  const _GeneralTab({required this.s, required this.set, required this.c});
  final UserSettings s;
  final void Function(void Function(UserSettings)) set;
  final AppColors c;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final str = ref.watch(stringsProvider);
    final currentLang = (s.language == 'ru') ? 'ru' : (s.language == 'zh' ? 'zh' : 'en');

    return Column(
      children: [
        GlassCard(
          borderRadius: 14,
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              _SectionHeader(title: str.sectionAppearanceLocale, icon: Icons.palette_outlined),
              const SizedBox(height: 8),
              GlassDropdownRow<String>(
                label: str.appearanceMode,
                description: str.appearanceModeDesc,
                value: s.theme,
                items: {
                  'dark': str.themeDark,
                  'light': str.themeLight,
                },
                onChanged: (v) => set((x) => x.theme = v),
              ),
              GlassDropdownRow<String>(
                label: str.languageLabel,
                description: str.languageDesc,
                value: currentLang,
                items: const {'en': 'English', 'ru': 'Русский', 'zh': '简体中文'},
                onChanged: (v) => set((x) => x.language = v),
              ),
            ],
          ),
        ),
        const SizedBox(height: 12),
        GlassCard(
          borderRadius: 14,
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              _SectionHeader(title: str.sectionSystemStartup, icon: Icons.desktop_windows_outlined),
              const SizedBox(height: 8),
              GlassSwitchRow(
                label: str.autoConnectLaunch,
                description: str.autoConnectDesc,
                value: s.autoConnect,
                onChanged: (v) => set((x) => x.autoConnect = v),
              ),
              GlassSwitchRow(
                label: str.startWithWindows,
                description: str.startWithWindowsDesc,
                value: s.startWithWindows,
                onChanged: (v) => set((x) => x.startWithWindows = v),
              ),
              GlassSwitchRow(
                label: str.minimizeToTray,
                description: str.minimizeToTrayDesc,
                value: s.minimizeToTray,
                onChanged: (v) => set((x) => x.minimizeToTray = v),
              ),
              GlassDropdownRow<String>(
                label: str.closeActionLabel,
                description: str.closeActionDesc,
                value: s.onCloseAction,
                items: {
                  'ask': str.closeActionAsk,
                  'tray': str.closeActionTray,
                  'exit': str.closeActionExit,
                },
                onChanged: (v) => set((x) => x.onCloseAction = v),
              ),
            ],
          ),
        ),
        const SizedBox(height: 12),
        _CoreUpdatesCard(c: c),
      ],
    );
  }
}

class _NetworkTab extends ConsumerWidget {
  const _NetworkTab({required this.s, required this.set, required this.c});
  final UserSettings s;
  final void Function(void Function(UserSettings)) set;
  final AppColors c;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final str = ref.watch(stringsProvider);

    return Column(
      children: [

        GlassCard(
          borderRadius: 14,
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              _SectionHeader(title: str.sectionNetworkRouting, icon: Icons.security_rounded),
              const SizedBox(height: 8),
              GlassSwitchRow(
                label: str.killSwitch,
                description: str.killSwitchDesc,
                value: s.killSwitchEnabled,
                onChanged: (v) => set((x) => x.killSwitchEnabled = v),
              ),
            ],
          ),
        ),

        const SizedBox(height: 12),

        GlassCard(
          borderRadius: 14,
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              _SectionHeader(title: str.sectionLocalProxy, icon: Icons.lan_outlined),
              const SizedBox(height: 4),
              Padding(
                padding: const EdgeInsets.only(left: 2, bottom: 12),
                child: Text(
                  str.localProxySubtitle,
                  style: TextStyle(fontSize: 11.5, color: c.textMuted),
                ),
              ),

              _LocalProxyPortsSection(s: s, set: set, c: c, str: str),

              const SizedBox(height: 14),

              GlassSwitchRow(
                label: str.allowLan,
                description: str.allowLanDesc,
                value: s.allowLanConnections,
                onChanged: (v) => set((x) => x.allowLanConnections = v),
              ),

              if (s.allowLanConnections) ...[
                const SizedBox(height: 12),
                _LanSharingInfoBox(s: s, c: c, str: str),
              ],
            ],
          ),
        ),
      ],
    );
  }
}

class _LocalProxyPortsSection extends StatelessWidget {
  const _LocalProxyPortsSection({
    required this.s,
    required this.set,
    required this.c,
    required this.str,
  });

  final UserSettings s;
  final void Function(void Function(UserSettings)) set;
  final AppColors c;
  final AppStrings str;

  @override
  Widget build(BuildContext context) {
    final customEnabled = s.useCustomProxyPorts;
    final hasPortCollision = customEnabled &&
        s.localSocksProxyPort > 0 &&
        s.localHttpProxyPort > 0 &&
        s.localSocksProxyPort == s.localHttpProxyPort;

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        InkWell(
          onTap: () => set((x) => x.useCustomProxyPorts = !customEnabled),
          borderRadius: BorderRadius.circular(8),
          hoverColor: c.isDark ? Colors.white.withValues(alpha: 0.03) : Colors.black.withValues(alpha: 0.02),
          child: Padding(
            padding: const EdgeInsets.symmetric(vertical: 8, horizontal: 4),
            child: Row(
              children: [
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        str.customProxyPorts,
                        style: TextStyle(
                          fontSize: 13,
                          fontWeight: FontWeight.w600,
                          color: c.textPrimary,
                        ),
                      ),
                      if (str.customProxyPortsDesc.isNotEmpty) ...[
                        const SizedBox(height: 2),
                        Text(
                          str.customProxyPortsDesc,
                          style: TextStyle(fontSize: 11, color: c.textMuted),
                        ),
                      ],
                    ],
                  ),
                ),
                const SizedBox(width: 8),
                if (!customEnabled)
                  Container(
                    padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 3.5),
                    decoration: BoxDecoration(
                      color: BrandColors.emerald.withValues(alpha: 0.12),
                      borderRadius: BorderRadius.circular(6),
                      border: Border.all(color: BrandColors.emerald.withValues(alpha: 0.3)),
                    ),
                    child: const Text(
                      'Default Auto',
                      style: TextStyle(
                        fontSize: 10.5,
                        fontWeight: FontWeight.w700,
                        color: BrandColors.emerald,
                      ),
                    ),
                  ),
                const SizedBox(width: 4),
                Transform.scale(
                  scale: 0.85,
                  child: IgnorePointer(
                    child: Switch(
                      value: customEnabled,
                      onChanged: (_) {},
                    ),
                  ),
                ),
              ],
            ),
          ),
        ),
        if (customEnabled) ...[
          const SizedBox(height: 8),
          Container(
            padding: const EdgeInsets.all(14),
            decoration: BoxDecoration(
              color: c.input.withValues(alpha: 0.45),
              borderRadius: BorderRadius.circular(12),
              border: Border.all(
                color: hasPortCollision
                    ? BrandColors.danger.withValues(alpha: 0.8)
                    : c.border.withValues(alpha: 0.5),
                width: hasPortCollision ? 1.4 : 1.0,
              ),
            ),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Row(
                  children: [
                    Container(
                      padding: const EdgeInsets.all(5),
                      decoration: BoxDecoration(
                        color: BrandColors.accentCyan.withValues(alpha: 0.15),
                        borderRadius: BorderRadius.circular(6),
                      ),
                      child: const Icon(Icons.tune_rounded, size: 13, color: BrandColors.accentCyan),
                    ),
                    const SizedBox(width: 8),
                    Text(
                      str.customPortAssignment,
                      style: TextStyle(fontSize: 12, fontWeight: FontWeight.w700, color: c.textPrimary),
                    ),
                    const Spacer(),
                    Container(
                      padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 2),
                      decoration: BoxDecoration(
                        color: c.input.withValues(alpha: 0.6),
                        borderRadius: BorderRadius.circular(5),
                      ),
                      child: Text(str.autoAssignZero, style: TextStyle(fontSize: 10, color: c.textMuted)),
                    ),
                  ],
                ),
                const SizedBox(height: 12),
                Row(
                  children: [
                    Expanded(
                      child: _LanInputField(
                        label: str.socks5Port,
                        hint: '10808 (0 = Auto)',
                        value: s.localSocksProxyPort == 0 ? '' : '${s.localSocksProxyPort}',
                        icon: Icons.cable_rounded,
                        isPortField: true,
                        helperBadge: '1–65535',
                        keyboardType: TextInputType.number,
                        onChanged: (v) {
                          final p = int.tryParse(v.trim()) ?? 0;
                          if (p >= 0 && p <= 65535) set((x) => x.localSocksProxyPort = p);
                        },
                      ),
                    ),
                    const SizedBox(width: 14),
                    Expanded(
                      child: _LanInputField(
                        label: str.httpPort,
                        hint: '10809 (0 = Auto)',
                        value: s.localHttpProxyPort == 0 ? '' : '${s.localHttpProxyPort}',
                        icon: Icons.language_rounded,
                        isPortField: true,
                        helperBadge: '1–65535',
                        keyboardType: TextInputType.number,
                        onChanged: (v) {
                          final p = int.tryParse(v.trim()) ?? 0;
                          if (p >= 0 && p <= 65535) set((x) => x.localHttpProxyPort = p);
                        },
                      ),
                    ),
                  ],
                ),
                if (hasPortCollision)
                  Padding(
                    padding: const EdgeInsets.only(top: 10),
                    child: Container(
                      padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 6),
                      decoration: BoxDecoration(
                        color: BrandColors.danger.withValues(alpha: 0.12),
                        borderRadius: BorderRadius.circular(6),
                        border: Border.all(color: BrandColors.danger.withValues(alpha: 0.4)),
                      ),
                      child: Row(
                        children: [
                          const Icon(Icons.error_outline_rounded, size: 14, color: BrandColors.danger),
                          const SizedBox(width: 8),
                          Expanded(
                            child: Text(
                              str.portCollisionWarning,
                              style: const TextStyle(fontSize: 11, fontWeight: FontWeight.w500, color: BrandColors.danger),
                            ),
                          ),
                        ],
                      ),
                    ),
                  ),
              ],
            ),
          ),
        ],
        const SizedBox(height: 6),
        GlassSwitchRow(
          label: str.lanAccessAuth,
          value: s.lanAuthEnabled,
          onChanged: (v) => set((x) => x.lanAuthEnabled = v),
        ),
        if (s.lanAuthEnabled) ...[
          const SizedBox(height: 8),
          Container(
            padding: const EdgeInsets.all(14),
            decoration: BoxDecoration(
              color: c.input.withValues(alpha: 0.45),
              borderRadius: BorderRadius.circular(12),
              border: Border.all(color: c.border.withValues(alpha: 0.5)),
            ),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Row(
                  children: [
                    Container(
                      padding: const EdgeInsets.all(5),
                      decoration: BoxDecoration(
                        color: BrandColors.accentPurple.withValues(alpha: 0.15),
                        borderRadius: BorderRadius.circular(6),
                      ),
                      child: const Icon(Icons.shield_outlined, size: 13, color: BrandColors.accentPurple),
                    ),
                    const SizedBox(width: 8),
                    Text(
                      str.lanAccessAuth,
                      style: TextStyle(fontSize: 12, fontWeight: FontWeight.w700, color: c.textPrimary),
                    ),
                    const Spacer(),
                    Container(
                      padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 2),
                      decoration: BoxDecoration(
                        color: c.input.withValues(alpha: 0.6),
                        borderRadius: BorderRadius.circular(5),
                      ),
                      child: Text(str.optionalBadge, style: TextStyle(fontSize: 10, color: c.textMuted)),
                    ),
                  ],
                ),
                const SizedBox(height: 12),
                Row(
                  children: [
                    Expanded(
                      child: _LanInputField(
                        label: str.lanUsername,
                        hint: 'Username',
                        value: s.lanProxyUsername,
                        icon: Icons.person_outline_rounded,
                        onChanged: (v) => set((x) => x.lanProxyUsername = v),
                      ),
                    ),
                    const SizedBox(width: 14),
                    Expanded(
                      child: _LanInputField(
                        label: str.lanPassword,
                        hint: 'Password',
                        value: s.lanProxyPassword,
                        icon: Icons.lock_outline_rounded,
                        obscure: true,
                        onChanged: (v) => set((x) => x.lanProxyPassword = v),
                      ),
                    ),
                  ],
                ),
              ],
            ),
          ),
        ],
      ],
    );
  }
}

class _PsiphonTab extends StatelessWidget {
  const _PsiphonTab({required this.s, required this.set, required this.c, required this.str});
  final UserSettings s;
  final void Function(void Function(UserSettings)) set;
  final AppColors c;
  final AppStrings str;

  @override
  Widget build(BuildContext context) {
    final isUpstreamUnsupported = s.protocolMode == 'conduit' || s.protocolMode == 'cdn_fronting';

    String host = '';
    String port = '';
    if (s.upstreamProxy.isNotEmpty) {
      final raw = s.upstreamProxy.trim();
      final uri = Uri.tryParse(raw.contains('://') ? raw : 'http://$raw');
      if (uri != null && uri.host.isNotEmpty) {
        host = uri.host;
        if (uri.hasPort) port = uri.port.toString();
      } else {
        final idx = raw.lastIndexOf(':');
        if (idx > 0) { host = raw.substring(0, idx); port = raw.substring(idx + 1); } else { host = raw; }
      }
    }

    void updateUpstream(String newHost, String newPort) {
      final h = newHost.trim();
      final p = newPort.trim();

      final needsBracket = h.contains(':') && !h.startsWith('[');
      final hostOut = needsBracket ? '[$h]' : h;
      set((x) => x.upstreamProxy = p.isNotEmpty ? '$hostOut:$p' : h);
    }

    return Column(
      children: [

        GlassCard(
          borderRadius: 14,
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              _SectionHeader(title: str.psiphonControlsTitle, icon: Icons.vpn_lock_rounded),
              const SizedBox(height: 8),
              GlassDropdownRow<String>(
                label: str.protocolModeLabel,
                value: s.protocolMode,
                items: {
                  'auto': str.protocolModeAuto,
                  'direct': str.protocolModeDirect,
                  'cdn_fronting': str.protocolModeCdn,
                  'conduit': str.protocolModeConduit,
                },
                onChanged: (v) {
                  set((x) {
                    x.protocolMode = v;

                    x.beastMode = false;
                    x.autoFindIpAndSni = false;
                    x.conduitRejectCensoredCountries = false;
                  });
                },
              ),

              if (s.protocolMode == 'direct') ...[
                GlassSwitchRow(
                  label: str.beastModeLabel,
                  description: str.beastModeDesc,
                  value: s.beastMode,
                  onChanged: (v) => set((x) => x.beastMode = v),
                ),
              ],

              if (s.protocolMode == 'cdn_fronting') ...[
                GlassSwitchRow(
                  label: str.beastModeLabel,
                  description: str.beastModeDesc,
                  value: s.beastMode,
                  onChanged: (v) => set((x) => x.beastMode = v),
                ),
                GlassSwitchRow(
                  label: str.skipCertVerifyLabel,
                  description: str.skipCertVerifyDesc,
                  value: s.cdnFrontingSkipCertVerify,
                  onChanged: (v) => set((x) => x.cdnFrontingSkipCertVerify = v),
                ),
                GlassSwitchRow(
                  label: str.autoFindCleanIpLabel,
                  description: str.autoFindCleanIpDesc,
                  value: s.autoFindIpAndSni,
                  onChanged: (v) => set((x) => x.autoFindIpAndSni = v),
                ),
                if (s.autoFindIpAndSni) ...[
                  const SizedBox(height: 10),
                  _CdnScanCorpusSection(s: s, set: set, str: str),
                ],
                GlassSwitchRow(
                  label: str.saveFoundIpLabel,
                  description: str.saveFoundIpDesc,
                  value: s.saveFoundIpsAndSni,
                  onChanged: (v) => set((x) => x.saveFoundIpsAndSni = v),
                ),
                GlassDropdownRow<int>(
                  label: str.establishTimeoutLabel,
                  description: str.establishTimeoutDesc,
                  value: s.establishTunnelTimeoutSeconds ?? 300,
                  items: {
                    0: str.timeoutForever,
                    120: str.timeout2Min,
                    300: str.timeout5Min,
                    600: str.timeout10Min,
                    1800: str.timeout30Min,
                  },
                  onChanged: (v) => set((x) => x.establishTunnelTimeoutSeconds = v),
                ),
                const SizedBox(height: 12),
                Row(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Expanded(
                      child: _LanMultiLineField(
                        label: str.customCdnIpsLabel,
                        description: str.customCdnIpsDesc,
                        hint: '104.16.0.1\n104.17.0.1\n162.159.192.1',
                        value: s.cdnFrontingCustomIpList,
                        icon: Icons.numbers_rounded,
                        height: 96,
                        onChanged: (v) => set((x) => x.cdnFrontingCustomIpList = v),
                      ),
                    ),
                    const SizedBox(width: 12),
                    Expanded(
                      child: _LanMultiLineField(
                        label: str.customSniLabel,
                        description: str.customSniDesc,
                        hint: 'speedtest.net\nfast.com\ncloudflare.com',
                        value: s.cdnFrontingCustomSni,
                        icon: Icons.language_rounded,
                        height: 96,
                        onChanged: (v) => set((x) => x.cdnFrontingCustomSni = v),
                      ),
                    ),
                  ],
                ),
              ],

              if (s.protocolMode == 'conduit') ...[
                GlassSwitchRow(
                  label: str.conduitRejectCensored,
                  description: str.conduitRejectCensoredDesc,
                  value: s.conduitRejectCensoredCountries,
                  onChanged: (v) => set((x) => x.conduitRejectCensoredCountries = v),
                ),
                GlassDropdownRow<String>(
                  label: str.conduitStationSelection,
                  description: str.conduitStationDesc,
                  value: s.conduitMode.isEmpty ? 'auto' : s.conduitMode,
                  items: {
                    'auto': str.conduitAuto,
                    'public': str.conduitPublic,
                    'peer': str.conduitPeer,
                  },
                  onChanged: (v) => set((x) => x.conduitMode = v),
                ),
                if (s.conduitMode == 'peer') ...[
                  Padding(
                    padding: const EdgeInsets.only(top: 8, bottom: 4),
                    child: _LanInputField(
                      label: str.conduitPeerToken,
                      hint: str.conduitPeerTokenHint,
                      value: s.conduitCompartmentId,
                      icon: Icons.vpn_key_rounded,
                      onChanged: (v) => set((x) => x.conduitCompartmentId = v),
                    ),
                  ),
                ],
              ],
            ],
          ),
        ),

        const SizedBox(height: 12),

        GlassCard(
          borderRadius: 14,
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              _SectionHeader(title: str.upstreamProxyTitle, icon: Icons.cloud_sync_rounded),
              const SizedBox(height: 8),
              if (isUpstreamUnsupported) ...[
                Container(
                  padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 10),
                  decoration: BoxDecoration(
                    color: BrandColors.amber.withValues(alpha: 0.12),
                    borderRadius: BorderRadius.circular(8),
                    border: Border.all(color: BrandColors.amber.withValues(alpha: 0.3)),
                  ),
                  child: Row(
                    children: [
                      const Icon(Icons.info_outline_rounded, size: 18, color: BrandColors.amber),
                      const SizedBox(width: 8),
                      Expanded(
                        child: Text(
                          s.protocolMode == 'conduit'
                              ? str.upstreamNotSupportedConduit
                              : str.upstreamNotSupportedCdn,
                          style: TextStyle(fontSize: 11.5, color: c.textMuted),
                        ),
                      ),
                    ],
                  ),
                ),
                const SizedBox(height: 10),
              ],
              Opacity(
                opacity: isUpstreamUnsupported ? 0.45 : 1.0,
                child: IgnorePointer(
                  ignoring: isUpstreamUnsupported,
                  child: GlassSwitchRow(
                    label: str.upstreamProxyEnable,
                    description: isUpstreamUnsupported
                        ? 'Disabled for ${s.protocolMode == 'conduit' ? 'Conduit' : 'CDN Fronting'} (direct connection required)'
                        : str.upstreamProxyEnableDescPsiphon,
                    value: isUpstreamUnsupported ? false : s.upstreamProxyEnabled,
                    onChanged: (v) => set((x) => x.upstreamProxyEnabled = v),
                  ),
                ),
              ),
              if (!isUpstreamUnsupported && s.upstreamProxyEnabled) ...[
                GlassDropdownRow<String>(
                  label: str.upstreamProxyScheme,
                  description: str.upstreamProxySchemeDesc,
                  value: s.upstreamProxyScheme,
                  items: {
                    'http': str.upstreamHttp,
                    'socks5': str.upstreamSocks5,
                  },
                  onChanged: (v) => set((x) => x.upstreamProxyScheme = v),
                ),
                const SizedBox(height: 12),

                Row(
                  children: [
                    Expanded(
                      child: _LanInputField(
                        label: str.upstreamHost,
                        hint: '127.0.0.1',
                        value: host,
                        icon: Icons.dns_rounded,
                        onChanged: (v) { host = v; updateUpstream(host, port); },
                      ),
                    ),
                    const SizedBox(width: 12),
                    Expanded(
                      child: _LanInputField(
                        label: str.upstreamPort,
                        hint: '10808',
                        value: port,
                        icon: Icons.numbers_rounded,
                        isPortField: true,
                        helperBadge: '1–65535',
                        keyboardType: TextInputType.number,
                        onChanged: (v) {
                          final p = int.tryParse(v.trim());
                          if (v.trim().isEmpty || (p != null && p >= 1 && p <= 65535)) { port = v.trim(); updateUpstream(host, port); }
                        },
                      ),
                    ),
                  ],
                ),
                const SizedBox(height: 12),
                Row(
                  children: [
                    Expanded(child: _LanInputField(label: str.upstreamAuthUser, hint: str.optionalBadge, value: s.upstreamProxyUsername, icon: Icons.person_outline_rounded, onChanged: (v) => set((x) => x.upstreamProxyUsername = v))),
                    const SizedBox(width: 12),
                    Expanded(child: _LanInputField(label: str.upstreamAuthPass, hint: str.optionalBadge, value: s.upstreamProxyPassword, icon: Icons.lock_outline_rounded, obscure: true, onChanged: (v) => set((x) => x.upstreamProxyPassword = v))),
                  ],
                ),
              ],
            ],
          ),
        ),
      ],
    );
  }
}

class _AetherTab extends StatefulWidget {
  const _AetherTab({required this.s, required this.set, required this.c, required this.str});
  final UserSettings s;
  final void Function(void Function(UserSettings)) set;
  final AppColors c;
  final AppStrings str;
  @override
  State<_AetherTab> createState() => _AetherTabState();
}

class _AetherTabState extends State<_AetherTab> {
  late TextEditingController _endpointCtrl;
  String get proto => widget.s.aetherProtocol;
  bool get isMasque => proto == 'masque';
  String currentEndpoint() => switch (proto) {
        'masque' => widget.s.aetherEndpointMasque,
        'wireguard' => widget.s.aetherEndpointWireguard,
        'warp' => widget.s.aetherEndpointWarp,
        'masque_on_masque' => widget.s.aetherEndpointMasqueOnMasque,
        _ => widget.s.aetherEndpointMasque,
      };
  void setEndpoint(String v) => widget.set((x) {
        switch (proto) {
          case 'masque': x.aetherEndpointMasque = v;
          case 'wireguard': x.aetherEndpointWireguard = v;
          case 'warp': x.aetherEndpointWarp = v;
          case 'masque_on_masque': x.aetherEndpointMasqueOnMasque = v;
        }
      });
  String endpointHint() => switch (proto) {
        'masque' => 'engage.cloudflareclient.com:2408',
        'wireguard' => '162.159.193.1:2408',
        'warp' => '162.159.192.1:2408',
        'masque_on_masque' => 'engage.cloudflareclient.com:443',
        _ => 'engage.cloudflareclient.com:2408',
      };
  @override
  void initState() { super.initState(); _endpointCtrl = TextEditingController(text: currentEndpoint()); }
  @override
  void didUpdateWidget(covariant _AetherTab oldWidget) {
    super.didUpdateWidget(oldWidget);
    final cur = currentEndpoint();
    if (cur != _endpointCtrl.text && cur != _oldEndpoint(oldWidget)) {
      final sel = _endpointCtrl.selection;
      _endpointCtrl.text = cur;
      try { _endpointCtrl.selection = sel; } catch (_) {}
    }
  }
  String _oldEndpoint(_AetherTab w) => switch (w.s.aetherProtocol) {
        'masque' => w.s.aetherEndpointMasque,
        'wireguard' => w.s.aetherEndpointWireguard,
        'warp' => w.s.aetherEndpointWarp,
        'masque_on_masque' => w.s.aetherEndpointMasqueOnMasque,
        _ => w.s.aetherEndpointMasque,
      };
  @override
  void dispose() { _endpointCtrl.dispose(); super.dispose(); }

  @override
  Widget build(BuildContext context) {
    final c = widget.c;
    final s = widget.s;

    return GlassCard(
      borderRadius: 14,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          _SectionHeader(title: widget.str.aetherSectionTitle, icon: Icons.bolt_rounded),
          const SizedBox(height: 8),

          GlassDropdownRow<String>(
            label: widget.str.aetherProtocolType,
            description: widget.str.aetherProtocolDesc,
            value: proto,
            items: const {
              'masque': 'MASQUE',
              'wireguard': 'WireGuard',
              'warp': 'Warp (WARP-on-WARP)',
              'masque_on_masque': 'Masque on Masque',
            },
            onChanged: (v) => widget.set((x) {
              x.aetherProtocol = v;
              final currentMethod = ConnectionMethodX.parse(x.connectionMethod);
              if (currentMethod.isAether) {
                x.connectionMethod = switch (v) {
                  'wireguard' => 'wireguard',
                  'warp' => 'warp_on_warp',
                  'masque_on_masque' => 'masque_on_masque',
                  _ => 'masque',
                };
              }
            }),
          ),

          GlassDropdownRow<String>(
            label: widget.str.aetherIpVersion,
            description: widget.str.aetherIpVersionDesc,
            value: s.aetherIpVersion,
            items: const {
              '4': 'IPv4',
              '6': 'IPv6',
              'dual': 'IPv4 + IPv6',
            },
            onChanged: (v) => widget.set((x) => x.aetherIpVersion = v),
          ),

          GlassDropdownRow<String>(
            label: widget.str.aetherScanMode,
            description: widget.str.aetherScanModeDesc,
            value: s.aetherScanMode,
            items: {
              'turbo': widget.str.aetherScanTurbo,
              'balanced': widget.str.aetherScanBalanced,
              'thorough': widget.str.aetherScanThorough,
              'stealth': widget.str.aetherScanStealth,
              'ironclad': widget.str.aetherScanIronclad,
            },
            onChanged: (v) => widget.set((x) => x.aetherScanMode = v),
          ),

          GlassDropdownRow<String>(
            label: widget.str.aetherNoise,
            description: widget.str.aetherNoiseDesc,
            value: s.aetherNoize,
            items: {
              'off': widget.str.aetherNoiseDisabled,
              'light': widget.str.aetherNoiseLight,
              'balanced': widget.str.aetherNoiseBalanced,
              'aggressive': widget.str.aetherNoiseAggressive,
            },
            onChanged: (v) => widget.set((x) => x.aetherNoize = v),
          ),

          if (proto == 'masque' || proto == 'masque_on_masque') ...[
            GlassSwitchRow(
              label: widget.str.aetherFragment,
              description: widget.str.aetherFragmentDesc,
              value: s.aetherFragment,
              onChanged: (v) => widget.set((x) => x.aetherFragment = v),
            ),
            GlassDropdownRow<String>(
              label: widget.str.aetherTransport,
              value: s.aetherMasqueTransport,
              items: const {'h3': 'HTTP/3 (QUIC / UDP)', 'h2': 'HTTP/2 (TCP fallback)'},
              onChanged: (v) => widget.set((x) => x.aetherMasqueTransport = v),
            ),
            GlassSwitchRow(
              label: 'QUIC v2 probe',
              description: 'Version-negotiation probe before handshake (fixes H3 where v1 blocked). Off = --no-quic-v2.',
              value: s.aetherQuicV2,
              onChanged: (v) => widget.set((x) => x.aetherQuicV2 = v),
            ),
          ],

          Padding(
            padding: const EdgeInsets.symmetric(vertical: 9),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  widget.str.aetherManualEndpoint,
                  style: TextStyle(
                    fontSize: 12.5,
                    fontWeight: FontWeight.w600,
                    color: c.textPrimary,
                  ),
                ),
                const SizedBox(height: 3),
                Text(
                  widget.str.aetherManualEndpointDesc(proto == 'masque' ? 'MASQUE' : proto == 'wireguard' ? 'WireGuard' : 'Warp'),
                  style: TextStyle(fontSize: 11, color: c.textMuted),
                ),
                const SizedBox(height: 7),
                Container(
                  height: 42,
                  alignment: Alignment.center,
                  decoration: BoxDecoration(
                    color: c.input.withValues(alpha: 0.65),
                    borderRadius: BorderRadius.circular(9),
                    border: Border.all(color: c.border.withValues(alpha: 0.6)),
                  ),
                  child: TextField(
                    controller: _endpointCtrl,
                    onChanged: setEndpoint,
                    style: TextStyle(fontSize: 12.5, color: c.textPrimary),
                    textAlignVertical: TextAlignVertical.center,
                    decoration: InputDecoration(
                      hintText: endpointHint(),
                      hintStyle: TextStyle(fontSize: 12, color: c.textMuted),
                      isDense: true,
                      isCollapsed: true,
                      contentPadding: const EdgeInsets.symmetric(horizontal: 12),
                      border: InputBorder.none,
                    ),
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

class _TorBridgesPresets {
  static const String meekCdn77 =
      'meek_lite 192.0.2.20:80 url=https://1603026938.rsc.cdn77.org front=www.phpmyadmin.net utls=HelloRandomizedALPN';

  static const String snowflakeCdn77 =
      'snowflake 192.0.2.3:80 2B280B23E1107BB62ABFC40DDCC8824814F80A72 '
      'url=https://1098762253.rsc.cdn77.org/ '
      'front=www.cdn77.com '
      'ice=stun:stun.antisip.com:3478,stun:stun.epygi.com:3478,stun:stun.uls.co.za:3478,stun:stun.voipgate.com:3478,stun:stun.mixvoip.com:3478,stun:stun.nextcloud.com:3478,stun:stun.bethesda.net:3478,stun:stun.nextcloud.com:443,stun:stun.sipgate.net:3478,stun:stun.sipgate.net:10000,stun:stun.sonetel.com:3478,stun:stun.voipia.net:3478 '
      'utls-imitate=hellorandomizedalpn';

  static const String obfs4Iat =
      'obfs4 212.83.43.95:443 BFE712113A72899AD685764B211FACD30FF52C31 cert=ayq0XzCwhpdysn5o0EyDUbmSOx3X/oTEbzDMvczHOdBJKlvIdHHLJGkZARtT4dcBFArPPg iat-mode=1\n'
      'obfs4 212.83.43.74:443 39562501228A4D5E27FCA4C0C81A01EE23AE3EE4 cert=PBwr+S8JTVZo6MPdHnkTwXJPILWADLqfMGoVvhZClMq/Urndyd42BwX9YFJHZnBB3H0XCw iat-mode=1';

  static const String obfs4Public =
      'obfs4 51.222.13.177:80 5EDAC3B810E12B01F6FD8050D2FD3E277B289A08 cert=2uplIpLQ0q9+0qMFrK5pkaYRDOe460LL9WHBvatgkuRr/SL31wBOEupaMMJ6koRE6Ld0ew iat-mode=0\n'
      'obfs4 37.218.245.14:38224 D9A82D2F9C2F65A18407B1D2B764F130847F8B5D cert=bjRaMrr1BRiAW8IE9U5z27fQaYgOhX1UCmOpg2pFpoMvo6ZgQMzLsaTzzQNTlm7hNcb+Sg iat-mode=0\n'
      'obfs4 45.145.95.6:27015 C5B7CD6946FF10C5B3E89691A7D3F2C122D2117C cert=TD7PbUO0/0k6xYHMPW3vJxICfkMZNdkRrb63Zhl5j9dW3iRGiCx0A7mPhe5T2EDzQ35+Zw iat-mode=0\n'
      'obfs4 209.148.46.65:443 74FAD13168806246602538555B5521A0383A1875 cert=ssH+9rP8dG2NLDN2XuFw63hIO/9MNNinLmxQDpVa+7kTOa9/m+tGWT1SmSYpQ9uTBGa6Hw iat-mode=0';
}

class _TorTab extends StatefulWidget {
  const _TorTab({required this.s, required this.set, required this.c, required this.str});
  final UserSettings s;
  final void Function(void Function(UserSettings)) set;
  final AppColors c;
  final AppStrings str;

  @override
  State<_TorTab> createState() => _TorTabState();
}

class _TorTabState extends State<_TorTab> {
  late final TextEditingController _ctrl;

  @override
  void initState() {
    super.initState();
    _ctrl = TextEditingController(text: widget.s.torBridges);
  }

  @override
  void didUpdateWidget(covariant _TorTab oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (widget.s.torBridges != _ctrl.text && widget.s.torBridges != oldWidget.s.torBridges) {
      _ctrl.text = widget.s.torBridges;
    }
  }

  @override
  void dispose() {
    _ctrl.dispose();
    super.dispose();
  }

  void _applyPreset(String text) {
    _ctrl.text = text;
    widget.set((x) => x.torBridges = text);
  }

  @override
  Widget build(BuildContext context) {
    final c = widget.c;

    return GlassCard(
      borderRadius: 14,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Container(
                width: 36,
                height: 36,
                decoration: BoxDecoration(
                  color: c.cardElevated.withValues(alpha: 0.6),
                  borderRadius: BorderRadius.circular(9),
                  border: Border.all(color: c.border.withValues(alpha: 0.6)),
                ),
                child: Icon(Icons.shield_moon_rounded, size: 20, color: BrandColors.accentCyan),
              ),
              const SizedBox(width: 12),
              Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(
                    widget.str.torOptionsTitle,
                    style: TextStyle(
                      fontSize: 13,
                      fontWeight: FontWeight.w700,
                      letterSpacing: 0.3,
                      color: c.textPrimary,
                    ),
                  ),
                  const SizedBox(height: 2),
                  Text(
                    widget.str.torOptionsDesc,
                    style: TextStyle(fontSize: 11, color: c.textMuted),
                  ),
                ],
              ),
            ],
          ),
          const SizedBox(height: 16),
          Text(
            widget.str.torPresetsTitle,
            style: TextStyle(
              fontSize: 11,
              fontWeight: FontWeight.w700,
              letterSpacing: 0.8,
              color: c.textMuted,
            ),
          ),
          const SizedBox(height: 8),

          Row(
            children: [
              Expanded(
                child: _TorPresetButton(
                  label: 'Meek CDN77',
                  onTap: () => _applyPreset(_TorBridgesPresets.meekCdn77),
                ),
              ),
              const SizedBox(width: 8),
              Expanded(
                child: _TorPresetButton(
                  label: 'Snowflake CDN77',
                  onTap: () => _applyPreset(_TorBridgesPresets.snowflakeCdn77),
                ),
              ),
              const SizedBox(width: 8),
              Expanded(
                child: _TorPresetButton(
                  label: widget.str.isZh ? 'obfs4 (抗时序)' : (widget.str.isRu ? 'obfs4 (анти-тайминг)' : 'obfs4 (Anti-timing)'),
                  onTap: () => _applyPreset(_TorBridgesPresets.obfs4Iat),
                ),
              ),
              const SizedBox(width: 8),
              Expanded(
                child: _TorPresetButton(
                  label: widget.str.isZh ? 'obfs4 (公共网桥)' : (widget.str.isRu ? 'obfs4 (публичные)' : 'obfs4 (Public)'),
                  onTap: () => _applyPreset(_TorBridgesPresets.obfs4Public),
                ),
              ),
            ],
          ),
          const SizedBox(height: 8),

          Row(
            children: [
              _TorPresetButton(
                label: widget.str.torClearBridges,
                icon: Icons.delete_sweep_rounded,
                isDestructive: true,
                onTap: () => _applyPreset(''),
              ),
            ],
          ),
          const SizedBox(height: 12),
          Container(
            height: 115,
            decoration: BoxDecoration(
              color: c.isDark ? const Color(0xFF14141E) : c.input.withValues(alpha: 0.65),
              borderRadius: BorderRadius.circular(10),
              border: Border.all(color: c.border.withValues(alpha: 0.6)),
            ),
            child: TextField(
              controller: _ctrl,
              onChanged: (v) => widget.set((x) => x.torBridges = v),
              maxLines: null,
              expands: true,
              keyboardType: TextInputType.multiline,
              style: TextStyle(
                fontFamily: 'Cascadia Mono',
                fontFamilyFallback: const ['Consolas', 'Courier New', 'monospace'],
                fontSize: 12,
                color: c.textPrimary,
              ),
              decoration: InputDecoration(
                hintText: widget.str.torBridgesInputHint,
                hintStyle: TextStyle(
                  fontFamily: 'Cascadia Mono',
                  fontFamilyFallback: const ['Consolas', 'Courier New', 'monospace'],
                  fontSize: 11.5,
                  color: c.textMuted,
                ),
                isDense: true,
                contentPadding: const EdgeInsets.all(12),
                border: InputBorder.none,
                enabledBorder: InputBorder.none,
                focusedBorder: InputBorder.none,
              ),
            ),
          ),
          const SizedBox(height: 6),
          Text(
            widget.str.torBridgesFootnote,
            style: TextStyle(fontSize: 11, color: c.textMuted),
          ),
        ],
      ),
    );
  }
}

class _TorPresetButton extends StatefulWidget {
  const _TorPresetButton({
    required this.label,
    required this.onTap,
    this.icon,
    this.isDestructive = false,
  });

  final String label;
  final VoidCallback onTap;
  final IconData? icon;
  final bool isDestructive;

  @override
  State<_TorPresetButton> createState() => _TorPresetButtonState();
}

class _TorPresetButtonState extends State<_TorPresetButton> {
  bool _hovered = false;

  @override
  Widget build(BuildContext context) {
    final c = Theme.of(context).extension<AppColors>()!;
    final isDestructive = widget.isDestructive;

    final baseBorder = isDestructive
        ? const Color(0xFFEF4444).withValues(alpha: 0.45)
        : c.border.withValues(alpha: 0.6);
    final hoverBorder = isDestructive
        ? const Color(0xFFEF4444).withValues(alpha: 0.85)
        : BrandColors.accentCyan.withValues(alpha: 0.7);

    final baseBg = isDestructive
        ? const Color(0xFFEF4444).withValues(alpha: 0.08)
        : c.cardElevated.withValues(alpha: 0.45);
    final hoverBg = isDestructive
        ? const Color(0xFFEF4444).withValues(alpha: 0.18)
        : (c.isDark ? BrandColors.accentCyan.withValues(alpha: 0.12) : BrandColors.accentCyan.withValues(alpha: 0.08));

    final textColor = isDestructive
        ? BrandColors.danger
        : (_hovered ? (c.isDark ? BrandColors.accentCyan : BrandColors.primary) : c.textPrimary);

    return MouseRegion(
      cursor: SystemMouseCursors.click,
      onEnter: (_) => setState(() => _hovered = true),
      onExit: (_) => setState(() => _hovered = false),
      child: GestureDetector(
        onTap: widget.onTap,
        child: AnimatedContainer(
          duration: const Duration(milliseconds: 140),
          height: 30,
          padding: const EdgeInsets.symmetric(horizontal: 12),
          decoration: BoxDecoration(
            color: _hovered ? hoverBg : baseBg,
            borderRadius: BorderRadius.circular(8),
            border: Border.all(color: _hovered ? hoverBorder : baseBorder),
          ),
          alignment: Alignment.center,
          child: Row(
            mainAxisSize: MainAxisSize.min,
            mainAxisAlignment: MainAxisAlignment.center,
            children: [
              if (widget.icon != null) ...[
                Icon(widget.icon, size: 14, color: textColor),
                const SizedBox(width: 5),
              ],
              Flexible(
                child: Text(
                  widget.label,
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  textAlign: TextAlign.center,
                  style: TextStyle(
                    fontSize: 11.5,
                    fontWeight: FontWeight.w600,
                    color: textColor,
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

class _ChainedTab extends StatelessWidget {
  const _ChainedTab({required this.s, required this.set, required this.c, required this.str});
  final UserSettings s;
  final void Function(void Function(UserSettings)) set;
  final AppColors c;
  final AppStrings str;

  @override
  Widget build(BuildContext context) {
    final mode = s.chainedSubMode;

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [

        GlassCard(
          borderRadius: 14,
          padding: const EdgeInsets.all(16),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              _SectionHeader(title: str.chainedArchitecture, icon: Icons.alt_route_rounded),
              const SizedBox(height: 10),
              GlassDropdownRow<String>(
                label: str.chainedMode,
                description: str.chainedModeDesc,
                value: mode,
                items: {
                  'psiphon_warp': str.chainedPsiphonWarp,
                  'psiphon_v2ray': str.chainedPsiphonV2ray,
                  'tor_warp': str.chainedTorWarp,
                  'tor_v2ray': str.chainedTorV2ray,
                },
                onChanged: (v) => set((x) => x.chainedSubMode = v),
              ),
            ],
          ),
        ),

        const SizedBox(height: 14),

        AnimatedSwitcher(
          duration: const Duration(milliseconds: 220),
          child: switch (mode) {
            'psiphon_warp' => GlassCard(
                key: const ValueKey('psiphon_warp_settings'),
                borderRadius: 14,
                padding: const EdgeInsets.all(16),
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    _SectionHeader(title: str.chainedPsiphonWarpTitle, icon: Icons.layers_rounded),
                    const SizedBox(height: 8),
                    Text(
                      str.chainedPsiphonWarpDesc,
                      style: TextStyle(fontSize: 11.5, color: c.textMuted),
                    ),
                    const SizedBox(height: 14),
                    GlassDropdownRow<String>(
                      label: str.chainedOuterTransport,
                      description: str.chainedOuterTransportDesc,
                      value: s.chainedPsiphonOuterTransport,
                      items: {
                        'auto': str.transportAuto,
                        'masque': str.transportMasque,
                        'wireguard': str.transportWireguard,
                        'warp_on_warp': str.transportDoubleWarp,
                      },
                      onChanged: (v) => set((x) => x.chainedPsiphonOuterTransport = v),
                    ),
                  ],
                ),
              ),
            'tor_warp' => GlassCard(
                key: const ValueKey('tor_warp_settings'),
                borderRadius: 14,
                padding: const EdgeInsets.all(16),
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    _SectionHeader(title: str.chainedTorWarpTitle, icon: Icons.shield_moon_rounded),
                    const SizedBox(height: 8),
                    Text(
                      str.chainedTorWarpDesc,
                      style: TextStyle(fontSize: 11.5, color: c.textMuted),
                    ),
                    const SizedBox(height: 14),
                    GlassDropdownRow<String>(
                      label: str.chainedOuterTransport,
                      description: str.chainedOuterTransportDesc,
                      value: s.chainedTorOuterTransport,
                      items: {
                        'auto': str.transportAuto,
                        'masque': str.transportMasque,
                        'wireguard': str.transportWireguard,
                        'warp_on_warp': str.transportDoubleWarp,
                      },
                      onChanged: (v) => set((x) => x.chainedTorOuterTransport = v),
                    ),
                  ],
                ),
              ),
            'psiphon_v2ray' => const PsiphonOverV2RaySection(
                key: ValueKey('psiphon_v2ray_settings'),
              ),
            'tor_v2ray' => const PsiphonOverV2RaySection(
                key: ValueKey('tor_v2ray_settings'),
                isTor: true,
              ),
            _ => const SizedBox(),
          },
        ),
      ],
    );
  }
}

class _ShardTab extends ConsumerStatefulWidget {
  const _ShardTab({required this.s, required this.set, required this.c});
  final UserSettings s;
  final void Function(void Function(UserSettings)) set;
  final AppColors c;

  @override
  ConsumerState<_ShardTab> createState() => _ShardTabState();
}

class _ShardTabState extends ConsumerState<_ShardTab> {
  bool _isRefreshing = false;
  bool _isRotating = false;
  int _nodeCount = 45;
  int _pathCount = 270;
  String _lastCheck = '';
  late final TextEditingController _ipCtrl;

  @override
  void initState() {
    super.initState();
    _ipCtrl = TextEditingController(text: widget.s.shardCustomCfIp);
    _loadPoolInfo();
  }

  @override
  void didUpdateWidget(covariant _ShardTab oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (widget.s.shardCustomCfIp != oldWidget.s.shardCustomCfIp &&
        widget.s.shardCustomCfIp != _ipCtrl.text) {
      _ipCtrl.text = widget.s.shardCustomCfIp;
    }
  }

  @override
  void dispose() {
    _ipCtrl.dispose();
    super.dispose();
  }

  Future<void> _rotateNode() async {
    if (_isRotating) return;
    setState(() => _isRotating = true);
    try {
      await ref.read(coreClientProvider).rotateShardNode();
      if (mounted) {
        setState(() => _isRotating = false);
        ScaffoldMessenger.of(context).showSnackBar(
          SnackBar(
            content: Text(ref.read(stringsProvider).shardSwitchedIpToast),
            behavior: SnackBarBehavior.floating,
            duration: const Duration(seconds: 3),
          ),
        );
      }
    } catch (_) {
      if (mounted) {
        setState(() => _isRotating = false);
      }
    }
  }

  Future<void> _loadPoolInfo() async {
    try {
      final info = await ref.read(coreClientProvider).getShardInfo();
      if (mounted && info.isNotEmpty) {
        setState(() {
          _nodeCount = (info['nodeCount'] as num?)?.toInt() ?? _nodeCount;
          _pathCount = (info['pathCount'] as num?)?.toInt() ?? _pathCount;
          final rawCheck = info['lastCheck'] as String?;
          if (rawCheck != null && rawCheck.isNotEmpty) {
            final dt = DateTime.tryParse(rawCheck);
            if (dt != null && dt.year > 2020) {
              _lastCheck = '${dt.toLocal().hour.toString().padLeft(2, '0')}:${dt.toLocal().minute.toString().padLeft(2, '0')} (${dt.toLocal().month}/${dt.toLocal().day})';
            }
          }
        });
      }
    } catch (_) {}
  }

  Future<void> _refreshPool() async {
    if (_isRefreshing) return;
    setState(() => _isRefreshing = true);
    try {
      final res = await ref.read(coreClientProvider).refreshShardPool(force: true);
      if (mounted) {
        setState(() {
          _isRefreshing = false;
          _nodeCount = (res['nodeCount'] as num?)?.toInt() ?? _nodeCount;
          _pathCount = (res['pathCount'] as num?)?.toInt() ?? _pathCount;
          final rawCheck = res['lastCheck'] as String?;
          if (rawCheck != null && rawCheck.isNotEmpty) {
            final dt = DateTime.tryParse(rawCheck);
            if (dt != null && dt.year > 2020) {
              _lastCheck = '${dt.toLocal().hour.toString().padLeft(2, '0')}:${dt.toLocal().minute.toString().padLeft(2, '0')} (${dt.toLocal().month}/${dt.toLocal().day})';
            }
          }
        });
        ScaffoldMessenger.of(context).showSnackBar(
          SnackBar(
            content: Text(ref.read(stringsProvider).shardRefreshedToast(_nodeCount, _pathCount)),
            behavior: SnackBarBehavior.floating,
            duration: const Duration(seconds: 3),
          ),
        );
      }
    } catch (_) {
      if (mounted) {
        setState(() => _isRefreshing = false);
      }
    }
  }

  @override
  Widget build(BuildContext context) {
    final c = widget.c;
    final s = widget.s;
    final set = widget.set;
    final str = ref.watch(stringsProvider);

    final statusAsync = ref.watch(tunnelStatusProvider);
    final status = statusAsync.valueOrNull ?? ref.watch(coreClientProvider).current;
    final isShardConnected = status.isConnected;

    final hasCustomIp = s.shardCustomCfIp.trim().isNotEmpty;

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [

        GlassCard(
          borderRadius: 14,
          padding: const EdgeInsets.all(16),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Row(
                children: [
                  _SectionHeader(
                    title: str.shardPoolTitle,
                    icon: Icons.cloud_sync_rounded,
                  ),
                  const Spacer(),

                  OutlinedButton.icon(
                    onPressed: _isRefreshing ? null : _refreshPool,
                    icon: _isRefreshing
                        ? const SizedBox(
                            width: 14,
                            height: 14,
                            child: CircularProgressIndicator(strokeWidth: 2, color: BrandColors.accentCyan),
                          )
                        : const Icon(Icons.refresh_rounded, size: 15),
                    label: Text(
                      _isRefreshing ? str.shardUpdating : str.shardUpdateFromCloud,
                      style: const TextStyle(fontSize: 11.5, fontWeight: FontWeight.w600),
                    ),
                    style: OutlinedButton.styleFrom(
                      foregroundColor: BrandColors.accentCyan,
                      side: BorderSide(color: BrandColors.accentCyan.withValues(alpha: 0.4)),
                      padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
                      shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(8)),
                    ),
                  ),
                ],
              ),
              const SizedBox(height: 12),

              Row(
                children: [
                  Expanded(
                    child: Container(
                      padding: const EdgeInsets.all(12),
                      decoration: BoxDecoration(
                        color: c.input.withValues(alpha: 0.5),
                        borderRadius: BorderRadius.circular(10),
                        border: Border.all(color: c.border.withValues(alpha: 0.3)),
                      ),
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          Row(
                            children: [
                              const Icon(Icons.layers_rounded, size: 14, color: BrandColors.accentCyan),
                              const SizedBox(width: 6),
                              Text(str.shardActiveNodes, style: TextStyle(fontSize: 11, color: c.textMuted)),
                            ],
                          ),
                          const SizedBox(height: 6),
                          Text(
                            str.shardNodesCount(_nodeCount),
                            style: TextStyle(fontSize: 16, fontWeight: FontWeight.w700, color: c.textPrimary),
                          ),
                          const SizedBox(height: 2),
                          Text(str.shardVlessSeed, style: TextStyle(fontSize: 9.5, color: c.textMuted)),
                        ],
                      ),
                    ),
                  ),
                  const SizedBox(width: 10),
                  Expanded(
                    child: Container(
                      padding: const EdgeInsets.all(12),
                      decoration: BoxDecoration(
                        color: c.input.withValues(alpha: 0.5),
                        borderRadius: BorderRadius.circular(10),
                        border: Border.all(color: c.border.withValues(alpha: 0.3)),
                      ),
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          Row(
                            children: [
                              const Icon(Icons.alt_route_rounded, size: 14, color: BrandColors.emerald),
                              const SizedBox(width: 6),
                              Text(str.shardEdgeMultiplier, style: TextStyle(fontSize: 11, color: c.textMuted)),
                            ],
                          ),
                          const SizedBox(height: 6),
                          Text(
                            hasCustomIp ? str.shardCustomEdge : str.shard6EdgeIps,
                            style: TextStyle(fontSize: 16, fontWeight: FontWeight.w700, color: c.textPrimary),
                          ),
                          const SizedBox(height: 2),
                          Text(
                            hasCustomIp ? str.shardDirectUserRouting : str.shardCdnPool,
                            style: TextStyle(fontSize: 9.5, color: c.textMuted),
                          ),
                        ],
                      ),
                    ),
                  ),
                  const SizedBox(width: 10),
                  Expanded(
                    child: Container(
                      padding: const EdgeInsets.all(12),
                      decoration: BoxDecoration(
                        color: c.input.withValues(alpha: 0.5),
                        borderRadius: BorderRadius.circular(10),
                        border: Border.all(color: c.border.withValues(alpha: 0.3)),
                      ),
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          Row(
                            children: [
                              const Icon(Icons.speed_rounded, size: 14, color: Color(0xFFFBBF24)),
                              const SizedBox(width: 6),
                              Text(str.shardTotalPaths, style: TextStyle(fontSize: 11, color: c.textMuted)),
                            ],
                          ),
                          const SizedBox(height: 6),
                          Text(
                            str.shardPathsCount(_pathCount),
                            style: TextStyle(fontSize: 16, fontWeight: FontWeight.w700, color: c.textPrimary),
                          ),
                          const SizedBox(height: 2),
                          Text(
                            _lastCheck.isNotEmpty ? 'Sync: $_lastCheck' : str.shardBuiltInSeed,
                            style: TextStyle(fontSize: 9.5, color: c.textMuted),
                          ),
                        ],
                      ),
                    ),
                  ),
                ],
              ),
            ],
          ),
        ),
        const SizedBox(height: 12),

        GlassCard(
          borderRadius: 14,
          padding: const EdgeInsets.all(16),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Row(
                children: [
                  _SectionHeader(
                    title: str.shardRoutingTitle,
                    icon: Icons.alt_route_rounded,
                  ),
                  const Spacer(),

                  Container(
                    padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 4),
                    decoration: BoxDecoration(
                      color: s.shardSmartSplit
                          ? BrandColors.emerald.withValues(alpha: 0.15)
                          : BrandColors.accentCyan.withValues(alpha: 0.15),
                      borderRadius: BorderRadius.circular(20),
                      border: Border.all(
                        color: s.shardSmartSplit
                            ? BrandColors.emerald.withValues(alpha: 0.4)
                            : BrandColors.accentCyan.withValues(alpha: 0.4),
                      ),
                    ),
                    child: Row(
                      mainAxisSize: MainAxisSize.min,
                      children: [
                        Icon(
                          s.shardSmartSplit ? Icons.call_split_rounded : Icons.public_rounded,
                          size: 13,
                          color: s.shardSmartSplit ? BrandColors.emerald : BrandColors.accentCyan,
                        ),
                        const SizedBox(width: 5),
                        Text(
                          s.shardSmartSplit ? str.shardSmartSplitTitle : str.shardFullTunnelTitle,
                          style: TextStyle(
                            fontSize: 11,
                            fontWeight: FontWeight.w600,
                            color: s.shardSmartSplit ? BrandColors.emerald : BrandColors.accentCyan,
                          ),
                        ),
                      ],
                    ),
                  ),
                ],
              ),
              const SizedBox(height: 8),
              Text(
                str.shardRoutingDesc,
                style: TextStyle(fontSize: 11.5, color: c.textMuted),
              ),
              const SizedBox(height: 14),

              Row(
                children: [

                  Expanded(
                    child: InkWell(
                      borderRadius: BorderRadius.circular(12),
                      onTap: () {
                        set((x) => x.shardSmartSplit = false);
                      },
                      child: Container(
                        padding: const EdgeInsets.all(13),
                        decoration: BoxDecoration(
                          color: !s.shardSmartSplit
                              ? BrandColors.accentCyan.withValues(alpha: 0.1)
                              : c.input.withValues(alpha: 0.4),
                          borderRadius: BorderRadius.circular(12),
                          border: Border.all(
                            color: !s.shardSmartSplit
                                ? BrandColors.accentCyan
                                : c.border.withValues(alpha: 0.3),
                            width: !s.shardSmartSplit ? 1.5 : 1,
                          ),
                        ),
                        child: Column(
                          crossAxisAlignment: CrossAxisAlignment.start,
                          children: [
                            Row(
                              children: [
                                Icon(
                                  Icons.public_rounded,
                                  size: 18,
                                  color: !s.shardSmartSplit
                                      ? BrandColors.accentCyan
                                      : c.textMuted,
                                ),
                                const SizedBox(width: 8),
                                Expanded(
                                  child: Text(
                                    str.shardFullTunnelTitle,
                                    style: TextStyle(
                                      fontSize: 12.5,
                                      fontWeight: FontWeight.w700,
                                      color: !s.shardSmartSplit
                                          ? c.textPrimary
                                          : c.textMuted,
                                    ),
                                  ),
                                ),
                                if (!s.shardSmartSplit)
                                  const Icon(
                                    Icons.check_circle_rounded,
                                    size: 16,
                                    color: BrandColors.accentCyan,
                                  ),
                              ],
                            ),
                            const SizedBox(height: 5),
                            Text(
                              str.shardFullTunnelSubtitle,
                              style: TextStyle(
                                fontSize: 11,
                                fontWeight: FontWeight.w600,
                                color: !s.shardSmartSplit
                                    ? BrandColors.accentCyan
                                    : c.textMuted,
                              ),
                            ),
                            const SizedBox(height: 4),
                            Text(
                              str.shardFullTunnelDesc,
                              style: TextStyle(fontSize: 10.5, color: c.textMuted),
                            ),
                            const SizedBox(height: 8),
                            Container(
                              padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 3),
                              decoration: BoxDecoration(
                                color: BrandColors.accentCyan.withValues(alpha: 0.12),
                                borderRadius: BorderRadius.circular(4),
                              ),
                              child: Text(
                                str.shardFullTunnelBadge,
                                style: const TextStyle(fontSize: 10, fontWeight: FontWeight.w600, color: BrandColors.accentCyan),
                              ),
                            ),
                          ],
                        ),
                      ),
                    ),
                  ),
                  const SizedBox(width: 12),

                  Expanded(
                    child: InkWell(
                      borderRadius: BorderRadius.circular(12),
                      onTap: () {
                        set((x) => x.shardSmartSplit = true);
                      },
                      child: Container(
                        padding: const EdgeInsets.all(13),
                        decoration: BoxDecoration(
                          color: s.shardSmartSplit
                              ? BrandColors.emerald.withValues(alpha: 0.1)
                              : c.input.withValues(alpha: 0.4),
                          borderRadius: BorderRadius.circular(12),
                          border: Border.all(
                            color: s.shardSmartSplit
                                ? BrandColors.emerald
                                : c.border.withValues(alpha: 0.3),
                            width: s.shardSmartSplit ? 1.5 : 1,
                          ),
                        ),
                        child: Column(
                          crossAxisAlignment: CrossAxisAlignment.start,
                          children: [
                            Row(
                              children: [
                                Icon(
                                  Icons.call_split_rounded,
                                  size: 18,
                                  color: s.shardSmartSplit
                                      ? BrandColors.emerald
                                      : c.textMuted,
                                ),
                                const SizedBox(width: 8),
                                Expanded(
                                  child: Text(
                                    str.shardSmartSplitTitle,
                                    style: TextStyle(
                                      fontSize: 12.5,
                                      fontWeight: FontWeight.w700,
                                      color: s.shardSmartSplit
                                          ? c.textPrimary
                                          : c.textMuted,
                                    ),
                                  ),
                                ),
                                if (s.shardSmartSplit)
                                  const Icon(
                                    Icons.check_circle_rounded,
                                    size: 16,
                                    color: BrandColors.emerald,
                                  ),
                              ],
                            ),
                            const SizedBox(height: 5),
                            Text(
                              str.shardSmartSplitSubtitle,
                              style: TextStyle(
                                fontSize: 11,
                                fontWeight: FontWeight.w600,
                                color: s.shardSmartSplit
                                    ? BrandColors.emerald
                                    : c.textMuted,
                              ),
                            ),
                            const SizedBox(height: 4),
                            Text(
                              str.shardSmartSplitDesc,
                              style: TextStyle(fontSize: 10.5, color: c.textMuted),
                            ),
                            const SizedBox(height: 8),
                            Container(
                              padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 3),
                              decoration: BoxDecoration(
                                color: BrandColors.emerald.withValues(alpha: 0.12),
                                borderRadius: BorderRadius.circular(4),
                              ),
                              child: Text(
                                str.shardSmartSplitBadge,
                                style: const TextStyle(fontSize: 10, fontWeight: FontWeight.w600, color: BrandColors.emerald),
                              ),
                            ),
                          ],
                        ),
                      ),
                    ),
                  ),
                ],
              ),
              const SizedBox(height: 14),
              const Divider(height: 1),
              const SizedBox(height: 8),

              GlassSwitchRow(
                icon: Icons.sync_rounded,
                iconColor: BrandColors.accentCyan,
                label: str.shardRotateIpTitle,
                description: str.shardRotateIpDesc,
                value: s.shardRotateIp,
                onChanged: (val) => set((x) => x.shardRotateIp = val),
              ),
              const SizedBox(height: 12),

              Container(
                padding: const EdgeInsets.all(12),
                decoration: BoxDecoration(
                  color: c.input.withValues(alpha: 0.4),
                  borderRadius: BorderRadius.circular(10),
                  border: Border.all(color: c.border.withValues(alpha: 0.3)),
                ),
                child: Row(
                  children: [
                    Icon(
                      isShardConnected ? Icons.cloud_done_rounded : Icons.cloud_outlined,
                      size: 18,
                      color: isShardConnected ? BrandColors.emerald : c.textMuted,
                    ),
                    const SizedBox(width: 10),
                    Expanded(
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          Text(
                            isShardConnected
                                ? 'Connected: ${status.currentRouteIp.isNotEmpty ? status.currentRouteIp : "Active Node"}'
                                : str.shardRotateReady,
                            style: TextStyle(fontSize: 11.5, fontWeight: FontWeight.w600, color: c.textPrimary),
                          ),
                          const SizedBox(height: 2),
                          Text(
                            isShardConnected
                                ? str.shardRotateConnectedDesc
                                : str.shardRotateDisconnectedDesc,
                            style: TextStyle(fontSize: 10, color: c.textMuted),
                          ),
                        ],
                      ),
                    ),
                    const SizedBox(width: 10),
                    ElevatedButton.icon(
                      onPressed: _isRotating ? null : _rotateNode,
                      icon: _isRotating
                          ? const SizedBox(
                              width: 12,
                              height: 12,
                              child: CircularProgressIndicator(strokeWidth: 2, color: Colors.black),
                            )
                          : const Icon(Icons.shuffle_rounded, size: 14),
                      label: Text(
                        _isRotating ? str.shardRotating : str.shardRotateIpNow,
                        style: const TextStyle(fontSize: 11, fontWeight: FontWeight.w600),
                      ),
                      style: ElevatedButton.styleFrom(
                        backgroundColor: BrandColors.accentCyan,
                        foregroundColor: Colors.black,
                        padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 10),
                        shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(8)),
                      ),
                    ),
                  ],
                ),
              ),
            ],
          ),
        ),
        const SizedBox(height: 12),

        GlassCard(
          borderRadius: 14,
          padding: const EdgeInsets.all(16),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              _SectionHeader(
                title: str.shardCleanIpTitle,
                icon: Icons.filter_drama_rounded,
              ),
              const SizedBox(height: 6),
              Text(
                str.shardCleanIpDesc,
                style: TextStyle(fontSize: 11.5, color: c.textMuted),
              ),
              const SizedBox(height: 14),

              Row(
                crossAxisAlignment: CrossAxisAlignment.end,
                children: [
                  Expanded(
                    child: _LanInputField(
                      label: str.shardCleanIpLabel,
                      hint: str.shardCleanIpHint,
                      value: s.shardCustomCfIp,
                      icon: Icons.dns_rounded,
                      onChanged: (v) {
                        set((x) => x.shardCustomCfIp = v.trim());
                      },
                    ),
                  ),
                  if (hasCustomIp) ...[
                    const SizedBox(width: 8),
                    Container(
                      height: 42,
                      alignment: Alignment.center,
                      child: TextButton.icon(
                        onPressed: () {
                          set((x) => x.shardCustomCfIp = '');
                        },
                        icon: const Icon(Icons.clear_rounded, size: 15, color: BrandColors.danger),
                        label: Text(str.shardClear, style: const TextStyle(fontSize: 11.5, color: BrandColors.danger)),
                        style: TextButton.styleFrom(
                          padding: const EdgeInsets.symmetric(horizontal: 10),
                          shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(8)),
                        ),
                      ),
                    ),
                  ],
                ],
              ),
              const SizedBox(height: 10),

              Wrap(
                spacing: 8,
                runSpacing: 6,
                children: [
                  Text(str.shardPresetsLabel, style: TextStyle(fontSize: 11, color: c.textMuted, height: 2.2)),
                  for (final ip in ['104.16.0.1', '172.67.0.1', '104.17.0.1', '104.18.0.1', '162.159.192.1'])
                    ActionChip(
                      label: Text(ip, style: const TextStyle(fontSize: 10.5)),
                      backgroundColor: s.shardCustomCfIp == ip
                          ? BrandColors.primary.withValues(alpha: 0.25)
                          : c.input.withValues(alpha: 0.5),
                      side: BorderSide(
                        color: s.shardCustomCfIp == ip
                            ? BrandColors.primary
                            : c.border.withValues(alpha: 0.3),
                      ),
                      onPressed: () {
                        set((x) => x.shardCustomCfIp = ip);
                      },
                    ),
                ],
              ),
            ],
          ),
        ),
      ],
    );
  }
}

class _SectionHeader extends StatelessWidget {
  const _SectionHeader({required this.title, required this.icon});
  final String title;
  final IconData icon;

  @override
  Widget build(BuildContext context) {
    final c = Theme.of(context).extension<AppColors>()!;
    return Row(
      children: [
        Icon(icon, size: 16, color: BrandColors.accentCyan),
        const SizedBox(width: 8),
        Text(
          title,
          style: TextStyle(
            fontSize: 13,
            fontWeight: FontWeight.w700,
            letterSpacing: 0.3,
            color: c.textPrimary,
          ),
        ),
      ],
    );
  }
}

class _LanInputField extends StatefulWidget {
  const _LanInputField({
    required this.label,
    required this.hint,
    required this.value,
    this.icon,
    this.obscure = false,
    this.keyboardType,
    this.isPortField = false,
    this.helperBadge,
    required this.onChanged,
  });

  static const int maxPort = 65535;

  final String label;
  final String hint;
  final String value;
  final IconData? icon;
  final bool obscure;
  final TextInputType? keyboardType;
  final bool isPortField;
  final String? helperBadge;
  final ValueChanged<String> onChanged;

  @override
  State<_LanInputField> createState() => _LanInputFieldState();
}

class _LanInputFieldState extends State<_LanInputField> {
  late final TextEditingController _ctrl;
  late final FocusNode _focusNode;
  late bool _obscured;
  bool _isExceeded = false;
  bool _focused = false;

  @override
  void initState() {
    super.initState();
    _ctrl = TextEditingController(text: widget.value);
    _focusNode = FocusNode();
    _focusNode.addListener(() {
      if (mounted) setState(() => _focused = _focusNode.hasFocus);
    });
    _obscured = widget.obscure;
    _checkPortRange(widget.value);
  }

  void _checkPortRange(String text) {
    if (!widget.isPortField) {
      _isExceeded = false;
      return;
    }
    final trimmed = text.trim();
    if (trimmed.isEmpty) {
      _isExceeded = false;
      return;
    }
    final parsed = int.tryParse(trimmed);
    _isExceeded = (parsed != null && (parsed > _LanInputField.maxPort || parsed < 0));
  }

  @override
  void didUpdateWidget(covariant _LanInputField oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (widget.value != oldWidget.value && widget.value != _ctrl.text) {
      final sel = _ctrl.selection;
      final wasFocused = sel.isValid;
      _ctrl.text = widget.value;
      final newExceeded = widget.isPortField && widget.value.trim().isNotEmpty && (() { final p=int.tryParse(widget.value.trim()); return p!=null&&(p> _LanInputField.maxPort||p<0); })();
      if (newExceeded != _isExceeded) {
        setState(() => _isExceeded = newExceeded);
      } else {
        _isExceeded = newExceeded;
      }
      if (wasFocused) {
        try {
          final end = _ctrl.text.length;
          _ctrl.selection = sel.copyWith(baseOffset: sel.baseOffset.clamp(0,end), extentOffset: sel.extentOffset.clamp(0,end));
        } catch (_) { _ctrl.selection = TextSelection.collapsed(offset: _ctrl.text.length); }
      }
    }
  }

  @override
  void dispose() {
    _focusNode.dispose();
    _ctrl.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final c = Theme.of(context).extension<AppColors>()!;
    final formatters = <TextInputFormatter>[
      if (widget.isPortField) ...[
        FilteringTextInputFormatter.digitsOnly,
        LengthLimitingTextInputFormatter(5),
      ],
    ];

    final badgeText = _isExceeded
        ? '⚠️ Max ${_LanInputField.maxPort}!'
        : (widget.helperBadge ?? (widget.isPortField ? '1–65535' : null));

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Row(
          children: [
            if (widget.icon != null) ...[
              Icon(widget.icon, size: 13, color: _isExceeded ? BrandColors.danger : (_focused ? BrandColors.accentCyan : c.textSecondary)),
              const SizedBox(width: 5),
            ],
            Text(
              widget.label,
              style: TextStyle(
                fontSize: 12,
                fontWeight: FontWeight.w600,
                color: _isExceeded ? BrandColors.danger : c.textPrimary,
              ),
            ),
            if (badgeText != null) ...[
              const Spacer(),
              Container(
                padding: const EdgeInsets.symmetric(horizontal: 5, vertical: 1.5),
                decoration: BoxDecoration(
                  color: _isExceeded
                      ? BrandColors.danger.withValues(alpha: 0.18)
                      : c.cardElevated.withValues(alpha: 0.6),
                  borderRadius: BorderRadius.circular(5),
                  border: Border.all(
                    color: _isExceeded
                        ? BrandColors.danger.withValues(alpha: 0.6)
                        : c.border.withValues(alpha: 0.4),
                  ),
                ),
                child: Text(
                  badgeText,
                  style: TextStyle(
                    fontSize: 9.5,
                    fontWeight: _isExceeded ? FontWeight.w700 : FontWeight.w500,
                    color: _isExceeded ? BrandColors.danger : c.textMuted,
                  ),
                ),
              ),
            ],
          ],
        ),
        const SizedBox(height: 7),
        AnimatedContainer(
          duration: const Duration(milliseconds: 140),
          height: 42,
          alignment: Alignment.center,
          decoration: BoxDecoration(
            color: _isExceeded
                ? BrandColors.danger.withValues(alpha: 0.08)
                : (_focused ? c.cardElevated.withValues(alpha: 0.8) : c.input.withValues(alpha: 0.65)),
            borderRadius: BorderRadius.circular(10),
            border: Border.all(
              color: _isExceeded
                  ? BrandColors.danger.withValues(alpha: 0.85)
                  : (_focused ? BrandColors.accentCyan : c.border.withValues(alpha: 0.55)),
              width: (_isExceeded || _focused) ? 1.4 : 1.0,
            ),
          ),
          child: TextField(
            controller: _ctrl,
            focusNode: _focusNode,
            onChanged: (v) {
              if (widget.isPortField) {
                setState(() {
                  _checkPortRange(v);
                });
              }
              widget.onChanged(v);
            },
            obscureText: _obscured,
            keyboardType: widget.keyboardType,
            inputFormatters: formatters,
            style: TextStyle(
              fontSize: 12.5,
              color: _isExceeded ? BrandColors.danger : c.textPrimary,
              fontWeight: _isExceeded ? FontWeight.w600 : FontWeight.normal,
              fontFamily: widget.isPortField ? 'monospace' : null,
            ),
            textAlignVertical: TextAlignVertical.center,
            decoration: InputDecoration(
              hintText: widget.hint,
              hintStyle: TextStyle(fontSize: 12, color: c.textMuted),
              isDense: true,
              isCollapsed: true,
              filled: false,
              border: InputBorder.none,
              contentPadding: const EdgeInsets.symmetric(horizontal: 12),
              suffixIcon: widget.obscure
                  ? MouseRegion(
                      cursor: SystemMouseCursors.click,
                      child: GestureDetector(
                        onTap: () => setState(() => _obscured = !_obscured),
                        child: Padding(
                          padding: const EdgeInsets.only(right: 10),
                          child: Icon(
                            _obscured
                                ? Icons.visibility_off_rounded
                                : Icons.visibility_rounded,
                            size: 16,
                            color: _obscured ? c.textMuted : BrandColors.accentCyan,
                          ),
                        ),
                      ),
                    )
                  : null,
              suffixIconConstraints: const BoxConstraints(minWidth: 32, minHeight: 32),
            ),
          ),
        ),
      ],
    );
  }
}

class _LanMultiLineField extends StatefulWidget {
  const _LanMultiLineField({
    required this.label,
    required this.description,
    required this.hint,
    required this.value,
    required this.icon,
    required this.onChanged,
    this.height = 96,
  });

  final String label;
  final String description;
  final String hint;
  final String value;
  final IconData icon;
  final ValueChanged<String> onChanged;
  final double height;

  @override
  State<_LanMultiLineField> createState() => _LanMultiLineFieldState();
}

class _LanMultiLineFieldState extends State<_LanMultiLineField> {
  late final TextEditingController _ctrl;

  @override
  void initState() {
    super.initState();
    _ctrl = TextEditingController(text: widget.value.replaceAll(', ', '\n').replaceAll(',', '\n'));
  }

  @override
  void didUpdateWidget(covariant _LanMultiLineField oldWidget) {
    super.didUpdateWidget(oldWidget);
    final currentNormalized = widget.value.replaceAll(', ', '\n').replaceAll(',', '\n');
    if (currentNormalized != _ctrl.text && widget.value != _ctrl.text) {
      _ctrl.text = currentNormalized;
    }
  }

  @override
  void dispose() {
    _ctrl.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final c = Theme.of(context).extension<AppColors>()!;

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Row(
          children: [
            Icon(widget.icon, size: 13, color: c.textSecondary),
            const SizedBox(width: 5),
            Text(
              widget.label,
              style: TextStyle(
                fontSize: 12,
                fontWeight: FontWeight.w600,
                color: c.textPrimary,
              ),
            ),
          ],
        ),
        const SizedBox(height: 2),
        Text(
          widget.description,
          style: TextStyle(fontSize: 10.5, color: c.textMuted),
          maxLines: 1,
          overflow: TextOverflow.ellipsis,
        ),
        const SizedBox(height: 7),
        Container(
          height: widget.height,
          decoration: BoxDecoration(
            color: c.input.withValues(alpha: 0.65),
            borderRadius: BorderRadius.circular(10),
            border: Border.all(color: c.border.withValues(alpha: 0.6)),
          ),
          child: TextField(
            controller: _ctrl,
            onChanged: (v) {
              final lines = v
                  .split('\n')
                  .map((l) => l.trim())
                  .where((l) => l.isNotEmpty)
                  .toList();
              widget.onChanged(lines.join(', '));
            },
            keyboardType: TextInputType.multiline,
            maxLines: null,
            expands: true,
            textAlignVertical: TextAlignVertical.top,
            style: AppTheme.mono(c.textPrimary, size: 11.5, weight: FontWeight.w400),
            decoration: InputDecoration(
              hintText: widget.hint,
              hintStyle: TextStyle(fontSize: 11, color: c.textMuted),
              isDense: true,
              filled: false,
              border: InputBorder.none,
              contentPadding: const EdgeInsets.all(10),
            ),
          ),
        ),
      ],
    );
  }
}

class _LanSharingInfoBox extends StatefulWidget {
  const _LanSharingInfoBox({required this.s, required this.c, required this.str});

  final UserSettings s;
  final AppColors c;
  final AppStrings str;

  @override
  State<_LanSharingInfoBox> createState() => _LanSharingInfoBoxState();
}

class _LanSharingInfoBoxState extends State<_LanSharingInfoBox> {
  String _localIp = 'Detecting…';
  String? _copiedKey;

  @override
  void initState() {
    super.initState();
    _detectIp();
  }

  Future<void> _detectIp() async {
    try {
      final interfaces = await NetworkInterface.list(
        includeLoopback: false,
        type: InternetAddressType.IPv4,
      );
      for (final iface in interfaces) {
        for (final addr in iface.addresses) {
          if (!addr.isLoopback && !addr.address.startsWith('169.254')) {
            if (mounted) setState(() => _localIp = addr.address);
            return;
          }
        }
      }
    } catch (_) {}
    if (mounted) setState(() => _localIp = '192.168.1.X');
  }

  void _copy(String text, String key) {
    Clipboard.setData(ClipboardData(text: text));
    setState(() => _copiedKey = key);
    ScaffoldMessenger.of(context).hideCurrentSnackBar();
    ScaffoldMessenger.of(context).showSnackBar(
      SnackBar(
        content: Text(widget.str.copiedToClipboard(text)),
        duration: const Duration(milliseconds: 1400),
        behavior: SnackBarBehavior.floating,
        shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(8)),
      ),
    );
    Future.delayed(const Duration(milliseconds: 1800), () {
      if (mounted && _copiedKey == key) {
        setState(() => _copiedKey = null);
      }
    });
  }

  @override
  Widget build(BuildContext context) {
    final c = widget.c;
    final s = widget.s;
    final socksPort = s.localSocksProxyPort == 0 ? 10808 : s.localSocksProxyPort;
    final httpPort = s.localHttpProxyPort == 0 ? 10809 : s.localHttpProxyPort;
    final hasUser = s.lanAuthEnabled && s.lanProxyUsername.trim().isNotEmpty;
    final hasPass = s.lanAuthEnabled && s.lanProxyPassword.trim().isNotEmpty;

    return Container(
      padding: const EdgeInsets.all(14),
      decoration: BoxDecoration(
        color: c.input.withValues(alpha: 0.45),
        borderRadius: BorderRadius.circular(12),
        border: Border.all(color: c.border.withValues(alpha: 0.5)),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Container(
                width: 8,
                height: 8,
                decoration: const BoxDecoration(
                  color: BrandColors.emerald,
                  shape: BoxShape.circle,
                ),
              ),
              const SizedBox(width: 8),
              Text(
                widget.str.lanEndpointsTitle,
                style: TextStyle(
                  fontSize: 12.5,
                  fontWeight: FontWeight.w700,
                  color: c.textPrimary,
                ),
              ),
              const Spacer(),
              Text(
                widget.str.lanWifiClients,
                style: TextStyle(fontSize: 11, color: c.textMuted),
              ),
            ],
          ),
          const SizedBox(height: 12),

          Row(
            children: [
              Expanded(
                child: _InfoTile(
                  label: widget.str.lanHostIp,
                  value: _localIp,
                  copied: _copiedKey == 'ip',
                  onCopy: () => _copy(_localIp, 'ip'),
                  c: c,
                ),
              ),
              const SizedBox(width: 10),
              Expanded(
                child: _InfoTile(
                  label: widget.str.lanSocks5Endpoint,
                  value: '$_localIp:$socksPort',
                  copied: _copiedKey == 'socks',
                  onCopy: () => _copy('$_localIp:$socksPort', 'socks'),
                  c: c,
                ),
              ),
            ],
          ),
          const SizedBox(height: 8),
          Row(
            children: [
              Expanded(
                child: _InfoTile(
                  label: widget.str.lanHttpEndpoint,
                  value: '$_localIp:$httpPort',
                  copied: _copiedKey == 'http',
                  onCopy: () => _copy('$_localIp:$httpPort', 'http'),
                  c: c,
                ),
              ),
              const SizedBox(width: 10),
              Expanded(
                child: hasUser || hasPass
                    ? _InfoTile(
                        label: widget.str.lanAuthUserPass,
                        value: '${hasUser ? s.lanProxyUsername : "none"} : ${hasPass ? s.lanProxyPassword : "none"}',
                        copied: _copiedKey == 'auth',
                        onCopy: () => _copy(
                          '${hasUser ? s.lanProxyUsername : ""}:${hasPass ? s.lanProxyPassword : ""}',
                          'auth',
                        ),
                        c: c,
                      )
                    : _InfoTile(
                        label: widget.str.lanAuthLabel,
                        value: widget.str.lanNoAuth,
                        copied: false,
                        onCopy: null,
                        c: c,
                      ),
              ),
            ],
          ),
        ],
      ),
    );
  }
}

class _InfoTile extends StatelessWidget {
  const _InfoTile({
    required this.label,
    required this.value,
    required this.copied,
    required this.onCopy,
    required this.c,
  });

  final String label;
  final String value;
  final bool copied;
  final VoidCallback? onCopy;
  final AppColors c;

  @override
  Widget build(BuildContext context) {
    return Container(
      height: 42,
      padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 4),
      decoration: BoxDecoration(
        color: c.cardElevated.withValues(alpha: 0.7),
        borderRadius: BorderRadius.circular(9),
        border: Border.all(color: c.border.withValues(alpha: 0.5)),
      ),
      child: Row(
        children: [
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              mainAxisAlignment: MainAxisAlignment.center,
              children: [
                Text(
                  label,
                  style: TextStyle(fontSize: 10, color: c.textMuted, fontWeight: FontWeight.w500),
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                ),
                Text(
                  value,
                  style: AppTheme.mono(c.textPrimary, size: 11.5, weight: FontWeight.w600),
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                ),
              ],
            ),
          ),
          if (onCopy != null) ...[
            const SizedBox(width: 6),
            MouseRegion(
              cursor: SystemMouseCursors.click,
              child: InkWell(
                borderRadius: BorderRadius.circular(6),
                onTap: onCopy,
                child: Padding(
                  padding: const EdgeInsets.all(4),
                  child: Icon(
                    copied ? Icons.check_rounded : Icons.copy_rounded,
                    size: 15,
                    color: copied ? BrandColors.emerald : BrandColors.accentCyan,
                  ),
                ),
              ),
            ),
          ],
        ],
      ),
    );
  }
}

class _CoreUpdatesCard extends ConsumerStatefulWidget {
  const _CoreUpdatesCard({required this.c});
  final AppColors c;

  @override
  ConsumerState<_CoreUpdatesCard> createState() => _CoreUpdatesCardState();
}

class _CoreUpdatesCardState extends ConsumerState<_CoreUpdatesCard> {
  final _service = CoreUpdateService.instance;

  String _aetherInstalledVer = '1.7.0';
  String _aetherLatestVer = '1.7.0';
  bool _hasAetherUpdate = false;
  bool _isCheckingAether = false;
  bool _isUpdatingAether = false;
  int _aetherProgress = 0;
  String _aetherStatusText = '';

  String _torInstalledVer = '0.4.9.11';
  bool _isCheckingTor = false;
  String _torStatusText = 'Bundled stable release';

  @override
  void initState() {
    super.initState();
    _loadInstalledVersions();
  }

  void _loadInstalledVersions() {
    setState(() {
      _aetherInstalledVer = _service.getInstalledVersion('aether');
      _aetherLatestVer = _aetherInstalledVer;
      _torInstalledVer = _service.getInstalledVersion('tor');
    });
  }

  Future<void> _checkAetherUpdate() async {
    if (_isCheckingAether || _isUpdatingAether) return;
    setState(() {
      _isCheckingAether = true;
      _aetherStatusText = 'Checking GitHub releases…';
    });

    try {
      final info = await _service.checkForUpdate('aether');
      if (!mounted) return;
      setState(() {
        _aetherLatestVer = info.latestVersion;
        _hasAetherUpdate = info.hasUpdate;
        _aetherStatusText = info.hasUpdate
            ? 'Update v${info.latestVersion} available'
            : 'Up to date (v${info.installedVersion})';
      });
    } catch (e) {
      if (!mounted) return;
      setState(() => _aetherStatusText = 'Check failed: $e');
    } finally {
      if (mounted) setState(() => _isCheckingAether = false);
    }
  }

  Future<void> _checkTorUpdate() async {
    if (_isCheckingTor) return;
    setState(() {
      _isCheckingTor = true;
      _torStatusText = 'Verifying Tor engine…';
    });

    await Future.delayed(const Duration(milliseconds: 500));
    final ver = _service.getInstalledVersion('tor');
    if (!mounted) return;
    setState(() {
      _torInstalledVer = ver;
      _isCheckingTor = false;
      _torStatusText = 'Bundled release (v$ver)';
    });
  }

  Future<void> _checkAllUpdates() async {
    await Future.wait([
      _checkAetherUpdate(),
      _checkTorUpdate(),
    ]);
  }

  Future<void> _updateAether() async {
    if (_isUpdatingAether) return;
    setState(() {
      _isUpdatingAether = true;
      _aetherProgress = 0;
      _aetherStatusText = 'Starting download…';
    });

    try {
      final success = await _service.updateAether(
        onProgress: (p, status) {
          if (!mounted) return;
          setState(() {
            _aetherProgress = p;
            _aetherStatusText = status;
          });
        },
      );

      if (success && mounted) {
        final newVer = _service.getInstalledVersion('aether');
        setState(() {
          _aetherInstalledVer = newVer;
          _aetherLatestVer = newVer;
          _hasAetherUpdate = false;
          _aetherStatusText = 'Updated to v$newVer';
        });

        _showSuccessDialog(newVer);
      }
    } catch (e) {
      if (mounted) {
        setState(() => _aetherStatusText = 'Update failed: $e');
      }
    } finally {
      if (mounted) setState(() => _isUpdatingAether = false);
    }
  }

  void _showSuccessDialog(String version) {
    final c = widget.c;
    showDialog(
      context: context,
      builder: (ctx) => AlertDialog(
        backgroundColor: c.cardElevated,
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(16),
          side: BorderSide(color: c.border.withValues(alpha: 0.7)),
        ),
        title: Row(
          children: [
            Container(
              padding: const EdgeInsets.all(8),
              decoration: BoxDecoration(
                color: BrandColors.emerald.withValues(alpha: 0.15),
                shape: BoxShape.circle,
              ),
              child: const Icon(Icons.check_circle_rounded, color: BrandColors.emerald, size: 24),
            ),
            const SizedBox(width: 12),
            Expanded(
              child: Text(
                ref.read(stringsProvider).aetherCoreUpdated,
                style: TextStyle(fontSize: 15, fontWeight: FontWeight.w700, color: c.textPrimary),
              ),
            ),
          ],
        ),
        content: Text(
          'Aether network engine has been successfully updated to v$version.\nPlease restart the application to apply the updated core.',
          style: TextStyle(fontSize: 13, color: c.textSecondary, height: 1.4),
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(ctx).pop(),
            child: Text(ref.read(stringsProvider).close, style: TextStyle(color: c.textMuted)),
          ),
          ElevatedButton.icon(
            style: ElevatedButton.styleFrom(
              backgroundColor: BrandColors.accentCyan,
              foregroundColor: Colors.black,
              shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(8)),
            ),
            icon: const Icon(Icons.restart_alt_rounded, size: 16),
            label: Text(ref.read(stringsProvider).restartNow, style: const TextStyle(fontWeight: FontWeight.w700)),
            onPressed: () {
              Navigator.of(ctx).pop();
              _restartApp();
            },
          ),
        ],
      ),
    );
  }

  Future<void> _restartApp() async {
    try {
      final exe = Platform.resolvedExecutable;
      final workingDir = File(exe).parent.path;

      try {
        await ref.read(connectionControllerProvider).disconnect();
      } catch (_) {}

      try {
        await windowManager.hide();
      } catch (_) {}

      try {
        await TrayService.instance?.disposeAsync();
      } catch (_) {}

      bool started = false;
      try {
        await Process.start(
          exe,
          [],
          workingDirectory: workingDir,
          mode: ProcessStartMode.detached,
        );
        started = true;
      } catch (_) {}

      if (!started) {
        try {
          await Process.start(
            'explorer.exe',
            [exe],
            workingDirectory: workingDir,
            mode: ProcessStartMode.detached,
          );
        } catch (_) {}
      }

      exit(0);
    } catch (_) {
      exit(0);
    }
  }

  @override
  Widget build(BuildContext context) {
    final c = widget.c;
    final isCheckingAny = _isCheckingAether || _isCheckingTor;

    return GlassCard(
      borderRadius: 14,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [

          Row(
            children: [
              const Icon(Icons.system_update_alt_rounded, size: 16, color: BrandColors.accentCyan),
              const SizedBox(width: 8),
              Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(
                    ref.watch(stringsProvider).networkEngineUpdates,
                    style: TextStyle(
                      fontSize: 13,
                      fontWeight: FontWeight.w700,
                      letterSpacing: 0.3,
                      color: c.textPrimary,
                    ),
                  ),
                  Text(
                    'Manage & update standalone Aether and Tor runtime binaries',
                    style: TextStyle(fontSize: 11, color: c.textMuted),
                  ),
                ],
              ),
              const Spacer(),

              MouseRegion(
                cursor: isCheckingAny ? SystemMouseCursors.basic : SystemMouseCursors.click,
                child: InkWell(
                  borderRadius: BorderRadius.circular(8),
                  onTap: isCheckingAny ? null : _checkAllUpdates,
                  child: Container(
                    padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 6),
                    decoration: BoxDecoration(
                      color: c.card.withValues(alpha: 0.6),
                      borderRadius: BorderRadius.circular(8),
                      border: Border.all(color: c.border.withValues(alpha: 0.6)),
                    ),
                    child: Row(
                      mainAxisSize: MainAxisSize.min,
                      children: [
                        if (isCheckingAny)
                          const SizedBox(
                            width: 12,
                            height: 12,
                            child: CircularProgressIndicator(strokeWidth: 1.8, color: BrandColors.accentCyan),
                          )
                        else
                          Icon(Icons.refresh_rounded, size: 13, color: c.textSecondary),
                        const SizedBox(width: 6),
                        Text(
                          ref.watch(stringsProvider).checkUpdates,
                          style: TextStyle(
                            fontSize: 11.5,
                            fontWeight: FontWeight.w600,
                            color: c.textSecondary,
                          ),
                        ),
                      ],
                    ),
                  ),
                ),
              ),
            ],
          ),
          const SizedBox(height: 14),

          _AetherCoreTile(
            installedVer: _aetherInstalledVer,
            latestVer: _aetherLatestVer,
            hasUpdate: _hasAetherUpdate,
            isChecking: _isCheckingAether,
            isUpdating: _isUpdatingAether,
            progress: _aetherProgress,
            statusText: _aetherStatusText,
            onCheck: _checkAetherUpdate,
            onUpdate: _updateAether,
            c: c,
          ),

          const SizedBox(height: 10),

          _TorCoreTile(
            installedVer: _torInstalledVer,
            isChecking: _isCheckingTor,
            statusText: _torStatusText,
            onCheck: _checkTorUpdate,
            c: c,
          ),
        ],
      ),
    );
  }
}

class _AetherCoreTile extends ConsumerWidget {
  const _AetherCoreTile({
    required this.installedVer,
    required this.latestVer,
    required this.hasUpdate,
    required this.isChecking,
    required this.isUpdating,
    required this.progress,
    required this.statusText,
    required this.onCheck,
    required this.onUpdate,
    required this.c,
  });

  final String installedVer;
  final String latestVer;
  final bool hasUpdate;
  final bool isChecking;
  final bool isUpdating;
  final int progress;
  final String statusText;
  final VoidCallback onCheck;
  final VoidCallback onUpdate;
  final AppColors c;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final str = ref.watch(stringsProvider);
    return Container(
      padding: const EdgeInsets.all(12),
      decoration: BoxDecoration(
        color: c.input.withValues(alpha: 0.45),
        borderRadius: BorderRadius.circular(10),
        border: Border.all(
          color: hasUpdate
              ? BrandColors.accentCyan.withValues(alpha: 0.6)
              : c.border.withValues(alpha: 0.5),
        ),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Row(
            children: [
              Container(
                width: 34,
                height: 34,
                decoration: BoxDecoration(
                  color: BrandColors.accentCyan.withValues(alpha: 0.12),
                  borderRadius: BorderRadius.circular(8),
                  border: Border.all(color: BrandColors.accentCyan.withValues(alpha: 0.25)),
                ),
                child: const Icon(Icons.bolt_rounded, size: 18, color: BrandColors.accentCyan),
              ),
              const SizedBox(width: 10),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Row(
                      children: [
                        Text(
                          'Aether Core',
                          style: TextStyle(
                            fontSize: 13,
                            fontWeight: FontWeight.w600,
                            color: c.textPrimary,
                          ),
                        ),
                        const SizedBox(width: 8),
                        Container(
                          padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 1.5),
                          decoration: BoxDecoration(
                            color: c.card.withValues(alpha: 0.7),
                            borderRadius: BorderRadius.circular(4),
                            border: Border.all(color: c.border.withValues(alpha: 0.6)),
                          ),
                          child: Text(
                            'v$installedVer',
                            style: AppTheme.mono(c.textSecondary, size: 10.5, weight: FontWeight.w500),
                          ),
                        ),
                        if (hasUpdate) ...[
                          const SizedBox(width: 6),
                          Container(
                            padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 1.5),
                            decoration: BoxDecoration(
                              color: BrandColors.accentCyan.withValues(alpha: 0.2),
                              borderRadius: BorderRadius.circular(4),
                              border: Border.all(color: BrandColors.accentCyan.withValues(alpha: 0.5)),
                            ),
                            child: Text(
                              'v$latestVer available',
                              style: TextStyle(
                                fontSize: 10.5,
                                fontWeight: FontWeight.w700,
                                color: BrandColors.accentCyan,
                              ),
                            ),
                          ),
                        ],
                      ],
                    ),
                    const SizedBox(height: 2),
                    Text(
                      str.aetherCoreDesc,
                      style: TextStyle(fontSize: 11, color: c.textMuted),
                    ),
                  ],
                ),
              ),
              const SizedBox(width: 10),

              if (statusText.isNotEmpty && !isUpdating) ...[
                Text(
                  statusText,
                  style: TextStyle(
                    fontSize: 11.5,
                    color: hasUpdate ? BrandColors.accentCyan : c.textMuted,
                    fontWeight: hasUpdate ? FontWeight.w600 : FontWeight.w400,
                  ),
                ),
                const SizedBox(width: 8),
              ],

              if (hasUpdate)
                ElevatedButton.icon(
                  style: ElevatedButton.styleFrom(
                    backgroundColor: BrandColors.accentCyan,
                    foregroundColor: Colors.black,
                    elevation: 0,
                    padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
                    shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(8)),
                  ),
                  icon: const Icon(Icons.download_rounded, size: 14),
                  label: Text(str.updateNow, style: const TextStyle(fontSize: 11.5, fontWeight: FontWeight.w700)),
                  onPressed: isUpdating ? null : onUpdate,
                )
              else
                OutlinedButton.icon(
                  style: OutlinedButton.styleFrom(
                    foregroundColor: c.textSecondary,
                    side: BorderSide(color: c.border.withValues(alpha: 0.6)),
                    padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 8),
                    shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(8)),
                  ),
                  icon: const Icon(Icons.cloud_download_outlined, size: 14),
                  label: Text(str.reinstall, style: const TextStyle(fontSize: 11, fontWeight: FontWeight.w500)),
                  onPressed: isUpdating ? null : onUpdate,
                ),
              const SizedBox(width: 6),
              IconButton(
                icon: isChecking
                    ? const SizedBox(
                        width: 14,
                        height: 14,
                        child: CircularProgressIndicator(strokeWidth: 2, color: BrandColors.accentCyan),
                      )
                    : Icon(Icons.refresh_rounded, size: 16, color: c.textSecondary),
                tooltip: 'Check Aether release on GitHub',
                onPressed: (isChecking || isUpdating) ? null : onCheck,
                splashRadius: 16,
              ),
            ],
          ),
          if (isUpdating) ...[
            const SizedBox(height: 10),
            Row(
              children: [
                Expanded(
                  child: ClipRRect(
                    borderRadius: BorderRadius.circular(4),
                    child: LinearProgressIndicator(
                      value: progress > 0 ? progress / 100 : null,
                      backgroundColor: c.border.withValues(alpha: 0.4),
                      valueColor: const AlwaysStoppedAnimation<Color>(BrandColors.accentCyan),
                      minHeight: 4,
                    ),
                  ),
                ),
                const SizedBox(width: 10),
                Text(
                  '$progress%',
                  style: AppTheme.mono(BrandColors.accentCyan, size: 11, weight: FontWeight.w600),
                ),
              ],
            ),
          ],
        ],
      ),
    );
  }
}

class _CdnProviderDef {
  const _CdnProviderDef({
    required this.key,
    required this.name,
    required this.badge,
    required this.color,
    required this.icon,
  });

  final String key;
  final String name;
  final String badge;
  final Color color;
  final IconData icon;
}

class _CdnScanCorpusSection extends StatelessWidget {
  const _CdnScanCorpusSection({required this.s, required this.set, required this.str});
  final UserSettings s;
  final void Function(void Function(UserSettings)) set;
  final AppStrings str;

  static const _providers = [
    _CdnProviderDef(
      key: 'cloudflare',
      name: 'Cloudflare',
      badge: 'Anycast CDN',
      color: Color(0xFFF38020),
      icon: Icons.cloud_queue_rounded,
    ),
    _CdnProviderDef(
      key: 'google',
      name: 'Google',
      badge: 'GCP Edge',
      color: Color(0xFF4285F4),
      icon: Icons.language_rounded,
    ),
    _CdnProviderDef(
      key: 'psiphon-akamai',
      name: 'Psiphon (Akamai)',
      badge: 'Akamai Edge',
      color: Color(0xFF00A2E8),
      icon: Icons.hub_outlined,
    ),
    _CdnProviderDef(
      key: 'fastly',
      name: 'Fastly',
      badge: 'Edge Cloud',
      color: Color(0xFFFF2B2B),
      icon: Icons.flash_on_rounded,
    ),
    _CdnProviderDef(
      key: 'psiphon-bunny',
      name: 'Psiphon Bunny',
      badge: 'Bunny CDN',
      color: Color(0xFFFF9500),
      icon: Icons.speed_rounded,
    ),
    _CdnProviderDef(
      key: 'cloudfront',
      name: 'CloudFront',
      badge: 'AWS CDN',
      color: Color(0xFFFF9900),
      icon: Icons.cloud_done_rounded,
    ),
    _CdnProviderDef(
      key: 'vercel',
      name: 'Vercel',
      badge: 'Global Edge',
      color: Color(0xFF94A3B8),
      icon: Icons.change_history_rounded,
    ),
    _CdnProviderDef(
      key: 'github',
      name: 'GitHub',
      badge: 'Pages CDN',
      color: Color(0xFFA855F7),
      icon: Icons.code_rounded,
    ),
    _CdnProviderDef(
      key: 'curated-fronting',
      name: 'Curated Fronting',
      badge: 'Direct Fronts',
      color: Color(0xFF10B981),
      icon: Icons.verified_rounded,
    ),
    _CdnProviderDef(
      key: 'legacy-android-overrides',
      name: 'Legacy Android',
      badge: 'Core Fallback',
      color: Color(0xFF6366F1),
      icon: Icons.phone_android_rounded,
    ),
  ];

  int _selectedIndex(String key) {
    return s.frontedMeekCDNScanBuiltInSets.indexWhere(
      (e) => e.toLowerCase() == key.toLowerCase(),
    );
  }

  void _toggle(String key) {
    set((x) {
      final list = List<String>.from(x.frontedMeekCDNScanBuiltInSets);
      final idx = list.indexWhere((e) => e.toLowerCase() == key.toLowerCase());
      if (idx >= 0) {
        list.removeAt(idx);
      } else {
        list.add(key);
      }
      x.frontedMeekCDNScanBuiltInSets = list;
    });
  }

  @override
  Widget build(BuildContext context) {
    final c = Theme.of(context).extension<AppColors>()!;
    final selected = s.frontedMeekCDNScanBuiltInSets;
    final isCustom = selected.isNotEmpty;

    return Container(
      padding: const EdgeInsets.all(12),
      decoration: BoxDecoration(
        color: c.input.withValues(alpha: 0.3),
        borderRadius: BorderRadius.circular(12),
        border: Border.all(color: isCustom ? BrandColors.accentCyan.withValues(alpha: 0.4) : c.border.withValues(alpha: 0.5)),
      ),
      child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
          Row(children: [
              const Icon(Icons.hub_rounded, size: 14, color: BrandColors.accentCyan),
              const SizedBox(width: 6),
              Text(str.cdnCorpus, style: TextStyle(fontSize: 12, fontWeight: FontWeight.w700, color: c.textPrimary)),
              const SizedBox(width: 8),
              Container(padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 2), decoration: BoxDecoration(color: isCustom ? BrandColors.accentCyan.withValues(alpha: 0.15) : c.cardElevated.withValues(alpha: 0.6), borderRadius: BorderRadius.circular(6)), child: Text(isCustom ? '${selected.length}/10' : 'All', style: TextStyle(fontSize: 10, fontWeight: FontWeight.w600, color: isCustom ? BrandColors.accentCyan : c.textMuted))),
            ]),
          const SizedBox(height: 10),
          if (isCustom) ...[
            SingleChildScrollView(scrollDirection: Axis.horizontal, child: Row(children: [for (int i = 0; i < selected.length; i++) ...[if (i > 0) Padding(padding: const EdgeInsets.symmetric(horizontal: 4), child: Icon(Icons.chevron_right_rounded, size: 12, color: c.textMuted)), _QueueItemBadge(index: i + 1, keyName: selected[i], onRemove: () => _toggle(selected[i]), c: c)]])),
            const SizedBox(height: 8),
          ],

          Wrap(
            spacing: 8,
            runSpacing: 8,
            children: [
              for (final p in _providers)
                Builder(
                  builder: (context) {
                    final selIdx = _selectedIndex(p.key);
                    final isSel = selIdx >= 0;
                    return InkWell(
                      onTap: () => _toggle(p.key),
                      borderRadius: BorderRadius.circular(9),
                      child: AnimatedContainer(
                        duration: const Duration(milliseconds: 160),
                        padding: const EdgeInsets.symmetric(horizontal: 9, vertical: 7),
                        decoration: BoxDecoration(
                          color: isSel
                              ? p.color.withValues(alpha: 0.14)
                              : c.card.withValues(alpha: 0.4),
                          borderRadius: BorderRadius.circular(9),
                          border: Border.all(
                            color: isSel
                                ? p.color.withValues(alpha: 0.8)
                                : c.border.withValues(alpha: 0.45),
                            width: isSel ? 1.4 : 1.0,
                          ),
                          boxShadow: isSel
                              ? [
                                  BoxShadow(
                                    color: p.color.withValues(alpha: 0.18),
                                    blurRadius: 6,
                                    offset: const Offset(0, 2),
                                  ),
                                ]
                              : null,
                        ),
                        child: Row(
                          mainAxisSize: MainAxisSize.min,
                          children: [
                            if (isSel)
                              Container(
                                padding: const EdgeInsets.symmetric(horizontal: 5, vertical: 1.5),
                                margin: const EdgeInsets.only(right: 6),
                                decoration: BoxDecoration(
                                  color: p.color,
                                  borderRadius: BorderRadius.circular(5),
                                ),
                                child: Text(
                                  '#${selIdx + 1}',
                                  style: const TextStyle(
                                    fontSize: 9.5,
                                    fontWeight: FontWeight.w800,
                                    color: Colors.white,
                                  ),
                                ),
                              )
                            else
                              Container(
                                width: 14,
                                height: 14,
                                margin: const EdgeInsets.only(right: 6),
                                decoration: BoxDecoration(
                                  shape: BoxShape.circle,
                                  border: Border.all(color: c.textMuted.withValues(alpha: 0.6), width: 1.2),
                                ),
                                child: Icon(Icons.add, size: 10, color: c.textMuted),
                              ),
                            Icon(p.icon, size: 13, color: isSel ? p.color : c.textMuted),
                            const SizedBox(width: 5),
                            Text(
                              p.name,
                              style: TextStyle(
                                fontSize: 11.5,
                                fontWeight: isSel ? FontWeight.w700 : FontWeight.w500,
                                color: isSel ? c.textPrimary : c.textSecondary,
                              ),
                            ),
                          ],
                        ),
                      ),
                    );
                  },
                ),
            ],
          ),

          const SizedBox(height: 12),

          Row(
            children: [
              _SmallButton(
                label: '⚡ Cloudflare + Google',
                onTap: () => set((x) => x.frontedMeekCDNScanBuiltInSets = ['cloudflare', 'google']),
              ),
              const SizedBox(width: 8),
              _SmallButton(
                label: '🛡️ Akamai + Fastly',
                onTap: () => set((x) => x.frontedMeekCDNScanBuiltInSets = ['psiphon-akamai', 'fastly']),
              ),
              const SizedBox(width: 8),
              _SmallButton(
                label: str.allCount(10),
                onTap: () => set((x) => x.frontedMeekCDNScanBuiltInSets = _providers.map((e) => e.key).toList()),
              ),
              const Spacer(),
              _SmallButton(
                label: str.cdnClearAuto,
                onTap: () => set((x) => x.frontedMeekCDNScanBuiltInSets = []),
              ),
            ],
          ),
        ],
      ),
    );
  }
}

class _QueueItemBadge extends StatelessWidget {
  const _QueueItemBadge({
    required this.index,
    required this.keyName,
    required this.onRemove,
    required this.c,
  });

  final int index;
  final String keyName;
  final VoidCallback onRemove;
  final AppColors c;

  static String _prettyName(String key) {
    switch (key.toLowerCase()) {
      case 'cloudflare':
        return 'Cloudflare';
      case 'google':
        return 'Google';
      case 'psiphon-akamai':
        return 'Akamai';
      case 'fastly':
        return 'Fastly';
      case 'psiphon-bunny':
        return 'Bunny';
      case 'cloudfront':
        return 'CloudFront';
      case 'vercel':
        return 'Vercel';
      case 'github':
        return 'GitHub';
      case 'curated-fronting':
        return 'Curated';
      case 'legacy-android-overrides':
        return 'Android';
      default:
        return key;
    }
  }

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 7, vertical: 3),
      decoration: BoxDecoration(
        color: c.cardElevated,
        borderRadius: BorderRadius.circular(6),
        border: Border.all(color: BrandColors.accentCyan.withValues(alpha: 0.4)),
      ),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          Container(
            padding: const EdgeInsets.symmetric(horizontal: 4, vertical: 1),
            decoration: BoxDecoration(
              color: BrandColors.accentCyan,
              borderRadius: BorderRadius.circular(4),
            ),
            child: Text(
              '#$index',
              style: const TextStyle(fontSize: 9, fontWeight: FontWeight.w800, color: Colors.black87),
            ),
          ),
          const SizedBox(width: 5),
          Text(
            _prettyName(keyName),
            style: TextStyle(fontSize: 11, fontWeight: FontWeight.w600, color: c.textPrimary),
          ),
          const SizedBox(width: 4),
          InkWell(
            onTap: onRemove,
            child: Icon(Icons.close_rounded, size: 12, color: c.textMuted),
          ),
        ],
      ),
    );
  }
}

class _SmallButton extends StatelessWidget {
  const _SmallButton({required this.label, required this.onTap});
  final String label;
  final VoidCallback onTap;
  @override
  Widget build(BuildContext context) {
    final c = Theme.of(context).extension<AppColors>()!;
    return GestureDetector(
      onTap: onTap,
      child: Container(
        padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 6),
        decoration: BoxDecoration(
          color: c.card.withValues(alpha: 0.7),
          borderRadius: BorderRadius.circular(7),
          border: Border.all(color: c.border.withValues(alpha: 0.6)),
        ),
        child: Text(label, style: TextStyle(fontSize: 11, fontWeight: FontWeight.w600, color: c.textSecondary)),
      ),
    );
  }
}

class _TorCoreTile extends ConsumerWidget {
  const _TorCoreTile({
    required this.installedVer,
    required this.isChecking,
    required this.statusText,
    required this.onCheck,
    required this.c,
  });

  final String installedVer;
  final bool isChecking;
  final String statusText;
  final VoidCallback onCheck;
  final AppColors c;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final str = ref.watch(stringsProvider);
    return Container(
      padding: const EdgeInsets.all(12),
      decoration: BoxDecoration(
        color: c.input.withValues(alpha: 0.45),
        borderRadius: BorderRadius.circular(10),
        border: Border.all(color: c.border.withValues(alpha: 0.5)),
      ),
      child: Row(
        children: [
          Container(
            width: 34,
            height: 34,
            decoration: BoxDecoration(
              color: const Color(0xFF9C27B0).withValues(alpha: 0.14),
              borderRadius: BorderRadius.circular(8),
              border: Border.all(color: const Color(0xFF9C27B0).withValues(alpha: 0.3)),
            ),
            child: const Icon(Icons.masks_rounded, size: 18, color: Color(0xFFBA68C8)),
          ),
          const SizedBox(width: 10),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Row(
                  children: [
                    Text(
                      str.torCoreTitle,
                      style: TextStyle(
                        fontSize: 13,
                        fontWeight: FontWeight.w600,
                        color: c.textPrimary,
                      ),
                    ),
                    const SizedBox(width: 8),
                    Container(
                      padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 1.5),
                      decoration: BoxDecoration(
                        color: c.card.withValues(alpha: 0.7),
                        borderRadius: BorderRadius.circular(4),
                        border: Border.all(color: c.border.withValues(alpha: 0.6)),
                      ),
                      child: Text(
                        'v$installedVer',
                        style: AppTheme.mono(c.textSecondary, size: 10.5, weight: FontWeight.w500),
                      ),
                    ),
                  ],
                ),
                const SizedBox(height: 2),
                Text(
                  str.torCoreSubtitle,
                  style: TextStyle(fontSize: 11, color: c.textMuted),
                ),
              ],
            ),
          ),
          const SizedBox(width: 10),
          Text(
            statusText,
            style: TextStyle(fontSize: 11.5, color: c.textMuted),
          ),
          const SizedBox(width: 8),
          IconButton(
            icon: isChecking
                ? const SizedBox(
                    width: 14,
                    height: 14,
                    child: CircularProgressIndicator(strokeWidth: 2, color: BrandColors.accentCyan),
                  )
                : Icon(Icons.refresh_rounded, size: 16, color: c.textSecondary),
            tooltip: str.torVerifyTooltip,
            onPressed: isChecking ? null : onCheck,
            splashRadius: 16,
          ),
        ],
      ),
    );
  }
}
