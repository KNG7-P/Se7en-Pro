
enum ConnectionMethod {

  masque,
  wireguard,
  warpOnWarp,

  psiphon,
  psiphonOverWarp,
  psiphonOverV2Ray,

  tor,
  torOverWarp,
  torOverV2Ray,

  shard,

  masqueOnMasque;
}

class ConnectionMethodOption {
  const ConnectionMethodOption(this.key, this.display, this.description);
  final String key;
  final String display;
  final String description;
}

extension ConnectionMethodX on ConnectionMethod {

  String get token => switch (this) {
        ConnectionMethod.psiphon => 'psiphon',
        ConnectionMethod.masque => 'masque',
        ConnectionMethod.wireguard => 'wireguard',
        ConnectionMethod.warpOnWarp => 'warp_on_warp',
        ConnectionMethod.tor => 'tor',
        ConnectionMethod.psiphonOverWarp => 'psiphon_over_warp',
        ConnectionMethod.torOverWarp => 'tor_over_warp',
        ConnectionMethod.psiphonOverV2Ray => 'psiphon_over_v2ray',
        ConnectionMethod.torOverV2Ray => 'tor_over_v2ray',
        ConnectionMethod.shard => 'shard',
        ConnectionMethod.masqueOnMasque => 'masque_on_masque',
      };

  String get displayName => switch (this) {
        ConnectionMethod.psiphon => 'Psiphon',
        ConnectionMethod.masque => 'MASQUE',
        ConnectionMethod.wireguard => 'WireGuard',
        ConnectionMethod.warpOnWarp => 'Warp on Warp',
        ConnectionMethod.tor => 'Tor',
        ConnectionMethod.psiphonOverWarp => 'Psiphon over WARP',
        ConnectionMethod.torOverWarp => 'Tor over WARP',
        ConnectionMethod.psiphonOverV2Ray => 'Psiphon over V2Ray',
        ConnectionMethod.torOverV2Ray => 'Tor over V2Ray',
        ConnectionMethod.shard => 'SHARD',
        ConnectionMethod.masqueOnMasque => 'Masque on Masque',
      };

  bool get isAether =>
      this == ConnectionMethod.masque ||
      this == ConnectionMethod.wireguard ||
      this == ConnectionMethod.warpOnWarp ||
      this == ConnectionMethod.masqueOnMasque;

  bool get isChained =>
      this == ConnectionMethod.psiphonOverWarp ||
      this == ConnectionMethod.torOverWarp ||
      this == ConnectionMethod.psiphonOverV2Ray ||
      this == ConnectionMethod.torOverV2Ray;

  bool get supportsRegionPicker =>
      this == ConnectionMethod.psiphon ||
      this == ConnectionMethod.tor ||
      this == ConnectionMethod.psiphonOverWarp ||
      this == ConnectionMethod.torOverWarp ||
      this == ConnectionMethod.psiphonOverV2Ray ||
      this == ConnectionMethod.torOverV2Ray;

  bool get isTor =>
      this == ConnectionMethod.tor ||
      this == ConnectionMethod.torOverWarp ||
      this == ConnectionMethod.torOverV2Ray;

  bool get isPsiphon =>
      this == ConnectionMethod.psiphon ||
      this == ConnectionMethod.psiphonOverWarp ||
      this == ConnectionMethod.psiphonOverV2Ray;

  bool get isShard => this == ConnectionMethod.shard;

  bool get usesV2Ray =>
      this == ConnectionMethod.psiphonOverV2Ray ||
      this == ConnectionMethod.torOverV2Ray;

  static ConnectionMethod parse(String? token) =>
      switch ((token ?? '').trim().toLowerCase()) {
        'psiphon' => ConnectionMethod.psiphon,
        'masque' => ConnectionMethod.masque,
        'wireguard' || 'warp' || 'wg' => ConnectionMethod.wireguard,
        'warp_on_warp' || 'warponwarp' || 'gool' || 'wiw' =>
          ConnectionMethod.warpOnWarp,
        'tor' => ConnectionMethod.tor,
        'psiphon_over_warp' || 'psiphonoverwarp' || 'chain' || 'pow' =>
          ConnectionMethod.psiphonOverWarp,
        'tor_over_warp' || 'toroverwarp' || 'tow' => ConnectionMethod.torOverWarp,
        'psiphon_over_v2ray' || 'psiphonoverv2ray' || 'psiphon_v2ray' || 'pov' =>
          ConnectionMethod.psiphonOverV2Ray,
        'tor_over_v2ray' || 'toroverv2ray' || 'tor_v2ray' || 'tov' =>
          ConnectionMethod.torOverV2Ray,
        'shard' => ConnectionMethod.shard,
        'masque_on_masque' ||
                'masqueonmasque' ||
                'masque_in_masque' ||
                'mim' ||
                'mom' =>
          ConnectionMethod.masqueOnMasque,
        _ => ConnectionMethod.wireguard,
      };

  static const List<ConnectionMethodOption> allOptions = [
    ConnectionMethodOption('psiphon', 'Psiphon',
        'CDN-fronting capable Psiphon core. Most resilient on heavily filtered networks.'),
    ConnectionMethodOption('masque', 'MASQUE',
        'Cloudflare WARP over MASQUE. Fast and hard to fingerprint.'),
    ConnectionMethodOption('wireguard', 'WireGuard',
        'Classic Cloudflare WARP over WireGuard.'),
    ConnectionMethodOption('warp_on_warp', 'Warp on Warp',
        'WARP tunnelled inside WARP — an extra hop for tougher networks.'),
    ConnectionMethodOption('tor', 'Tor',
        'The Tor network with optional bridges and pluggable transports.'),
    ConnectionMethodOption('psiphon_over_warp', 'Psiphon over WARP',
        'Multi-hop: Psiphon tunnelled inside Cloudflare WARP/MASQUE for maximum DPI evasion.'),
    ConnectionMethodOption('psiphon_over_v2ray', 'Psiphon over V2Ray',
        'Multi-hop: Psiphon tunnelled inside V2Ray / Xray / Sing-box proxy node.'),
    ConnectionMethodOption('tor_over_warp', 'Tor over WARP',
        'Multi-hop: Tor network traffic routed through Cloudflare WARP.'),
    ConnectionMethodOption('tor_over_v2ray', 'Tor over V2Ray',
        'Multi-hop: Tor network traffic routed through V2Ray / Xray / Sing-box proxy node.'),
    ConnectionMethodOption('shard', 'SHARD',
        'Dedicated Cloudflare SHARD transport with TLS fragmentation and cipher-suite pinning.'),
    ConnectionMethodOption('masque_on_masque', 'Masque on Masque',
        'Double MASQUE hops inside Cloudflare European backbone. Clean foreign exit IP.'),
  ];
}
