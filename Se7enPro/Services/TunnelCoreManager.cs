using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using Microsoft.Extensions.Logging;
using Se7enPro.Models;

namespace Se7enPro.Services;

public sealed class TunnelCoreManager : ITunnelCoreManager, IConnectionEngine, IDisposable
{
    private const int MaxLogLines = 5000;

    private readonly ILogger<TunnelCoreManager> _logger;
    private readonly ISettingsService _settings;
    private readonly IChildProcessGuard _childGuard;
    private readonly object _stateLock = new();
    private readonly List<string> _recentLog = new();
    private Process? _process;
    private CancellationTokenSource? _cts;
    private string? _workDir;
    private volatile bool _userWantsConnection;
    private CancellationTokenSource? _retryDelayCts;

    private int _consecutiveFastFailures;
    private DateTime _lastStartUtc;
    private const int MaxConsecutiveFastFailures = 6;
    private static readonly TimeSpan FastFailWindow = TimeSpan.FromSeconds(20);

    private int _processGeneration;

    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);

    private static readonly object _upstreamLock = new();
    private static string? _upstreamProxyUrlOverride;
    public static string? UpstreamProxyUrlOverride { get { lock (_upstreamLock) return _upstreamProxyUrlOverride; } set { lock (_upstreamLock) _upstreamProxyUrlOverride = value; } }
    public static void SetUpstreamProxyUrlOverride(string v) { lock (_upstreamLock) _upstreamProxyUrlOverride = v; }
    public static void ClearUpstreamProxyUrlOverride() { lock (_upstreamLock) _upstreamProxyUrlOverride = null; }
    internal static string? TryGetUpstreamProxyUrlOverride() { lock (_upstreamLock) return _upstreamProxyUrlOverride; }

    public TunnelCoreManager(
        ILogger<TunnelCoreManager> logger,
        ISettingsService settings,
        IChildProcessGuard childGuard)
    {
        _logger = logger;
        _settings = settings;
        _childGuard = childGuard;
    }

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public int SocksProxyPort { get; private set; }
    public int HttpProxyPort { get; private set; }
    public string ClientRegion { get; private set; } = "";

    public string ConnectedServerRegion { get; private set; } = "";

    public string CurrentRouteIp { get; private set; } = "";
    public string CurrentRouteSni { get; private set; } = "";

    public long BytesSent { get; private set; }
    public long BytesReceived { get; private set; }
    public double DownSpeedBytesPerSec => 0;
    public double UpSpeedBytesPerSec => 0;

    public int ConnectProgressPercent { get; private set; }
    public string ConnectProgressText { get; private set; } = "";

    private readonly List<string> _availableRegions = new();
    public IReadOnlyList<string> AvailableEgressRegions => _availableRegions.AsReadOnly();

    public ConnectionMethod Method => ConnectionMethod.Psiphon;
    public IReadOnlyList<string> CoreProcessNames { get; } = new[] { EngineProcessNames.Psiphon };

    public event EventHandler? ConnectProgressChanged;

    private void SetConnectProgress(int percent, string text)
    {
        percent = Math.Clamp(percent, 0, 100);
        ConnectProgressPercent = percent;
        ConnectProgressText = text;
        try { ConnectProgressChanged?.Invoke(this, EventArgs.Empty); } catch { }
    }

    public IReadOnlyList<string> RecentLog
    {
        get
        {
            lock (_stateLock) return _recentLog.ToArray();
        }
    }

    public event EventHandler<ConnectionState>? StateChanged;
    public event EventHandler<Notice>? NoticeReceived;
    public event EventHandler<string>? LogLineAppended;
    public event EventHandler? BytesTransferredChanged;
    public event EventHandler? LogCleared;
    public event EventHandler? RouteChanged;

    public Task StartAsync() => RunGatedAsync(StartAsyncCore);

    private void StartAsyncCore()
    {

        var wasWanting = _userWantsConnection;
        _userWantsConnection = true;
        if (!wasWanting) _consecutiveFastFailures = 0;
        CancelPendingRestart();

        if (_process is not null && !_process.HasExited)
        {
            _logger.LogInformation("Tunnel is already running");
            return;
        }

        SetState(ConnectionState.Connecting);
        AppendLog("Starting tunnel...");
        LogSanitizer.ResetScanState();

        BytesSent = 0;
        BytesReceived = 0;
        ConnectedServerRegion = "";
        CurrentRouteIp = "";
        CurrentRouteSni = "";
        BytesTransferredChanged?.Invoke(this, EventArgs.Empty);
        RouteChanged?.Invoke(this, EventArgs.Empty);

        if (!TryValidateConfiguredPorts(out var portError))
        {
            AppendLog(portError);
            _logger.LogWarning("Listen-port preflight failed: {Error}", portError);
            _userWantsConnection = false;
            CancelPendingRestart();
            SetState(ConnectionState.Error);
            return;
        }

        try
        {
            _workDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Se7en",
                "tunnel-core");
            Directory.CreateDirectory(_workDir);

            var exePath = ResolveTunnelCoreExe();
            var configPath = Path.Combine(_workDir, "config.json");
            File.WriteAllText(configPath, BuildConfigJson());

            var serverListPath = WriteEmbeddedServerList();

            _cts = new CancellationTokenSource();

            _processGeneration++;
            var generation = _processGeneration;

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true,
                WorkingDirectory = _workDir,
            };
            psi.ArgumentList.Add("--config");
            psi.ArgumentList.Add(configPath);
            if (serverListPath is not null)
            {
                psi.ArgumentList.Add("--serverList");
                psi.ArgumentList.Add(serverListPath);
            }

            _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _process.OutputDataReceived += (_, e) => OnLineReceived(e.Data, stderr: false);
            _process.ErrorDataReceived += (_, e) => OnLineReceived(e.Data, stderr: true);
            _process.Exited += (_, _) => OnProcessExited(generation);

            if (!_process.Start())
            {
                throw new InvalidOperationException("Failed to start psiphon-tunnel-core.exe");
            }

            _childGuard.Adopt(_process);

            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            _lastStartUtc = DateTime.UtcNow;
            _logger.LogInformation("psiphon-tunnel-core started (pid {Pid})", _process.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start tunnel");
            AppendLog($"Failed to start: {ex.Message}");
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

    public Task RestartAsync()
    {
        return RunGatedAsync(() =>
        {
            if (State != ConnectionState.Connected && State != ConnectionState.Connecting)
            {
                return Task.CompletedTask;
            }
            return StopAsyncCoreAsync().ContinueWith(_ => StartAsyncCore());
        });
    }

    public Task StopAsync() => RunGatedAsync(StopAsyncCoreAsync);

    private async Task StopAsyncCoreAsync()
    {
        _userWantsConnection = false;
        _consecutiveFastFailures = 0;
        CancelPendingRestart();

        var proc = _process;
        if (proc is null || proc.HasExited)
        {
            DisposeProcessQuietly(proc);
            _process = null;
            SetState(ConnectionState.Disconnected);
            return;
        }

        SetState(ConnectionState.Disconnecting);
        AppendLog("Stopping tunnel...");
        LogSanitizer.ResetScanState();

        try
        {

            try { proc.StandardInput.Close(); } catch {  }

            if (!await WaitForExitAsync(proc, TimeSpan.FromSeconds(5)))
            {
                _logger.LogWarning("Tunnel did not exit gracefully; killing");
                try { proc.Kill(entireProcessTree: true); } catch {  }
                await WaitForExitAsync(proc, TimeSpan.FromSeconds(5));
            }
        }
        finally
        {
            _cts?.Cancel();
            _process = null;
            DisposeProcessQuietly(proc);

            ConnectedServerRegion = "";
            CurrentRouteIp = "";
            CurrentRouteSni = "";
            BytesSent = 0;
            BytesReceived = 0;
            BytesTransferredChanged?.Invoke(this, EventArgs.Empty);
            RouteChanged?.Invoke(this, EventArgs.Empty);

            SetState(ConnectionState.Disconnected);

            lock (_stateLock) _recentLog.Clear();
            LogCleared?.Invoke(this, EventArgs.Empty);

            AppendLog("Stopped tunnel");
        }
    }

    public void CancelConnecting()
    {
        _userWantsConnection = false;
        CancelPendingRestart();
        try { _cts?.Cancel(); } catch { }
    }

    private static async Task<bool> WaitForExitAsync(Process p, TimeSpan timeout)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            await p.WaitForExitAsync(cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return p.HasExited;
        }
    }

    private void OnProcessExited(int generation)
    {

        if (generation != _processGeneration)
        {
            _logger.LogInformation(
                "Ignoring stale tunnel-core exit (generation {Generation} ≠ {Current})",
                generation, _processGeneration);
            return;
        }

        var proc = _process;
        var exitCode = -1;
        try { if (proc is not null && proc.HasExited) exitCode = proc.ExitCode; } catch { }
        _logger.LogInformation("psiphon-tunnel-core exited with code {Code}", exitCode);

        var ranFor = DateTime.UtcNow - _lastStartUtc;
        _process = null;

        var toDispose = proc;
        _ = Task.Run(() => DisposeProcessQuietly(toDispose));

        if (State is ConnectionState.Disconnecting or ConnectionState.Disconnected || !_userWantsConnection)
        {
            return;
        }

        AppendLog($"tunnel-core exited unexpectedly (code {exitCode}).");
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
            AppendLog("Auto-restarting tunnel-core...");
            SetState(ConnectionState.Connecting);
            ScheduleAutoRestart(TimeSpan.FromSeconds(3));
            return;
        }

        _consecutiveFastFailures++;
        if (_consecutiveFastFailures >= MaxConsecutiveFastFailures)
        {
            _userWantsConnection = false;
            CancelPendingRestart();
            AppendLog(
                $"tunnel-core failed {_consecutiveFastFailures} times in a row without staying up. "
              + "Giving up to avoid a restart loop â€” check your port settings and network, then press Connect to retry.");
            _logger.LogError(
                "Giving up after {Count} consecutive fast failures", _consecutiveFastFailures);
            SetState(ConnectionState.Error);
            return;
        }

        var delaySeconds = Math.Min(60, 3 * (1 << (_consecutiveFastFailures - 1)));
        AppendLog(
            $"tunnel-core exited too quickly; retrying in {delaySeconds}s "
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
            try
            {
                await Task.Delay(delay, cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (!_userWantsConnection)
            {

                if (State == ConnectionState.Connecting) SetState(ConnectionState.Disconnected);
                return;
            }
            try { await StartAsync(); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Auto-restart attempt failed");
                if (_userWantsConnection)
                {
                    ScheduleAutoRestart(TimeSpan.FromSeconds(10));
                }
            }
        });
    }

    private void CancelPendingRestart()
    {
        var cts = _retryDelayCts;
        _retryDelayCts = null;
        if (cts is null) return;
        try { cts.Cancel(); } catch {  }
        try { cts.Dispose(); } catch {  }
    }

    private void OnLineReceived(string? line, bool stderr)
    {
        if (string.IsNullOrWhiteSpace(line)) return;

        try
        {
            var notice = JsonSerializer.Deserialize<Notice>(line);
            if (notice is not null && !string.IsNullOrEmpty(notice.NoticeType))
            {
                HandleNotice(notice);
                NoticeReceived?.Invoke(this, notice);
                var pretty = LogSanitizer.FormatNotice(notice.NoticeType, notice.Data);
                if (!string.IsNullOrEmpty(pretty))
                {
                    AppendLog(pretty!);
                }
                return;
            }
        }
        catch
        {

        }

        if (stderr)
        {
            AppendLog(LogSanitizer.Scrub(line));
        }
    }

    private void HandleNotice(Notice notice)
    {
        switch (notice.NoticeType)
        {
            case "Tunnels":
                {
                    var count = notice.Data.TryGetProperty("count", out var c) && c.ValueKind == JsonValueKind.Number
                        ? c.GetInt32()
                        : 0;
                    if (count > 0)
                    {

                        _consecutiveFastFailures = 0;
                        SetState(ConnectionState.Connected);
                    }
                    else if (State == ConnectionState.Connected)
                    {
                        SetState(ConnectionState.Connecting);
                    }
                    break;
                }

            case "ListeningSocksProxyPort":
                if (notice.Data.TryGetProperty("port", out var sp) && sp.ValueKind == JsonValueKind.Number)
                {
                    SocksProxyPort = sp.GetInt32();
                    SetConnectProgress(35, "Local SOCKS proxy ready");
                }
                break;

            case "ListeningHttpProxyPort":
                if (notice.Data.TryGetProperty("port", out var hp) && hp.ValueKind == JsonValueKind.Number)
                {
                    HttpProxyPort = hp.GetInt32();
                    SetConnectProgress(45, "Local HTTP proxy ready");
                }
                break;

            case "ConnectingServer":
                SetConnectProgress(65, "Connecting to tunnel server...");
                break;

            case "EstablishedServer":
                SetConnectProgress(80, "Handshake verified with server...");
                break;

            case "ClientRegion":
                if (notice.Data.TryGetProperty("region", out var cr) && cr.ValueKind == JsonValueKind.String)
                {
                    ClientRegion = cr.GetString() ?? "";
                }
                break;

            case "ConnectedServerRegion":
                if (notice.Data.TryGetProperty("serverRegion", out var srv) && srv.ValueKind == JsonValueKind.String)
                {
                    var connected = srv.GetString() ?? "";
                    ConnectedServerRegion = connected;
                    var requested = _settings.Settings.EgressRegion;
                    if (!string.IsNullOrEmpty(requested) && !string.Equals(requested, connected, StringComparison.OrdinalIgnoreCase))
                    {
                        AppendLog($"[Psiphon] Connected to server in region {connected} (requested: {requested}).");
                    }
                    RouteChanged?.Invoke(this, EventArgs.Empty);
                }
                break;

            case "AvailableEgressRegions":
                if (notice.Data.TryGetProperty("regions", out var regs) && regs.ValueKind == JsonValueKind.Array)
                {
                    _availableRegions.Clear();
                    foreach (var r in regs.EnumerateArray())
                    {
                        if (r.ValueKind == JsonValueKind.String)
                        {
                            var s = r.GetString();
                            if (!string.IsNullOrEmpty(s)) _availableRegions.Add(s);
                        }
                    }
                    RouteChanged?.Invoke(this, EventArgs.Empty);
                }
                break;

            case "BytesTransferred":
                {
                    var changed = false;
                    JsonElement target = notice.Data;
                    if (notice.Data.TryGetProperty("bytesTransferred", out var bt) && bt.ValueKind == JsonValueKind.Object)
                    {
                        target = bt;
                    }

                    if ((target.TryGetProperty("sent", out var bs) || target.TryGetProperty("bytesSent", out bs) || target.TryGetProperty("tx", out bs)) && bs.ValueKind == JsonValueKind.Number)
                    {
                        var d = bs.GetInt64();
                        if (d > 0) { BytesSent += d; changed = true; }
                    }
                    if ((target.TryGetProperty("received", out var br) || target.TryGetProperty("bytesReceived", out br) || target.TryGetProperty("rx", out br)) && br.ValueKind == JsonValueKind.Number)
                    {
                        var d = br.GetInt64();
                        if (d > 0) { BytesReceived += d; changed = true; }
                    }
                    if ((target.TryGetProperty("bytes", out var bTotal) || target.TryGetProperty("totalBytes", out bTotal)) && bTotal.ValueKind == JsonValueKind.Number)
                    {
                        var total = bTotal.GetInt64();
                        if (total > BytesReceived) { BytesReceived = total; changed = true; }
                    }

                    if (changed)
                    {
                        BytesTransferredChanged?.Invoke(this, EventArgs.Empty);
                    }
                    break;
                }

            case "ActiveTunnel":
                if (notice.Data.TryGetProperty("dialAddress", out var da) && da.ValueKind == JsonValueKind.String)
                {
                    var dialAddr = UnescapeRouteToken(da.GetString() ?? "");
                    var protocol = notice.Data.TryGetProperty("protocol", out var proto) && proto.ValueKind == JsonValueKind.String
                        ? proto.GetString() : "";
                    var ipChanged = false;
                    var sniChanged = false;
                    if (dialAddr.Length > 0)
                    {
                        AppendLog($"Route: {dialAddr} via {protocol}");
                        var newIp = ExtractIpFromDialAddress(dialAddr);
                        if (!string.IsNullOrEmpty(newIp) && newIp != CurrentRouteIp)
                        {
                            CurrentRouteIp = newIp;
                            ipChanged = true;
                        }
                    }
                    if (notice.Data.TryGetProperty("meekSNIServerName", out var ms) && ms.ValueKind == JsonValueKind.String)
                    {
                        var sni = UnescapeRouteToken(ms.GetString() ?? "");
                        if (sni != CurrentRouteSni)
                        {
                            CurrentRouteSni = sni;
                            sniChanged = true;
                        }
                    }
                    if (ipChanged || sniChanged)
                    {
                        MaybePersistFoundRoute();
                        RouteChanged?.Invoke(this, EventArgs.Empty);
                    }
                    SetConnectProgress(90, "Tunnel route established");
                }
                break;

            case "Info":
                if (notice.Data.TryGetProperty("message", out var im) && im.ValueKind == JsonValueKind.String)
                {
                    var msg = im.GetString() ?? "";
                    var (foundIp, foundSni) = TryParseCdnScanFound(msg);
                    if (!string.IsNullOrEmpty(foundIp))
                    {
                        var anyChange = false;
                        if (foundIp != CurrentRouteIp)
                        {
                            CurrentRouteIp = foundIp;
                            anyChange = true;
                        }
                        if (!string.IsNullOrEmpty(foundSni) && foundSni != CurrentRouteSni)
                        {
                            CurrentRouteSni = foundSni!;
                            anyChange = true;
                        }
                        if (anyChange)
                        {
                            MaybePersistFoundRoute();
                            RouteChanged?.Invoke(this, EventArgs.Empty);
                        }
                    }
                }
                break;
        }
    }

    private static string ExtractIpFromDialAddress(string dialAddress)
    {
        var hostPart = dialAddress;
        var colonIdx = dialAddress.LastIndexOf(':');
        if (colonIdx > 0) hostPart = dialAddress[..colonIdx];
        return hostPart.Trim('[', ']');
    }

    private static readonly System.Text.RegularExpressions.Regex CdnScanFoundRegex =
        new(@"cdn fronting scan found \(ip:\s*([^\s,)]+),\s*sni:\s*([^\s,)]+)\)",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    private static (string Ip, string Sni) TryParseCdnScanFound(string msg)
    {
        var m = CdnScanFoundRegex.Match(msg);
        if (!m.Success) return ("", "");
        return (UnescapeRouteToken(m.Groups[1].Value), UnescapeRouteToken(m.Groups[2].Value));
    }

    private static string UnescapeRouteToken(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value.Replace("\\", "");
    }

    private void MaybePersistFoundRoute()
    {
        var settings = _settings.Settings;
        if (!settings.SaveFoundIpsAndSni) return;
        if (string.IsNullOrEmpty(CurrentRouteIp) && string.IsNullOrEmpty(CurrentRouteSni)) return;

        var changed = false;
        if (!string.IsNullOrEmpty(CurrentRouteIp))
        {
            var newList = AppendUniqueLine(settings.CdnFrontingCustomIpList, CurrentRouteIp);
            if (newList != settings.CdnFrontingCustomIpList)
            {
                settings.CdnFrontingCustomIpList = newList;
                changed = true;
            }
        }
        if (!string.IsNullOrEmpty(CurrentRouteSni))
        {
            var newSnis = AppendUniqueLine(settings.CdnFrontingCustomSni, CurrentRouteSni);
            if (newSnis != settings.CdnFrontingCustomSni)
            {
                settings.CdnFrontingCustomSni = newSnis;
                changed = true;
            }
        }
        if (changed)
        {
            _settings.Save();
            AppendLog($"Saved route: ip={CurrentRouteIp} sni={CurrentRouteSni}");
        }
    }

    private static string AppendUniqueLine(string current, string value)
    {
        var trimmed = value.Trim();
        if (string.IsNullOrEmpty(trimmed)) return current;
        var lines = (current ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var ln in lines)
        {
            if (string.Equals(ln.Trim(), trimmed, StringComparison.OrdinalIgnoreCase))
                return current ?? "";
        }
        var existing = (current ?? "").TrimEnd('\r', '\n');
        return existing.Length == 0 ? trimmed : existing + Environment.NewLine + trimmed;
    }

    private string BuildConfigJson()
    {
        var s = _settings.Settings;
        var dataRoot = Path.Combine(_workDir!, "data");
        Directory.CreateDirectory(dataRoot);

        var cfg = new JsonObject
        {
            ["ClientPlatform"] = $"{EmbeddedValues.ClientPlatform}_{Environment.OSVersion.Version}",
            ["ClientVersion"] = EmbeddedValues.ClientVersion,
            ["PropagationChannelId"] = EmbeddedValues.PropagationChannelId,
            ["SponsorId"] = EmbeddedValues.SponsorId,
            ["RemoteServerListURLs"] = JsonNode.Parse(EmbeddedValues.RemoteServerListUrlsJson),
            ["ObfuscatedServerListRootURLs"] = JsonNode.Parse(EmbeddedValues.ObfuscatedServerListRootUrlsJson),
            ["RemoteServerListSignaturePublicKey"] = EmbeddedValues.RemoteServerListSignaturePublicKey,
            ["ServerEntrySignaturePublicKey"] = EmbeddedValues.ServerEntrySignaturePublicKey,
            ["DataRootDirectory"] = dataRoot,
            ["MigrateDataStoreDirectory"] = dataRoot,
            ["UseIndistinguishableTLS"] = true,
            ["EmitDiagnosticNotices"] = true,
            ["EmitDiagnosticNetworkParameters"] = true,
            ["EmitServerAlerts"] = true,

            ["EmitBytesTransferred"] = true,
            ["FeedbackUploadURLs"] = JsonNode.Parse(EmbeddedValues.FeedbackUploadUrlsJson),
            ["FeedbackEncryptionPublicKey"] = EmbeddedValues.FeedbackEncryptionPublicKey,
            ["EnableFeedbackUpload"] = true,

            ["EstablishTunnelTimeoutSeconds"] = s.EstablishTunnelTimeoutSeconds ?? 300,

            ["LocalHttpProxyPort"] = s.UseCustomProxyPorts ? SanitizeListenPort(s.LocalHttpProxyPort, "HTTP") : 0,
            ["LocalSocksProxyPort"] = s.UseCustomProxyPorts ? SanitizeListenPort(s.LocalSocksProxyPort, "SOCKS") : 0,
        };

        var authUser = s.LanAuthEnabled ? s.LanProxyUsername : null;
        var authPass = s.LanAuthEnabled ? s.LanProxyPassword : null;
        var lanBind = LanExposurePolicy.ResolveBindAddress(
            s.AllowLanConnections,
            authUser,
            authPass,
            engineEnforcesCredentials: true,
            out var lanReason);

        if (IPAddress.Any.Equals(lanBind))
        {
            cfg["ListenInterface"] = "any";
            cfg["LocalProxyUsername"] = s.LanAuthEnabled ? s.LanProxyUsername : "";
            cfg["LocalProxyPassword"] = s.LanAuthEnabled ? s.LanProxyPassword : "";
        }
        else if (!string.IsNullOrEmpty(lanReason))
        {
            AppendLog(lanReason);
        }

        if (!string.IsNullOrEmpty(s.EgressRegion))
        {
            cfg["EgressRegion"] = s.EgressRegion;
        }

        if (s.DisableTimeouts)
        {
            cfg["NetworkLatencyMultiplierLambda"] = 0.1;
        }

        string upstreamProxyUrl = "";
        if (!string.IsNullOrEmpty(UpstreamProxyUrlOverride))
        {
            upstreamProxyUrl = UpstreamProxyUrlOverride;
        }
        else if (s.ProtocolMode.Equals("conduit", StringComparison.OrdinalIgnoreCase) ||
                 s.ProtocolMode.Equals("cdn_fronting", StringComparison.OrdinalIgnoreCase))
        {
            if (s.UpstreamProxyEnabled && !string.IsNullOrWhiteSpace(s.UpstreamProxy))
            {
                AppendLog($"Upstream proxy bypassed for {s.ProtocolMode} mode (direct connection required).");
            }
            upstreamProxyUrl = "";
        }
        else
        {
            upstreamProxyUrl = BuildUpstreamProxyUrl(s);
        }

        cfg["UpstreamProxyUrl"] = upstreamProxyUrl;

        if (!string.IsNullOrEmpty(upstreamProxyUrl))
        {
            AppendLog($"Using upstream proxy: {LogSanitizer.Scrub(upstreamProxyUrl)} "
                    + "— every tunnel connection is dialled through it.");
            _ = PreflightUpstreamProxyAsync(upstreamProxyUrl);
        }

        ApplyAdvancedTunnelConfig(cfg, s);

        return cfg.ToJsonString();
    }

    private static void ApplyAdvancedTunnelConfig(JsonObject cfg, Models.UserSettings s)
    {
        if (!string.IsNullOrEmpty(UpstreamProxyUrlOverride))
        {

            cfg["LimitTunnelProtocols"] = new JsonArray(
                "FRONTED-MEEK-OSSH",
                "FRONTED-MEEK-HTTP-OSSH",
                "TLS-OSSH",
                "UNFRONTED-MEEK-HTTPS-OSSH",
                "UNFRONTED-MEEK-OSSH",
                "SHADOWSOCKS-OSSH",
                "OSSH",
                "SSH");
            cfg["InproxyAllowClient"] = false;
            cfg["InproxyEnableProxy"] = false;
            cfg["InproxyBrokerSpecs"] = new JsonArray();
            cfg["InproxyTunnelProtocolSelectionProbability"] = 0.0;
            cfg["HoldOffInproxyTunnelProbability"] = 1.0;
            return;
        }

        if (s.BeastMode && s.ProtocolMode != "conduit")
        {
            cfg["AggressiveEstablishment"] = true;
        }

        switch (s.ProtocolMode)
        {
            case "cdn_fronting":

                cfg["LimitTunnelProtocols"] = new JsonArray(
                    "FRONTED-MEEK-CDN-OSSH");
                cfg["DisableTactics"] = true;
                cfg["InproxyAllowClient"] = false;
                cfg["InproxyEnableProxy"] = false;
                cfg["InproxyBrokerSpecs"] = new JsonArray();
                cfg["InproxyTunnelProtocolSelectionProbability"] = 0.0;
                cfg["InproxyTunnelProtocolPreferProbability"] = 0.0;
                cfg["HoldOffInproxyTunnelProbability"] = 1.0;

                var hasUserIpList =
                    CdnFrontingBuilder.ParseCdnFrontingCustomIpList(s.CdnFrontingCustomIpList).Count > 0;

                bool includeBuiltInDefaults;
                bool includeFastly = true;
                bool includeAkamai = true;

                if (s.AutoFindIpAndSni && s.FrontedMeekCDNScanBuiltInSets is { Count: > 0 } customSets)
                {

                    includeAkamai = customSets.Any(x => x != null && x.Contains("akamai", StringComparison.OrdinalIgnoreCase));
                    includeFastly = customSets.Any(x => x != null && x.Contains("fastly", StringComparison.OrdinalIgnoreCase));
                    includeBuiltInDefaults = includeAkamai || includeFastly;
                }
                else
                {
                    includeBuiltInDefaults = s.AutoFindIpAndSni || !hasUserIpList;
                }

                cfg["FrontedMeekDialOverrides"] = CdnFrontingBuilder.BuildDialOverrides(
                    s.CdnFrontingCustomIpList,
                    s.CdnFrontingCustomSni,
                    includeBuiltInDefaults,
                    s.CdnFrontingSkipCertVerify,
                    includeFastly: includeFastly,
                    includeAkamai: includeAkamai);
                cfg["FrontedMeekDialOverridesProbability"] = 1.0;

                cfg["FrontedMeekCDNScanUseBuiltInSpec"] = s.AutoFindIpAndSni;

                if (s.AutoFindIpAndSni && s.FrontedMeekCDNScanBuiltInSets is { Count: > 0 } sets)
                {
                    var arr = new JsonArray();
                    foreach (var name in sets)
                    {
                        var trimmed = name?.Trim();
                        if (!string.IsNullOrEmpty(trimmed))
                            arr.Add(JsonValue.Create(trimmed));
                    }
                    if (arr.Count > 0)
                        cfg["FrontedMeekCDNScanBuiltInSets"] = arr;
                }
                break;

            case "direct":
                cfg["LimitTunnelProtocols"] = new JsonArray(
                    "SSH", "OSSH", "TLS-OSSH",
                    "UNFRONTED-MEEK-OSSH",
                    "UNFRONTED-MEEK-HTTPS-OSSH",
                    "UNFRONTED-MEEK-SESSION-TICKET-OSSH",
                    "QUIC-OSSH", "SHADOWSOCKS-OSSH",
                    "FRONTED-MEEK-OSSH",
                    "FRONTED-MEEK-CDN-OSSH",
                    "FRONTED-MEEK-HTTP-OSSH",
                    "FRONTED-MEEK-CDN-HTTP-OSSH",
                    "FRONTED-MEEK-QUIC-OSSH",
                    "FRONTED-MEEK-CDN-QUIC-OSSH");

                cfg["DisableTactics"] = true;
                cfg["InproxyAllowClient"] = false;
                cfg["InproxyEnableProxy"] = false;
                cfg["InproxyBrokerSpecs"] = new JsonArray();
                cfg["InproxyTunnelProtocolSelectionProbability"] = 0.0;
                cfg["InproxyTunnelProtocolPreferProbability"] = 0.0;
                cfg["HoldOffInproxyTunnelProbability"] = 1.0;
                break;

            case "conduit":
                cfg["LimitTunnelProtocols"] = new JsonArray(
                    "INPROXY-WEBRTC-SSH",
                    "INPROXY-WEBRTC-OSSH",
                    "INPROXY-WEBRTC-TLS-OSSH",
                    "INPROXY-WEBRTC-UNFRONTED-MEEK-OSSH",
                    "INPROXY-WEBRTC-UNFRONTED-MEEK-HTTPS-OSSH",
                    "INPROXY-WEBRTC-UNFRONTED-MEEK-SESSION-TICKET-OSSH",
                    "INPROXY-WEBRTC-FRONTED-MEEK-OSSH",
                    "INPROXY-WEBRTC-FRONTED-MEEK-HTTP-OSSH",
                    "INPROXY-WEBRTC-QUIC-OSSH",
                    "INPROXY-WEBRTC-FRONTED-MEEK-QUIC-OSSH",
                    "INPROXY-WEBRTC-SHADOWSOCKS-OSSH");
                cfg["InproxyAllowClient"] = true;
                cfg["InproxyEnableProxy"] = false;
                cfg["HoldOffInproxyTunnelProbability"] = 0.0;

                if (!string.IsNullOrWhiteSpace(s.ConduitCompartmentId) && s.ConduitMode != "public")
                {
                    cfg["InproxyClientPersonalCompartmentID"] = s.ConduitCompartmentId.Trim();
                }

                if (s.ConduitRejectCensoredCountries)
                {
                    cfg["InproxyRejectProxyCountryCodes"] = new JsonArray(
                        "IR", "CN", "RU", "BY", "TM", "KP");
                }
                break;

            case "auto":
            default:

                cfg["InproxyDelaySeconds"] = 15;
                cfg["LimitTunnelProtocols"] = new JsonArray(
                    "SSH", "OSSH", "TLS-OSSH",
                    "UNFRONTED-MEEK-OSSH",
                    "UNFRONTED-MEEK-HTTPS-OSSH",
                    "UNFRONTED-MEEK-SESSION-TICKET-OSSH",
                    "QUIC-OSSH", "SHADOWSOCKS-OSSH",
                    "FRONTED-MEEK-OSSH",
                    "FRONTED-MEEK-CDN-OSSH",
                    "FRONTED-MEEK-HTTP-OSSH",
                    "FRONTED-MEEK-CDN-HTTP-OSSH",
                    "FRONTED-MEEK-QUIC-OSSH",
                    "FRONTED-MEEK-CDN-QUIC-OSSH",
                    "INPROXY-WEBRTC-SSH",
                    "INPROXY-WEBRTC-OSSH",
                    "INPROXY-WEBRTC-TLS-OSSH",
                    "INPROXY-WEBRTC-UNFRONTED-MEEK-OSSH",
                    "INPROXY-WEBRTC-UNFRONTED-MEEK-HTTPS-OSSH",
                    "INPROXY-WEBRTC-UNFRONTED-MEEK-SESSION-TICKET-OSSH",
                    "INPROXY-WEBRTC-FRONTED-MEEK-OSSH",
                    "INPROXY-WEBRTC-FRONTED-MEEK-HTTP-OSSH",
                    "INPROXY-WEBRTC-QUIC-OSSH",
                    "INPROXY-WEBRTC-FRONTED-MEEK-QUIC-OSSH",
                    "INPROXY-WEBRTC-SHADOWSOCKS-OSSH");
                break;
        }
    }

    private const string CachedTunnelExeName = "Se7enPro.Tunnel.exe";

    private string ResolveTunnelCoreExe()
    {

        var appDir = AppContext.BaseDirectory;

        var candidates = new[]
        {
            Path.Combine(appDir, "Resources", "psiphon-tunnel-core.exe"),
            Path.Combine(appDir, "psiphon-tunnel-core.exe"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "psiphon-tunnel-core.exe"),
            Path.GetFullPath(Path.Combine(appDir, @"..\..\..\..\..\Se7enPro\Resources\psiphon-tunnel-core.exe")),
            Path.GetFullPath(Path.Combine(appDir, @"..\..\..\..\Se7enPro\Resources\psiphon-tunnel-core.exe")),
            Path.GetFullPath(Path.Combine(appDir, @"..\..\..\Se7enPro\Resources\psiphon-tunnel-core.exe")),
            Path.GetFullPath(Path.Combine(appDir, @"..\Resources\psiphon-tunnel-core.exe")),
        };

        var bundled = candidates.FirstOrDefault(File.Exists);
        if (bundled == null)
        {
            bundled = Path.Combine(appDir, "Resources", "psiphon-tunnel-core.exe");
            if (!File.Exists(bundled))
            {
                throw new FileNotFoundException(
                    "psiphon-tunnel-core.exe not found next to Se7enPro",
                    bundled);
            }
        }

        var copyTo = Path.Combine(_workDir!, CachedTunnelExeName);

        foreach (var stale in Directory.EnumerateFiles(_workDir!, "*.exe"))
        {
            if (string.Equals(Path.GetFileName(stale), CachedTunnelExeName,
                              StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            try { File.Delete(stale); } catch {  }
        }

        FileCacheHelper.StageFileSafe(bundled, copyTo, _logger, new[] { CachedTunnelExeName, "psiphon-tunnel-core", "Se7enPro.Tunnel" });

        return copyTo;
    }

    private string? WriteEmbeddedServerList()
    {
        try
        {
            var plain = SecretStore.DecryptResource("Se7enPro.Resources.server_entries.bin");
            var dest = Path.Combine(_workDir!, "server_entries.txt");
            File.WriteAllBytes(dest, plain);
            Array.Clear(plain, 0, plain.Length);
            return dest;
        }
        catch
        {
            var plainTextFallback = Path.Combine(AppContext.BaseDirectory, "Resources", "server_entries.txt");
            if (File.Exists(plainTextFallback))
            {
                var dest = Path.Combine(_workDir!, "server_entries.txt");
                File.Copy(plainTextFallback, dest, overwrite: true);
                return dest;
            }
            _logger.LogWarning("Embedded server list unavailable; tunnel-core will rely on remote server list fetch");
            return null;
        }
    }

    private int SanitizeListenPort(int port, string label = "Proxy")
    {
        if (port <= 0) return 0;
        if (port > 65535)
        {
            _logger.LogWarning("{Label} port {Port} exceeds maximum allowed TCP port 65535. Falling back to dynamic port.", label, port);
            return 0;
        }
        return port;
    }

    private bool TryValidateConfiguredPorts(out string error)
    {
        error = "";
        var s = _settings.Settings;

        var authUser = s.LanAuthEnabled ? s.LanProxyUsername : null;
        var authPass = s.LanAuthEnabled ? s.LanProxyPassword : null;
        var bindAddr = LanExposurePolicy.ResolveBindAddress(
            s.AllowLanConnections,
            authUser,
            authPass,
            engineEnforcesCredentials: true,
            out _);

        var socks = s.UseCustomProxyPorts ? SanitizeListenPort(s.LocalSocksProxyPort, "SOCKS") : 0;
        var http = s.UseCustomProxyPorts ? SanitizeListenPort(s.LocalHttpProxyPort, "HTTP") : 0;

        if (socks != 0 && http != 0 && socks == http)
        {
            error = $"SOCKS and HTTP are both set to port {socks}. "
                  + "Give them different ports, or set one to 0 (auto), in Settings.";
            return false;
        }

        foreach (var (label, port) in new[] { ("SOCKS", socks), ("HTTP", http) })
        {
            if (port == 0) continue;
            if (!IsPortBindable(bindAddr, port, out var reason))
            {
                error = $"The {label} port {port} can't be opened ({reason}). "
                      + "Pick a different port or set it to 0 (auto) in Settings, then press Connect.";
                return false;
            }
        }
        return true;
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

    private void DisposeProcessQuietly(Process? proc)
    {
        if (proc is null) return;
        try { proc.CancelOutputRead(); } catch { }
        try { proc.CancelErrorRead(); } catch { }
        try { proc.Dispose(); } catch { }
    }

    private static string BuildUpstreamProxyUrl(Models.UserSettings s)
    {
        if (!s.UpstreamProxyEnabled || string.IsNullOrWhiteSpace(s.UpstreamProxy))
            return "";

        var raw = s.UpstreamProxy.Trim();
        var scheme = (s.UpstreamProxyScheme ?? "").Trim().ToLowerInvariant();
        if (scheme != "socks5" && scheme != "socks5h" && scheme != "http" && scheme != "https")
        {
            scheme = "http";
        }

        string hostAndPort = raw;
        if (raw.Contains("://"))
        {
            try
            {
                var uri = new Uri(raw);
                scheme = uri.Scheme.ToLowerInvariant();
                hostAndPort = uri.Authority;
                if (string.IsNullOrEmpty(uri.Authority)) hostAndPort = raw.Substring(raw.IndexOf("://", StringComparison.Ordinal) + 3);
            }
            catch
            {
                var parts = raw.Split(new[] { "://" }, 2, StringSplitOptions.None);
                scheme = parts[0].ToLowerInvariant();
                hostAndPort = parts[1].Split('/')[0];
            }
        }

        if (scheme != "socks5" && scheme != "socks5h" && scheme != "http" && scheme != "https")
            scheme = "http";

        var user = (s.UpstreamProxyUsername ?? "").Trim();
        var pass = (s.UpstreamProxyPassword ?? "").Trim();
        string auth = "";
        if (!string.IsNullOrEmpty(user))
        {
            auth = !string.IsNullOrEmpty(pass)
                ? $"{Uri.EscapeDataString(user)}:{Uri.EscapeDataString(pass)}@"
                : $"{Uri.EscapeDataString(user)}@";
        }

        if (hostAndPort.Contains('@'))
        {
            return $"{scheme}://{hostAndPort}";
        }

        return $"{scheme}://{auth}{hostAndPort}";
    }

    private async Task PreflightUpstreamProxyAsync(string proxyUrl)
    {
        string host;
        string scheme;
        int port;
        try
        {
            var uri = new Uri(proxyUrl);
            host = uri.Host;
            scheme = uri.Scheme.ToLowerInvariant();
            port = uri.Port > 0
                ? uri.Port
                : (scheme == "http" ? 8080 : 1080);
            if (string.IsNullOrEmpty(host)) return;
        }
        catch { return; }

        string? problem;
        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await client.ConnectAsync(host, port, cts.Token);

            var stream = client.GetStream();
            problem = scheme.StartsWith("socks", StringComparison.Ordinal)
                ? await ProbeSocksProxyAsync(stream, scheme, cts.Token)
                : await ProbeHttpProxyAsync(stream, cts.Token);
        }
        catch (OperationCanceledException)
        {
            problem = "it never answered";
        }
        catch (Exception ex)
        {
            problem = ex.Message;
        }

        if (problem is null) return;

        AppendLog($"WARNING: upstream proxy {LogSanitizer.Scrub($"{host}:{port}")} is not usable â€” "
                + $"{problem}. Psiphon dials every connection through it, so the tunnel cannot "
                + "establish in any protocol mode while it stays like this. Turn \"Use upstream "
                + "proxy\" off (or clear the address) in Settings -> Upstream proxy and connect "
                + "again.");
    }

    private static async Task<string?> ProbeSocksProxyAsync(
        NetworkStream stream, string scheme, CancellationToken ct)
    {
        if (scheme is "socks4" or "socks4a")
        {

            var name = Encoding.ASCII.GetBytes("www.google.com");
            var request = new byte[9 + name.Length + 1];
            request[0] = 4;
            request[1] = 1;
            request[2] = 0;
            request[3] = 80;
            request[7] = 1;
            Buffer.BlockCopy(name, 0, request, 9, name.Length);
            await stream.WriteAsync(request, ct);

            var reply = new byte[8];
            return await ReadExactlyAsync(stream, reply, ct)
                ? null
                : "it accepted the connection but never answered the SOCKS4 request";
        }

        await stream.WriteAsync(new byte[] { 5, 2, 0, 2 }, ct);

        var greeting = new byte[2];
        if (!await ReadExactlyAsync(stream, greeting, ct))
            return "it accepted the connection but never answered the SOCKS5 greeting";

        if (greeting[0] != 5) return "it did not answer as a SOCKS5 proxy";
        if (greeting[1] == 0xFF) return "it rejected both authentication methods offered";
        return null;
    }

    private static async Task<string?> ProbeHttpProxyAsync(NetworkStream stream, CancellationToken ct)
    {
        var request = Encoding.ASCII.GetBytes(
            "CONNECT www.google.com:443 HTTP/1.1\r\nHost: www.google.com:443\r\n\r\n");
        await stream.WriteAsync(request, ct);

        var buffer = new byte[64];
        var read = await stream.ReadAsync(buffer, ct);
        if (read <= 0)
            return "it accepted the connection but never answered an HTTP CONNECT";

        var reply = Encoding.ASCII.GetString(buffer, 0, read);
        return reply.StartsWith("HTTP/", StringComparison.Ordinal)
            ? null
            : "it did not answer as an HTTP proxy";
    }

    private static async Task<bool> ReadExactlyAsync(
        NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct);
            if (n <= 0) return false;
            read += n;
        }
        return true;
    }

    private static string GetSystemHttpProxy()
    {
        try
        {
            var systemProxy = System.Net.WebRequest.GetSystemWebProxy();

            var probe = new Uri("https://example.com/");
            var proxyUri = systemProxy.GetProxy(probe);

            if (proxyUri is null || proxyUri.Equals(probe) || systemProxy.IsBypassed(probe))
                return "";

            if (IsLoopbackHost(proxyUri.Host)) return "";

            return $"http://{proxyUri.Host}:{proxyUri.Port}";
        }
        catch
        {
            return "";
        }
    }

    private static bool IsLoopbackHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip);
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
                BytesSent = 0;
                BytesReceived = 0;
                BytesTransferredChanged?.Invoke(this, EventArgs.Empty);
            }
            else if (s == ConnectionState.Connecting && ConnectProgressPercent == 0)
            {
                SetConnectProgress(15, "Connecting to Psiphon network...");
            }
        }

        StateChanged?.Invoke(this, s);
    }

    private void AppendLog(string line)
    {
        lock (_stateLock)
        {
            _recentLog.Add($"{DateTime.Now:HH:mm:ss} {line}");
            if (_recentLog.Count > MaxLogLines)
            {
                _recentLog.RemoveRange(0, _recentLog.Count - MaxLogLines);
            }
        }
        LogLineAppended?.Invoke(this, line);
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch {  }
        try { _process?.Kill(entireProcessTree: true); } catch {  }
        _process?.Dispose();
        _cts?.Dispose();
    }
}
