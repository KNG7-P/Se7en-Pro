using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Windows;
using Microsoft.Extensions.Logging.Abstractions;
using Se7enPro.Models;
using Se7enPro.Services;
using Se7enPro.Views;

namespace VerifyCriticalFixes;

internal static class MihomoSingBoxModalTests
{
    private static void Check(bool ok, string what, string? detail = null) => Program.Check(ok, what, detail);

    internal static int Run()
    {
        var before = Program.FailedCount;
        TestMihomoConfig();
        TestDnsResolverOrdering();
        TestV2RayCoreSelectionAndSeparation();
        TestModalCenteringAndClamping();
        TestShardMtuAndNavCursor();
        TestPsiphonServerListAndAetherChainedBucket();
        TestStabilityAndAetherModeUi();
        return Program.FailedCount - before;
    }

    private static void TestMihomoConfig()
    {
        Program.Section("Mihomo UDP / IPv6 / Reject Rule Generation");

        Check(!MihomoConfigBuilder.SupportsSocksUdp(ConnectionMethod.Psiphon), "Psiphon SOCKS5 is TCP-only (udp: false)");
        Check(!MihomoConfigBuilder.SupportsSocksUdp(ConnectionMethod.PsiphonOverV2Ray), "PsiphonOverV2Ray SOCKS5 is TCP-only (udp: false)");
        Check(!MihomoConfigBuilder.SupportsSocksUdp(ConnectionMethod.TorOverV2Ray), "TorOverV2Ray SOCKS5 is TCP-only (udp: false)");
        Check(!MihomoConfigBuilder.SupportsSocksUdp(ConnectionMethod.FoxyOverV2Ray), "FoxyOverV2Ray SOCKS5 is TCP-only (udp: false)");
        Check(MihomoConfigBuilder.SupportsSocksUdp(ConnectionMethod.WireGuard), "WireGuard Aether supports UDP");
        Check(MihomoConfigBuilder.SupportsSocksUdp(ConnectionMethod.Shard), "Shard Xray supports UDP");

        var settings = new UserSettings();
        var yamlNoSplit = MihomoConfigBuilder.BuildYaml(
            10808, enableV6: false, ConnectionMethod.WireGuard,
            1280, "se7en_tun", "198.18.0.1", 30, "fdfe:dcba:9876::1", 126,
            settings, "Ethernet", new[] { "aether" });
        Check(yamlNoSplit.Contains("tcp-concurrent: true", StringComparison.Ordinal), "Mihomo YAML enables tcp-concurrent");
        Check(yamlNoSplit.Contains("keep-alive-idle: 60", StringComparison.Ordinal), "Mihomo YAML sets keep-alive-idle");
        Check(yamlNoSplit.Contains("enable: false\r\nrules:", StringComparison.Ordinal)
              || yamlNoSplit.Contains("enable: false\nrules:", StringComparison.Ordinal),
              "Mihomo YAML disables packet sniffer when no split-domain rules exist");

        var splitSettings = new UserSettings { SplitTunnelEnabled = true };
        var splitDomains = new[] { "example.com" };
        var yamlTcpOnly = MihomoConfigBuilder.BuildYaml(
            10808, enableV6: false, ConnectionMethod.PsiphonOverV2Ray,
            1280, "se7en_tun", "198.18.0.1", 30, "fdfe:dcba:9876::1", 126,
            splitSettings, "Ethernet", new[] { "xray", "sing-box" }, splitDomains: splitDomains);
        Check(yamlTcpOnly.Contains("ipv6: false", StringComparison.Ordinal), "Mihomo YAML disables top-level IPv6 when IPv6 is off");
        Check(yamlTcpOnly.Contains("udp: false", StringComparison.Ordinal), "Mihomo YAML disables SOCKS5 UDP for PsiphonOverV2Ray");
        Check(yamlTcpOnly.Contains("AND,((NETWORK,udp),(NOT,((DST-PORT,53)))),REJECT", StringComparison.Ordinal), "Mihomo YAML rejects non-DNS UDP cleanly for TCP-only proxies");
        Check(!yamlTcpOnly.Contains("inet6-address:", StringComparison.Ordinal), "Mihomo YAML omits inet6-address when enableV6 is false");
        Check(!yamlTcpOnly.Contains("QUIC:", StringComparison.Ordinal), "Mihomo YAML omits QUIC sniffer when UDP is disabled");

        var yamlUdp = MihomoConfigBuilder.BuildYaml(
            10808, enableV6: true, ConnectionMethod.WireGuard,
            1280, "se7en_tun", "198.18.0.1", 30, "fdfe:dcba:9876::1", 126,
            splitSettings, "Ethernet", new[] { "aether" }, splitDomains: splitDomains);
        Check(yamlUdp.Contains("ipv6: true", StringComparison.Ordinal), "Mihomo YAML enables IPv6 when enableV6 is true");
        Check(yamlUdp.Contains("udp: true", StringComparison.Ordinal), "Mihomo YAML enables SOCKS5 UDP for WireGuard");
        Check(yamlUdp.Contains("QUIC:", StringComparison.Ordinal), "Mihomo YAML keeps QUIC sniffer when UDP and split-domains are active");
        Check(!yamlUdp.Contains("REJECT", StringComparison.Ordinal), "Mihomo YAML does not reject UDP when proxy supports UDP");

        var mihomoExe = FindBundledBinary("mihomo", "mihomo.exe");
        Check(mihomoExe is not null, "Bundled mihomo.exe located for CLI validation");
        if (mihomoExe is not null)
        {
            var mihomoDir = Path.GetDirectoryName(mihomoExe)!;
            var (okNoSplit, outNoSplit) = ValidateWithExternalCli(mihomoExe, $"-d \"{mihomoDir}\" -t -f \"{{0}}\"", yamlNoSplit, ".yaml");
            Check(okNoSplit, "mihomo.exe -t validates no-split YAML", outNoSplit);
            var (okTcp, outTcp) = ValidateWithExternalCli(mihomoExe, $"-d \"{mihomoDir}\" -t -f \"{{0}}\"", yamlTcpOnly, ".yaml");
            Check(okTcp, "mihomo.exe -t validates TCP-only YAML", outTcp);
            var (okUdp, outUdp) = ValidateWithExternalCli(mihomoExe, $"-d \"{mihomoDir}\" -t -f \"{{0}}\"", yamlUdp, ".yaml");
            Check(okUdp, "mihomo.exe -t validates UDP-enabled YAML", outUdp);
        }
    }

    private static void TestDnsResolverOrdering()
    {
        Program.Section("SocksDnsForwarder Health Ordering & Promotion");

        var u1 = new DnsServerEntry(DnsTransport.Udp, "1.1.1.1", System.Net.IPAddress.Parse("1.1.1.1"), "1.1.1.1", 53);
        var u2 = new DnsServerEntry(DnsTransport.Udp, "8.8.8.8", System.Net.IPAddress.Parse("8.8.8.8"), "8.8.8.8", 53);
        var doh = new DnsServerEntry(DnsTransport.Doh, "https://dns.google/dns-query", null, "dns.google", 443);
        var targets = new List<DnsServerEntry> { u1, u2, doh };

        using var fwd = new SocksDnsForwarder(10808, relayTargets: targets);
        var now = DateTime.UtcNow.Ticks;

        fwd.MarkTargetResult(u1, succeeded: false, nowTicks: now);
        fwd.MarkTargetResult(u2, succeeded: false, nowTicks: now);
        fwd.MarkTargetResult(doh, succeeded: true, nowTicks: now);

        var ordered = fwd.OrderRelayTargets(targets, now + TimeSpan.FromSeconds(1).Ticks);
        Check(ordered.Count == 3 && ordered[0].TargetHostPort == doh.TargetHostPort,
            "Working resolver promoted to index 0 after UDP 53 failures",
            ordered.Count > 0 ? ordered[0].TargetHostPort : "empty");

        var orderedAfterCooldown = fwd.OrderRelayTargets(targets, now + TimeSpan.FromSeconds(60).Ticks);
        Check(orderedAfterCooldown[0].TargetHostPort == doh.TargetHostPort,
            "Last known healthy resolver remains preferred even after cooldown expires");
    }

    private static void TestV2RayCoreSelectionAndSeparation()
    {
        Program.Section("Xray / Sing-Box Auto-Routing, Settings Separation & Schemas");

        var xhttpNode = new V2RayConfigEntry
        {
            Name = "XHTTP Node",
            Protocol = "vless",
            Address = "example.com",
            Port = 443,
            UserId = "00000000-0000-0000-0000-000000000000",
            Network = "xhttp",
            Security = "tls",
            Sni = "example.com",
            Path = "/xhttp"
        };

        Check(V2RayConfigBuilder.ResolveCoreKind(xhttpNode, "sing_box", out var xhttpReason),
            "XHTTP config auto-switches from Sing-Box to Xray", xhttpReason);
        Check(!string.IsNullOrEmpty(xhttpReason), "XHTTP auto-switch emits diagnostic reason");

        var hy2Node = new V2RayConfigEntry
        {
            Name = "Hy2 Node",
            Protocol = "hysteria2",
            Address = "example.com",
            Port = 443,
            UserId = "password123",
            Security = "tls",
            Sni = "example.com"
        };
        Check(!V2RayConfigBuilder.ResolveCoreKind(hy2Node, "xray", out var hy2Reason),
            "Hysteria2 config auto-switches from Xray to Sing-Box", hy2Reason);

        var freshDefaults = new UserSettings();
        Check(!freshDefaults.V2RayEnableFragment && !freshDefaults.SingBoxEnableFragment,
            "Both Xray and Sing-Box default TLS fragment to false to prevent closed-pipe stalls on WS+TLS CDNs");

        var settings = new UserSettings
        {
            UseCustomProxyPorts = true,
            LocalSocksProxyPort = 10820,
            LocalHttpProxyPort = 10821,
            V2RayEnableFragment = true,
            V2RayEnableMux = false,
            V2RayRouteDnsThroughV2Ray = true,
            SingBoxEnableFragment = true,
            SingBoxFragmentFallbackDelay = "350ms",
            SingBoxEnableMux = true,
            SingBoxMuxProtocol = "smux",
            SingBoxMuxPadding = true,
            SingBoxRouteDnsThroughCore = false
        };

        var wsNode = new V2RayConfigEntry
        {
            Name = "WS TLS Node",
            Protocol = "vless",
            Address = "cloudflare.com",
            Port = 443,
            UserId = "00000000-0000-0000-0000-000000000000",
            Network = "ws",
            Security = "tls",
            Sni = "cloudflare.com",
            Host = "cloudflare.com",
            Path = "/?ed=2048"
        };

        var huNode = new V2RayConfigEntry
        {
            Name = "HTTPUpgrade Node",
            Protocol = "vless",
            Address = "example.com",
            Port = 443,
            UserId = "00000000-0000-0000-0000-000000000000",
            Network = "httpupgrade",
            Security = "tls",
            Sni = "example.com",
            Host = "cdn.example.com",
            Path = "/hu"
        };

        var policy = new DnsResolverPolicy(NullLogger.Instance);
        var dnsPlan = policy.Build(settings, hasV6Address: false);

        var chainedXrayJson = V2RayConfigBuilder.BuildCoreConfigJson(
            settings, xhttpNode, isXray: true, isChainedOuter: true, socksPort: 18181, httpPort: 18182, dnsPlan: dnsPlan);
        var chainedSbJson = V2RayConfigBuilder.BuildCoreConfigJson(
            settings, huNode, isXray: false, isChainedOuter: true, socksPort: 18181, httpPort: 18182, dnsPlan: dnsPlan);

        using (var doc = JsonDocument.Parse(chainedXrayJson))
        {
            var httpIn = doc.RootElement.GetProperty("inbounds")[1];
            Check(httpIn.GetProperty("sniffing").GetProperty("enabled").GetBoolean() == false,
                "Chained outer Xray http-in disables sniffing rewrite for Psiphon/Tor/Foxy");
            Check(httpIn.GetProperty("sniffing").GetProperty("routeOnly").GetBoolean(),
                "Xray http-in uses routeOnly=true");
        }

        using (var doc = JsonDocument.Parse(chainedSbJson))
        {
            var rules = doc.RootElement.GetProperty("route").GetProperty("rules");
            Check(rules.GetArrayLength() == 0,
                "Chained outer Sing-Box leaves route.rules empty (no sniff interference)");
        }

        var xrayPort = V2RayConfigBuilder.GetCoreInboundPort(settings, isXray: true);
        var sbPort = V2RayConfigBuilder.GetCoreInboundPort(settings, isXray: false);
        Check(xrayPort == 10820 && sbPort == 10820,
            "Both Xray and Sing-Box unify inbound port selection with Network tab LocalSocksProxyPort");
        Check(V2RayConfigBuilder.GetCoreInboundPort(freshDefaults, isXray: true) == 10808
            && V2RayConfigBuilder.GetCoreInboundPort(freshDefaults, isXray: false) == 10808,
            "Default GetCoreInboundPort returns 10808 when UseCustomProxyPorts is off");

        var directXrayJson = V2RayConfigBuilder.BuildCoreConfigJson(
            settings, huNode, isXray: true, isChainedOuter: false, socksPort: xrayPort, httpPort: xrayPort + 1, dnsPlan: dnsPlan);
        var directSbJson = V2RayConfigBuilder.BuildCoreConfigJson(
            settings, huNode, isXray: false, isChainedOuter: false, socksPort: 20820, httpPort: 20821, dnsPlan: dnsPlan);
        var wsXrayDefaultJson = V2RayConfigBuilder.BuildCoreConfigJson(
            freshDefaults, wsNode, isXray: true, isChainedOuter: false, socksPort: 10808, httpPort: 10809, dnsPlan: dnsPlan);

        Check(directXrayJson.Contains("\"port\": 10820", StringComparison.Ordinal), "Xray uses unified Network tab SOCKS port (10820)");
        Check(directXrayJson.Contains("\"httpupgradeSettings\"", StringComparison.Ordinal), "Xray emits httpupgradeSettings");
        Check(!directXrayJson.Contains("\"mux\"", StringComparison.Ordinal), "Xray keeps V2RayEnableMux=false while SingBoxEnableMux=true");
        Check(wsXrayDefaultJson.Contains("\"http/1.1\"", StringComparison.Ordinal) && !wsXrayDefaultJson.Contains("\"dialerProxy\": \"fragment\"", StringComparison.Ordinal),
            "Xray WS+TLS forces http/1.1 ALPN and omits fragment by default");

        Check(directSbJson.Contains("\"listen_port\": 20820", StringComparison.Ordinal), "Sing-Box uses resolved inbound listen_port (20820)");
        Check(directSbJson.Contains("\"protocol\": \"smux\"", StringComparison.Ordinal), "Sing-Box uses separated SingBoxMuxProtocol (smux)");
        Check(directSbJson.Contains("\"padding\": true", StringComparison.Ordinal), "Sing-Box uses separated SingBoxMuxPadding");
        Check(directSbJson.Contains("\"fragment_fallback_delay\": \"350ms\"", StringComparison.Ordinal), "Sing-Box uses separated SingBoxFragmentFallbackDelay");
        Check(directSbJson.Contains("\"default_domain_resolver\": \"local\"", StringComparison.Ordinal), "Sing-Box uses separated SingBoxRouteDnsThroughCore=false");
        Check(directSbJson.Contains("\"type\": \"httpupgrade\"", StringComparison.Ordinal), "Sing-Box emits httpupgrade transport");

        var h2Node = new V2RayConfigEntry
        {
            Name = "H2 Node",
            Protocol = "vless",
            Address = "example.com",
            Port = 443,
            UserId = "00000000-0000-0000-0000-000000000000",
            Network = "h2",
            Security = "tls",
            Sni = "example.com",
            Path = "/h2"
        };
        var h2SbJson = V2RayConfigBuilder.BuildCoreConfigJson(
            settings, h2Node, isXray: false, isChainedOuter: false, socksPort: sbPort, httpPort: sbPort + 1, dnsPlan: dnsPlan);
        Check(h2SbJson.Contains("\"alpn\"", StringComparison.Ordinal), "Sing-Box h2 transport sets tls.alpn");

        var xhttpGuardThrew = false;
        try { V2RayConfigBuilder.BuildSingBoxOutbounds(xhttpNode); }
        catch (InvalidOperationException) { xhttpGuardThrew = true; }
        Check(xhttpGuardThrew, "BuildSingBoxOutbounds throws explicit error instead of emitting broken v2ray-http for XHTTP");

        var xrayExe = FindBundledBinary("xray", "xray.exe");
        Check(xrayExe is not null, "Bundled xray.exe located for CLI validation");
        if (xrayExe is not null)
        {
            var (okXr1, outXr1) = ValidateWithExternalCli(xrayExe, "run -test -c \"{0}\"", directXrayJson, ".json");
            Check(okXr1, "xray.exe run -test validates generated HTTPUpgrade config", outXr1);
            var (okXr2, outXr2) = ValidateWithExternalCli(xrayExe, "run -test -c \"{0}\"", chainedXrayJson, ".json");
            Check(okXr2, "xray.exe run -test validates generated XHTTP chained config", outXr2);
            var (okXr3, outXr3) = ValidateWithExternalCli(xrayExe, "run -test -c \"{0}\"", wsXrayDefaultJson, ".json");
            Check(okXr3, "xray.exe run -test validates generated WS+TLS+ALPN default config", outXr3);
        }

        var sbExe = FindBundledBinary("sing_box", "sing-box.exe");
        Check(sbExe is not null, "Bundled sing-box.exe located for CLI validation");
        if (sbExe is not null)
        {
            var (okSb1, outSb1) = ValidateWithExternalCli(sbExe, "check -c \"{0}\"", directSbJson, ".json");
            Check(okSb1, "sing-box.exe check validates separated Sing-Box HTTPUpgrade+Mux+Fragment config", outSb1);
            var (okSb2, outSb2) = ValidateWithExternalCli(sbExe, "check -c \"{0}\"", h2SbJson, ".json");
            Check(okSb2, "sing-box.exe check validates H2+ALPN config", outSb2);
        }
    }

    private static void TestModalCenteringAndClamping()
    {
        Program.Section("Modal Dialog Centering & Height Clamping");

        var workArea = new Rect(0, 0, 1366, 728);
        var (leftCompact, topCompact) = ModalWindowHelper.ComputeCenteredPosition(
            ownerLeft: 200, ownerTop: 80, ownerWidth: 960, ownerHeight: 620,
            hasValidOwner: true, dialogWidth: 470, dialogHeight: 360, workArea);
        Check(leftCompact == 445 && topCompact == 210, "Compact dialog centers over owner", $"{leftCompact},{topCompact}");

        var (leftExpanded, topExpanded) = ModalWindowHelper.ComputeCenteredPosition(
            ownerLeft: 200, ownerTop: 80, ownerWidth: 960, ownerHeight: 620,
            hasValidOwner: true, dialogWidth: 470, dialogHeight: 620, workArea);
        Check(leftExpanded == 445 && topExpanded == 80 && topExpanded + 620 <= workArea.Bottom,
            "Expanded CAPTCHA dialog re-centers cleanly and stays within owner/workArea",
            $"{leftExpanded},{topExpanded}");

        var (_, clampedTop) = ModalWindowHelper.ComputeCenteredPosition(
            ownerLeft: 100, ownerTop: 350, ownerWidth: 960, ownerHeight: 620,
            hasValidOwner: true, dialogWidth: 470, dialogHeight: 600, workArea);
        Check(clampedTop + 600 <= workArea.Bottom && clampedTop >= workArea.Top,
            "Dialog near bottom edge clamps inside workArea",
            $"top={clampedTop}");

        var (_, maxH) = ModalWindowHelper.ComputeMaxDimensions(ownerHeight: 680, hasValidOwner: true, workArea);
        Check(maxH > 0 && maxH <= workArea.Height - 24 && maxH <= 680,
            "MaxHeight stays bounded to owner & screen workArea",
            $"maxH={maxH}");
    }

    private static void TestShardMtuAndNavCursor()
    {
        Program.Section("SHARD Auto MTU Scan Setting & Sidebar Hand Cursor");

        var defaults = new UserSettings();
        Check(defaults.ShardAutoScanMtu, "ShardAutoScanMtu defaults to true");
        Check(defaults.ShardMtu == 1280, "ShardMtu defaults to 1280");

        defaults.ShardAutoScanMtu = false;
        defaults.ShardMtu = 1360;
        var json = JsonSerializer.Serialize(defaults);
        var restored = JsonSerializer.Deserialize<UserSettings>(json);
        Check(restored is not null && !restored.ShardAutoScanMtu && restored.ShardMtu == 1360,
            "ShardAutoScanMtu and ShardMtu round-trip via JSON");

        var rootDir = FindRepoRoot();
        Check(rootDir is not null, "Repo root located for UI/Engine contract checks");
        if (rootDir is not null)
        {
            var styles = File.ReadAllText(Path.Combine(rootDir, "Se7enPro", "Themes", "Styles.xaml"));
            var navIdx = styles.IndexOf("x:Key=\"UI.NavItem\"", StringComparison.Ordinal);
            var navSlice = navIdx >= 0 ? styles.Substring(navIdx, Math.Min(450, styles.Length - navIdx)) : "";
            Check(navSlice.Contains("Property=\"Cursor\" Value=\"Hand\"", StringComparison.Ordinal),
                "UI.NavItem sets Cursor=Hand for sidebar tabs");

            var shardView = File.ReadAllText(Path.Combine(rootDir, "Se7enPro", "Views", "Settings", "ShardSettingsView.xaml"));
            Check(shardView.Contains("ShardAutoScanMtu", StringComparison.Ordinal) && shardView.Contains("Loc_shardAutoScanMtuTitle", StringComparison.Ordinal),
                "ShardSettingsView exposes Auto-scan before connect switch under MTU row");

            var shardEngine = File.ReadAllText(Path.Combine(rootDir, "Se7enPro", "Services", "Shard", "ShardEngine.cs"));
            Check(shardEngine.Contains("AutoConfigureShardMtuAsync", StringComparison.Ordinal) && shardEngine.Contains("s.ShardAutoScanMtu", StringComparison.Ordinal),
                "ShardEngine runs AutoConfigureShardMtuAsync before connecting when enabled");
        }
    }

    private static void TestPsiphonServerListAndAetherChainedBucket()
    {
        Program.Section("Psiphon Server List Bootstrap, Aether Chained Bucket & Unified Port UI");

        Check(AetherExtras.LastConnBucketKey(ConnectionMethod.Masque, "h2", false, isChained: true) == "chain-masque-h2",
            "WARP over V2Ray uses isolated chain-masque-h2 lastconn bucket instead of direct ISP cache");
        Check(AetherExtras.LastConnBucketKey(ConnectionMethod.Masque, "h2", false, isChained: false) == "masque-h2",
            "Direct WARP keeps masque-h2 bucket");

        var b64Url = Convert.ToBase64String(Encoding.UTF8.GetBytes("https://example.invalid/server_list_compressed"));
        var parsed = PsiphonServerListBootstrap.ParseRemoteServerListUrls($"[{{\"URL\":\"{b64Url}\"}},{{\"URL\":\"https://mirror.invalid/list\"}}]");
        Check(parsed.Count == 2 && parsed[0] == "https://example.invalid/server_list_compressed" && parsed[1] == "https://mirror.invalid/list",
            "ParseRemoteServerListUrls decodes base64 and plain HTTPS mirrors");

        var rawEntries = "3020302030\n3020302031";
        var jsonPayload = JsonSerializer.Serialize(new { data = rawEntries });
        byte[] compressed;
        using (var ms = new MemoryStream())
        {
            using (var zl = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            {
                var utf8 = Encoding.UTF8.GetBytes(jsonPayload);
                zl.Write(utf8, 0, utf8.Length);
            }
            compressed = ms.ToArray();
        }
        var extracted = PsiphonServerListBootstrap.ExtractServerEntriesPayload(compressed);
        Check(extracted == rawEntries, "ExtractServerEntriesPayload decompresses ZLib JSON remote server list");

        var parsedVless = V2RayLinkParser.ParseSingleLink("vless://00000000-0000-0000-0000-000000000000@example.com:443?type=ws&security=tls&sni=example.com#TestNode");
        Check(parsedVless is not null && parsedVless.EnableFragment is null,
            "Imported VLESS nodes leave EnableFragment=null to inherit active core setting");

        var rootDir = FindRepoRoot();
        if (rootDir is not null)
        {
            var chainedView = File.ReadAllText(Path.Combine(rootDir, "Se7enPro", "Views", "Settings", "ChainedSettingsView.xaml"));
            Check(!chainedView.Contains("V2RayInboundPort", StringComparison.Ordinal) && !chainedView.Contains("SingBoxInboundPort", StringComparison.Ordinal),
                "ChainedSettingsView removes redundant V2RayInboundPort and SingBoxInboundPort boxes");

            var chainedEngine = File.ReadAllText(Path.Combine(rootDir, "Se7enPro", "Services", "ChainedEngine.cs"));
            Check(chainedEngine.Contains("ConnectionMethod.WarpOverV2Ray => \"WARP\"", StringComparison.Ordinal),
                "ChainedEngine displays WARP [2/2] progress tag for WarpOverV2Ray");
        }
    }

    private static void TestStabilityAndAetherModeUi()
    {
        Program.Section("TUN Stability, DNS Cooldown Hysteresis, Aether Mode Toggle & Min Window Size");

        var u1 = new DnsServerEntry(DnsTransport.Udp, "1.1.1.1", System.Net.IPAddress.Parse("1.1.1.1"), "1.1.1.1", 53);
        var u2 = new DnsServerEntry(DnsTransport.Udp, "8.8.8.8", System.Net.IPAddress.Parse("8.8.8.8"), "8.8.8.8", 53);
        using var fwd = new SocksDnsForwarder(10808, relayTargets: new List<DnsServerEntry> { u1, u2 });
        var now = DateTime.UtcNow.Ticks;

        fwd.MarkTargetResult(u1, succeeded: false, nowTicks: now);
        var afterOneFail = fwd.OrderRelayTargets(new[] { u1, u2 }, now + TimeSpan.FromSeconds(1).Ticks);
        Check(afterOneFail[0].TargetHostPort == u1.TargetHostPort,
            "Single transient DNS failure does not immediately cooldown resolver");

        fwd.MarkTargetResult(u1, succeeded: false, nowTicks: now);
        var afterTwoFails = fwd.OrderRelayTargets(new[] { u1, u2 }, now + TimeSpan.FromSeconds(1).Ticks);
        Check(afterTwoFails[0].TargetHostPort == u2.TargetHostPort,
            "Two consecutive DNS failures demote resolver to cooldown");

        var s = new UserSettings();
        s.ApplyAetherPreset(fast: false);
        Check(s.AetherScanMode == "balanced"
              && s.AetherScanModeMasque == "balanced"
              && s.AetherScanModeMim == "balanced"
              && s.AetherScanModeWireguard == "balanced"
              && s.AetherScanModeWarp == "balanced"
              && s.AetherNoizeMasque == "balanced"
              && s.AetherNoizeMim == "balanced"
              && s.AetherNoizeWireguard == "balanced"
              && s.AetherNoizeWarp == "balanced"
              && s.AetherFragment,
            "Balanced Mode sets scan, noise, and H2 fragmentation to pre-fast-connect balanced defaults");

        s.ApplyAetherPreset(fast: true);
        Check(s.AetherScanMode == "turbo"
              && s.AetherScanModeMasque == "turbo"
              && s.AetherScanModeMim == "turbo"
              && s.AetherScanModeWireguard == "turbo"
              && s.AetherScanModeWarp == "turbo"
              && s.AetherNoizeMasque == "firewall"
              && s.AetherNoizeMim == "firewall"
              && !s.AetherFragment,
            "Fast Mode restores turbo/firewall fast-connect defaults");

        var rootDir = FindRepoRoot();
        if (rootDir is not null)
        {
            var aetherView = File.ReadAllText(Path.Combine(rootDir, "Se7enPro", "Views", "Settings", "AetherSettingsView.xaml"));
            var identityMatches = aetherView.Split("SelectedValue=\"{Binding AetherIdentityAcquisitionMethod,", StringSplitOptions.None).Length - 1;
            Check(identityMatches == 1,
                "AetherSettingsView keeps exactly one Identity Acquisition Method combobox",
                $"occurrences={identityMatches}");
            Check(aetherView.Contains("IsAetherFastMode, Mode=TwoWay", StringComparison.Ordinal)
                  && aetherView.Contains("IsAetherBalancedMode, Mode=TwoWay", StringComparison.Ordinal)
                  && aetherView.Contains("Loc_aetherFastMode", StringComparison.Ordinal)
                  && aetherView.Contains("Loc_aetherBalancedMode", StringComparison.Ordinal),
                "AetherSettingsView exposes Fast Mode / Balanced Mode segmented toggle");

            var mainWindowXaml = File.ReadAllText(Path.Combine(rootDir, "Se7enPro", "Views", "MainWindow.xaml"));
            Check(mainWindowXaml.Contains("MinWidth=\"1000\"", StringComparison.Ordinal)
                  && mainWindowXaml.Contains("MinHeight=\"680\"", StringComparison.Ordinal),
                "MainWindow enforces MinWidth=1000 MinHeight=680 to prevent card clipping");

            var tunSession = File.ReadAllText(Path.Combine(rootDir, "Se7enPro", "Services", "Tun", "WintunTunManager.Session.cs"));
            Check(!tunSession.Contains("\"48MiB\"", StringComparison.Ordinal)
                  && !tunSession.Contains("\"32MiB\"", StringComparison.Ordinal),
                "WintunTunManager removes 48MiB/32MiB GOMEMLIMIT caps that throttled mihomo/tun2socks");
        }
    }

    private static string? FindRepoRoot()
    {
        var dir = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
        {
            if (File.Exists(Path.Combine(dir, "Se7enPro", "Se7enPro.csproj")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        return null;
    }

    private static string? FindBundledBinary(string folder, string exeName)
    {
        var dir = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
        {
            var candidate1 = Path.Combine(dir, "Resources", folder, exeName);
            if (File.Exists(candidate1)) return candidate1;
            var candidate2 = Path.Combine(dir, "Se7enPro", "Resources", folder, exeName);
            if (File.Exists(candidate2)) return candidate2;
            dir = Directory.GetParent(dir)?.FullName;
        }
        return null;
    }

    private static (bool Ok, string Output) ValidateWithExternalCli(string exePath, string argFormat, string content, string ext)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"se7en_test_{Guid.NewGuid():N}{ext}");
        File.WriteAllText(tmp, content);
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = string.Format(argFormat, tmp),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = Process.Start(psi)!;
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(8000);
            var combined = (stdout + "\n" + stderr).Trim();
            return (proc.ExitCode == 0, combined);
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }
}
