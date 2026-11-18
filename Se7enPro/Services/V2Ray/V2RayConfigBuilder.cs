using System;
using System.Collections.Generic;
using System.Net;
using System.Text.Json;
using Se7enPro.Models;

namespace Se7enPro.Services;

internal static class V2RayConfigBuilder
{
    private static readonly HashSet<string> ValidSsMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "aes-128-gcm", "aes-256-gcm", "chacha20-poly1305", "chacha20-ietf-poly1305", "xchacha20-poly1305",
        "2022-blake3-aes-128-gcm", "2022-blake3-aes-256-gcm", "2022-blake3-chacha20-poly1305",
        "none", "plain"
    };

    private static readonly HashSet<string> ValidStreamSecurity =
        new(StringComparer.OrdinalIgnoreCase) { "none", "tls", "reality" };

    private static readonly HashSet<string> ValidFlows =
        new(StringComparer.OrdinalIgnoreCase) { "xtls-rprx-vision", "xtls-rprx-vision-udp443" };

    internal static int GetCoreInboundPort(UserSettings s, bool isXray)
    {
        if (s.UseCustomProxyPorts && s.LocalSocksProxyPort > 0)
            return s.LocalSocksProxyPort;
        return 10808;
    }

    internal static string SanitizeSingBoxMuxProtocol(string? value)
    {
        var v = value?.Trim().ToLowerInvariant();
        return v is "smux" or "yamux" ? v : "h2mux";
    }

    internal static string? ValidateOutboundNode(V2RayConfigEntry? config)
    {
        if (config is null)
        {
            return "no V2Ray/Xray/Sing-box node is configured. Add a node or pick an "
                 + "active one in Settings -> V2Ray / Xray before connecting.";
        }

        if (string.IsNullOrWhiteSpace(config.Address))
        {
            var label = string.IsNullOrWhiteSpace(config.Name) ? "the active node" : $"\"{config.Name}\"";
            return $"{label} has no server address, so it cannot carry any traffic.";
        }

        if (config.Port is < 1 or > 65535)
        {
            var label = string.IsNullOrWhiteSpace(config.Name) ? "the active node" : $"\"{config.Name}\"";
            return $"{label} has an invalid port ({config.Port}); it must be between 1 and 65535.";
        }

        return null;
    }

    internal static bool ResolveCoreKind(V2RayConfigEntry? config, string? coreSetting, out string? autoSwitchReason)
    {
        var proto = (config?.Protocol ?? "").Trim().ToLowerInvariant();
        var network = (config?.Network ?? "").Trim().ToLowerInvariant();
        var prefIsXray = (coreSetting ?? "xray").ToLowerInvariant().Contains("xray");

        if (proto is "hysteria2" or "hy2" or "tuic")
        {
            autoSwitchReason = prefIsXray
                ? $"Protocol '{proto}' requires Sing-Box core; automatically using sing-box."
                : null;
            return false;
        }

        if (network is "xhttp" or "splithttp" or "kcp" or "mkcp")
        {
            autoSwitchReason = !prefIsXray
                ? $"Transport '{network}' is not supported by Sing-Box; automatically using xray."
                : null;
            return true;
        }

        autoSwitchReason = null;
        return prefIsXray;
    }

    internal static bool HasCustomResolvers(UserSettings s) =>
        !string.IsNullOrWhiteSpace(s.CustomDnsUdp)
        || !string.IsNullOrWhiteSpace(s.CustomDnsDot)
        || !string.IsNullOrWhiteSpace(s.CustomDnsDoh);

    internal static string NormalizeStreamSecurity(string? raw)
    {
        var v = (raw ?? "").Trim().ToLowerInvariant();
        if (v.Length == 0) return "none";
        return ValidStreamSecurity.Contains(v) ? v : "none";
    }

    private static string ResolveSsMethod(V2RayConfigEntry? c, string? globalMethod)
    {
        var sec = c?.Security?.Trim();
        if (!string.IsNullOrWhiteSpace(sec) && ValidSsMethods.Contains(sec!))
            return sec!;
        if (!string.IsNullOrWhiteSpace(globalMethod) && ValidSsMethods.Contains(globalMethod.Trim()))
            return globalMethod!.Trim();
        return "2022-blake3-aes-128-gcm";
    }

    private static string NormalizeRealityPublicKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return "";
        return key.Trim().Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    internal static string BuildCoreConfigJson(
        UserSettings s,
        V2RayConfigEntry? config,
        bool isXray,
        bool isChainedOuter,
        int socksPort,
        int httpPort,
        DnsResolverPolicy.Plan dnsPlan,
        string? userUpstreamUrl = null,
        Action<string>? log = null)
    {
        var hasAuth = !isChainedOuter && LanPolicy.CanExpose(s);
        var listenAddr = isChainedOuter || !LanPolicy.CanExpose(s) ? "127.0.0.1" : "0.0.0.0";
        if (s.AllowLanConnections && !LanPolicy.HasCredentials(s))
        {
            log?.Invoke("[LAN] LAN sharing is on but no proxy username/password is set; "
                      + "the inbound stays on 127.0.0.1 to avoid an open proxy.");
        }

        if (isXray)
        {
            object socksSettings = hasAuth
                ? new { auth = "password", accounts = new[] { new { user = s.LanProxyUsername, pass = s.LanProxyPassword } }, udp = true }
                : new { auth = "noauth", udp = true };

            object httpSettings = hasAuth
                ? new { accounts = new[] { new { user = s.LanProxyUsername, pass = s.LanProxyPassword } } }
                : new { auth = "noauth" };

            var muxEnabled = !isChainedOuter && s.V2RayEnableMux;
            var routeDns = s.V2RayRouteDnsThroughV2Ray;

            var dnsServers = routeDns
                ? DnsResolverPolicy.ToXrayDnsServers(dnsPlan)
                : new List<object> { "localhost" };

            if (routeDns && HasCustomResolvers(s))
            {
                log?.Invoke($"[V2Ray] custom DNS: {dnsServers.Count - 1} resolver(s) configured, "
                          + "used in order with the built-in defaults as fallback");
            }

            var shouldFragment = config?.EnableFragment ?? s.V2RayEnableFragment;
            if (shouldFragment)
            {
                log?.Invoke($"[V2Ray] TLS fragmentation enabled (packets: {s.V2RayFragmentPackets}, length: {s.V2RayFragmentLength}, interval: {s.V2RayFragmentInterval}ms)");
            }

            var xrayInbounds = new List<object>
            {
                new
                {
                    tag = "socks-in",
                    port = socksPort,
                    listen = listenAddr,
                    protocol = "socks",
                    settings = socksSettings,
                    sniffing = new
                    {
                        enabled = !isChainedOuter,
                        destOverride = new[] { "http", "tls", "quic" },
                        routeOnly = true
                    }
                },
                new
                {
                    tag = "http-in",
                    port = httpPort,
                    listen = listenAddr,
                    protocol = "http",
                    settings = httpSettings,
                    sniffing = new
                    {
                        enabled = !isChainedOuter,
                        destOverride = new[] { "http", "tls", "quic" },
                        routeOnly = true
                    }
                }
            };

            var xrayOutbounds = BuildXrayOutbounds(config, s.ShadowsocksMethod, shouldFragment, s.V2RayFragmentPackets, s.V2RayFragmentLength, s.V2RayFragmentInterval, userUpstreamUrl);
            var xrayObj = new Dictionary<string, object>
            {
                ["log"] = new { loglevel = "warning" },
                ["dns"] = new Dictionary<string, object>
                {
                    ["queryStrategy"] = "UseIPv4",
                    ["servers"] = dnsServers
                },
                ["inbounds"] = xrayInbounds.ToArray(),
                ["outbounds"] = xrayOutbounds
            };

            if (muxEnabled)
                foreach (var ob in xrayOutbounds)
                    if (ob is Dictionary<string, object> d && d.TryGetValue("tag", out var t) && t as string == "proxy")
                        d["mux"] = new { enabled = true, concurrency = 8 };

            return JsonSerializer.Serialize(xrayObj, new JsonSerializerOptions { WriteIndented = true });
        }
        else
        {
            var shouldFragment = config?.EnableFragment ?? s.SingBoxEnableFragment;
            var sbSecurity = NormalizeStreamSecurity(config?.Security);
            var sbFragDelay = string.IsNullOrWhiteSpace(s.SingBoxFragmentFallbackDelay) ? "500ms" : s.SingBoxFragmentFallbackDelay.Trim();
            if (shouldFragment && sbSecurity == "tls")
            {
                log?.Invoke($"[V2Ray] TLS fragmentation enabled for Sing-Box (fallback_delay: {sbFragDelay})");
            }
            var singboxInbounds = new List<object>
            {
                hasAuth
                    ? (object)new
                    {
                        type = "socks",
                        tag = "socks-in",
                        listen = listenAddr,
                        listen_port = socksPort,
                        users = new[] { new { username = s.LanProxyUsername, password = s.LanProxyPassword } }
                    }
                    : new
                    {
                        type = "socks",
                        tag = "socks-in",
                        listen = listenAddr,
                        listen_port = socksPort
                    },
                hasAuth
                    ? (object)new
                    {
                        type = "http",
                        tag = "http-in",
                        listen = listenAddr,
                        listen_port = httpPort,
                        users = new[] { new { username = s.LanProxyUsername, password = s.LanProxyPassword } }
                    }
                    : new
                    {
                        type = "http",
                        tag = "http-in",
                        listen = listenAddr,
                        listen_port = httpPort
                    }
            };

            var muxEnabled2 = !isChainedOuter && s.SingBoxEnableMux;
            var muxProtocol2 = SanitizeSingBoxMuxProtocol(s.SingBoxMuxProtocol);
            var routeDns2 = s.SingBoxRouteDnsThroughCore;

            var singboxDnsServers = routeDns2
                ? DnsResolverPolicy.ToSingBoxDnsServers(dnsPlan)
                : new List<object> { new { type = "local", tag = "local" } };
            if (routeDns2 && singboxDnsServers.Count == 0)
            {
                singboxDnsServers.Add(new { type = "udp", tag = "dns0", server = "1.1.1.1", server_port = 53 });
            }

            var hasLocalDnsTag = false;
            foreach (var srv in singboxDnsServers)
            {
                if (srv is not null)
                {
                    var prop = srv.GetType().GetProperty("tag");
                    if (prop?.GetValue(srv) as string == "local")
                    {
                        hasLocalDnsTag = true;
                        break;
                    }
                }
            }
            if (!hasLocalDnsTag)
            {
                singboxDnsServers.Add(new { type = "local", tag = "local" });
            }

            if (routeDns2 && HasCustomResolvers(s))
            {
                log?.Invoke($"[V2Ray] custom DNS: {singboxDnsServers.Count - 1} resolver(s) configured, "
                          + "used in order with the built-in defaults as fallback");
            }
            var singboxOutbounds = BuildSingBoxOutbounds(config, s.ShadowsocksMethod, shouldFragment, userUpstreamUrl, sbFragDelay);
            foreach (var ob in singboxOutbounds)
            {
                if (ob is Dictionary<string, object> d && d.TryGetValue("tag", out var t) && t as string is "proxy" or "user-upstream")
                {
                    d["domain_resolver"] = "local";
                }
            }
            var singboxObj = new Dictionary<string, object>
            {
                ["log"] = new { level = "warn" },
                ["dns"] = new { servers = singboxDnsServers },
                ["inbounds"] = singboxInbounds,
                ["outbounds"] = singboxOutbounds,
                ["route"] = new
                {
                    default_domain_resolver = routeDns2 ? "dns0" : "local",
                    rules = isChainedOuter
                        ? Array.Empty<object>()
                        : new object[]
                        {
                            new { action = "sniff" }
                        }
                }
            };
            if (muxEnabled2 && singboxObj["outbounds"] is object[] arr)
                foreach (var ob in arr)
                    if (ob is Dictionary<string, object> d && d.TryGetValue("tag", out var t) && t as string == "proxy")
                        d["multiplex"] = new { enabled = true, protocol = muxProtocol2, max_connections = 4, padding = s.SingBoxMuxPadding };

            return JsonSerializer.Serialize(singboxObj, new JsonSerializerOptions { WriteIndented = true });
        }
    }

    internal static object[] BuildXrayOutbounds(
        V2RayConfigEntry? c,
        string? globalSsMethod = null,
        bool enableFragment = false,
        string fragmentPackets = "tlshello",
        string fragmentLength = "100-200",
        string fragmentInterval = "10-20",
        string? userUpstreamUrl = null)
    {
        if (c == null || string.IsNullOrWhiteSpace(c.Address))
        {
            throw new InvalidOperationException(
                ValidateOutboundNode(c)
                ?? "The selected node cannot carry traffic; refusing to build a direct (unprotected) config.");
        }

        var proto = (c.Protocol ?? "vless").ToLowerInvariant();
        var network = string.IsNullOrWhiteSpace(c.Network) ? "tcp" : c.Network.ToLowerInvariant();
        var security = proto is "shadowsocks" or "ss"
            ? "none"
            : NormalizeStreamSecurity(c.Security);

        var netIsRaw = network is "tcp" or "raw";
        var isXHttp = network is "xhttp" or "splithttp" or "http" or "h2";

        var streamSettings = new Dictionary<string, object>
        {
            ["network"] = isXHttp ? "xhttp" : network,
            ["security"] = security
        };

        if (enableFragment && string.IsNullOrEmpty(userUpstreamUrl))
        {
            streamSettings["sockopt"] = new
            {
                dialerProxy = "fragment",
                tcpNoDelay = true
            };
        }

        if (security == "reality")
        {
            if (!netIsRaw && !isXHttp && network != "grpc")
            {
                throw new InvalidOperationException(
                    $"\"{c.Name}\" uses Reality over {network}. Reality needs a TCP, XHTTP or gRPC node.");
            }
            if (string.IsNullOrWhiteSpace(c.PublicKey))
            {
                throw new InvalidOperationException(
                    $"\"{c.Name}\" asks for Reality but the link carried no public key (pbk).");
            }

            var sni = string.IsNullOrWhiteSpace(c.Sni) ? c.Host ?? c.Address : c.Sni;
            if (IPAddress.TryParse(sni, out _))
            {
                throw new InvalidOperationException(
                    $"\"{c.Name}\" has an IP address ({sni}) as its SNI. TLS and Reality need a hostname.");
            }

            streamSettings["realitySettings"] = new
            {
                serverName = sni,
                publicKey = c.PublicKey!,
                shortId = c.ShortId ?? "",
                spiderX = "/",
                fingerprint = "chrome"
            };
        }
        else if (security == "tls")
        {
            var sni = string.IsNullOrWhiteSpace(c.Sni) ? c.Host ?? c.Address : c.Sni;
            var tlsDict = new Dictionary<string, object>
            {
                ["serverName"] = sni,
                ["fingerprint"] = "chrome"
            };
            if (network is "h2" or "http" or "xhttp" or "splithttp")
            {
                tlsDict["alpn"] = new[] { "h2", "http/1.1" };
            }
            else if (network is "grpc" or "gun")
            {
                tlsDict["alpn"] = new[] { "h2" };
            }
            else
            {
                tlsDict["alpn"] = new[] { "http/1.1" };
            }
            streamSettings["tlsSettings"] = tlsDict;
        }

        if (isXHttp)
        {
            var xhttpHost = !string.IsNullOrWhiteSpace(c.Host) ? c.Host : (!string.IsNullOrWhiteSpace(c.Sni) ? c.Sni : c.Address);
            streamSettings["xhttpSettings"] = new
            {
                path = c.Path ?? "/",
                host = xhttpHost,
                mode = network is "xhttp" or "splithttp" ? "auto" : "stream-one"
            };
        }
        else if (network == "ws")
        {
            var wsHost = !string.IsNullOrWhiteSpace(c.Host) ? c.Host : (!string.IsNullOrWhiteSpace(c.Sni) ? c.Sni : c.Address);
            streamSettings["wsSettings"] = new
            {
                path = c.Path ?? "/",
                host = wsHost
            };
        }
        else if (network == "httpupgrade")
        {
            var huHost = !string.IsNullOrWhiteSpace(c.Host) ? c.Host : (!string.IsNullOrWhiteSpace(c.Sni) ? c.Sni : c.Address);
            streamSettings["httpupgradeSettings"] = new
            {
                path = c.Path ?? "/",
                host = huHost
            };
        }
        else if (network == "grpc")
        {
            streamSettings["grpcSettings"] = new
            {
                serviceName = c.Path ?? "",
                multiMode = true
            };
        }

        var outboundSettings = new Dictionary<string, object>();
        if (proto == "vless")
        {
            var userObj = new Dictionary<string, object>
            {
                ["id"] = c.UserId,
                ["encryption"] = "none"
            };

            if (!string.IsNullOrWhiteSpace(c.Flow))
            {
                var cleanFlow = c.Flow.Trim();
                if (netIsRaw && ValidFlows.Contains(cleanFlow))
                {
                    userObj["flow"] = cleanFlow.ToLowerInvariant();
                }
            }

            outboundSettings["vnext"] = new object[]
            {
                new
                {
                    address = c.Address,
                    port = c.Port,
                    users = new object[] { userObj }
                }
            };
        }
        else if (proto == "vmess")
        {
            outboundSettings["vnext"] = new object[]
            {
                new
                {
                    address = c.Address,
                    port = c.Port,
                    users = new object[] { new { id = c.UserId, alterId = 0, security = "auto" } }
                }
            };
        }
        else if (proto == "trojan")
        {
            outboundSettings["servers"] = new object[]
            {
                new
                {
                    address = c.Address,
                    port = c.Port,
                    password = c.UserId
                }
            };
        }
        else if (proto is "shadowsocks" or "ss")
        {
            var ssMethod = ResolveSsMethod(c, globalSsMethod);
            outboundSettings["servers"] = new object[]
            {
                new { address = c.Address, port = c.Port, method = ssMethod, password = c.UserId }
            };
        }
        else if (proto is "hysteria2" or "hy2" or "tuic")
        {
            throw new InvalidOperationException(
                $"Xray does not support {proto.ToUpperInvariant()}. Sing-box must be used for this protocol.");
        }
        else
        {
            outboundSettings["vnext"] = new object[]
            {
                new
                {
                    address = c.Address,
                    port = c.Port,
                    users = new object[] { new { id = c.UserId } }
                }
            };
        }

        var outbounds = new List<object>
        {
            new Dictionary<string, object>
            {
                ["tag"] = "proxy",
                ["protocol"] = (proto is "shadowsocks" or "ss") ? "shadowsocks" : proto,
                ["settings"] = outboundSettings,
                ["streamSettings"] = streamSettings
            }
        };

        if (!string.IsNullOrEmpty(userUpstreamUrl))
        {
            var upstream = BuildXrayUpstreamOutbound(userUpstreamUrl);
            if (upstream is not null)
            {
                var sockopt = new Dictionary<string, object>();
                if (streamSettings.TryGetValue("sockopt", out var existing) &&
                    existing is Dictionary<string, object> existingMap)
                {
                    foreach (var kv in existingMap) sockopt[kv.Key] = kv.Value;
                }

                sockopt["dialerProxy"] = "user-upstream";
                streamSettings["sockopt"] = sockopt;
                outbounds.Add(upstream);
            }
        }

        if (enableFragment && string.IsNullOrEmpty(userUpstreamUrl))
        {
            outbounds.Add(new
            {
                tag = "fragment",
                protocol = "freedom",
                settings = new
                {
                    domainStrategy = "AsIs",
                    fragment = new
                    {
                        packets = string.IsNullOrWhiteSpace(fragmentPackets) ? "tlshello" : fragmentPackets.Trim(),
                        length = string.IsNullOrWhiteSpace(fragmentLength) ? "100-200" : fragmentLength.Trim(),
                        interval = string.IsNullOrWhiteSpace(fragmentInterval) ? "10-20" : fragmentInterval.Trim()
                    }
                },
                streamSettings = new
                {
                    sockopt = new
                    {
                        tcpNoDelay = true
                    }
                }
            });
        }

        outbounds.Add(new { tag = "direct", protocol = "freedom", settings = new { } });
        return outbounds.ToArray();
    }

    internal static object[] BuildSingBoxOutbounds(V2RayConfigEntry? c, string? globalSsMethod = null, bool enableFragment = false, string? userUpstreamUrl = null, string? fragmentFallbackDelay = null)
    {
        if (c == null || string.IsNullOrWhiteSpace(c.Address))
        {
            throw new InvalidOperationException(
                ValidateOutboundNode(c)
                ?? "The selected node cannot carry traffic; refusing to build a direct (unprotected) config.");
        }

        var netRaw = (c.Network ?? "").Trim().ToLowerInvariant();
        if (netRaw is "xhttp" or "splithttp" or "kcp" or "mkcp")
        {
            throw new InvalidOperationException(
                $"Sing-Box does not support '{netRaw}' transport. Switch to Xray core (auto-selected on start).");
        }

        var proto = (c.Protocol ?? "vless").ToLowerInvariant();
        var outb = new Dictionary<string, object>
        {
            ["type"] = (proto == "ss") ? "shadowsocks" : proto,
            ["tag"] = "proxy",
            ["server"] = c.Address,
            ["server_port"] = c.Port
        };

        if (proto == "vless")
        {
            outb["uuid"] = c.UserId;
            if (!string.IsNullOrWhiteSpace(c.Flow))
            {
                var cleanFlow = c.Flow.Trim();
                var sbNet = (c.Network ?? "tcp").Trim().ToLowerInvariant();
                if (ValidFlows.Contains(cleanFlow) && (sbNet is "tcp" or "raw"))
                {
                    outb["flow"] = cleanFlow.Equals("xtls-rprx-vision-udp443", StringComparison.OrdinalIgnoreCase)
                        ? "xtls-rprx-vision"
                        : cleanFlow.ToLowerInvariant();
                }
            }
        }
        else if (proto == "vmess")
        {
            outb["uuid"] = c.UserId;
            outb["security"] = "auto";
        }
        else if (proto == "trojan")
        {
            outb["password"] = c.UserId;
        }
        else if (proto is "shadowsocks" or "ss")
        {
            var ssMethod = ResolveSsMethod(c, globalSsMethod);
            if (ssMethod.Equals("chacha20-poly1305", StringComparison.OrdinalIgnoreCase))
                ssMethod = "chacha20-ietf-poly1305";
            outb["method"] = ssMethod;
            outb["password"] = c.UserId;
        }
        else if (proto is "hysteria2" or "hy2")
        {
            outb["type"] = "hysteria2";
            outb["password"] = c.UserId;
        }
        else if (proto == "tuic")
        {
            outb["type"] = "tuic";
            outb["uuid"] = c.UserId;
            outb["password"] = c.UserId;
        }

        var singBoxSecurity = NormalizeStreamSecurity(c.Security);
        if (singBoxSecurity is "tls" or "reality")
        {
            var tls = new Dictionary<string, object>
            {
                ["enabled"] = true,
                ["server_name"] = string.IsNullOrWhiteSpace(c.Sni) ? c.Host ?? c.Address : c.Sni,
                ["insecure"] = false,
                ["utls"] = new
                {
                    enabled = true,
                    fingerprint = "chrome"
                }
            };
            if (singBoxSecurity == "reality")
            {
                var realityDict = new Dictionary<string, object>
                {
                    ["enabled"] = true,
                    ["public_key"] = NormalizeRealityPublicKey(c.PublicKey)
                };
                if (!string.IsNullOrWhiteSpace(c.ShortId))
                {
                    realityDict["short_id"] = c.ShortId.Trim();
                }
                tls["reality"] = realityDict;
            }
            if (singBoxSecurity == "tls" && netRaw is "h2" or "http" or "grpc")
            {
                tls["alpn"] = new[] { "h2", "http/1.1" };
            }
            if (enableFragment && singBoxSecurity == "tls")
            {
                tls["fragment"] = true;
                var delay = string.IsNullOrWhiteSpace(fragmentFallbackDelay) ? "500ms" : fragmentFallbackDelay.Trim();
                tls["fragment_fallback_delay"] = delay;
            }
            outb["tls"] = tls;
        }

        if (netRaw == "ws")
        {
            var wsPath = c.Path ?? "/";
            var wsHeaders = new { Host = !string.IsNullOrWhiteSpace(c.Host) ? c.Host : (!string.IsNullOrWhiteSpace(c.Sni) ? c.Sni : c.Address) };
            var edIdx = wsPath.IndexOf("?ed=", StringComparison.OrdinalIgnoreCase);
            if (edIdx >= 0 && int.TryParse(wsPath[(edIdx + 4)..].Split('&')[0], out var maxEd) && maxEd > 0)
            {
                wsPath = edIdx == 0 ? "/" : wsPath[..edIdx];
                outb["transport"] = new
                {
                    type = "ws",
                    path = wsPath,
                    headers = wsHeaders,
                    max_early_data = maxEd,
                    early_data_header_name = "Sec-WebSocket-Protocol"
                };
            }
            else
            {
                outb["transport"] = new
                {
                    type = "ws",
                    path = wsPath,
                    headers = wsHeaders
                };
            }
        }
        else if (netRaw == "httpupgrade")
        {
            outb["transport"] = new
            {
                type = "httpupgrade",
                path = c.Path ?? "/",
                host = !string.IsNullOrWhiteSpace(c.Host) ? c.Host : (!string.IsNullOrWhiteSpace(c.Sni) ? c.Sni : c.Address)
            };
        }
        else if (netRaw is "http" or "h2")
        {
            outb["transport"] = new
            {
                type = "http",
                path = c.Path ?? "/",
                host = new[] { !string.IsNullOrWhiteSpace(c.Host) ? c.Host : (!string.IsNullOrWhiteSpace(c.Sni) ? c.Sni : c.Address) }
            };
        }
        else if (netRaw == "grpc")
        {
            outb["transport"] = new
            {
                type = "grpc",
                service_name = c.Path ?? ""
            };
        }

        if (!string.IsNullOrEmpty(userUpstreamUrl))
        {
            var upstream = BuildSingBoxUpstreamOutbound(userUpstreamUrl);
            if (upstream is not null)
            {
                outb["detour"] = "user-upstream";
                return new object[]
                {
                    outb,
                    upstream,
                    new { type = "direct", tag = "direct" }
                };
            }
        }

        return new object[]
        {
            outb,
            new { type = "direct", tag = "direct" }
        };
    }

    private static object? BuildXrayUpstreamOutbound(string url)
    {
        if (!TryParseUpstreamUri(url, out var uri, out var user, out var pass))
            return null;
        var scheme = uri.Scheme.ToLowerInvariant();
        object users = Array.Empty<object>();
        if (!string.IsNullOrEmpty(user))
        {
            users = new object[]
            {
                string.IsNullOrEmpty(pass)
                    ? new { user = user }
                    : new { user = user, pass = pass }
            };
        }

        if (scheme is "socks5" or "socks" or "socks5h")
        {
            return new Dictionary<string, object>
            {
                ["tag"] = "user-upstream",
                ["protocol"] = "socks",
                ["settings"] = new Dictionary<string, object>
                {
                    ["servers"] = new object[]
                    {
                        new Dictionary<string, object>
                        {
                            ["address"] = uri.Host,
                            ["port"] = uri.Port > 0 ? uri.Port : 1080,
                            ["users"] = users
                        }
                    }
                }
            };
        }

        if (scheme is "http" or "https")
        {
            return new Dictionary<string, object>
            {
                ["tag"] = "user-upstream",
                ["protocol"] = "http",
                ["settings"] = new Dictionary<string, object>
                {
                    ["servers"] = new object[]
                    {
                        new Dictionary<string, object>
                        {
                            ["address"] = uri.Host,
                            ["port"] = uri.Port > 0 ? uri.Port : (scheme == "https" ? 443 : 8080),
                            ["users"] = users
                        }
                    }
                }
            };
        }
        return null;
    }

    private static object? BuildSingBoxUpstreamOutbound(string url)
    {
        if (!TryParseUpstreamUri(url, out var uri, out var user, out var pass))
            return null;
        var scheme = uri.Scheme.ToLowerInvariant();
        var d = new Dictionary<string, object>
        {
            ["tag"] = "user-upstream",
            ["server"] = uri.Host,
            ["server_port"] = uri.Port > 0 ? uri.Port : 1080
        };
        if (scheme is "socks5" or "socks" or "socks5h")
        {
            d["type"] = "socks";
            d["version"] = "5";
        }
        else if (scheme is "http" or "https")
        {
            d["type"] = "http";
            if (uri.Port <= 0) d["server_port"] = scheme == "https" ? 443 : 8080;
        }
        else
        {
            return null;
        }

        if (!string.IsNullOrEmpty(user))
        {
            d["username"] = user;
            d["password"] = pass ?? "";
        }
        return d;
    }

    private static bool TryParseUpstreamUri(
        string url, out Uri uri, out string? user, out string? pass)
    {
        user = null;
        pass = null;
        try
        {
            uri = new Uri(url);
            if (!uri.IsAbsoluteUri || string.IsNullOrEmpty(uri.Host)) return false;
            var info = uri.UserInfo;
            if (!string.IsNullOrEmpty(info))
            {
                var parts = info.Split(':', 2);
                user = Uri.UnescapeDataString(parts[0]);
                pass = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "";
            }
            return true;
        }
        catch
        {
            uri = null!;
            return false;
        }
    }
}
