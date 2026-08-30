using System;
using System.Collections.Generic;
using System.IO;
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

    internal static ConnectionMethod? MethodOverride;

    public override ConnectionMethod Method
    {
        get
        {
            if (MethodOverride.HasValue) return MethodOverride.Value;
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
            "turbo" => TimeSpan.FromSeconds(90),
            "thorough" => TimeSpan.FromSeconds(240),
            "stealth" => TimeSpan.FromSeconds(240),
            "ironclad" => TimeSpan.FromSeconds(320),
            _ => TimeSpan.FromSeconds(180),
        };

    internal static int? SocksPortOverride;

    protected override PreparedLaunch Prepare(string workDir, int socksPort, int httpPort)
    {
        var source = Path.Combine(AppDir, "Resources", "aether", "aether.exe");
        var exePath = StageFile(source, Path.Combine(workDir, EngineProcessNames.Aether));

        var s = _settings.Settings;
        var method = Method;
        var actualSocks = SocksPortOverride ?? socksPort;

        var args = new List<string>
        {
            "--bind", $"127.0.0.1:{actualSocks}",

            "--http-proxy", $"127.0.0.1:{httpPort}",
            "--log-level", "info",
        };

        switch (method)
        {
            case ConnectionMethod.WireGuard:
                args.Add("--warp");
                break;
            case ConnectionMethod.WarpOnWarp:
                args.Add("--gool");
                break;
            default:
                args.Add("--masque");
                break;
        }

        var peer = (s.AetherManualPeer ?? "").Trim();
        var scan = NormalizeScan(s.AetherScanMode);
        if (peer.Length > 0)
        {
            if (method == ConnectionMethod.WireGuard)
            {
                args.Add("--wg-peer");
                args.Add(peer);
            }
            else
            {
                args.Add("--peer");
                args.Add(peer);
            }
        }
        else
        {

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

        var transport = NormalizeTransport(s.AetherMasqueTransport);
        if (method == ConnectionMethod.Masque)
        {

            if (transport == "h2")
            {
                args.Add("--h2");

                if (s.AetherFragment) args.Add("--fragment");
            }

        }

        args.Add("--quick-reconnect");

        args.Add("--perf");
        args.Add("high");

        args.Add("--validate-secs");
        args.Add("4");

        args.Add("--dns");
        args.Add("1.1.1.1,1.0.0.1");

        if (method == ConnectionMethod.WireGuard)
        {
            args.Add("--keepalive");
            args.Add("5");
        }

        _transportLabel = method == ConnectionMethod.Masque
            ? (transport == "h2" ? "HTTP/2 (TCP)" : "HTTP/3 (QUIC)")
            : method.ToDisplayName();
        _obfuscationLabel = "";
        PublishRoute();

        Log($"Launching Aether ({method.ToDisplayName()}, scan={scan}"
          + (method == ConnectionMethod.Masque ? $", transport={_transportLabel}" : "")
          + (noize.Length > 0 ? $", noize={noize}" : "") + ").");

        _candidatesFound = 0;

        return new PreparedLaunch(exePath, args, workDir,
            StdinPrimer: "1\n",
            HttpProxyPort: httpPort,
            SocksPortOverride: actualSocks);
    }

    private string _transportLabel = "";
    private string _obfuscationLabel = "";
    private int _candidatesFound;

    protected override void OnCoreLine(string line)
    {
        var edge = Extract(line, "using cloudflare edge ");
        if (edge is not null)
        {
            if (!string.Equals(CurrentRouteIp, edge, StringComparison.Ordinal))
            {
                CurrentRouteIp = edge;
                PublishRoute();
            }
            SetConnectProgress(70, $"Found edge {edge}, validating...");
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
            SetConnectProgress(10, $"Verifying cached edge ({ip ?? "..."})...");
        }
        else if (line.Contains("cached gateway", StringComparison.OrdinalIgnoreCase) && line.Contains("no longer works", StringComparison.OrdinalIgnoreCase))
        {
            _candidatesFound = 0;
            SetConnectProgress(15, "Cached edge expired; hunting fresh edge IPs...");
        }
        else if (line.Contains("hunting for a working", StringComparison.OrdinalIgnoreCase))
        {
            _candidatesFound = 0;
            SetConnectProgress(20, "Hunting for working Cloudflare edge...");
        }
        else if (line.Contains("prober", StringComparison.OrdinalIgnoreCase) && line.Contains("scan mode=", StringComparison.OrdinalIgnoreCase))
        {
            var candMatch = Extract(line, "candidates=");
            var cCount = candMatch?.Split(' ')[0] ?? "2000+";
            SetConnectProgress(25, $"Scanning {cCount} edge candidates in parallel...");
        }
        else if (line.Contains("candidate ok", StringComparison.OrdinalIgnoreCase))
        {
            _candidatesFound++;
            var cand = Extract(line, "candidate ok ");
            var pct = Math.Min(25 + (_candidatesFound * 6), 65);
            SetConnectProgress(pct, $"Candidate OK ({_candidatesFound}): {cand ?? "found"}");
        }
        else if (line.Contains("selected MASQUE gateway", StringComparison.OrdinalIgnoreCase) || line.Contains("best gateway", StringComparison.OrdinalIgnoreCase))
        {
            var best = Extract(line, "selected MASQUE gateway ") ?? Extract(line, "best gateway ");
            SetConnectProgress(70, $"Selected best edge: {best ?? "ready"}");
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
        else if (line.Contains("tunnel validated", StringComparison.OrdinalIgnoreCase) || line.Contains("data-plane", StringComparison.OrdinalIgnoreCase))
        {
            SetConnectProgress(96, "Data-plane confirmed! Exposing proxy...");
        }
        else if (line.Contains("socks5 server listening", StringComparison.OrdinalIgnoreCase) || line.Contains("handshake_confirmed", StringComparison.OrdinalIgnoreCase))
        {
            SetConnectProgress(100, "Connected");
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
}
