using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using PsiphonUI.Models;

namespace PsiphonUI.Services;

public sealed class TunnelCoreManager : ITunnelCoreManager, IDisposable
{
    private const int MaxLogLines = 5000;

    private readonly ILogger<TunnelCoreManager> _logger;
    private readonly ISettingsService _settings;
    private readonly ISystemProxyService _systemProxy;
    private readonly IChildProcessGuard _childGuard;
    private readonly object _stateLock = new();
    private readonly List<string> _recentLog = new();
    private Process? _process;
    private CancellationTokenSource? _cts;
    private string? _workDir;
    private volatile bool _userWantsConnection;
    private CancellationTokenSource? _retryDelayCts;

    public TunnelCoreManager(
        ILogger<TunnelCoreManager> logger,
        ISettingsService settings,
        ISystemProxyService systemProxy,
        IChildProcessGuard childGuard)
    {
        _logger = logger;
        _settings = settings;
        _systemProxy = systemProxy;
        _childGuard = childGuard;
    }

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public int SocksProxyPort { get; private set; }
    public int HttpProxyPort { get; private set; }
    public string ClientRegion { get; private set; } = "";

    public string ConnectedServerRegion { get; private set; } = "";

    public long BytesSent { get; private set; }
    public long BytesReceived { get; private set; }

    private readonly List<string> _availableRegions = new();
    public IReadOnlyList<string> AvailableEgressRegions => _availableRegions.AsReadOnly();

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

    public async Task StartAsync()
    {
        _userWantsConnection = true;
        CancelPendingRestart();

        if (_process is not null && !_process.HasExited)
        {
            _logger.LogInformation("Tunnel is already running");
            return;
        }

        SetState(ConnectionState.Connecting);
        AppendLog("[INFO] Starting tunnel...");

        BytesSent = 0;
        BytesReceived = 0;
        ConnectedServerRegion = "";
        BytesTransferredChanged?.Invoke(this, EventArgs.Empty);

        try
        {
            _workDir = RuntimePathSecurity.GetRuntimeDirectory(
                "tunnel-core",
                preferMachineSecure: AdminElevation.IsAdministrator());

            var exePath = ResolveTunnelCoreExe();
            var configPath = Path.Combine(_workDir, "config.json");
            File.WriteAllText(configPath, BuildConfigJson());

            var serverListPath = WriteEmbeddedServerList();

            _cts = new CancellationTokenSource();

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
            _process.Exited += OnProcessExited;

            if (!_process.Start())
            {
                throw new InvalidOperationException("Failed to start psiphon-tunnel-core.exe");
            }

            _childGuard.Adopt(_process);

            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            _logger.LogInformation("psiphon-tunnel-core started (pid {Pid})", _process.Id);
            AppendLog($"[INFO] psiphon-tunnel-core started (pid {_process.Id})");
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start tunnel");
            AppendLog($"[ERROR] Failed to start: {ex.Message}");
            _process = null;
            if (_userWantsConnection)
            {
                AppendLog("[INFO] Auto-retrying in a few seconds...");
                SetState(ConnectionState.Connecting);
                ScheduleAutoRestart(TimeSpan.FromSeconds(5));
            }
            else
            {
                SetState(ConnectionState.Disconnected);
            }
        }
    }

    public async Task RestartAsync()
    {

        if (State != ConnectionState.Connected && State != ConnectionState.Connecting)
        {
            return;
        }

        await StopAsync();
        await StartAsync();
    }

    public async Task StopAsync()
    {
        _userWantsConnection = false;
        CancelPendingRestart();

        var proc = _process;
        if (proc is null || proc.HasExited)
        {
            _process = null;
            SetState(ConnectionState.Disconnected);
            return;
        }

        SetState(ConnectionState.Disconnecting);
        AppendLog("[INFO] Stopping tunnel...");

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

            _systemProxy.Clear();

            ConnectedServerRegion = "";
            BytesSent = 0;
            BytesReceived = 0;
            BytesTransferredChanged?.Invoke(this, EventArgs.Empty);

            SetState(ConnectionState.Disconnected);

            lock (_stateLock) _recentLog.Clear();
            LogCleared?.Invoke(this, EventArgs.Empty);

            AppendLog("[INFO] Tunnel stopped");
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

    private void OnProcessExited(object? sender, EventArgs e)
    {
        var exitCode = _process?.ExitCode ?? -1;
        _logger.LogInformation("psiphon-tunnel-core exited with code {Code}", exitCode);
        AppendLog($"[INFO] Process exited (code {exitCode})");

        _process = null;

        if (State == ConnectionState.Disconnecting)
        {
            return;
        }

        if (_userWantsConnection)
        {
            AppendLog("[INFO] tunnel-core exited unexpectedly; auto-restarting...");
            SetState(ConnectionState.Connecting);
            ScheduleAutoRestart(TimeSpan.FromSeconds(3));
        }
        else
        {
            SetState(ConnectionState.Disconnected);
        }
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
            if (!_userWantsConnection) return;
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

    private static readonly HashSet<string> NoisyNoticeTypes = new(StringComparer.Ordinal)
    {
        "BytesTransferred",
    };

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
                if (!NoisyNoticeTypes.Contains(notice.NoticeType))
                {

                    AppendLog(LogSanitizer.FormatNotice(notice.NoticeType, notice.Data));
                }
                return;
            }
        }
        catch
        {

        }

        AppendLog(LogSanitizer.Scrub((stderr ? "[stderr] " : "") + line));
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
                        SetState(ConnectionState.Connected);
                        if (_settings.Settings.SetSystemProxy && HttpProxyPort > 0)
                        {
                            _systemProxy.Set(HttpProxyPort);
                        }
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
        }
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

            ["LocalHttpProxyPort"] = SanitizeListenPort(s.LocalHttpProxyPort),
            ["LocalSocksProxyPort"] = SanitizeListenPort(s.LocalSocksProxyPort),
        };

        if (s.AllowLanConnections)
        {
            cfg["ListenInterface"] = "any";
        }

        if (!string.IsNullOrEmpty(s.EgressRegion))
        {
            cfg["EgressRegion"] = s.EgressRegion;
        }

        if (s.DisableTimeouts)
        {
            cfg["NetworkLatencyMultiplierLambda"] = 0.1;
        }

        cfg["UpstreamProxyUrl"] = string.IsNullOrWhiteSpace(s.UpstreamProxy)
            ? GetSystemHttpProxy()
            : NormalizeProxyUrl(s.UpstreamProxy);

        ApplyAdvancedTunnelConfig(cfg, s);

        return cfg.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static void ApplyAdvancedTunnelConfig(JsonObject cfg, Models.UserSettings s)
    {
        if (s.BeastMode)
        {
            cfg["AggressiveEstablishment"] = true;
        }

        switch (s.ProtocolMode)
        {
            case "cdn_fronting":

                cfg["LimitTunnelProtocols"] = new JsonArray("FRONTED-MEEK-CDN-OSSH");
                cfg["DisableTactics"] = true;
                cfg["FrontedMeekDialOverrides"] = CdnFrontingBuilder.BuildDialOverrides(
                    s.CdnFrontingCustomIpList, s.CdnFrontingCustomSni);
                cfg["FrontedMeekDialOverridesProbability"] = 1.0;
                break;

            case "direct":

                cfg["LimitTunnelProtocols"] = new JsonArray(
                    "SSH", "OSSH", "TLS-OSSH", "QUIC-OSSH", "SHADOWSOCKS-OSSH");
                cfg["DisableTactics"] = true;
                break;

            case "auto":
            default:

                break;
        }
    }

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
                    "psiphon-tunnel-core.exe not found next to PsiphonUI",
                    bundled);
            }
        }

        var copyTo = Path.Combine(_workDir!, "psiphon-tunnel-core.exe");
        CopyFileWithHashVerification(bundled, copyTo);
        return copyTo;
    }

    private static void CopyFileWithHashVerification(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, overwrite: true);

        var sourceHash = ComputeSha256(source);
        var copiedHash = ComputeSha256(destination);
        if (!sourceHash.AsSpan().SequenceEqual(copiedHash))
        {
            throw new IOException($"Runtime copy verification failed for {Path.GetFileName(destination)}");
        }
    }

    private static byte[] ComputeSha256(string path)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        return sha.ComputeHash(stream);
    }

    private string? WriteEmbeddedServerList()
    {
        var appDir = AppContext.BaseDirectory;
        var bundled = Path.Combine(appDir, "Resources", "server_entries.txt");
        if (!File.Exists(bundled))
        {
            _logger.LogWarning("Embedded server_entries.txt not found; tunnel-core will rely on remote server list fetch");
            return null;
        }

        var dest = Path.Combine(_workDir!, "server_entries.txt");
        File.Copy(bundled, dest, overwrite: true);
        return dest;
    }

    private static int SanitizeListenPort(int port)
        => port is >= 1 and <= 65535 ? port : 0;

    private static string NormalizeProxyUrl(string url)
    {
        url = url.Trim();
        if (string.IsNullOrEmpty(url)) return "";

        if (url.Contains("://")) return url;

        return $"http://{url}";
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

            return $"http://{proxyUri.Host}:{proxyUri.Port}";
        }
        catch
        {
            return "";
        }
    }

    private void SetState(ConnectionState s)
    {
        if (State == s) return;
        State = s;
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
