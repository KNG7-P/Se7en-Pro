import 'dart:async';
import 'dart:math';

import '../models/connection_method.dart';
import '../models/connection_state.dart';
import '../models/tunnel_status.dart';
import 'core_client.dart';

class MockCoreClient implements CoreClient {
  final _statusCtrl = StreamController<TunnelStatus>.broadcast();
  final _logCtrl = StreamController<String>.broadcast();
  final _log = <String>[];
  final _rng = Random();

  TunnelStatus _status = const TunnelStatus();
  Timer? _progressTimer;
  Timer? _bytesTimer;
  ConnectionMethod _method = ConnectionMethod.psiphon;
  String _egressRegion = '';
  int _connectToken = 0;

  static const int _maxLog = 5000;

  @override
  Stream<TunnelStatus> get statusStream => _statusCtrl.stream;

  @override
  Stream<Map<String, dynamic>> get settingsUpdatesStream => const Stream.empty();

  @override
  Stream<String> get logStream => _logCtrl.stream;

  @override
  TunnelStatus get current => _status;

  @override
  List<String> get recentLog => List.unmodifiable(_log);

  @override
  void clearRecentLog() {
    _log.clear();
  }

  @override
  Future<void> initialize() async {
    _emitLog('Se7en core (simulation) ready.');
  }

  void _emit(TunnelStatus s) {
    _status = s;
    if (!_statusCtrl.isClosed) _statusCtrl.add(s);
  }

  void _emitLog(String line) {
    final stamped =
        '${DateTime.now().toIso8601String().substring(11, 19)} $line';
    _log.add(stamped);
    if (_log.length > _maxLog) _log.removeRange(0, _log.length - _maxLog);
    if (!_logCtrl.isClosed) _logCtrl.add(stamped);
  }

  @override
  Future<void> connect() async {
    _connectToken++;
    final token = _connectToken;
    _progressTimer?.cancel();
    _bytesTimer?.cancel();

    _emit(_status.copyWith(
      state: ConnectionState.connecting,
      connectProgressPercent: 0,
      connectProgressText: 'Starting ${_method.displayName}…',
      currentRouteIp: '',
      currentRouteSni: '',
    ));
    _emitLog('Connecting via ${_method.displayName}…');

    final stages = _stagesFor(_method);
    var i = 0;
    _progressTimer = Timer.periodic(const Duration(milliseconds: 420), (t) {
      if (token != _connectToken) {
        t.cancel();
        return;
      }
      if (i >= stages.length) {
        t.cancel();
        _finishConnect(token);
        return;
      }
      final (pct, text, log) = stages[i];
      _emit(_status.copyWith(
        connectProgressPercent: pct,
        connectProgressText: text,
      ));
      if (log != null) _emitLog(log);
      i++;
    });
  }

  void _finishConnect(int token) {
    if (token != _connectToken) return;
    final region = _egressRegion.isEmpty
        ? _pickRandomRegion()
        : _egressRegion.toUpperCase();
    final ip = _fakeIp();
    _emit(_status.copyWith(
      state: ConnectionState.connected,
      connectProgressPercent: 100,
      connectProgressText: 'Connected (${_method.displayName})',
      connectedServerRegion: region,
      clientRegion: 'IR',
      socksProxyPort: 1080,
      httpProxyPort: 8080,
      currentRouteIp: ip,
      currentRouteSni: _method.isAether ? 'consumer-speed.cloudflareclient.com' : 'www.google.com',
      availableEgressRegions: _demoRegions,
      activeMethodToken: _method.token,
    ));
    _emitLog('Tunnel established. Exit region: $region, edge IP: $ip');

    _bytesTimer = Timer.periodic(const Duration(milliseconds: 800), (t) {
      if (token != _connectToken || _status.state != ConnectionState.connected) {
        t.cancel();
        return;
      }
      _emit(_status.copyWith(
        bytesReceived: _status.bytesReceived + _rng.nextInt(240000) + 8000,
        bytesSent: _status.bytesSent + _rng.nextInt(90000) + 3000,
      ));
    });
  }

  @override
  Future<void> disconnect() async {
    _connectToken++;
    _progressTimer?.cancel();
    _bytesTimer?.cancel();
    _emit(_status.copyWith(state: ConnectionState.disconnecting));
    _emitLog('Disconnecting…');
    await Future.delayed(const Duration(milliseconds: 500));
    _emit(const TunnelStatus(state: ConnectionState.disconnected));
    _emitLog('Disconnected.');
  }

  @override
  Future<void> setMethod(ConnectionMethod method) async {
    final wasConnected = _status.state == ConnectionState.connected ||
        _status.state == ConnectionState.connecting;
    _method = method;
    _emitLog('Engine switched to ${method.displayName}.');
    _emit(_status.copyWith(activeMethodToken: method.token));
    if (wasConnected) {
      await disconnect();
      await connect();
    }
  }

  @override
  Future<void> setEgressRegion(String code, {bool? isTor}) async {
    _egressRegion = code;
    _emitLog(code.isEmpty
        ? 'Exit region set to Best / Auto.'
        : 'Exit region set to ${code.toUpperCase()}.');
    if (_status.state == ConnectionState.connected) {
      await disconnect();
      await connect();
    }
  }

  @override
  Future<void> applySettings(Map<String, dynamic> settingsJson) async {
    final m = settingsJson['connectionMethod'] as String?;
    if (m != null) _method = ConnectionMethodX.parse(m);
    _egressRegion = settingsJson['egressRegion'] as String? ?? _egressRegion;
  }

  @override
  Future<void> dispose() async {
    _progressTimer?.cancel();
    _bytesTimer?.cancel();
    await _statusCtrl.close();
    await _logCtrl.close();
  }

  List<(int, String, String?)> _stagesFor(ConnectionMethod m) {
    if (m.isChained) {
      return [
        (8, 'Connecting to Cloudflare WARP (outer leg)…', 'Starting outer WARP transport…'),
        (24, 'Scanning clean edge endpoints…', 'Probing candidate edges…'),
        (46, 'Outer WARP tunnel established.', 'Outer leg connected on 127.0.0.1:1820'),
        (62, 'Tunnelling inner leg through WARP…', 'Dialing inner engine over WARP…'),
        (84, 'Establishing inner tunnel…', null),
        (96, 'Finalising route…', null),
      ];
    }
    if (m.isAether) {
      return [
        (10, 'Launching Aether core…', 'aether: starting'),
        (28, 'Scanning for clean edge IPs…', 'aether: scan mode=balanced'),
        (52, 'Handshaking with edge…', 'aether: edge selected'),
        (74, 'Negotiating ${m == ConnectionMethod.masque ? 'MASQUE/QUIC' : 'WireGuard'}…', null),
        (92, 'Verifying reachability…', 'aether: SOCKS5 CONNECT probe ok'),
      ];
    }
    if (m == ConnectionMethod.tor) {
      return [
        (10, 'Starting Tor…', 'tor: bootstrapping'),
        (30, 'Building circuits…', 'tor: Bootstrapped 30%'),
        (55, 'Connecting to relays…', 'tor: Bootstrapped 55%'),
        (80, 'Establishing exit circuit…', 'tor: Bootstrapped 80%'),
        (95, 'Done bootstrapping…', 'tor: Bootstrapped 95%'),
      ];
    }

    return [
      (12, 'Starting Psiphon core…', 'psiphon: candidate servers loaded'),
      (34, 'Fronting through CDN…', 'psiphon: connecting'),
      (58, 'Establishing tunnel…', 'psiphon: SOCKS proxy starting'),
      (82, 'Handshaking…', 'psiphon: ListeningSocksProxyPort 1080'),
      (95, 'Almost there…', 'psiphon: Homepage received'),
    ];
  }

  static const _demoRegions = [
    'US', 'GB', 'DE', 'FR', 'CA', 'NL', 'SE', 'JP', 'SG', 'AU'
  ];

  String _pickRandomRegion() => _demoRegions[_rng.nextInt(_demoRegions.length)];

  String _fakeIp() =>
      '${_rng.nextInt(223) + 1}.${_rng.nextInt(256)}.${_rng.nextInt(256)}.${_rng.nextInt(254) + 1}';

  @override
  Future<void> restartAsAdmin() async {}

  @override
  Future<void> shutdown() async => dispose();

  @override
  Future<int> testV2RayLatency(Map<String, dynamic> configJson) async {
    await Future.delayed(const Duration(milliseconds: 150));
    return 65 + _rng.nextInt(120);
  }

  @override
  Future<Map<String, dynamic>> refreshShardPool({bool force = false}) async {
    await Future.delayed(const Duration(milliseconds: 300));
    return {
      'success': true,
      'nodeCount': 45,
      'pathCount': 270,
      'lastCheck': DateTime.now().toUtc().toIso8601String(),
    };
  }

  @override
  Future<Map<String, dynamic>> getShardInfo() async {
    return {
      'success': true,
      'nodeCount': 45,
      'pathCount': 270,
      'lastCheck': DateTime.now().toUtc().toIso8601String(),
    };
  }

  @override
  Future<Map<String, dynamic>> rotateShardNode() async {
    return {'success': true};
  }
}
