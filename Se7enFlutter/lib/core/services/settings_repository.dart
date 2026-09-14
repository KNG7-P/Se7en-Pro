import 'dart:io';

import 'package:path/path.dart' as p;
import 'package:path_provider/path_provider.dart';

import '../models/user_settings.dart';

class SettingsRepository {
  SettingsRepository([File? customFile]) : _file = customFile;
  UserSettings _cache = UserSettings();
  File? _file;

  UserSettings get value => _cache;

  Future<File> _resolveFile() async {
    if (_file != null) return _file!;
    Directory dir;
    final localAppData = Platform.environment['LOCALAPPDATA'];
    if (Platform.isWindows && localAppData != null && localAppData.isNotEmpty) {
      dir = Directory(p.join(localAppData, 'Se7en'));
    } else {
      dir = Directory(p.join((await getApplicationSupportDirectory()).path, 'Se7en'));
    }

    return _file = File(p.join(dir.path, 'settings.json'));
  }

  Future<UserSettings> load() async {
    try {
      final f = await _resolveFile();
      final loaded = await _tryRead(f);

      _cache = loaded ?? await _tryRead(File('${f.path}.bak')) ?? UserSettings();
    } catch (_) {
      _cache = UserSettings();
    }
    return _cache;
  }

  Future<UserSettings?> _tryRead(File f) async {
    for (var attempt = 0; attempt < 3; attempt++) {
      try {
        if (!await f.exists()) return null;
        final raw = await f.readAsString();
        if (raw.trim().isEmpty) return null;
        return UserSettings.decode(raw);
      } catch (_) {
        if (attempt < 2) {
          await Future<void>.delayed(const Duration(milliseconds: 60));
        }
      }
    }
    return null;
  }

  void cache(UserSettings settings) => _cache = settings;

  @Deprecated('Daemon is sole writer; use CoreClient.applySettings instead')
  Future<void> save(UserSettings settings) async {
    _cache = settings;

  }
}
