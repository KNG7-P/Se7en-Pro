using Se7enPro.Models;
using Se7enPro.Services;
using Se7enPro.ViewModels;

namespace VerifyCriticalFixes;

internal static class V2RayShareLinkTests
{
    internal static void Run()
    {
        Program.Section("V2Ray share links — protocol round trips");

        RoundTrip(new V2RayConfigEntry
        {
            Name = "Reality / تست",
            Protocol = "vless",
            Address = "vless.example.com",
            Port = 443,
            UserId = "11111111-2222-3333-4444-555555555555",
            Security = "reality",
            Network = "ws",
            Path = "/socket path?x=1",
            Host = "ws.example.com",
            Sni = "sni.example.com",
            PublicKey = "public-key",
            ShortId = "ab12",
            Flow = "xtls-rprx-vision",
        }, "VLESS Reality/WebSocket");

        RoundTrip(new V2RayConfigEntry
        {
            Name = "VMess gRPC",
            Protocol = "vmess",
            Address = "vmess.example.com",
            Port = 8443,
            UserId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
            Security = "tls",
            Network = "grpc",
            Path = "service name",
            Host = "vmess.example.com",
            Sni = "vmess.example.com",
        }, "VMess TLS/gRPC");

        RoundTrip(new V2RayConfigEntry
        {
            Name = "Trojan node",
            Protocol = "trojan",
            Address = "trojan.example.com",
            Port = 443,
            UserId = "p@ss:word/?",
            Security = "tls",
            Network = "ws",
            Path = "/trojan",
            Host = "trojan.example.com",
            Sni = "trojan.example.com",
        }, "Trojan TLS/WebSocket");

        RoundTrip(new V2RayConfigEntry
        {
            Name = "Shadowsocks node",
            Protocol = "shadowsocks",
            Address = "ss.example.com",
            Port = 8388,
            UserId = "p:ss word",
            Security = "aes-256-gcm",
            Network = "tcp",
        }, "Shadowsocks SIP002");

        RoundTrip(new V2RayConfigEntry
        {
            Name = "Hysteria2 node",
            Protocol = "hysteria2",
            Address = "hy2.example.com",
            Port = 443,
            UserId = "hy2 password",
            Security = "tls",
            Network = "udp",
            Host = "hy2.example.com",
            Sni = "hy2.example.com",
        }, "Hysteria2 TLS");

        var unsupported = new V2RayConfigEntry { Protocol = "unknown" };
        Program.Check(V2RayLinkParser.ToShareLink(unsupported).Length == 0,
            "an unknown protocol does not produce a misleading generic link");
    }

    private static void RoundTrip(V2RayConfigEntry source, string label)
    {
        var item = new V2RayConfigItemViewModel(source.Clone());
        var link = item.ShareLink;
        var parsed = V2RayLinkParser.ParseSingleLink(link);
        Program.Check(parsed is not null, $"{label}: exported link parses", link);
        if (parsed is null) return;

        CheckEqual(source.Protocol, parsed.Protocol, label, "protocol");
        CheckEqual(source.Name, parsed.Name, label, "name");
        CheckEqual(source.Address, parsed.Address, label, "address");
        CheckEqual(source.Port, parsed.Port, label, "port");
        CheckEqual(source.UserId, parsed.UserId, label, "user ID/password");
        CheckEqual(source.Security, parsed.Security, label, "security/method");
        CheckEqual(source.Network, parsed.Network, label, "network");
        CheckEqual(source.Path, parsed.Path, label, "path/service name");
        CheckEqual(source.Host, parsed.Host, label, "host");
        CheckEqual(source.Sni, parsed.Sni, label, "SNI");
        CheckEqual(source.PublicKey, parsed.PublicKey, label, "Reality public key");
        CheckEqual(source.ShortId, parsed.ShortId, label, "Reality short ID");
        CheckEqual(source.Flow, parsed.Flow, label, "flow");
    }

    private static void CheckEqual<T>(T expected, T actual, string label, string field) =>
        Program.Check(EqualityComparer<T>.Default.Equals(expected, actual),
            $"{label}: {field} survives export/import", $"expected={expected}; actual={actual}");
}
