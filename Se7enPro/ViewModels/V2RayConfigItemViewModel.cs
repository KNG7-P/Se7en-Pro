using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Se7enPro.Models;

namespace Se7enPro.ViewModels;

public sealed partial class V2RayConfigItemViewModel : ObservableObject
{
    public V2RayConfigEntry Entry { get; }

    public string Id => Entry.Id;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Subtitle))]
    [NotifyPropertyChangedFor(nameof(ShareLink))]
    private string _name = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Subtitle))]
    [NotifyPropertyChangedFor(nameof(ProtocolBadge))]
    [NotifyPropertyChangedFor(nameof(ProtocolColor))]
    [NotifyPropertyChangedFor(nameof(ProtocolBgColor))]
    [NotifyPropertyChangedFor(nameof(ShareLink))]
    private string _protocol = "vless";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Subtitle))]
    [NotifyPropertyChangedFor(nameof(AddressPort))]
    [NotifyPropertyChangedFor(nameof(ShareLink))]
    private string _address = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Subtitle))]
    [NotifyPropertyChangedFor(nameof(AddressPort))]
    [NotifyPropertyChangedFor(nameof(ShareLink))]
    private int _port = 443;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SecurityBadge))]
    [NotifyPropertyChangedFor(nameof(HasSecurity))]
    private string _security = "reality";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NetworkBadge))]
    private string _network = "tcp";

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LatencyDisplay))]
    [NotifyPropertyChangedFor(nameof(LatencyColor))]
    [NotifyPropertyChangedFor(nameof(LatencyBgColor))]
    private int _latency = -1;

    [ObservableProperty]
    private bool _isTesting;

    public V2RayConfigItemViewModel(V2RayConfigEntry entry)
    {
        Entry = entry;
        _name = entry.Name;
        _protocol = entry.Protocol;
        _address = entry.Address;
        _port = entry.Port;
        _security = entry.Security;
        _network = entry.Network;
        _isActive = entry.IsActive;
    }

    public string Subtitle => $"{Protocol.ToUpperInvariant()} • {Address}:{Port}";
    public string AddressPort => $"{Address}:{Port}";

    public string ProtocolBadge => Protocol.ToUpperInvariant();

    public string ProtocolColor => Protocol.ToLowerInvariant() switch
    {
        "vless" => "#06B6D4",
        "vmess" => "#A855F7",
        "trojan" => "#F59E0B",
        "shadowsocks" or "ss" => "#22C55E",
        "hysteria2" or "hy2" => "#EC4899",
        _ => "#14B8A6"
    };

    public string ProtocolBgColor => Protocol.ToLowerInvariant() switch
    {
        "vless" => "#2606B6D4",
        "vmess" => "#26A855F7",
        "trojan" => "#26F59E0B",
        "shadowsocks" or "ss" => "#2622C55E",
        "hysteria2" or "hy2" => "#26EC4899",
        _ => "#2614B8A6"
    };

    public string SecurityBadge => !string.IsNullOrEmpty(Security) && !Security.Equals("none", StringComparison.OrdinalIgnoreCase)
        ? Security.ToUpperInvariant() : "";
    public bool HasSecurity => !string.IsNullOrEmpty(SecurityBadge);

    public string NetworkBadge => !string.IsNullOrEmpty(Network) ? Network.ToUpperInvariant() : "TCP";

    public string LatencyDisplay => Latency switch
    {
        -2 => "Testing...",
        -3 => "Timeout",
        >= 0 => $"{Latency} ms",
        _ => "Test"
    };

    public string LatencyColor => Latency switch
    {
        -2 => "#06B6D4",
        -3 => "#EF4444",
        >= 0 and < 250 => "#22C55E",
        >= 250 and < 600 => "#F59E0B",
        >= 600 => "#EF4444",
        _ => "#94A3B8"
    };

    public string LatencyBgColor => Latency switch
    {
        -2 => "#2006B6D4",
        -3 => "#20EF4444",
        >= 0 and < 250 => "#2022C55E",
        >= 250 and < 600 => "#20F59E0B",
        >= 600 => "#20EF4444",
        _ => "#2094A3B8"
    };

    public string ShareLink
    {
        get
        {
            var encName = Uri.EscapeDataString(Name);
            return $"{Protocol}://{Entry.UserId}@{Address}:{Port}#{encName}";
        }
    }

    public void SyncToEntry()
    {
        Entry.Name = Name;
        Entry.Protocol = Protocol;
        Entry.Address = Address;
        Entry.Port = Port;
        Entry.Security = Security;
        Entry.Network = Network;
        Entry.IsActive = IsActive;
    }
}
