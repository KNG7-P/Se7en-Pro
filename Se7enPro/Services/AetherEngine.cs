using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Se7enPro.Models;

namespace Se7enPro.Services;

public sealed class AetherEngine : LocalSocksEngineBase
{
    public AetherEngine(
        ILogger<AetherEngine> logger,
        ISettingsService settings,
        IChildProcessGuard childGuard)
        : base(logger, settings, childGuard)
    {
    }

    private static readonly object _overrideLock = new();
    private static ConnectionMethod? _methodOverride;
    private static int? _socksPortOverride;

    internal static void SetOverrides(int port, ConnectionMethod method)
    {
        lock (_overrideLock) { _socksPortOverride = port; _methodOverride = method; }
    }
    internal static void ClearOverrides()
    {
        lock (_overrideLock) { _socksPortOverride = null; _methodOverride = null; }
    }

    public override ConnectionMethod Method
    {
        get
        {
            lock (_overrideLock)
            {
                if (_methodOverride.HasValue) return _methodOverride.Value;
            }
            var m = ConnectionMethodExtensions.ParseConnectionMethod(_settings.Settings.ConnectionMethod);
            return m.IsAether() ? m : ConnectionMethod.Masque;
        }
    }

    public override IReadOnlyList<string> CoreProcessNames { get; } =
        new[] { EngineProcessNames.Aether };

    protected override string EngineDisplayName => Method.ToDisplayName();

    protected override string WorkSubdirectory => "aether";

    protected override TimeSpan ReadyTimeout =>
        NormalizeScan(_settings.Settings.AetherScanMode) switch
        {
            "turbo" => TimeSpan.FromSeconds(75),
            "thorough" => TimeSpan.FromSeconds(380),
            "stealth" => TimeSpan.FromSeconds(255),
            "ironclad" => TimeSpan.FromSeconds(245),
            _ => TimeSpan.FromSeconds(190),
        };

    internal static int? TryGetSocksPortOverride()
    {
        lock (_overrideLock) return _socksPortOverride;
    }

    protected override PreparedLaunch Prepare(string workDir, int socksPort, int httpPort)
    {
        _currentWorkDir = workDir;
        var bundled = ResolveBundledResource("aether", "aether.exe");
        var exePath = StageFile(bundled, Path.Combine(workDir, EngineProcessNames.Aether));

        try
        {
            var configFiles = new[]
            {
                "aether-masque-secondary.toml",
                "aether-masque.toml",
                "aether-secondary.toml",
                "aether.toml"
            };

            foreach (var cfgName in configFiles)
            {
                var src = ResolveBundledResource("aether", cfgName);
                if (File.Exists(src))
                {
                    var dst = Path.Combine(workDir, cfgName);

                    if (cfgName.Contains("secondary") || !File.Exists(dst) || new FileInfo(dst).Length < 100)
                    {
                        FileCacheHelper.StageFileSafe(src, dst, _logger);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to stage pre-registered Aether identity files.");
        }

        var s = _settings.Settings;
        var method = Method;
        var actualSocks = TryGetSocksPortOverride() ?? socksPort;

        var args = new List<string>
        {
            "--bind", $"127.0.0.1:{actualSocks}",

            "--http-proxy", $"127.0.0.1:{httpPort}",
            "--log-level", "info",
            "--reconnect-secs", "1",
        };

        var perf = _settings.Settings.AetherPerfProfile?.Trim().ToLowerInvariant();
        if (perf is "low" or "medium" or "high")
        {
            args.Add("--perf");
            args.Add(perf);
        }

        switch (method)
        {
            case ConnectionMethod.WireGuard:
                args.Add("--warp");
                break;
            case ConnectionMethod.WarpOnWarp:
                args.Add("--gool");
                break;
            case ConnectionMethod.MasqueOnMasque:
                args.Add("--mim");
                break;
            default:
                args.Add("--masque");
                break;
        }

        var defaultTransport = method == ConnectionMethod.MasqueOnMasque ? "h2" : s.AetherMasqueTransport;
        var rawTransport = _masqueTransportFallback ?? defaultTransport;
        var transport = NormalizeTransport(rawTransport);
        _currentMasqueTransport = transport;
        var needsMasqueTransport = method is ConnectionMethod.Masque or ConnectionMethod.MasqueOnMasque;

        var manualPeer = method switch
        {
            ConnectionMethod.WireGuard => (!string.IsNullOrWhiteSpace(s.AetherEndpointWireguard) ? s.AetherEndpointWireguard : s.AetherManualPeer ?? "").Trim(),
            ConnectionMethod.WarpOnWarp => (!string.IsNullOrWhiteSpace(s.AetherEndpointWarp) ? s.AetherEndpointWarp : s.AetherManualPeer ?? "").Trim(),
            ConnectionMethod.MasqueOnMasque => (!string.IsNullOrWhiteSpace(s.AetherEndpointMasqueOnMasque) ? s.AetherEndpointMasqueOnMasque : s.AetherManualPeer ?? "").Trim(),
            _ => (!string.IsNullOrWhiteSpace(s.AetherEndpointMasque) ? s.AetherEndpointMasque : s.AetherManualPeer ?? "").Trim(),
        };

        try
        {
            var wgFile = Path.Combine(workDir, "aether-lastconn.toml");
            var masqueFile = Path.Combine(workDir, "aether-masque-lastconn.toml");

            if ((method is ConnectionMethod.Masque or ConnectionMethod.MasqueOnMasque) && !File.Exists(masqueFile) && File.Exists(wgFile))
            {
                var ip = ExtractPeerIp(wgFile);
                if (!string.IsNullOrWhiteSpace(ip))
                {
                    File.WriteAllText(masqueFile, $"peer = \"{ip}:443\"\nprofile = \"firewall\"\n");
                }
            }
            else if (method == ConnectionMethod.WireGuard && !File.Exists(wgFile) && File.Exists(masqueFile))
            {
                var ip = ExtractPeerIp(masqueFile);
                if (!string.IsNullOrWhiteSpace(ip))
                {
                    File.WriteAllText(wgFile, $"peer = \"{ip}:2408\"\nprofile = \"balanced\"\n");
                }
            }
        }
        catch { }

        var scan = NormalizeScan(s.AetherScanMode);
        if (manualPeer.Length > 0)
        {
            if (method == ConnectionMethod.MasqueOnMasque)
            {
                if (manualPeer.Contains(','))
                {
                    args.Add("--mim-peers");
                    args.Add(manualPeer);
                }
                else
                {
                    args.Add("--mim-outer");
                    args.Add(manualPeer);
                }
            }
            else if (method == ConnectionMethod.WireGuard)
            {
                args.Add("--wg-peer");
                args.Add(manualPeer);
            }
            else if (method == ConnectionMethod.WarpOnWarp)
            {
                if (manualPeer.Contains(','))
                {
                    args.Add("--wiw-peers");
                    args.Add(manualPeer);
                }
                else
                {
                    args.Add("--wiw-outer");
                    args.Add(manualPeer);
                }
            }
            else
            {
                args.Add("--peer");
                args.Add(manualPeer);
            }
        }
        else
        {

            if (method == ConnectionMethod.MasqueOnMasque)
            {
                var cachedEdge = GetBestVerifiedCachedEdge(workDir, method, transport, _logger);
                if (!string.IsNullOrWhiteSpace(cachedEdge))
                {
                    args.Add("--mim-outer");
                    args.Add(cachedEdge);
                }
            }
            else if (method == ConnectionMethod.WarpOnWarp)
            {
                var cachedEdge = GetBestVerifiedCachedEdge(workDir, method, "udp", _logger);
                if (!string.IsNullOrWhiteSpace(cachedEdge))
                {
                    args.Add("--wiw-outer");
                    args.Add(cachedEdge);
                }
            }

            args.Add("--scan");
            args.Add(scan);
        }

        var noize = (s.AetherNoize ?? "").Trim();
        if (noize.Length > 0 && !noize.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            args.Add("--noize");
            args.Add(noize);
        }

        switch ((s.AetherIpVersion ?? "4").Trim().ToLowerInvariant())
        {
            case "6":
                args.Add("-6");
                break;
            case "dual":
            case "both":
                args.Add("--dual");
                break;
            default:
                args.Add("-4");
                break;
        }

        if (needsMasqueTransport)
        {

            if (transport == "h2")
            {
                args.Add("--h2");

                if (s.AetherFragment || method == ConnectionMethod.MasqueOnMasque)
                    args.Add("--fragment");
            }
            else
            {
                args.Add("--h3");
            }
        }

        if (needsMasqueTransport && !s.AetherQuicV2) args.Add("--no-quic-v2");
        var mark = (s.AetherMark ?? "").Trim();
        if (mark.Length > 0) { args.Add("--mark"); args.Add(mark); }

        try
        {
            var ptSrcCandidates = new[]
            {
                Path.Combine(AppDir, "Resources", "aether", "pt"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "aether", "pt"),
                Path.GetFullPath(Path.Combine(AppDir, @"..\..\..\..\..\Se7enPro\Resources\aether\pt")),
                Path.GetFullPath(Path.Combine(AppDir, @"..\..\..\..\Se7enPro\Resources\aether\pt")),
                Path.GetFullPath(Path.Combine(AppDir, @"..\..\..\Se7enPro\Resources\aether\pt")),
                Path.GetFullPath(Path.Combine(AppDir, @"..\Resources\aether\pt")),
            };
            var ptSrc = ptSrcCandidates.FirstOrDefault(Directory.Exists);
            if (!string.IsNullOrEmpty(ptSrc) && Directory.Exists(ptSrc))
            {
                var ptDst = Path.Combine(workDir, "pt");
                Directory.CreateDirectory(ptDst);
                foreach (var f in Directory.EnumerateFiles(ptSrc, "*", SearchOption.TopDirectoryOnly))
                {
                    FileCacheHelper.StageFileSafe(f, Path.Combine(ptDst, Path.GetFileName(f)), _logger);
                }
            }
        }
        catch { }

        var masqueCfg = Path.Combine(workDir, "aether-masque.toml");
        if (File.Exists(masqueCfg))
        {
            args.Add("--masque-config");
            args.Add(masqueCfg);
        }
        var warpCfg = Path.Combine(workDir, "aether.toml");
        if (File.Exists(warpCfg))
        {
            args.Add("--config");
            args.Add(warpCfg);
        }

        args.Add("--quick-reconnect");

        _transportLabel = needsMasqueTransport
            ? (transport == "h2" ? "HTTP/2 (TCP)" : "HTTP/3 (QUIC)")
            : method.ToDisplayName();
        _obfuscationLabel = "";
        PublishRoute();

        Log($"Launching Aether ({method.ToDisplayName()}, scan={scan}"
          + (needsMasqueTransport ? $", transport={_transportLabel}" : "")
          + (noize.Length > 0 ? $", noize={noize}" : "") + ").");

        _candidatesFound = 0;
        _lastCandidateProgressTick = 0;

        return new PreparedLaunch(exePath, args, workDir,
            StdinPrimer: "1\n",
            HttpProxyPort: httpPort,
            SocksPortOverride: TryGetSocksPortOverride(),
            EnvironmentVariables: new Dictionary<string, string>
            {
                ["AETHER_QUICK_RECONNECT"] = "1"
            });
    }

    private string? _currentWorkDir;
    private string? _masqueTransportFallback;
    private string _currentMasqueTransport = "";
    private string _transportLabel = "";
    private string _obfuscationLabel = "";
    private int _candidatesFound;
    private long _lastCandidateProgressTick;

    protected override void OnSessionStopped()
    {
        _masqueTransportFallback = null;
        _candidatesFound = 0;
        _lastCandidateProgressTick = 0;
        base.OnSessionStopped();
    }

    protected override void OnCoreLine(string line)
    {
        var edge = Extract(line, "using cloudflare edge ");
        if (edge is not null)
        {
            var cleanEdge = CleanEdgeIp(edge);
            if (!string.Equals(CurrentRouteIp, cleanEdge, StringComparison.Ordinal))
            {
                CurrentRouteIp = cleanEdge;
                PublishRoute();
            }
            SetConnectProgress(70, $"Found edge {cleanEdge}, validating...");
            if (!string.IsNullOrWhiteSpace(_currentWorkDir))
            {
                var colonIdx = edge.LastIndexOf(':');
                int port = Method is ConnectionMethod.WireGuard or ConnectionMethod.WarpOnWarp ? 2408 : 443;
                if (colonIdx > 0)
                {
                    var portStr = edge.Substring(colonIdx + 1).Split(' ')[0].Trim();
                    if (int.TryParse(portStr, out var parsedPort)) port = parsedPort;
                }
                RecordWorkingEdge(_currentWorkDir, cleanEdge, port, Method, _currentMasqueTransport);
            }
            return;
        }

        if (line.Contains("masque-in-masque ready:", StringComparison.OrdinalIgnoreCase))
        {
            var info = Extract(line, "masque-in-masque ready:");
            if (!string.IsNullOrWhiteSpace(info))
            {
                _transportLabel = "Double MASQUE · Foreign IP (" + info.Trim() + ")";
                PublishRoute();
                if (!string.IsNullOrWhiteSpace(_currentWorkDir))
                {
                    var outerPart = info.Split(new[] { "(outer)" }, StringSplitOptions.None)[0].Trim();
                    var colonIdx = outerPart.LastIndexOf(':');
                    int port = 443;
                    var ip = outerPart;
                    if (colonIdx > 0)
                    {
                        ip = outerPart.Substring(0, colonIdx).Trim();
                        int.TryParse(outerPart.Substring(colonIdx + 1).Trim(), out port);
                    }
                    RecordWorkingEdge(_currentWorkDir, ip, port, ConnectionMethod.MasqueOnMasque, "h2");
                }
            }
            else
            {
                _transportLabel = "Double MASQUE · Foreign IP";
                PublishRoute();
            }
            SetConnectProgress(100, "Connected (Foreign IP)");
            ConfirmTunnelReady("masque-in-masque ready");
            return;
        }

        var transport = Extract(line, "MASQUE transport: ");
        if (transport is not null)
        {
            var to = transport.IndexOf(" to ", StringComparison.Ordinal);
            var parsed = to > 0 ? transport.Substring(0, to) : transport;
            if (!string.Equals(_transportLabel, parsed, StringComparison.Ordinal))
            {
                _transportLabel = parsed;
                PublishRoute();
            }
            SetConnectProgress(80, $"Transport: {_transportLabel}");
            return;
        }

        var obfuscation = Extract(line, "obfuscation profile: ")
                       ?? Extract(line, "aethernoize primary profile: ");
        if (obfuscation is not null)
        {
            if (!string.Equals(_obfuscationLabel, obfuscation, StringComparison.Ordinal))
            {
                _obfuscationLabel = obfuscation;
                PublishRoute();
            }
        }

        if (line.Contains("verifying cached gateway", StringComparison.OrdinalIgnoreCase))
        {
            var gw = Extract(line, "verifying cached gateway ");
            var idx = gw?.IndexOf(" before", StringComparison.Ordinal) ?? -1;
            var ip = idx > 0 ? gw!.Substring(0, idx) : gw;
            SetConnectProgress(15, $"Verifying cached edge ({ip ?? "..."})...");
        }
        else if (line.Contains("cached gateway", StringComparison.OrdinalIgnoreCase) && line.Contains("still works", StringComparison.OrdinalIgnoreCase))
        {
            var gw = Extract(line, "cached gateway ");
            var idx = gw?.IndexOf(" still", StringComparison.Ordinal) ?? -1;
            var ip = idx > 0 ? gw!.Substring(0, idx) : gw;
            SetConnectProgress(65, $"Cached edge verified ({ip ?? "ready"})...");
        }
        else if (line.Contains("cached gateway", StringComparison.OrdinalIgnoreCase) && line.Contains("no longer works", StringComparison.OrdinalIgnoreCase))
        {
            _candidatesFound = 0;
            SetConnectProgress(20, "Cached edge expired; hunting fresh edge IPs...");
            if (!string.IsNullOrWhiteSpace(_currentWorkDir))
            {
                var gw = Extract(line, "cached gateway ");
                var idx = gw?.IndexOf(" no", StringComparison.Ordinal) ?? -1;
                var ip = idx > 0 ? gw!.Substring(0, idx) : gw;
                if (!string.IsNullOrWhiteSpace(ip)) MarkEdgeFailed(_currentWorkDir, ip);
            }
        }
        else if (line.Contains("hunting for a working", StringComparison.OrdinalIgnoreCase))
        {
            _candidatesFound = 0;
            SetConnectProgress(25, "Hunting for working Cloudflare edge...");
        }
        else if (line.Contains("prober", StringComparison.OrdinalIgnoreCase) && line.Contains("scan mode=", StringComparison.OrdinalIgnoreCase))
        {
            var candMatch = Extract(line, "candidates=");
            var cCount = candMatch?.Split(' ')[0] ?? "2000+";
            SetConnectProgress(28, $"Scanning {cCount} edge candidates in parallel...");
        }
        else if (line.Contains("candidate ok", StringComparison.OrdinalIgnoreCase))
        {
            _candidatesFound++;
            var nowTick = Environment.TickCount64;

            if (_candidatesFound == 1 || _candidatesFound % 5 == 0 || (nowTick - _lastCandidateProgressTick) >= 350)
            {
                _lastCandidateProgressTick = nowTick;
                var cand = Extract(line, "candidate ok ");
                var pct = Math.Min(28 + (_candidatesFound * 4), 68);
                SetConnectProgress(pct, $"Candidate OK ({_candidatesFound}): {cand ?? "found"}");
            }
        }
        else if (line.Contains("selected MASQUE gateway", StringComparison.OrdinalIgnoreCase) || line.Contains("best gateway", StringComparison.OrdinalIgnoreCase))
        {
            var best = Extract(line, "selected MASQUE gateway ") ?? Extract(line, "best gateway ");
            SetConnectProgress(70, $"Selected best edge: {best ?? "ready"}");
            if (!string.IsNullOrWhiteSpace(_currentWorkDir) && !string.IsNullOrWhiteSpace(best))
            {
                var clean = CleanEdgeIp(best);
                var colon = best.LastIndexOf(':');
                int port = Method is ConnectionMethod.WireGuard or ConnectionMethod.WarpOnWarp ? 2408 : 443;
                if (colon > 0)
                {
                    var pStr = best.Substring(colon + 1).Split(' ')[0].Trim();
                    if (int.TryParse(pStr, out var p)) port = p;
                }
                RecordWorkingEdge(_currentWorkDir, clean, port, Method, _currentMasqueTransport);
            }
        }
        else if (line.Contains("Aether has not registered", StringComparison.OrdinalIgnoreCase)
              || line.Contains("cannot register from inside", StringComparison.OrdinalIgnoreCase))
        {
            Log($"Aether identity missing: {line.Trim()} — connect once with Aether alone to register.");
        }
        else if (line.Contains("MASQUE tunnel ended", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("MASQUE tunnel closed", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("wireguard tunnel closed", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("wireguard tunnel exited", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("tunnel failed validation", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("tunnel exited before", StringComparison.OrdinalIgnoreCase))
        {
            SetConnectProgress(30, "Tunnel attempt timed out; retrying or rescanning...");
        }
        else if (line.Contains("retrying last known-good gateway", StringComparison.OrdinalIgnoreCase))
        {
            SetConnectProgress(35, "Retrying edge gateway before rescan...");
        }
        else if (line.Contains("last known-good gateway", StringComparison.OrdinalIgnoreCase) && line.Contains("no longer works", StringComparison.OrdinalIgnoreCase))
        {
            SetConnectProgress(20, "Edge gateway dead; rescanning fresh candidates...");
            if (!string.IsNullOrWhiteSpace(_currentWorkDir))
            {
                var gw = Extract(line, "last known-good gateway ");
                var idx = gw?.IndexOf(" no", StringComparison.Ordinal) ?? -1;
                var ip = idx > 0 ? gw!.Substring(0, idx) : gw;
                if (!string.IsNullOrWhiteSpace(ip)) MarkEdgeFailed(_currentWorkDir, ip);
            }
        }
        else if (line.Contains("[-] outer edge ", StringComparison.OrdinalIgnoreCase) && line.Contains("failed", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(_currentWorkDir))
            {
                var gw = Extract(line, "[-] outer edge ");
                var idx = gw?.IndexOf(" failed", StringComparison.Ordinal) ?? -1;
                var ip = idx > 0 ? gw!.Substring(0, idx) : gw;
                if (!string.IsNullOrWhiteSpace(ip)) MarkEdgeFailed(_currentWorkDir, ip);
            }
        }
        else if (line.Contains("no usable MASQUE gateway found", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("no usable WireGuard gateway found", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("no inner masque edge answered", StringComparison.OrdinalIgnoreCase))
        {
            if ((Method == ConnectionMethod.Masque || Method == ConnectionMethod.MasqueOnMasque) && _currentMasqueTransport == "h3" && _masqueTransportFallback == null)
            {
                _masqueTransportFallback = "h2";
                Log("MASQUE failed over HTTP/3 (QUIC may be blocked or nested QUIC unsupported). Auto-falling back to HTTP/2 (TCP)...");
                SetConnectProgress(20, "Falling back to HTTP/2 (TCP)...");
                RestartCore("fallback from HTTP/3 to HTTP/2");
                return;
            }
            SetConnectProgress(15, "No working edge found; retrying scan...");
        }
        else if (line.Contains("[*] establishing outer MASQUE tunnel to", StringComparison.OrdinalIgnoreCase))
        {
            var target = Extract(line, "establishing outer MASQUE tunnel to ");
            if (target is not null)
            {
                var clean = CleanEdgeIp(target);
                if (!string.IsNullOrWhiteSpace(clean)) CurrentRouteIp = clean;
            }
            SetConnectProgress(75, "Connecting to outer edge gateway...");
        }
        else if (line.Contains("CERTIFICATE_VERIFY_FAILED", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("TLS handshake failed", StringComparison.OrdinalIgnoreCase))
        {
            SetConnectProgress(25, "TLS verification failed on edge; marking failed...");
            if (!string.IsNullOrWhiteSpace(_currentWorkDir) && !string.IsNullOrWhiteSpace(CurrentRouteIp))
            {
                MarkEdgeFailed(_currentWorkDir, CurrentRouteIp);
            }
        }
        else if (line.Contains("[h2] connecting tcp to", StringComparison.OrdinalIgnoreCase) || line.Contains("[h3] connecting", StringComparison.OrdinalIgnoreCase))
        {
            SetConnectProgress(75, "Connecting to edge gateway...");
        }
        else if (line.Contains("fragmenting client hello", StringComparison.OrdinalIgnoreCase))
        {
            SetConnectProgress(80, "Applying TLS ClientHello fragmentation...");
        }
        else if (line.Contains("tls established", StringComparison.OrdinalIgnoreCase))
        {
            SetConnectProgress(85, "TLS handshake established...");
        }
        else if (line.Contains("connect-ip request sent", StringComparison.OrdinalIgnoreCase))
        {
            SetConnectProgress(88, "MASQUE tunnel requested...");
        }
        else if (line.Contains("connect-ip status: 200", StringComparison.OrdinalIgnoreCase))
        {
            SetConnectProgress(90, "MASQUE connected (200), confirming data-plane...");
        }
        else if (line.Contains("trying inner MASQUE edge", StringComparison.OrdinalIgnoreCase))
        {
            SetConnectProgress(88, "Connecting inner MASQUE tunnel...");
        }
        else if (line.Contains("inner MASQUE tunnel established", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("inner endpoint", StringComparison.OrdinalIgnoreCase))
        {
            SetConnectProgress(95, "Inner tunnel established! Exposing proxy...");
        }
        else if (line.Contains("exposing socks5", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("socks5 server listening", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("handshake_confirmed", StringComparison.OrdinalIgnoreCase))
        {
            SetConnectProgress(100, "Connected");
            ConfirmTunnelReady("socks5 server listening");
        }
        else if (line.Contains("tunnel validated", StringComparison.OrdinalIgnoreCase) || line.Contains("data-plane", StringComparison.OrdinalIgnoreCase))
        {
            if (Method is ConnectionMethod.MasqueOnMasque or ConnectionMethod.WarpOnWarp)
            {

                if (line.Contains("[outer]", StringComparison.OrdinalIgnoreCase) || !line.Contains("exposing socks5", StringComparison.OrdinalIgnoreCase))
                {
                    SetConnectProgress(85, "Outer hop verified! Establishing inner tunnel...");
                }
                else
                {
                    SetConnectProgress(96, "Data-plane confirmed! Exposing proxy...");
                    ConfirmTunnelReady("core data-plane verified");
                }
            }
            else
            {
                SetConnectProgress(96, "Data-plane confirmed! Exposing proxy...");
                ConfirmTunnelReady("core data-plane verified");
            }
        }
    }

    private void PublishRoute()
    {
        CurrentRouteSni = _obfuscationLabel.Length > 0 && _transportLabel.Length > 0
            ? $"{_transportLabel} · noize {_obfuscationLabel}"
            : _transportLabel + _obfuscationLabel;
        RaiseRouteChanged();
    }

    private static string? Extract(string line, string marker)
    {
        var i = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        var value = line.Substring(i + marker.Length).Trim();
        return value.Length == 0 ? null : value;
    }

    private static string NormalizeScan(string? mode) =>
        (mode ?? "").Trim().ToLowerInvariant() switch
        {
            "turbo" => "turbo",
            "thorough" => "thorough",
            "stealth" => "stealth",
            "ironclad" => "ironclad",
            _ => "balanced",
        };

    public static string NormalizeTransport(string? transport) =>
        (transport ?? "").Trim().ToLowerInvariant() switch
        {
            "h2" or "http2" or "http/2" or "tcp" => "h2",
            _ => "h3",
        };

    public static string CleanEdgeIp(string edge)
    {
        var s = edge.Replace("(outer)", "").Replace("(inner)", "").Trim();
        if (s.StartsWith("[") && s.Contains("]:"))
        {
            return s.Substring(1, s.IndexOf("]:") - 1).Trim();
        }
        if (s.StartsWith("[") && s.EndsWith("]"))
        {
            return s.Substring(1, s.Length - 2).Trim();
        }
        var colonIdx = s.LastIndexOf(':');
        if (colonIdx > 0 && !s.Contains(']'))
        {
            return s.Substring(0, colonIdx).Trim();
        }
        return s;
    }

    private static string? ExtractPeerIp(string filePath)
    {
        try
        {
            foreach (var line in File.ReadAllLines(filePath))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("peer", StringComparison.OrdinalIgnoreCase))
                {
                    var eq = trimmed.IndexOf('=');
                    if (eq > 0)
                    {
                        var val = trimmed.Substring(eq + 1).Trim().Trim('"', '\'');
                        var colon = val.LastIndexOf(':');
                        return colon > 0 ? val.Substring(0, colon) : val;
                    }
                }
            }
        }
        catch { }
        return null;
    }

    private static int? ExtractPeerPort(string filePath)
    {
        try
        {
            foreach (var line in File.ReadAllLines(filePath))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("peer", StringComparison.OrdinalIgnoreCase))
                {
                    var eq = trimmed.IndexOf('=');
                    if (eq > 0)
                    {
                        var val = trimmed.Substring(eq + 1).Trim().Trim('"', '\'');
                        var colon = val.LastIndexOf(':');
                        if (colon > 0 && int.TryParse(val.Substring(colon + 1), out var p))
                            return p;
                    }
                }
            }
        }
        catch { }
        return null;
    }

    public class AetherEdgeCandidate
    {
        public string Ip { get; set; } = "";
        public int Port { get; set; } = 443;
        public string Method { get; set; } = "";
        public string Transport { get; set; } = "";
        public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
        public int SuccessCount { get; set; } = 1;
        public int FailCount { get; set; }
    }

    private static readonly object _poolLock = new();

    private static List<AetherEdgeCandidate> LoadEdgePool(string workDir)
    {
        try
        {
            var poolPath = Path.Combine(workDir, "aether-edge-pool.json");
            if (File.Exists(poolPath))
            {
                var json = File.ReadAllText(poolPath);
                var list = JsonSerializer.Deserialize<List<AetherEdgeCandidate>>(json);
                if (list != null) return list;
            }
        }
        catch { }
        return new List<AetherEdgeCandidate>();
    }

    private static void SaveEdgePool(string workDir, List<AetherEdgeCandidate> pool)
    {
        try
        {
            var poolPath = Path.Combine(workDir, "aether-edge-pool.json");
            var json = JsonSerializer.Serialize(pool, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(poolPath, json);
        }
        catch { }
    }

    public static void RecordWorkingEdge(string workDir, string ip, int port, ConnectionMethod method, string transport)
    {
        if (string.IsNullOrWhiteSpace(workDir) || string.IsNullOrWhiteSpace(ip)) return;
        var cleanIp = CleanEdgeIp(ip);
        if (string.IsNullOrWhiteSpace(cleanIp)) return;
        var effectivePort = port > 0 ? port : (method is ConnectionMethod.WireGuard or ConnectionMethod.WarpOnWarp ? 2408 : 443);

        lock (_poolLock)
        {
            var pool = LoadEdgePool(workDir);
            var existing = pool.FirstOrDefault(c => c.Ip.Equals(cleanIp, StringComparison.OrdinalIgnoreCase) && c.Port == effectivePort);
            if (existing != null)
            {
                existing.LastSeenUtc = DateTime.UtcNow;
                existing.SuccessCount++;
                existing.FailCount = 0;
                if (!string.IsNullOrWhiteSpace(transport)) existing.Transport = transport;
                if (!string.IsNullOrWhiteSpace(method.ToString())) existing.Method = method.ToString();
            }
            else
            {
                pool.Insert(0, new AetherEdgeCandidate
                {
                    Ip = cleanIp,
                    Port = effectivePort,
                    Method = method.ToString(),
                    Transport = transport,
                    LastSeenUtc = DateTime.UtcNow,
                    SuccessCount = 1,
                    FailCount = 0
                });
            }

            pool = pool.OrderByDescending(c => c.SuccessCount - (c.FailCount * 2))
                       .ThenByDescending(c => c.LastSeenUtc)
                       .Take(20)
                       .ToList();
            SaveEdgePool(workDir, pool);

            try
            {
                var masqueFile = Path.Combine(workDir, "aether-masque-lastconn.toml");
                var wgFile = Path.Combine(workDir, "aether-lastconn.toml");

                if (method == ConnectionMethod.Masque && transport != "h2")
                {
                    File.WriteAllText(masqueFile, $"peer = \"{cleanIp}:443\"\nprofile = \"firewall\"\n");
                    if (!File.Exists(wgFile) || string.IsNullOrWhiteSpace(ExtractPeerIp(wgFile)))
                    {
                        File.WriteAllText(wgFile, $"peer = \"{cleanIp}:2408\"\nprofile = \"balanced\"\n");
                    }
                }
                else if (method is ConnectionMethod.WireGuard or ConnectionMethod.WarpOnWarp)
                {
                    File.WriteAllText(wgFile, $"peer = \"{cleanIp}:{effectivePort}\"\nprofile = \"balanced\"\n");
                    if (!File.Exists(masqueFile) || string.IsNullOrWhiteSpace(ExtractPeerIp(masqueFile)))
                    {
                        File.WriteAllText(masqueFile, $"peer = \"{cleanIp}:443\"\nprofile = \"firewall\"\n");
                    }
                }
            }
            catch { }
        }
    }

    public static void MarkEdgeFailed(string workDir, string ip)
    {
        if (string.IsNullOrWhiteSpace(workDir) || string.IsNullOrWhiteSpace(ip)) return;
        var cleanIp = CleanEdgeIp(ip);
        lock (_poolLock)
        {
            var pool = LoadEdgePool(workDir);
            var item = pool.FirstOrDefault(c => c.Ip.Equals(cleanIp, StringComparison.OrdinalIgnoreCase));
            if (item != null)
            {
                item.FailCount++;
                SaveEdgePool(workDir, pool);
            }
        }
    }

    private static string? GetBestVerifiedCachedEdge(string workDir, ConnectionMethod method, string targetTransport, ILogger? logger = null)
    {
        List<AetherEdgeCandidate> candidates;
        lock (_poolLock)
        {
            candidates = LoadEdgePool(workDir);
        }

        var ordered = candidates.Where(c => c.FailCount < 2)
                               .OrderByDescending(c => c.SuccessCount - (c.FailCount * 2))
                               .ThenByDescending(c => c.LastSeenUtc)
                               .ToList();

        var masqueFile = Path.Combine(workDir, "aether-masque-lastconn.toml");
        var wgFile = Path.Combine(workDir, "aether-lastconn.toml");
        var masqueIp = ExtractPeerIp(masqueFile);
        var wgIp = ExtractPeerIp(wgFile);
        var wgPort = ExtractPeerPort(wgFile) ?? 2408;

        var probeList = new List<(string Ip, int Port)>();

        if (method is ConnectionMethod.MasqueOnMasque)
        {
            if (targetTransport == "h2")
            {

                foreach (var c in ordered.Where(c => string.Equals(c.Transport, "h2", StringComparison.OrdinalIgnoreCase)))
                {
                    probeList.Add((c.Ip, c.Port));
                }
            }
            else
            {
                foreach (var c in ordered.Where(c => string.Equals(c.Transport, "h3", StringComparison.OrdinalIgnoreCase)))
                {
                    probeList.Add((c.Ip, c.Port));
                }
                if (!string.IsNullOrWhiteSpace(masqueIp)) probeList.Add((masqueIp, 443));
            }
        }
        else if (method is ConnectionMethod.WarpOnWarp)
        {
            foreach (var c in ordered.Where(c => c.Method.Contains("Warp", StringComparison.OrdinalIgnoreCase) || c.Method.Contains("WireGuard", StringComparison.OrdinalIgnoreCase)))
            {
                probeList.Add((c.Ip, c.Port));
            }
            if (!string.IsNullOrWhiteSpace(wgIp)) probeList.Add((wgIp, wgPort));
            foreach (var c in ordered.Where(c => c.Port == 2408)) probeList.Add((c.Ip, 2408));
        }

        var distinctProbes = probeList
            .Where(p => !string.IsNullOrWhiteSpace(p.Ip))
            .DistinctBy(p => $"{p.Ip}:{p.Port}")
            .Take(3)
            .ToList();

        foreach (var (candIp, candPort) in distinctProbes)
        {
            if (method == ConnectionMethod.MasqueOnMasque && targetTransport == "h2")
            {
                try
                {
                    using var tcp = new TcpClient();
                    var ar = tcp.BeginConnect(candIp, candPort, null, null);
                    var success = ar.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(350));
                    if (success && tcp.Connected)
                    {
                        tcp.EndConnect(ar);
                        logger?.LogInformation("[Aether] Fast probe verified alive cached edge: {Ip}:{Port}", candIp, candPort);
                        return $"{candIp}:{candPort}";
                    }
                    else
                    {
                        MarkEdgeFailed(workDir, candIp);
                    }
                }
                catch
                {
                    MarkEdgeFailed(workDir, candIp);
                }
            }
            else
            {
                return $"{candIp}:{candPort}";
            }
        }

        return null;
    }
}
