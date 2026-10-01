using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Se7enPro.Models;

namespace Se7enPro.Services;

public sealed class AetherEngine : LocalSocksEngineBase
{
    private readonly IdentityProvisioner _identityProvisioner;

    public AetherEngine(
        ILogger<AetherEngine> logger,
        ISettingsService settings,
        IChildProcessGuard childGuard,
        IdentityProvisioner identityProvisioner)
        : base(logger, settings, childGuard)
    {
        _identityProvisioner = identityProvisioner;
    }

    protected override async Task BeforeStartAsync(string workDir, CancellationToken ct)
    {
        if (_identityProvisioner.HasValidIdentity(workDir, Method))
        {
            return;
        }

        SetConnectProgress(10, Loc.Of("Checking Cloudflare WARP identity..."));
        await _identityProvisioner.EnsureIdentityAsync(
            workDir,
            Method,
            p => SetConnectProgress(p.Percent, Loc.Of(p.Text)),
            ct);
    }

    
    
    
    
    
    private static readonly object OverrideLock = new();
    private static ConnectionMethod? _methodOverride;

    internal static ConnectionMethod? MethodOverride
    {
        get { lock (OverrideLock) return _methodOverride; }
        set { lock (OverrideLock) _methodOverride = value; }
    }

    public override ConnectionMethod Method
    {
        get
        {
            var forced = MethodOverride;
            if (forced.HasValue) return forced.Value;
            var m = ConnectionMethodExtensions.ParseConnectionMethod(_settings.Settings.ConnectionMethod);
            return m.IsAether() ? m : ConnectionMethod.Masque;
        }
    }

    public override IReadOnlyList<string> CoreProcessNames { get; } =
        new[] { EngineProcessNames.Aether };

    protected override string EngineDisplayName => Method.ToDisplayName();

    protected override string WorkSubdirectory => "aether";

    
    
    
    
    
    
    
    
    
    protected override TimeSpan ReadyTimeout
    {
        get
        {
            var s = _settings.Settings;
            var scan = Method switch
            {
                ConnectionMethod.WireGuard => !string.IsNullOrEmpty(s.AetherScanModeWireguard) ? s.AetherScanModeWireguard : s.AetherScanMode,
                ConnectionMethod.WarpOnWarp => !string.IsNullOrEmpty(s.AetherScanModeWarp) ? s.AetherScanModeWarp : s.AetherScanMode,
                _ => !string.IsNullOrEmpty(s.AetherScanModeMasque) ? s.AetherScanModeMasque : s.AetherScanMode,
            };
            return NormalizeScan(scan) switch
            {
                "turbo" => TimeSpan.FromSeconds(90),
                "thorough" => TimeSpan.FromSeconds(240),
                "verified" => TimeSpan.FromSeconds(240),
                "ironclad" => TimeSpan.FromSeconds(320),
                _ => TimeSpan.FromSeconds(180),
            };
        }
    }

    private static int? _socksPortOverride;

    internal static int? SocksPortOverride
    {
        get { lock (OverrideLock) return _socksPortOverride; }
        set { lock (OverrideLock) _socksPortOverride = value; }
    }

    protected override PreparedLaunch Prepare(string workDir, int socksPort, int httpPort)
    {
        var binName = OperatingSystem.IsWindows() ? "aether.exe" : "aether";
        var source = Path.Combine(AppDir, "Resources", "aether", binName);
        if (!File.Exists(source))
        {
            source = Path.Combine(AppDir, "Resources", "aether", "aether.exe");
        }
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

        
        string peer;
        string scanRaw;
        string noizeRaw;
        string ipVerRaw;

        switch (method)
        {
            case ConnectionMethod.WireGuard:
                peer = !string.IsNullOrWhiteSpace(s.AetherEndpointWireguard) ? s.AetherEndpointWireguard : s.AetherManualPeer;
                scanRaw = !string.IsNullOrWhiteSpace(s.AetherScanModeWireguard) ? s.AetherScanModeWireguard : s.AetherScanMode;
                noizeRaw = !string.IsNullOrWhiteSpace(s.AetherNoizeWireguard) ? s.AetherNoizeWireguard : s.AetherNoize;
                ipVerRaw = !string.IsNullOrWhiteSpace(s.AetherIpVersionWireguard) ? s.AetherIpVersionWireguard : s.AetherIpVersion;
                break;
            case ConnectionMethod.WarpOnWarp:
                peer = !string.IsNullOrWhiteSpace(s.AetherEndpointWarp) ? s.AetherEndpointWarp : s.AetherManualPeer;
                scanRaw = !string.IsNullOrWhiteSpace(s.AetherScanModeWarp) ? s.AetherScanModeWarp : s.AetherScanMode;
                noizeRaw = !string.IsNullOrWhiteSpace(s.AetherNoizeWarp) ? s.AetherNoizeWarp : s.AetherNoize;
                ipVerRaw = !string.IsNullOrWhiteSpace(s.AetherIpVersionWarp) ? s.AetherIpVersionWarp : s.AetherIpVersion;
                break;
            default: 
                peer = !string.IsNullOrWhiteSpace(s.AetherEndpointMasque) ? s.AetherEndpointMasque : s.AetherManualPeer;
                scanRaw = !string.IsNullOrWhiteSpace(s.AetherScanModeMasque) ? s.AetherScanModeMasque : s.AetherScanMode;
                noizeRaw = !string.IsNullOrWhiteSpace(s.AetherNoizeMasque) ? s.AetherNoizeMasque : s.AetherNoize;
                ipVerRaw = !string.IsNullOrWhiteSpace(s.AetherIpVersionMasque) ? s.AetherIpVersionMasque : s.AetherIpVersion;
                break;
        }

        
        bool hasMultiHopPeers = false;
        if (method == ConnectionMethod.WarpOnWarp)
        {
            var wiwOuter = s.AetherWiwOuterPeer?.Trim() ?? "";
            var wiwInner = s.AetherWiwInnerPeer?.Trim() ?? "";
            if (wiwOuter.Length > 0)
            {
                args.Add("--wiw-outer");
                args.Add(wiwOuter);
                hasMultiHopPeers = true;
            }
            if (wiwInner.Length > 0)
            {
                args.Add("--wiw-inner");
                args.Add(wiwInner);
                hasMultiHopPeers = true;
            }
        }

        
        peer = (peer ?? "").Trim();
        var scan = NormalizeScan(scanRaw);
        if (peer.Length > 0 && !hasMultiHopPeers)
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
        else if (!hasMultiHopPeers)
        {
            
            args.Add("--scan");
            args.Add(scan);
        }

        
        var exitLocRaw = method switch
        {
            ConnectionMethod.WireGuard => !string.IsNullOrWhiteSpace(s.AetherExitLocWireguard) ? s.AetherExitLocWireguard : s.AetherExitLoc,
            ConnectionMethod.WarpOnWarp => !string.IsNullOrWhiteSpace(s.AetherExitLocWarp) ? s.AetherExitLocWarp : s.AetherExitLoc,
            _ => !string.IsNullOrWhiteSpace(s.AetherExitLocMasque) ? s.AetherExitLocMasque : s.AetherExitLoc,
        };
        var exitLoc = (exitLocRaw ?? "").Trim();
        if (exitLoc.Length > 0)
        {
            args.Add("--exit-loc");
            args.Add(exitLoc);
        }

        
        var noize = (noizeRaw ?? "").Trim();
        if (noize.Length > 0 && !noize.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            args.Add("--noize");
            args.Add(noize);
        }

        
        switch ((ipVerRaw ?? "4").Trim().ToLowerInvariant())
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
            
            
            
            if (s.AetherMasqueQuic || transport == "h3")
            {
                args.Add("--h3");
            }
            else
            {
                args.Add("--h2");

                
                
                if (s.AetherFragment)
                {
                    args.Add("--fragment");
                    if (!string.IsNullOrWhiteSpace(s.AetherFragmentSize))
                    {
                        args.Add("--fragment-size");
                        args.Add(s.AetherFragmentSize.Trim());
                    }
                    if (!string.IsNullOrWhiteSpace(s.AetherFragmentDelay))
                    {
                        args.Add("--fragment-delay");
                        args.Add(s.AetherFragmentDelay.Trim());
                    }
                }
            }
        }

        
        
        
        
        if (s.AetherCacheEdges)
        {
            args.Add("--quick-reconnect");
        }
        else
        {
            args.Add("--no-quick-reconnect");
        }

        
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
        
        var primer = transport == "h2" ? "2\n" : "1\n";
        return new PreparedLaunch(exePath, args, workDir,
            StdinPrimer: primer,
            HttpProxyPort: httpPort,
            SocksPortOverride: SocksPortOverride);
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
            SetConnectProgress(70, string.Format(Loc.Of("Found edge {0}, validating..."), edge));
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
            SetConnectProgress(80, string.Format(Loc.Of("Transport: {0}"), _transportLabel));
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

        
        
        
        
        var cachedEdge = Extract(line, "verifying cached gateway ")
                      ?? Extract(line, "verifying cached WireGuard endpoint ");
        if (cachedEdge is not null)
        {
            var idx = cachedEdge.IndexOf(" before", StringComparison.Ordinal);
            var ip = idx > 0 ? cachedEdge.Substring(0, idx) : cachedEdge;
            SetConnectProgress(10, string.Format(Loc.Of("Verifying cached edge ({0})..."), ip));
        }
        else if (line.Contains("cached", StringComparison.OrdinalIgnoreCase)
              && (line.Contains("no longer answers", StringComparison.OrdinalIgnoreCase)
               || line.Contains("no longer responds", StringComparison.OrdinalIgnoreCase)))
        {
            _candidatesFound = 0;
            SetConnectProgress(15, Loc.Of("Cached edge expired; hunting fresh edge IPs..."));
        }
        else if (line.Contains("hunting for a working", StringComparison.OrdinalIgnoreCase))
        {
            _candidatesFound = 0;
            SetConnectProgress(20, Loc.Of("Hunting for working Cloudflare edge..."));
        }
        else if (line.Contains("prober", StringComparison.OrdinalIgnoreCase) && line.Contains("scan mode=", StringComparison.OrdinalIgnoreCase))
        {
            var candMatch = Extract(line, "candidates=");
            var cCount = candMatch?.Split(' ')[0] ?? "2000+";
            SetConnectProgress(25, string.Format(Loc.Of("Scanning {0} edge candidates in parallel..."), cCount));
        }
        else if (line.Contains("candidate ok", StringComparison.OrdinalIgnoreCase))
        {
            _candidatesFound++;
            var cand = Extract(line, "candidate ok ");
            var pct = Math.Min(25 + (_candidatesFound * 6), 65);
            SetConnectProgress(pct, string.Format(Loc.Of("Candidate OK ({0}): {1}"), _candidatesFound, cand ?? "found"));
        }
        else if (line.Contains("selected MASQUE gateway", StringComparison.OrdinalIgnoreCase)
              || line.Contains("selected WireGuard endpoint", StringComparison.OrdinalIgnoreCase)
              || line.Contains("best gateway", StringComparison.OrdinalIgnoreCase))
        {
            var best = Extract(line, "selected MASQUE gateway ")
                    ?? Extract(line, "selected WireGuard endpoint ")
                    ?? Extract(line, "best gateway ");
            SetConnectProgress(70, string.Format(Loc.Of("Selected best edge: {0}"), best ?? "ready"));
        }
        else if (line.Contains("[h2] connecting tcp to", StringComparison.OrdinalIgnoreCase) || line.Contains("[h3] connecting", StringComparison.OrdinalIgnoreCase))
        {
            SetConnectProgress(75, Loc.Of("Connecting to edge gateway..."));
        }
        else if (line.Contains("fragmenting client hello", StringComparison.OrdinalIgnoreCase))
        {
            SetConnectProgress(80, Loc.Of("Applying TLS ClientHello fragmentation..."));
        }
        else if (line.Contains("tls established", StringComparison.OrdinalIgnoreCase))
        {
            SetConnectProgress(85, Loc.Of("TLS handshake established..."));
        }
        else if (line.Contains("connect-ip request sent", StringComparison.OrdinalIgnoreCase))
        {
            SetConnectProgress(88, Loc.Of("MASQUE tunnel requested..."));
        }
        else if (line.Contains("connect-ip status: 200", StringComparison.OrdinalIgnoreCase))
        {
            SetConnectProgress(90, Loc.Of("MASQUE connected (200), confirming data-plane..."));
        }
        else if (line.Contains("tunnel validated", StringComparison.OrdinalIgnoreCase) || line.Contains("data-plane", StringComparison.OrdinalIgnoreCase))
        {
            SetConnectProgress(96, Loc.Of("Data-plane confirmed! Exposing proxy..."));
        }
        else if (line.Contains("socks5 server listening", StringComparison.OrdinalIgnoreCase) || line.Contains("handshake_confirmed", StringComparison.OrdinalIgnoreCase))
        {
            SetConnectProgress(100, Loc.Of("Connected"));
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
            
            "stealth" => "verified",
            "verified" => "verified",
            "ironclad" => "ironclad",
            _ => "balanced",
        };

        public static string NormalizeTransport(string? transport) =>
        (transport ?? "").Trim().ToLowerInvariant() switch
        {
            "h3" or "http3" or "http/3" or "quic" => "h3",
            _ => "h2",
        };
}
