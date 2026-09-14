import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'dart:math' as math;

import 'package:flutter/foundation.dart';
import 'package:path/path.dart' as p;

import '../models/connection_method.dart';
import '../models/connection_state.dart';
import '../models/tunnel_status.dart';
import 'core_client.dart';

class PipeCoreClient implements CoreClient {
  final _statusCtrl = StreamController<TunnelStatus>.broadcast();
  final _settingsUpdatesCtrl = StreamController<Map<String, dynamic>>.broadcast();
  final _logCtrl = StreamController<String>.broadcast();
  final _installedAppsCtrl = StreamController<List<Map<String, dynamic>>>.broadcast();
  final _pingResultCtrl = StreamController<Map<String, dynamic>>.broadcast();
  final _log = <String>[];
  static const int _maxLog = 5000;

  List<Map<String, dynamic>>? _cachedInstalledApps;
  List<Map<String, dynamic>>? get cachedInstalledApps => _cachedInstalledApps;

  Stream<List<Map<String, dynamic>>> get installedAppsStream => _installedAppsCtrl.stream;
  Stream<Map<String, dynamic>> get pingResultStream => _pingResultCtrl.stream;

  final Map<String, Completer<int>> _pendingV2RayPings = {};
  Completer<Map<String, dynamic>>? _pendingShardPoolInfo;

  void fetchInstalledApps() => _send({'cmd': 'get_installed_apps'});
  void testPing(String host, [int port = 443]) => _send({'cmd': 'test_ping', 'host': host, 'port': port});

  @override
  Future<int> testV2RayLatency(Map<String, dynamic> configJson) async {
    final id = configJson['id'] as String? ?? '';
    if (id.isEmpty) return -1;

    final completer = Completer<int>();
    _pendingV2RayPings[id] = completer;

    _send({
      'cmd': 'test_v2ray_config',
      'id': id,
      'config': configJson,
    });

    try {
      return await completer.future.timeout(
        const Duration(seconds: 8),
        onTimeout: () {
          _pendingV2RayPings.remove(id);
          return -3;
        },
      );
    } catch (_) {
      _pendingV2RayPings.remove(id);
      return -3;
    }
  }

  @override
  Future<Map<String, dynamic>> refreshShardPool({bool force = false}) async {
    final completer = Completer<Map<String, dynamic>>();
    _pendingShardPoolInfo = completer;
    _send({'cmd': 'refresh_shard_pool', 'force': force});
    try {
      return await completer.future.timeout(
        const Duration(seconds: 15),
        onTimeout: () {
          _pendingShardPoolInfo = null;
          return {'success': false, 'nodeCount': 0, 'pathCount': 0};
        },
      );
    } catch (_) {
      _pendingShardPoolInfo = null;
      return {'success': false, 'nodeCount': 0, 'pathCount': 0};
    }
  }

  @override
  Future<Map<String, dynamic>> getShardInfo() async {
    final completer = Completer<Map<String, dynamic>>();
    _pendingShardPoolInfo = completer;
    _send({'cmd': 'get_shard_info'});
    try {
      return await completer.future.timeout(
        const Duration(seconds: 5),
        onTimeout: () {
          _pendingShardPoolInfo = null;
          return {'success': false, 'nodeCount': 0, 'pathCount': 0};
        },
      );
    } catch (_) {
      _pendingShardPoolInfo = null;
      return {'success': false, 'nodeCount': 0, 'pathCount': 0};
    }
  }

  @override
  Future<Map<String, dynamic>> rotateShardNode() async {
    _send({'cmd': 'rotate_shard_node'});
    return {'success': true};
  }

  TunnelStatus _status = const TunnelStatus();
  DateTime? _lastByteAt;
  Socket? _socket;
  StreamSubscription? _socketSub;
  Process? _backendProcess;
  bool _disposed = false;
  bool _isConnecting = false;
  Timer? _reconnectTimer;

  bool _shuttingDown = false;
  Completer<void>? _daemonClosed;

  final List<String> _pending = [];

  @override
  Stream<TunnelStatus> get statusStream => _statusCtrl.stream;

  @override
  Stream<Map<String, dynamic>> get settingsUpdatesStream => _settingsUpdatesCtrl.stream;

  @override
  Stream<String> get logStream => _logCtrl.stream;

  @override
  TunnelStatus get current => _status;

  @override
  List<String> get recentLog => List.unmodifiable(_log);

  @override
  void clearRecentLog() {
    _log.clear();
    _send({'cmd': 'clear_logs'});
  }

  @override
  Future<void> initialize() async {
    if (_disposed) return;
    _emitLog('Initializing Se7en IPC Client...');
    await _ensureConnected();
  }

  Future<void> _ensureConnected() async {
    if (_disposed || _isConnecting || _socket != null) return;
    _isConnecting = true;

    try {
      final port = await _resolveIpcPort();
      if (port != null) {
        try {
          await _connectToPort(port);
          _isConnecting = false;
          return;
        } catch (_) {

        }
      }

      await _spawnBackendDaemonIfNeeded();

      for (var i = 0; i < 10; i++) {
        if (_disposed) break;
        await Future.delayed(const Duration(milliseconds: 500));
        final retryPort = await _resolveIpcPort();
        if (retryPort != null) {
          try {
            await _connectToPort(retryPort);
            _emitLog('Connected to Se7enPro backend daemon on port $retryPort.');
            _isConnecting = false;
            return;
          } catch (_) {

          }
        }
      }
    } catch (e) {
      _emitLog('Warning: Could not connect to backend daemon: $e');
    } finally {
      _isConnecting = false;
      if (_socket == null && !_disposed) {
        _scheduleReconnect();
      }
    }
  }

  Future<int?> _resolveIpcPort() async {
    try {
      final localAppData = Platform.environment['LOCALAPPDATA'];
      if (localAppData == null || localAppData.isEmpty) return null;
      final portFile = File(p.join(localAppData, 'Se7en', 'ipc.port'));
      if (await portFile.exists()) {
        final content = (await portFile.readAsString()).trim();
        return int.tryParse(content);
      }
    } catch (_) {}
    return null;
  }

  Future<String?> _resolveIpcToken() async {
    try {
      final localAppData = Platform.environment['LOCALAPPDATA'];
      if (localAppData == null || localAppData.isEmpty) return null;
      final tokenFile = File(p.join(localAppData, 'Se7en', 'ipc.token'));
      if (await tokenFile.exists()) {
        final content = (await tokenFile.readAsString()).trim();
        return content.isEmpty ? null : content;
      }
    } catch (_) {}
    return null;
  }

  Future<void> _spawnBackendDaemonIfNeeded() async {
    try {
      final exe = await _findBackendExecutable();
      if (exe == null) {
        _emitLog('Backend executable not located; waiting for manual launch.');
        return;
      }

      _emitLog('Launching Se7enPro background daemon: ${exe.path}');
      if (exe.path.endsWith('.dll')) {
        _backendProcess = await Process.start(
          'dotnet',
          [exe.path, '--daemon'],
          workingDirectory: exe.parent.path,
          mode: ProcessStartMode.detached,
        );
      } else {
        _backendProcess = await Process.start(
          exe.path,
          ['--daemon'],
          workingDirectory: exe.parent.path,
          mode: ProcessStartMode.detached,
        );
      }
    } catch (e) {
      _emitLog('Failed to auto-spawn backend daemon: $e');
    }
  }

  Future<File?> _findBackendExecutable() async {
    final currentDir = File(Platform.resolvedExecutable).parent.path;

    String devBuild(String config, String file) => p.normalize(p.join(
          currentDir,
          '..', '..', '..', '..', '..',
          'Se7enPro',
          'bin',
          config,
          'net8.0-windows10.0.19041.0',
          file,
        ));

    final candidates = [
      File(p.join(currentDir, 'Se7enCore.exe')),
      File(p.join(currentDir, 'Se7enCore.dll')),
      File(p.join(currentDir, 'Se7enPro.dll')),
      File(p.join(currentDir, 'Se7enPro.exe')),
      File(p.join(currentDir, 'backend', 'Se7enPro.dll')),
      File(devBuild('Release', 'Se7enCore.exe')),
      File(devBuild('Release', 'Se7enCore.dll')),
      File(devBuild('Release', 'Se7enPro.dll')),
      File(devBuild('Release', 'Se7enPro.exe')),
      File(devBuild('Debug', 'Se7enCore.exe')),
      File(devBuild('Debug', 'Se7enCore.dll')),
      File(devBuild('Debug', 'Se7enPro.dll')),
      File(devBuild('Debug', 'Se7enPro.exe')),
    ];

    final ownExe = Platform.resolvedExecutable.toLowerCase();
    for (final c in candidates) {
      if (await c.exists()) {
        if (c.path.toLowerCase() == ownExe) continue;
        return c;
      }
    }

    return null;
  }

  Future<void> _connectToPort(int port) async {

    final token = await _resolveIpcToken();
    if (token == null) {
      throw const SocketException('IPC token not available yet');
    }

    final s = await Socket.connect('127.0.0.1', port, timeout: const Duration(seconds: 2));
    try {
      s.setOption(SocketOption.tcpNoDelay, true);
    } catch (_) {}
    _socket = s;

    _socketSub = s
        .cast<List<int>>()
        .transform(utf8.decoder)
        .transform(const LineSplitter())
        .listen(
      _handleIncomingMessage,
      onError: (e) {
        _emitLog('IPC socket error: $e');
        _handleDisconnect();
      },
      onDone: () {
        _emitLog('Backend connection closed.');
        _handleDisconnect();
      },
      cancelOnError: true,
    );

    s.write('${jsonEncode({'cmd': 'auth', 'token': token})}\n');

    _flushPending();

    _send({'cmd': 'status'});

    _send({'cmd': 'get_installed_apps'});
  }

  void _handleIncomingMessage(String line) {
    if (line.trim().isEmpty) return;
    try {
      final json = jsonDecode(line) as Map<String, dynamic>;
      final event = json['event'] as String?;

      switch (event) {
        case 'status':
          final data = json['data'] as Map<String, dynamic>?;
          if (data != null) {
            _emitStatus(TunnelStatus.fromJson(data));
          }
          break;

        case 'log':
          final logLine = json['line'] as String?;
          if (logLine != null) {
            _emitLog(logLine);
          }
          break;

        case 'progress':
          final pct = json['percent'] as int? ?? _status.connectProgressPercent;
          final text = json['text'] as String? ?? _status.connectProgressText;
          _emitStatus(_status.copyWith(
            connectProgressPercent: pct,
            connectProgressText: text,
          ));
          break;

        case 'bytes':
          final sent = (json['sent'] as num?)?.toInt() ?? _status.bytesSent;
          final received = (json['received'] as num?)?.toInt() ?? _status.bytesReceived;
          var downSpeed = (json['downSpeed'] as num?)?.toDouble();
          var upSpeed = (json['upSpeed'] as num?)?.toDouble();
          final now = DateTime.now();
          if (downSpeed == null || upSpeed == null) {
            final dt = _lastByteAt == null ? 0.0 : now.difference(_lastByteAt!).inMilliseconds / 1000.0;
            if (dt > 0.05) {
              downSpeed ??= math.max(0.0, (received - _status.bytesReceived) / dt);
              upSpeed ??= math.max(0.0, (sent - _status.bytesSent) / dt);
              _lastByteAt = now;
            } else {
              downSpeed ??= _status.downSpeed;
              upSpeed ??= _status.upSpeed;
            }
          } else {
            _lastByteAt = now;
          }
          if (!downSpeed.isFinite || downSpeed.isNegative) {
            downSpeed = 0.0;
          }
          if (!upSpeed.isFinite || upSpeed.isNegative) {
            upSpeed = 0.0;
          }
          _emitStatus(_status.copyWith(
            bytesSent: sent,
            bytesReceived: received,
            downSpeed: downSpeed,
            upSpeed: upSpeed,
          ));
          break;

        case 'route':
          final ip = json['ip'] as String? ?? '';
          final sni = json['sni'] as String? ?? '';
          final region = json['serverRegion'] as String? ?? '';
          _emitStatus(_status.copyWith(
            currentRouteIp: ip,
            currentRouteSni: sni,
            connectedServerRegion: region,
          ));
          break;

        case 'recent_log':
          final lines = (json['lines'] as List<dynamic>?)?.cast<String>() ?? [];
          for (final l in lines) {
            _emitLog(l);
          }
          break;

        case 'installed_apps':
          final apps = (json['apps'] as List<dynamic>?)
                  ?.map((a) => Map<String, dynamic>.from(a as Map))
                  .toList() ??
              [];
          _cachedInstalledApps = apps;
          if (!_installedAppsCtrl.isClosed) _installedAppsCtrl.add(apps);
          break;

        case 'ping_result':
          if (!_pingResultCtrl.isClosed) {
            _pingResultCtrl.add(Map<String, dynamic>.from(json));
          }
          break;

        case 'v2ray_ping_result':
          final id = json['id'] as String?;
          final lat = (json['latencyMs'] as num?)?.toInt() ?? -3;
          if (id != null && _pendingV2RayPings.containsKey(id)) {
            _pendingV2RayPings.remove(id)?.complete(lat);
          }
          break;

        case 'shard_info':
          final completer = _pendingShardPoolInfo;
          _pendingShardPoolInfo = null;
          if (completer != null && !completer.isCompleted) {
            completer.complete(Map<String, dynamic>.from(json));
          }
          break;

        case 'settings_updated':
          final settingsData = json['settings'] as Map<String, dynamic>?;
          if (settingsData != null && !_settingsUpdatesCtrl.isClosed) {
            _settingsUpdatesCtrl.add(settingsData);
          }
          break;

        case 'auth_failed':

          _emitLog('Backend rejected the IPC handshake (${json['reason'] ?? 'unknown'}); retrying.');
          _handleDisconnect();
          break;
      }
    } catch (e) {
      debugPrint('Error parsing IPC message: $e\nLine: $line');
    }
  }

  void _handleDisconnect() {
    _lastByteAt = null;
    for (final c in _pendingV2RayPings.values) {
      if (!c.isCompleted) c.complete(-3);
    }
    _pendingV2RayPings.clear();

    final shardCompleter = _pendingShardPoolInfo;
    _pendingShardPoolInfo = null;
    if (shardCompleter != null && !shardCompleter.isCompleted) {
      shardCompleter.complete({});
    }

    _socketSub?.cancel();
    _socketSub = null;
    try {
      _socket?.destroy();
    } catch (_) {}
    _socket = null;

    _emitStatus(_status.copyWith(
      state: ConnectionState.disconnected,
      downSpeed: 0.0,
      upSpeed: 0.0,
      currentRouteIp: null,
      currentRouteSni: null,
    ));

    final closed = _daemonClosed;
    if (closed != null && !closed.isCompleted) closed.complete();

    _scheduleReconnect();
  }

  void _scheduleReconnect() {

    if (_disposed || _shuttingDown) return;
    _reconnectTimer?.cancel();
    _reconnectTimer = Timer(const Duration(seconds: 2), () {
      if (!_disposed && _socket == null) {
        _ensureConnected();
      }
    });
  }

  void _send(Map<String, dynamic> payload) {
    final jsonStr = jsonEncode(payload);
    if (_socket != null) {
      try {
        _socket!.write('$jsonStr\n');
      } catch (e) {
        debugPrint('Failed to send IPC message: $e');
      }
    } else {

      _pending.add('$jsonStr\n');
    }
  }

  void _flushPending() {
    if (_socket == null || _pending.isEmpty) return;
    final buffered = List<String>.from(_pending);
    _pending.clear();
    try {
      _socket!.write(buffered.join());
    } catch (e) {
      debugPrint('Failed to flush buffered IPC messages: $e');
    }
  }

  void _emitStatus(TunnelStatus s) {
    _status = s;
    if (!_statusCtrl.isClosed) _statusCtrl.add(s);
  }

  void _emitLog(String line) {
    _log.add(line);
    if (_log.length > _maxLog) _log.removeRange(0, _log.length - _maxLog);
    if (!_logCtrl.isClosed) _logCtrl.add(line);
  }

  @override
  Future<void> connect() async {
    _emitStatus(_status.copyWith(
      state: ConnectionState.connecting,
      connectProgressPercent: 10,
      connectProgressText: 'Initiating connection...',
    ));
    _emitLog('[App] Initiating connection...');
    _send({'cmd': 'connect'});
  }

  @override
  Future<void> disconnect() async {
    _lastByteAt = null;
    _emitStatus(_status.copyWith(
      state: ConnectionState.disconnecting,
      downSpeed: 0.0,
      upSpeed: 0.0,
    ));
    _send({'cmd': 'disconnect'});
  }

  @override
  Future<void> setMethod(ConnectionMethod method) async {
    _emitLog('[App] Selected protocol: ${method.displayName}');
    _send({'cmd': 'set_method', 'method': method.token});
  }

  @override
  Future<void> setEgressRegion(String code, {bool? isTor}) async {
    _send({'cmd': 'set_egress_region', 'region': code, 'isTor': ?isTor});
  }

  @override
  Future<void> applySettings(Map<String, dynamic> settingsJson) async {
    _send({'cmd': 'apply_settings', 'settings': settingsJson});
  }

  @override
  Future<void> restartAsAdmin() async {
    _send({'cmd': 'restart_as_admin'});
  }

  @override
  Future<void> shutdown() async {

    _shuttingDown = true;
    _reconnectTimer?.cancel();
    _reconnectTimer = null;

    final closed = _daemonClosed ??= Completer<void>();
    if (_socket == null) {

      if (!closed.isCompleted) closed.complete();
    } else {
      try {
        _send({'cmd': 'shutdown'});
      } catch (_) {}
    }

    try {
      await closed.future.timeout(_shutdownGrace);
    } on TimeoutException {
      _emitLog('Backend did not close within ${_shutdownGrace.inSeconds}s; terminating it.');
    }

    await dispose();
  }

  static const _shutdownGrace = Duration(seconds: 7);

  @override
  Future<void> dispose() async {
    _disposed = true;
    _reconnectTimer?.cancel();
    _socketSub?.cancel();
    try {
      _socket?.destroy();
    } catch (_) {}
    _socket = null;

    if (_daemonClosed?.isCompleted != true) {
      _backendProcess?.kill();
    }
    _backendProcess = null;

    await _statusCtrl.close();
    await _settingsUpdatesCtrl.close();
    await _logCtrl.close();
    await _installedAppsCtrl.close();
    await _pingResultCtrl.close();
  }
}
