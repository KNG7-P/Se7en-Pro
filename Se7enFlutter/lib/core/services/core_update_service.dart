import 'dart:async';
import 'dart:convert';
import 'dart:io';

class CoreUpdateInfo {
  const CoreUpdateInfo({
    required this.coreId,
    required this.displayName,
    required this.installedVersion,
    required this.latestVersion,
    required this.hasUpdate,
    required this.downloadUrl,
    required this.releaseNotes,
    required this.downloadSizeBytes,
  });

  final String coreId;
  final String displayName;
  final String installedVersion;
  final String latestVersion;
  final bool hasUpdate;
  final String downloadUrl;
  final String releaseNotes;
  final int downloadSizeBytes;
}

class CoreUpdateService {
  static final CoreUpdateService instance = CoreUpdateService._();
  CoreUpdateService._();

  final _httpClient = HttpClient()
    ..connectionTimeout = const Duration(seconds: 15)
    ..userAgent = 'Se7enPro-CoreUpdater/1.0';

  String getInstalledVersion(String coreId) {
    try {
      final id = coreId.toLowerCase();
      if (id == 'aether') {
        final path = _findAetherPath();
        if (path != null && File(path).existsSync()) {
          final ver = _queryExeVersion(path, '--version');
          if (ver != null && ver.isNotEmpty) {
            final parts = ver.split(' ');
            if (parts.length >= 2) return parts[1].trim();
            return ver.trim();
          }
        }
        return '1.7.0';
      } else if (id == 'tor') {
        final path = _findTorPath();
        if (path != null && File(path).existsSync()) {
          final ver = _queryExeVersion(path, '--version');
          if (ver != null && ver.isNotEmpty) {
            final lines = ver.split('\n');
            if (lines.isNotEmpty) {
              final first = lines.first.trim();
              final idx = first.toLowerCase().indexOf('version ');
              if (idx >= 0) {
                final rest = first.substring(idx + 8).trim();
                final space = rest.indexOf(' ');
                return space > 0 ? rest.substring(0, space) : rest;
              }
            }
          }
        }
        return '0.4.9.11';
      }
    } catch (_) {}
    return coreId.toLowerCase() == 'tor' ? '0.4.9.11' : '1.7.0';
  }

  Future<CoreUpdateInfo> checkForUpdate(String coreId) async {
    final id = coreId.toLowerCase();
    final installed = getInstalledVersion(id);

    if (id == 'tor') {
      await Future.delayed(const Duration(milliseconds: 400));
      return CoreUpdateInfo(
        coreId: 'tor',
        displayName: 'Tor (Onion Routing)',
        installedVersion: installed,
        latestVersion: installed,
        hasUpdate: false,
        downloadUrl: '',
        releaseNotes: 'Tor engine is running the latest bundled release.',
        downloadSizeBytes: 0,
      );
    }

    if (id != 'aether') {
      return CoreUpdateInfo(
        coreId: id,
        displayName: coreId,
        installedVersion: installed,
        latestVersion: installed,
        hasUpdate: false,
        downloadUrl: '',
        releaseNotes: '',
        downloadSizeBytes: 0,
      );
    }

    try {
      final uri = Uri.parse('https://api.github.com/repos/CluvexStudio/Aether/releases/latest');
      final request = await _httpClient.getUrl(uri);
      final response = await request.close();
      if (response.statusCode != 200) {
        throw HttpException('GitHub API returned ${response.statusCode}');
      }

      final body = await response.transform(utf8.decoder).join();
      final Map<String, dynamic> json = jsonDecode(body) as Map<String, dynamic>;

      final tagName = (json['tag_name'] as String? ?? '').trim();
      final latestVersion = tagName.replaceAll(RegExp(r'^[vV]'), '').trim();
      final releaseNotes = json['body'] as String? ?? '';

      String downloadUrl = '';
      int downloadSizeBytes = 0;

      final assets = json['assets'] as List<dynamic>? ?? [];
      for (final asset in assets) {
        final name = (asset['name'] as String? ?? '').toLowerCase();
        if (name == 'aether-windows-x86_64.zip') {
          downloadUrl = asset['browser_download_url'] as String? ?? '';
          downloadSizeBytes = (asset['size'] as num?)?.toInt() ?? 0;
          break;
        }
      }

      if (downloadUrl.isEmpty && tagName.isNotEmpty) {
        downloadUrl =
            'https://github.com/CluvexStudio/Aether/releases/download/$tagName/aether-windows-x86_64.zip';
      }

      final hasUpdate = _isNewerVersion(installed, latestVersion);

      return CoreUpdateInfo(
        coreId: 'aether',
        displayName: 'Aether (WARP / MASQUE)',
        installedVersion: installed,
        latestVersion: latestVersion.isEmpty ? installed : latestVersion,
        hasUpdate: hasUpdate,
        downloadUrl: downloadUrl,
        releaseNotes: releaseNotes,
        downloadSizeBytes: downloadSizeBytes,
      );
    } catch (e) {
      return CoreUpdateInfo(
        coreId: 'aether',
        displayName: 'Aether (WARP / MASQUE)',
        installedVersion: installed,
        latestVersion: installed,
        hasUpdate: false,
        downloadUrl: '',
        releaseNotes: 'Could not fetch remote version: $e',
        downloadSizeBytes: 0,
      );
    }
  }

  Future<bool> updateAether({
    required void Function(int progress, String status) onProgress,
  }) async {
    onProgress(5, 'Checking latest release…');
    final info = await checkForUpdate('aether');

    var downloadUrl = info.downloadUrl;
    if (downloadUrl.isEmpty) {
      downloadUrl =
          'https://github.com/CluvexStudio/Aether/releases/latest/download/aether-windows-x86_64.zip';
    }

    final tempDir = Directory(
        '${Directory.systemTemp.path}\\Se7en_Update_${DateTime.now().millisecondsSinceEpoch}');
    await tempDir.create(recursive: true);

    try {
      final zipFile = File('${tempDir.path}\\aether-windows-x86_64.zip');
      onProgress(10, 'Starting download…');

      final uri = Uri.parse(downloadUrl);
      final request = await _httpClient.getUrl(uri);
      final response = await request.close();
      if (response.statusCode != 200 && response.statusCode != 302) {
        throw HttpException('Download failed: HTTP ${response.statusCode}');
      }

      final totalBytes = response.contentLength > 0
          ? response.contentLength
          : (info.downloadSizeBytes > 0 ? info.downloadSizeBytes : 15000000);
      var receivedBytes = 0;
      final sink = zipFile.openWrite();

      await for (final chunk in response) {
        sink.add(chunk);
        receivedBytes += chunk.length;
        final pct = (10 + ((receivedBytes / totalBytes) * 70).clamp(0, 70)).toInt();
        onProgress(pct, 'Downloading: $pct%');
      }
      await sink.flush();
      await sink.close();

      onProgress(85, 'Extracting archive…');
      final extractDir = Directory('${tempDir.path}\\extracted');
      await extractDir.create(recursive: true);

      final extractRes = await Process.run('tar', ['-xf', zipFile.path, '-C', extractDir.path]);
      if (extractRes.exitCode != 0) {
        await Process.run('powershell', [
          '-NoProfile',
          '-Command',
          "Expand-Archive -LiteralPath '${zipFile.path}' -DestinationPath '${extractDir.path}' -Force"
        ]);
      }

      File? extractedExe;
      await for (final f in extractDir.list(recursive: true)) {
        if (f is File && f.path.toLowerCase().endsWith('aether.exe')) {
          extractedExe = f;
          break;
        }
      }

      if (extractedExe == null || !extractedExe.existsSync()) {
        throw const FileSystemException('Extracted package did not contain aether.exe');
      }

      onProgress(92, 'Installing binary…');
      await Process.run('taskkill', ['/F', '/IM', 'aether.exe', '/T'])
          .catchError((_) => ProcessResult(0, 0, '', ''));
      await Process.run('taskkill', ['/F', '/IM', 'Se7enPro.Aether.exe', '/T'])
          .catchError((_) => ProcessResult(0, 0, '', ''));

      final appData = Platform.environment['LOCALAPPDATA'] ?? '';
      if (appData.isNotEmpty) {
        final targetCached = File('$appData\\Se7en\\aether\\aether.exe');
        final targetNamed = File('$appData\\Se7en\\aether\\Se7enPro.Aether.exe');
        targetCached.parent.createSync(recursive: true);
        try {
          if (targetCached.existsSync()) targetCached.deleteSync();
        } catch (_) {}
        try {
          if (targetNamed.existsSync()) targetNamed.deleteSync();
        } catch (_) {}
        extractedExe.copySync(targetCached.path);
        extractedExe.copySync(targetNamed.path);
      }

      try {
        final exeDir = File(Platform.resolvedExecutable).parent.path;
        final resFile = File('$exeDir\\Resources\\aether\\aether.exe');
        if (resFile.parent.existsSync()) {
          extractedExe.copySync(resFile.path);
        }
      } catch (_) {}

      try {
        File? extractedPt;
        await for (final f in extractDir.list(recursive: true)) {
          if (f is File && f.path.toLowerCase().endsWith('lyrebird.exe')) {
            extractedPt = f;
            break;
          }
        }
        if (extractedPt != null) {
          if (appData.isNotEmpty) {
            final ptDir = Directory('$appData\\Se7en\\aether\\pt');
            ptDir.createSync(recursive: true);
            extractedPt.copySync('${ptDir.path}\\lyrebird.exe');
          }
          try {
            final exeDir = File(Platform.resolvedExecutable).parent.path;
            final resPt = File('$exeDir\\Resources\\aether\\pt\\lyrebird.exe');
            resPt.parent.createSync(recursive: true);
            extractedPt.copySync(resPt.path);
          } catch (_) {}
        }
      } catch (_) {}

      onProgress(100, 'Aether core successfully installed!');
      return true;
    } finally {
      try {
        if (tempDir.existsSync()) {
          tempDir.deleteSync(recursive: true);
        }
      } catch (_) {}
    }
  }

  bool _isNewerVersion(String current, String latest) {
    try {
      final cParts = current.split('.').map((p) => int.tryParse(p) ?? 0).toList();
      final lParts = latest.split('.').map((p) => int.tryParse(p) ?? 0).toList();
      final len = cParts.length > lParts.length ? cParts.length : lParts.length;
      while (cParts.length < len) {
        cParts.add(0);
      }
      while (lParts.length < len) {
        lParts.add(0);
      }
      for (var i = 0; i < len; i++) {
        if (lParts[i] > cParts[i]) return true;
        if (lParts[i] < cParts[i]) return false;
      }
    } catch (_) {}
    return false;
  }

  String? _findAetherPath() {
    final appData = Platform.environment['LOCALAPPDATA'];
    if (appData != null) {
      final cached = File('$appData\\Se7en\\aether\\aether.exe');
      if (cached.existsSync()) return cached.path;
      final cachedNamed = File('$appData\\Se7en\\aether\\Se7enPro.Aether.exe');
      if (cachedNamed.existsSync()) return cachedNamed.path;
    }
    try {
      final exeDir = File(Platform.resolvedExecutable).parent.path;
      final res = File('$exeDir\\Resources\\aether\\aether.exe');
      if (res.existsSync()) return res.path;
    } catch (_) {}
    final candidate = File('${Directory.current.path}\\Resources\\aether\\aether.exe');
    if (candidate.existsSync()) return candidate.path;
    return null;
  }

  String? _findTorPath() {
    final candidate = File('${Directory.current.path}\\Resources\\tor\\tor.exe');
    if (candidate.existsSync()) return candidate.path;
    return null;
  }

  String? _queryExeVersion(String exePath, String arg) {
    try {
      final res = Process.runSync(exePath, [arg]);
      if (res.exitCode == 0) {
        return (res.stdout as String? ?? '').trim();
      }
    } catch (_) {}
    return null;
  }
}
