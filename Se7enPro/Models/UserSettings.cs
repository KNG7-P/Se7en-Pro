using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Se7enPro.Models;

public sealed class UserSettings
{
    [JsonPropertyName("theme")]
    public string Theme { get; set; } = "dark";

    [JsonPropertyName("language")]
    public string Language { get; set; } = "en";

    [JsonPropertyName("visualEffects")]
    public string VisualEffects { get; set; } = "auto";

    [JsonPropertyName("egressRegion")]
    public string EgressRegion { get; set; } = "";

    [JsonPropertyName("disableTimeouts")]
    public bool DisableTimeouts { get; set; }

    [JsonPropertyName("localSocksProxyPort")]
    public int LocalSocksProxyPort { get; set; }

    [JsonPropertyName("localHttpProxyPort")]
    public int LocalHttpProxyPort { get; set; }

    [JsonPropertyName("useCustomProxyPorts")]
    public bool UseCustomProxyPorts { get; set; }

    [JsonPropertyName("lanAuthEnabled")]
    public bool LanAuthEnabled { get; set; }

    [JsonPropertyName("allowLanConnections")]
    public bool AllowLanConnections { get; set; }

    [JsonPropertyName("setSystemProxy")]
    public bool SetSystemProxy { get; set; } = true;

    [JsonPropertyName("autoConnect")]
    public bool AutoConnect { get; set; }

    [JsonPropertyName("startWithWindows")]
    public bool StartWithWindows { get; set; }

    [JsonPropertyName("minimizeToTray")]
    public bool MinimizeToTray { get; set; } = true;

    [JsonPropertyName("onCloseAction")]
    public string OnCloseAction { get; set; } = "ask";

    [JsonPropertyName("upstreamProxy")]
    public string UpstreamProxy { get; set; } = "";

    
    
    
    
    [JsonPropertyName("upstreamProxyEnabled")]
    public bool UpstreamProxyEnabled { get; set; } = true;

    [JsonPropertyName("upstreamProxyScheme")]
    public string UpstreamProxyScheme { get; set; } = "http";

    [JsonPropertyName("upstreamProxyUsername")]
    public string UpstreamProxyUsername { get; set; } = "";

    [JsonPropertyName("upstreamProxyPassword")]
    public string UpstreamProxyPassword { get; set; } = "";

    [JsonPropertyName("systemWideTunneling")]
    public bool SystemWideTunneling { get; set; }

    [JsonPropertyName("killSwitchEnabled")]
    public bool KillSwitchEnabled { get; set; }

    [JsonPropertyName("protocolMode")]
    public string ProtocolMode { get; set; } = "auto";

    [JsonPropertyName("beastMode")]
    public bool BeastMode { get; set; }

    [JsonPropertyName("cdnFrontingCustomIpList")]
    public string CdnFrontingCustomIpList { get; set; } = "";

    [JsonPropertyName("cdnFrontingCustomSni")]
    public string CdnFrontingCustomSni { get; set; } = "";

    [JsonPropertyName("cdnFrontingSkipCertVerify")]
    public bool CdnFrontingSkipCertVerify { get; set; } = false;

    [JsonPropertyName("autoFindIpAndSni")]
    public bool AutoFindIpAndSni { get; set; }

    [JsonPropertyName("saveFoundIpsAndSni")]
    public bool SaveFoundIpsAndSni { get; set; } = false;

    private List<string> _frontedMeekCDNScanBuiltInSets = new() { "psiphon-akamai", "fastly" };
    [JsonPropertyName("frontedMeekCDNScanBuiltInSets")]
    public List<string> FrontedMeekCDNScanBuiltInSets
    {
        get => _frontedMeekCDNScanBuiltInSets ??= new() { "psiphon-akamai", "fastly" };
        set => _frontedMeekCDNScanBuiltInSets = value ?? new() { "psiphon-akamai", "fastly" };
    }

    [JsonPropertyName("establishTunnelTimeoutSeconds")]
    public int? EstablishTunnelTimeoutSeconds { get; set; } = 300;

    [JsonPropertyName("conduitMode")]
    public string ConduitMode { get; set; } = "auto";

    [JsonPropertyName("conduitCompartmentId")]
    public string ConduitCompartmentId { get; set; } = "";

    [JsonPropertyName("conduitRejectCensoredCountries")]
    public bool ConduitRejectCensoredCountries { get; set; } = true;

    [JsonPropertyName("lanProxyUsername")]
    public string LanProxyUsername { get; set; } = "";

    [JsonPropertyName("lanProxyPassword")]
    public string LanProxyPassword { get; set; } = "";

    
    
    
    
    
    
    
    
    
    
    [JsonPropertyName("connectionMethod")]
    public string ConnectionMethod { get; set; } = "wireguard";

    
    
    [JsonPropertyName("aetherProtocol")]
    public string AetherProtocol { get; set; } = "masque";

    
    [JsonPropertyName("aetherScanMode")]
    public string AetherScanMode { get; set; } = "balanced";

    [JsonPropertyName("aetherScanModeMasque")]
    public string AetherScanModeMasque { get; set; } = "balanced";

    [JsonPropertyName("aetherScanModeMim")]
    public string AetherScanModeMim { get; set; } = "turbo";

    [JsonPropertyName("aetherScanModeWireguard")]
    public string AetherScanModeWireguard { get; set; } = "balanced";

    [JsonPropertyName("aetherScanModeWarp")]
    public string AetherScanModeWarp { get; set; } = "balanced";

    
    
    [JsonPropertyName("aetherNoize")]
    public string AetherNoize { get; set; } = "balanced";

    [JsonPropertyName("aetherNoizeMasque")]
    public string AetherNoizeMasque { get; set; } = "balanced";

    [JsonPropertyName("aetherNoizeMim")]
    public string AetherNoizeMim { get; set; } = "balanced";

    [JsonPropertyName("aetherNoizeWireguard")]
    public string AetherNoizeWireguard { get; set; } = "balanced";

    [JsonPropertyName("aetherNoizeWarp")]
    public string AetherNoizeWarp { get; set; } = "balanced";

    
    [JsonPropertyName("aetherIpVersion")]
    public string AetherIpVersion { get; set; } = "4";

    [JsonPropertyName("aetherIpVersionMasque")]
    public string AetherIpVersionMasque { get; set; } = "4";

    [JsonPropertyName("aetherIpVersionMim")]
    public string AetherIpVersionMim { get; set; } = "4";

    [JsonPropertyName("aetherIpVersionWireguard")]
    public string AetherIpVersionWireguard { get; set; } = "4";

    [JsonPropertyName("aetherIpVersionWarp")]
    public string AetherIpVersionWarp { get; set; } = "4";

    
    
    [JsonPropertyName("aetherManualPeer")]
    public string AetherManualPeer { get; set; } = "";

    // ---- Aether 2.3.0: --gool changed meaning ----
    //
    // In aether 2.1.x "--gool" was WARP-in-WARP. In 2.3.0 it carries a WireGuard WARP
    // identity inside the MASQUE tunnel and registers it THROUGH that tunnel, so the exit
    // is foreign; the old behaviour moved to "--gool-classic".
    //
    // "masque" is the default because it is the one that solves the blocked-registration
    // problem. Choosing "classic" keeps the manual wiw peers meaningful.
    [JsonPropertyName("aetherGoolMode")]
    public string AetherGoolMode { get; set; } = "masque";

    // ---- Aether 2.3.0: optional capabilities ----
    //
    // Every one of these is withheld unless the bundled aether.exe advertises the flag, so
    // a missing or older binary degrades to the previous behaviour instead of failing to
    // start. See AetherCapabilities.
    //
    // Note: aetherEch, aetherExitLocSecs and aetherFragmentWireguard already existed in this
    // model but were never read by anything. They are wired now rather than redeclared.

    /// <summary>Certificate verification. aether leaves it off by default.</summary>
    [JsonPropertyName("aetherTlsVerify")]
    public bool AetherTlsVerify { get; set; } = false;

    // ---- TLS fingerprint shaping ----
    //
    // Aether writes Chrome's ClientHello by default. Deep packet inspection does not match
    // on "is this TLS", it matches on "does this look exactly like the browser I claim to
    // be", so the cipher list, the group order and the GREASE values are all part of the
    // fingerprint and all of them are levers. They exist because some mobile operators'
    // middleboxes are known to fingerprint rather than simply block.

    /// <summary>
    /// TLS 1.2 cipher suites, ':' separated, e.g. "ECDHE-ECDSA-AES128-GCM-SHA256".
    /// Blank keeps Chrome's default. TLS 1.3 suites cannot be changed and are ignored.
    /// </summary>
    [JsonPropertyName("aetherTlsCiphers")]
    public string AetherTlsCiphers { get; set; } = "";

    /// <summary>
    /// TLS groups in preference order; the first with a usable key share wins.
    /// Blank keeps Chrome's "P-256:X25519:P-384".
    /// </summary>
    [JsonPropertyName("aetherTlsGroups")]
    public string AetherTlsGroups { get; set; } = "";

    /// <summary>Drop the GREASE values in TLS handshake.</summary>
    [JsonPropertyName("aetherDisableGrease")]
    public bool AetherDisableGrease { get; set; } = false;

    /// <summary>Domain for ECH key lookup. Defaults to cloudflare-ech.com.</summary>
    [JsonPropertyName("aetherEchDomain")]
    public string AetherEchDomain { get; set; } = "";

    /// <summary>Resolver for ECH key lookup. Defaults to Custom DNS.</summary>
    [JsonPropertyName("aetherEchDns")]
    public string AetherEchDns { get; set; } = "";

    /// <summary>Carry Tor inside the tunnel; bridges are then fetched through it.</summary>
    [JsonPropertyName("aetherTor")]
    public bool AetherTor { get; set; } = false;

    /// <summary>Use running onionoo relays as plain bridges, for a network that blocks Tor outright.</summary>
    [JsonPropertyName("aetherTorRelays")]
    public bool AetherTorRelays { get; set; } = false;

    /// <summary>
    /// Fragment the TLS ClientHello on the WireGuard carrier too, not just HTTP/2.
    ///
    /// WireGuard mode opens a plain UDP socket with no TLS, so this only has an effect on
    /// the transports that actually negotiate TLS; the flag is therefore gated on those.
    /// </summary>
    [JsonPropertyName("aetherFragmentWireguard")]
    public bool AetherFragmentWireguard { get; set; } = false;

    /// <summary>
    /// How often the exit country is rechecked, in seconds. 0 keeps aether's own default.
    ///
    /// Only sent alongside --exit-loc, since there is nothing to recheck without it.
    /// </summary>
    [JsonPropertyName("aetherExitLocSecs")]
    public int AetherExitLocSecs { get; set; } = 0;

    [JsonPropertyName("aetherEndpointMasque")]
    public string AetherEndpointMasque { get; set; } = "";

    [JsonPropertyName("aetherEndpointMim")]
    public string AetherEndpointMim { get; set; } = "";

    [JsonPropertyName("aetherEndpointWireguard")]
    public string AetherEndpointWireguard { get; set; } = "";

    [JsonPropertyName("aetherEndpointWarp")]
    public string AetherEndpointWarp { get; set; } = "";

    
    [JsonPropertyName("aetherWiwOuterPeer")]
    public string AetherWiwOuterPeer { get; set; } = "";

    [JsonPropertyName("aetherWiwInnerPeer")]
    public string AetherWiwInnerPeer { get; set; } = "";

    
    [JsonPropertyName("aetherMimOuterPeer")]
    public string AetherMimOuterPeer { get; set; } = "";

    [JsonPropertyName("aetherMimInnerPeer")]
    public string AetherMimInnerPeer { get; set; } = "";

    
    [JsonPropertyName("aetherExitLoc")]
    public string AetherExitLoc { get; set; } = "";

    [JsonPropertyName("aetherExitLocMasque")]
    public string AetherExitLocMasque { get; set; } = "";

    [JsonPropertyName("aetherExitLocMim")]
    public string AetherExitLocMim { get; set; } = "";

    [JsonPropertyName("aetherExitLocWireguard")]
    public string AetherExitLocWireguard { get; set; } = "";

    [JsonPropertyName("aetherExitLocWarp")]
    public string AetherExitLocWarp { get; set; } = "";

    
    
    [JsonPropertyName("aetherCacheEdges")]
    public bool AetherCacheEdges { get; set; } = true;

    
    [JsonPropertyName("chainedOuterTransport")]
    public string ChainedOuterTransport { get; set; } = "auto";

    [JsonPropertyName("chainedPsiphonOuterTransport")]
    public string ChainedPsiphonOuterTransport { get; set; } = "auto";

    [JsonPropertyName("chainedTorOuterTransport")]
    public string ChainedTorOuterTransport { get; set; } = "auto";

    
    [JsonPropertyName("chainedSubMode")]
    public string ChainedSubMode { get; set; } = "psiphon_warp";

    
    
    
    
    
    
    
    [JsonPropertyName("aetherEch")]
    public bool AetherEch { get; set; }

    
    
    
    
    
    [JsonPropertyName("aetherMasqueTransport")]
    public string AetherMasqueTransport
    {
        get => _aetherMasqueTransport;
        set
        {
            _aetherMasqueTransport = value;
            _aetherMasqueQuic = string.Equals(value, "h3", System.StringComparison.OrdinalIgnoreCase);
        }
    }
    private string _aetherMasqueTransport = "h3";

    [JsonPropertyName("aetherMasqueQuic")]
    public bool AetherMasqueQuic
    {
        get => _aetherMasqueQuic;
        set
        {
            _aetherMasqueQuic = value;
            _aetherMasqueTransport = value ? "h3" : "h2";
        }
    }
    private bool _aetherMasqueQuic = true;

    
    [JsonPropertyName("aetherFragment")]
    public bool AetherFragment { get; set; } = true;

    
    [JsonPropertyName("aetherFragmentSize")]
    public string AetherFragmentSize { get; set; } = "16-32";

    [JsonPropertyName("aetherFragmentDelay")]
    public string AetherFragmentDelay { get; set; } = "2-10";

    [JsonPropertyName("aetherUpstreamProxy")]
    public string AetherUpstreamProxy { get; set; } = "";

    [JsonPropertyName("aetherUpstreamProxyEnabled")]
    public bool AetherUpstreamProxyEnabled { get; set; } = false;

    [JsonPropertyName("aetherUpstreamProxyScheme")]
    public string AetherUpstreamProxyScheme { get; set; } = "socks5";

    [JsonPropertyName("aetherUpstreamProxyHost")]
    public string AetherUpstreamProxyHost { get; set; } = "";

    [JsonPropertyName("aetherUpstreamProxyPort")]
    public string AetherUpstreamProxyPort { get; set; } = "";

    [JsonPropertyName("aetherUpstreamProxyUsername")]
    public string AetherUpstreamProxyUsername { get; set; } = "";

    [JsonPropertyName("aetherUpstreamProxyPassword")]
    public string AetherUpstreamProxyPassword { get; set; } = "";

    
    [JsonPropertyName("torExitCountry")]
    public string TorExitCountry { get; set; } = "";

    
    
    [JsonPropertyName("torBridges")]
    public string TorBridges { get; set; } = "";

    
    
    
    
    
    [JsonPropertyName("splitTunnelEnabled")]
    public bool SplitTunnelEnabled { get; set; }

    
    
    
    
    
    [JsonPropertyName("splitTunnelMode")]
    public string SplitTunnelMode { get; set; } = "exclude";

    [JsonPropertyName("splitTunnelEntries")]
    public List<SplitTunnelEntry> SplitTunnelEntries { get; set; } = new();

    
    [JsonPropertyName("shardCustomCfIp")]
    public string ShardCustomCfIp { get; set; } = "";

    [JsonPropertyName("shardSmartSplit")]
    public bool ShardSmartSplit { get; set; } = false;

    [JsonPropertyName("shardRotateIp")]
    public bool ShardRotateIp { get; set; } = true;

    [JsonPropertyName("shadowsocksMethod")]
    public string ShadowsocksMethod { get; set; } = "2022-blake3-aes-128-gcm";

    
    [JsonPropertyName("v2rayCore")]
    public string V2RayCore { get; set; } = "xray";

    // ---- Custom DNS resolvers (tab: Network) ----
    // Blank means "automatic": the tunnel keeps using the resolvers it picks today.
    // Populated lists take precedence over those defaults, in the order typed.

    [JsonPropertyName("customDnsUdp")]
    public string CustomDnsUdp { get; set; } = "";

    [JsonPropertyName("customDnsDot")]
    public string CustomDnsDot { get; set; } = "";

    [JsonPropertyName("customDnsDoh")]
    public string CustomDnsDoh { get; set; } = "";

    /// <summary>
    /// When true the tunnel refuses to fall back to any resolver the user did not list.
    /// Off by default: a typo in a list should degrade to working DNS, not to none.
    /// </summary>
    [JsonPropertyName("customDnsStrict")]
    public bool CustomDnsStrict { get; set; } = false;

    /// <summary>Target number of pre-provisioned Cloudflare identities in pool.</summary>
    [JsonPropertyName("identityPoolTarget")]
    public int IdentityPoolTarget { get; set; } = 3;

    /// <summary>Base URLs of bootstrap relays for identity provisioning.</summary>
    [JsonPropertyName("identityRelayUrls")]
    public string IdentityRelayUrls { get; set; } = "";

    [JsonPropertyName("v2rayInboundPort")]
    public int V2RayInboundPort { get; set; } = 10808;

    [JsonPropertyName("v2rayEnableMux")]
    public bool V2RayEnableMux { get; set; } = false;

    [JsonPropertyName("v2rayRouteDnsThroughV2Ray")]
    public bool V2RayRouteDnsThroughV2Ray { get; set; } = true;

    [JsonPropertyName("v2rayEnableFragment")]
    public bool V2RayEnableFragment { get; set; } = true;

    [JsonPropertyName("v2rayFragmentPackets")]
    public string V2RayFragmentPackets { get; set; } = "tlshello";

    [JsonPropertyName("v2rayFragmentLength")]
    public string V2RayFragmentLength { get; set; } = "100-200";

    [JsonPropertyName("v2rayFragmentInterval")]
    public string V2RayFragmentInterval { get; set; } = "10-20";

    [JsonPropertyName("v2rayActiveConfigId")]
    public string V2RayActiveConfigId { get; set; } = "";

    private List<V2RayConfigEntry> _v2rayConfigs = new();
    [JsonPropertyName("v2rayConfigs")]
    public List<V2RayConfigEntry> V2RayConfigs
    {
        get => _v2rayConfigs ??= new();
        set => _v2rayConfigs = value ?? new();
    }
}

public sealed class V2RayConfigEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("protocol")]
    public string Protocol { get; set; } = "vless";

    [JsonPropertyName("address")]
    public string Address { get; set; } = "";

    [JsonPropertyName("port")]
    public int Port { get; set; } = 443;

    [JsonPropertyName("userId")]
    public string UserId { get; set; } = "";

    [JsonPropertyName("security")]
    public string Security { get; set; } = "reality";

    [JsonPropertyName("network")]
    public string Network { get; set; } = "tcp";

    [JsonPropertyName("path")]
    public string? Path { get; set; }

    [JsonPropertyName("host")]
    public string? Host { get; set; }

    [JsonPropertyName("sni")]
    public string? Sni { get; set; }

    [JsonPropertyName("publicKey")]
    public string? PublicKey { get; set; }

    [JsonPropertyName("shortId")]
    public string? ShortId { get; set; }

    [JsonPropertyName("flow")]
    public string? Flow { get; set; }

    [JsonPropertyName("enableFragment")]
    public bool? EnableFragment { get; set; }

    [JsonPropertyName("isActive")]
    public bool IsActive { get; set; }

    public V2RayConfigEntry Clone() => new()
    {
        Id = Id,
        Name = Name,
        Protocol = Protocol,
        Address = Address,
        Port = Port,
        UserId = UserId,
        Security = Security,
        Network = Network,
        Path = Path,
        Host = Host,
        Sni = Sni,
        PublicKey = PublicKey,
        ShortId = ShortId,
        Flow = Flow,
        EnableFragment = EnableFragment,
        IsActive = IsActive,
    };
}

public sealed class SplitTunnelEntry
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "domain";

    [JsonPropertyName("value")]
    public string Value { get; set; } = "";
}
