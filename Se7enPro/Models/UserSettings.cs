using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace Se7enPro.Models;

public sealed class UserSettings
{
    [JsonIgnore]
    public object SyncRoot { get; } = new();

    [JsonPropertyName("theme")]
    public string Theme { get; set; } = "dark";

    [JsonPropertyName("language")]
    public string Language { get; set; } = "en";

    [JsonPropertyName("visualEffects")]
    public string VisualEffects { get; set; } = "auto";

    [JsonPropertyName("egressRegion")]
    public string EgressRegion { get; set; } = "auto";

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
    public bool MinimizeToTray { get; set; }

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

    [JsonPropertyName("tunMethod")]
    public string TunMethod { get; set; } = "wintun";

    [JsonPropertyName("killSwitchEnabled")]
    public bool KillSwitchEnabled { get; set; }

    [JsonPropertyName("autoReconnect")]
    public bool AutoReconnect { get; set; } = true;

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

    
    
    [JsonPropertyName("lanternRegion")]
    public string LanternRegion { get; set; } = "auto";

    [JsonPropertyName("veepRegion")]
    public string VeepRegion { get; set; } = "auto";

    [JsonPropertyName("aetherIdentityAcquisitionMethod")]
    public string AetherIdentityAcquisitionMethod { get; set; } = "lantern";

    [JsonPropertyName("aetherProtocol")]
    public string AetherProtocol { get; set; } = "masque";

    
    [JsonPropertyName("aetherScanMode")]
    public string AetherScanMode { get; set; } = "turbo";

    [JsonPropertyName("aetherScanModeMasque")]
    public string AetherScanModeMasque { get; set; } = "turbo";

    [JsonPropertyName("aetherScanModeMim")]
    public string AetherScanModeMim { get; set; } = "turbo";

    [JsonPropertyName("aetherScanModeWireguard")]
    public string AetherScanModeWireguard { get; set; } = "turbo";

    [JsonPropertyName("aetherScanModeWarp")]
    public string AetherScanModeWarp { get; set; } = "turbo";

    
    
    [JsonPropertyName("aetherNoize")]
    public string AetherNoize { get; set; } = "balanced";

    [JsonPropertyName("aetherNoizeMasque")]
    public string AetherNoizeMasque { get; set; } = "firewall";

    [JsonPropertyName("aetherNoizeMim")]
    public string AetherNoizeMim { get; set; } = "firewall";

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

    [JsonPropertyName("aetherGoolMode")]
    public string AetherGoolMode { get; set; } = "masque";


    [JsonPropertyName("aetherTlsVerify")]
    public bool AetherTlsVerify { get; set; } = false;


    [JsonPropertyName("aetherTlsCiphers")]
    public string AetherTlsCiphers { get; set; } = "";

    [JsonPropertyName("aetherTlsGroups")]
    public string AetherTlsGroups { get; set; } = "";

    [JsonPropertyName("aetherDisableGrease")]
    public bool AetherDisableGrease { get; set; } = false;

    [JsonPropertyName("aetherTlsSigalgs")]
    public string AetherTlsSigalgs { get; set; } = "";

    [JsonPropertyName("aetherTlsAlps")]
    public string AetherTlsAlps { get; set; } = "";

    [JsonPropertyName("aetherTlsFingerprint")]
    public string AetherTlsFingerprint { get; set; } = "";

    [JsonPropertyName("aetherTls13Ciphers")]
    public string AetherTls13Ciphers { get; set; } = "";

    [JsonPropertyName("aetherTlsKeyShares")]
    public string AetherTlsKeyShares { get; set; } = "";

    [JsonPropertyName("aetherDisableOcspStapling")]
    public bool AetherDisableOcspStapling { get; set; } = false;

    [JsonPropertyName("aetherDisableSct")]
    public bool AetherDisableSct { get; set; } = false;

    [JsonPropertyName("aetherDisableSessionTicket")]
    public bool AetherDisableSessionTicket { get; set; } = false;

    [JsonPropertyName("aetherDisableExtensionPermutation")]
    public bool AetherDisableExtensionPermutation { get; set; } = false;

    [JsonPropertyName("aetherEnableEchGrease")]
    public bool AetherEnableEchGrease { get; set; } = false;

    [JsonPropertyName("aetherMasqueSni")]
    public string AetherMasqueSni { get; set; } = "";

    [JsonPropertyName("aetherUpstreamProxy")]
    public string AetherUpstreamProxy { get; set; } = "";

    [JsonPropertyName("aetherEchDomain")]
    public string AetherEchDomain { get; set; } = "";

    [JsonPropertyName("aetherEchDns")]
    public string AetherEchDns { get; set; } = "";

    [JsonPropertyName("aetherTor")]
    public bool AetherTor { get; set; } = false;

    [JsonPropertyName("aetherTorRelays")]
    public bool AetherTorRelays { get; set; } = false;

    [JsonPropertyName("aetherFragmentWireguard")]
    public bool AetherFragmentWireguard { get; set; } = false;

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

    [JsonPropertyName("chainedFoxyOuterTransport")]
    public string ChainedFoxyOuterTransport { get; set; } = "auto";

    [JsonPropertyName("chainedLanternOuterTransport")]
    public string ChainedLanternOuterTransport { get; set; } = "auto";

    [JsonPropertyName("chainedVeepOuterTransport")]
    public string ChainedVeepOuterTransport { get; set; } = "auto";

    
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
    private string _aetherMasqueTransport = "h2";

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
    private bool _aetherMasqueQuic = false;

    
    [JsonPropertyName("aetherFragment")]
    public bool AetherFragment { get; set; } = false;

    
    [JsonPropertyName("aetherFragmentSize")]
    public string AetherFragmentSize { get; set; } = "16-32";

    [JsonPropertyName("aetherFragmentDelay")]
    public string AetherFragmentDelay { get; set; } = "2-10";

    
    [JsonPropertyName("torExitCountry")]
    public string TorExitCountry { get; set; } = "auto";

    
    
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

    [JsonPropertyName("shardMtu")]
    public int ShardMtu { get; set; } = 1280;

    [JsonPropertyName("shardAutoScanMtu")]
    public bool ShardAutoScanMtu { get; set; } = true;

    [JsonPropertyName("shadowsocksMethod")]
    public string ShadowsocksMethod { get; set; } = "2022-blake3-aes-128-gcm";

    

    [JsonPropertyName("firefoxAccounts")]
    public ObservableCollection<FirefoxAccountInfo> FirefoxAccounts { get; set; } = new();

    [JsonPropertyName("firefoxActiveAccountId")]
    public string FirefoxActiveAccountId { get; set; } = "";

    [JsonPropertyName("firefoxServerCountry")]
    public string FirefoxServerCountry { get; set; } = "REC";

    [JsonPropertyName("firefoxServerCity")]
    public string FirefoxServerCity { get; set; } = "";

    [JsonPropertyName("firefoxLastEdgeHost")]
    public string FirefoxLastEdgeHost { get; set; } = "";

    [JsonPropertyName("firefoxLastEdgePort")]
    public int FirefoxLastEdgePort { get; set; }

    [JsonPropertyName("firefoxCustomProxyEnabled")]
    public bool FirefoxCustomProxyEnabled { get; set; }

    [JsonPropertyName("firefoxCustomProxyType")]
    public string FirefoxCustomProxyType { get; set; } = "socks5";

    [JsonPropertyName("firefoxCustomProxyHost")]
    public string FirefoxCustomProxyHost { get; set; } = "";

    [JsonPropertyName("firefoxCustomProxyPort")]
    public int FirefoxCustomProxyPort { get; set; } = 1080;

    [JsonPropertyName("firefoxCustomProxyUsername")]
    public string FirefoxCustomProxyUsername { get; set; } = "";

    [JsonPropertyName("firefoxCustomProxyPassword")]
    public string FirefoxCustomProxyPassword { get; set; } = "";

    
    [JsonPropertyName("v2rayCore")]
    public string V2RayCore { get; set; } = "xray";


    [JsonPropertyName("customDnsUdp")]
    public string CustomDnsUdp { get; set; } = "";

    [JsonPropertyName("customDnsDot")]
    public string CustomDnsDot { get; set; } = "";

    [JsonPropertyName("customDnsDoh")]
    public string CustomDnsDoh { get; set; } = "";

    [JsonPropertyName("customDnsStrict")]
    public bool CustomDnsStrict { get; set; } = false;

    [JsonPropertyName("identityPoolTarget")]
    public int IdentityPoolTarget { get; set; } = 3;

    [JsonPropertyName("identityRelayUrls")]
    public string IdentityRelayUrls { get; set; } = "";

    [JsonPropertyName("v2rayInboundPort")]
    public int V2RayInboundPort { get; set; } = 10808;

    [JsonPropertyName("v2rayEnableMux")]
    public bool V2RayEnableMux { get; set; } = false;

    [JsonPropertyName("v2rayRouteDnsThroughV2Ray")]
    public bool V2RayRouteDnsThroughV2Ray { get; set; } = true;

    [JsonPropertyName("v2rayEnableFragment")]
    public bool V2RayEnableFragment { get; set; } = false;

    [JsonPropertyName("v2rayFragmentPackets")]
    public string V2RayFragmentPackets { get; set; } = "tlshello";

    [JsonPropertyName("v2rayFragmentLength")]
    public string V2RayFragmentLength { get; set; } = "100-200";

    [JsonPropertyName("v2rayFragmentInterval")]
    public string V2RayFragmentInterval { get; set; } = "10-20";

    [JsonPropertyName("singBoxInboundPort")]
    public int SingBoxInboundPort { get; set; } = 10808;

    [JsonPropertyName("singBoxRouteDnsThroughCore")]
    public bool SingBoxRouteDnsThroughCore { get; set; } = true;

    [JsonPropertyName("singBoxEnableMux")]
    public bool SingBoxEnableMux { get; set; } = false;

    [JsonPropertyName("singBoxMuxProtocol")]
    public string SingBoxMuxProtocol { get; set; } = "h2mux";

    [JsonPropertyName("singBoxMuxPadding")]
    public bool SingBoxMuxPadding { get; set; } = false;

    [JsonPropertyName("singBoxEnableFragment")]
    public bool SingBoxEnableFragment { get; set; } = false;

    [JsonPropertyName("singBoxFragmentFallbackDelay")]
    public string SingBoxFragmentFallbackDelay { get; set; } = "500ms";

    [JsonPropertyName("v2rayActiveConfigId")]
    public string V2RayActiveConfigId { get; set; } = "";

    private List<V2RayConfigEntry> _v2rayConfigs = new();
    [JsonPropertyName("v2rayConfigs")]
    public List<V2RayConfigEntry> V2RayConfigs
    {
        get => _v2rayConfigs ??= new();
        set => _v2rayConfigs = value ?? new();
    }

    [JsonPropertyName("settingsSchemaVersion")]
    public int SettingsSchemaVersion { get; set; }

    [JsonPropertyName("aetherMtuMasque")]
    public int AetherMtuMasque { get; set; } = 1280;

    [JsonPropertyName("aetherMtuWireguard")]
    public int AetherMtuWireguard { get; set; } = 1280;

    [JsonPropertyName("aetherMtuWarp")]
    public int AetherMtuWarp { get; set; } = 1280;

    [JsonPropertyName("aetherMtuMim")]
    public int AetherMtuMim { get; set; } = 1280;

    [JsonPropertyName("aetherMtuAutoLadder")]
    public bool AetherMtuAutoLadder { get; set; } = true;

    [JsonPropertyName("aetherGoolPeer")]
    public string AetherGoolPeer { get; set; } = "";

    public int GetAetherMtu(Se7enPro.Services.ConnectionMethod method)
    {
        var configured = method switch
        {
            Se7enPro.Services.ConnectionMethod.WireGuard => AetherMtuWireguard,
            Se7enPro.Services.ConnectionMethod.WarpOnWarp => AetherMtuWarp,
            Se7enPro.Services.ConnectionMethod.MasqueInMasque => AetherMtuMim,
            _ => AetherMtuMasque,
        };
        if (configured <= 0) return 1280;
        return System.Math.Clamp(configured, 576, 1380);
    }

    public void SetAetherMtu(Se7enPro.Services.ConnectionMethod method, int value)
    {
        value = System.Math.Clamp(value, 576, 1380);
        switch (method)
        {
            case Se7enPro.Services.ConnectionMethod.WireGuard:
                AetherMtuWireguard = value;
                break;
            case Se7enPro.Services.ConnectionMethod.WarpOnWarp:
                AetherMtuWarp = value;
                break;
            case Se7enPro.Services.ConnectionMethod.MasqueInMasque:
                AetherMtuMim = value;
                break;
            default:
                AetherMtuMasque = value;
                break;
        }
    }

    public void ApplyAetherPreset(bool fast)
    {
        var scan = fast ? "turbo" : "balanced";
        var masqueNoize = fast ? "firewall" : "balanced";
        AetherScanMode = scan;
        AetherScanModeMasque = scan;
        AetherScanModeMim = scan;
        AetherScanModeWireguard = scan;
        AetherScanModeWarp = scan;
        AetherNoize = "balanced";
        AetherNoizeMasque = masqueNoize;
        AetherNoizeMim = masqueNoize;
        AetherNoizeWireguard = "balanced";
        AetherNoizeWarp = "balanced";
        AetherFragment = !fast;
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
