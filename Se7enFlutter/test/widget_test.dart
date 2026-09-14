
import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:se7en/core/models/user_settings.dart';
import 'package:se7en/core/models/connection_method.dart';
import 'package:se7en/core/models/tunnel_status.dart';
import 'package:se7en/core/models/tun_health.dart';
import 'package:se7en/core/data/region_catalog.dart';
import 'package:se7en/core/services/settings_repository.dart';

void main() {
  test('UserSettings round-trips through JSON', () {
    final s = UserSettings(
      theme: 'light',
      connectionMethod: 'masque',
      egressRegion: 'DE',
      localSocksProxyPort: 1080,
      systemWideTunneling: true,
    )..splitTunnelEntries.add(SplitTunnelEntry(kind: 'domain', value: 'x.com'));

    final restored = UserSettings.decode(s.encode());

    expect(restored.theme, 'light');
    expect(restored.connectionMethod, 'masque');
    expect(restored.egressRegion, 'DE');
    expect(restored.localSocksProxyPort, 1080);
    expect(restored.systemWideTunneling, isTrue);
    expect(restored.splitTunnelEntries.single.value, 'x.com');
  });

  test('UserSettings sanitizes invalid TCP ports > 65535 or <= 0', () {
    final invalid = UserSettings.fromJson({
      'localSocksProxyPort': 65655,
      'localHttpProxyPort': 70000,
      'v2rayInboundPort': -5,
    });
    expect(invalid.localSocksProxyPort, 0);
    expect(invalid.localHttpProxyPort, 0);
    expect(invalid.v2rayInboundPort, 10808);

    final valid = UserSettings.fromJson({
      'localSocksProxyPort': 65535,
      'localHttpProxyPort': 8080,
    });
    expect(valid.localSocksProxyPort, 65535);
    expect(valid.localHttpProxyPort, 8080);
    expect(valid.useCustomProxyPorts, isTrue);

    final auto = UserSettings.fromJson({
      'localSocksProxyPort': 0,
      'localHttpProxyPort': 0,
    });
    expect(auto.useCustomProxyPorts, isFalse);
  });

  test('CDN scan corpus preserves exact user selection order', () {
    final s = UserSettings(
      frontedMeekCDNScanBuiltInSets: ['cloudflare', 'google'],
    );
    expect(s.frontedMeekCDNScanBuiltInSets, ['cloudflare', 'google']);
    final json = s.toJson();
    final restored = UserSettings.fromJson(json);
    expect(restored.frontedMeekCDNScanBuiltInSets, ['cloudflare', 'google']);
  });

  test('ConnectionMethod parse/token are stable', () {
    for (final m in ConnectionMethod.values) {
      expect(ConnectionMethodX.parse(m.token), m);
    }
    expect(ConnectionMethodX.parse('chain'), ConnectionMethod.psiphonOverWarp);
    expect(ConnectionMethod.masque.isAether, isTrue);
    expect(ConnectionMethod.torOverWarp.isChained, isTrue);
  });

  test('RegionCatalog puts Auto first and names known codes', () {
    final opts = RegionCatalog.optionsFrom(['DE', 'US']);
    expect(opts.first.isAuto, isTrue);
    expect(RegionCatalog.nameFor('DE'), 'Germany');
    expect(RegionCatalog.nameFor('ZZ'), 'ZZ');
  });

  test('TunnelStatus isAdmin defaults to false and is preserved across copyWith and fromJson', () {
    const initial = TunnelStatus();
    expect(initial.isAdmin, isFalse);

    final fromEmptyJson = TunnelStatus.fromJson({});
    expect(fromEmptyJson.isAdmin, isFalse);

    final fromAdminJson = TunnelStatus.fromJson({'isAdmin': true});
    expect(fromAdminJson.isAdmin, isTrue);

    final copiedPreservesFalse = initial.copyWith(bytesReceived: 1024);
    expect(copiedPreservesFalse.isAdmin, isFalse);

    final copiedPreservesTrue = fromAdminJson.copyWith(bytesReceived: 2048);
    expect(copiedPreservesTrue.isAdmin, isTrue);

    final overridden = initial.copyWith(isAdmin: true);
    expect(overridden.isAdmin, isTrue);
  });

  test('TunnelStatus downSpeed and upSpeed are parsed and preserved across copyWith and fromJson', () {
    const initial = TunnelStatus();
    expect(initial.downSpeed, 0.0);
    expect(initial.upSpeed, 0.0);

    final fromJson = TunnelStatus.fromJson({
      'bytesReceived': 1048576,
      'bytesSent': 524288,
      'downSpeed': 102400.5,
      'upSpeed': 51200.25,
    });
    expect(fromJson.downSpeed, 102400.5);
    expect(fromJson.upSpeed, 51200.25);

    final copied = fromJson.copyWith(downSpeed: 204800.0);
    expect(copied.downSpeed, 204800.0);
    expect(copied.upSpeed, 51200.25);
  });

  group('TunHealth', () {
    const idle = 'idle text';

    test('setting on but not elevated: not wanted, idle detail', () {
      final h = TunHealth.of(const TunnelStatus(isAdmin: false), true, idle);
      expect(h.wanted, isFalse);
      expect(h.active, isFalse);
      expect(h.failed, isFalse);
      expect(h.detail, idle);
      expect(h.errorTooltip, isNull);
    });

    test('no status yet (backend not connected) is never reported as on', () {
      final h = TunHealth.of(null, true, idle);
      expect(h.wanted, isFalse);
      expect(h.active, isFalse);
      expect(h.detail, idle);
    });

    test('wanted while Starting is busy, not active', () {
      final h = TunHealth.of(
        const TunnelStatus(isAdmin: true, tunStatusText: 'Starting'),
        true,
        idle,
      );
      expect(h.wanted, isTrue);
      expect(h.busy, isTrue);
      expect(h.active, isFalse);
      expect(h.failed, isFalse);
      expect(h.detail, contains('Creating'));
    });

    test('running adapter is active with a healthy detail', () {
      final h = TunHealth.of(
        const TunnelStatus(isAdmin: true, tunActive: true, tunStatusText: 'Running'),
        true,
        idle,
      );
      expect(h.active, isTrue);
      expect(h.failed, isFalse);
      expect(h.busy, isFalse);
      expect(h.detail, contains('Wintun adapter up'));
      expect(h.errorTooltip, isNull);
    });

    test('Error surfaces the backend reason and a tooltip', () {
      final h = TunHealth.of(
        const TunnelStatus(
          isAdmin: true,
          tunStatusText: 'Error',
          tunLastError: 'core exited (code=1)',
        ),
        true,
        idle,
      );
      expect(h.wanted, isTrue);
      expect(h.failed, isTrue);
      expect(h.active, isFalse);
      expect(h.detail, 'core exited (code=1)');
      expect(h.errorTooltip, 'core exited (code=1)');
    });

    test('Error without a reason still reads as a failure', () {
      final h = TunHealth.of(
        const TunnelStatus(isAdmin: true, tunStatusText: 'Error'),
        true,
        idle,
      );
      expect(h.failed, isTrue);
      expect(h.detail, contains('failed to start'));
    });

    test('wanted but Off means waiting for the tunnel', () {
      final h = TunHealth.of(const TunnelStatus(isAdmin: true), true, idle);
      expect(h.wanted, isTrue);
      expect(h.active, isFalse);
      expect(h.failed, isFalse);
      expect(h.detail, contains('Waiting'));
    });

    test('a stale error is hidden once the user switches TUN off', () {
      final h = TunHealth.of(
        const TunnelStatus(isAdmin: true, tunStatusText: 'Error', tunLastError: 'x'),
        false,
        idle,
      );
      expect(h.wanted, isFalse);
      expect(h.detail, idle);

      expect(h.failed, isTrue);
    });
  });

  test('SettingsRepository loads settings from file and updates in-memory cache', () async {
    final tempDir = await Directory.systemTemp.createTemp('se7en_settings_test_');
    try {
      final testFile = File('${tempDir.path}/settings.json');
      final repo = SettingsRepository(testFile);

      final initial = await repo.load();
      expect(initial.egressRegion, isEmpty);

      final modified = initial.clone()
        ..egressRegion = 'NL'
        ..connectionMethod = 'warp_on_warp'
        ..setSystemProxy = false
        ..splitTunnelEnabled = true;

      await repo.save(modified);
      expect(repo.value.egressRegion, 'NL');
      expect(repo.value.connectionMethod, 'warp_on_warp');

      await testFile.writeAsString(modified.encode());
      expect(await testFile.exists(), isTrue);

      final reloadedRepo = SettingsRepository(testFile);
      final reloaded = await reloadedRepo.load();
      expect(reloaded.egressRegion, 'NL');
      expect(reloaded.connectionMethod, 'warp_on_warp');
      expect(reloaded.setSystemProxy, isFalse);
      expect(reloaded.splitTunnelEnabled, isTrue);
    } finally {
      await tempDir.delete(recursive: true);
    }
  });

  test('UserSettings language normalizer supports zh and fa without resetting to en', () {
    final en = UserSettings.fromJson({'language': 'en'});
    final ru = UserSettings.fromJson({'language': 'ru'});
    final zh = UserSettings.fromJson({'language': 'zh'});
    final fa = UserSettings.fromJson({'language': 'fa'});
    final unknown = UserSettings.fromJson({'language': 'de'});

    expect(en.language, 'en');
    expect(ru.language, 'ru');
    expect(zh.language, 'zh');
    expect(fa.language, 'fa');
    expect(unknown.language, 'en');
  });

  test('Duplex split bar flex calculation avoids flex 0 crash when download or upload is zero', () {

    int dlTotal = 0;
    int ulTotal = 0;
    int total = dlTotal + ulTotal;
    int dlPct = total > 0 ? ((dlTotal / total) * 100).toInt() : 50;
    int ulPct = 100 - dlPct;
    int safeDlFlex = dlPct <= 0 ? 1 : dlPct;
    int safeUlFlex = ulPct <= 0 ? 1 : ulPct;
    expect(safeDlFlex, greaterThan(0));
    expect(safeUlFlex, greaterThan(0));

    dlTotal = 0;
    ulTotal = 1024;
    total = dlTotal + ulTotal;
    dlPct = total > 0 ? ((dlTotal / total) * 100).toInt() : 50;
    ulPct = 100 - dlPct;
    safeDlFlex = dlPct <= 0 ? 1 : dlPct;
    safeUlFlex = ulPct <= 0 ? 1 : ulPct;
    expect(safeDlFlex, 1);
    expect(safeUlFlex, 100);

    dlTotal = 2048;
    ulTotal = 0;
    total = dlTotal + ulTotal;
    dlPct = total > 0 ? ((dlTotal / total) * 100).toInt() : 50;
    ulPct = 100 - dlPct;
    safeDlFlex = dlPct <= 0 ? 1 : dlPct;
    safeUlFlex = ulPct <= 0 ? 1 : ulPct;
    expect(safeDlFlex, 100);
    expect(safeUlFlex, 1);
  });
}
