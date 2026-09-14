import 'connection_state.dart';

class TunnelStatus {
  const TunnelStatus({
    this.state = ConnectionState.disconnected,
    this.socksProxyPort = 0,
    this.httpProxyPort = 0,
    this.clientRegion = '',
    this.connectedServerRegion = '',
    this.currentRouteIp = '',
    this.currentRouteSni = '',
    this.bytesSent = 0,
    this.bytesReceived = 0,
    this.downSpeed = 0.0,
    this.upSpeed = 0.0,
    this.connectProgressPercent = 0,
    this.connectProgressText = '',
    this.availableEgressRegions = const [],
    this.activeMethodToken = 'psiphon',
    this.tunActive = false,
    this.tunStatusText = 'Off',
    this.tunLastError = '',
    this.upstreamProxyDisplay = '',
    this.isAdmin = false,
  });

  final ConnectionState state;
  final int socksProxyPort;
  final int httpProxyPort;
  final String clientRegion;
  final String connectedServerRegion;
  final String currentRouteIp;
  final String currentRouteSni;
  final int bytesSent;
  final int bytesReceived;
  final double downSpeed;
  final double upSpeed;
  final int connectProgressPercent;
  final String connectProgressText;
  final List<String> availableEgressRegions;
  final String activeMethodToken;
  final bool tunActive;
  final String tunStatusText;

  final String tunLastError;

  final String upstreamProxyDisplay;
  final bool isAdmin;

  bool get isConnected => state == ConnectionState.connected;

  factory TunnelStatus.fromJson(Map<String, dynamic> j) => TunnelStatus(
        state: ConnectionStateX.parse(j['state'] as String?),
        socksProxyPort: j['socksProxyPort'] as int? ?? 0,
        httpProxyPort: j['httpProxyPort'] as int? ?? 0,
        clientRegion: j['clientRegion'] as String? ?? '',
        connectedServerRegion: j['connectedServerRegion'] as String? ?? '',
        currentRouteIp: j['currentRouteIp'] as String? ?? '',
        currentRouteSni: j['currentRouteSni'] as String? ?? '',
        bytesSent: (j['bytesSent'] as num?)?.toInt() ?? 0,
        bytesReceived: (j['bytesReceived'] as num?)?.toInt() ?? 0,
        downSpeed: (j['downSpeed'] as num?)?.toDouble() ?? 0.0,
        upSpeed: (j['upSpeed'] as num?)?.toDouble() ?? 0.0,
        connectProgressPercent: j['connectProgressPercent'] as int? ?? 0,
        connectProgressText: j['connectProgressText'] as String? ?? '',
        availableEgressRegions:
            (j['availableEgressRegions'] as List<dynamic>?)?.cast<String>() ??
                const [],
        activeMethodToken: j['activeMethodToken'] as String? ?? 'psiphon',
        tunActive: j['tunActive'] as bool? ?? false,
        tunStatusText: j['tunStatusText'] as String? ?? 'Off',
        tunLastError: j['tunLastError'] as String? ?? '',
        upstreamProxyDisplay: j['upstreamProxyDisplay'] as String? ?? '',
        isAdmin: j['isAdmin'] as bool? ?? false,
      );

  TunnelStatus copyWith({
    ConnectionState? state,
    int? socksProxyPort,
    int? httpProxyPort,
    String? clientRegion,
    String? connectedServerRegion,
    String? currentRouteIp,
    String? currentRouteSni,
    int? bytesSent,
    int? bytesReceived,
    double? downSpeed,
    double? upSpeed,
    int? connectProgressPercent,
    String? connectProgressText,
    List<String>? availableEgressRegions,
    String? activeMethodToken,
    bool? tunActive,
    String? tunStatusText,
    String? tunLastError,
    String? upstreamProxyDisplay,
    bool? isAdmin,
  }) =>
      TunnelStatus(
        state: state ?? this.state,
        socksProxyPort: socksProxyPort ?? this.socksProxyPort,
        httpProxyPort: httpProxyPort ?? this.httpProxyPort,
        clientRegion: clientRegion ?? this.clientRegion,
        connectedServerRegion:
            connectedServerRegion ?? this.connectedServerRegion,
        currentRouteIp: currentRouteIp ?? this.currentRouteIp,
        currentRouteSni: currentRouteSni ?? this.currentRouteSni,
        bytesSent: bytesSent ?? this.bytesSent,
        bytesReceived: bytesReceived ?? this.bytesReceived,
        downSpeed: downSpeed ?? this.downSpeed,
        upSpeed: upSpeed ?? this.upSpeed,
        connectProgressPercent:
            connectProgressPercent ?? this.connectProgressPercent,
        connectProgressText: connectProgressText ?? this.connectProgressText,
        availableEgressRegions:
            availableEgressRegions ?? this.availableEgressRegions,
        activeMethodToken: activeMethodToken ?? this.activeMethodToken,
        tunActive: tunActive ?? this.tunActive,
        tunStatusText: tunStatusText ?? this.tunStatusText,
        tunLastError: tunLastError ?? this.tunLastError,
        upstreamProxyDisplay: upstreamProxyDisplay ?? this.upstreamProxyDisplay,
        isAdmin: isAdmin ?? this.isAdmin,
      );
}
