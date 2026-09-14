import 'dart:convert';
import 'dart:io';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../core/i18n/app_strings.dart';
import '../core/models/user_settings.dart';
import '../core/services/providers.dart';
import '../theme/app_colors.dart';
import 'config_list.dart';
import 'glass_controls.dart';

enum CoreType {
  xray,
  singBox,
}

class V2RayConfig {
  final String id;
  String name;
  String protocol;
  String address;
  int port;
  String userId;
  String security;
  String network;
  String? path;
  String? host;
  String? sni;
  String? publicKey;
  String? shortId;
  String? flow;
  bool isActive;
  int latency;
  DateTime lastTested;
  bool? enableFragment;

  V2RayConfig({
    required this.id,
    required this.name,
    this.protocol = 'vless',
    required this.address,
    required this.port,
    required this.userId,
    this.security = 'reality',
    this.network = 'tcp',
    this.path,
    this.host,
    this.sni,
    this.publicKey,
    this.shortId,
    this.flow,
    this.isActive = false,
    this.latency = -1,
    this.enableFragment,
    DateTime? lastTested,
  }) : lastTested = lastTested ?? DateTime.now();

  V2RayConfig copyWith({
    String? id,
    String? name,
    String? protocol,
    String? address,
    int? port,
    String? userId,
    String? security,
    String? network,
    String? path,
    String? host,
    String? sni,
    String? publicKey,
    String? shortId,
    String? flow,
    bool? isActive,
    int? latency,
    DateTime? lastTested,
    bool? enableFragment,
  }) {
    return V2RayConfig(
      id: id ?? this.id,
      name: name ?? this.name,
      protocol: protocol ?? this.protocol,
      address: address ?? this.address,
      port: port ?? this.port,
      userId: userId ?? this.userId,
      security: security ?? this.security,
      network: network ?? this.network,
      path: path ?? this.path,
      host: host ?? this.host,
      sni: sni ?? this.sni,
      publicKey: publicKey ?? this.publicKey,
      shortId: shortId ?? this.shortId,
      flow: flow ?? this.flow,
      isActive: isActive ?? this.isActive,
      latency: latency ?? this.latency,
      lastTested: lastTested ?? this.lastTested,
      enableFragment: enableFragment ?? this.enableFragment,
    );
  }

  V2RayConfigEntry toEntry() => V2RayConfigEntry(
        id: id,
        name: name,
        protocol: protocol,
        address: address,
        port: port,
        userId: userId,
        security: security,
        network: network,
        path: path,
        host: host,
        sni: sni,
        publicKey: publicKey,
        shortId: shortId,
        flow: flow,
        isActive: isActive,
        latency: latency,
        enableFragment: enableFragment,
      );

  factory V2RayConfig.fromEntry(V2RayConfigEntry e) => V2RayConfig(
        id: e.id,
        name: e.name,
        protocol: e.protocol,
        address: e.address,
        port: e.port,
        userId: e.userId,
        security: e.security,
        network: e.network,
        path: e.path,
        host: e.host,
        sni: e.sni,
        publicKey: e.publicKey,
        shortId: e.shortId,
        flow: e.flow,
        isActive: e.isActive,
        latency: e.latency,
        enableFragment: e.enableFragment,
      );
}

class PsiphonOverV2RaySection extends ConsumerStatefulWidget {
  const PsiphonOverV2RaySection({super.key, this.isTor = false});

  final bool isTor;

  @override
  ConsumerState<PsiphonOverV2RaySection> createState() => _PsiphonOverV2RaySectionState();
}

class _PsiphonOverV2RaySectionState extends ConsumerState<PsiphonOverV2RaySection> {
  CoreType _selectedCore = CoreType.xray;
  bool _isTestingAll = false;

  final List<V2RayConfig> _configs = [];

  String _inboundPort = '10808';
  late final TextEditingController _inboundPortCtrl;
  bool _routeDnsThroughV2Ray = true;
  bool _enableMultiplexing = false;
  bool _enableFragment = true;

  @override
  void initState() {
    super.initState();
    final s = ref.read(settingsProvider);
    _selectedCore = s.v2rayCore == 'sing_box' ? CoreType.singBox : CoreType.xray;
    _inboundPort = s.v2rayInboundPort > 0 ? s.v2rayInboundPort.toString() : '10808';
    _inboundPortCtrl = TextEditingController(text: _inboundPort);
    _enableMultiplexing = s.v2rayEnableMux;
    _enableFragment = s.v2rayEnableFragment;
    _routeDnsThroughV2Ray = s.v2rayRouteDnsThroughV2Ray;
    _configs.addAll(s.v2rayConfigs.map(V2RayConfig.fromEntry));
    if (s.v2rayActiveConfigId.isNotEmpty) {
      for (final c in _configs) {
        c.isActive = (c.id == s.v2rayActiveConfigId);
      }
    }
    if (!_configs.any((c) => c.isActive) && _configs.isNotEmpty) {
      _configs.first.isActive = true;
      if (s.v2rayActiveConfigId.isEmpty) {
        WidgetsBinding.instance.addPostFrameCallback((_) {
          if (!mounted) return;
          ref.read(settingsProvider.notifier).update((st) {
            if (st.v2rayConfigs.isNotEmpty) {
              st.v2rayActiveConfigId = st.v2rayConfigs.first.id;
              st.v2rayConfigs.first.isActive = true;
            }
          });
        });
      }
    }
  }

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();

    final s = ref.read(settingsProvider);
    final portStr = s.v2rayInboundPort > 0 ? s.v2rayInboundPort.toString() : '10808';
    if (portStr != _inboundPortCtrl.text) _inboundPortCtrl.text = portStr;
    if (_enableFragment != s.v2rayEnableFragment) {
      _enableFragment = s.v2rayEnableFragment;
    }
    if (_enableMultiplexing != s.v2rayEnableMux) {
      _enableMultiplexing = s.v2rayEnableMux;
    }
    if (_routeDnsThroughV2Ray != s.v2rayRouteDnsThroughV2Ray) {
      _routeDnsThroughV2Ray = s.v2rayRouteDnsThroughV2Ray;
    }
  }

  @override
  void dispose() {
    _inboundPortCtrl.dispose();
    super.dispose();
  }

  void _showAddConfigModal() {
    showDialog(
      context: context,
      builder: (context) => _AddConfigModal(
        str: ref.read(stringsProvider),
        onSave: (config) {
          setState(() {
            _configs.add(config);
            if (_configs.length == 1 || !_configs.any((c) => c.isActive)) {
              config.isActive = true;
            }
          });
          ref.read(settingsProvider.notifier).update((s) {
            s.v2rayConfigs.add(config.toEntry());
            if (s.v2rayConfigs.length == 1 || s.v2rayActiveConfigId.isEmpty) {
              s.v2rayActiveConfigId = config.id;
              s.v2rayConfigs.first.isActive = true;
            }
          });
        },
      ),
    );
  }

  void _showEditConfigModal(V2RayConfig config) {
    showDialog(
      context: context,
      builder: (context) => _AddConfigModal(
        editConfig: config,
        str: ref.read(stringsProvider),
        onSave: (updated) {
          setState(() {
            final idx = _configs.indexWhere((c) => c.id == config.id);
            if (idx != -1) {
              _configs[idx] = updated;
            }
          });
          ref.read(settingsProvider.notifier).update((s) {
            final idx = s.v2rayConfigs.indexWhere((c) => c.id == config.id);
            if (idx != -1) {
              s.v2rayConfigs[idx] = updated.toEntry();
            }
          });
        },
      ),
    );
  }

  void _showImportLinkModal() {
    showDialog(
      context: context,
      builder: (context) => _ImportLinkModal(
        str: ref.read(stringsProvider),
        onImport: (newConfigs) {
          setState(() {
            _configs.addAll(newConfigs);
            final hasActive = _configs.any((c) => c.isActive);
            if (!hasActive && _configs.isNotEmpty) {
              _configs.first.isActive = true;
            }
          });
          ref.read(settingsProvider.notifier).update((s) {
            s.v2rayConfigs.addAll(newConfigs.map((c) => c.toEntry()));
            final hasActive = s.v2rayConfigs.any((c) => c.id == s.v2rayActiveConfigId && c.isActive);
            if (!hasActive && s.v2rayConfigs.isNotEmpty) {
              s.v2rayActiveConfigId = s.v2rayConfigs.first.id;
              for (var i = 0; i < s.v2rayConfigs.length; i++) {
                s.v2rayConfigs[i].isActive = (i == 0);
              }
            }
          });
          ScaffoldMessenger.of(context).showSnackBar(
            SnackBar(
              content: Text(ref.read(stringsProvider).v2rayImportSuccess(newConfigs.length)),
              behavior: SnackBarBehavior.floating,
            ),
          );
        },
      ),
    );
  }

  Future<int> _measureRealDelay(V2RayConfig config) async {
    if (config.address.trim().isEmpty || config.port <= 0) return -1;
    try {
      final client = ref.read(coreClientProvider);
      return await client.testV2RayLatency(config.toEntry().toJson());
    } catch (_) {
      return -3;
    }
  }

  void _testLatency(V2RayConfig config) async {
    setState(() {
      config.latency = -2;
    });

    final ms = await _measureRealDelay(config);

    if (!mounted) return;
    setState(() {
      config.latency = ms;
      config.lastTested = DateTime.now();
    });

    ref.read(settingsProvider.notifier).update((s) {
      final found = s.v2rayConfigs.where((c) => c.id == config.id).firstOrNull;
      if (found != null) found.latency = ms;
    });
  }

  void _pingAll() async {
    if (_isTestingAll || _configs.isEmpty) return;
    setState(() {
      _isTestingAll = true;
      for (final c in _configs) {
        c.latency = -2;
      }
    });

    const batchSize = 4;
    for (var i = 0; i < _configs.length; i += batchSize) {
      if (!mounted) break;
      final batch = _configs.skip(i).take(batchSize).toList();
      await Future.wait(batch.map((c) async {
        final ms = await _measureRealDelay(c);
        if (mounted) {
          setState(() {
            c.latency = ms;
            c.lastTested = DateTime.now();
          });
        }
      }));
    }

    if (!mounted) return;
    setState(() {
      _isTestingAll = false;
    });

    ref.read(settingsProvider.notifier).update((s) {
      for (final c in _configs) {
        final found = s.v2rayConfigs.where((x) => x.id == c.id).firstOrNull;
        if (found != null) found.latency = c.latency;
      }
    });
  }

  void _deleteConfig(V2RayConfig config) {
    final str = ref.read(stringsProvider);
    showDialog(
      context: context,
      builder: (ctx) => AlertDialog(
        backgroundColor: Theme.of(ctx).extension<AppColors>()!.cardElevated,
        title: Text(str.v2rayDeletePrompt),
        content: Text(str.v2rayDeleteConfirm(config.name)),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(ctx),
            child: Text(str.cancel, style: const TextStyle(color: BrandColors.danger)),
          ),
          ElevatedButton(
            style: ElevatedButton.styleFrom(backgroundColor: Colors.redAccent),
            onPressed: () {
              Navigator.pop(ctx);
              setState(() {
                _configs.removeWhere((c) => c.id == config.id);
                if (config.isActive && _configs.isNotEmpty) {
                  _configs.first.isActive = true;
                }
              });
              ref.read(settingsProvider.notifier).update((s) {
                s.v2rayConfigs.removeWhere((c) => c.id == config.id);
                if (s.v2rayActiveConfigId == config.id) {
                  s.v2rayActiveConfigId = s.v2rayConfigs.isNotEmpty ? s.v2rayConfigs.first.id : '';
                  if (s.v2rayConfigs.isNotEmpty) s.v2rayConfigs.first.isActive = true;
                }
              });
            },
            child: Text(str.delete, style: const TextStyle(color: Colors.white)),
          ),
        ],
      ),
    );
  }

  void _confirmClearAll() {
    final str = ref.read(stringsProvider);
    final c = Theme.of(context).extension<AppColors>()!;
    showDialog(
      context: context,
      builder: (ctx) => AlertDialog(
        backgroundColor: c.cardElevated,
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(16),
          side: BorderSide(color: c.border.withValues(alpha: 0.3)),
        ),
        title: Row(
          children: [
            Container(
              padding: const EdgeInsets.all(8),
              decoration: BoxDecoration(
                color: BrandColors.danger.withValues(alpha: 0.15),
                borderRadius: BorderRadius.circular(8),
              ),
              child: const Icon(Icons.delete_sweep_rounded, color: BrandColors.danger, size: 20),
            ),
            const SizedBox(width: 12),
            Expanded(
              child: Text(
                str.v2rayClearAllConfirmTitle,
                style: TextStyle(fontSize: 15, fontWeight: FontWeight.w700, color: c.textPrimary),
              ),
            ),
          ],
        ),
        content: Text(
          str.v2rayClearAllConfirmDesc,
          style: TextStyle(fontSize: 13, color: c.textSecondary, height: 1.4),
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(ctx),
            child: Text(str.cancel, style: const TextStyle(color: BrandColors.danger)),
          ),
          ElevatedButton(
            style: ElevatedButton.styleFrom(
              backgroundColor: BrandColors.danger,
              foregroundColor: Colors.white,
              shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(8)),
            ),
            onPressed: () {
              Navigator.pop(ctx);
              setState(() {
                _configs.clear();
              });
              ref.read(settingsProvider.notifier).update((s) {
                s.v2rayConfigs.clear();
                s.v2rayActiveConfigId = '';
              });
            },
            child: Text(str.v2rayClearAll),
          ),
        ],
      ),
    );
  }

  void _toggleActive(V2RayConfig config) {
    setState(() {
      for (final c in _configs) {
        c.isActive = false;
      }
      config.isActive = true;
    });
    ref.read(settingsProvider.notifier).update((s) {
      s.v2rayActiveConfigId = config.id;
      for (final c in s.v2rayConfigs) {
        c.isActive = (c.id == config.id);
      }
    });
  }

  @override
  Widget build(BuildContext context) {
    final c = Theme.of(context).extension<AppColors>()!;
    final str = ref.watch(stringsProvider);
    final isTor = widget.isTor;

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [

        Container(
          padding: const EdgeInsets.all(14),
          decoration: BoxDecoration(
            color: BrandColors.accentPurple.withValues(alpha: 0.08),
            borderRadius: BorderRadius.circular(12),
            border: Border.all(color: BrandColors.accentPurple.withValues(alpha: 0.25)),
          ),
          child: Row(
            children: [
              Container(
                padding: const EdgeInsets.all(8),
                decoration: BoxDecoration(
                  color: BrandColors.accentPurple.withValues(alpha: 0.18),
                  borderRadius: BorderRadius.circular(8),
                ),
                child: Icon(
                  isTor
                      ? Icons.shield_moon_rounded
                      : Icons.bolt_rounded,
                  size: 20,
                  color: BrandColors.accentPurple,
                ),
              ),
              const SizedBox(width: 12),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      isTor
                          ? str.v2rayTorTitle
                          : str.v2rayPsiphonTitle,
                      style: TextStyle(
                        fontSize: 13,
                        fontWeight: FontWeight.w700,
                        color: c.textPrimary,
                      ),
                    ),
                    const SizedBox(height: 2),
                    Text(
                      isTor
                          ? str.v2rayTorDesc
                          : str.v2rayPsiphonDesc,
                      style: TextStyle(
                        fontSize: 11,
                        color: c.textSecondary,
                      ),
                    ),
                  ],
                ),
              ),
            ],
          ),
        ),

        const SizedBox(height: 16),

        GlassCard(
          borderRadius: 14,
          padding: const EdgeInsets.all(14),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Row(
                children: [
                  Container(
                    padding: const EdgeInsets.all(6),
                    decoration: BoxDecoration(
                      color: BrandColors.accentCyan.withValues(alpha: 0.15),
                      borderRadius: BorderRadius.circular(6),
                    ),
                    child: const Icon(
                      Icons.memory_rounded,
                      size: 16,
                      color: BrandColors.accentCyan,
                    ),
                  ),
                  const SizedBox(width: 10),
                  Text(
                    str.v2rayEngineCore,
                    style: TextStyle(
                      fontSize: 13,
                      fontWeight: FontWeight.w700,
                      color: c.textPrimary,
                    ),
                  ),
                  const Spacer(),
                  Container(
                    padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 3),
                    decoration: BoxDecoration(
                      color: BrandColors.accentCyan.withValues(alpha: 0.15),
                      borderRadius: BorderRadius.circular(6),
                      border: Border.all(color: BrandColors.accentCyan.withValues(alpha: 0.4)),
                    ),
                    child: Row(
                      mainAxisSize: MainAxisSize.min,
                      children: [
                        const Icon(Icons.check_circle_rounded, size: 12, color: BrandColors.success),
                        const SizedBox(width: 4),
                        Text(
                          _selectedCore == CoreType.xray ? str.v2rayXrayActive : str.v2raySingBoxActive,
                          style: const TextStyle(
                            fontSize: 10.5,
                            fontWeight: FontWeight.w600,
                            color: BrandColors.accentCyan,
                          ),
                        ),
                      ],
                    ),
                  ),
                ],
              ),

              const SizedBox(height: 12),

              Row(
                children: [
                  Expanded(
                    child: _CoreCardOption(
                      icon: Icons.bolt_rounded,
                      title: 'Xray Core',
                      badge: 'Xray',
                      badgeColor: BrandColors.accentCyan,
                      isSelected: _selectedCore == CoreType.xray,
                      onTap: () {
                        setState(() => _selectedCore = CoreType.xray);
                        ref.read(settingsProvider.notifier).update((s) => s.v2rayCore = 'xray');
                      },
                    ),
                  ),
                  const SizedBox(width: 10),
                  Expanded(
                    child: _CoreCardOption(
                      icon: Icons.shield_rounded,
                      title: 'Sing-Box Core',
                      badge: 'Sing-Box',
                      badgeColor: BrandColors.accentPurple,
                      isSelected: _selectedCore == CoreType.singBox,
                      onTap: () {
                        setState(() => _selectedCore = CoreType.singBox);
                        ref.read(settingsProvider.notifier).update((s) => s.v2rayCore = 'sing_box');
                      },
                    ),
                  ),
                ],
              ),
            ],
          ),
        ),

        const SizedBox(height: 16),

        GlassCard(
          borderRadius: 14,
          padding: const EdgeInsets.all(14),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Row(
                children: [
                  Container(
                    padding: const EdgeInsets.all(6),
                    decoration: BoxDecoration(
                      color: BrandColors.accentCyan.withValues(alpha: 0.15),
                      borderRadius: BorderRadius.circular(6),
                    ),
                    child: const Icon(
                      Icons.settings_input_component_rounded,
                      size: 16,
                      color: BrandColors.accentCyan,
                    ),
                  ),
                  const SizedBox(width: 10),
                  Text(
                    str.v2rayBridgeTitle,
                    style: TextStyle(
                      fontSize: 13,
                      fontWeight: FontWeight.w700,
                      color: c.textPrimary,
                    ),
                  ),
                ],
              ),
              const SizedBox(height: 12),

              Row(
                crossAxisAlignment: CrossAxisAlignment.center,
                children: [
                  Expanded(
                    child: Container(
                      height: 52,
                      padding: const EdgeInsets.symmetric(horizontal: 14),
                      decoration: BoxDecoration(
                        color: c.input.withValues(alpha: 0.6),
                        borderRadius: BorderRadius.circular(10),
                        border: Border.all(color: c.border.withValues(alpha: 0.5)),
                      ),
                      child: Row(
                        crossAxisAlignment: CrossAxisAlignment.center,
                        children: [
                          const Icon(Icons.lan_rounded, size: 16, color: BrandColors.accentCyan),
                          const SizedBox(width: 10),
                          Expanded(
                            child: Text(
                              str.inboundPortLabel,
                              style: TextStyle(
                                fontSize: 12,
                                fontWeight: FontWeight.w600,
                                color: c.textPrimary,
                              ),
                            ),
                          ),
                          Container(
                            width: 76,
                            height: 32,
                            alignment: Alignment.center,
                            decoration: BoxDecoration(
                              color: c.cardElevated,
                              borderRadius: BorderRadius.circular(8),
                              border: Border.all(color: c.border.withValues(alpha: 0.8)),
                            ),
                            child: TextField(
                              controller: _inboundPortCtrl,
                              onChanged: (v) {
                                _inboundPort = v;
                                final p = int.tryParse(v);
                                if (p != null && p > 0 && p <= 65535) {
                                  ref.read(settingsProvider.notifier).update((s) => s.v2rayInboundPort = p);
                                }
                              },
                              inputFormatters: [
                                FilteringTextInputFormatter.digitsOnly,
                                LengthLimitingTextInputFormatter(5),
                              ],
                              keyboardType: TextInputType.number,
                              textAlign: TextAlign.center,
                              textAlignVertical: TextAlignVertical.center,
                              style: const TextStyle(
                                fontSize: 13,
                                fontWeight: FontWeight.w700,
                                color: BrandColors.accentCyan,
                                height: 1.0,
                              ),
                              decoration: const InputDecoration(
                                isCollapsed: true,
                                contentPadding: EdgeInsets.zero,
                                border: InputBorder.none,
                                enabledBorder: InputBorder.none,
                                focusedBorder: InputBorder.none,
                              ),
                            ),
                          ),
                        ],
                      ),
                    ),
                  ),
                  const SizedBox(width: 12),
                  Expanded(
                    child: Container(
                      height: 52,
                      padding: const EdgeInsets.symmetric(horizontal: 14),
                      decoration: BoxDecoration(
                        color: c.input.withValues(alpha: 0.6),
                        borderRadius: BorderRadius.circular(10),
                        border: Border.all(color: c.border.withValues(alpha: 0.5)),
                      ),
                      child: Row(
                        crossAxisAlignment: CrossAxisAlignment.center,
                        children: [
                          const Icon(Icons.dns_rounded, size: 16, color: BrandColors.accentCyan),
                          const SizedBox(width: 10),
                          Expanded(
                            child: Text(
                              str.routeDnsLabel,
                              style: TextStyle(
                                fontSize: 12,
                                fontWeight: FontWeight.w600,
                                color: c.textPrimary,
                              ),
                            ),
                          ),
                          Transform.scale(
                            scale: 0.85,
                            child: Switch(
                              value: _routeDnsThroughV2Ray,
                              onChanged: (v) {
                                setState(() => _routeDnsThroughV2Ray = v);
                                ref.read(settingsProvider.notifier).update((s) => s.v2rayRouteDnsThroughV2Ray = v);
                              },
                            ),
                          ),
                        ],
                      ),
                    ),
                  ),
                ],
              ),
              const SizedBox(height: 10),

              Container(
                padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 10),
                decoration: BoxDecoration(
                  color: c.input.withValues(alpha: 0.6),
                  borderRadius: BorderRadius.circular(10),
                  border: Border.all(color: c.border.withValues(alpha: 0.5)),
                ),
                child: Row(
                  children: [
                    Container(
                      padding: const EdgeInsets.all(6),
                      decoration: BoxDecoration(
                        color: BrandColors.accentCyan.withValues(alpha: 0.12),
                        borderRadius: BorderRadius.circular(8),
                      ),
                      child: const Icon(Icons.alt_route_rounded, size: 16, color: BrandColors.accentCyan),
                    ),
                    const SizedBox(width: 12),
                    Expanded(
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          Text(
                            str.enableMuxLabel,
                            style: TextStyle(
                              fontSize: 12,
                              fontWeight: FontWeight.w600,
                              color: c.textPrimary,
                            ),
                          ),
                          const SizedBox(height: 2),
                          Text(
                            str.enableMuxDesc,
                            style: TextStyle(fontSize: 11, color: c.textMuted),
                          ),
                        ],
                      ),
                    ),
                    const SizedBox(width: 12),
                    Transform.scale(
                      scale: 0.85,
                      child: Switch(
                        value: _enableMultiplexing,
                        onChanged: (v) {
                          setState(() => _enableMultiplexing = v);
                          ref.read(settingsProvider.notifier).update((s) => s.v2rayEnableMux = v);
                        },
                      ),
                    ),
                  ],
                ),
              ),
              const SizedBox(height: 10),

              Container(
                padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 10),
                decoration: BoxDecoration(
                  color: c.input.withValues(alpha: 0.6),
                  borderRadius: BorderRadius.circular(10),
                  border: Border.all(color: c.border.withValues(alpha: 0.5)),
                ),
                child: Row(
                  children: [
                    Container(
                      padding: const EdgeInsets.all(6),
                      decoration: BoxDecoration(
                        color: BrandColors.accentPurple.withValues(alpha: 0.12),
                        borderRadius: BorderRadius.circular(8),
                      ),
                      child: const Icon(Icons.call_split_rounded, size: 16, color: BrandColors.accentPurple),
                    ),
                    const SizedBox(width: 12),
                    Expanded(
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          Row(
                            children: [
                              Text(
                                str.v2rayFragmentLabel,
                                style: TextStyle(
                                  fontSize: 12,
                                  fontWeight: FontWeight.w600,
                                  color: c.textPrimary,
                                ),
                              ),
                              const SizedBox(width: 8),
                              Container(
                                padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 2),
                                decoration: BoxDecoration(
                                  color: BrandColors.accentPurple.withValues(alpha: 0.15),
                                  borderRadius: BorderRadius.circular(4),
                                ),
                                child: const Text(
                                  'tlshello / 100-200 / 10-20',
                                  style: TextStyle(
                                    fontSize: 9,
                                    fontFamily: 'Consolas',
                                    color: BrandColors.accentPurple,
                                    fontWeight: FontWeight.w600,
                                  ),
                                ),
                              ),
                            ],
                          ),
                          const SizedBox(height: 2),
                          Text(
                            str.v2rayFragmentDesc,
                            style: TextStyle(fontSize: 11, color: c.textMuted),
                          ),
                        ],
                      ),
                    ),
                    const SizedBox(width: 12),
                    Transform.scale(
                      scale: 0.85,
                      child: Switch(
                        value: _enableFragment,
                        onChanged: (v) {
                          setState(() => _enableFragment = v);
                          ref.read(settingsProvider.notifier).update((s) => s.v2rayEnableFragment = v);
                        },
                      ),
                    ),
                  ],
                ),
              ),
            ],
          ),
        ),

        const SizedBox(height: 16),

        ConfigList(
          configs: _configs,
          selectedCore: _selectedCore,
          isTestingAll: _isTestingAll,
          onAddConfig: _showAddConfigModal,
          onImportConfig: _showImportLinkModal,
          onPingAll: _pingAll,
          onClearAll: _confirmClearAll,
          onEditConfig: _showEditConfigModal,
          onDeleteConfig: _deleteConfig,
          onTestLatency: _testLatency,
          onToggleActive: _toggleActive,
        ),
      ],
    );
  }
}

class _CoreCardOption extends StatefulWidget {
  const _CoreCardOption({
    required this.icon,
    required this.title,
    required this.badge,
    required this.badgeColor,
    required this.isSelected,
    required this.onTap,
  });

  final IconData icon;
  final String title;
  final String badge;
  final Color badgeColor;
  final bool isSelected;
  final VoidCallback onTap;

  @override
  State<_CoreCardOption> createState() => _CoreCardOptionState();
}

class _CoreCardOptionState extends State<_CoreCardOption> {
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
          duration: const Duration(milliseconds: 160),
          padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 12),
          decoration: BoxDecoration(
            color: widget.isSelected
                ? widget.badgeColor.withValues(alpha: 0.15)
                : _hovered
                    ? c.cardElevated.withValues(alpha: 0.8)
                    : c.card.withValues(alpha: 0.4),
            borderRadius: BorderRadius.circular(10),
            border: Border.all(
              color: widget.isSelected
                  ? widget.badgeColor.withValues(alpha: 0.7)
                  : _hovered
                      ? c.border
                      : c.border.withValues(alpha: 0.4),
              width: widget.isSelected ? 1.5 : 1,
            ),
          ),
          child: Row(
            children: [
              Container(
                padding: const EdgeInsets.all(8),
                decoration: BoxDecoration(
                  color: widget.isSelected
                      ? widget.badgeColor.withValues(alpha: 0.25)
                      : c.input.withValues(alpha: 0.5),
                  borderRadius: BorderRadius.circular(8),
                ),
                child: Icon(
                  widget.icon,
                  size: 20,
                  color: widget.isSelected ? widget.badgeColor : c.textSecondary,
                ),
              ),
              const SizedBox(width: 12),
              Expanded(
                child: Row(
                  children: [
                    Flexible(
                      child: Text(
                        widget.title,
                        style: TextStyle(
                          fontSize: 13,
                          fontWeight: widget.isSelected ? FontWeight.w700 : FontWeight.w600,
                          color: widget.isSelected ? c.textPrimary : c.textSecondary,
                        ),
                      ),
                    ),
                    const SizedBox(width: 8),
                    Container(
                      padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 2),
                      decoration: BoxDecoration(
                        color: widget.badgeColor.withValues(alpha: 0.2),
                        borderRadius: BorderRadius.circular(4),
                      ),
                      child: Text(
                        widget.badge,
                        style: TextStyle(
                          fontSize: 9.5,
                          fontWeight: FontWeight.w700,
                          color: widget.badgeColor,
                        ),
                      ),
                    ),
                  ],
                ),
              ),
              if (widget.isSelected)
                Container(
                  padding: const EdgeInsets.all(4),
                  decoration: BoxDecoration(
                    color: widget.badgeColor.withValues(alpha: 0.2),
                    borderRadius: BorderRadius.circular(6),
                  ),
                  child: Icon(
                    Icons.check_rounded,
                    size: 14,
                    color: widget.badgeColor,
                  ),
                ),
            ],
          ),
        ),
      ),
    );
  }
}

class _AddConfigModal extends StatefulWidget {
  const _AddConfigModal({this.editConfig, required this.onSave, required this.str});

  final V2RayConfig? editConfig;
  final ValueChanged<V2RayConfig> onSave;
  final AppStrings str;

  @override
  State<_AddConfigModal> createState() => _AddConfigModalState();
}

class _AddConfigModalState extends State<_AddConfigModal> {
  final _formKey = GlobalKey<FormState>();

  late final TextEditingController _nameCtrl;
  late final TextEditingController _addressCtrl;
  late final TextEditingController _portCtrl;
  late final TextEditingController _userIdCtrl;
  late final TextEditingController _sniCtrl;
  late final TextEditingController _pathCtrl;
  late final TextEditingController _publicKeyCtrl;
  late final TextEditingController _shortIdCtrl;

  late String _selectedProtocol;
  late String _selectedSecurity;
  late String _selectedNetwork;

  @override
  void initState() {
    super.initState();
    final e = widget.editConfig;
    _nameCtrl = TextEditingController(text: e?.name ?? '');
    _addressCtrl = TextEditingController(text: e?.address ?? '');
    _portCtrl = TextEditingController(text: e != null ? e.port.toString() : '443');
    _userIdCtrl = TextEditingController(text: e?.userId ?? '');
    _sniCtrl = TextEditingController(text: e?.sni ?? e?.host ?? '');
    _pathCtrl = TextEditingController(text: e?.path ?? '');
    _publicKeyCtrl = TextEditingController(text: e?.publicKey ?? '');
    _shortIdCtrl = TextEditingController(text: e?.shortId ?? '');

    _selectedProtocol = e?.protocol ?? 'vless';
    _selectedSecurity = e?.security ?? 'reality';
    _selectedNetwork = e?.network ?? 'tcp';
  }

  @override
  void dispose() {
    _nameCtrl.dispose();
    _addressCtrl.dispose();
    _portCtrl.dispose();
    _userIdCtrl.dispose();
    _sniCtrl.dispose();
    _pathCtrl.dispose();
    _publicKeyCtrl.dispose();
    _shortIdCtrl.dispose();
    super.dispose();
  }

  void _submit() {
    if (!_formKey.currentState!.validate()) return;

    final addressRaw = _addressCtrl.text.trim();
    final userRaw = _userIdCtrl.text.trim();
    if (addressRaw.isEmpty || userRaw.isEmpty) return;
    final name = _nameCtrl.text.trim().isEmpty ? 'Server $addressRaw' : _nameCtrl.text.trim();
    final address = addressRaw;
    final port = int.tryParse(_portCtrl.text.trim()) ?? 443;
    final userId = userRaw;

    final config = V2RayConfig(
      id: widget.editConfig?.id ?? 'cfg_${DateTime.now().millisecondsSinceEpoch}',
      name: name,
      protocol: _selectedProtocol,
      address: address,
      port: port,
      userId: userId,
      security: _selectedSecurity,
      network: _selectedNetwork,
      sni: _sniCtrl.text.trim().isNotEmpty ? _sniCtrl.text.trim() : null,
      host: _sniCtrl.text.trim().isNotEmpty ? _sniCtrl.text.trim() : null,
      path: _pathCtrl.text.trim().isNotEmpty ? _pathCtrl.text.trim() : null,
      publicKey: _publicKeyCtrl.text.trim().isNotEmpty ? _publicKeyCtrl.text.trim() : null,
      shortId: _shortIdCtrl.text.trim().isNotEmpty ? _shortIdCtrl.text.trim() : null,
      isActive: widget.editConfig?.isActive ?? false,
      latency: widget.editConfig?.latency ?? -1,
      enableFragment: widget.editConfig?.enableFragment,
    );

    widget.onSave(config);
    Navigator.pop(context);
  }

  @override
  Widget build(BuildContext context) {
    final c = Theme.of(context).extension<AppColors>()!;
    final isEdit = widget.editConfig != null;

    return Dialog(
      backgroundColor: Colors.transparent,
      insetPadding: const EdgeInsets.symmetric(horizontal: 20, vertical: 24),
      child: ConstrainedBox(
        constraints: const BoxConstraints(maxWidth: 580, maxHeight: 720),
        child: Container(
          decoration: BoxDecoration(
            color: c.cardElevated,
            borderRadius: BorderRadius.circular(16),
            border: Border.all(color: c.glassBorder, width: 1.2),
            boxShadow: [
              BoxShadow(
                color: Colors.black.withValues(alpha: 0.35),
                blurRadius: 30,
                offset: const Offset(0, 10),
              ),
            ],
          ),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [

              Container(
                padding: const EdgeInsets.fromLTRB(20, 16, 16, 16),
                decoration: BoxDecoration(
                  border: Border(bottom: BorderSide(color: c.border.withValues(alpha: 0.5))),
                ),
                child: Row(
                  children: [
                    Container(
                      padding: const EdgeInsets.all(8),
                      decoration: BoxDecoration(
                        color: BrandColors.accentCyan.withValues(alpha: 0.15),
                        borderRadius: BorderRadius.circular(8),
                      ),
                      child: Icon(
                        isEdit ? Icons.edit_rounded : Icons.add_rounded,
                        size: 18,
                        color: BrandColors.accentCyan,
                      ),
                    ),
                    const SizedBox(width: 12),
                    Expanded(
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          Text(
                            isEdit ? widget.str.v2rayEditConfig : widget.str.v2rayAddConfig,
                            style: TextStyle(
                              fontSize: 15,
                              fontWeight: FontWeight.w700,
                              color: c.textPrimary,
                            ),
                          ),
                          const SizedBox(height: 2),
                          Text(
                            isEdit ? widget.str.v2rayEditConfigDesc : widget.str.v2rayAddConfigDesc,
                            style: TextStyle(fontSize: 11, color: c.textMuted),
                          ),
                        ],
                      ),
                    ),
                    IconButton(
                      icon: Icon(Icons.close_rounded, size: 18, color: BrandColors.danger.withValues(alpha: 0.85)),
                      tooltip: 'Close',
                      onPressed: () => Navigator.pop(context),
                      splashRadius: 18,
                    ),
                  ],
                ),
              ),

              Flexible(
                child: SingleChildScrollView(
                  padding: const EdgeInsets.all(20),
                  child: Form(
                    key: _formKey,
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [

                        _ModalLabel(label: widget.str.v2rayProtocol),
                        const SizedBox(height: 6),
                        Row(
                          children: [
                            _ProtocolSelectPill(
                              label: 'VLESS',
                              isSelected: _selectedProtocol == 'vless',
                              color: BrandColors.accentCyan,
                              onTap: () => setState(() => _selectedProtocol = 'vless'),
                            ),
                            const SizedBox(width: 8),
                            _ProtocolSelectPill(
                              label: 'VMess',
                              isSelected: _selectedProtocol == 'vmess',
                              color: BrandColors.accentPurple,
                              onTap: () => setState(() => _selectedProtocol = 'vmess'),
                            ),
                            const SizedBox(width: 8),
                            _ProtocolSelectPill(
                              label: 'Trojan',
                              isSelected: _selectedProtocol == 'trojan',
                              color: Colors.orangeAccent,
                              onTap: () => setState(() => _selectedProtocol = 'trojan'),
                            ),
                            const SizedBox(width: 8),
                            _ProtocolSelectPill(
                              label: 'Shadowsocks',
                              isSelected: _selectedProtocol == 'shadowsocks',
                              color: BrandColors.success,
                              onTap: () => setState(() => _selectedProtocol = 'shadowsocks'),
                            ),
                          ],
                        ),

                        const SizedBox(height: 16),

                        _ModalInputField(
                          label: widget.str.v2rayProfileName,
                          hint: widget.str.v2rayProfileNameHint,
                          controller: _nameCtrl,
                          icon: Icons.label_outline_rounded,
                        ),

                        const SizedBox(height: 14),

                        Row(
                          crossAxisAlignment: CrossAxisAlignment.start,
                          children: [
                            Expanded(
                              flex: 3,
                              child: _ModalInputField(
                                label: widget.str.v2rayServerAddress,
                                hint: widget.str.v2rayServerAddressHint,
                                controller: _addressCtrl,
                                icon: Icons.dns_rounded,
                                validator: (v) => v == null || v.trim().isEmpty ? 'Required' : null,
                              ),
                            ),
                            const SizedBox(width: 12),
                            Expanded(
                              flex: 1,
                              child: _ModalInputField(
                                label: widget.str.v2rayPort,
                                hint: '443',
                                controller: _portCtrl,
                                icon: Icons.numbers_rounded,
                                keyboardType: TextInputType.number,
                                validator: (v) {
                                  if (v == null || v.trim().isEmpty) return widget.str.v2rayPortRequired;
                                  final p = int.tryParse(v.trim());
                                  if (p == null || p <= 0 || p > 65535) return 'Invalid';
                                  return null;
                                },
                              ),
                            ),
                          ],
                        ),

                        const SizedBox(height: 14),

                        _ModalInputField(
                          label: _selectedProtocol == 'trojan' || _selectedProtocol == 'shadowsocks'
                              ? 'Password'
                              : widget.str.v2rayUserId,
                          hint: _selectedProtocol == 'trojan' || _selectedProtocol == 'shadowsocks'
                              ? widget.str.v2rayPasswordHint
                              : 'e.g. 7d3a95f2-9cb0-46d2-8a91-7649d03429a1',
                          controller: _userIdCtrl,
                          icon: Icons.key_rounded,
                          validator: (v) => v == null || v.trim().isEmpty ? 'Required' : null,
                        ),

                        const SizedBox(height: 14),

                        Row(
                          children: [
                            Expanded(
                              child: _ModalDropdownField<String>(
                                label: widget.str.v2rayTransportNetwork,
                                value: _selectedNetwork,
                                items: const {
                                  'tcp': 'TCP',
                                  'ws': 'WebSocket',
                                  'grpc': 'gRPC',
                                  'http': 'HTTP/2',
                                },
                                onChanged: (v) => setState(() => _selectedNetwork = v),
                              ),
                            ),
                            const SizedBox(width: 12),
                            Expanded(
                              child: _ModalDropdownField<String>(
                                label: widget.str.v2raySecurityLayer,
                                value: _selectedSecurity,
                                items: {
                                  'reality': widget.str.v2raySecurityReality,
                                  'tls': widget.str.v2raySecurityTls,
                                  'none': widget.str.v2raySecurityNone,
                                },
                                onChanged: (v) => setState(() => _selectedSecurity = v),
                              ),
                            ),
                          ],
                        ),

                        const SizedBox(height: 14),

                        _ModalInputField(
                          label: widget.str.v2raySni,
                          hint: widget.str.v2raySniHint,
                          controller: _sniCtrl,
                          icon: Icons.public_rounded,
                        ),

                        if (_selectedNetwork == 'ws' || _selectedNetwork == 'http') ...[
                          const SizedBox(height: 14),
                          _ModalInputField(
                            label: widget.str.v2rayPath,
                            hint: widget.str.v2rayPathHint,
                            controller: _pathCtrl,
                            icon: Icons.alt_route_rounded,
                          ),
                        ],

                        if (_selectedSecurity == 'reality') ...[
                          const SizedBox(height: 14),
                          Row(
                            children: [
                              Expanded(
                                flex: 2,
                                child: _ModalInputField(
                                  label: widget.str.v2rayPublicKey,
                                  hint: widget.str.v2rayPublicKeyHint,
                                  controller: _publicKeyCtrl,
                                  icon: Icons.lock_outline_rounded,
                                ),
                              ),
                              const SizedBox(width: 12),
                              Expanded(
                                flex: 1,
                                child: _ModalInputField(
                                  label: widget.str.v2rayShortId,
                                  hint: widget.str.v2rayShortIdHint,
                                  controller: _shortIdCtrl,
                                  icon: Icons.fingerprint_rounded,
                                ),
                              ),
                            ],
                          ),
                        ],
                      ],
                    ),
                  ),
                ),
              ),

              Container(
                padding: const EdgeInsets.symmetric(horizontal: 20, vertical: 14),
                decoration: BoxDecoration(
                  border: Border(top: BorderSide(color: c.border.withValues(alpha: 0.5))),
                ),
                child: Row(
                  mainAxisAlignment: MainAxisAlignment.end,
                  children: [
                    OutlinedButton(
                      onPressed: () => Navigator.pop(context),
                      style: OutlinedButton.styleFrom(
                        foregroundColor: BrandColors.danger,
                        side: BorderSide(color: BrandColors.danger.withValues(alpha: 0.4)),
                        backgroundColor: BrandColors.danger.withValues(alpha: 0.06),
                        padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 10),
                        shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(8)),
                      ),
                      child: Text(widget.str.cancel, style: const TextStyle(fontSize: 12)),
                    ),
                    const SizedBox(width: 10),
                    ElevatedButton.icon(
                      onPressed: _submit,
                      icon: const Icon(Icons.check_rounded, size: 16),
                      label: Text(isEdit ? widget.str.v2raySaveChanges : widget.str.v2rayAddConfig, style: const TextStyle(fontSize: 12)),
                      style: ElevatedButton.styleFrom(
                        backgroundColor: BrandColors.accentCyan,
                        foregroundColor: Colors.white,
                        padding: const EdgeInsets.symmetric(horizontal: 18, vertical: 10),
                        shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(8)),
                      ),
                    ),
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

class _ModalLabel extends StatelessWidget {
  const _ModalLabel({required this.label});
  final String label;

  @override
  Widget build(BuildContext context) {
    final c = Theme.of(context).extension<AppColors>()!;
    return Text(
      label,
      style: TextStyle(
        fontSize: 11.5,
        fontWeight: FontWeight.w600,
        color: c.textSecondary,
      ),
    );
  }
}

class _ProtocolSelectPill extends StatelessWidget {
  const _ProtocolSelectPill({
    required this.label,
    required this.isSelected,
    required this.color,
    required this.onTap,
  });

  final String label;
  final bool isSelected;
  final Color color;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final c = Theme.of(context).extension<AppColors>()!;

    return Expanded(
      child: MouseRegion(
        cursor: SystemMouseCursors.click,
        child: GestureDetector(
          onTap: onTap,
          child: AnimatedContainer(
            duration: const Duration(milliseconds: 140),
            padding: const EdgeInsets.symmetric(vertical: 7),
            decoration: BoxDecoration(
              color: isSelected
                  ? color.withValues(alpha: 0.18)
                  : c.input.withValues(alpha: 0.5),
              borderRadius: BorderRadius.circular(8),
              border: Border.all(
                color: isSelected ? color : c.border.withValues(alpha: 0.5),
                width: isSelected ? 1.4 : 1,
              ),
            ),
            child: Center(
              child: Text(
                label,
                style: TextStyle(
                  fontSize: 11.5,
                  fontWeight: isSelected ? FontWeight.w700 : FontWeight.w500,
                  color: isSelected ? color : c.textPrimary,
                ),
              ),
            ),
          ),
        ),
      ),
    );
  }
}

class _ModalInputField extends StatelessWidget {
  const _ModalInputField({
    required this.label,
    required this.hint,
    required this.controller,
    this.icon,
    this.keyboardType,
    this.validator,
  });

  final String label;
  final String hint;
  final TextEditingController controller;
  final IconData? icon;
  final TextInputType? keyboardType;
  final String? Function(String?)? validator;

  @override
  Widget build(BuildContext context) {
    final c = Theme.of(context).extension<AppColors>()!;

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        _ModalLabel(label: label),
        const SizedBox(height: 5),
        TextFormField(
          controller: controller,
          keyboardType: keyboardType,
          validator: validator,
          style: TextStyle(fontSize: 12.5, color: c.textPrimary),
          decoration: InputDecoration(
            isDense: true,
            hintText: hint,
            hintStyle: TextStyle(fontSize: 11.5, color: c.textMuted),
            prefixIcon: icon != null
                ? Padding(
                    padding: const EdgeInsets.symmetric(horizontal: 10),
                    child: Icon(icon, size: 15, color: c.textMuted),
                  )
                : null,
            prefixIconConstraints: const BoxConstraints(minWidth: 36),
            filled: true,
            fillColor: c.input.withValues(alpha: 0.65),
            contentPadding: const EdgeInsets.symmetric(horizontal: 12, vertical: 11),
            border: OutlineInputBorder(
              borderRadius: BorderRadius.circular(8),
              borderSide: BorderSide(color: c.border.withValues(alpha: 0.5)),
            ),
            enabledBorder: OutlineInputBorder(
              borderRadius: BorderRadius.circular(8),
              borderSide: BorderSide(color: c.border.withValues(alpha: 0.5)),
            ),
            focusedBorder: OutlineInputBorder(
              borderRadius: BorderRadius.circular(8),
              borderSide: const BorderSide(color: BrandColors.accentCyan, width: 1.4),
            ),
            errorBorder: OutlineInputBorder(
              borderRadius: BorderRadius.circular(8),
              borderSide: const BorderSide(color: Colors.redAccent),
            ),
          ),
        ),
      ],
    );
  }
}

class _ModalDropdownField<T> extends StatefulWidget {
  const _ModalDropdownField({
    super.key,
    required this.label,
    required this.value,
    required this.items,
    required this.onChanged,
  });

  final String label;
  final T value;
  final Map<T, String> items;
  final ValueChanged<T> onChanged;

  @override
  State<_ModalDropdownField<T>> createState() => _ModalDropdownFieldState<T>();
}

class _ModalDropdownFieldState<T> extends State<_ModalDropdownField<T>> {
  bool _hovered = false;

  @override
  Widget build(BuildContext context) {
    final c = Theme.of(context).extension<AppColors>()!;

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        _ModalLabel(label: widget.label),
        const SizedBox(height: 5),
        MouseRegion(
          cursor: SystemMouseCursors.click,
          onEnter: (_) => setState(() => _hovered = true),
          onExit: (_) => setState(() => _hovered = false),
          child: ModernPopupMenuButton<T>(
            onSelected: widget.onChanged,
            itemBuilder: (context) => [
              for (final entry in widget.items.entries)
                PopupMenuItem<T>(
                  value: entry.key,
                  padding: EdgeInsets.zero,
                  height: 38,
                  child: ModernPopupHoverTile(
                    child: Row(
                      children: [
                        Expanded(
                          child: Text(
                            entry.value,
                            style: TextStyle(
                              fontSize: 12,
                              fontWeight: entry.key == widget.value ? FontWeight.w700 : FontWeight.w500,
                              color: entry.key == widget.value ? BrandColors.accentCyan : c.textPrimary,
                            ),
                          ),
                        ),
                        if (entry.key == widget.value) ...[
                          const SizedBox(width: 8),
                          const Icon(Icons.check_rounded, size: 15, color: BrandColors.accentCyan),
                        ],
                      ],
                    ),
                  ),
                ),
            ],
            child: AnimatedContainer(
              duration: const Duration(milliseconds: 140),
              padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 10),
              decoration: BoxDecoration(
                color: _hovered
                    ? c.cardElevated.withValues(alpha: 0.9)
                    : c.input.withValues(alpha: 0.65),
                borderRadius: BorderRadius.circular(8),
                border: Border.all(
                  color: _hovered
                      ? BrandColors.accentCyan.withValues(alpha: 0.55)
                      : c.border.withValues(alpha: 0.5),
                ),
              ),
              child: Row(
                children: [
                  Expanded(
                    child: Text(
                      widget.items[widget.value] ?? widget.value.toString(),
                      style: TextStyle(
                        fontSize: 12,
                        fontWeight: FontWeight.w600,
                        color: c.textPrimary,
                      ),
                      overflow: TextOverflow.ellipsis,
                    ),
                  ),
                  const SizedBox(width: 6),
                  Icon(Icons.unfold_more_rounded, size: 16, color: c.textSecondary),
                ],
              ),
            ),
          ),
        ),
      ],
    );
  }
}

class _ImportLinkModal extends StatefulWidget {
  const _ImportLinkModal({required this.onImport, required this.str});

  final ValueChanged<List<V2RayConfig>> onImport;
  final AppStrings str;

  @override
  State<_ImportLinkModal> createState() => _ImportLinkModalState();
}

class _ImportLinkModalState extends State<_ImportLinkModal> {
  final TextEditingController _urlCtrl = TextEditingController();
  String? _error;
  bool _isLoading = false;

  @override
  void dispose() {
    _urlCtrl.dispose();
    super.dispose();
  }

  void _pasteFromClipboard() async {
    final data = await Clipboard.getData(Clipboard.kTextPlain);
    if (data?.text != null && data!.text!.trim().isNotEmpty) {
      setState(() {
        _urlCtrl.text = data.text!.trim();
        _error = null;
      });
    }
  }

  List<V2RayConfig> _parseConfigText(String rawText) {
    String text = rawText.trim();
    if (text.isEmpty) return [];

    if (!text.contains('\n') && !text.startsWith('vless://') && !text.startsWith('vmess://') && !text.startsWith('trojan://')) {
      try {
        final normalized = base64.normalize(text.replaceAll(RegExp(r'\s+'), ''));
        final decoded = utf8.decode(base64.decode(normalized));
        if (decoded.contains('://') || decoded.contains('\n')) {
          text = decoded;
        }
      } catch (_) {}
    }

    final lines = text.split(RegExp(r'[\r\n]+')).map((l) => l.trim()).where((l) => l.isNotEmpty).toList();
    final result = <V2RayConfig>[];

    for (int i = 0; i < lines.length; i++) {
      final line = lines[i];

      if (line.startsWith('vmess://')) {
        final b64 = line.substring(8).trim();
        try {
          final normalized = base64.normalize(b64);
          final jsonStr = utf8.decode(base64.decode(normalized));
          final map = jsonDecode(jsonStr) as Map<String, dynamic>;
          final name = (map['ps'] ?? 'VMess Node ${result.length + 1}').toString();
          final add = (map['add'] ?? '127.0.0.1').toString();
          final port = int.tryParse(map['port']?.toString() ?? '443') ?? 443;
          final id = (map['id'] ?? '00000000-0000-0000-0000-000000000000').toString();
          final net = (map['net'] ?? 'tcp').toString();
          final tls = (map['tls'] ?? 'none').toString();
          final path = map['path']?.toString();
          final host = (map['host'] ?? map['sni'])?.toString();

          result.add(V2RayConfig(
            id: 'imp_${DateTime.now().millisecondsSinceEpoch}_$i',
            name: name,
            protocol: 'vmess',
            address: add,
            port: port,
            userId: id,
            security: tls == 'tls' ? 'tls' : 'none',
            network: net,
            path: path,
            host: host,
            sni: host,
            latency: -1,
          ));
          continue;
        } catch (_) {}
      }

      if (line.startsWith('ss://')) {
        try {
          var clean = line.substring(5);
          var tag = 'Shadowsocks Node ${result.length + 1}';
          final hashIdx = clean.indexOf('#');
          if (hashIdx >= 0) {
            tag = Uri.decodeComponent(clean.substring(hashIdx + 1).trim());
            clean = clean.substring(0, hashIdx);
          }

          String method = '2022-blake3-aes-128-gcm';
          String password = '';
          String server = '127.0.0.1';
          int port = 8388;

          if (clean.contains('@')) {
            final atIdx = clean.lastIndexOf('@');
            final userB64 = clean.substring(0, atIdx);
            final hostPart = clean.substring(atIdx + 1);

            try {
              final norm = base64.normalize(userB64);
              final decodedUser = utf8.decode(base64.decode(norm));
              if (decodedUser.contains(':')) {
                final split = decodedUser.split(':');
                method = split[0];
                password = split.sublist(1).join(':');
              } else {
                password = decodedUser;
              }
            } catch (_) {
              password = userB64;
            }

            if (hostPart.contains(':')) {
              final split = hostPart.split(':');
              server = split[0];
              port = int.tryParse(split[1].split('?')[0]) ?? 8388;
            } else {
              server = hostPart;
            }
          } else {
            try {
              final norm = base64.normalize(clean);
              final decoded = utf8.decode(base64.decode(norm));
              if (decoded.contains('@')) {
                final atIdx = decoded.lastIndexOf('@');
                final userPart = decoded.substring(0, atIdx);
                final hostPart = decoded.substring(atIdx + 1);
                if (userPart.contains(':')) {
                  final split = userPart.split(':');
                  method = split[0];
                  password = split.sublist(1).join(':');
                }
                if (hostPart.contains(':')) {
                  final split = hostPart.split(':');
                  server = split[0];
                  port = int.tryParse(split[1].split('?')[0]) ?? 8388;
                }
              }
            } catch (_) {}
          }

          result.add(V2RayConfig(
            id: 'imp_${DateTime.now().millisecondsSinceEpoch}_$i',
            name: tag,
            protocol: 'shadowsocks',
            address: server,
            port: port,
            userId: password,
            security: method,
            network: 'tcp',
            latency: -1,
          ));
          continue;
        } catch (_) {}
      }

      if (line.startsWith('vless://') ||
          line.startsWith('trojan://') ||
          line.startsWith('hysteria2://') ||
          line.startsWith('hy2://') ||
          line.startsWith('tuic://')) {
        try {
          final uri = Uri.parse(line);
          final rawScheme = uri.scheme;
          final protocol = (rawScheme == 'hy2' || rawScheme == 'hysteria2') ? 'hysteria2' : rawScheme;
          final userInfo = uri.userInfo;
          final host = uri.host;
          final port = uri.port > 0 ? uri.port : 443;
          final name = uri.fragment.isNotEmpty
              ? Uri.decodeComponent(uri.fragment)
              : 'Imported $protocol Node ${result.length + 1}';

          final pbk = uri.queryParameters['pbk'] ?? uri.queryParameters['publicKey'];
          final sid = uri.queryParameters['sid'] ?? uri.queryParameters['shortId'];
          final flow = uri.queryParameters['flow'];
          final rawSec = uri.queryParameters['security']?.toLowerCase();
          final security = rawSec ?? (protocol == 'trojan' ? 'tls' : (pbk != null && pbk.trim().isNotEmpty ? 'reality' : 'none'));
          final rawType = (uri.queryParameters['type'] ?? uri.queryParameters['headerType'] ?? 'tcp').toLowerCase();
          final network = (rawType == 'splithttp') ? 'xhttp' : rawType;
          final sni = uri.queryParameters['sni'] ?? uri.queryParameters['host'];
          final path = uri.queryParameters['path'] ?? uri.queryParameters['serviceName'];

          result.add(V2RayConfig(
            id: 'imp_${DateTime.now().millisecondsSinceEpoch}_$i',
            name: name,
            protocol: protocol,
            address: host.isNotEmpty ? host : '127.0.0.1',
            port: port,
            userId: userInfo.isNotEmpty ? userInfo : 'user',
            security: security,
            network: network,
            sni: sni,
            publicKey: pbk,
            shortId: sid,
            flow: flow,
            path: path,
            latency: -1,
          ));
          continue;
        } catch (_) {}
      }

      if (line.contains(':') && !line.startsWith('http')) {
        result.add(V2RayConfig(
          id: 'imp_${DateTime.now().millisecondsSinceEpoch}_$i',
          name: 'Config Node ${result.length + 1}',
          protocol: 'vless',
          address: line.contains(':') ? line.split(':')[0] : line,
          port: 443,
          userId: '00000000-0000-0000-0000-000000000000',
          security: 'reality',
          network: 'tcp',
          latency: -1,
        ));
      }
    }

    return result;
  }

  Future<void> _parseAndImport() async {
    final text = _urlCtrl.text.trim();
    if (text.isEmpty) {
      setState(() => _error = widget.str.v2rayImportEmptyError);
      return;
    }

    if (text.startsWith('http://')) {
      setState(() {
        _error = 'Insecure subscription URL. Plain http:// can be modified in transit, '
            'which would let whoever controls the network choose your servers. '
            'Use the https:// address of this subscription.';
      });
      return;
    }

    if (text.startsWith('https://')) {
      setState(() {
        _isLoading = true;
        _error = null;
      });

      try {
        final client = HttpClient();
        client.connectionTimeout = const Duration(seconds: 10);
        final request = await client.getUrl(Uri.parse(text));
        request.headers.set(HttpHeaders.userAgentHeader, 'v2rayN/6.23 ClashforWindows/0.20.39 Se7enPro/1.1.0');
        request.headers.set(HttpHeaders.acceptHeader, '*/*');

        final response = await request.close();

        final downgraded = response.redirects
            .any((r) => r.location.scheme.toLowerCase() != 'https');
        if (downgraded) {
          client.close();
          if (mounted) {
            setState(() {
              _isLoading = false;
              _error = 'The subscription redirected to an insecure http:// address; '
                  'the import was cancelled.';
            });
          }
          return;
        }

        if (response.statusCode != 200) {
          client.close();
          if (mounted) {
            setState(() {
              _isLoading = false;
              _error = 'Subscription server responded with HTTP ${response.statusCode}';
            });
          }
          return;
        }

        final body = await response.transform(utf8.decoder).join();
        client.close();

        final configs = _parseConfigText(body);
        if (!mounted) return;

        if (configs.isEmpty) {
          setState(() {
            _isLoading = false;
            _error = widget.str.v2rayImportNoValid;
          });
          return;
        }

        widget.onImport(configs);
        Navigator.pop(context);
        return;
      } catch (e) {
        if (mounted) {
          setState(() {
            _isLoading = false;
            _error = 'Failed to fetch subscription: $e';
          });
        }
        return;
      }
    }

    final result = _parseConfigText(text);

    if (result.isEmpty) {
      setState(() => _error = widget.str.v2rayImportNoValid);
      return;
    }

    widget.onImport(result);
    Navigator.pop(context);
  }

  @override
  Widget build(BuildContext context) {
    final c = Theme.of(context).extension<AppColors>()!;

    return Dialog(
      backgroundColor: Colors.transparent,
      insetPadding: const EdgeInsets.symmetric(horizontal: 20, vertical: 24),
      child: ConstrainedBox(
        constraints: const BoxConstraints(maxWidth: 560),
        child: Container(
          decoration: BoxDecoration(
            color: c.cardElevated,
            borderRadius: BorderRadius.circular(16),
            border: Border.all(color: c.glassBorder, width: 1.2),
            boxShadow: [
              BoxShadow(
                color: Colors.black.withValues(alpha: 0.35),
                blurRadius: 30,
                offset: const Offset(0, 10),
              ),
            ],
          ),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [

              Container(
                padding: const EdgeInsets.fromLTRB(20, 16, 16, 16),
                decoration: BoxDecoration(
                  border: Border(bottom: BorderSide(color: c.border.withValues(alpha: 0.5))),
                ),
                child: Row(
                  children: [
                    Container(
                      padding: const EdgeInsets.all(8),
                      decoration: BoxDecoration(
                        color: BrandColors.accentCyan.withValues(alpha: 0.15),
                        borderRadius: BorderRadius.circular(8),
                      ),
                      child: const Icon(
                        Icons.link_rounded,
                        size: 18,
                        color: BrandColors.accentCyan,
                      ),
                    ),
                    const SizedBox(width: 12),
                    Expanded(
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          Text(
                            widget.str.v2rayImportDialogTitle,
                            style: TextStyle(
                              fontSize: 15,
                              fontWeight: FontWeight.w700,
                              color: c.textPrimary,
                            ),
                          ),
                          const SizedBox(height: 2),
                          Text(
                            widget.str.v2rayImportDialogSubtitle,
                            style: TextStyle(fontSize: 11, color: c.textMuted),
                          ),
                        ],
                      ),
                    ),
                    IconButton(
                      icon: Icon(Icons.close_rounded, size: 18, color: BrandColors.danger.withValues(alpha: 0.85)),
                      tooltip: 'Close',
                      onPressed: () => Navigator.pop(context),
                      splashRadius: 18,
                    ),
                  ],
                ),
              ),

              Padding(
                padding: const EdgeInsets.all(20),
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      widget.str.v2rayImportInputHint,
                      style: TextStyle(
                        fontSize: 11.5,
                        fontWeight: FontWeight.w600,
                        color: c.textSecondary,
                      ),
                    ),
                    const SizedBox(height: 8),

                    Container(
                      decoration: BoxDecoration(
                        color: c.input.withValues(alpha: 0.65),
                        borderRadius: BorderRadius.circular(10),
                        border: Border.all(color: c.border.withValues(alpha: 0.5)),
                      ),
                      child: TextField(
                        controller: _urlCtrl,
                        maxLines: 6,
                        style: TextStyle(
                          fontSize: 11.5,
                          fontFamily: 'monospace',
                          color: c.textPrimary,
                        ),
                        decoration: InputDecoration(
                          hintText: 'https://example.com/api/v1/client/subscribe?token=...\n-- OR --\nvless://uuid@server:443?security=reality&pbk=...#Server\nvmess://eyJ2IjoiMiIs...==\ntrojan://password@server:443#TrojanNode',
                          hintStyle: TextStyle(fontSize: 11, color: c.textMuted),
                          border: InputBorder.none,
                          contentPadding: const EdgeInsets.all(12),
                        ),
                      ),
                    ),

                    if (_error != null) ...[
                      const SizedBox(height: 8),
                      Row(
                        children: [
                          const Icon(Icons.error_outline_rounded, size: 14, color: Colors.redAccent),
                          const SizedBox(width: 6),
                          Expanded(
                            child: Text(
                              _error!,
                              style: const TextStyle(fontSize: 11, color: Colors.redAccent),
                            ),
                          ),
                        ],
                      ),
                    ],
                  ],
                ),
              ),

              Container(
                padding: const EdgeInsets.symmetric(horizontal: 20, vertical: 14),
                decoration: BoxDecoration(
                  border: Border(top: BorderSide(color: c.border.withValues(alpha: 0.5))),
                ),
                child: Row(
                  children: [
                    OutlinedButton.icon(
                      onPressed: _isLoading ? null : _pasteFromClipboard,
                      icon: const Icon(Icons.paste_rounded, size: 14),
                      label: Text(widget.str.v2rayPasteClipboard, style: const TextStyle(fontSize: 11.5)),
                      style: OutlinedButton.styleFrom(
                        foregroundColor: c.textPrimary,
                        side: BorderSide(color: c.border.withValues(alpha: 0.6)),
                        padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 9),
                        shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(8)),
                      ),
                    ),
                    const Spacer(),
                    OutlinedButton(
                      onPressed: _isLoading ? null : () => Navigator.pop(context),
                      style: OutlinedButton.styleFrom(
                        foregroundColor: BrandColors.danger,
                        side: BorderSide(color: BrandColors.danger.withValues(alpha: 0.4)),
                        backgroundColor: BrandColors.danger.withValues(alpha: 0.06),
                        padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 9),
                        shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(8)),
                      ),
                      child: Text(widget.str.cancel, style: const TextStyle(fontSize: 12)),
                    ),
                    const SizedBox(width: 10),
                    ElevatedButton.icon(
                      onPressed: _isLoading ? null : _parseAndImport,
                      icon: _isLoading
                          ? const SizedBox(
                              width: 14,
                              height: 14,
                              child: CircularProgressIndicator(strokeWidth: 2, color: Colors.white),
                            )
                          : const Icon(Icons.download_done_rounded, size: 16),
                      label: Text(_isLoading ? '...' : widget.str.v2rayImportAction, style: const TextStyle(fontSize: 12)),
                      style: ElevatedButton.styleFrom(
                        backgroundColor: BrandColors.accentCyan,
                        foregroundColor: Colors.white,
                        padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 9),
                        shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(8)),
                      ),
                    ),
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

