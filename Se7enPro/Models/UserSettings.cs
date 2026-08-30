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

    [JsonPropertyName("localSocksProxyPort")]
    public int LocalSocksProxyPort { get; set; }

    [JsonPropertyName("localHttpProxyPort")]
    public int LocalHttpProxyPort { get; set; }

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

    [JsonPropertyName("autoFindIpAndSni")]
    public bool AutoFindIpAndSni { get; set; }

    [JsonPropertyName("saveFoundIpsAndSni")]
    public bool SaveFoundIpsAndSni { get; set; } = false;

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
    public string ConnectionMethod { get; set; } = "psiphon";

    [JsonPropertyName("aetherScanMode")]
    public string AetherScanMode { get; set; } = "balanced";

    [JsonPropertyName("aetherNoize")]
    public string AetherNoize { get; set; } = "balanced";

    [JsonPropertyName("aetherIpVersion")]
    public string AetherIpVersion { get; set; } = "4";

    [JsonPropertyName("aetherManualPeer")]
    public string AetherManualPeer { get; set; } = "";

    [JsonPropertyName("chainedOuterTransport")]
    public string ChainedOuterTransport { get; set; } = "auto";

    [JsonPropertyName("aetherEch")]
    public bool AetherEch { get; set; }

    [JsonPropertyName("aetherMasqueTransport")]
    public string AetherMasqueTransport { get; set; } = "h3";

    [JsonPropertyName("aetherFragment")]
    public bool AetherFragment { get; set; } = false;

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
}

public sealed class SplitTunnelEntry
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "domain";

    [JsonPropertyName("value")]
    public string Value { get; set; } = "";
}
