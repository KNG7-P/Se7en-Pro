using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Se7enPro.Models;

namespace Se7enPro.Services;

public abstract class LocalSocksEngineBase : IConnectionEngine, IDisposable
{
    protected readonly ILogger _logger;
    protected readonly ISettingsService _settings;
    private readonly IChildProcessGuard _childGuard;

    protected Process? _process;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _retryDelayCts;
    private volatile bool _userWantsConnection;

    private int _consecutiveFastFailures;
    private DateTime _lastStartUtc;
    private const int MaxConsecutiveFastFailures = 6;
    private static readonly TimeSpan FastFailWindow = TimeSpan.FromSeconds(20);

    private int _processGeneration;
    private volatile bool _isIntentionalRestart;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly LocalProxyBridge _socksBridge = new();
    private readonly LocalProxyBridge _httpBridge = new();

    protected LocalSocksEngineBase(
        ILogger logger,
        ISettingsService settings,
        IChildProcessGuard childGuard)
    {
        _logger = logger;
        _settings = settings;
        _childGuard = childGuard;

        _socksBridge.BytesTransferredChanged += OnBridgeBytesChanged;
        _httpBridge.BytesTransferredChanged += OnBridgeBytesChanged;
    }

    private void OnBridgeBytesChanged(object? sender, EventArgs e) => RaiseBytesChanged();

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public int SocksProxyPort { get; private set; }

    public int HttpProxyPort { get; private set; }

    public string ClientRegion { get; protected set; } = "";
    public string ConnectedServerRegion { get; protected set; } = "";
    protected virtual bool AutoProbeUpdatesConnectedServerRegion => true;
    public string CurrentRouteIp { get; protected set; } = "";
    public string CurrentRouteSni { get; protected set; } = "";

    private readonly List<string> _availableRegions = new();
    public IReadOnlyList<string> AvailableEgressRegions => _availableRegions.AsReadOnly();

    public long BytesSent => _socksBridge.BytesSent + _httpBridge.BytesSent;
    public long BytesReceived => _socksBridge.BytesReceived + _httpBridge.BytesReceived;

    public int ConnectProgressPercent { get; protected set; }
    public string ConnectProgressText { get; protected set; } = "";

    public event EventHandler<ConnectionState>? StateChanged;
    public event EventHandler<Notice>? NoticeReceived;
    public event EventHandler<string>? LogLineAppended;
    public event EventHandler? BytesTransferredChanged;
    public event EventHandler? RouteChanged;
    public event EventHandler? ConnectProgressChanged;

    public void SetConnectProgress(int percent, string text)
    {
        percent = Math.Clamp(percent, 0, 100);
        ConnectProgressPercent = percent;
        ConnectProgressText = text;
        try { ConnectProgressChanged?.Invoke(this, EventArgs.Empty); } catch { }
    }

    protected void RaiseNotice(Notice n) => NoticeReceived?.Invoke(this, n);
    protected void RaiseRouteChanged() => RouteChanged?.Invoke(this, EventArgs.Empty);
    protected void RaiseBytesChanged() => BytesTransferredChanged?.Invoke(this, EventArgs.Empty);

    public abstract ConnectionMethod Method { get; }
    public abstract IReadOnlyList<string> CoreProcessNames { get; }

    protected abstract string EngineDisplayName { get; }

    protected abstract string WorkSubdirectory { get; }

    protected virtual TimeSpan ReadyTimeout => TimeSpan.FromSeconds(45);

    protected virtual IReadOnlyList<(string Host, int Port)> ProbeTargets => new[]
    {
        ("1.1.1.1", 80),
        ("1.0.0.1", 80),
        ("1.1.1.1", 443),
        ("cloudflare.com", 80),
        ("www.google.com", 80),
    };

    protected sealed record PreparedLaunch(
        string ExePath,
        IReadOnlyList<string> Arguments,
        string WorkingDirectory,

        string? StdinPrimer = null,

        int HttpProxyPort = 0,

        int? SocksPortOverride = null,

        IDictionary<string, string>? EnvironmentVariables = null,

        int? CoreTargetSocksPort = null);

    protected abstract PreparedLaunch Prepare(string workDir, int socksPort, int httpPort);

    protected virtual Process CreateCoreProcess(ProcessStartInfo psi)
    {
        return new Process { StartInfo = psi };
    }

    protected virtual void OnCoreLine(string line) { }

    protected virtual IReadOnlyList<int> ReservedEnginePorts => Array.Empty<int>();

    protected string AppDir => AppContext.BaseDirectory;

    public Task StartAsync() => RunGatedAsync(StartAsyncCore);

    private void StartAsyncCore()
    {
        var wasWanting = _userWantsConnection;
        _userWantsConnection = true;
        if (!wasWanting) _consecutiveFastFailures = 0;
        CancelPendingRestart();

        if (_process is not null && !_process.HasExited)
        {
            return;
        }

        DisposeProcessQuietly(_process);
        _process = null;

        SetState(ConnectionState.Connecting);
        Log($"Starting {EngineDisplayName}...");

        HttpProxyPort = 0;
        ConnectedServerRegion = "";
        CurrentRouteIp = "";
        CurrentRouteSni = "";
        RaiseBytesChanged();
        RaiseRouteChanged();

        try
        {
            var workDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Se7en",
                WorkSubdirectory);
            Directory.CreateDirectory(workDir);

            var s = _settings.Settings;

            var authUser = s.LanAuthEnabled ? s.LanProxyUsername : null;
            var authPass = s.LanAuthEnabled ? s.LanProxyPassword : null;
            var bindAddr = LanExposurePolicy.ResolveBindAddress(
                s.AllowLanConnections,
                authUser,
                authPass,
                engineEnforcesCredentials: false,
                out var lanReason);
            if (!string.IsNullOrEmpty(lanReason)) Log(lanReason);

            _socksBridge.Stop();
            _httpBridge.Stop();

            var requestedSocks = s.UseCustomProxyPorts ? SanitizeListenPort(s.LocalSocksProxyPort, "SOCKS5") : 0;
            var requestedHttp = s.UseCustomProxyPorts ? SanitizeListenPort(s.LocalHttpProxyPort, "HTTP") : 0;

            if (requestedSocks != 0 && requestedHttp != 0 && requestedSocks == requestedHttp)
            {
                throw new InvalidOperationException(
                    $"SOCKS and HTTP proxy ports cannot both be set to {requestedSocks}. " +
                    "Give them different ports or set them to 0 (auto) in Settings.");
            }

            var avoidList = new List<int>(ReservedEnginePorts);

            int publishedSocks;
            if (requestedSocks > 0)
            {
                if (avoidList.Contains(requestedSocks))
                {
                    throw new InvalidOperationException(
                        $"The SOCKS port {requestedSocks} is reserved internally by {EngineDisplayName}. " +
                        "Pick a different port or set it to 0 (auto) in Settings.");
                }
                if (!IsPortBindable(bindAddr, requestedSocks, out var reason))
                {
                    throw new InvalidOperationException(
                        $"The SOCKS port {requestedSocks} can't be opened ({reason}). " +
                        "Pick a different port or set it to 0 (auto) in Settings.");
                }
                publishedSocks = requestedSocks;
            }
            else
            {
                publishedSocks = PickFreeLoopbackPort(avoidList.ToArray());
            }
            avoidList.Add(publishedSocks);

            int publishedHttp;
            if (requestedHttp > 0)
            {
                if (avoidList.Contains(requestedHttp))
                {
                    throw new InvalidOperationException(
                        $"The HTTP port {requestedHttp} is reserved internally by {EngineDisplayName}. " +
                        "Pick a different port or set it to 0 (auto) in Settings.");
                }
                if (!IsPortBindable(bindAddr, requestedHttp, out var reason))
                {
                    throw new InvalidOperationException(
                        $"The HTTP port {requestedHttp} can't be opened ({reason}). " +
                        "Pick a different port or set it to 0 (auto) in Settings.");
                }
                publishedHttp = requestedHttp;
            }
            else
            {
                publishedHttp = PickFreeLoopbackPort(avoidList.ToArray());
            }
            avoidList.Add(publishedHttp);

            var coreSocks = PickFreeLoopbackPort(avoidList.ToArray());
            avoidList.Add(coreSocks);
            var coreHttp = PickFreeLoopbackPort(avoidList.ToArray());

            var launch = Prepare(workDir, coreSocks, coreHttp);
            var actualCoreSocks = launch.CoreTargetSocksPort ?? launch.SocksPortOverride ?? coreSocks;
            var actualCoreHttp = launch.HttpProxyPort;

            if (launch.SocksPortOverride.HasValue)
            {
                SocksProxyPort = actualCoreSocks;
                HttpProxyPort = actualCoreHttp;
            }
            else
            {
                var bridgeUser = s.LanAuthEnabled ? s.LanProxyUsername : "";
                var bridgePass = s.LanAuthEnabled ? s.LanProxyPassword : "";
                if (publishedSocks != actualCoreSocks)
                {
                    _socksBridge.Start(publishedSocks, actualCoreSocks, bindAddr, bridgeUser, bridgePass, isHttp: false);
                }
                SocksProxyPort = publishedSocks;

                if (actualCoreHttp > 0)
                {
                    if (publishedHttp != actualCoreHttp)
                    {
                        _httpBridge.Start(publishedHttp, actualCoreHttp, bindAddr, bridgeUser, bridgePass, isHttp: true);
                    }
                    HttpProxyPort = publishedHttp;
                }
                else
                {
                    HttpProxyPort = 0;
                }

                Log($"SOCKS running on port {SocksProxyPort}");
                if (HttpProxyPort > 0)
                {
                    Log($"HTTP proxy running on port {HttpProxyPort}");
                }
            }
            var socksPort = SocksProxyPort;

            var psi = new ProcessStartInfo
            {
                FileName = launch.ExePath,
                WorkingDirectory = launch.WorkingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true,

                RedirectStandardInput = true,
            };
            if (launch.EnvironmentVariables is not null)
            {
                foreach (var (k, v) in launch.EnvironmentVariables)
                {
                    psi.EnvironmentVariables[k] = v;
                }
            }
            foreach (var arg in launch.Arguments) psi.ArgumentList.Add(arg);

            _cts = new CancellationTokenSource();

            _processGeneration++;
            var generation = _processGeneration;

            var proc = CreateCoreProcess(psi);
            proc.EnableRaisingEvents = true;
            proc.Exited += (_, _) => OnProcessExited(generation);

            var alreadyRunning = false;
            try { alreadyRunning = proc.Id > 0 && !proc.HasExited; } catch { }

            if (!alreadyRunning)
            {

                proc.OutputDataReceived += (_, e) => OnLineReceived(e.Data);
                proc.ErrorDataReceived += (_, e) => OnLineReceived(e.Data);
                if (!proc.Start())
                {
                    throw new InvalidOperationException($"Failed to start {EngineDisplayName} core");
                }
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
            }

            _process = proc;
            _childGuard.Adopt(proc);
            OnProcessStarted(proc);

            try
            {
                if (!string.IsNullOrEmpty(launch.StdinPrimer))
                {
                    proc.StandardInput.Write(launch.StdinPrimer);
                    proc.StandardInput.Flush();
                }
            }
            catch {  }
            try { proc.StandardInput.Close(); } catch { }

            _lastStartUtc = DateTime.UtcNow;
            _logger.LogInformation("{Engine} core started (pid {Pid}) socks={Port} http={HttpPort}",
                EngineDisplayName, proc.Id, socksPort, HttpProxyPort);

            StartReadinessProbe(socksPort, _cts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start {Engine}", EngineDisplayName);
            Log($"Failed to start {EngineDisplayName}: {ex.Message}");
            DisposeProcessQuietly(_process);
            _process = null;
            NoteFailureAndMaybeRestart(ranLongEnough: false);
        }
    }

    private async Task RunGatedAsync(Action action)
    {
        await _lifecycleGate.WaitAsync();
        try { action(); }
        finally { _lifecycleGate.Release(); }
    }

    private async Task RunGatedAsync(Func<Task> action)
    {
        await _lifecycleGate.WaitAsync();
        try { await action(); }
        finally { _lifecycleGate.Release(); }
    }

    public Task StopAsync() => RunGatedAsync(StopAsyncCoreAsync);

    private async Task StopAsyncCoreAsync()
    {
        _userWantsConnection = false;
        _consecutiveFastFailures = 0;
        CancelPendingRestart();

        var proc = _process;
        _cts?.Cancel();

        if (proc is null || proc.HasExited)
        {
            DisposeProcessQuietly(proc);
            _process = null;
            SocksProxyPort = 0;
            HttpProxyPort = 0;
            SetState(ConnectionState.Disconnected);
            return;
        }

        SetState(ConnectionState.Disconnecting);
        Log($"Stopping {EngineDisplayName}...");

        try
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            try
            {
                using var waitCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await proc.WaitForExitAsync(waitCts.Token);
            }
            catch { }
        }
        finally
        {
            _process = null;
            DisposeProcessQuietly(proc);
            _socksBridge.Stop();
            _httpBridge.Stop();
            _socksBridge.ResetCounters();
            _httpBridge.ResetCounters();
            SocksProxyPort = 0;
            HttpProxyPort = 0;
            ConnectedServerRegion = "";
            CurrentRouteIp = "";
            CurrentRouteSni = "";
            RaiseBytesChanged();
            RaiseRouteChanged();
            SetState(ConnectionState.Disconnected);
            OnSessionStopped();
            Log($"Stopped {EngineDisplayName}");
        }
    }

    public virtual void CancelConnecting()
    {
        _userWantsConnection = false;
        CancelPendingRestart();
        try { _cts?.Cancel(); } catch { }
    }

    private void OnProcessExited(int generation)
    {

        if (generation != _processGeneration)
        {
            _logger.LogInformation(
                "Ignoring stale {Engine} core exit (generation {Generation} ≠ {Current})",
                EngineDisplayName, generation, _processGeneration);
            return;
        }

        var proc = _process;
        var exitCode = -1;
        try { if (proc is not null && proc.HasExited) exitCode = proc.ExitCode; } catch { }

        var ranFor = DateTime.UtcNow - _lastStartUtc;
        _process = null;
        _socksBridge.Stop();
        _httpBridge.Stop();

        var toDispose = proc;
        _ = Task.Run(() => DisposeProcessQuietly(toDispose));

        var wasIntentional = _isIntentionalRestart;
        _isIntentionalRestart = false;

        if (State is ConnectionState.Disconnecting or ConnectionState.Disconnected || !_userWantsConnection) return;

        if (wasIntentional)
        {
            _consecutiveFastFailures = 0;
            SetState(ConnectionState.Connecting);
            ScheduleAutoRestart(TimeSpan.Zero);
            return;
        }

        Log($"{EngineDisplayName} core exited unexpectedly (code {exitCode}).");
        NoteFailureAndMaybeRestart(ranLongEnough: ranFor >= FastFailWindow);
    }

    private void NoteFailureAndMaybeRestart(bool ranLongEnough)
    {

        _ = RunGatedAsync(() => NoteFailureAndMaybeRestartCore(ranLongEnough));
    }

    private void NoteFailureAndMaybeRestartCore(bool ranLongEnough)
    {
        if (!_userWantsConnection)
        {
            SocksProxyPort = 0;
            HttpProxyPort = 0;
            if (State == ConnectionState.Connecting) SetState(ConnectionState.Disconnected);
            return;
        }

        if (ranLongEnough)
        {
            _consecutiveFastFailures = 0;
            Log($"Auto-restarting {EngineDisplayName}...");
            SetState(ConnectionState.Connecting);
            ScheduleAutoRestart(TimeSpan.FromSeconds(3));
            return;
        }

        _consecutiveFastFailures++;
        if (_consecutiveFastFailures >= MaxConsecutiveFastFailures)
        {
            _userWantsConnection = false;
            SocksProxyPort = 0;
            HttpProxyPort = 0;
            CancelPendingRestart();
            Log($"{EngineDisplayName} failed {_consecutiveFastFailures} times in a row without staying up. "
              + "Giving up to avoid a restart loop â€” check your settings and network, then press Connect to retry.");
            SetState(ConnectionState.Error);
            return;
        }

        var delaySeconds = Math.Min(60, 3 * (1 << (_consecutiveFastFailures - 1)));
        Log($"{EngineDisplayName} exited too quickly; retrying in {delaySeconds}s "
          + $"(attempt {_consecutiveFastFailures}/{MaxConsecutiveFastFailures})...");
        SetState(ConnectionState.Connecting);
        ScheduleAutoRestart(TimeSpan.FromSeconds(delaySeconds));
    }

    private void ScheduleAutoRestart(TimeSpan delay)
    {
        CancelPendingRestart();
        var cts = new CancellationTokenSource();
        _retryDelayCts = cts;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(delay, cts.Token); }
            catch (OperationCanceledException) { return; }
            if (!_userWantsConnection)
            {

                if (State == ConnectionState.Connecting) SetState(ConnectionState.Disconnected);
                return;
            }
            try { await StartAsync(); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{Engine} auto-restart failed", EngineDisplayName);
                if (_userWantsConnection) ScheduleAutoRestart(TimeSpan.FromSeconds(10));
            }
        });
    }

    private void CancelPendingRestart()
    {
        var cts = _retryDelayCts;
        _retryDelayCts = null;
        if (cts is null) return;
        try { cts.Cancel(); } catch { }
        try { cts.Dispose(); } catch { }
    }

    protected virtual void OnProcessStarted(Process proc) { }
    protected virtual void OnSessionStopped() { }

    protected void RestartCore(string reason, bool resetFailureCount = true)
    {
        if (resetFailureCount) _consecutiveFastFailures = 0;
        _isIntentionalRestart = true;
        Log($"Restarting {EngineDisplayName}: {reason}");
        try { _process?.Kill(); } catch { }
    }

    protected void ConfirmTunnelReady(string? reason = null)
    {
        if (State == ConnectionState.Connected) return;
        _consecutiveFastFailures = 0;
        Log($"{EngineDisplayName} tunnel is up ({reason ?? "core data-plane confirmed"}).");
        SetState(ConnectionState.Connected);
        OnTunnelConnected(SocksProxyPort, HttpProxyPort, _cts?.Token ?? CancellationToken.None);
    }

    private void StartReadinessProbe(int socksPort, CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            var deadline = DateTime.UtcNow + ReadyTimeout;
            var attempt = 0;
            SetConnectProgress(30, $"Establishing {EngineDisplayName} tunnel...");
            while (!ct.IsCancellationRequested && DateTime.UtcNow < deadline)
            {
                if (State == ConnectionState.Connected) return;
                attempt++;
                var pct = Math.Min(30 + (attempt * 6), 88);
                SetConnectProgress(pct, $"Verifying {EngineDisplayName} connection (attempt {attempt})...");
                if (attempt == 1 || attempt % 3 == 0)
                {
                    Log($"Verifying {EngineDisplayName} tunnel connectivity (attempt {attempt})...");
                }
                foreach (var (host, port) in ProbeTargets)
                {
                    if (ct.IsCancellationRequested || State == ConnectionState.Connected) return;
                    bool ok;
                    try { ok = await ProbeSocksConnectAsync(socksPort, host, port, ct); }
                    catch { ok = false; }
                    if (ok)
                    {
                        if (ct.IsCancellationRequested || State == ConnectionState.Connected) return;
                        _consecutiveFastFailures = 0;
                        SetConnectProgress(100, $"{EngineDisplayName} connected");
                        Log($"{EngineDisplayName} tunnel is up (verified via {host}:{port}).");
                        SetState(ConnectionState.Connected);
                        OnTunnelConnected(socksPort, HttpProxyPort, ct);
                        return;
                    }
                }
                try { await Task.Delay(TimeSpan.FromMilliseconds(1500), ct); }
                catch (OperationCanceledException) { return; }
            }

            if (ct.IsCancellationRequested || State == ConnectionState.Connected) return;

            Log($"{EngineDisplayName} did not establish a working tunnel within "
              + $"{ReadyTimeout.TotalSeconds:0}s; restarting.");
            var proc = _process;
            try { if (proc is not null && !proc.HasExited) proc.Kill(entireProcessTree: true); }
            catch { }
        });
    }

    protected virtual void OnTunnelConnected(int socksPort, int httpPort, CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var handler = new SocketsHttpHandler
                {
                    Proxy = new WebProxy($"socks5://127.0.0.1:{socksPort}"),
                    ConnectTimeout = TimeSpan.FromSeconds(6)
                };
                using var client = new HttpClient(handler)
                {
                    Timeout = TimeSpan.FromSeconds(8)
                };

                string? ip = null;
                string? country = null;

                try
                {
                    var lines = (await client.GetStringAsync("http://ip-api.com/line/?fields=status,countryCode,query", ct))
                        .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    if (lines.Length >= 3 && lines[0].Trim().Equals("success", StringComparison.OrdinalIgnoreCase))
                    {
                        var c = lines[1].Trim().ToUpperInvariant();
                        if (c.Length == 2 && c != "T1" && c != "XX")
                        {
                            country = c;
                            ip = lines[2].Trim();
                        }
                    }
                }
                catch { }

                if (string.IsNullOrEmpty(country) || string.IsNullOrEmpty(ip))
                {
                    try
                    {
                        var json = await client.GetStringAsync("https://freeipapi.com/api/json", ct);
                        using var doc = System.Text.Json.JsonDocument.Parse(json);
                        if (string.IsNullOrEmpty(country) && doc.RootElement.TryGetProperty("countryCode", out var cProp))
                        {
                            var c = cProp.GetString()?.Trim().ToUpperInvariant();
                            if (!string.IsNullOrEmpty(c) && c.Length == 2 && c != "T1" && c != "XX")
                            {
                                country = c;
                            }
                        }
                        if (string.IsNullOrEmpty(ip) && doc.RootElement.TryGetProperty("ipAddress", out var ipProp))
                        {
                            ip = ipProp.GetString()?.Trim();
                        }
                    }
                    catch { }
                }

                if (string.IsNullOrEmpty(country) || string.IsNullOrEmpty(ip))
                {
                    try
                    {
                        var json = await client.GetStringAsync("https://api.country.is", ct);
                        using var doc = System.Text.Json.JsonDocument.Parse(json);
                        if (string.IsNullOrEmpty(country) && doc.RootElement.TryGetProperty("country", out var cProp))
                        {
                            var c = cProp.GetString()?.Trim().ToUpperInvariant();
                            if (!string.IsNullOrEmpty(c) && c.Length == 2 && c != "T1" && c != "XX")
                            {
                                country = c;
                            }
                        }
                        if (string.IsNullOrEmpty(ip) && doc.RootElement.TryGetProperty("ip", out var ipProp))
                        {
                            ip = ipProp.GetString()?.Trim();
                        }
                    }
                    catch { }
                }

                if (string.IsNullOrEmpty(ip))
                {
                    try
                    {
                        var text = await client.GetStringAsync("https://1.1.1.1/cdn-cgi/trace", ct);
                        foreach (var line in text.Split('\n'))
                        {
                            var trimmed = line.Trim();
                            if (trimmed.StartsWith("ip=", StringComparison.OrdinalIgnoreCase))
                            {
                                ip = trimmed.Substring(3).Trim();
                                break;
                            }
                        }
                    }
                    catch { }
                }

                if (AutoProbeUpdatesConnectedServerRegion && string.IsNullOrEmpty(ConnectedServerRegion))
                {
                    if (!string.IsNullOrEmpty(country) && country.Length == 2)
                    {
                        ConnectedServerRegion = country;
                    }
                }
                if (!string.IsNullOrEmpty(ip) && string.IsNullOrEmpty(CurrentRouteIp))
                {
                    CurrentRouteIp = ip;
                }
                RaiseRouteChanged();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not resolve exit location via probe");
            }
        }, ct);
    }

    private static async Task<bool> ProbeSocksConnectAsync(
        int socksPort, string host, int port, CancellationToken outerCt)
    {
        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        attemptCts.CancelAfter(TimeSpan.FromSeconds(7));
        var ct = attemptCts.Token;

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, socksPort, ct);
        await using var stream = client.GetStream();

        await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, ct);
        var methodResp = new byte[2];
        await ReadExactAsync(stream, methodResp, ct);
        if (methodResp[0] != 0x05 || methodResp[1] != 0x00) return false;

        byte[] req;
        if (IPAddress.TryParse(host, out var ip) &&
            ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            req = new byte[] { 0x05, 0x01, 0x00, 0x01, b[0], b[1], b[2], b[3],
                               (byte)(port >> 8), (byte)(port & 0xFF) };
        }
        else
        {
            var h = Encoding.ASCII.GetBytes(host);
            req = new byte[4 + 1 + h.Length + 2];
            req[0] = 0x05; req[1] = 0x01; req[2] = 0x00; req[3] = 0x03;
            req[4] = (byte)h.Length;
            Array.Copy(h, 0, req, 5, h.Length);
            req[5 + h.Length] = (byte)(port >> 8);
            req[6 + h.Length] = (byte)(port & 0xFF);
        }
        await stream.WriteAsync(req, ct);

        var reply = new byte[4];
        await ReadExactAsync(stream, reply, ct);
        return reply[0] == 0x05 && reply[1] == 0x00;
    }

    private static async Task ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(offset), ct);
            if (n <= 0) throw new IOException("SOCKS peer closed the connection");
            offset += n;
        }
    }

    private int SanitizeListenPort(int port, string label = "Proxy")
    {
        if (port <= 0) return 0;
        if (port > 65535)
        {
            _logger.LogWarning("{Label} port {Port} exceeds maximum allowed TCP port 65535. Falling back to dynamic port.", label, port);
            Log($"{label} port {port} exceeds 65535 limit. Using automatic port.");
            return 0;
        }
        return port;
    }

    private static bool IsPortBindable(IPAddress addr, int port, out string reason)
    {
        reason = "";
        try
        {
            var tcpListeners = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
            var conflict = Array.Find(tcpListeners, ep => ep.Port == port && (ep.Address.Equals(addr) || ep.Address.Equals(IPAddress.Any) || addr.Equals(IPAddress.Any)));
            if (conflict != null)
            {
                reason = "already in use by another application";
                return false;
            }
            return true;
        }
        catch
        {
            return true;
        }
    }

    protected static int PickFreeLoopbackPort(params int[] avoid)
    {

        for (var attempt = 0; attempt < 64; attempt++)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port;
            try { port = ((IPEndPoint)listener.LocalEndpoint).Port; }
            finally { listener.Stop(); }
            if (Array.IndexOf(avoid, port) < 0) return port;
        }
        throw new InvalidOperationException("Could not find a free loopback port");
    }

    protected string ResolveBundledResource(params string[] relativePathParts)
    {
        var rel = Path.Combine(relativePathParts);
        var candidates = new[]
        {
            Path.Combine(AppDir, "Resources", rel),
            Path.Combine(AppDir, rel),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", rel),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, rel),
            Path.GetFullPath(Path.Combine(AppDir, @"..\..\..\..\..\Se7enPro\Resources", rel)),
            Path.GetFullPath(Path.Combine(AppDir, @"..\..\..\..\Se7enPro\Resources", rel)),
            Path.GetFullPath(Path.Combine(AppDir, @"..\..\..\Se7enPro\Resources", rel)),
            Path.GetFullPath(Path.Combine(AppDir, @"..\Resources", rel)),
            Path.Combine(Directory.GetCurrentDirectory(), "Resources", rel),
        };

        foreach (var c in candidates)
        {
            if (File.Exists(c)) return c;
        }

        return Path.Combine(AppDir, "Resources", rel);
    }

    protected string StageFile(string sourcePath, string destPath)
    {
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException(
                $"Bundled {EngineDisplayName} resource missing: {Path.GetFileName(sourcePath)}",
                sourcePath);
        }

        if (destPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            var dir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                var currentExe = Path.GetFileName(destPath);
                try
                {
                    foreach (var stale in Directory.EnumerateFiles(dir, "*.exe"))
                    {
                        var staleName = Path.GetFileName(stale);
                        if (!string.Equals(staleName, currentExe, StringComparison.OrdinalIgnoreCase))
                        {

                            if (CoreProcessNames == null || !CoreProcessNames.Contains(staleName, StringComparer.OrdinalIgnoreCase))
                            {
                                try { File.Delete(stale); } catch { }
                            }
                        }
                    }
                }
                catch { }
            }
        }

        FileCacheHelper.StageFileSafe(sourcePath, destPath, _logger, CoreProcessNames);
        return destPath;
    }

    private static readonly System.Text.RegularExpressions.Regex RustLogPrefixRegex = new(@"^\[\d{4}-\d{2}-\d{2}T[\d:.]+Z\s+(?:INFO|WARN|ERROR|DEBUG|TRACE)\s+[^\]]+\]\s*", System.Text.RegularExpressions.RegexOptions.Compiled);

    protected void Log(string line)
    {
        LogLineAppended?.Invoke(this, line);
    }

    protected virtual bool ShouldSuppressCoreLogLine(string line) => false;

    private void OnLineReceived(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        if (ShouldSuppressCoreLogLine(line)) return;
        try { OnCoreLine(line); } catch { }

        var clean = RustLogPrefixRegex.Replace(line, "").Trim();
        if (string.IsNullOrEmpty(clean)) clean = line;

        Log(LogSanitizer.Scrub(clean));
    }

    private void SetState(ConnectionState s)
    {

        if (s == ConnectionState.Connected && !_userWantsConnection) return;
        if (State == s) return;
        State = s;

        if (s == ConnectionState.Connected)
        {
            SetConnectProgress(100, "Connected");

        }
        else
        {

            if (s is ConnectionState.Disconnected or ConnectionState.Error)
            {
                SetConnectProgress(0, "");

                _socksBridge.ResetCounters();
                _httpBridge.ResetCounters();
            }
            else if (s == ConnectionState.Connecting && ConnectProgressPercent == 0)
            {
                SetConnectProgress(10, "Connecting...");
            }
        }

        StateChanged?.Invoke(this, s);
    }

    private static async Task<bool> WaitForExitAsync(Process p, TimeSpan timeout)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            await p.WaitForExitAsync(cts.Token);
            return true;
        }
        catch (OperationCanceledException) { return p.HasExited; }
    }

    private void DisposeProcessQuietly(Process? proc)
    {
        if (proc is null) return;
        try { proc.CancelOutputRead(); } catch { }
        try { proc.CancelErrorRead(); } catch { }
        try { proc.Dispose(); } catch { }
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _process?.Kill(entireProcessTree: true); } catch { }
        try { _process?.Dispose(); } catch { }
        try { _cts?.Dispose(); } catch { }
        CancelPendingRestart();
    }
}
