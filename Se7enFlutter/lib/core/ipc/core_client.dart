import '../models/connection_method.dart';
import '../models/tunnel_status.dart';

abstract class CoreClient {

  Stream<TunnelStatus> get statusStream;

  Stream<Map<String, dynamic>> get settingsUpdatesStream;

  Stream<String> get logStream;

  TunnelStatus get current;

  List<String> get recentLog;

  Future<void> initialize();

  Future<void> connect();

  Future<void> disconnect();

  Future<void> setMethod(ConnectionMethod method);

  Future<void> setEgressRegion(String code, {bool? isTor});

  Future<void> applySettings(Map<String, dynamic> settingsJson);

  void clearRecentLog();

  Future<void> restartAsAdmin();

  Future<void> shutdown();

  Future<int> testV2RayLatency(Map<String, dynamic> configJson);

  Future<Map<String, dynamic>> refreshShardPool({bool force = false});

  Future<Map<String, dynamic>> getShardInfo();

  Future<Map<String, dynamic>> rotateShardNode();

  Future<void> dispose();
}
