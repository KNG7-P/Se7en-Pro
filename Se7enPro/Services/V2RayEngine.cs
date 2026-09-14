using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Se7enPro.Models;

namespace Se7enPro.Services;

public sealed class V2RayEngine : IConnectionEngine, IDisposable
{
    private readonly ILogger<V2RayEngine> _logger;
    private readonly ISettingsService _settings;
    private readonly IChildProcessGuard _childGuard;

    private Process? _process;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _runCts;

    public V2RayEngine(
        ILogger<V2RayEngine> logger,
        ISettingsService settings,
        IChildProcessGuard childGuard)
    {
        _logger = logger;
        _settings = settings;
        _childGuard = childGuard;
    }

    public ConnectionMethod Method => ConnectionMethod.PsiphonOverV2Ray;
    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;

    private static readonly object _overrideLock = new();
    private static int? _socksPortOverride;
    internal static void SetSocksPortOverride(int port) { lock (_overrideLock) _socksPortOverride = port; }
    internal static void ClearSocksPortOverride() { lock (_overrideLock) _socksPortOverride = null; }
    internal static int? TryGetSocksPortOverride() { lock (_overrideLock) return _socksPortOverride; }

    public int SocksProxyPort => TryGetSocksPortOverride() ??
        (_settings.Settings.UseCustomProxyPorts && _settings.Settings.LocalSocksProxyPort > 0
            ? _settings.Settings.LocalSocksProxyPort
            : (_settings.Settings.V2RayInboundPort > 0 ? _settings.Settings.V2RayInboundPort : 10808));
    public int HttpProxyPort => TryGetSocksPortOverride().HasValue
        ? TryGetSocksPortOverride()!.Value + 1
        : (_settings.Settings.UseCustomProxyPorts && _settings.Settings.LocalHttpProxyPort > 0
            ? _settings.Settings.LocalHttpProxyPort
            : (SocksProxyPort + 1));
    public string ClientRegion => "";
    public string ConnectedServerRegion => "";
    public string CurrentRouteIp
    {
        get
        {
            var active = ResolveActiveConfig();
            return active?.Address?.Trim() ?? "";
        }
    }
    public string CurrentRouteSni
    {
        get
        {
            var active = ResolveActiveConfig();
            return active?.Sni?.Trim() ?? active?.Host?.Trim() ?? "";
        }
    }
    public IReadOnlyList<string> AvailableEgressRegions => Array.Empty<string>();
    public long BytesSent { get; private set; }
    public long BytesReceived { get; private set; }
    public int ConnectProgressPercent { get; private set; }
    public string ConnectProgressText { get; private set; } = "";
    public IReadOnlyList<string> CoreProcessNames => new[] { "xray.exe", "sing-box.exe" };

    public event EventHandler<ConnectionState>? StateChanged;
    public event EventHandler<Notice>? NoticeReceived;
    public event EventHandler<string>? LogLineAppended;
    public event EventHandler? BytesTransferredChanged;
    public event EventHandler? RouteChanged;
    public event EventHandler? ConnectProgressChanged;

    private void SetState(ConnectionState state)
    {
        if (State == state) return;
        State = state;
        StateChanged?.Invoke(this, state);
    }

    private void SetProgress(int percent, string text)
    {
        ConnectProgressPercent = Math.Clamp(percent, 0, 100);
        ConnectProgressText = text;
        try { ConnectProgressChanged?.Invoke(this, EventArgs.Empty); } catch { }
    }

    private void Log(string line)
    {
        LogLineAppended?.Invoke(this, line);
        _logger.LogInformation("[V2Ray] {Line}", line);
    }

    public async Task StartAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (State is ConnectionState.Connecting or ConnectionState.Connected) return;

            SetState(ConnectionState.Connecting);
            SetProgress(15, "Configuring V2Ray/Xray/Sing-box node...");

            _runCts?.Cancel();
            _runCts = new CancellationTokenSource();

            var config = ResolveActiveConfig();
            if (config == null)
            {
                Log("No active V2Ray configuration found. Starting on default SOCKS inbound port.");
            }

            var (exePath, isXray) = ResolveCoreExecutable();
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
            {
                Log($"V2Ray/Sing-box executable not found at {exePath}");
                SetState(ConnectionState.Error);
                return;
            }

            var configFile = GenerateConfigFile(config, isXray);
            Log($"Starting core: {Path.GetFileName(exePath)} (Inbound port: {SocksProxyPort})...");
            SetProgress(40, $"Launching {Path.GetFileNameWithoutExtension(exePath)}...");

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = isXray ? $"run -c \"{configFile}\"" : $"run -c \"{configFile}\"",
                WorkingDirectory = Path.GetDirectoryName(exePath)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            if (!isXray)
            {
                psi.EnvironmentVariables["ENABLE_DEPRECATED_LEGACY_DNS_SERVERS"] = "true";
                psi.EnvironmentVariables["ENABLE_DEPRECATED_MISSING_DOMAIN_RESOLVER"] = "true";
            }

            _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _process.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data)) Log(e.Data);
            };
            _process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data)) Log(e.Data);
            };
            _process.Exited += (_, _) =>
            {
                Log("V2Ray core process exited.");
                if (State != ConnectionState.Disconnected)
                {
                    SetState(ConnectionState.Disconnected);
                }
            };

            _process.Start();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            _childGuard.Adopt(_process);

            var portReady = false;
            for (var i = 0; i < 30; i++)
            {
                if (_process == null || _process.HasExited) break;
                try
                {
                    using var probe = new TcpClient();
                    using var probeCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
                    await probe.ConnectAsync(IPAddress.Loopback, SocksProxyPort, probeCts.Token);
                    portReady = true;
                    break;
                }
                catch
                {
                    await Task.Delay(250);
                }
            }

            if (!portReady || _process == null || _process.HasExited)
            {
                if (_runCts?.IsCancellationRequested == true)
                {
                    SetState(ConnectionState.Disconnected);
                    return;
                }
                Log($"V2Ray inbound port {SocksProxyPort} failed to open or process exited prematurely.");
                SetState(ConnectionState.Error);
                return;
            }

            SetProgress(100, "V2Ray core ready");
            SetState(ConnectionState.Connected);
            Log($"V2Ray inbound active on 127.0.0.1:{SocksProxyPort} (SOCKS) & 127.0.0.1:{SocksProxyPort + 1} (HTTP)");
        }
        catch (Exception ex)
        {
            if (_runCts?.IsCancellationRequested == true)
            {
                SetState(ConnectionState.Disconnected);
                return;
            }
            _logger.LogError(ex, "Failed to start V2RayEngine");
            Log($"Error starting core: {ex.Message}");
            SetState(ConnectionState.Error);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void CancelConnecting()
    {
        try { _runCts?.Cancel(); } catch { }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _runCts?.Cancel();
            if (_process != null && !_process.HasExited)
            {
                try
                {
                    _process.Kill(true);
                    await _process.WaitForExitAsync();
                }
                catch { }
            }
            _process = null;
            SetState(ConnectionState.Disconnected);
            Log("V2Ray core stopped.");
        }
        finally
        {
            ClearSocksPortOverride();
            _gate.Release();
        }
    }

    private V2RayConfigEntry? ResolveActiveConfig()
    {
        var list = _settings.Settings.V2RayConfigs;
        if (list == null || list.Count == 0) return null;

        var activeId = _settings.Settings.V2RayActiveConfigId;
        var found = list.FirstOrDefault(c => c.Id == activeId);
        return found ?? list.FirstOrDefault(c => c.IsActive) ?? list.FirstOrDefault();
    }

    private (string ExePath, bool IsXray) ResolveCoreExecutable()
    {
        var coreSetting = (_settings.Settings.V2RayCore ?? "xray").ToLowerInvariant();
        var isXray = coreSetting.Contains("xray");

        var appDir = AppDomain.CurrentDomain.BaseDirectory;
        if (isXray)
        {
            var candidate = Path.Combine(appDir, "Resources", "xray", "xray.exe");
            if (File.Exists(candidate)) return (candidate, true);
        }
        else
        {
            var candidate = Path.Combine(appDir, "Resources", "sing_box", "sing-box.exe");
            if (File.Exists(candidate)) return (candidate, false);
        }

        var xrayFallback = Path.Combine(appDir, "Resources", "xray", "xray.exe");
        if (File.Exists(xrayFallback)) return (xrayFallback, true);

        var singboxFallback = Path.Combine(appDir, "Resources", "sing_box", "sing-box.exe");
        if (File.Exists(singboxFallback)) return (singboxFallback, false);

        return ("", true);
    }

    private string GenerateConfigFile(V2RayConfigEntry? config, bool isXray)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dir = Path.Combine(localAppData, "Se7en");
        Directory.CreateDirectory(dir);
        var filePath = Path.Combine(dir, isXray ? "xray_run.json" : "singbox_run.json");

        var s = _settings.Settings;
        var authUser = s.LanAuthEnabled ? s.LanProxyUsername : null;
        var authPass = s.LanAuthEnabled ? s.LanProxyPassword : null;
        var bindAddr = LanExposurePolicy.ResolveBindAddress(s.AllowLanConnections, authUser, authPass, engineEnforcesCredentials: true, out var lanReason);
        var isChainedOuter = TryGetSocksPortOverride().HasValue;
        var listenAddr = isChainedOuter || !bindAddr.Equals(System.Net.IPAddress.Any) ? "127.0.0.1" : "0.0.0.0";
        if (!string.IsNullOrEmpty(lanReason) && s.AllowLanConnections) _logger.LogInformation("[V2Ray] {Reason}", lanReason);
        var hasAuth = s.LanAuthEnabled && !string.IsNullOrWhiteSpace(s.LanProxyUsername) && !string.IsNullOrEmpty(s.LanProxyPassword);

        if (isXray)
        {
            object socksSettings = hasAuth
                ? new { auth = "password", accounts = new[] { new { user = s.LanProxyUsername, pass = s.LanProxyPassword } }, udp = true }
                : new { auth = "noauth", udp = true };

            object httpSettings = hasAuth
                ? new { accounts = new[] { new { user = s.LanProxyUsername, pass = s.LanProxyPassword } } }
                : new { auth = "noauth" };

            var muxEnabled = s.V2RayEnableMux;
            var routeDns = s.V2RayRouteDnsThroughV2Ray;
            var dnsServers = routeDns
                ? new object[] { "https://1.1.1.1/dns-query", "1.1.1.1", "8.8.8.8", "localhost" }
                : new object[] { "localhost" };

            var shouldFragment = (config?.EnableFragment ?? s.V2RayEnableFragment);
            if (shouldFragment)
            {
                Log($"[V2Ray] TLS fragmentation enabled (packets: {s.V2RayFragmentPackets}, length: {s.V2RayFragmentLength}, interval: {s.V2RayFragmentInterval}ms)");
            }

            var xrayInbounds = new List<object>
            {
                new
                {
                    tag = "socks-in",
                    port = SocksProxyPort,
                    listen = listenAddr,
                    protocol = "socks",
                    settings = socksSettings,
                    sniffing = new
                    {
                        enabled = true,
                        destOverride = new[] { "http" },
                        routeOnly = true
                    }
                }
            };
            if (!isChainedOuter)
            {
                xrayInbounds.Add(new
                {
                    tag = "http-in",
                    port = SocksProxyPort + 1,
                    listen = listenAddr,
                    protocol = "http",
                    settings = httpSettings,
                    sniffing = new
                    {
                        enabled = true,
                        destOverride = new[] { "http", "tls", "quic" },
                        routeOnly = false
                    }
                });
            }

            var xrayObj = new Dictionary<string, object>
            {
                ["log"] = new { loglevel = "warning" },
                ["dns"] = new { servers = dnsServers },
                ["inbounds"] = xrayInbounds.ToArray(),
                ["outbounds"] = BuildXrayOutbounds(config, s.ShadowsocksMethod, shouldFragment, s.V2RayFragmentPackets, s.V2RayFragmentLength, s.V2RayFragmentInterval)
            };
            if (muxEnabled)
                xrayObj["mux"] = new { enabled = true, concurrency = 8 };

            File.WriteAllText(filePath, JsonSerializer.Serialize(xrayObj, new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            var shouldFragment = (config?.EnableFragment ?? s.V2RayEnableFragment);
            if (shouldFragment)
            {
                Log("[V2Ray] TLS fragmentation enabled for Sing-Box");
            }
            var singboxInbounds = new List<object>
            {
                hasAuth
                    ? (object)new
                    {
                        type = "socks",
                        tag = "socks-in",
                        listen = listenAddr,
                        listen_port = SocksProxyPort,
                        users = new[] { new { username = s.LanProxyUsername, password = s.LanProxyPassword } }
                    }
                    : new
                    {
                        type = "socks",
                        tag = "socks-in",
                        listen = listenAddr,
                        listen_port = SocksProxyPort
                    }
            };
            if (!isChainedOuter)
            {
                singboxInbounds.Add(hasAuth
                    ? (object)new
                    {
                        type = "http",
                        tag = "http-in",
                        listen = listenAddr,
                        listen_port = SocksProxyPort + 1,
                        users = new[] { new { username = s.LanProxyUsername, password = s.LanProxyPassword } }
                    }
                    : new
                    {
                        type = "http",
                        tag = "http-in",
                        listen = listenAddr,
                        listen_port = SocksProxyPort + 1
                    });
            }

            var muxEnabled2 = s.V2RayEnableMux;
            var routeDns2 = s.V2RayRouteDnsThroughV2Ray;
            var singboxDnsServers = routeDns2
                ? new object[]
                {
                    new { type = "https", tag = "remote", server = "1.1.1.1", path = "/dns-query" },
                    new { type = "udp", tag = "remote2", server = "1.1.1.1" },
                    new { type = "local", tag = "local" }
                }
                : new object[]
                {
                    new { type = "local", tag = "local" }
                };
            var singboxObj = new Dictionary<string, object>
            {
                ["log"] = new { level = "warn" },
                ["dns"] = new { servers = singboxDnsServers },
                ["inbounds"] = singboxInbounds,
                ["outbounds"] = BuildSingBoxOutbounds(config, s.ShadowsocksMethod, shouldFragment),
                ["route"] = new
                {
                    default_domain_resolver = "local",
                    rules = new object[]
                    {
                        new { action = "sniff" }
                    }
                }
            };
            if (muxEnabled2 && singboxObj["outbounds"] is object[] arr)
                foreach (var ob in arr)
                    if (ob is Dictionary<string, object> d && d.TryGetValue("tag", out var t) && t as string == "proxy")
                        d["multiplex"] = new { enabled = true, max_connections = 8 };

            File.WriteAllText(filePath, JsonSerializer.Serialize(singboxObj, new JsonSerializerOptions { WriteIndented = true }));
        }

        return filePath;
    }

    private static readonly HashSet<string> ValidSsMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "aes-128-gcm","aes-256-gcm","chacha20-poly1305","xchacha20-poly1305",
        "2022-blake3-aes-128-gcm","2022-blake3-aes-256-gcm","2022-blake3-chacha20-poly1305",
        "none","plain"
    };

    private static string ResolveSsMethod(V2RayConfigEntry? c, string? globalMethod)
    {

        string? candidate = null;
        if (!string.IsNullOrWhiteSpace(globalMethod) && ValidSsMethods.Contains(globalMethod.Trim()))
            candidate = globalMethod!.Trim();
        var sec = c?.Security?.Trim();
        if (candidate == null && !string.IsNullOrWhiteSpace(sec) && ValidSsMethods.Contains(sec!))
            candidate = sec;
        return candidate ?? "2022-blake3-aes-128-gcm";
    }

    private static object[] BuildXrayOutbounds(
        V2RayConfigEntry? c,
        string? globalSsMethod = null,
        bool enableFragment = false,
        string fragmentPackets = "tlshello",
        string fragmentLength = "100-200",
        string fragmentInterval = "10-20")
    {
        if (c == null || string.IsNullOrWhiteSpace(c.Address))
        {
            return new object[]
            {
                new { tag = "direct", protocol = "freedom", settings = new { } }
            };
        }

        var proto = (c.Protocol ?? "vless").ToLowerInvariant();
        var network = string.IsNullOrWhiteSpace(c.Network) ? "tcp" : c.Network.ToLowerInvariant();
        var security = string.IsNullOrWhiteSpace(c.Security) ? "none" : c.Security.ToLowerInvariant();

        var streamSettings = new Dictionary<string, object>
        {
            ["network"] = (network is "xhttp" or "splithttp") ? "xhttp" : network,
            ["security"] = security
        };

        if (enableFragment)
        {
            streamSettings["sockopt"] = new
            {
                dialerProxy = "fragment",
                tcpNoDelay = true
            };
        }

        if (security == "reality")
        {
            var sni = string.IsNullOrWhiteSpace(c.Sni) ? c.Host ?? c.Address : c.Sni;
            streamSettings["realitySettings"] = new
            {
                serverName = sni,
                publicKey = c.PublicKey ?? "",
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
            else if (network != "ws")
            {
                tlsDict["alpn"] = new[] { "http/1.1" };
            }
            streamSettings["tlsSettings"] = tlsDict;
        }

        if (network is "xhttp" or "splithttp")
        {
            var xhttpHost = !string.IsNullOrWhiteSpace(c.Host) ? c.Host : (!string.IsNullOrWhiteSpace(c.Sni) ? c.Sni : c.Address);
            streamSettings["xhttpSettings"] = new
            {
                path = c.Path ?? "/",
                host = xhttpHost,
                mode = "auto"
            };
            streamSettings["splithttpSettings"] = new
            {
                path = c.Path ?? "/",
                host = xhttpHost,
                mode = "auto"
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
        else if (network is "http" or "h2")
        {
            var h2Host = !string.IsNullOrWhiteSpace(c.Host) ? c.Host : (!string.IsNullOrWhiteSpace(c.Sni) ? c.Sni : c.Address);
            streamSettings["httpSettings"] = new
            {
                path = c.Path ?? "/",
                host = new[] { h2Host }
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
                if (!string.Equals(cleanFlow, "none", StringComparison.OrdinalIgnoreCase) && network == "tcp")
                {
                    userObj["flow"] = cleanFlow;
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
            new
            {
                tag = "proxy",
                protocol = (proto is "shadowsocks" or "ss") ? "shadowsocks" : proto,
                settings = outboundSettings,
                streamSettings = streamSettings
            }
        };

        if (enableFragment)
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

    private static object[] BuildSingBoxOutbounds(V2RayConfigEntry? c, string? globalSsMethod = null, bool enableFragment = false)
    {
        if (c == null || string.IsNullOrWhiteSpace(c.Address))
        {
            return new object[]
            {
                new { type = "direct", tag = "direct" }
            };
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
                if (!string.Equals(cleanFlow, "none", StringComparison.OrdinalIgnoreCase) && (c.Network == "tcp" || string.IsNullOrEmpty(c.Network)))
                {
                    outb["flow"] = cleanFlow;
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

        if (c.Security == "tls" || c.Security == "reality")
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
            if (c.Security == "reality")
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
            if (enableFragment)
            {
                tls["fragment"] = true;
            }
            outb["tls"] = tls;
        }

        if (c.Network == "ws")
        {
            outb["transport"] = new
            {
                type = "ws",
                path = c.Path ?? "/",
                headers = new { Host = !string.IsNullOrWhiteSpace(c.Host) ? c.Host : (!string.IsNullOrWhiteSpace(c.Sni) ? c.Sni : c.Address) }
            };
        }
        else if (c.Network is "xhttp" or "splithttp" or "http")
        {
            outb["transport"] = new
            {
                type = "http",
                path = c.Path ?? "/",
                host = new[] { !string.IsNullOrWhiteSpace(c.Host) ? c.Host : (!string.IsNullOrWhiteSpace(c.Sni) ? c.Sni : c.Address) }
            };
        }
        else if (c.Network == "grpc")
        {
            outb["transport"] = new
            {
                type = "grpc",
                service_name = c.Path ?? ""
            };
        }

        return new object[]
        {
            outb,
            new { type = "direct", tag = "direct" }
        };
    }

    private static string NormalizeRealityPublicKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return "";

        return key.Trim().Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private static readonly SemaphoreSlim _testThrottle = new(4, 4);

    public async Task<int> MeasureRealDelayAsync(V2RayConfigEntry? config, CancellationToken ct = default)
    {
        if (config == null || string.IsNullOrWhiteSpace(config.Address) || config.Port <= 0)
            return -1;

        if (State == ConnectionState.Connected && ResolveActiveConfig()?.Id == config.Id)
        {
            return await ProbeUrlThroughProxyAsync(SocksProxyPort, ct);
        }

        await _testThrottle.WaitAsync(ct);
        try
        {
            return await RunIsolatedTestAsync(config, ct);
        }
        finally
        {
            _testThrottle.Release();
        }
    }

    private async Task<int> RunIsolatedTestAsync(V2RayConfigEntry config, CancellationToken ct)
    {
        var (exePath, isXray) = ResolveCoreExecutable();
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
        {
            return -1;
        }

        var (testSocksPort, testHttpPort) = GetFreeLoopbackPorts();
        var tempConfigFile = Path.Combine(Path.GetTempPath(), $"se7en_test_{Guid.NewGuid():N}.json");

        try
        {
            if (isXray)
            {
                var s = _settings.Settings;
                var shouldFragment = config.EnableFragment ?? s.V2RayEnableFragment;
                var xrayTestObj = new Dictionary<string, object>
                {
                    ["log"] = new { loglevel = "none" },
                    ["dns"] = new
                    {
                        servers = new object[]
                        {
                            "localhost",
                            "8.8.8.8",
                            "1.1.1.1"
                        }
                    },
                    ["inbounds"] = new object[]
                    {
                        new
                        {
                            tag = "socks-test",
                            port = testSocksPort,
                            listen = "127.0.0.1",
                            protocol = "socks",
                            settings = new { auth = "noauth", udp = true },
                            sniffing = new
                            {
                                enabled = true,
                                destOverride = new[] { "http" },
                                routeOnly = true
                            }
                        },
                        new
                        {
                            tag = "http-test",
                            port = testHttpPort,
                            listen = "127.0.0.1",
                            protocol = "http",
                            settings = new { auth = "noauth" }
                        }
                    },
                    ["outbounds"] = BuildXrayOutbounds(config, s.ShadowsocksMethod, shouldFragment, s.V2RayFragmentPackets, s.V2RayFragmentLength, s.V2RayFragmentInterval)
                };
                File.WriteAllText(tempConfigFile, JsonSerializer.Serialize(xrayTestObj));
            }
            else
            {
                var s = _settings.Settings;
                var shouldFragment = config.EnableFragment ?? s.V2RayEnableFragment;
                var singboxTestObj = new Dictionary<string, object>
                {
                    ["log"] = new { level = "warn" },
                    ["dns"] = new
                    {
                        servers = new object[]
                        {
                            new { type = "local", tag = "local" },
                            new { type = "udp", tag = "remote", server = "8.8.8.8" },
                            new { type = "udp", tag = "remote2", server = "1.1.1.1" }
                        }
                    },
                    ["route"] = new
                    {
                        default_domain_resolver = "local",
                        rules = new object[]
                        {
                            new { action = "sniff" }
                        }
                    },
                    ["inbounds"] = new object[]
                    {
                        new
                        {
                            type = "socks",
                            tag = "socks-test",
                            listen = "127.0.0.1",
                            listen_port = testSocksPort
                        },
                        new
                        {
                            type = "http",
                            tag = "http-test",
                            listen = "127.0.0.1",
                            listen_port = testHttpPort
                        }
                    },
                    ["outbounds"] = BuildSingBoxOutbounds(config, s.ShadowsocksMethod, shouldFragment)
                };
                File.WriteAllText(tempConfigFile, JsonSerializer.Serialize(singboxTestObj));
            }

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = $"run -c \"{tempConfigFile}\"",
                WorkingDirectory = Path.GetDirectoryName(exePath)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            if (!isXray)
            {
                psi.EnvironmentVariables["ENABLE_DEPRECATED_LEGACY_DNS_SERVERS"] = "true";
                psi.EnvironmentVariables["ENABLE_DEPRECATED_MISSING_DOMAIN_RESOLVER"] = "true";
            }

            using var proc = new Process { StartInfo = psi };
            var stderrBuilder = new StringBuilder();
            proc.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                    stderrBuilder.AppendLine(e.Data);
            };
            proc.Start();
            proc.BeginErrorReadLine();
            _childGuard.Adopt(proc);

            try
            {

                var ready = false;
                for (var i = 0; i < 60; i++)
                {
                    if (proc.HasExited) break;
                    try
                    {
                        using var probe = new TcpClient();
                        using var probeCts = new CancellationTokenSource(50);
                        await probe.ConnectAsync(IPAddress.Loopback, testSocksPort, probeCts.Token);
                        ready = true;
                        break;
                    }
                    catch
                    {
                        await Task.Delay(50, ct);
                    }
                }

                if (!ready || proc.HasExited)
                {
                    if (stderrBuilder.Length > 0)
                    {
                        _logger.LogWarning("[V2Ray Ping] Core {Exe} failed or exited before port {Port}: {Error}",
                            Path.GetFileName(exePath), testSocksPort, stderrBuilder.ToString().Trim());
                    }
                    return -3;
                }

                return await ProbeUrlThroughProxyAsync(testSocksPort, ct);
            }
            finally
            {
                if (!proc.HasExited)
                {
                    try { proc.Kill(true); } catch { }
                    try { await proc.WaitForExitAsync(); } catch { }
                }
            }
        }
        catch (OperationCanceledException)
        {
            return -3;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to run isolated real delay test");
            return -3;
        }
        finally
        {
            try
            {
                if (File.Exists(tempConfigFile)) File.Delete(tempConfigFile);
            }
            catch { }
        }
    }

    private static (int socksPort, int httpPort) GetFreeLoopbackPorts()
    {
        using var l1 = new TcpListener(IPAddress.Loopback, 0);
        l1.Start();
        var p1 = ((IPEndPoint)l1.LocalEndpoint).Port;

        using var l2 = new TcpListener(IPAddress.Loopback, 0);
        l2.Start();
        var p2 = ((IPEndPoint)l2.LocalEndpoint).Port;

        l1.Stop();
        l2.Stop();
        return (p1, p2);
    }

    private static async Task<int> ProbeUrlThroughProxyAsync(int socksPort, CancellationToken ct)
    {
        var targetUrls = new[]
        {
            "http://www.google.com/generate_204",
            "http://www.gstatic.com/generate_204",
            "http://cp.cloudflare.com/generate_204"
        };

        foreach (var url in targetUrls)
        {
            if (ct.IsCancellationRequested) break;
            var sw = Stopwatch.StartNew();
            try
            {
                using var handler = new SocketsHttpHandler
                {
                    Proxy = new WebProxy($"socks5://127.0.0.1:{socksPort}"),
                    UseProxy = true,
                    ConnectTimeout = TimeSpan.FromSeconds(3.5),
                    PooledConnectionLifetime = TimeSpan.FromSeconds(1)
                };
                using var client = new HttpClient(handler)
                {
                    Timeout = TimeSpan.FromSeconds(4.5)
                };

                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                linkedCts.CancelAfter(TimeSpan.FromSeconds(4.5));

                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
                var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token);
                sw.Stop();

                if (resp.IsSuccessStatusCode || (int)resp.StatusCode == 204)
                {
                    return Math.Max(1, (int)sw.ElapsedMilliseconds);
                }
            }
            catch { }
        }

        try
        {
            var sw = Stopwatch.StartNew();
            using var tcp = new TcpClient();
            using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            pingCts.CancelAfter(TimeSpan.FromSeconds(3.5));
            await tcp.ConnectAsync(IPAddress.Loopback, socksPort, pingCts.Token);
            using var stream = tcp.GetStream();

            await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, pingCts.Token);
            var greet = new byte[2];
            await stream.ReadExactlyAsync(greet, pingCts.Token);
            if (greet[0] == 0x05 && greet[1] == 0x00)
            {

                var connectReq = new byte[] { 0x05, 0x01, 0x00, 0x01, 8, 8, 8, 8, 0x00, 0x35 };
                await stream.WriteAsync(connectReq, pingCts.Token);
                var reply = new byte[4];
                await stream.ReadExactlyAsync(reply, pingCts.Token);
                if (reply[1] == 0x00)
                {
                    sw.Stop();
                    return Math.Max(1, (int)sw.ElapsedMilliseconds);
                }
            }
        }
        catch { }

        return -3;
    }

    public void Dispose()
    {
        _runCts?.Cancel();
        try
        {
            _process?.Kill(true);
            _process?.Dispose();
        }
        catch { }
        _gate.Dispose();
    }
}
