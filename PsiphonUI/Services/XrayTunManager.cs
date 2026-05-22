using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using PsiphonUI.Models;

namespace PsiphonUI.Services;

public sealed class XrayTunManager : IXrayTunManager
{

    private const string TunInterfaceName = "xray_tun";

    private const string TunGateway = "172.18.0.1";

    private const string TunLocalIp = "172.18.0.2";
    private const string TunSubnetMask = "255.255.255.252";

    private const int TunMtu = 9000;

    private static readonly string[] TunDnsServers = { "1.1.1.1", "8.8.8.8" };

    private static readonly TimeSpan TunStartupTimeout = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan CreatingAdapterMarkerTimeout = TimeSpan.FromSeconds(5);

    private static readonly JsonSerializerOptions WriteIndentedJson = new()
    {
        WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    private readonly ILogger<XrayTunManager> _logger;
    private readonly IChildProcessGuard _childGuard;
    private readonly ITunnelCoreManager _tunnel;
    private readonly ISettingsService _settings;

    private readonly object _lock = new();
    private readonly SemaphoreSlim _reconcileGate = new(1, 1);

    private Process? _process;
    private CancellationTokenSource? _cts;
    private string? _workDir;
    private string? _xrayLogPath;
    private bool _defaultRouteInstalled;

    private readonly ConcurrentQueue<string> _recentOutput = new();
    private const int RecentOutputMax = 16;

    private StreamWriter? _xrayLogWriter;

    private ulong _excludeAdapterLuid;

    private volatile bool _xrayCreatingAdapterSeen;

    public XrayTunState State { get; private set; } = XrayTunState.Off;
    public string? LastError { get; private set; }

    public event EventHandler? StateChanged;

    public XrayTunManager(
        ILogger<XrayTunManager> logger,
        ITunnelCoreManager tunnel,
        ISettingsService settings,
        IChildProcessGuard childGuard)
    {
        _logger = logger;
        _tunnel = tunnel;
        _settings = settings;
        _childGuard = childGuard;

        _tunnel.StateChanged += OnTunnelStateChanged;
        _settings.SettingsChanged += OnSettingsChanged;
    }

    private async void OnTunnelStateChanged(object? sender, ConnectionState s)
    {
        try { await ReconcileAsync(); }
        catch (Exception ex) { _logger.LogError(ex, "OnTunnelStateChanged failed"); }
    }

    private async void OnSettingsChanged(object? sender, EventArgs e)
    {
        try { await ReconcileAsync(); }
        catch (Exception ex) { _logger.LogError(ex, "OnSettingsChanged failed"); }
    }

    private async Task ReconcileAsync()
    {
        await _reconcileGate.WaitAsync();
        try
        {
            var systemWide = _settings.Settings.SystemWideTunneling;
            var tunnelState = _tunnel.State;
            var socksPort = _tunnel.SocksProxyPort;
            var want = systemWide
                && tunnelState == ConnectionState.Connected
                && socksPort > 0;

            var have = State is XrayTunState.Starting or XrayTunState.Running;

            WriteDiag($"reconcile: systemWide={systemWide} tunnel={tunnelState} "
                    + $"socks={socksPort} want={want} have={have} state={State}");

            if (want && !have)
            {
                await StartAsync(_tunnel.SocksProxyPort);
            }
            else if (!want && have)
            {
                await StopAsync();
            }
            else if (!want && State == XrayTunState.Error)
            {

                SetState(XrayTunState.Off, error: null);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "XrayTunManager.ReconcileAsync failed");
            SetError(ex.Message);
        }
        finally
        {
            _reconcileGate.Release();
        }
    }

    private async Task StartAsync(int socksPort)
    {
        if (!IsAdministrator())
        {
            SetError("System-wide tunneling needs Administrator privileges. "
                   + "Click the toggle again and choose \"Restart as Administrator\".");
            return;
        }

        SetState(XrayTunState.Starting, error: null);
        _recentOutput.Clear();
        _xrayCreatingAdapterSeen = false;

        _excludeAdapterLuid = 0;
        try
        {
            if (NativeMethods.ConvertInterfaceAliasToLuid(TunInterfaceName, out var stale) == 0
                && stale != 0)
            {
                _excludeAdapterLuid = stale;
                WriteDiag($"pre-launch: stale '{TunInterfaceName}' LUID={stale:X} present; will try to physically remove it");
            }
        }
        catch {  }

        try
        {
            await RemoveStaleWintunDeviceAsync();
        }
        catch (Exception ex)
        {
            WriteDiag($"pnputil pre-cleanup threw (continuing): {ex.Message}");
        }
        _excludeAdapterLuid = 0;

        _workDir = RuntimePathSecurity.GetRuntimeDirectory(
            "xray-" + Guid.NewGuid().ToString("N"),
            preferMachineSecure: true);

        var diagDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Psiphon", "logs");
        Directory.CreateDirectory(diagDir);
        _xrayLogPath = Path.Combine(diagDir, "xray-tun.log");
        try
        {
            _xrayLogWriter = new StreamWriter(
                new FileStream(_xrayLogPath, FileMode.Create, FileAccess.Write, FileShare.Read))
            { AutoFlush = true };
            _xrayLogWriter.WriteLine($"# xray TUN session {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Couldn't open xray log file at {Path}", _xrayLogPath);
            _xrayLogWriter = null;
        }

        var appDir = AppContext.BaseDirectory;
        var sourceXrayDir = Path.Combine(appDir, "Resources", "xray");
        if (!Directory.Exists(sourceXrayDir))
        {
            SetError("Bundled xray resources not found next to the app.");
            return;
        }

        foreach (var name in new[] { "xray.exe", "wintun.dll", "geoip.dat", "geosite.dat" })
        {
            var src = Path.Combine(sourceXrayDir, name);
            if (!File.Exists(src))
            {
                SetError($"Bundled xray resource missing: {name}");
                return;
            }
            CopyFileWithHashVerification(src, Path.Combine(_workDir, name));
        }

        var configPath = Path.Combine(_workDir, "config.json");
        var configJson = BuildConfigJson(socksPort, _settings.Settings);

        var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        await File.WriteAllTextAsync(configPath, configJson, utf8NoBom);
        try { _xrayLogWriter?.WriteLine("# config:\n" + configJson); } catch {  }

        _cts = new CancellationTokenSource();

        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(_workDir, "xray.exe"),
            WorkingDirectory = _workDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-config");
        psi.ArgumentList.Add(configPath);

        psi.Environment["XRAY_LOCATION_ASSET"] = _workDir;
        psi.Environment["V2RAY_LOCATION_ASSET"] = _workDir;

        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += OnXrayOutput;
        proc.ErrorDataReceived += OnXrayError;
        proc.Exited += OnXrayExited;

        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            SetError($"Failed to start xray.exe: {ex.Message}");
            return;
        }

        _childGuard.Adopt(proc);

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        lock (_lock) _process = proc;

        var (ready, tunIfaceIndex) = await WaitForTunDeviceAsync(proc, _cts.Token);
        if (!ready)
        {
            if (proc.HasExited)
            {
                SetError($"xray.exe exited (code={proc.ExitCode}) before WinTUN came up. "
                       + $"Last output: {DescribeRecentOutput()}. "
                       + $"Full log: {_xrayLogPath}");
            }
            else
            {
                SetError("Xray didn't bring up the WinTUN device within "
                       + $"{TunStartupTimeout.TotalSeconds:0}s. "
                       + $"Last output: {DescribeRecentOutput()}. "
                       + $"Full log: {_xrayLogPath}");
            }
            await StopAsync();
            return;
        }

        WriteDiag($"WinTUN adapter ready, ifIndex={tunIfaceIndex}");

        try
        {

            ConfigureTunAdapter(tunIfaceIndex);
            WriteDiag("ConfigureTunAdapter done (netsh IP/MTU/DNS applied)");
            InstallDefaultRoute(tunIfaceIndex);
            WriteDiag($"InstallDefaultRoute done (0.0.0.0/0 -> {TunGateway} metric 1 if {tunIfaceIndex})");
            _defaultRouteInstalled = true;
            SetState(XrayTunState.Running, error: null);
            WriteDiag("state -> Running");
        }
        catch (Exception ex)
        {
            WriteDiag($"adapter setup FAILED: {ex.Message}");
            SetError($"Failed to configure WinTUN routing: {ex.Message}");
            await StopAsync();
        }
    }

    private void WriteDiag(string line)
    {
        try
        {
            _xrayLogWriter?.WriteLine($"[diag {DateTime.Now:HH:mm:ss.fff}] {line}");
        }
        catch {  }
    }

    private async Task<(bool ok, int ifIndex)> WaitForTunDeviceAsync(Process proc, CancellationToken ct)
    {

        var markerDeadline = DateTime.UtcNow + CreatingAdapterMarkerTimeout;
        while (DateTime.UtcNow < markerDeadline && !ct.IsCancellationRequested)
        {
            if (proc.HasExited) return (false, 0);
            if (_xrayCreatingAdapterSeen)
            {
                WriteDiag("xray began 'Creating adapter'; starting LUID poll");
                break;
            }
            try { await Task.Delay(100, ct); } catch { return (false, 0); }
        }
        if (!_xrayCreatingAdapterSeen)
        {
            WriteDiag($"'Creating adapter' marker not seen within {CreatingAdapterMarkerTimeout.TotalSeconds:F0}s; "
                    + "polling LUID anyway (exclude filter still rejects pre-launch zombies)");
        }

        var pollDeadline = DateTime.UtcNow + TunStartupTimeout;
        var lastProgress = DateTime.UtcNow;
        ulong lastLuid = 0;
        var polls = 0;
        int lastAliasResult = -1;

        while (DateTime.UtcNow < pollDeadline && !ct.IsCancellationRequested)
        {
            if (proc.HasExited) return (false, 0);
            polls++;

            ulong currentLuid = 0;
            int aliasResult = -1;
            try
            {
                aliasResult = NativeMethods.ConvertInterfaceAliasToLuid(TunInterfaceName, out var luid);
                if (aliasResult == 0) currentLuid = luid;
            }
            catch (Exception ex)
            {
                WriteDiag($"ConvertInterfaceAliasToLuid threw: {ex.Message}");
            }
            lastAliasResult = aliasResult;

            if (currentLuid == 0)
            {
                try
                {
                    foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
                    {
                        if (!n.Name.Equals(TunInterfaceName, StringComparison.OrdinalIgnoreCase)) continue;

                        var props = n.GetIPProperties();
                        var idxFromProps = props?.GetIPv4Properties()?.Index
                                        ?? props?.GetIPv6Properties()?.Index
                                        ?? 0;
                        if (idxFromProps > 0)
                        {
                            WriteDiag($"NetworkInterface fallback found '{n.Name}' ifIndex={idxFromProps}, "
                                    + $"Type={n.NetworkInterfaceType}, Status={n.OperationalStatus}");
                            return (true, idxFromProps);
                        }
                    }
                }
                catch {  }
            }

            if (currentLuid != 0
                && currentLuid != _excludeAdapterLuid
                && currentLuid == lastLuid)
            {
                try
                {
                    if (NativeMethods.ConvertInterfaceLuidToIndex(ref currentLuid, out var idx) == 0
                        && idx > 0)
                    {
                        WriteDiag($"WinTUN '{TunInterfaceName}' settled at LUID={currentLuid:X}, "
                                + $"ifIndex={idx} (polls={polls})");
                        return (true, (int)idx);
                    }
                }
                catch (Exception ex)
                {
                    WriteDiag($"ConvertInterfaceLuidToIndex threw: {ex.Message}");
                }
            }

            if ((DateTime.UtcNow - lastProgress).TotalSeconds >= 2)
            {
                WriteDiag($"still polling: poll#{polls}, aliasResult={aliasResult}, "
                        + $"currentLuid={currentLuid:X}, lastLuid={lastLuid:X}, "
                        + $"exclude={_excludeAdapterLuid:X}, markerSeen={_xrayCreatingAdapterSeen}");
                lastProgress = DateTime.UtcNow;
            }

            lastLuid = currentLuid;
            try { await Task.Delay(250, ct); } catch { return (false, 0); }
        }

        WriteDiag($"WaitForTunDeviceAsync gave up after {polls} polls in "
                + $"{TunStartupTimeout.TotalSeconds:F0}s. Last aliasResult={lastAliasResult}, "
                + $"lastLuid={lastLuid:X}, exclude={_excludeAdapterLuid:X}, "
                + $"markerSeen={_xrayCreatingAdapterSeen}");
        return (false, 0);
    }

    public async Task StopAsync()
    {
        Process? proc;
        CancellationTokenSource? cts;
        StreamWriter? writer;

        var caller = new System.Diagnostics.StackTrace(skipFrames: 1, fNeedFileInfo: false)
            .GetFrames()
            ?.Take(6)
            .Select(f => f.GetMethod()?.DeclaringType?.Name + "." + f.GetMethod()?.Name)
            .Where(s => !string.IsNullOrEmpty(s))
            .ToArray() ?? Array.Empty<string>();
        WriteDiag($"StopAsync from: {string.Join(" <- ", caller)}");

        lock (_lock)
        {
            proc = _process;
            cts = _cts;
            writer = _xrayLogWriter;
            _process = null;
            _cts = null;
            _xrayLogWriter = null;
        }

        var preservedError = State == XrayTunState.Error ? LastError : null;

        SetState(XrayTunState.Stopping, error: null);

        try { cts?.Cancel(); } catch {  }

        if (_defaultRouteInstalled)
        {
            try { RemoveDefaultRoute(); }
            catch (Exception ex) { _logger.LogWarning(ex, "RemoveDefaultRoute failed"); }
            _defaultRouteInstalled = false;
        }

        if (proc is not null)
        {
            try
            {
                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    try { await proc.WaitForExitAsync(timeout.Token); } catch {  }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "xray kill failed");
            }
            finally
            {
                proc.Dispose();
            }
        }

        try
        {
            if (!string.IsNullOrEmpty(_workDir) && Directory.Exists(_workDir))
                Directory.Delete(_workDir, recursive: true);
        }
        catch {  }

        try { writer?.Dispose(); } catch {  }
        cts?.Dispose();
        if (preservedError is not null)
        {

            SetState(XrayTunState.Error, preservedError);
        }
        else
        {
            SetState(XrayTunState.Off, error: null);
        }
    }

    private void OnXrayOutput(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Data)) return;
        _logger.LogInformation("[xray] {Line}", e.Data);
        NoteXrayMilestone(e.Data);
        RememberOutputLine(e.Data, isErr: false);
    }

    private void OnXrayError(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Data)) return;
        _logger.LogWarning("[xray] {Line}", e.Data);
        NoteXrayMilestone(e.Data);
        RememberOutputLine(e.Data, isErr: true);
    }

    private void NoteXrayMilestone(string line)
    {
        if (!_xrayCreatingAdapterSeen
            && line.Contains("Creating adapter", StringComparison.OrdinalIgnoreCase))
        {
            _xrayCreatingAdapterSeen = true;
        }
    }

    private void RememberOutputLine(string line, bool isErr)
    {
        _recentOutput.Enqueue(line);
        while (_recentOutput.Count > RecentOutputMax && _recentOutput.TryDequeue(out _)) { }
        try
        {
            _xrayLogWriter?.WriteLine(isErr ? "[err] " + line : line);
        }
        catch {  }
    }

    private string DescribeRecentOutput()
    {
        var lines = _recentOutput.ToArray();
        return lines.Length == 0 ? "(no output)" : string.Join(" | ", lines);
    }

    private void OnXrayExited(object? sender, EventArgs e)
    {
        var code = (sender as Process)?.ExitCode ?? -1;
        _logger.LogInformation("xray.exe exited (code={Code})", code);
        if (State is XrayTunState.Running or XrayTunState.Starting)
        {
            SetError($"xray.exe exited unexpectedly (code={code}). "
                   + $"Last output: {DescribeRecentOutput()}. "
                   + $"Full log: {_xrayLogPath}");
            _ = Task.Run(async () =>
            {
                try { await StopAsync(); }
                catch (Exception ex) { _logger.LogWarning(ex, "Cleanup after unexpected xray exit failed"); }
            });
        }
    }

    private static bool IsAdministrator()
    {
        try
        {
            using var ident = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(ident).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private void ConfigureTunAdapter(int ifIndex)
    {
        AssignTunIpv4WithRetry(ifIndex);

        RunNetsh($"interface ipv4 set subinterface \"{TunInterfaceName}\" mtu={TunMtu} store=active",
                 acceptFailure: true);

        var first = true;
        foreach (var dns in TunDnsServers)
        {
            RunNetsh(first
                ? $"interface ipv4 set dnsservers name=\"{TunInterfaceName}\" static {dns} primary validate=no"
                : $"interface ipv4 add dnsservers name=\"{TunInterfaceName}\" {dns} validate=no",
                acceptFailure: true);
            first = false;
        }

        RunNetsh($"interface ipv4 set interface \"{TunInterfaceName}\" metric=1 store=active",
                 acceptFailure: true);
        _ = ifIndex;
    }

    private void AssignTunIpv4WithRetry(int ifIndex)
    {
        var deadline = DateTime.UtcNow + TunStartupTimeout;
        var attempt = 0;
        var bindingForced = false;
        var psFallbackTried = false;
        Exception? lastError = null;

        while (DateTime.UtcNow < deadline)
        {
            attempt++;
            try
            {
                RunNetsh($"interface ipv4 set address name=\"{TunInterfaceName}\" "
                        + $"static {TunLocalIp} {TunSubnetMask} {TunGateway}");
                WriteDiag($"set address ok on attempt {attempt}");
                return;
            }
            catch (InvalidOperationException ex)
                when (ex.Message.Contains("Element not found", StringComparison.OrdinalIgnoreCase))
            {
                lastError = ex;
                if (attempt == 1)
                {
                    WriteDiag("netsh: NOT_FOUND (IPv4 not yet bound to WinTUN); retrying...");
                }

                if (!bindingForced && attempt >= 3)
                {
                    bindingForced = true;
                    WriteDiag("netsh still NOT_FOUND after 3 attempts; forcing ms_tcpip via Enable-NetAdapterBinding");
                    ForceTcpipBinding();

                    Thread.Sleep(1000);
                    continue;
                }

                if (!psFallbackTried && attempt >= 6 && ifIndex > 0)
                {
                    psFallbackTried = true;
                    WriteDiag($"netsh still NOT_FOUND after {attempt} attempts; trying New-NetIPAddress -InterfaceIndex {ifIndex}");
                    if (TryAssignViaNewNetIPAddress(ifIndex))
                    {
                        WriteDiag("New-NetIPAddress fallback succeeded");
                        return;
                    }
                    WriteDiag("New-NetIPAddress fallback failed; continuing netsh retries");
                }

                Thread.Sleep(500);
            }
        }

        throw new InvalidOperationException(
            $"Couldn't assign IPv4 to WinTUN '{TunInterfaceName}' after {attempt} attempts "
          + $"({TunStartupTimeout.TotalSeconds:F0}s deadline). Last error: {lastError?.Message ?? "(unknown)"}. "
          + "This usually means the IPv4 protocol isn't bound to the WinTUN adapter. "
          + "Try opening 'View Network Connections', right-click 'xray_tun' → Properties, "
          + "and make sure 'Internet Protocol Version 4 (TCP/IPv4)' is checked.");
    }

    private bool TryAssignViaNewNetIPAddress(int ifIndex)
    {

        var ps =
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \""
          + $"Try {{ Remove-NetIPAddress -InterfaceIndex {ifIndex} -IPAddress '{TunLocalIp}' -Confirm:$false -ErrorAction SilentlyContinue }} Catch {{}};"
          + $"Try {{ New-NetIPAddress -InterfaceIndex {ifIndex} -IPAddress '{TunLocalIp}' "
          +  $"-PrefixLength 30 -DefaultGateway '{TunGateway}' -AddressFamily IPv4 -ErrorAction Stop | Out-Null;"
          +  $" Write-Host 'ok' }}"
          + $"Catch {{ Write-Host \\\"new-netip-fail: $_\\\"; exit 1 }}\"";
        try
        {
            RunTool("powershell.exe", ps, acceptFailure: true, timeoutMs: 15000);

            foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (!n.Name.Equals(TunInterfaceName, StringComparison.OrdinalIgnoreCase)) continue;
                var v4 = n.GetIPProperties()?.UnicastAddresses;
                if (v4 is null) continue;
                foreach (var a in v4)
                {
                    if (a.Address.AddressFamily == AddressFamily.InterNetwork
                        && a.Address.ToString() == TunLocalIp)
                    {
                        return true;
                    }
                }
            }
            return false;
        }
        catch (Exception ex)
        {
            WriteDiag($"New-NetIPAddress spawn failed: {ex.Message}");
            return false;
        }
    }

    private void ForceTcpipBinding()
    {

        var ps =
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "
          + $"\"Try {{ Enable-NetAdapterBinding -Name '{TunInterfaceName}' "
          +  "-ComponentID ms_tcpip -ErrorAction Stop } "
          +  "Catch { Write-Host \\\"force-bind warn: $_\\\" }\"";
        try
        {
            RunTool("powershell.exe", ps, acceptFailure: true, timeoutMs: 15000);
        }
        catch (Exception ex)
        {

            WriteDiag($"force-bind spawn failed (continuing): {ex.Message}");
        }
    }

    private void InstallDefaultRoute(int ifIndex)
    {

        RunRoute($"ADD 0.0.0.0 MASK 0.0.0.0 {TunGateway} METRIC 1 IF {ifIndex}");
    }

    private void RemoveDefaultRoute() => RunRoute($"DELETE 0.0.0.0 MASK 0.0.0.0 {TunGateway}");

    private void RunRoute(string args) => RunTool("route.exe", args, acceptFailure: false);

    private void RunNetsh(string args, bool acceptFailure = false)
        => RunTool("netsh.exe", args, acceptFailure);

    private void RunTool(string fileName, string args, bool acceptFailure, int timeoutMs = 5000)
    {
        var toolPath = Path.IsPathRooted(fileName)
            ? fileName
            : RuntimePathSecurity.ResolveSystem32Tool(fileName);

        var psi = new ProcessStartInfo
        {
            FileName = toolPath,
            Arguments = args,
            WorkingDirectory = Path.GetDirectoryName(toolPath) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException($"Couldn't launch {toolPath}");
        if (!p.WaitForExit(timeoutMs))
        {
            throw new InvalidOperationException($"{toolPath} timed out");
        }
        if (p.ExitCode != 0)
        {
            var stderr = p.StandardError.ReadToEnd();
            var stdout = p.StandardOutput.ReadToEnd();
            var msg = $"{toolPath} {args} failed (exit={p.ExitCode}): {stderr}{stdout}";
            try { _xrayLogWriter?.WriteLine("[net] " + msg); } catch {  }
            if (!acceptFailure)
            {
                throw new InvalidOperationException(msg);
            }
            _logger.LogDebug("{Tool} returned non-zero (continuing): {Message}", fileName, msg);
        }
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

    internal static string BuildConfigJson(int psiphonSocksPort, UserSettings settings)
    {

        var rules = new JsonArray
        {

            new JsonObject
            {
                ["type"] = "field",
                ["network"] = "udp",
                ["port"] = "135,137-139,5353",
                ["outboundTag"] = "block",
            },
            new JsonObject
            {
                ["type"] = "field",
                ["ip"] = new JsonArray("224.0.0.0/3", "ff00::/8"),
                ["outboundTag"] = "block",
            },

            new JsonObject
            {
                ["type"] = "field",
                ["inboundTag"] = new JsonArray("tun-in"),
                ["port"] = "53",
                ["outboundTag"] = "dns-out",
            },

            new JsonObject
            {
                ["type"] = "field",
                ["ip"] = new JsonArray("geoip:private"),
                ["outboundTag"] = "direct",
            },
            new JsonObject
            {
                ["type"] = "field",
                ["ip"] = new JsonArray("127.0.0.0/8"),
                ["outboundTag"] = "direct",
            },
        };

        _ = settings;

        var cfg = new JsonObject
        {
            ["log"] = new JsonObject { ["loglevel"] = "warning" },

            ["dns"] = new JsonObject
            {
                ["servers"] = new JsonArray("1.1.1.1", "8.8.8.8"),
                ["queryStrategy"] = "UseIPv4",
            },

            ["inbounds"] = new JsonArray(
                new JsonObject
                {
                    ["tag"] = "tun-in",
                    ["protocol"] = "tun",

                    ["settings"] = new JsonObject
                    {
                        ["name"] = TunInterfaceName,
                        ["MTU"] = TunMtu,
                        ["userLevel"] = 0,
                    },
                    ["sniffing"] = new JsonObject
                    {
                        ["enabled"] = true,
                        ["destOverride"] = new JsonArray("http", "tls"),
                        ["routeOnly"] = false,
                    },
                }),

            ["outbounds"] = new JsonArray(
                new JsonObject
                {
                    ["tag"] = "psiphon",
                    ["protocol"] = "socks",
                    ["settings"] = new JsonObject
                    {
                        ["servers"] = new JsonArray(
                            new JsonObject
                            {
                                ["address"] = "127.0.0.1",
                                ["port"] = psiphonSocksPort,
                            }),
                    },
                },
                new JsonObject { ["tag"] = "direct", ["protocol"] = "freedom" },
                new JsonObject { ["tag"] = "block", ["protocol"] = "blackhole" },
                new JsonObject
                {
                    ["tag"] = "dns-out",
                    ["protocol"] = "dns",
                    ["settings"] = new JsonObject { ["userLevel"] = 0 },
                }),

            ["routing"] = new JsonObject
            {
                ["domainStrategy"] = "IPIfNonMatch",
                ["rules"] = rules,

            },
        };
        return cfg.ToJsonString(WriteIndentedJson);
    }

    private async Task RemoveStaleWintunDeviceAsync()
    {
        var guid = ComputeWintunDeviceGuid(TunInterfaceName);
        var instanceId = $"SWD\\Wintun\\{{{guid:D}}}";
        var pnpUtil = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "pnputil.exe");
        if (!File.Exists(pnpUtil))
        {
            WriteDiag($"pnputil not found at {pnpUtil}; skipping pre-cleanup");
            return;
        }
        WriteDiag($"pnputil pre-cleanup: removing instance '{instanceId}' if present");

        var psi = new ProcessStartInfo
        {
            FileName = pnpUtil,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("/remove-device");
        psi.ArgumentList.Add(instanceId);

        try
        {
            using var p = Process.Start(psi)
                ?? throw new InvalidOperationException("Process.Start returned null");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { await p.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                WriteDiag("pnputil timed out; killing");
                try { p.Kill(entireProcessTree: true); } catch {  }
            }

            var stdout = await p.StandardOutput.ReadToEndAsync();
            var stderr = await p.StandardError.ReadToEndAsync();
            WriteDiag($"pnputil exit={p.ExitCode} stdout={stdout.Trim()} stderr={stderr.Trim()}");
        }
        catch (Exception ex)
        {
            WriteDiag($"pnputil spawn failed (continuing): {ex.Message}");
        }
    }

    internal static Guid ComputeWintunDeviceGuid(string adapterName)
    {
        var md5 = MD5.HashData(Encoding.UTF8.GetBytes(adapterName));
        return new Guid(md5);
    }

    private static class NativeMethods
    {

        [DllImport("iphlpapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern int ConvertInterfaceAliasToLuid(
            [MarshalAs(UnmanagedType.LPWStr)] string interfaceAlias,
            out ulong interfaceLuid);

        [DllImport("iphlpapi.dll", ExactSpelling = true)]
        public static extern int ConvertInterfaceLuidToIndex(
            ref ulong interfaceLuid,
            out uint interfaceIndex);
    }

    private void SetState(XrayTunState s, string? error)
    {
        if (State == s && LastError == error) return;
        State = s;
        LastError = error;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetError(string message)
    {
        _logger.LogWarning("XrayTunManager: {Message}", message);
        SetState(XrayTunState.Error, message);
    }

    public async ValueTask DisposeAsync()
    {
        _tunnel.StateChanged -= OnTunnelStateChanged;
        _settings.SettingsChanged -= OnSettingsChanged;
        await StopAsync();
        _reconcileGate.Dispose();
    }
}
