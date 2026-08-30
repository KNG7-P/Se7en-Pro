using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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

    private Process? _process;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _retryDelayCts;
    private volatile bool _userWantsConnection;

    private int _consecutiveFastFailures;
    private DateTime _lastStartUtc;
    private const int MaxConsecutiveFastFailures = 6;
    private static readonly TimeSpan FastFailWindow = TimeSpan.FromSeconds(20);

    private int _processGeneration;
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

        IDictionary<string, string>? EnvironmentVariables = null);

    protected abstract PreparedLaunch Prepare(string workDir, int socksPort, int httpPort);

    protected virtual void OnCoreLine(string line) { }

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
            var bindAddr = s.AllowLanConnections ? IPAddress.Any : IPAddress.Loopback;

            var requestedSocks = SanitizeListenPort(s.LocalSocksProxyPort);
            var requestedHttp = SanitizeListenPort(s.LocalHttpProxyPort);

            if (requestedSocks != 0 && requestedHttp != 0 && requestedSocks == requestedHttp)
            {
                throw new InvalidOperationException(
                    $"SOCKS and HTTP proxy ports cannot both be set to {requestedSocks}. " +
                    "Give them different ports or set them to 0 (auto) in Settings.");
            }

            int publishedSocks;
            if (requestedSocks > 0)
            {
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
                publishedSocks = PickFreeLoopbackPort();
            }

            int publishedHttp;
            if (requestedHttp > 0)
            {
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
                publishedHttp = PickFreeLoopbackPort(publishedSocks);
            }

            var avoidList = new List<int> { publishedSocks, publishedHttp };
            var coreSocks = PickFreeLoopbackPort(avoidList.ToArray());
            avoidList.Add(coreSocks);
            var coreHttp = PickFreeLoopbackPort(avoidList.ToArray());

            var launch = Prepare(workDir, coreSocks, coreHttp);
            var actualCoreSocks = launch.SocksPortOverride ?? coreSocks;
            var actualCoreHttp = launch.HttpProxyPort;

            if (launch.SocksPortOverride.HasValue)
            {
                SocksProxyPort = actualCoreSocks;
                HttpProxyPort = actualCoreHttp;
            }
            else
            {
                _socksBridge.Start(publishedSocks, actualCoreSocks, bindAddr);
                if (actualCoreHttp > 0)
                {
                    _httpBridge.Start(publishedHttp, actualCoreHttp, bindAddr);
                    HttpProxyPort = publishedHttp;
                }
                else
                {
                    HttpProxyPort = 0;
                }
                SocksProxyPort = publishedSocks;

                Log($"SOCKS running on port {publishedSocks}");
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

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.OutputDataReceived += (_, e) => OnLineReceived(e.Data);
            proc.ErrorDataReceived += (_, e) => OnLineReceived(e.Data);
            proc.Exited += (_, _) => OnProcessExited(generation);

            if (!proc.Start())
            {
                throw new InvalidOperationException($"Failed to start {EngineDisplayName} core");
            }

            _process = proc;
            _childGuard.Adopt(proc);
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

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
        }
        finally
        {
            _process = null;
            _ = Task.Run(() => DisposeProcessQuietly(proc));
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
            Log($"Stopped {EngineDisplayName}");
        }
    }

    private void OnProcessExited(int generation)
    {

        if (generation != _processGeneration)
        {
            _logger.LogInformation(
                "Ignoring stale {Engine} core exit (generation {Generation} â‰  {Current})",
                EngineDisplayName, generation, _processGeneration);
            return;
        }

        var proc = _process;
        var exitCode = -1;
        try { if (proc is not null && proc.HasExited) exitCode = proc.ExitCode; } catch { }

        var ranFor = DateTime.UtcNow - _lastStartUtc;
        _process = null;
        SocksProxyPort = 0;
        HttpProxyPort = 0;

        var toDispose = proc;
        _ = Task.Run(() => DisposeProcessQuietly(toDispose));

        if (State == ConnectionState.Disconnecting) return;

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

    private void StartReadinessProbe(int socksPort, CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            var deadline = DateTime.UtcNow + ReadyTimeout;
            var attempt = 0;
            while (!ct.IsCancellationRequested && DateTime.UtcNow < deadline)
            {
                attempt++;
                foreach (var (host, port) in ProbeTargets)
                {
                    if (ct.IsCancellationRequested) return;
                    bool ok;
                    try { ok = await ProbeSocksConnectAsync(socksPort, host, port, ct); }
                    catch { ok = false; }
                    if (ok)
                    {
                        if (ct.IsCancellationRequested) return;
                        _consecutiveFastFailures = 0;
                        Log($"{EngineDisplayName} tunnel is up (verified via {host}:{port}).");
                        SetState(ConnectionState.Connected);
                        OnTunnelConnected(socksPort, HttpProxyPort, ct);
                        return;
                    }
                }
                try { await Task.Delay(TimeSpan.FromMilliseconds(1500), ct); }
                catch (OperationCanceledException) { return; }
            }

            if (ct.IsCancellationRequested) return;

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

                if (string.IsNullOrEmpty(country))
                {
                    try
                    {
                        var json = await client.GetStringAsync("https://api.country.is", ct);
                        using var doc = System.Text.Json.JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("country", out var cProp))
                        {
                            var c = cProp.GetString()?.Trim().ToUpperInvariant();
                            if (!string.IsNullOrEmpty(c) && c.Length == 2 && c != "T1" && c != "XX")
                            {
                                country = c;
                            }
                        }
                        if (doc.RootElement.TryGetProperty("ip", out var ipProp))
                        {
                            ip = ipProp.GetString()?.Trim();
                        }
                    }
                    catch { }
                }

                if (string.IsNullOrEmpty(country))
                {
                    try
                    {
                        var text = await client.GetStringAsync("https://1.1.1.1/cdn-cgi/trace", ct);
                        foreach (var line in text.Split('\n'))
                        {
                            var trimmed = line.Trim();
                            if (trimmed.StartsWith("ip=", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(ip))
                                ip = trimmed.Substring(3).Trim();
                            else if (trimmed.StartsWith("loc=", StringComparison.OrdinalIgnoreCase))
                            {
                                var c = trimmed.Substring(4).Trim().ToUpperInvariant();
                                if (c.Length == 2 && c != "T1" && c != "XX")
                                    country = c;
                            }
                        }
                    }
                    catch { }
                }

                if (!string.IsNullOrEmpty(country) && country.Length == 2)
                {
                    ConnectedServerRegion = country;
                }
                if (!string.IsNullOrEmpty(ip))
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

    private static int SanitizeListenPort(int port)
        => port is >= 1 and <= 65535 ? port : 0;

    private static bool IsPortBindable(IPAddress addr, int port, out string reason)
    {
        reason = "";
        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(addr, port);
            listener.Start();
            return true;
        }
        catch (SocketException ex)
        {
            reason = ex.SocketErrorCode == SocketError.AddressAlreadyInUse
                ? "already in use"
                : ex.SocketErrorCode.ToString();
            return false;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }
        finally
        {
            try { listener?.Stop(); } catch { }
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

    protected string StageFile(string sourcePath, string destPath)
    {
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException(
                $"Bundled {EngineDisplayName} resource missing: {Path.GetFileName(sourcePath)}",
                sourcePath);
        }
        if (!FileCacheHelper.IsCachedCopyUpToDate(sourcePath, destPath))
        {
            try { File.Copy(sourcePath, destPath, overwrite: true); }
            catch (IOException) when (File.Exists(destPath)) { }
        }
        return destPath;
    }

    private static readonly System.Text.RegularExpressions.Regex RustLogPrefixRegex = new(
        @"^\[\d{4}-\d{2}-\d{2}T[\d:.]+Z\s+(?:INFO|WARN|ERROR|DEBUG|TRACE)\s+[^\]]+\]\s*",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    protected void Log(string line)
    {
        LogLineAppended?.Invoke(this, line);
    }

    private void OnLineReceived(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
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
