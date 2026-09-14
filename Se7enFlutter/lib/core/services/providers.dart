import 'dart:async';
import 'dart:io';
import 'package:flutter/foundation.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../models/connection_method.dart';
import '../models/connection_state.dart';
import '../models/tunnel_status.dart';
import '../models/user_settings.dart';
import '../ipc/core_client.dart';
import '../ipc/pipe_core_client.dart';
import 'settings_repository.dart';

final coreClientProvider = Provider<CoreClient>((ref) {
  final client = PipeCoreClient();
  ref.onDispose(client.dispose);
  return client;
});

final settingsRepositoryProvider =
    Provider<SettingsRepository>((ref) => SettingsRepository());

final settingsProvider =
    StateNotifierProvider<SettingsController, UserSettings>((ref) {
  final client = ref.watch(coreClientProvider);
  final controller = SettingsController(
    ref.watch(settingsRepositoryProvider),
    onApplied: (s) => client.applySettings(s.toJson()),
  );
  final sub = client.settingsUpdatesStream.listen((data) {
    controller.syncFromDaemon(data);
  });
  ref.onDispose(sub.cancel);
  return controller;
});

class SettingsController extends StateNotifier<UserSettings> {
  SettingsController(this._repo, {this.onApplied}) : super(UserSettings());

  final SettingsRepository _repo;
  final void Function(UserSettings s)? onApplied;

  Future<void> load() async {
    state = await _repo.load();
  }

  Future<void> update(void Function(UserSettings s) edit) async {
    final draft = state.clone();
    edit(draft);
    state = draft;
    _repo.cache(draft);
    onApplied?.call(draft);
  }

  Future<void> replace(UserSettings next) async {
    state = next;
    _repo.cache(next);
    onApplied?.call(next);
  }

  void syncFromDaemon(Map<String, dynamic> json) {
    try {
      final next = UserSettings.fromJson(json);
      state = next;
      _repo.cache(next);
    } catch (_) {}
  }
}

final tunnelStatusProvider = StreamProvider<TunnelStatus>((ref) {
  final client = ref.watch(coreClientProvider);
  return client.statusStream;
});

final connectionStateProvider = Provider<ConnectionState>((ref) {
  final async = ref.watch(tunnelStatusProvider);
  return async.maybeWhen(
    data: (s) => s.state,
    orElse: () => ConnectionState.disconnected,
  );
});

final connectionStartTimeProvider = StateProvider<DateTime?>((ref) {
  ref.listen<ConnectionState>(connectionStateProvider, (prev, next) {
    if (next == ConnectionState.connected) {
      if (ref.controller.state == null) {
        ref.controller.state = DateTime.now();
      }
    } else if (next == ConnectionState.disconnected || next == ConnectionState.error) {
      ref.controller.state = null;
    }
  });
  return null;
});

final selectedMethodProvider = Provider<ConnectionMethod>((ref) {
  final token = ref.watch(settingsProvider.select((s) => s.connectionMethod));
  return ConnectionMethodX.parse(token);
});

final connectionControllerProvider = Provider<ConnectionController>((ref) {
  return ConnectionController(ref);
});

class ConnectionController {
  ConnectionController(this._ref);
  final Ref _ref;

  CoreClient get _client => _ref.read(coreClientProvider);

  Future<void> _ensureReady() async {
    await _client.initialize();
  }

  Future<void> initialize() async {
    await _ensureReady();
    await _client.applySettings(_ref.read(settingsProvider).toJson());
    if (_ref.read(settingsProvider).autoConnect) {
      try { await connect(); } catch (e) { debugPrint('autoConnect failed: $e'); }
    }
  }

  bool _isToggling = false;

  Future<void> toggle() async {
    if (_isToggling) return;
    _isToggling = true;
    try {
      final state = _ref.read(connectionStateProvider);
      if (state == ConnectionState.connected ||
          state == ConnectionState.connecting) {
        await disconnect();
      } else {
        await connect();
      }
    } finally {
      _isToggling = false;
    }
  }

  Timer? _netWatch;
  void startNetworkWatch() {
    _netWatch?.cancel();
    _netWatch = Timer.periodic(const Duration(seconds: 5), (_) async {
      final s = _ref.read(tunnelStatusProvider).valueOrNull;
      if (s == null || s.state != ConnectionState.connected) return;
      try {
        final addrs = await InternetAddress.lookup('1.1.1.1');
        if (addrs.isEmpty) throw const SocketException('no route');
      } catch (_) {
        debugPrint('network lost — reconnecting');
        try { await connect(); } catch (_) {}
      }
    });
  }
  void stopNetworkWatch() { _netWatch?.cancel(); _netWatch = null; }

  Future<void> connect() async {
    try {
      await _ensureReady();
      await _client.applySettings(_ref.read(settingsProvider).toJson());
      await _client.connect();
      startNetworkWatch();
    } catch (e) {
      debugPrint('connect failed: $e');
    }
  }

  Future<void> disconnect() { stopNetworkWatch(); return _client.disconnect(); }

  Future<void> setMethod(ConnectionMethod method) async {
    await _ref
        .read(settingsProvider.notifier)
        .update((s) {
          s.connectionMethod = method.token;
          if (method == ConnectionMethod.masque) {
            s.aetherProtocol = 'masque';
          } else if (method == ConnectionMethod.wireguard) {
            s.aetherProtocol = 'wireguard';
          } else if (method == ConnectionMethod.warpOnWarp) {
            s.aetherProtocol = 'warp';
          } else if (method == ConnectionMethod.masqueOnMasque) {
            s.aetherProtocol = 'masque_on_masque';
          }
        });
    await _client.setMethod(method);
  }

  Future<void> setEgressRegion(String code, {ConnectionMethod? method}) async {
    final m = method ?? _ref.read(selectedMethodProvider);
    final isTor = m?.isTor ?? false;
    await _ref.read(settingsProvider.notifier).update((s) {
      if (isTor) {
        s.torExitCountry = code.trim().toLowerCase();
      } else {
        s.egressRegion = code.trim().toUpperCase();
      }
    });
    await _client.setEgressRegion(code, isTor: isTor);
  }
}

enum SettingsTab {
  general,
  network,
  psiphon,
  aether,
  tor,
  shard,
  chained,
}

final currentNavIndexProvider = StateProvider<int>((ref) => 0);

final currentSettingsTabProvider = StateProvider<SettingsTab>((ref) => SettingsTab.general);

