import 'dart:convert';

class SplitTunnelEntry {
  SplitTunnelEntry({this.kind = 'domain', this.value = ''});

  String kind;
  String value;

  factory SplitTunnelEntry.fromJson(Map<String, dynamic> j) => SplitTunnelEntry(
        kind: j['kind'] as String? ?? 'domain',
        value: j['value'] as String? ?? '',
      );

  Map<String, dynamic> toJson() => {'kind': kind, 'value': value};

  SplitTunnelEntry copy() => SplitTunnelEntry(kind: kind, value: value);

  @override
  bool operator ==(Object other) =>
      identical(this, other) ||
      other is SplitTunnelEntry &&
          runtimeType == other.runtimeType &&
          kind.toLowerCase() == other.kind.toLowerCase() &&
          value.trim().toLowerCase() == other.value.trim().toLowerCase();

  @override
  int get hashCode => Object.hash(kind.toLowerCase(), value.trim().toLowerCase());
}

class UserSettings {

  String theme;
  String language;

  String egressRegion;
  bool disableTimeouts;
  int localSocksProxyPort;
  int localHttpProxyPort;
  bool useCustomProxyPorts;
  bool allowLanConnections;
  bool setSystemProxy;
  bool autoConnect;
  bool startWithWindows;
  bool ipScannerEnabled;
  bool minimizeToTray;
  String onCloseAction;

  String upstreamProxy;
  bool upstreamProxyEnabled;
  String upstreamProxyScheme;
  String upstreamProxyUsername;
  String upstreamProxyPassword;

  bool systemWideTunneling;
  bool killSwitchEnabled;

  String protocolMode;
  bool beastMode;
  String cdnFrontingCustomIpList;
  String cdnFrontingCustomSni;
  bool cdnFrontingSkipCertVerify;
  bool autoFindIpAndSni;
  bool saveFoundIpsAndSni;
  List<String> frontedMeekCDNScanBuiltInSets;
  int? establishTunnelTimeoutSeconds;

  String conduitMode;
  String conduitCompartmentId;
  bool conduitRejectCensoredCountries;

  bool lanAuthEnabled;
  String lanProxyUsername;
  String lanProxyPassword;

  String connectionMethod;

  String aetherProtocol;
  String aetherScanMode;
  String aetherNoize;
  String aetherIpVersion;
  String aetherManualPeer;
  String aetherEndpointMasque;
  String aetherEndpointWireguard;
  String aetherEndpointWarp;
  String aetherEndpointMasqueOnMasque;
  String chainedOuterTransport;
  String chainedPsiphonOuterTransport;
  String chainedTorOuterTransport;
  String chainedSubMode;
  String aetherMasqueTransport;
  bool aetherFragment;
  String aetherPerfProfile;
  bool aetherMimEnabled;
  String aetherMimOuter;
  String aetherMimInner;
  String aetherMimPeers;
  bool aetherQuicV2;
  String aetherMark;

  String torExitCountry;
  String torBridges;

  bool splitTunnelEnabled;
  String splitTunnelMode;
  List<SplitTunnelEntry> splitTunnelEntries;

  String v2rayCore;
  int v2rayInboundPort;
  bool v2rayEnableMux;
  bool v2rayRouteDnsThroughV2Ray;
  bool v2rayEnableFragment;
  String v2rayFragmentPackets;
  String v2rayFragmentLength;
  String v2rayFragmentInterval;
  String v2rayActiveConfigId;
  List<V2RayConfigEntry> v2rayConfigs;

  String shardCustomCfIp;
  bool shardSmartSplit;
  bool shardRotateIp;

  UserSettings({
    this.theme = 'dark',
    this.language = 'en',
    this.egressRegion = '',
    this.disableTimeouts = false,
    this.localSocksProxyPort = 0,
    this.localHttpProxyPort = 0,
    this.useCustomProxyPorts = false,
    this.allowLanConnections = false,
    this.setSystemProxy = true,
    this.autoConnect = false,
    this.startWithWindows = false,
    this.ipScannerEnabled = false,
    this.minimizeToTray = true,
    this.onCloseAction = 'ask',
    this.upstreamProxy = '',
    this.upstreamProxyEnabled = false,
    this.upstreamProxyScheme = 'http',
    this.upstreamProxyUsername = '',
    this.upstreamProxyPassword = '',
    this.systemWideTunneling = false,
    this.killSwitchEnabled = false,
    this.protocolMode = 'auto',
    this.beastMode = false,
    this.cdnFrontingCustomIpList = '',
    this.cdnFrontingCustomSni = '',
    this.cdnFrontingSkipCertVerify = false,
    this.autoFindIpAndSni = false,
    this.saveFoundIpsAndSni = false,
    List<String>? frontedMeekCDNScanBuiltInSets,
    this.establishTunnelTimeoutSeconds = 300,
    this.conduitMode = 'auto',
    this.conduitCompartmentId = '',
    this.conduitRejectCensoredCountries = true,
    this.lanAuthEnabled = false,
    this.lanProxyUsername = '',
    this.lanProxyPassword = '',
    this.connectionMethod = 'wireguard',
    this.aetherProtocol = 'masque',
    this.aetherScanMode = 'balanced',
    this.aetherNoize = 'balanced',
    this.aetherIpVersion = '4',
    this.aetherManualPeer = '',
    this.aetherEndpointMasque = '',
    this.aetherEndpointWireguard = '',
    this.aetherEndpointWarp = '',
    this.aetherEndpointMasqueOnMasque = '',
    this.chainedOuterTransport = 'auto',
    this.chainedPsiphonOuterTransport = 'auto',
    this.chainedTorOuterTransport = 'auto',
    this.chainedSubMode = 'psiphon_warp',
    this.aetherMasqueTransport = 'h3',
    this.aetherFragment = false,
    this.aetherPerfProfile = 'auto',
    this.aetherMimEnabled = false,
    this.aetherMimOuter = '',
    this.aetherMimInner = '',
    this.aetherMimPeers = '',
    this.aetherQuicV2 = true,
    this.aetherMark = '',
    this.torExitCountry = '',
    this.torBridges = '',
    this.splitTunnelEnabled = false,
    this.splitTunnelMode = 'exclude',
    List<SplitTunnelEntry>? splitTunnelEntries,
    this.v2rayCore = 'xray',
    this.v2rayInboundPort = 10808,
    this.v2rayEnableMux = false,
    this.v2rayRouteDnsThroughV2Ray = true,
    this.v2rayEnableFragment = true,
    this.v2rayFragmentPackets = 'tlshello',
    this.v2rayFragmentLength = '100-200',
    this.v2rayFragmentInterval = '10-20',
    this.v2rayActiveConfigId = '',
    List<V2RayConfigEntry>? v2rayConfigs,
    this.shardCustomCfIp = '',
    this.shardSmartSplit = false,
    this.shardRotateIp = true,
  })  : frontedMeekCDNScanBuiltInSets =
              frontedMeekCDNScanBuiltInSets == null
                  ? ['psiphon-akamai', 'fastly']
                  : List<String>.from(frontedMeekCDNScanBuiltInSets),
        splitTunnelEntries = splitTunnelEntries ?? [],
        v2rayConfigs = v2rayConfigs ?? [];

  static const int minPort = 1;
  static const int maxPort = 65535;

  static int sanitizePort(dynamic v, {int fallback = 0}) {
    if (v is! int) return fallback;
    return (v >= minPort && v <= maxPort) ? v : fallback;
  }

  static List<String> _parseStringList(dynamic v) {
    if (v is List) return v.map((e) => e.toString()).where((s) => s.isNotEmpty).toList();
    return [];
  }

  static String _normLang(String? v) {
    final t = (v ?? 'en').trim().toLowerCase();
    if (t == 'ru') return 'ru';
    if (t == 'zh') return 'zh';
    if (t == 'fa') return 'fa';
    return 'en';
  }

  factory UserSettings.fromJson(Map<String, dynamic> j) => UserSettings(
        theme: j['theme'] as String? ?? 'dark',
        language: _normLang(j['language'] as String?),
        egressRegion: j['egressRegion'] as String? ?? '',
        disableTimeouts: j['disableTimeouts'] as bool? ?? false,
        localSocksProxyPort: sanitizePort(j['localSocksProxyPort']),
        localHttpProxyPort: sanitizePort(j['localHttpProxyPort']),
        useCustomProxyPorts: j['useCustomProxyPorts'] as bool? ??
            ((j['localSocksProxyPort'] != null && (j['localSocksProxyPort'] as int? ?? 0) > 0) ||
                (j['localHttpProxyPort'] != null && (j['localHttpProxyPort'] as int? ?? 0) > 0)),
        allowLanConnections: j['allowLanConnections'] as bool? ?? false,
        setSystemProxy: j['setSystemProxy'] as bool? ?? true,
        autoConnect: j['autoConnect'] as bool? ?? false,
        startWithWindows: j['startWithWindows'] as bool? ?? false,
        ipScannerEnabled: j['ipScannerEnabled'] as bool? ?? false,
        minimizeToTray: j['minimizeToTray'] as bool? ?? true,
        onCloseAction: j['onCloseAction'] as String? ?? 'ask',
        upstreamProxy: j['upstreamProxy'] as String? ?? '',
        upstreamProxyEnabled: j['upstreamProxyEnabled'] as bool? ?? false,
        upstreamProxyScheme: j['upstreamProxyScheme'] as String? ?? 'http',
        upstreamProxyUsername: j['upstreamProxyUsername'] as String? ?? '',
        upstreamProxyPassword: j['upstreamProxyPassword'] as String? ?? '',
        systemWideTunneling: j['systemWideTunneling'] as bool? ?? false,
        killSwitchEnabled: j['killSwitchEnabled'] as bool? ?? false,
        protocolMode: j['protocolMode'] as String? ?? 'auto',
        beastMode: j['beastMode'] as bool? ?? false,
        cdnFrontingCustomIpList: j['cdnFrontingCustomIpList'] as String? ?? '',
        cdnFrontingCustomSni: j['cdnFrontingCustomSni'] as String? ?? '',
        cdnFrontingSkipCertVerify:
            j['cdnFrontingSkipCertVerify'] as bool? ?? false,
        autoFindIpAndSni: j['autoFindIpAndSni'] as bool? ?? false,
        saveFoundIpsAndSni: j['saveFoundIpsAndSni'] as bool? ?? false,
        frontedMeekCDNScanBuiltInSets: j.containsKey('frontedMeekCDNScanBuiltInSets')
            ? _parseStringList(j['frontedMeekCDNScanBuiltInSets'])
            : ['psiphon-akamai', 'fastly'],
        establishTunnelTimeoutSeconds: j['establishTunnelTimeoutSeconds'] is int
            ? j['establishTunnelTimeoutSeconds'] as int
            : (j['establishTunnelTimeoutSeconds'] is num
                ? (j['establishTunnelTimeoutSeconds'] as num).toInt()
                : 300),
        conduitMode: j['conduitMode'] as String? ?? 'auto',
        conduitCompartmentId: j['conduitCompartmentId'] as String? ?? '',
        conduitRejectCensoredCountries:
            j['conduitRejectCensoredCountries'] as bool? ?? true,
        lanAuthEnabled: j['lanAuthEnabled'] as bool? ?? false,
        lanProxyUsername: j['lanProxyUsername'] as String? ?? '',
        lanProxyPassword: j['lanProxyPassword'] as String? ?? '',
        connectionMethod: j['connectionMethod'] as String? ?? 'wireguard',
        aetherProtocol: j['aetherProtocol'] as String? ?? 'masque',
        aetherScanMode: j['aetherScanMode'] as String? ?? 'balanced',
        aetherNoize: j['aetherNoize'] as String? ?? 'balanced',
        aetherIpVersion: j['aetherIpVersion'] as String? ?? '4',
        aetherManualPeer: j['aetherManualPeer'] as String? ?? '',
        aetherEndpointMasque: j['aetherEndpointMasque'] as String? ?? '',
        aetherEndpointWireguard: j['aetherEndpointWireguard'] as String? ?? '',
        aetherEndpointWarp: j['aetherEndpointWarp'] as String? ?? '',
        aetherEndpointMasqueOnMasque:
            j['aetherEndpointMasqueOnMasque'] as String? ?? '',
        chainedOuterTransport: j['chainedOuterTransport'] as String? ?? 'auto',
        chainedPsiphonOuterTransport: (j['chainedPsiphonOuterTransport'] as String?)?.trim().isNotEmpty == true
            ? (j['chainedPsiphonOuterTransport'] as String).trim()
            : ((j['chainedOuterTransport'] as String?)?.trim().isNotEmpty == true ? (j['chainedOuterTransport'] as String).trim() : 'auto'),
        chainedTorOuterTransport:
            (j['chainedTorOuterTransport'] as String?)?.trim().isNotEmpty == true
                ? (j['chainedTorOuterTransport'] as String).trim()
                : ((j['chainedOuterTransport'] as String?)?.trim().isNotEmpty == true ? (j['chainedOuterTransport'] as String).trim() : 'auto'),

        chainedSubMode: j['chainedSubMode'] as String? ?? 'psiphon_warp',
        aetherMasqueTransport: j['aetherMasqueTransport'] as String? ?? 'h3',
        aetherFragment: j['aetherFragment'] as bool? ?? false,
        aetherPerfProfile: j['aetherPerfProfile'] as String? ?? 'auto',
        aetherMimEnabled: j['aetherMimEnabled'] as bool? ?? false,
        aetherMimOuter: j['aetherMimOuter'] as String? ?? '',
        aetherMimInner: j['aetherMimInner'] as String? ?? '',
        aetherMimPeers: j['aetherMimPeers'] as String? ?? '',
        aetherQuicV2: j['aetherQuicV2'] as bool? ?? true,
        aetherMark: j['aetherMark'] as String? ?? '',
        torExitCountry: j['torExitCountry'] as String? ?? '',
        torBridges: j['torBridges'] as String? ?? '',
        splitTunnelEnabled: j['splitTunnelEnabled'] as bool? ?? false,
        splitTunnelMode: j['splitTunnelMode'] as String? ?? 'exclude',
        splitTunnelEntries: (j['splitTunnelEntries'] as List<dynamic>?)
                ?.map((e) => SplitTunnelEntry.fromJson(e as Map<String, dynamic>))
                .toList() ??
            [],
        v2rayCore: j['v2rayCore'] as String? ?? 'xray',
        v2rayInboundPort: sanitizePort(j['v2rayInboundPort'], fallback: 10808),
        v2rayEnableMux: j['v2rayEnableMux'] as bool? ?? false,
        v2rayRouteDnsThroughV2Ray: j['v2rayRouteDnsThroughV2Ray'] as bool? ?? true,
        v2rayEnableFragment: j['v2rayEnableFragment'] as bool? ?? true,
        v2rayFragmentPackets: j['v2rayFragmentPackets'] as String? ?? 'tlshello',
        v2rayFragmentLength: j['v2rayFragmentLength'] as String? ?? '100-200',
        v2rayFragmentInterval: j['v2rayFragmentInterval'] as String? ?? '10-20',
        v2rayActiveConfigId: j['v2rayActiveConfigId'] as String? ?? '',
        v2rayConfigs: (j['v2rayConfigs'] as List<dynamic>?)
                ?.map((e) => V2RayConfigEntry.fromJson(e as Map<String, dynamic>))
                .toList() ??
            [],
        shardCustomCfIp: j['shardCustomCfIp'] as String? ?? '',
        shardSmartSplit: j['shardSmartSplit'] as bool? ?? false,
        shardRotateIp: j['shardRotateIp'] as bool? ?? true,
      );

  Map<String, dynamic> toJson() => {
        'theme': theme,
        'language': language,
        'egressRegion': egressRegion,
        'disableTimeouts': disableTimeouts,
        'localSocksProxyPort': localSocksProxyPort,
        'localHttpProxyPort': localHttpProxyPort,
        'useCustomProxyPorts': useCustomProxyPorts,
        'allowLanConnections': allowLanConnections,
        'setSystemProxy': setSystemProxy,
        'autoConnect': autoConnect,
        'startWithWindows': startWithWindows,
        'ipScannerEnabled': ipScannerEnabled,
        'minimizeToTray': minimizeToTray,
        'onCloseAction': onCloseAction,
        'upstreamProxy': upstreamProxy,
        'upstreamProxyEnabled': upstreamProxyEnabled,
        'upstreamProxyScheme': upstreamProxyScheme,
        'upstreamProxyUsername': upstreamProxyUsername,
        'upstreamProxyPassword': upstreamProxyPassword,
        'systemWideTunneling': systemWideTunneling,
        'killSwitchEnabled': killSwitchEnabled,
        'protocolMode': protocolMode,
        'beastMode': beastMode,
        'cdnFrontingCustomIpList': cdnFrontingCustomIpList,
        'cdnFrontingCustomSni': cdnFrontingCustomSni,
        'cdnFrontingSkipCertVerify': cdnFrontingSkipCertVerify,
        'autoFindIpAndSni': autoFindIpAndSni,
        'saveFoundIpsAndSni': saveFoundIpsAndSni,
        'frontedMeekCDNScanBuiltInSets': frontedMeekCDNScanBuiltInSets,
        'establishTunnelTimeoutSeconds': establishTunnelTimeoutSeconds,
        'conduitMode': conduitMode,
        'conduitCompartmentId': conduitCompartmentId,
        'conduitRejectCensoredCountries': conduitRejectCensoredCountries,
        'lanAuthEnabled': lanAuthEnabled,
        'lanProxyUsername': lanProxyUsername,
        'lanProxyPassword': lanProxyPassword,
        'connectionMethod': connectionMethod,
        'aetherProtocol': aetherProtocol,
        'aetherScanMode': aetherScanMode,
        'aetherNoize': aetherNoize,
        'aetherIpVersion': aetherIpVersion,
        'aetherManualPeer': aetherManualPeer,
        'aetherEndpointMasque': aetherEndpointMasque,
        'aetherEndpointWireguard': aetherEndpointWireguard,
        'aetherEndpointWarp': aetherEndpointWarp,
        'aetherEndpointMasqueOnMasque': aetherEndpointMasqueOnMasque,
        'chainedOuterTransport': chainedOuterTransport,
        'chainedPsiphonOuterTransport': chainedPsiphonOuterTransport,
        'chainedTorOuterTransport': chainedTorOuterTransport,
        'chainedSubMode': chainedSubMode,
        'aetherMasqueTransport': aetherMasqueTransport,
        'aetherFragment': aetherFragment,
        'aetherPerfProfile': aetherPerfProfile,
        'aetherMimEnabled': aetherMimEnabled,
        'aetherMimOuter': aetherMimOuter,
        'aetherMimInner': aetherMimInner,
        'aetherMimPeers': aetherMimPeers,
        'aetherQuicV2': aetherQuicV2,
        'aetherMark': aetherMark,
        'torExitCountry': torExitCountry,
        'torBridges': torBridges,
        'splitTunnelEnabled': splitTunnelEnabled,
        'splitTunnelMode': splitTunnelMode,
        'splitTunnelEntries': splitTunnelEntries.map((e) => e.toJson()).toList(),
        'v2rayCore': v2rayCore,
        'v2rayInboundPort': v2rayInboundPort,
        'v2rayEnableMux': v2rayEnableMux,
        'v2rayRouteDnsThroughV2Ray': v2rayRouteDnsThroughV2Ray,
        'v2rayEnableFragment': v2rayEnableFragment,
        'v2rayFragmentPackets': v2rayFragmentPackets,
        'v2rayFragmentLength': v2rayFragmentLength,
        'v2rayFragmentInterval': v2rayFragmentInterval,
        'v2rayActiveConfigId': v2rayActiveConfigId,
        'v2rayConfigs': v2rayConfigs.map((e) => e.toJson()).toList(),
        'shardCustomCfIp': shardCustomCfIp,
        'shardSmartSplit': shardSmartSplit,
        'shardRotateIp': shardRotateIp,
      };

  String encode() => jsonEncode(toJson());

  factory UserSettings.decode(String raw) =>
      UserSettings.fromJson(jsonDecode(raw) as Map<String, dynamic>);

  UserSettings clone() => UserSettings.fromJson(jsonDecode(encode()));
}

class V2RayConfigEntry {
  V2RayConfigEntry({
    required this.id,
    required this.name,
    this.protocol = 'vless',
    required this.address,
    this.port = 443,
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
  });

  String id;
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
  bool? enableFragment;

  factory V2RayConfigEntry.fromJson(Map<String, dynamic> j) => V2RayConfigEntry(
        id: j['id'] as String? ?? '',
        name: j['name'] as String? ?? '',
        protocol: j['protocol'] as String? ?? 'vless',
        address: j['address'] as String? ?? '',
        port: UserSettings.sanitizePort(j['port'], fallback: 443),
        userId: j['userId'] as String? ?? '',
        security: j['security'] as String? ?? 'reality',
        network: j['network'] as String? ?? 'tcp',
        path: j['path'] as String?,
        host: j['host'] as String?,
        sni: j['sni'] as String?,
        publicKey: j['publicKey'] as String?,
        shortId: j['shortId'] as String?,
        flow: j['flow'] as String?,
        isActive: j['isActive'] as bool? ?? false,
        latency: j['latency'] as int? ?? -1,
        enableFragment: j['enableFragment'] as bool?,
      );

  Map<String, dynamic> toJson() => {
        'id': id,
        'name': name,
        'protocol': protocol,
        'address': address,
        'port': port,
        'userId': userId,
        'security': security,
        'network': network,
        'path': path,
        'host': host,
        'sni': sni,
        'publicKey': publicKey,
        'shortId': shortId,
        'flow': flow,
        'isActive': isActive,
        'latency': latency,
        if (enableFragment != null) 'enableFragment': enableFragment,
      };
}
