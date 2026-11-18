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
        DeleteRunConfigs();

        if (State == ConnectionState.Connected)
        {
            SetState(ConnectionState.Error);
        }
        else if (State != ConnectionState.Disconnected)
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

    private int _runtimeSocksPortOverride;
    private volatile bool _activeCoreIsXray = true;
    internal bool ActiveCoreIsXray => _activeCoreIsXray;

    public int SocksProxyPort => State is ConnectionState.Connected or ConnectionState.Connecting
        ? (TryGetSocksPortOverride() ??
            (_runtimeSocksPortOverride > 0
                ? _runtimeSocksPortOverride
                : (_settings.Settings.UseCustomProxyPorts && _settings.Settings.LocalSocksProxyPort > 0
                    ? _settings.Settings.LocalSocksProxyPort
                    : GetCoreInboundPort(_settings.Settings, _activeCoreIsXray))))
        : 0;

    internal static int GetCoreInboundPort(UserSettings s, bool isXray)
        => V2RayConfigBuilder.GetCoreInboundPort(s, isXray);
    public int HttpProxyPort => SocksProxyPort == 0
        ? 0
        : (TryGetSocksPortOverride().HasValue
            ? TryGetSocksPortOverride()!.Value + 1
            : (_runtimeSocksPortOverride > 0
                ? _runtimeSocksPortOverride + 1
                : (_settings.Settings.UseCustomProxyPorts && _settings.Settings.LocalHttpProxyPort > 0
                    ? _settings.Settings.LocalHttpProxyPort
                    : (SocksProxyPort + 1))));
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
    public string ServerEndpointAddress => ResolveActiveConfig()?.Address?.Trim() ?? "";
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
                Log($"Cannot start V2Ray/Sing-box: {configProblem}");
                _logger.LogError("V2Ray start refused: {Problem}", configProblem);
                SetProgress(0, $"Cannot start V2Ray: {configProblem}");
                SetState(ConnectionState.Error);
                return;
            }

            var (exePath, isXray) = ResolveCoreExecutable(config);
            _activeCoreIsXray = isXray;
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
            {
                Log($"V2Ray/Sing-box executable not found at {exePath}");
                SetProgress(0, $"V2Ray/Sing-box executable not found at {exePath}");
                SetState(ConnectionState.Error);
                return;
            }

            DeleteRunConfigs();
            KillOrphanProcesses();

            _runtimeSocksPortOverride = 0;
            if (!TryGetSocksPortOverride().HasValue)
            {
                var desiredSocks = SocksProxyPort;
                var desiredHttp = HttpProxyPort;
                if (!IsPortAvailable(desiredSocks) || !IsPortAvailable(desiredHttp))
                {
                    if (_settings.Settings.UseCustomProxyPorts &&
                        (_settings.Settings.LocalSocksProxyPort > 0 || _settings.Settings.LocalHttpProxyPort > 0))
                    {
                        Log($"Warning: configured V2Ray inbound port ({desiredSocks}/{desiredHttp}) is already in use by another process.");
                    }
                    else
                    {
                        var (freeSocks, _) = GetFreeLoopbackPorts();
                        _runtimeSocksPortOverride = freeSocks;
                        Log($"Default V2Ray port {desiredSocks} was in use; switched to free port {freeSocks}.");
                    }
                }
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
            psi.EnvironmentVariables["GOMEMLIMIT"] = "128MiB";
            psi.EnvironmentVariables["GODEBUG"] = "madvdontneed=1";
            if (!isXray)
            {
                psi.EnvironmentVariables["ENABLE_DEPRECATED_LEGACY_DNS_SERVERS"] = "true";
                psi.EnvironmentVariables["ENABLE_DEPRECATED_MISSING_DOMAIN_RESOLVER"] = "true";
                psi.EnvironmentVariables["ENABLE_DEPRECATED_SPECIAL_OUTBOUNDS"] = "true";
                psi.EnvironmentVariables["ENABLE_DEPRECATED_LEGACY_DOMAIN_STRATEGY_OPTIONS"] = "true";
                psi.EnvironmentVariables["ENABLE_DEPRECATED_OUTBOUND_DNS_RULE_ITEM"] = "true";
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

            
            
            
            
            var portReady = await WaitForInboundPortAsync(
                SocksProxyPort, TimeSpan.FromSeconds(10),
                _runCts?.Token ?? CancellationToken.None);

            if (!portReady || _process == null || _process.HasExited)
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
                DeleteRunConfigs();
                return;
            }

            SetProgress(100, "V2Ray core ready");
            SetState(ConnectionState.Connected);
            Log($"V2Ray inbound active on {SocksProxyPort} (SOCKS) & {HttpProxyPort} (HTTP)");
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
            DeleteRunConfigs();
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool IsPortAvailable(int port)
    {
        if (port <= 0) return true;
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void KillOrphanProcesses()
    {
        foreach (var procName in new[] { "xray", "sing-box" })
        {
            try
            {
                foreach (var p in Process.GetProcessesByName(procName))
                {
                    using (p)
                    {
                        try
                        {
                            var file = p.MainModule?.FileName;
                            if (!string.IsNullOrEmpty(file) && StartupReaper.ShouldReap(file))
                            {
                                p.Kill();
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }
    }

    private async Task<bool> WaitForInboundPortAsync(int port, TimeSpan timeout, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        var token = timeoutCts.Token;

        var s = _settings.Settings;
        var isChainedOuter = TryGetSocksPortOverride().HasValue;
        var hasAuth = !isChainedOuter && LanPolicy.CanExpose(s);
        var user = hasAuth ? s.LanProxyUsername : null;
        var pass = hasAuth ? s.LanProxyPassword : null;

        while (!token.IsCancellationRequested)
        {
            if (_process == null || _process.HasExited)
            {
                return false;
            }

            try
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(IPAddress.Loopback, port, token).ConfigureAwait(false);
                if (_process == null || _process.HasExited)
                {
                    return false;
                }
                if (await SocksProbe.IsSocksPortListeningAsync(port, token, user, pass).ConfigureAwait(false))
                {
                    var httpPort = HttpProxyPort;
                    if (httpPort > 0 && httpPort != port)
                    {
                        using var httpTcp = new TcpClient();
                        await httpTcp.ConnectAsync(IPAddress.Loopback, httpPort, token).ConfigureAwait(false);
                    }
                    return true;
                }
            }
            catch
            {
            }

            try { await Task.Delay(100, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }

        return false;
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
                    Log($"[Location] V2Ray exit route confirmed: {result.Ip} â€” {locationDetail}");
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
            if (proc is not null)
            {
                try
                {
                    if (!proc.HasExited)
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
                }
                catch
                {
                }

                try { proc.CancelOutputRead(); } catch { }
                try { proc.CancelErrorRead(); } catch { }
                try { proc.Dispose(); } catch { }
            }

            SetState(ConnectionState.Disconnected);
            Log("V2Ray core stopped.");
        }
        finally
        {
            DeleteRunConfigs();
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

    internal static string? ValidateOutboundNode(V2RayConfigEntry? config) =>
        V2RayConfigBuilder.ValidateOutboundNode(config);

    internal static bool ResolveCoreKind(V2RayConfigEntry? config, string? coreSetting, out string? autoSwitchReason) =>
        V2RayConfigBuilder.ResolveCoreKind(config, coreSetting, out autoSwitchReason);

    private (string ExePath, bool IsXray) ResolveCoreExecutable(V2RayConfigEntry? config = null)
    {
        var targetConfig = config ?? ResolveActiveConfig();
        var proto = (targetConfig?.Protocol ?? "").Trim().ToLowerInvariant();
        var network = (targetConfig?.Network ?? "").Trim().ToLowerInvariant();
        var requiresSingBox = proto is "hysteria2" or "hy2" or "tuic";
        var requiresXray = network is "xhttp" or "splithttp" or "kcp" or "mkcp";

        var isXray = ResolveCoreKind(targetConfig, _settings.Settings.V2RayCore, out var autoSwitchReason);
        if (!string.IsNullOrEmpty(autoSwitchReason))
        {
            Log($"[V2Ray] {autoSwitchReason}");
        }

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

        if (!requiresSingBox)
        {
            var xrayFallback = Path.Combine(appDir, "Resources", "xray", xrayBin);
            if (File.Exists(xrayFallback)) return (xrayFallback, true);
            var xrayFallbackWin = Path.Combine(appDir, "Resources", "xray", "xray.exe");
            if (File.Exists(xrayFallbackWin)) return (xrayFallbackWin, true);
        }

        if (!requiresXray)
        {
            var singboxFallback = Path.Combine(appDir, "Resources", "sing_box", singboxBin);
            if (File.Exists(singboxFallback)) return (singboxFallback, false);
            var singboxFallbackWin = Path.Combine(appDir, "Resources", "sing_box", "sing-box.exe");
            if (File.Exists(singboxFallbackWin)) return (singboxFallbackWin, false);
        }

        return ("", true);
    }

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
            return false;
        }
    }

    private static string RunConfigPath(bool isXray)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Se7en");
        return Path.Combine(dir, isXray ? "xray_run.json" : "singbox_run.json");
    }

    private static void DeleteRunConfigs()
    {
        try { File.Delete(RunConfigPath(isXray: true)); } catch { }
        try { File.Delete(RunConfigPath(isXray: false)); } catch { }
    }

    internal static string SanitizeSingBoxMuxProtocol(string? value) =>
        V2RayConfigBuilder.SanitizeSingBoxMuxProtocol(value);

    private string GenerateConfigFile(V2RayConfigEntry? config, bool isXray)
    {
        var problem = ValidateOutboundNode(config);
        if (problem is not null)
        {
            throw new InvalidOperationException(problem);
        }

        _activeCoreIsXray = isXray;
        var filePath = RunConfigPath(isXray);
        var configDir = Path.GetDirectoryName(filePath)!;
        Directory.CreateDirectory(configDir);
        IdentityFileGuard.RestrictToCurrentUser(configDir, out _);

        var json = BuildCoreConfigJson(config, isXray);
        File.WriteAllText(filePath, json);
        return filePath;
    }

    internal string BuildCoreConfigJson(V2RayConfigEntry? config, bool isXray)
    {
        _activeCoreIsXray = isXray;
        var s = _settings.Settings;
        var isChainedOuter = TryGetSocksPortOverride().HasValue;
        return V2RayConfigBuilder.BuildCoreConfigJson(
            s,
            config,
            isXray,
            isChainedOuter,
            SocksProxyPort,
            HttpProxyPort,
            BuildDnsPlan(s),
            ResolveUserUpstreamUrl(s),
            Log);
    }

    private static readonly SemaphoreSlim _testThrottle = new(6, 6);

        public static async Task<int> MeasureTcpPingAsync(V2RayConfigEntry? config, CancellationToken ct = default)
    {
        if (config == null || string.IsNullOrWhiteSpace(config.Address) || config.Port <= 0)
            return -3;

        var host = config.Address.Trim();
        var port = config.Port;

        var sw = Stopwatch.StartNew();
        try
        {
            using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true
            };

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(2500));

            await socket.ConnectAsync(host, port, timeoutCts.Token).ConfigureAwait(false);
            sw.Stop();
            return Math.Max(1, (int)sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            return -3;
        }
        catch (Exception)
        {
            return -3;
        }
    }

    public async Task<int> MeasureRealDelayAsync(V2RayConfigEntry? config, CancellationToken ct = default)
    {
        if (config == null || string.IsNullOrWhiteSpace(config.Address) || config.Port <= 0)
            return -3;

        if (State == ConnectionState.Connected && ResolveActiveConfig()?.Id == config.Id)
        {
            return await ProbeUrlThroughProxyAsync(SocksProxyPort, ct);
        }

        await _testThrottle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await RunIsolatedTestAsync(config, ct).ConfigureAwait(false);
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
            Log($"Cannot test this node: {nodeProblem}");
            return -1;
        }

        var (exePath, isXray) = ResolveCoreExecutable(config);
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
                    ["outbounds"] = V2RayConfigBuilder.BuildXrayOutbounds(config, s.ShadowsocksMethod, shouldFragment, s.V2RayFragmentPackets, s.V2RayFragmentLength, s.V2RayFragmentInterval)
                };
                File.WriteAllText(tempConfigFile, JsonSerializer.Serialize(xrayTestObj));
            }
            else
            {
                var s = _settings.Settings;
                var shouldFragment = config.EnableFragment ?? s.SingBoxEnableFragment;
                var sbFragDelay = string.IsNullOrWhiteSpace(s.SingBoxFragmentFallbackDelay) ? "500ms" : s.SingBoxFragmentFallbackDelay.Trim();
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
                    ["outbounds"] = V2RayConfigBuilder.BuildSingBoxOutbounds(config, s.ShadowsocksMethod, shouldFragment, fragmentFallbackDelay: sbFragDelay)
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
            psi.EnvironmentVariables["GOMEMLIMIT"] = "128MiB";
            psi.EnvironmentVariables["GODEBUG"] = "madvdontneed=1";
            if (!isXray)
            {
                psi.EnvironmentVariables["ENABLE_DEPRECATED_LEGACY_DNS_SERVERS"] = "true";
                psi.EnvironmentVariables["ENABLE_DEPRECATED_MISSING_DOMAIN_RESOLVER"] = "true";
                psi.EnvironmentVariables["ENABLE_DEPRECATED_SPECIAL_OUTBOUNDS"] = "true";
                psi.EnvironmentVariables["ENABLE_DEPRECATED_LEGACY_DOMAIN_STRATEGY_OPTIONS"] = "true";
                psi.EnvironmentVariables["ENABLE_DEPRECATED_OUTBOUND_DNS_RULE_ITEM"] = "true";
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
