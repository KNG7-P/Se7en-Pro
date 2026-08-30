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

    public static string? UpstreamProxyUrlOverride;

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
                "Ignoring stale tunnel-core exit (generation {Generation} â‰  {Current})",
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

        if (State == ConnectionState.Disconnecting)
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
                }
                break;

            case "ListeningHttpProxyPort":
                if (notice.Data.TryGetProperty("port", out var hp) && hp.ValueKind == JsonValueKind.Number)
                {
                    HttpProxyPort = hp.GetInt32();
                }
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
                    ConnectedServerRegion = srv.GetString() ?? "";
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
                }
                break;

            case "BytesTransferred":
                {

                    var changed = false;
                    if (notice.Data.TryGetProperty("sent", out var bs) && bs.ValueKind == JsonValueKind.Number)
                    {
                        var d = bs.GetInt64();
                        if (d > 0) { BytesSent += d; changed = true; }
                    }
                    if (notice.Data.TryGetProperty("received", out var br) && br.ValueKind == JsonValueKind.Number)
                    {
                        var d = br.GetInt64();
                        if (d > 0) { BytesReceived += d; changed = true; }
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

            ["EstablishTunnelTimeoutSeconds"] = 0,

            ["LocalHttpProxyPort"] = SanitizeListenPort(s.LocalHttpProxyPort),
            ["LocalSocksProxyPort"] = SanitizeListenPort(s.LocalSocksProxyPort),
        };

        if (s.AllowLanConnections)
        {
            cfg["ListenInterface"] = "any";

            if (!string.IsNullOrEmpty(s.LanProxyUsername) &&
                !string.IsNullOrEmpty(s.LanProxyPassword))
            {
                cfg["LocalProxyUsername"] = s.LanProxyUsername;
                cfg["LocalProxyPassword"] = s.LanProxyPassword;
            }
        }

        if (!string.IsNullOrEmpty(s.EgressRegion))
        {
            cfg["EgressRegion"] = s.EgressRegion;
        }

        if (s.DisableTimeouts)
        {
            cfg["NetworkLatencyMultiplierLambda"] = 0.1;
        }

        var upstreamProxyUrl = UpstreamProxyUrlOverride
            ?? (s.UpstreamProxyEnabled && !string.IsNullOrWhiteSpace(s.UpstreamProxy)
                ? NormalizeProxyUrl(s.UpstreamProxy)
                : GetSystemHttpProxy());
        cfg["UpstreamProxyUrl"] = upstreamProxyUrl;

        if (!string.IsNullOrEmpty(upstreamProxyUrl))
        {
            AppendLog($"Using upstream proxy: {LogSanitizer.Scrub(upstreamProxyUrl)} "
                    + "â€” every tunnel connection is dialled through it.");
            _ = PreflightUpstreamProxyAsync(upstreamProxyUrl);
        }

        ApplyAdvancedTunnelConfig(cfg, s);

        return cfg.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
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
            cfg["InproxyEnabled"] = false;
            cfg["InproxyAllowClient"] = false;
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

                var hasUserIpList =
                    CdnFrontingBuilder.ParseCdnFrontingCustomIpList(s.CdnFrontingCustomIpList).Count > 0;
                var includeBuiltInDefaults = s.AutoFindIpAndSni || !hasUserIpList;

                cfg["FrontedMeekDialOverrides"] = CdnFrontingBuilder.BuildDialOverrides(
                    s.CdnFrontingCustomIpList,
                    s.CdnFrontingCustomSni,
                    includeBuiltInDefaults);
                cfg["FrontedMeekDialOverridesProbability"] = 1.0;

                cfg["FrontedMeekCDNScanUseBuiltInSpec"] = s.AutoFindIpAndSni;
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
                cfg["InproxyEnabled"] = true;
                cfg["InproxyAllowClient"] = true;

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

                break;
        }
    }

    private const string CachedTunnelExeName = "Se7enPro.Tunnel.exe";

    private string ResolveTunnelCoreExe()
    {

        var appDir = AppContext.BaseDirectory;

        var bundled = Path.Combine(appDir, "Resources", "psiphon-tunnel-core.exe");
        if (!File.Exists(bundled))
        {

            bundled = Path.Combine(appDir, "psiphon-tunnel-core.exe");
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

        if (!FileCacheHelper.IsCachedCopyUpToDate(bundled, copyTo))
        {
            try
            {
                File.Copy(bundled, copyTo, overwrite: true);
            }
            catch (IOException)
            {

                if (!File.Exists(copyTo))
                {
                    throw;
                }
            }
        }

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

    private static int SanitizeListenPort(int port)
        => port is >= 1 and <= 65535 ? port : 0;

    private bool TryValidateConfiguredPorts(out string error)
    {
        error = "";
        var s = _settings.Settings;
        var bindAddr = s.AllowLanConnections ? IPAddress.Any : IPAddress.Loopback;

        var socks = SanitizeListenPort(s.LocalSocksProxyPort);
        var http = SanitizeListenPort(s.LocalHttpProxyPort);

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

    private void DisposeProcessQuietly(Process? proc)
    {
        if (proc is null) return;
        try { proc.CancelOutputRead(); } catch { }
        try { proc.CancelErrorRead(); } catch { }
        try { proc.Dispose(); } catch { }
    }

    private static string NormalizeProxyUrl(string url)
    {
        url = url.Trim();
        if (string.IsNullOrEmpty(url)) return "";

        if (url.Contains("://")) return url;

        return $"http://{url}";
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
        else if (s is ConnectionState.Disconnected or ConnectionState.Error)
        {
            SetConnectProgress(0, "");
        }
        else if (s == ConnectionState.Connecting && ConnectProgressPercent == 0)
        {
            SetConnectProgress(15, "Connecting to Psiphon network...");
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
