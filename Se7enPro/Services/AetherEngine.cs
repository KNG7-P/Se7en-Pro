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

        _transportLabel = method == ConnectionMethod.Masque
            ? (transport == "h2" ? "HTTP/2 (TCP)" : "HTTP/3 (QUIC)")
            : method.ToDisplayName();
        _obfuscationLabel = "";
        PublishRoute();

        Log($"Launching Aether ({method.ToDisplayName()}, scan={scan}"
          + (method == ConnectionMethod.Masque ? $", transport={_transportLabel}" : "")
          + (noize.Length > 0 ? $", noize={noize}" : "")
          + $", socks 127.0.0.1:{socksPort}, http 127.0.0.1:{httpPort}).");

        return new PreparedLaunch(exePath, args, workDir,
            StdinPrimer: "1\n",
            HttpProxyPort: httpPort,
            SocksPortOverride: actualSocks);
    }

    private string _transportLabel = "";
    private string _obfuscationLabel = "";

    protected override void OnCoreLine(string line)
    {
        var edge = Extract(line, "using cloudflare edge ");
        if (edge is not null)
        {
            CurrentRouteIp = edge;
            PublishRoute();
            SetConnectProgress(70, $"Found edge {edge}, validating...");
            return;
        }

        var transport = Extract(line, "MASQUE transport: ");
        if (transport is not null)
        {

            var to = transport.IndexOf(" to ", StringComparison.Ordinal);
            _transportLabel = to > 0 ? transport.Substring(0, to) : transport;
            PublishRoute();
            SetConnectProgress(80, $"Transport: {_transportLabel}");
            return;
        }

        var obfuscation = Extract(line, "obfuscation profile: ")
                       ?? Extract(line, "aethernoize primary profile: ");
        if (obfuscation is not null)
        {
            _obfuscationLabel = obfuscation;
            PublishRoute();
        }

        if (line.Contains("scanning", StringComparison.OrdinalIgnoreCase)
            || line.Contains("candidate", StringComparison.OrdinalIgnoreCase)
            || line.Contains("wireguard scan", StringComparison.OrdinalIgnoreCase))
        {
            SetConnectProgress(25, "Scanning Cloudflare edge candidates...");
        }
        else if (line.Contains("testing gateway", StringComparison.OrdinalIgnoreCase)
                 || line.Contains("connecting to gateway", StringComparison.OrdinalIgnoreCase)
                 || line.Contains("peer_validated", StringComparison.OrdinalIgnoreCase))
        {
            SetConnectProgress(55, "Testing edge gateway connectivity...");
        }
        else if (line.Contains("validat", StringComparison.OrdinalIgnoreCase)
                 || line.Contains("data-plane", StringComparison.OrdinalIgnoreCase)
                 || line.Contains("probe", StringComparison.OrdinalIgnoreCase))
        {
            SetConnectProgress(85, "Validating data-plane connection...");
        }
        else if (line.Contains("socks5 server listening", StringComparison.OrdinalIgnoreCase)
                 || line.Contains("tunnel validated", StringComparison.OrdinalIgnoreCase)
                 || line.Contains("handshake_confirmed", StringComparison.OrdinalIgnoreCase))
        {
            SetConnectProgress(95, "Finalizing tunnel...");
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
