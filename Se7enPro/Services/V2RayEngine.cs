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
    private int _processGeneration;

        private void OnProcessExited(int generation)
    {
        if (generation != _processGeneration) return;

        Log("V2Ray core process exited.");

        var p = _process;
        if (p is not null)
        {
            _process = null;
            try { p.CancelOutputRead(); } catch { }
            try { p.CancelErrorRead(); } catch { }
            try { p.Dispose(); } catch { }
        }

        if (State != ConnectionState.Disconnected)
        {
            SetState(ConnectionState.Disconnected);
        }
    }

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

    
    
    
    private static string? _userUpstreamOverride;
    internal static void SetUserUpstreamOverride(string? url)
    {
        lock (_overrideLock) _userUpstreamOverride = string.IsNullOrWhiteSpace(url) ? null : url.Trim();
    }
    internal static void ClearUserUpstreamOverride() { lock (_overrideLock) _userUpstreamOverride = null; }
    internal static string? TryGetUserUpstreamOverride() { lock (_overrideLock) return _userUpstreamOverride; }

    private static bool _suppressSettingsUpstream;
    internal static void SetSuppressSettingsUpstream(bool suppress)
    {
        lock (_overrideLock) _suppressSettingsUpstream = suppress;
    }

        private static string? ResolveUserUpstreamUrl(Models.UserSettings s)
    {
        var url = TryGetUserUpstreamOverride();
        if (string.IsNullOrEmpty(url) && !_suppressSettingsUpstream && !TryGetSocksPortOverride().HasValue)
        {
            url = TunnelCoreManager.BuildUpstreamProxyUrl(s);
        }
        if (string.IsNullOrEmpty(url)) return null;
        if (url.StartsWith("socks5h://", StringComparison.OrdinalIgnoreCase))
        {
            url = "socks5://" + url["socks5h://".Length..];
        }
        return url;
    }

    public int SocksProxyPort => TryGetSocksPortOverride() ??
        (_settings.Settings.UseCustomProxyPorts && _settings.Settings.LocalSocksProxyPort > 0
            ? _settings.Settings.LocalSocksProxyPort
            : (_settings.Settings.V2RayInboundPort > 0 ? _settings.Settings.V2RayInboundPort : 10808));
    public int HttpProxyPort => TryGetSocksPortOverride().HasValue
        ? TryGetSocksPortOverride()!.Value + 1
        : (_settings.Settings.UseCustomProxyPorts && _settings.Settings.LocalHttpProxyPort > 0
            ? _settings.Settings.LocalHttpProxyPort
            : (SocksProxyPort + 1));
    private string? _detectedCountry;
    private string? _detectedIp;
    private string? _detectedCity;

    public string ClientRegion => "";
    public string ConnectedServerRegion => _detectedCountry ?? "";
    public string CurrentRouteIp
    {
        get
        {
            if (!string.IsNullOrEmpty(_detectedIp)) return _detectedIp;
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
    public IReadOnlyList<string> CoreProcessNames => OperatingSystem.IsWindows()
        ? new[] { "xray.exe", "sing-box.exe" }
        : new[] { "xray", "sing-box", "xray.exe", "sing-box.exe" };

#pragma warning disable CS0067
    public event EventHandler<ConnectionState>? StateChanged;
    public event EventHandler<Notice>? NoticeReceived;
    public event EventHandler<string>? LogLineAppended;
    public event EventHandler? BytesTransferredChanged;
    public event EventHandler? RouteChanged;
    public event EventHandler? ConnectProgressChanged;
#pragma warning restore CS0067

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

            _detectedCountry = null;
            _detectedIp = null;
            _detectedCity = null;

            _runCts?.Cancel();
            _runCts = new CancellationTokenSource();

            var config = ResolveActiveConfig();
            var configProblem = ValidateOutboundNode(config);
            if (configProblem is not null)
            {
                // Never fall through to a `freedom`/direct outbound here. The core would
                // happily open its SOCKS inbound, the readiness probe would succeed against
                // that local port, and the app would report a live, encrypted tunnel while
                // every byte went out directly with the user's real IP.
                Log($"Cannot start V2Ray/Sing-box: {configProblem}");
                _logger.LogError("V2Ray start refused: {Problem}", configProblem);
                SetState(ConnectionState.Error);
                return;
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
            psi.EnvironmentVariables["GOMEMLIMIT"] = "40MiB";
            psi.EnvironmentVariables["GODEBUG"] = "madvdontneed=1";
            if (!isXray)
            {
                psi.EnvironmentVariables["ENABLE_DEPRECATED_LEGACY_DNS_SERVERS"] = "true";
                psi.EnvironmentVariables["ENABLE_DEPRECATED_MISSING_DOMAIN_RESOLVER"] = "true";
            }

            
            
            
            
            
            var generation = ++_processGeneration;

            _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _process.OutputDataReceived += (_, e) =>
            {
                
                
                
                
                
                if (!string.IsNullOrWhiteSpace(e.Data)) Log(LogSanitizer.Scrub(e.Data));
            };
            _process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data)) Log(LogSanitizer.Scrub(e.Data));
            };
            _process.Exited += (_, _) => OnProcessExited(generation);

            _process.Start();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            _childGuard.Adopt(_process);

            
            
            
            
            var portReady = await SocksProbe.WaitForTunnelAsync(
                SocksProxyPort, DateTime.UtcNow + TimeSpan.FromSeconds(8),
                _runCts?.Token ?? CancellationToken.None);

            if (portReady is null || _process == null || _process.HasExited)
            {
                if (_runCts?.IsCancellationRequested == true)
                {
                    SetState(ConnectionState.Disconnected);
                    return;
                }

                
                
                
                
                
                Log($"V2Ray inbound port {SocksProxyPort} failed to open or process exited prematurely.");
                var doomed = _process;
                _process = null;
                if (doomed is not null)
                {
                    try { if (!doomed.HasExited) doomed.Kill(entireProcessTree: true); } catch { }
                    try { doomed.CancelOutputRead(); } catch { }
                    try { doomed.CancelErrorRead(); } catch { }
                    try { doomed.Dispose(); } catch { }
                }
                SetState(ConnectionState.Error);
                return;
            }

            SetProgress(100, "V2Ray core ready");
            SetState(ConnectionState.Connected);
            Log($"V2Ray inbound active on 127.0.0.1:{SocksProxyPort} (SOCKS) & 127.0.0.1:{HttpProxyPort} (HTTP)");
            StartLocationProbe(SocksProxyPort, _runCts?.Token ?? CancellationToken.None);
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

    private void StartLocationProbe(int socksPort, CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await EndpointProbeHelper.ProbeAsync(socksPort, ct);
                if (result != null)
                {
                    _detectedCountry = result.CountryCode;
                    _detectedIp = result.Ip;
                    _detectedCity = result.CityOrColo;
                    var locationDetail = !string.IsNullOrEmpty(result.CityOrColo)
                        ? $"{CountryHelper.FullName(result.CountryCode)} ({result.CityOrColo})"
                        : CountryHelper.FullName(result.CountryCode);
                    Log($"[Location] V2Ray exit route confirmed: {result.Ip} — {locationDetail}");
                    try { RouteChanged?.Invoke(this, EventArgs.Empty); } catch { }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "V2Ray exit probe failed");
            }
        }, ct);
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
            _detectedCountry = null;
            _detectedIp = null;
            _detectedCity = null;

            _processGeneration++;
            _runCts?.Cancel();
            var proc = _process;
            _process = null;
            if (proc is not null && !proc.HasExited)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                
                
                
                
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await proc.WaitForExitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning("V2Ray core did not exit within 5s after Kill; continuing");
                }
                catch { }
            }

            if (proc is not null)
            {
                try { proc.CancelOutputRead(); } catch { }
                try { proc.CancelErrorRead(); } catch { }
                try { proc.Dispose(); } catch { }
            }

            SetState(ConnectionState.Disconnected);
            Log("V2Ray core stopped.");
        }
        finally
        {
            ClearSocksPortOverride();
            _gate.Release();
        }
    }

    public V2RayConfigEntry? ResolveActiveConfig()
    {
        var list = _settings.Settings.V2RayConfigs;
        if (list == null || list.Count == 0) return null;

        var activeId = _settings.Settings.V2RayActiveConfigId;
        var found = list.FirstOrDefault(c => c.Id == activeId);
        return found ?? list.FirstOrDefault(c => c.IsActive) ?? list.FirstOrDefault();
    }

    /// <summary>
    /// Returns null when the node can actually carry traffic, otherwise a short
    /// human-readable reason. Used to refuse a start instead of silently degrading
    /// to a direct (unencrypted) outbound.
    /// </summary>
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

    private (string ExePath, bool IsXray) ResolveCoreExecutable()
    {
        var coreSetting = (_settings.Settings.V2RayCore ?? "xray").ToLowerInvariant();
        var isXray = coreSetting.Contains("xray");

        var appDir = AppDomain.CurrentDomain.BaseDirectory;
        var xrayBin = OperatingSystem.IsWindows() ? "xray.exe" : "xray";
        var singboxBin = OperatingSystem.IsWindows() ? "sing-box.exe" : "sing-box";

        if (isXray)
        {
            var candidate = Path.Combine(appDir, "Resources", "xray", xrayBin);
            if (File.Exists(candidate)) return (candidate, true);
            var candidateWin = Path.Combine(appDir, "Resources", "xray", "xray.exe");
            if (File.Exists(candidateWin)) return (candidateWin, true);
        }
        else
        {
            var candidate = Path.Combine(appDir, "Resources", "sing_box", singboxBin);
            if (File.Exists(candidate)) return (candidate, false);
            var candidateWin = Path.Combine(appDir, "Resources", "sing_box", "sing-box.exe");
            if (File.Exists(candidateWin)) return (candidateWin, false);
        }

        
        var xrayFallback = Path.Combine(appDir, "Resources", "xray", xrayBin);
        if (File.Exists(xrayFallback)) return (xrayFallback, true);
        var xrayFallbackWin = Path.Combine(appDir, "Resources", "xray", "xray.exe");
        if (File.Exists(xrayFallbackWin)) return (xrayFallbackWin, true);

        var singboxFallback = Path.Combine(appDir, "Resources", "sing_box", singboxBin);
        if (File.Exists(singboxFallback)) return (singboxFallback, false);
        var singboxFallbackWin = Path.Combine(appDir, "Resources", "sing_box", "sing-box.exe");
        if (File.Exists(singboxFallbackWin)) return (singboxFallbackWin, false);

        return ("", true);
    }

    /// <summary>
    /// Builds the resolver plan for the current session.
    ///
    /// <paramref name="hasV6Address"/> mirrors whether a v6 address exists on the tunnel
    /// adapter. A v6 resolver is only advertised when one does, because handing a core a
    /// v6-only resolver on a v4-only tunnel makes every lookup fail on connect.
    /// </summary>
    private DnsResolverPolicy.Plan BuildDnsPlan(UserSettings s) =>
        new DnsResolverPolicy(_logger).Build(s, hasV6Address: HasV6TunnelAddress());

    private bool HasV6TunnelAddress()
    {
        try
        {
            return System.Net.NetworkInformation.NetworkInterface
                .GetAllNetworkInterfaces()
                .Any(ni =>
                {
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
                        return false;
                    if (!ni.Description.Contains("Wintun", StringComparison.OrdinalIgnoreCase))
                        return false;

                    try
                    {
                        return ni.GetIPProperties().UnicastAddresses.Any(
                            a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6);
                    }
                    catch { return false; }
                });
        }
        catch
        {
            // Detection is an optimisation for the default v6 resolver only, so a failure
            // must not prevent the v4 resolvers from being configured.
            return false;
        }
    }

    private static bool HasCustomResolvers(UserSettings s) =>
        !string.IsNullOrWhiteSpace(s.CustomDnsUdp)
        || !string.IsNullOrWhiteSpace(s.CustomDnsDot)
        || !string.IsNullOrWhiteSpace(s.CustomDnsDoh);

    private string GenerateConfigFile(V2RayConfigEntry? config, bool isXray)
    {
        // Defence in depth: StartAsync validates first, but this method must never be
        // able to emit a direct (freedom) config no matter who calls it.
        var problem = ValidateOutboundNode(config);
        if (problem is not null)
        {
            throw new InvalidOperationException(problem);
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dir = Path.Combine(localAppData, "Se7en");
        Directory.CreateDirectory(dir);
        var filePath = Path.Combine(dir, isXray ? "xray_run.json" : "singbox_run.json");

        var s = _settings.Settings;
        var hasAuth = s.LanAuthEnabled && !string.IsNullOrWhiteSpace(s.LanProxyUsername) && !string.IsNullOrEmpty(s.LanProxyPassword);
        var isChainedOuter = TryGetSocksPortOverride().HasValue;
        var listenAddr = isChainedOuter || !s.AllowLanConnections ? "127.0.0.1" : "0.0.0.0";

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

            // When DNS is routed through the tunnel, the user's resolver list leads and
            // the built-in public resolvers follow as the fallback. That order is the
            // whole point of the setting: a custom resolver appended AFTER 1.1.1.1 is
            // never asked, which is the defect this feature exists to fix.
            var dnsServers = routeDns
                ? DnsResolverPolicy.ToXrayDnsServers(BuildDnsPlan(s))
                : new List<object> { "localhost" };

            if (routeDns && HasCustomResolvers(s))
            {
                Log($"[V2Ray] custom DNS: {dnsServers.Count - 1} resolver(s) configured, "
                    + "used in order with the built-in defaults as fallback");
            }

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

            var xrayOutbounds = BuildXrayOutbounds(config, s.ShadowsocksMethod, shouldFragment, s.V2RayFragmentPackets, s.V2RayFragmentLength, s.V2RayFragmentInterval, ResolveUserUpstreamUrl(s));
            var xrayObj = new Dictionary<string, object>
            {
                ["log"] = new { loglevel = "warning" },
                ["dns"] = new { servers = dnsServers },
                ["inbounds"] = xrayInbounds.ToArray(),
                ["outbounds"] = xrayOutbounds
            };
            
            
            if (muxEnabled)
                foreach (var ob in xrayOutbounds)
                    if (ob is Dictionary<string, object> d && d.TryGetValue("tag", out var t) && t as string == "proxy")
                        d["mux"] = new { enabled = true, concurrency = 8 };

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

            // sing-box wants a structured object per resolver rather than a scheme
            // string, so each configured entry is expanded into its own transport shape.
            var singboxDnsServers = routeDns2
                ? DnsResolverPolicy.ToSingBoxDnsServers(BuildDnsPlan(s))
                : new List<object> { new { type = "local", tag = "local" } };

            if (routeDns2 && HasCustomResolvers(s))
            {
                Log($"[V2Ray] custom DNS: {singboxDnsServers.Count - 1} resolver(s) configured, "
                    + "used in order with the built-in defaults as fallback");
            }
            var singboxObj = new Dictionary<string, object>
            {
                ["log"] = new { level = "warn" },
                ["dns"] = new { servers = singboxDnsServers },
                ["inbounds"] = singboxInbounds,
                ["outbounds"] = BuildSingBoxOutbounds(config, s.ShadowsocksMethod, shouldFragment, ResolveUserUpstreamUrl(s)),
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

    private static readonly HashSet<string> ValidStreamSecurity =
        new(StringComparer.OrdinalIgnoreCase) { "none", "tls", "reality" };

    private static readonly HashSet<string> ValidFlows =
        new(StringComparer.OrdinalIgnoreCase) { "xtls-rprx-vision", "xtls-rprx-vision-udp443" };

        private static string NormalizeStreamSecurity(string? raw)
    {
        var v = (raw ?? "").Trim().ToLowerInvariant();
        if (v.Length == 0) return "none";
        if (ValidStreamSecurity.Contains(v)) return v;

        return "none";
    }

    private static object[] BuildXrayOutbounds(
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
            // Refuse to synthesise a `freedom`/direct outbound. That produced a
            // "connected" but completely unprotected session whenever a node had
            // no address. Callers must validate first (see ValidateOutboundNode).
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
            else if (network != "ws")
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

        private static object? BuildXrayUpstreamOutbound(string url)
    {
        if (!TryParseUpstreamUri(url, out var uri, out var user, out var pass))
        {
            return null;
        }
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

    private static object[] BuildSingBoxOutbounds(V2RayConfigEntry? c, string? globalSsMethod = null, bool enableFragment = false, string? userUpstreamUrl = null)
    {
        if (c == null || string.IsNullOrWhiteSpace(c.Address))
        {
            // Same fail-closed rule as BuildXrayOutbounds: never fall back to a direct
            // outbound, which yields a "connected" but unprotected session.
            throw new InvalidOperationException(
                ValidateOutboundNode(c)
                ?? "The selected node cannot carry traffic; refusing to build a direct (unprotected) config.");
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
                    outb["flow"] = cleanFlow.ToLowerInvariant();
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

        private static object? BuildSingBoxUpstreamOutbound(string url)
    {
        if (!TryParseUpstreamUri(url, out var uri, out var user, out var pass))
        {
            return null;
        }
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
        var nodeProblem = ValidateOutboundNode(config);
        if (nodeProblem is not null)
        {
            // Building a test config would throw out of BuildXrayOutbounds; refuse up
            // front so the "live test" button reports the real reason instead of -3.
            Log($"Cannot test this node: {nodeProblem}");
            return -1;
        }

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
                    ["log"] = new { loglevel = "warning" },
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
            psi.EnvironmentVariables["GOMEMLIMIT"] = "32MiB";
            psi.EnvironmentVariables["GODEBUG"] = "madvdontneed=1";
            if (!isXray)
            {
                psi.EnvironmentVariables["ENABLE_DEPRECATED_LEGACY_DNS_SERVERS"] = "true";
                psi.EnvironmentVariables["ENABLE_DEPRECATED_MISSING_DOMAIN_RESOLVER"] = "true";
            }

            using var proc = new Process { StartInfo = psi };
            var coreOutput = new StringBuilder();
            
            
            
            
            void OnCoreLine(object? sender, DataReceivedEventArgs e)
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                    lock (coreOutput) coreOutput.AppendLine(e.Data);
            }
            proc.ErrorDataReceived += OnCoreLine;
            proc.OutputDataReceived += OnCoreLine;
            proc.Start();
            proc.BeginErrorReadLine();
            proc.BeginOutputReadLine();
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
                    string captured;
                    lock (coreOutput) captured = coreOutput.ToString().Trim();
                    
                    
                    
                    if (captured.Length > 0)
                    {
                        _logger.LogWarning("[V2Ray Ping] Core {Exe} failed or exited before port {Port}: {Error}",
                            Path.GetFileName(exePath), testSocksPort, captured);
                        Log($"[V2Ray Ping] {Path.GetFileName(exePath)} could not start for this node: {captured}");
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
            "http://cp.cloudflare.com/generate_204",
            "https://www.gstatic.com/generate_204"
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
