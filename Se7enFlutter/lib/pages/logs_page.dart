import 'dart:async';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../core/i18n/app_strings.dart';
import '../core/services/providers.dart';
import '../theme/app_colors.dart';
import '../theme/app_theme.dart';
import '../widgets/glass_controls.dart';

enum LogEngineFilter {
  all(
    label: 'All Logs',
    icon: Icons.list_alt_rounded,
    keywords: [],
  ),
  errors(
    label: 'Errors & Warnings',
    icon: Icons.warning_amber_rounded,
    keywords: ['error', 'err', 'fail', 'exception', 'fatal', 'panic', 'warn', 'aborted', 'denied'],
  ),
  psiphon(
    label: 'Psiphon Core',
    icon: Icons.shield_outlined,
    keywords: ['[psiphon]', 'psiphon', 'psi', 'tactics', 'ssh tunnel', 'meek', 'fronted', 'handshake'],
  ),
  v2ray(
    label: 'V2Ray / Xray',
    icon: Icons.hub_rounded,
    keywords: ['[v2ray]', 'v2ray', 'xray', 'sing-box', 'singbox', 'reality', 'vless', 'vmess', 'trojan', '10808'],
  ),
  aether(
    label: 'Cloudflare WARP',
    icon: Icons.bolt_rounded,
    keywords: ['[warp]', '[aether]', 'aether', 'warp', 'wireguard', 'masque', 'quic', 'connect-ip'],
  ),
  tor(
    label: 'Tor Network',
    icon: Icons.masks_rounded,
    keywords: ['[tor]', 'onion', 'snowflake', 'obfs4', 'lyrebird', 'webtunnel', 'conjure', 'circuit', 'bootstrap'],
  ),
  tunnel(
    label: 'TUN & Routing',
    icon: Icons.alt_route_rounded,
    keywords: ['[tun]', 'wintun', 'route', 'routing', 'dns', 'split tunnel', 'system proxy', 'kill switch', 'adapter'],
  ),
  shard(
    label: 'SHARD',
    icon: Icons.grain_rounded,
    keywords: ['[shard]', 'shard', 'msn-guard', 'finalmask', 'cipher', '1824'],
  );

  const LogEngineFilter({
    required this.label,
    required this.icon,
    required this.keywords,
  });

  final String label;
  final IconData icon;
  final List<String> keywords;

  bool matches(String line) {
    if (this == LogEngineFilter.all) return true;
    final lower = line.toLowerCase();

    if (this == LogEngineFilter.errors) {
      for (final kw in keywords) {
        if (lower.contains(kw)) return true;
      }
      return false;
    }

    final hasPsiphonTag = lower.contains('[psiphon]') || lower.contains('[psi]');
    final hasTorTag = lower.contains('[tor]');
    final hasTunTag = lower.contains('[tun]') || lower.contains('[wintun]');
    final hasWarpTag = lower.contains('[warp]') || lower.contains('[aether]');
    final hasV2rayTag = lower.contains('[v2ray]');
    final hasUltraTag = lower.contains('[ultracore]') || lower.contains('[ultra]');
    final hasShardTag = lower.contains('[shard]');

    if (this == LogEngineFilter.psiphon) {
      if (hasPsiphonTag) return true;
      if (hasTorTag || hasTunTag || hasWarpTag || hasV2rayTag || hasUltraTag || hasShardTag) return false;
      return lower.contains('psiphon') || lower.contains('ssh tunnel') || lower.contains('meek');
    }

    if (this == LogEngineFilter.tor) {
      if (hasTorTag) return true;
      if (hasPsiphonTag || hasTunTag || hasWarpTag || hasV2rayTag || hasUltraTag || hasShardTag) return false;
      final torWordMatch = RegExp(r'\btor\b').hasMatch(lower);
      return torWordMatch ||
          lower.contains('onion') ||
          lower.contains('snowflake') ||
          lower.contains('obfs4') ||
          lower.contains('webtunnel') ||
          lower.contains('lyrebird') ||
          lower.contains('circuit established');
    }

    if (this == LogEngineFilter.tunnel) {
      if (hasTunTag) return true;
      if (hasPsiphonTag || hasTorTag) return false;
      for (final kw in keywords) {
        if (lower.contains(kw)) return true;
      }
      return false;
    }

    if (this == LogEngineFilter.v2ray) {
      if (hasV2rayTag) return true;
      if (hasPsiphonTag || hasTorTag || hasUltraTag || hasShardTag) return false;
      for (final kw in keywords) {
        if (lower.contains(kw)) return true;
      }
      return false;
    }

    if (this == LogEngineFilter.aether) {
      if (hasWarpTag) return true;
      if (hasPsiphonTag || hasTorTag || hasUltraTag || hasShardTag) return false;
      for (final kw in keywords) {
        if (lower.contains(kw)) return true;
      }
      return false;
    }

    if (this == LogEngineFilter.shard) {
      if (hasShardTag) return true;
      if (hasPsiphonTag || hasTorTag || hasTunTag || hasWarpTag || hasV2rayTag || hasUltraTag) return false;
      for (final kw in keywords) {
        if (lower.contains(kw)) return true;
      }
      return false;
    }

    return false;
  }

  String getLocalizedLabel(AppStrings str) {
    switch (this) {
      case LogEngineFilter.all:
        return str.logsAll;
      case LogEngineFilter.errors:
        return str.logsErrors;
      case LogEngineFilter.psiphon:
        return str.logsPsiphon;
      case LogEngineFilter.v2ray:
        return str.logsV2ray;
      case LogEngineFilter.aether:
        return str.logsWarp;
      case LogEngineFilter.tor:
        return str.logsTor;
      case LogEngineFilter.tunnel:
        return str.logsTun;
      case LogEngineFilter.shard:
        return str.logsShard;
    }
  }
}

class LogsPage extends ConsumerStatefulWidget {
  const LogsPage({super.key});

  @override
  ConsumerState<LogsPage> createState() => _LogsPageState();
}

class _LogsPageState extends ConsumerState<LogsPage> {
  static const int _maxHistory = 1200;
  final _lines = <String>[];
  final _pendingLines = <String>[];
  Timer? _batchTimer;
  final _scroll = ScrollController();
  StreamSubscription<String>? _sub;
  bool _autoScroll = true;
  String _filter = '';
  LogEngineFilter _engineFilter = LogEngineFilter.all;

  @override
  void initState() {
    super.initState();
    final core = ref.read(coreClientProvider);
    final initial = core.recentLog;
    if (initial.length > _maxHistory) {
      _lines.addAll(initial.sublist(initial.length - _maxHistory));
    } else {
      _lines.addAll(initial);
    }

    _batchTimer = Timer.periodic(const Duration(milliseconds: 120), (_) => _flushPendingLogs());

    _sub = core.logStream.listen((line) {
      _pendingLines.add(line);
    });

    WidgetsBinding.instance.addPostFrameCallback((_) => _jumpToEnd());
  }

  void _flushPendingLogs() {
    if (!mounted || _pendingLines.isEmpty) return;
    final toAdd = List<String>.from(_pendingLines);
    _pendingLines.clear();

    setState(() {
      _lines.addAll(toAdd);
      if (_lines.length > _maxHistory) {
        _lines.removeRange(0, _lines.length - _maxHistory);
      }
    });
    _maybeAutoScroll();
  }

  @override
  void dispose() {
    _batchTimer?.cancel();
    _sub?.cancel();
    _scroll.dispose();
    super.dispose();
  }

  void _maybeAutoScroll() {
    if (!_autoScroll) return;
    WidgetsBinding.instance.addPostFrameCallback((_) => _jumpToEnd());
  }

  void _jumpToEnd() {
    if (_scroll.hasClients) {
      _scroll.jumpTo(_scroll.position.maxScrollExtent);
    }
  }

  void _clearLogs() {
    setState(() {
      _lines.clear();
      _pendingLines.clear();
    });
    ref.read(coreClientProvider).clearRecentLog();
  }

  @override
  Widget build(BuildContext context) {
    final c = Theme.of(context).extension<AppColors>()!;
    final str = ref.watch(stringsProvider);
    final f = _filter.trim().toLowerCase();
    final visible = _lines.where((l) {
      if (!_engineFilter.matches(l)) return false;
      if (f.isNotEmpty && !l.toLowerCase().contains(f)) return false;
      return true;
    }).toList();

    return Padding(
      padding: const EdgeInsets.fromLTRB(24, 6, 24, 20),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [

          _ModernLogToolbar(
            str: str,
            count: visible.length,
            autoScroll: _autoScroll,
            engineFilter: _engineFilter,
            onAutoScroll: (v) {
              setState(() => _autoScroll = v);
              if (v) _maybeAutoScroll();
            },
            onEngineFilterChanged: (eng) => setState(() => _engineFilter = eng),
            onFilter: (v) => setState(() => _filter = v),
            onCopy: () => Clipboard.setData(ClipboardData(text: visible.join('\n'))),
            onClear: _clearLogs,
          ),
          const SizedBox(height: 12),

          Expanded(
            child: GlassCard(
              padding: const EdgeInsets.all(4),
              borderRadius: 14,
              fillColor: c.isDark ? const Color(0xFF090D16) : const Color(0xFFF1F5F9),
              child: ClipRRect(
                borderRadius: BorderRadius.circular(10),
                child: visible.isEmpty
                    ? Center(
                        child: Text(
                          str.noLogsYet,
                          style: TextStyle(fontSize: 12.5, color: c.textMuted),
                        ),
                      )
                    : SelectionArea(
                        child: Scrollbar(
                          controller: _scroll,
                          child: ListView.builder(
                            controller: _scroll,
                            padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 10),
                            itemCount: visible.length,
                            itemBuilder: (context, i) => _ModernLogEntry(text: visible[i]),
                          ),
                        ),
                      ),
              ),
            ),
          ),
        ],
      ),
    );
  }
}

class _ModernLogToolbar extends StatelessWidget {
  const _ModernLogToolbar({
    required this.str,
    required this.count,
    required this.autoScroll,
    required this.engineFilter,
    required this.onAutoScroll,
    required this.onEngineFilterChanged,
    required this.onFilter,
    required this.onCopy,
    required this.onClear,
  });

  final AppStrings str;
  final int count;
  final bool autoScroll;
  final LogEngineFilter engineFilter;
  final ValueChanged<bool> onAutoScroll;
  final ValueChanged<LogEngineFilter> onEngineFilterChanged;
  final ValueChanged<String> onFilter;
  final VoidCallback onCopy;
  final VoidCallback onClear;

  @override
  Widget build(BuildContext context) {
    final c = Theme.of(context).extension<AppColors>()!;
    return Row(
      children: [
        Expanded(
          child: SizedBox(
            height: 38,
            child: TextField(
              onChanged: onFilter,
              style: TextStyle(fontSize: 12.5, color: c.textPrimary),
              textAlignVertical: TextAlignVertical.center,
              decoration: InputDecoration(
                hintText: str.isZh ? '搜索日志… ($count)' : (str.isRu ? 'Поиск в журнале… ($count)' : 'Search notices and network logs… ($count items)'),
                hintStyle: TextStyle(fontSize: 12, color: c.textMuted),
                prefixIcon: Icon(Icons.search_rounded, size: 17, color: c.textSecondary),
                isDense: true,
                filled: true,
                fillColor: c.input.withValues(alpha: 0.65),
                contentPadding: const EdgeInsets.symmetric(horizontal: 10),
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
        ),
        const SizedBox(width: 10),
        _ActionChip(
          label: str.isZh ? '自动滚动' : (str.isRu ? 'Автопрокрутка' : 'Auto-scroll'),
          icon: Icons.vertical_align_bottom_rounded,
          active: autoScroll,
          onTap: () => onAutoScroll(!autoScroll),
        ),
        const SizedBox(width: 8),
        _EngineFilterDropdown(
          selected: engineFilter,
          onChanged: onEngineFilterChanged,
          str: str,
        ),
        const SizedBox(width: 8),
        _ButtonIcon(icon: Icons.copy_all_rounded, tooltip: str.copyLogs, onTap: onCopy),
        const SizedBox(width: 6),
        _ButtonIcon(icon: Icons.delete_sweep_rounded, tooltip: str.clearLogs, onTap: onClear),
      ],
    );
  }
}

class _EngineFilterDropdown extends StatelessWidget {
  const _EngineFilterDropdown({
    required this.selected,
    required this.onChanged,
    required this.str,
  });

  final LogEngineFilter selected;
  final ValueChanged<LogEngineFilter> onChanged;
  final AppStrings str;

  @override
  Widget build(BuildContext context) {
    final c = Theme.of(context).extension<AppColors>()!;
    final isFiltered = selected != LogEngineFilter.all;

    return ClipRRect(
      borderRadius: BorderRadius.circular(10),
      child: Material(
        color: Colors.transparent,
        child: ModernPopupMenuButton<LogEngineFilter>(
          tooltip: str.logsFilterTooltip,
          onSelected: onChanged,
          itemBuilder: (context) => [
            for (final eng in LogEngineFilter.values)
              PopupMenuItem<LogEngineFilter>(
                value: eng,
                padding: EdgeInsets.zero,
                height: 38,
                child: ModernPopupHoverTile(
                  child: Row(
                    children: [
                      Icon(
                        eng.icon,
                        size: 16,
                        color: eng == selected ? BrandColors.accentCyan : c.textSecondary,
                      ),
                      const SizedBox(width: 10),
                      Expanded(
                        child: Text(
                          eng.getLocalizedLabel(str),
                          style: TextStyle(
                            fontSize: 12.5,
                            fontWeight: eng == selected ? FontWeight.w700 : FontWeight.w500,
                            color: eng == selected ? BrandColors.accentCyan : c.textPrimary,
                          ),
                        ),
                      ),
                      if (eng == selected)
                        const Icon(Icons.check_rounded, size: 16, color: BrandColors.accentCyan),
                    ],
                  ),
                ),
              ),
          ],
          child: AnimatedContainer(
            duration: const Duration(milliseconds: 140),
            height: 38,
            padding: const EdgeInsets.symmetric(horizontal: 11),
            decoration: BoxDecoration(
              color: isFiltered
                  ? BrandColors.accentCyan.withValues(alpha: 0.16)
                  : c.card.withValues(alpha: 0.5),
              borderRadius: BorderRadius.circular(10),
              border: Border.all(
                color: isFiltered
                    ? BrandColors.accentCyan.withValues(alpha: 0.5)
                    : c.border.withValues(alpha: 0.6),
              ),
            ),
            child: Row(
              mainAxisSize: MainAxisSize.min,
              children: [
                Icon(
                  selected.icon,
                  size: 15,
                  color: isFiltered ? BrandColors.accentCyan : c.textSecondary,
                ),
                const SizedBox(width: 7),
                Text(
                  selected.getLocalizedLabel(str),
                  style: TextStyle(
                    fontSize: 12,
                    fontWeight: isFiltered ? FontWeight.w700 : FontWeight.w500,
                    color: isFiltered ? BrandColors.accentCyan : c.textSecondary,
                  ),
                ),
                const SizedBox(width: 5),
                Icon(
                  Icons.unfold_more_rounded,
                  size: 15,
                  color: isFiltered ? BrandColors.accentCyan : c.textMuted,
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}

class _ActionChip extends StatelessWidget {
  const _ActionChip({
    required this.label,
    required this.icon,
    required this.active,
    required this.onTap,
  });
  final String label;
  final IconData icon;
  final bool active;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final c = Theme.of(context).extension<AppColors>()!;
    return GestureDetector(
      onTap: onTap,
      child: AnimatedContainer(
        duration: const Duration(milliseconds: 140),
        height: 38,
        padding: const EdgeInsets.symmetric(horizontal: 12),
        decoration: BoxDecoration(
          color: active
              ? BrandColors.accentCyan.withValues(alpha: 0.16)
              : c.card.withValues(alpha: 0.5),
          borderRadius: BorderRadius.circular(10),
          border: Border.all(
            color: active
                ? BrandColors.accentCyan.withValues(alpha: 0.45)
                : c.border.withValues(alpha: 0.6),
          ),
        ),
        child: Row(
          children: [
            Icon(icon, size: 15, color: active ? BrandColors.accentCyan : c.textSecondary),
            const SizedBox(width: 6),
            Text(
              label,
              style: TextStyle(
                fontSize: 12,
                fontWeight: FontWeight.w600,
                color: active ? BrandColors.accentCyan : c.textSecondary,
              ),
            ),
          ],
        ),
      ),
    );
  }
}

class _ButtonIcon extends StatelessWidget {
  const _ButtonIcon({required this.icon, required this.tooltip, required this.onTap});
  final IconData icon;
  final String tooltip;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final c = Theme.of(context).extension<AppColors>()!;
    return Tooltip(
      message: tooltip,
      child: GestureDetector(
        onTap: onTap,
        child: Container(
          width: 38,
          height: 38,
          decoration: BoxDecoration(
            color: c.card.withValues(alpha: 0.6),
            borderRadius: BorderRadius.circular(10),
            border: Border.all(color: c.border.withValues(alpha: 0.6)),
          ),
          child: Icon(icon, size: 17, color: c.textSecondary),
        ),
      ),
    );
  }
}

class _ModernLogEntry extends StatelessWidget {
  const _ModernLogEntry({required this.text});
  final String text;

  Color _tint(AppColors c) {
    final low = text.toLowerCase();
    if (low.contains('error') || low.contains('failed') || low.contains('fatal')) {
      return BrandColors.danger;
    }
    if (low.contains('warn')) return BrandColors.warning;
    if (low.contains('connected') || low.contains('established') || low.contains('ok')) {
      return BrandColors.emerald;
    }
    return c.textSecondary;
  }

  @override
  Widget build(BuildContext context) {
    final c = Theme.of(context).extension<AppColors>()!;
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 2.5),
      child: Text(
        text,
        style: AppTheme.mono(_tint(c), size: 11.5, weight: FontWeight.w400),
      ),
    );
  }
}
