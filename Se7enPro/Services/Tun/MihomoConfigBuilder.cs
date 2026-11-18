using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using Se7enPro.Models;

namespace Se7enPro.Services;

internal static class MihomoConfigBuilder
{
    internal static bool SupportsSocksUdp(ConnectionMethod method)
        => method.IsAether()
           || method == ConnectionMethod.WarpOverV2Ray
           || method == ConnectionMethod.Shard
           || method == ConnectionMethod.Lantern
           || method == ConnectionMethod.LanternOverWarp;

    internal static bool SupportsTunnelIpv6(ConnectionMethod method, Se7enPro.Models.UserSettings settings)
    {
        if (!method.IsAether()) return false;
        var ipVer = method switch
        {
            ConnectionMethod.WireGuard => !string.IsNullOrWhiteSpace(settings.AetherIpVersionWireguard) ? settings.AetherIpVersionWireguard : settings.AetherIpVersion,
            ConnectionMethod.WarpOnWarp => !string.IsNullOrWhiteSpace(settings.AetherIpVersionWarp) ? settings.AetherIpVersionWarp : settings.AetherIpVersion,
            ConnectionMethod.MasqueInMasque => !string.IsNullOrWhiteSpace(settings.AetherIpVersionMim) ? settings.AetherIpVersionMim : settings.AetherIpVersion,
            _ => !string.IsNullOrWhiteSpace(settings.AetherIpVersionMasque) ? settings.AetherIpVersionMasque : settings.AetherIpVersion,
        };
        return (ipVer ?? "4").Trim().ToLowerInvariant() is "6" or "dual" or "both";
    }

    internal static string YamlSingleQuote(string value) =>
        "'" + value.Replace("'", "''") + "'";

    internal static string SanitizeRuleAtom(string value) =>
        value.Replace("\r", "").Replace("\n", "").Replace(",", "").Trim();

    internal static string BuildYaml(
        int socksPort,
        bool enableV6,
        ConnectionMethod activeMethod,
        int activeTunMtu,
        string tunInterfaceName,
        string tunAddressV4,
        int tunPrefixV4,
        string tunAddressV6,
        int tunPrefixV6,
        UserSettings s,
        string? realIfaceName,
        IEnumerable<string> engineProcessNames,
        IReadOnlyList<string>? splitDomains = null,
        IReadOnlyList<(string CleanCidr, AddressFamily Family)>? splitIpCidrs = null,
        IReadOnlyList<string>? splitProcNames = null,
        IReadOnlyList<string>? splitProcPaths = null)
    {
        var requiresAuth = LanPolicy.CanExpose(s);
        var supportsSocksUdp = SupportsSocksUdp(activeMethod);
        var hasDirectOut = !string.IsNullOrEmpty(realIfaceName);
        var splitInclude = string.Equals((s.SplitTunnelMode ?? "exclude").Trim(), "include", StringComparison.OrdinalIgnoreCase);
        var emitSplitRules = s.SplitTunnelEnabled && (splitInclude || hasDirectOut);
        var hasDomainSplitRules = emitSplitRules && splitDomains is { Count: > 0 };
        var hasProcessRules = hasDirectOut
            || (emitSplitRules && (splitProcNames is { Count: > 0 } || splitProcPaths is { Count: > 0 }));

        var sb = new StringBuilder();
        sb.AppendLine("mode: rule");
        sb.AppendLine("log-level: warning");
        sb.AppendLine($"ipv6: {(enableV6 ? "true" : "false")}");
        sb.AppendLine("geodata-mode: false");
        sb.AppendLine("geo-auto-update: false");
        sb.AppendLine("tcp-concurrent: true");
        sb.AppendLine("keep-alive-idle: 60");
        sb.AppendLine("keep-alive-interval: 15");
        sb.AppendLine($"find-process-mode: {(hasProcessRules ? "strict" : "off")}");
        sb.AppendLine("tun:");
        sb.AppendLine("  enable: true");
        sb.AppendLine("  stack: gvisor");
        sb.AppendLine($"  device: {tunInterfaceName}");
        sb.AppendLine($"  mtu: {activeTunMtu}");
        sb.AppendLine("  auto-route: false");
        sb.AppendLine("  auto-redirect: false");
        sb.AppendLine("  auto-detect-interface: true");
        sb.AppendLine("  strict-route: false");
        sb.AppendLine("  inet4-address:");
        sb.AppendLine($"    - {tunAddressV4}/{tunPrefixV4}");
        if (enableV6)
        {
            sb.AppendLine("  inet6-address:");
            sb.AppendLine($"    - {tunAddressV6}/{tunPrefixV6}");
        }
        sb.AppendLine("dns:");
        sb.AppendLine("  enable: false");
        sb.AppendLine("proxies:");
        sb.AppendLine("  - name: Se7enProxy");
        sb.AppendLine("    type: socks5");
        sb.AppendLine("    server: 127.0.0.1");
        sb.AppendLine($"    port: {socksPort}");
        sb.AppendLine($"    udp: {(supportsSocksUdp ? "true" : "false")}");
        if (requiresAuth)
        {
            if (!string.IsNullOrEmpty(s.LanProxyUsername))
                sb.AppendLine($"    username: {YamlSingleQuote(s.LanProxyUsername)}");
            if (!string.IsNullOrEmpty(s.LanProxyPassword))
                sb.AppendLine($"    password: {YamlSingleQuote(s.LanProxyPassword)}");
        }
        if (!string.IsNullOrEmpty(realIfaceName))
        {
            sb.AppendLine("  - name: Se7enDirect");
            sb.AppendLine("    type: direct");
            sb.AppendLine($"    interface-name: {YamlSingleQuote(realIfaceName)}");
        }
        if (hasDomainSplitRules)
        {
            sb.AppendLine("sniffer:");
            sb.AppendLine("  enable: true");
            sb.AppendLine("  override-destination: false");
            sb.AppendLine("  sniff:");
            sb.AppendLine("    HTTP:");
            sb.AppendLine("      ports: [80, 8080-8880]");
            sb.AppendLine("    TLS:");
            sb.AppendLine("      ports: [443, 8443]");
            if (supportsSocksUdp)
            {
                sb.AppendLine("    QUIC:");
                sb.AppendLine("      ports: [443]");
            }
        }
        else
        {
            sb.AppendLine("sniffer:");
            sb.AppendLine("  enable: false");
        }
        sb.AppendLine("rules:");
        if (supportsSocksUdp)
        {
            sb.AppendLine($"  - {YamlSingleQuote("DST-PORT,53,Se7enProxy")}");
        }
        else
        {
            sb.AppendLine($"  - {YamlSingleQuote("AND,((NETWORK,udp),(DST-PORT,53)),REJECT")}");
            sb.AppendLine($"  - {YamlSingleQuote("DST-PORT,53,Se7enProxy")}");
            sb.AppendLine($"  - {YamlSingleQuote("AND,((NETWORK,udp),(NOT,((DST-PORT,53)))),REJECT")}");
        }

        if (hasDirectOut)
        {
            var seenEngineProcs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rawName in engineProcessNames)
            {
                var clean = SanitizeRuleAtom(rawName);
                if (clean.Length == 0) continue;
                var exeName = clean.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? clean : clean + ".exe";
                if (seenEngineProcs.Add(exeName))
                {
                    sb.AppendLine($"  - {YamlSingleQuote($"PROCESS-NAME,{exeName},Se7enDirect")}");
                }
            }
        }

        if (s.SplitTunnelEnabled)
        {
            var include = string.Equals((s.SplitTunnelMode ?? "exclude").Trim(), "include", StringComparison.OrdinalIgnoreCase);
            var emitRules = include || hasDirectOut;
            if (emitRules)
            {
                var target = include ? "Se7enProxy" : "Se7enDirect";
                if (splitProcPaths is not null)
                {
                    foreach (var p in splitProcPaths)
                    {
                        var clean = SanitizeRuleAtom(p);
                        if (clean.Length > 0)
                            sb.AppendLine($"  - {YamlSingleQuote($"PROCESS-PATH,{clean},{target}")}");
                    }
                }
                if (splitProcNames is not null)
                {
                    foreach (var n in splitProcNames)
                    {
                        var clean = SanitizeRuleAtom(n);
                        if (clean.Length > 0)
                            sb.AppendLine($"  - {YamlSingleQuote($"PROCESS-NAME,{clean},{target}")}");
                    }
                }
                if (splitIpCidrs is not null)
                {
                    foreach (var (cidr, family) in splitIpCidrs)
                    {
                        var ruleType = family == AddressFamily.InterNetworkV6 ? "IP-CIDR6" : "IP-CIDR";
                        sb.AppendLine($"  - {YamlSingleQuote($"{ruleType},{cidr},{target},no-resolve")}");
                    }
                }
                if (splitDomains is not null)
                {
                    foreach (var d in splitDomains)
                    {
                        var clean = SanitizeRuleAtom(d).TrimStart('*').TrimStart('.');
                        if (clean.Length > 0)
                            sb.AppendLine($"  - {YamlSingleQuote($"DOMAIN-SUFFIX,{clean},{target}")}");
                    }
                }
            }

            sb.AppendLine($"  - {YamlSingleQuote(include && hasDirectOut ? "MATCH,Se7enDirect" : "MATCH,Se7enProxy")}");
        }
        else
        {
            sb.AppendLine($"  - {YamlSingleQuote("MATCH,Se7enProxy")}");
        }

        return sb.ToString();
    }
}
