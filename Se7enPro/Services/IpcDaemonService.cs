using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Se7enPro.Models;

namespace Se7enPro.Services;

public sealed class IpcDaemonService : IAsyncDisposable
{
    private const string PipeName = "se7en_ipc";
    private readonly ILogger<IpcDaemonService> _logger;
    private readonly ITunnelCoreManager _connectionManager;
    private readonly ITunManager _tun;
    private readonly ISettingsService _settings;
    private readonly V2RayEngine _v2ray;
    private readonly ShardEngine _shard;
    private readonly CancellationTokenSource _cts = new();

    private readonly ConcurrentDictionary<string, Func<string, Task>> _connectedClients = new();
    private TcpListener? _tcpListener;
    private int _tcpPort;
    private string? _portFilePath;
    private string? _tokenFilePath;

    private string? _sessionToken;

    private static readonly TimeSpan AuthTimeout = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan AutoExitDelay = TimeSpan.FromSeconds(30);
    private CancellationTokenSource? _autoExitCts;

    public IpcDaemonService(
        ILogger<IpcDaemonService> logger,
        ITunnelCoreManager connectionManager,
        ITunManager tun,
        ISettingsService settings,
        V2RayEngine v2ray,
        ShardEngine shard)
    {
        _logger = logger;
        _connectionManager = connectionManager;
        _tun = tun;
        _settings = settings;
        _v2ray = v2ray;
        _shard = shard;

        _connectionManager.StateChanged += OnStateChanged;
        _connectionManager.LogLineAppended += OnLogLineAppended;
        _connectionManager.ConnectProgressChanged += OnConnectProgressChanged;
        _connectionManager.BytesTransferredChanged += OnBytesTransferredChanged;
        _connectionManager.RouteChanged += OnRouteChanged;
        _tun.StateChanged += OnTunStateChanged;
        _tun.LogLineAppended += OnTunLogLineAppended;
        _settings.SettingsChanged += OnSettingsChanged;
    }

    public void Start()
    {
        _logger.LogInformation("Starting Se7en IPC Daemon...");

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dir = Path.Combine(localAppData, "Se7en");
        Directory.CreateDirectory(dir);

        _tokenFilePath = Path.Combine(dir, IpcAuth.TokenFileName);
        try
        {
            _sessionToken = IpcAuth.CreateSessionToken(_tokenFilePath);
        }
        catch (Exception ex)
        {

            _logger.LogError(ex, "Could not create the IPC session token; the TCP transport will not be started.");
            _sessionToken = null;
        }

        _ = Task.Run(() => RunNamedPipeServerLoopAsync(_cts.Token));

        if (_sessionToken is null) return;

        try
        {
            _tcpListener = new TcpListener(IPAddress.Loopback, 0);
            _tcpListener.Start();
            _tcpPort = ((IPEndPoint)_tcpListener.LocalEndpoint).Port;

            _portFilePath = Path.Combine(dir, IpcAuth.PortFileName);
            AtomicFile.WriteAllText(_portFilePath, _tcpPort.ToString());
            IpcAuth.RestrictFileToCurrentUser(_portFilePath);

            _logger.LogInformation("IPC Server listening on Named Pipe \\\\.\\pipe\\{Pipe} and TCP port {Port}", PipeName, _tcpPort);

            _ = Task.Run(() => RunTcpServerLoopAsync(_cts.Token));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not start companion TCP listener; Named Pipe will be sole transport.");
        }
    }

    private async Task RunNamedPipeServerLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {

                var pipeSecurity = IpcAuth.TryBuildPipeSecurity();
                var pipeServer = pipeSecurity is null
                    ? new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous)
                    : NamedPipeServerStreamAcl.Create(
                        PipeName,
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous,
                        inBufferSize: 0,
                        outBufferSize: 0,
                        pipeSecurity);

                await pipeServer.WaitForConnectionAsync(ct);
                _logger.LogInformation("Client connected via Windows Named Pipe.");

                var clientId = Guid.NewGuid().ToString("N");

                _ = Task.Run(() => HandleClientStreamAsync(clientId, pipeServer, ct, requireAuth: false), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error in Named Pipe accept loop, retrying in 500ms");

                try { await Task.Delay(500, CancellationToken.None); } catch { }
            }
        }
    }

    private async Task RunTcpServerLoopAsync(CancellationToken ct)
    {
        if (_tcpListener == null) return;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await _tcpListener.AcceptTcpClientAsync(ct);
                client.NoDelay = true;
                client.ReceiveBufferSize = 65536;
                client.SendBufferSize = 65536;
                client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

                _logger.LogInformation("Client connected via Localhost TCP ({Endpoint}).", client.Client.RemoteEndPoint);

                var clientId = Guid.NewGuid().ToString("N");
                var stream = client.GetStream();
                _ = Task.Run(() => HandleClientStreamAsync(clientId, stream, ct, requireAuth: true, client), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error in TCP accept loop, retrying in 500ms");
                try { await Task.Delay(500, CancellationToken.None); } catch { }
            }
        }
    }

    private async Task HandleClientStreamAsync(
        string clientId,
        Stream stream,
        CancellationToken ct,
        bool requireAuth,
        IDisposable? parentResource = null)
    {
        var writeLock = new SemaphoreSlim(1, 1);

        async Task SendLineAsync(string jsonLine)
        {
            try
            {
                await writeLock.WaitAsync(ct);
                try
                {
                    var bytes = Encoding.UTF8.GetBytes(jsonLine + "\n");
                    await stream.WriteAsync(bytes, ct);
                    await stream.FlushAsync(ct);
                }
                finally
                {
                    writeLock.Release();
                }
            }
            catch
            {

            }
        }

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 65536, leaveOpen: true);

        if (requireAuth)
        {
            using var authCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            authCts.CancelAfter(AuthTimeout);

            string? authLine;
            try
            {
                authLine = await reader.ReadLineAsync(authCts.Token);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("IPC: client {Id} did not authenticate within {Seconds}s; dropping.",
                    clientId, AuthTimeout.TotalSeconds);
                await CloseUnauthenticatedAsync(SendLineAsync, "auth timeout");
                Cleanup();
                return;
            }

            if (!TryAuthenticate(authLine))
            {
                _logger.LogWarning("IPC: client {Id} failed authentication; dropping.", clientId);
                await CloseUnauthenticatedAsync(SendLineAsync, "invalid or missing token");
                Cleanup();
                return;
            }
        }

        _connectedClients[clientId] = SendLineAsync;

        CancelAutoExit();

        await SendLineAsync(CreateStatusMessageJson());

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line == null) break;

                if (!string.IsNullOrWhiteSpace(line))
                {
                    var trimmed = line.Trim();

                    if (IsOffloadableQuery(trimmed))
                    {
                        _ = Task.Run(async () =>
                        {
                            var queryResponse = await ProcessCommandAsync(trimmed);
                            if (!string.IsNullOrEmpty(queryResponse))
                            {
                                await SendLineAsync(queryResponse);
                            }
                        }, ct);
                    }
                    else
                    {
                        var response = await ProcessCommandAsync(trimmed);
                        if (!string.IsNullOrEmpty(response))
                        {
                            await SendLineAsync(response);
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Client connection ended: {Id}", clientId);
        }
        finally
        {
            Cleanup();
            _logger.LogInformation("Client disconnected: {Id}", clientId);

            if (_connectedClients.IsEmpty)
            {

                ScheduleAutoExit();
            }
        }

        void Cleanup()
        {
            _connectedClients.TryRemove(clientId, out _);
            try { writeLock.Dispose(); } catch { }
            try { stream.Dispose(); } catch { }
            try { parentResource?.Dispose(); } catch { }
        }
    }

    private bool TryAuthenticate(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        var trimmed = line.Trim();

        string? presented = null;
        if (trimmed.StartsWith("{", StringComparison.Ordinal))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                var root = doc.RootElement;
                if (root.TryGetProperty("cmd", out var cmd) &&
                    string.Equals(cmd.GetString(), "auth", StringComparison.OrdinalIgnoreCase) &&
                    root.TryGetProperty("token", out var tok))
                {
                    presented = tok.GetString();
                }
            }
            catch
            {
                return false;
            }
        }
        else
        {
            presented = trimmed;
        }

        return IpcAuth.TokenEquals(_sessionToken, presented);
    }

    private static async Task CloseUnauthenticatedAsync(Func<string, Task> send, string reason)
    {
        try
        {
            await send(JsonSerializer.Serialize(new { @event = "auth_failed", reason }));
        }
        catch { }
    }

    private void DeletePortFile()
    {
        try
        {
            if (!string.IsNullOrEmpty(_portFilePath) && File.Exists(_portFilePath))
            {
                File.Delete(_portFilePath);
            }
        }
        catch { }
        try
        {
            if (!string.IsNullOrEmpty(_tokenFilePath) && File.Exists(_tokenFilePath))
            {
                File.Delete(_tokenFilePath);
            }
        }
        catch { }
    }

    private void ScheduleAutoExit()
    {
        var fresh = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _autoExitCts, fresh);
        try { previous?.Cancel(); } catch { }
        try { previous?.Dispose(); } catch { }

        var token = fresh.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(AutoExitDelay, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (_connectedClients.IsEmpty)
            {
                _logger.LogInformation("All UI clients disconnected. Terminating background daemon.");
                try { await _connectionManager.StopAsync(); } catch { }
                try { await _tun.DisposeAsync(); } catch { }
                DeletePortFile();
                Environment.Exit(0);
            }
        }, CancellationToken.None);
    }

    private void CancelAutoExit()
    {
        var cts = Interlocked.Exchange(ref _autoExitCts, null);
        try { cts?.Cancel(); } catch { }
        try { cts?.Dispose(); } catch { }
    }

    private static bool IsOffloadableQuery(string commandLine)
    {
        try
        {
            using var doc = JsonDocument.Parse(commandLine);
            if (!doc.RootElement.TryGetProperty("cmd", out var cmdProp)) return false;
            return cmdProp.GetString()?.ToLowerInvariant() switch
            {
                "test_ping" or "get_installed_apps" or "test_v2ray_config" or "refresh_shard_pool" or "get_shard_info" or "rotate_shard_node" => true,
                _ => false,
            };
        }
        catch
        {

            return false;
        }
    }

    private async Task<string?> ProcessCommandAsync(string commandLine)
    {
        try
        {
            using var doc = JsonDocument.Parse(commandLine);
            var root = doc.RootElement;
            if (!root.TryGetProperty("cmd", out var cmdProp))
            {
                return null;
            }

            var cmd = cmdProp.GetString()?.ToLowerInvariant();
            switch (cmd)
            {
                case "auth":

                    return JsonSerializer.Serialize(new { @event = "auth_ok" });

                case "status":
                    return CreateStatusMessageJson();

                case "exit":
                case "shutdown":
                    _logger.LogInformation("IPC: Received shutdown command from UI.");
                    _ = Task.Run(async () =>
                    {
                        try { await _connectionManager.StopAsync(); } catch { }
                        try { await _tun.DisposeAsync(); } catch { }
                        DeletePortFile();
                        Environment.Exit(0);
                    });
                    return CreateAckJson("shutdown");

                case "connect":
                    _logger.LogInformation("IPC: Received connect command.");
                    _ = Task.Run(async () =>
                    {
                        try { await _connectionManager.StartAsync(); }
                        catch (Exception ex) { _logger.LogError(ex, "Connect failed"); }
                    });
                    return CreateAckJson("connect");

                case "restart_as_admin":
                    _logger.LogInformation("IPC: Requesting restart as administrator.");
                    _ = Task.Run(() => RestartAsAdministrator());
                    return CreateAckJson("restart_as_admin");

                case "disconnect":
                    _logger.LogInformation("IPC: Received disconnect command.");
                    _ = Task.Run(async () =>
                    {
                        try { await _connectionManager.StopAsync(); }
                        catch (Exception ex) { _logger.LogError(ex, "Disconnect failed"); }
                    });
                    return CreateAckJson("disconnect");

                case "set_method":
                    if (root.TryGetProperty("method", out var methodProp))
                    {
                        var methodToken = methodProp.GetString();
                        _logger.LogInformation("IPC: Setting connection method to: {Method}", methodToken);
                        _settings.Settings.ConnectionMethod = methodToken ?? "psiphon";
                        _settings.Save();
                        if (_connectionManager.State is ConnectionState.Connected or ConnectionState.Connecting)
                        {
                            _ = Task.Run(async () =>
                            {
                                try { await _connectionManager.RestartAsync(); }
                                catch (Exception ex) { _logger.LogError(ex, "Restart failed during set_method"); }
                            });
                        }
                    }
                    return CreateStatusMessageJson();

                case "set_egress_region":
                    if (root.TryGetProperty("region", out var regProp))
                    {
                        var reg = regProp.GetString() ?? "";
                        var activeMethod = (_connectionManager as ConnectionManager)?.ActiveMethod;
                        var isTor = false;
                        if (root.TryGetProperty("isTor", out var isTorProp) &&
                            (isTorProp.ValueKind is JsonValueKind.True or JsonValueKind.False))
                        {
                            isTor = isTorProp.GetBoolean();
                        }
                        else
                        {
                            isTor = (activeMethod.HasValue && activeMethod.Value.IsTor())
                                || ConnectionMethodExtensions.ParseConnectionMethod(_settings.Settings.ConnectionMethod).IsTor();
                        }

                        if (isTor)
                        {
                            _settings.Settings.TorExitCountry = reg.Trim().ToLowerInvariant();
                        }
                        else
                        {
                            _settings.Settings.EgressRegion = reg.Trim().ToUpperInvariant();
                        }
                        _logger.LogInformation("IPC: set_egress_region reg='{Region}' (isTor={IsTor}) -> TorExitCountry='{Tor}', EgressRegion='{Egress}'",
                            reg, isTor, _settings.Settings.TorExitCountry, _settings.Settings.EgressRegion);
                        _settings.Save();

                        if (_connectionManager.State is ConnectionState.Connected or ConnectionState.Connecting)
                        {
                            _ = Task.Run(async () =>
                            {
                                try { await _connectionManager.RestartAsync(); }
                                catch (Exception ex) { _logger.LogError(ex, "Restart failed during set_egress_region"); }
                            });
                        }
                    }
                    return CreateStatusMessageJson();

                case "apply_settings":
                    if (root.TryGetProperty("settings", out var settingsProp))
                    {
                        if (settingsProp.GetRawText().Length > 512 * 1024)
                        {
                            _logger.LogWarning("IPC: apply_settings payload too large; rejected.");
                            return JsonSerializer.Serialize(new { @event = "error", message = "settings payload too large" });
                        }
                        var rawJson = settingsProp.GetRawText();
                        var updated = JsonSerializer.Deserialize<UserSettings>(rawJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        if (updated != null)
                        {

                            updated.ConnectionMethod = ConnectionMethodExtensions.ParseConnectionMethod(updated.ConnectionMethod).ToToken();
                            updated.EgressRegion = (updated.EgressRegion ?? "").Trim().ToUpperInvariant();
                            updated.TorExitCountry = (updated.TorExitCountry ?? "").Trim().ToLowerInvariant();
                            updated.LocalSocksProxyPort = UserSettings.SanitizePort(updated.LocalSocksProxyPort);
                            updated.LocalHttpProxyPort = UserSettings.SanitizePort(updated.LocalHttpProxyPort);
                            _logger.LogInformation("IPC: Applied updated user settings.");
                            _settings.Update(updated);
                        }
                    }
                    return CreateStatusMessageJson();

                case "get_installed_apps":
                    var apps = await InstalledAppsProvider.LoadAsync();
                    return JsonSerializer.Serialize(new
                    {
                        @event = "installed_apps",
                        apps = apps.Select(a => new
                        {
                            name = a.Name,
                            exe = a.FileName,
                            exePath = a.ExePath,
                            fileName = a.FileName,
                            isRunning = a.IsRunning,
                            icon = a.IconBase64
                        })
                    });

                case "clear_logs":
                    return CreateAckJson("clear_logs");

                case "test_ping":
                    var host = root.TryGetProperty("host", out var h) ? h.GetString() : null;
                    var port = root.TryGetProperty("port", out var p) ? p.GetInt32() : 443;
                    var latency = await MeasureTcpPingAsync(host, port);
                    return JsonSerializer.Serialize(new
                    {
                        @event = "ping_result",
                        host = host,
                        port = port,
                        latencyMs = latency
                    });

                case "test_v2ray_config":
                    var testId = root.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
                    V2RayConfigEntry? testConfig = null;
                    if (root.TryGetProperty("config", out var configProp))
                    {
                        try
                        {
                            testConfig = JsonSerializer.Deserialize<V2RayConfigEntry>(
                                configProp.GetRawText(),
                                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        }
                        catch { }
                    }

                    if (testConfig == null && !string.IsNullOrEmpty(testId))
                    {
                        testConfig = _settings.Settings.V2RayConfigs?.FirstOrDefault(c => c.Id == testId);
                    }

                    var v2rayLatency = -3;
                    if (testConfig != null)
                    {
                        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(7));
                        v2rayLatency = await _v2ray.MeasureRealDelayAsync(testConfig, testCts.Token);
                    }

                    return JsonSerializer.Serialize(new
                    {
                        @event = "v2ray_ping_result",
                        id = testId,
                        latencyMs = v2rayLatency
                    });

                case "get_recent_log":
                    var logs = _connectionManager.RecentLog;
                    return JsonSerializer.Serialize(new
                    {
                        @event = "recent_log",
                        lines = logs
                    });

                case "get_shard_info":
                    var shardSummary = _shard.GetPoolSummary();
                    return JsonSerializer.Serialize(new
                    {
                        @event = "shard_info",
                        nodeCount = shardSummary.NodeCount,
                        pathCount = shardSummary.PathCount,
                        lastCheck = shardSummary.LastCheckUtc.ToString("o"),
                        success = shardSummary.Success
                    });

                case "refresh_shard_pool":
                    var forceShard = root.TryGetProperty("force", out var forceProp) && forceProp.GetBoolean();
                    var refreshed = await _shard.RefreshSubscriptionAsync(forceShard);
                    return JsonSerializer.Serialize(new
                    {
                        @event = "shard_info",
                        nodeCount = refreshed.NodeCount,
                        pathCount = refreshed.PathCount,
                        lastCheck = refreshed.LastCheckUtc.ToString("o"),
                        success = refreshed.Success
                    });

                case "rotate_shard_node":
                    await _shard.RotateNodeAsync();
                    return JsonSerializer.Serialize(new { @event = "shard_rotated", success = true });

                default:
                    return JsonSerializer.Serialize(new { @event = "unknown_cmd", cmd = cmd });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse command: {Command}", commandLine);
            return JsonSerializer.Serialize(new { @event = "error", message = ex.Message });
        }
    }

    private void BroadcastJson(string json)
    {
        foreach (var client in _connectedClients.Values)
        {
            _ = client(json);
        }
    }

    private void OnStateChanged(object? sender, ConnectionState state)
    {
        BroadcastJson(CreateStatusMessageJson());
    }

    private void OnLogLineAppended(object? sender, string line)
    {
        BroadcastJson(JsonSerializer.Serialize(new
        {
            @event = "log",
            line = line
        }));
    }

    private void OnConnectProgressChanged(object? sender, EventArgs e)
    {
        BroadcastJson(JsonSerializer.Serialize(new
        {
            @event = "progress",
            percent = _connectionManager.ConnectProgressPercent,
            text = _connectionManager.ConnectProgressText
        }));
    }

    private void OnBytesTransferredChanged(object? sender, EventArgs e)
    {
        BroadcastJson(JsonSerializer.Serialize(new
        {
            @event = "bytes",
            sent = _connectionManager.BytesSent,
            received = _connectionManager.BytesReceived,
            downSpeed = _connectionManager.DownSpeedBytesPerSec,
            upSpeed = _connectionManager.UpSpeedBytesPerSec
        }));
    }

    private void OnRouteChanged(object? sender, EventArgs e)
    {
        BroadcastJson(JsonSerializer.Serialize(new
        {
            @event = "route",
            ip = _connectionManager.CurrentRouteIp,
            sni = _connectionManager.CurrentRouteSni,
            serverRegion = _connectionManager.ConnectedServerRegion
        }));
        BroadcastJson(CreateStatusMessageJson());
    }

    private void OnTunStateChanged(object? sender, EventArgs e)
    {
        BroadcastJson(CreateStatusMessageJson());
    }

    private void OnTunLogLineAppended(object? sender, string line)
    {
        OnLogLineAppended(sender, $"[TUN] {line}");
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        try
        {
            BroadcastJson(JsonSerializer.Serialize(new
            {
                @event = "settings_updated",
                settings = _settings.Settings
            }));
        }
        catch { }
        BroadcastJson(CreateStatusMessageJson());
    }

    private string CreateStatusMessageJson()
    {
        var status = new Dictionary<string, object?>
        {
            ["state"] = _connectionManager.State.ToString().ToLowerInvariant(),
            ["socksProxyPort"] = _connectionManager.SocksProxyPort,
            ["httpProxyPort"] = _connectionManager.HttpProxyPort,
            ["clientRegion"] = _connectionManager.ClientRegion,
            ["connectedServerRegion"] = _connectionManager.ConnectedServerRegion,
            ["currentRouteIp"] = _connectionManager.CurrentRouteIp,
            ["currentRouteSni"] = _connectionManager.CurrentRouteSni,
            ["bytesSent"] = _connectionManager.BytesSent,
            ["bytesReceived"] = _connectionManager.BytesReceived,
            ["downSpeed"] = _connectionManager.DownSpeedBytesPerSec,
            ["upSpeed"] = _connectionManager.UpSpeedBytesPerSec,
            ["connectProgressPercent"] = _connectionManager.ConnectProgressPercent,
            ["connectProgressText"] = _connectionManager.ConnectProgressText,
            ["availableEgressRegions"] = _connectionManager.AvailableEgressRegions,
            ["activeMethodToken"] = (_connectionManager as ConnectionManager)?.ActiveMethod.ToToken() ?? _settings.Settings.ConnectionMethod,
            ["tunActive"] = _tun.State == TunState.Running,
            ["tunStatusText"] = _tun.State.ToString(),

            ["tunLastError"] = _tun.LastError ?? "",
            ["upstreamProxyDisplay"] = _settings.Settings.UpstreamProxy,
            ["isAdmin"] = IsAdministrator(),
        };

        return JsonSerializer.Serialize(new
        {
            @event = "status",
            data = status
        });
    }

    private static bool IsAdministrator()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private void RestartAsAdministrator()
    {
        var exePath = Environment.ProcessPath
                      ?? Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
        {
            _logger.LogWarning("restart_as_admin: could not resolve the daemon executable; staying put.");
            return;
        }

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = "--daemon",
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = true,
            Verb = "runas",
        };

        if (string.Equals(Path.GetFileNameWithoutExtension(exePath), "dotnet", StringComparison.OrdinalIgnoreCase))
        {

            var dll = System.Reflection.Assembly.GetEntryAssembly()?.Location;
            if (string.IsNullOrEmpty(dll) || !File.Exists(dll))
            {
                _logger.LogWarning("restart_as_admin: running under dotnet but the entry assembly is not on disk; staying put.");
                return;
            }
            psi.Arguments = $"\"{dll}\" --daemon";
        }

        AdminElevation.ReleaseDaemonMutexAction?.Invoke();

        Process? elevated;
        try
        {
            elevated = Process.Start(psi);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {

            _logger.LogInformation("restart_as_admin: elevation declined ({Message}); continuing unelevated.", ex.Message);
            AdminElevation.ReacquireDaemonMutexAction?.Invoke();
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "restart_as_admin: failed to launch the elevated daemon.");
            AdminElevation.ReacquireDaemonMutexAction?.Invoke();
            return;
        }

        if (elevated is null)
        {
            AdminElevation.ReacquireDaemonMutexAction?.Invoke();
            return;
        }

        _ = Task.Run(async () =>
        {

            try { await _connectionManager.StopAsync(); }
            catch (Exception ex) { _logger.LogWarning(ex, "restart_as_admin: tunnel stop failed"); }
            try { await _tun.DisposeAsync(); }
            catch (Exception ex) { _logger.LogWarning(ex, "restart_as_admin: TUN teardown failed"); }

            _logger.LogInformation("restart_as_admin: handing over to the elevated daemon (pid {Pid}).", elevated.Id);
            Environment.Exit(0);
        });
    }

    private static string CreateAckJson(string cmd)
    {
        return JsonSerializer.Serialize(new { @event = "ack", cmd = cmd });
    }

    private static async Task<int> MeasureTcpPingAsync(string? host, int port)
    {
        if (string.IsNullOrWhiteSpace(host)) return -1;
        try
        {
            var sw = Stopwatch.StartNew();
            using var tcp = new TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await tcp.ConnectAsync(host, port, cts.Token);
            sw.Stop();
            return (int)sw.ElapsedMilliseconds;
        }
        catch
        {
            return -1;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        CancelAutoExit();

        _connectionManager.StateChanged -= OnStateChanged;
        _connectionManager.LogLineAppended -= OnLogLineAppended;
        _connectionManager.ConnectProgressChanged -= OnConnectProgressChanged;
        _connectionManager.BytesTransferredChanged -= OnBytesTransferredChanged;
        _connectionManager.RouteChanged -= OnRouteChanged;
        _tun.StateChanged -= OnTunStateChanged;
        _tun.LogLineAppended -= OnTunLogLineAppended;
        _settings.SettingsChanged -= OnSettingsChanged;

        try { _tcpListener?.Stop(); } catch { }
        if (!string.IsNullOrEmpty(_portFilePath) && File.Exists(_portFilePath))
        {
            try { File.Delete(_portFilePath); } catch { }
        }

        _connectedClients.Clear();
        _cts.Dispose();
        await Task.CompletedTask;
    }
}
