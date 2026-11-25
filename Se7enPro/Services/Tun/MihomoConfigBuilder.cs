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
        SplitRoutingPlan? plan = null)
    {
        var requiresAuth = LanPolicy.CanExpose(s);
        var supportsSocksUdp = SupportsSocksUdp(activeMethod);
        var hasDirectOut = !string.IsNullOrEmpty(realIfaceName);
        plan ??= SplitRoutingPlan.Disabled;
        var hasDomainSplitRules = plan.Enabled && plan.HasDomainRules;
        var hasProcessRules = hasDirectOut || plan.HasAppRules;

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

        // Block domain rules must be consulted BEFORE the port-53 redirect, otherwise a
        // query for a blocked domain matches DST-PORT,53 first and is dialled through the
        // proxy. The first matching rule wins in mihomo, so ordering is the enforcement.
        if (plan.Enabled)
        {
            foreach (var domain in plan.BlockedDomains)
            {
                var clean = SanitizeRuleAtom(domain).TrimStart('*').TrimStart('.');
                if (clean.Length == 0) continue;
                sb.AppendLine($"  - {YamlSingleQuote($"DOMAIN-SUFFIX,{clean},REJECT")}");
            }
        }

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

        // Precedence is explicit and deterministic: Block, then Bypass (Se7enDirect),
        // then Through Tunnel (Se7enProxy), then the profile default via MATCH. Every
        // application belongs to exactly one list, so no rule can shadow another one.
        var directTarget = hasDirectOut ? "Se7enDirect" : "Se7enProxy";

        foreach (var category in new[] { SplitRuleCategory.Block, SplitRuleCategory.Direct, SplitRuleCategory.Proxy })
        {
            EmitRules(sb, plan, category, directTarget, hasDirectOut);
        }

        var matchTarget = plan.Enabled
            ? plan.DefaultCategory switch
            {
                SplitRuleCategory.Direct => directTarget,
                SplitRuleCategory.Block => "REJECT",
                _ => "Se7enProxy",
            }
            : "Se7enProxy";
        sb.AppendLine($"  - {YamlSingleQuote($"MATCH,{matchTarget}")}");

        return sb.ToString();
    }

    private static void EmitRules(
        StringBuilder sb,
        SplitRoutingPlan plan,
        SplitRuleCategory category,
        string directTarget,
        bool hasDirectOut)
    {
        var target = category switch
        {
            SplitRuleCategory.Block => "REJECT",
            SplitRuleCategory.Direct => directTarget,
            _ => "Se7enProxy",
        };
        var noResolve = category == SplitRuleCategory.Direct && !hasDirectOut ? ",no-resolve" : "";

        foreach (var rule in plan.Rules)
        {
            if (rule.Category != category) continue;

            switch (rule.Target)
            {
                case SplitRuleTarget.ProcessPath:
                    {
                        var clean = SanitizeRuleAtom(rule.Value);
                        if (clean.Length > 0)
                        {
                            sb.AppendLine($"  - {YamlSingleQuote($"PROCESS-PATH,{clean},{target}")}");
                        }
                        break;
                    }

                case SplitRuleTarget.ProcessName:
                    {
                        var clean = SanitizeRuleAtom(rule.Value);
                        if (clean.Length > 0)
                        {
                            sb.AppendLine($"  - {YamlSingleQuote($"PROCESS-NAME,{clean},{target}")}");
                        }
                        break;
                    }

                case SplitRuleTarget.Keyword:
                    {
                        // mihomo has no substring matcher for images, so a keyword is
                        // expressed with the wildcard matchers it does have:
                        // "*keyword*" on the name, same on the full path.
                        var clean = SanitizeRuleAtom(rule.Value);
                        if (clean.Length > 0)
                        {
                            sb.AppendLine($"  - {YamlSingleQuote($"PROCESS-NAME-WILDCARD,*{clean}*,{target}")}");
                            sb.AppendLine($"  - {YamlSingleQuote($"PROCESS-PATH-WILDCARD,*{clean}*,{target}")}");
                        }
                        break;
                    }

                case SplitRuleTarget.Regex:
                    {
                        var clean = SanitizeRuleAtom(rule.Value);
                        if (clean.Length > 0)
                        {
                            sb.AppendLine($"  - {YamlSingleQuote($"PROCESS-NAME-REGEX,{clean},{target}")}");
                            sb.AppendLine($"  - {YamlSingleQuote($"PROCESS-PATH-REGEX,{clean},{target}")}");
                        }
                        break;
                    }

                case SplitRuleTarget.Suffix:
                    {
                        // A suffix is a tail match, which mihomo spells as "*.suffix".
                        var clean = SanitizeRuleAtom(rule.Value);
                        if (clean.Length > 0)
                        {
                            sb.AppendLine($"  - {YamlSingleQuote($"PROCESS-NAME-WILDCARD,*{clean},{target}")}");
                        }
                        break;
                    }

                case SplitRuleTarget.IpCidr:
                    {
                        var parsed = SplitRules.ParseIpCidr(rule.Value);
                        if (parsed is null) break;
                        var clean = SanitizeRuleAtom(rule.Value);
                        var ruleType = parsed.Value.Addr.AddressFamily == AddressFamily.InterNetworkV6
                            ? "IP-CIDR6"
                            : "IP-CIDR";
                        sb.AppendLine($"  - {YamlSingleQuote($"{ruleType},{clean},{target},no-resolve")}");
                        break;
                    }

                default:
                    {
                        var clean = SanitizeRuleAtom(rule.Value).TrimStart('*').TrimStart('.');
                        if (clean.Length > 0)
                        {
                            sb.AppendLine($"  - {YamlSingleQuote($"DOMAIN-SUFFIX,{clean},{target}{noResolve}")}");
                        }
                        break;
                    }
            }
        }
    }
}
