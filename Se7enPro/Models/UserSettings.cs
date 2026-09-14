using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Se7enPro.Models;

public sealed class UserSettings
{
    [JsonPropertyName("theme")]
    public string Theme { get; set; } = "dark";

    [JsonPropertyName("language")]
    public string Language { get; set; } = "en";

    [JsonPropertyName("egressRegion")]
    public string EgressRegion { get; set; } = "";

    [JsonPropertyName("disableTimeouts")]
    public bool DisableTimeouts { get; set; }

    public const int MinPort = 1;
    public const int MaxPort = 65535;

    public static int SanitizePort(int port, int fallback = 0)
        => port is >= MinPort and <= MaxPort ? port : fallback;

    private int _localSocksProxyPort;
    [JsonPropertyName("localSocksProxyPort")]
    public int LocalSocksProxyPort
    {
        get => _localSocksProxyPort;
        set => _localSocksProxyPort = SanitizePort(value);
    }

    private int _localHttpProxyPort;
    [JsonPropertyName("localHttpProxyPort")]
    public int LocalHttpProxyPort
    {
        get => _localHttpProxyPort;
        set => _localHttpProxyPort = SanitizePort(value);
    }

    [JsonPropertyName("useCustomProxyPorts")]
    public bool UseCustomProxyPorts { get; set; }

    [JsonPropertyName("allowLanConnections")]
    public bool AllowLanConnections { get; set; }

    [JsonPropertyName("setSystemProxy")]
    public bool SetSystemProxy { get; set; } = true;

    [JsonPropertyName("autoConnect")]
    public bool AutoConnect { get; set; }

    [JsonPropertyName("startWithWindows")]
    public bool StartWithWindows { get; set; }

    [JsonPropertyName("ipScannerEnabled")]
    public bool IpScannerEnabled { get; set; }

    [JsonPropertyName("minimizeToTray")]
    public bool MinimizeToTray { get; set; } = true;

    [JsonPropertyName("onCloseAction")]
    public string OnCloseAction { get; set; } = "ask";

    [JsonPropertyName("upstreamProxy")]
    public string UpstreamProxy { get; set; } = "";

    [JsonPropertyName("upstreamProxyEnabled")]
    public bool UpstreamProxyEnabled { get; set; } = false;

    [JsonPropertyName("upstreamProxyScheme")]
    public string UpstreamProxyScheme { get; set; } = "http";

    [JsonPropertyName("upstreamProxyUsername")]
    public string UpstreamProxyUsername { get; set; } = "";

    [JsonPropertyName("upstreamProxyPassword")]
    [System.Text.Json.Serialization.JsonConverter(typeof(Services.EncryptedStringConverter))]
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

    [JsonPropertyName("frontedMeekCDNScanBuiltInSets")]
    public List<string> FrontedMeekCDNScanBuiltInSets { get; set; } = new() { "psiphon-akamai", "fastly" };

    [JsonPropertyName("establishTunnelTimeoutSeconds")]
    public int? EstablishTunnelTimeoutSeconds { get; set; } = 300;

    [JsonPropertyName("conduitMode")]
    public string ConduitMode { get; set; } = "auto";

    [JsonPropertyName("conduitCompartmentId")]
    public string ConduitCompartmentId { get; set; } = "";

    [JsonPropertyName("conduitRejectCensoredCountries")]
    public bool ConduitRejectCensoredCountries { get; set; } = true;

    [JsonPropertyName("lanAuthEnabled")]
    public bool LanAuthEnabled { get; set; } = false;

    [JsonPropertyName("lanProxyUsername")]
    public string LanProxyUsername { get; set; } = "";

    [JsonPropertyName("lanProxyPassword")]
    [System.Text.Json.Serialization.JsonConverter(typeof(Services.EncryptedStringConverter))]
    public string LanProxyPassword { get; set; } = "";

    [JsonPropertyName("connectionMethod")]
    public string ConnectionMethod { get; set; } = "wireguard";

    [JsonPropertyName("aetherProtocol")]
    public string AetherProtocol { get; set; } = "masque";

    [JsonPropertyName("aetherEndpointMasque")]
    public string AetherEndpointMasque { get; set; } = "";

    [JsonPropertyName("aetherEndpointWireguard")]
    public string AetherEndpointWireguard { get; set; } = "";

    [JsonPropertyName("aetherEndpointWarp")]
    public string AetherEndpointWarp { get; set; } = "";

    [JsonPropertyName("aetherEndpointMasqueOnMasque")]
    public string AetherEndpointMasqueOnMasque { get; set; } = "";

    [JsonPropertyName("aetherScanMode")]
    public string AetherScanMode { get; set; } = "balanced";

    [JsonPropertyName("aetherNoize")]
    public string AetherNoize { get; set; } = "balanced";

    [JsonPropertyName("aetherIpVersion")]
    public string AetherIpVersion { get; set; } = "4";

    [JsonPropertyName("aetherPerfProfile")]
    public string AetherPerfProfile { get; set; } = "auto";

    [JsonPropertyName("aetherManualPeer")]
    public string AetherManualPeer { get; set; } = "";

    [JsonPropertyName("chainedOuterTransport")]
    public string ChainedOuterTransport { get; set; } = "auto";

    [JsonPropertyName("chainedPsiphonOuterTransport")]
    public string ChainedPsiphonOuterTransport { get; set; } = "auto";

    [JsonPropertyName("chainedTorOuterTransport")]
    public string ChainedTorOuterTransport { get; set; } = "auto";

    [JsonPropertyName("chainedSubMode")]
    public string ChainedSubMode { get; set; } = "psiphon_warp";

    [JsonPropertyName("shadowsocksMethod")]
    public string ShadowsocksMethod { get; set; } = "2022-blake3-aes-128-gcm";

    [JsonPropertyName("aetherEch")]
    public bool AetherEch { get; set; }

    [JsonPropertyName("aetherMasqueTransport")]
    public string AetherMasqueTransport { get; set; } = "h3";

    [JsonPropertyName("aetherFragment")]
    public bool AetherFragment { get; set; } = false;

    [JsonPropertyName("aetherMimEnabled")]
    public bool AetherMimEnabled { get; set; } = false;

    [JsonPropertyName("aetherMimOuter")]
    public string AetherMimOuter { get; set; } = "";

    [JsonPropertyName("aetherMimInner")]
    public string AetherMimInner { get; set; } = "";

    [JsonPropertyName("aetherMimPeers")]
    public string AetherMimPeers { get; set; } = "";

    [JsonPropertyName("aetherQuicV2")]
    public bool AetherQuicV2 { get; set; } = true;

    [JsonPropertyName("aetherMark")]
    public string AetherMark { get; set; } = "";

    [JsonPropertyName("torExitCountry")]
    public string TorExitCountry { get; set; } = "";

    [JsonPropertyName("torBridges")]
    public string TorBridges { get; set; } = "";

    [JsonPropertyName("splitTunnelEnabled")]
    public bool SplitTunnelEnabled { get; set; }

    [JsonPropertyName("splitTunnelMode")]
    public string SplitTunnelMode { get; set; } = "exclude";

    private List<SplitTunnelEntry> _splitTunnelEntries = new();
    [JsonPropertyName("splitTunnelEntries")]
    public List<SplitTunnelEntry> SplitTunnelEntries
    {
        get => _splitTunnelEntries ??= new();
        set => _splitTunnelEntries = value ?? new();
    }

    [JsonPropertyName("v2rayCore")]
    public string V2RayCore { get; set; } = "xray";

    private int _v2rayInboundPort = 10808;
    [JsonPropertyName("v2rayInboundPort")]
    public int V2RayInboundPort
    {
        get => _v2rayInboundPort;
        set => _v2rayInboundPort = SanitizePort(value, 10808);
    }

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

    [JsonPropertyName("shardCustomCfIp")]
    public string ShardCustomCfIp { get; set; } = "";

    [JsonPropertyName("shardSmartSplit")]
    public bool ShardSmartSplit { get; set; } = false;

    [JsonPropertyName("shardRotateIp")]
    public bool ShardRotateIp { get; set; } = true;
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

    [JsonPropertyName("latency")]
    public int Latency { get; set; } = -1;
}

public sealed class SplitTunnelEntry
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "domain";

    [JsonPropertyName("value")]
    public string Value { get; set; } = "";
}
