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

    private string? _activeLastConnWorkDir;
    private string? _activeLastConnBucket;

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

        var settings = _settings.Settings;

        if (!string.IsNullOrEmpty(UpstreamProxyOverride) || !string.IsNullOrWhiteSpace(settings.AetherUpstreamProxy))
        {
            Log("[Aether] Dialing through upstream/chained proxy; aether registers its own identity through it.");
            return;
        }

        var acqMethod = settings.AetherIdentityAcquisitionMethod ?? "lantern";
        if (string.Equals(acqMethod, "ech", StringComparison.OrdinalIgnoreCase) || AetherExtras.ShouldDeferProvisioning(settings))
        {
            if (AetherExtras.ShouldDeferProvisioning(settings))
            {
                Log("[Aether] ECH is on; letting aether register its own WARP identity through the encrypted handshake. SHARD stands by as the fallback.");
                return;
            }
        }

        SetConnectProgress(10, Loc.Of("Checking Cloudflare WARP identity..."));
        await _identityProvisioner.EnsureIdentityWithMethodAsync(
            workDir,
            Method,
            acqMethod,
            p => SetConnectProgress(p.Percent, Loc.Of(p.Text)),
            ct);
    }

    public override async Task<bool> RecoverFromMissingPrerequisitesAsync(CancellationToken ct)
    {
        if (!AetherExtras.ShouldDeferProvisioning(_settings.Settings))
        {
            return false;
        }

        if (!AetherExtras.TryClaimEchFallback())
        {
            Log("[Aether] SHARD fallback already attempted for this connect; not repeating it.");
            return false;
        }

        Log("[Aether] ECH could not register the WARP identity. Acquiring one through a relay or SHARD instead.");

        SetConnectProgress(15, Loc.Of("Acquiring a WARP identity through SHARD..."));

        await _identityProvisioner.EnsureIdentityAsync(
            WorkDirectory,
            Method,
            p => SetConnectProgress(p.Percent, Loc.Of(p.Text)),
            ct);

        return _identityProvisioner.HasValidIdentity(WorkDirectory, Method);
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
                ConnectionMethod.MasqueInMasque => !string.IsNullOrEmpty(s.AetherScanModeMim) ? s.AetherScanModeMim : s.AetherScanMode,
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

    internal static string? UpstreamProxyOverride
    {
        get { lock (OverrideLock) return _upstreamProxyOverride; }
        set { lock (OverrideLock) _upstreamProxyOverride = value; }
    }

    private static string? _upstreamProxyOverride;

    protected override PreparedLaunch Prepare(string workDir, int socksPort, int httpPort)
    {
        var binName = OperatingSystem.IsWindows() ? "aether.exe" : "aether";
        var source = Path.Combine(AppDir, "Resources", "aether", binName);
        if (!File.Exists(source))
        {
            source = Path.Combine(AppDir, "Resources", "aether", "aether.exe");
        }
        var exePath = StageExecutable(source, Path.Combine(workDir, EngineProcessNames.Aether));

        var bundledVersion = AetherCapabilities.Version(source);
        var stagedVersion = AetherCapabilities.Version(exePath);
        if (!string.Equals(exePath, source, StringComparison.OrdinalIgnoreCase)
            && AetherExtras.ShouldRefreshStagedCore(bundledVersion, stagedVersion))
        {
            Log($"[Aether] The staged core reports '{stagedVersion}' but the bundled core is "
                + $"'{bundledVersion}'. Refreshing the staged copy so the upgrade takes effect.");

            try
            {
                File.Delete(exePath);
                exePath = StageFile(source, Path.Combine(workDir, EngineProcessNames.Aether));
                AetherCapabilities.Invalidate();
                Log($"[Aether] Staged core refreshed: {AetherCapabilities.Version(exePath)}");
            }
            catch (Exception ex)
            {
                Log($"[Aether] Could not refresh the staged core ({ex.Message}). "
                    + "It will keep running the older version.");
            }
        }

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
                if (AetherExtras.IsClassicGool(s))
                {
                    if (AetherCapabilities.Supports(exePath, "--gool-classic"))
                    {
                        args.Add("--gool-classic");
                    }
                    else
                    {
                        args.Add("--gool");
                    }
                }
                else if (AetherCapabilities.Supports(exePath, "--gool"))
                {
                    args.Add("--gool");
                }
                break;
            case ConnectionMethod.MasqueInMasque:
                args.Add("--mim");
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
            case ConnectionMethod.MasqueInMasque:
                peer = !string.IsNullOrWhiteSpace(s.AetherEndpointMim) ? s.AetherEndpointMim : s.AetherManualPeer;
                scanRaw = !string.IsNullOrWhiteSpace(s.AetherScanModeMim) ? s.AetherScanModeMim : (string.IsNullOrWhiteSpace(s.AetherScanMode) ? "turbo" : s.AetherScanMode);
                noizeRaw = !string.IsNullOrWhiteSpace(s.AetherNoizeMim) ? s.AetherNoizeMim : s.AetherNoize;
                ipVerRaw = !string.IsNullOrWhiteSpace(s.AetherIpVersionMim) ? s.AetherIpVersionMim : s.AetherIpVersion;
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
            if (AetherExtras.IsClassicGool(s))
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
            else
            {
                var goolPeer = s.AetherGoolPeer?.Trim() ?? "";
                if (goolPeer.Length > 0 && AetherCapabilities.Supports(exePath, "--gool-peer"))
                {
                    args.Add("--gool-peer");
                    args.Add(goolPeer);
                }
            }
        }
        else if (method == ConnectionMethod.MasqueInMasque)
        {
            var mimOuter = s.AetherMimOuterPeer?.Trim() ?? "";
            var mimInner = s.AetherMimInnerPeer?.Trim() ?? "";
            if (mimOuter.Length > 0)
            {
                args.Add("--mim-outer");
                args.Add(mimOuter);
                hasMultiHopPeers = true;
            }
            if (mimInner.Length > 0)
            {
                args.Add("--mim-inner");
                args.Add(mimInner);
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
            ConnectionMethod.MasqueInMasque => !string.IsNullOrWhiteSpace(s.AetherExitLocMim) ? s.AetherExitLocMim : s.AetherExitLoc,
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

        var upstream = UpstreamProxyOverride;
        if (UpstreamProxyOverride is null && !string.IsNullOrWhiteSpace(s.AetherUpstreamProxy))
        {
            upstream = s.AetherUpstreamProxy;
        }
        var upstreamArgs = AetherExtras.BuildUpstreamArgs(upstream);
        var transport = AetherExtras.EffectiveTransport(s, upstreamArgs.Count > 0);
        var isGool = method == ConnectionMethod.WarpOnWarp;
        var isMasqueTunnel = method is ConnectionMethod.Masque or ConnectionMethod.MasqueInMasque
                             || (isGool && !AetherExtras.IsClassicGool(s));
        if (isMasqueTunnel)
        {
            var forceH2 = isGool && AetherExtras.IsGoolH2(s);
            if (transport == "h3" && !forceH2)
            {
                args.Add("--h3");
            }
            else
            {
                if (upstreamArgs.Count > 0
                    && NormalizeTransport(s.AetherMasqueTransport) == "h3")
                {
                    Log("[Aether] The chained proxy carries TCP only, so MASQUE runs over HTTP/2.");
                }

                args.Add("--h2");

                var shouldFragment = isGool ? (s.AetherFragmentWireguard || s.AetherFragment) : s.AetherFragment;
                if (shouldFragment)
                {
                    args.Add("--fragment");
                    var fragSize = !string.IsNullOrWhiteSpace(s.AetherFragmentSize) ? s.AetherFragmentSize.Trim() : "16-32";
                    args.Add("--fragment-size");
                    args.Add(fragSize);

                    var fragDelay = !string.IsNullOrWhiteSpace(s.AetherFragmentDelay) ? s.AetherFragmentDelay.Trim() : "2-10";
                    args.Add("--fragment-delay");
                    args.Add(fragDelay);
                }
            }
        }
        else if (method is ConnectionMethod.WireGuard
                 || (method == ConnectionMethod.WarpOnWarp && AetherExtras.IsClassicGool(s)))
        {
            if (s.AetherFragmentWireguard
                && AetherCapabilities.Supports(exePath, "--fragment"))
            {
                args.Add("--fragment");
                var fragSize = !string.IsNullOrWhiteSpace(s.AetherFragmentSize) ? s.AetherFragmentSize.Trim() : "16-32";
                args.Add("--fragment-size");
                args.Add(fragSize);
                var fragDelay = !string.IsNullOrWhiteSpace(s.AetherFragmentDelay) ? s.AetherFragmentDelay.Trim() : "2-10";
                args.Add("--fragment-delay");
                args.Add(fragDelay);
            }
        }
        if (upstreamArgs.Count > 0)
        {
            args.AddRange(upstreamArgs);
            Log($"[Aether] Dialing out through {AetherExtras.RedactUpstreamUrl(upstreamArgs[1])}.");
        }

        AetherExtras.AddOptional(args, s, exePath, method, Log);

        var isChained = upstreamArgs.Count > 0;
        var lastConnBucket = AetherExtras.LastConnBucketKey(method, transport, AetherExtras.IsClassicGool(s), isChained);
        _activeLastConnWorkDir = workDir;
        _activeLastConnBucket = lastConnBucket;
        AetherExtras.RestoreLastConn(workDir, lastConnBucket, s.AetherCacheEdges);

        if (s.AetherCacheEdges)
        {
            args.Add("--quick-reconnect");
        }
        else
        {
            args.Add("--no-quick-reconnect");
        }

        args.Add("--validate-secs");
        args.Add(AetherExtras.DefaultValidateSecs.ToString());

        var dnsField = AetherExtras.BuildDnsField(s);
        args.Add("--dns");
        args.Add(dnsField);

        if (method == ConnectionMethod.WireGuard)
        {
            args.Add("--keepalive");
            args.Add("5");
        }

        _transportLabel = method switch
        {
            ConnectionMethod.Masque => transport == "h2" ? "HTTP/2 (TCP)" : "HTTP/3 (QUIC)",
            ConnectionMethod.MasqueInMasque => "MASQUE on MASQUE (MiM)",
            _ => method.ToDisplayName()
        };

        Log($"[Aether] {AetherCapabilities.Version(exePath)}; "
            + $"dns={dnsField}; validate={AetherExtras.DefaultValidateSecs}s; bucket={lastConnBucket}; "
            + $"optional=[{string.Join(",", AetherExtras.AppliedOptionalFlags)}]");
        _obfuscationLabel = "";
        PublishRoute();

        Log($"Launching Aether ({method.ToDisplayName()}, scan={scan}"
          + (method is ConnectionMethod.Masque or ConnectionMethod.MasqueInMasque ? $", transport={_transportLabel}" : "")
          + (noize.Length > 0 ? $", noize={noize}" : "") + ").");

        _candidatesFound = 0;
        _serverEndpointAddress = "";
        _reportedMasqueHttp400 = false;
        
        var aetherMtu = s.GetAetherMtu(method);
        var isMasque = method is ConnectionMethod.Masque or ConnectionMethod.MasqueInMasque
                    || (method == ConnectionMethod.WarpOnWarp && !AetherExtras.IsClassicGool(s));

        var validateSecsStr = AetherExtras.DefaultValidateSecs.ToString();
        var env = new Dictionary<string, string>
        {
            [isMasque ? "AETHER_MASQUE_MTU" : "AETHER_WG_MTU"] = aetherMtu.ToString(),
            ["AETHER_MASQUE_VALIDATE_SECS"] = validateSecsStr,
            ["AETHER_WG_VALIDATE_SECS"] = validateSecsStr,
            ["AETHER_PERF_PROFILE"] = "high",
            ["AETHER_STATS"] = "1",
            ["AETHER_STATS_SECS"] = "86400"
        };

        var isFragmentActive = isMasqueTunnel ? (isGool ? (s.AetherFragmentWireguard || s.AetherFragment) : s.AetherFragment) : s.AetherFragmentWireguard;
        if (isFragmentActive && (transport == "h2" || string.IsNullOrWhiteSpace(s.AetherMasqueTransport)))
        {
            env["AETHER_MASQUE_H2_FRAGMENT"] = "1";
            var fragSize = !string.IsNullOrWhiteSpace(s.AetherFragmentSize) ? s.AetherFragmentSize.Trim() : "16-32";
            var fragDelay = !string.IsNullOrWhiteSpace(s.AetherFragmentDelay) ? s.AetherFragmentDelay.Trim() : "2-10";
            env["AETHER_MASQUE_H2_FRAGMENT_SIZE"] = fragSize;
            env["AETHER_MASQUE_H2_FRAGMENT_DELAY"] = fragDelay;
        }

        if (isMasque)
        {
            var sni = !string.IsNullOrWhiteSpace(s.AetherMasqueSni) ? s.AetherMasqueSni.Trim() : "www.cloudflare.com";
            env["AETHER_MASQUE_SNI"] = sni;
        }

        var primer = transport == "h2" ? "2\n" : "1\n";
        return new PreparedLaunch(exePath, args, workDir,
            StdinPrimer: primer,
            HttpProxyPort: httpPort,
            SocksPortOverride: actualSocks,
            EnvironmentVariables: env);
    }

    

    private string _transportLabel = "";
    private string _obfuscationLabel = "";
    private int _candidatesFound;
    private volatile string _serverEndpointAddress = "";
    private bool _reportedMasqueHttp400;

    public override string ServerEndpointAddress => _serverEndpointAddress;

    private void RecordEdgeEndpoint(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;
        var token = raw.Trim().Split(new[] { ' ', '\t', '(', ')' }, StringSplitOptions.RemoveEmptyEntries)[0];
        if (token.StartsWith("[", StringComparison.Ordinal))
        {
            var close = token.IndexOf(']');
            if (close > 1) token = token.Substring(1, close - 1);
        }
        else
        {
            var colon = token.LastIndexOf(':');
            if (colon > 0 && token.IndexOf(':') == colon)
                token = token.Substring(0, colon);
        }
        if (token.Length > 0 && !string.Equals(_serverEndpointAddress, token, StringComparison.Ordinal))
        {
            _serverEndpointAddress = token;
            if (State == ConnectionState.Connected) RaiseRouteChanged();
        }
    }

        protected override void OnCoreLine(string line)
    {
        var edge = Extract(line, "using cloudflare edge ");
        if (edge is not null)
        {
            RecordEdgeEndpoint(edge);
            if (State != ConnectionState.Connected
                && !string.Equals(CurrentRouteIp, edge, StringComparison.Ordinal))
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
            RecordEdgeEndpoint(ip);
            SetConnectProgress(10, string.Format(Loc.Of("Verifying cached edge ({0})..."), ip));
        }
        else if (line.Contains("cached", StringComparison.OrdinalIgnoreCase)
              && (line.Contains("no longer answers", StringComparison.OrdinalIgnoreCase)
               || line.Contains("no longer responds", StringComparison.OrdinalIgnoreCase)))
        {
            _candidatesFound = 0;
        _serverEndpointAddress = "";
        _reportedMasqueHttp400 = false;
            SetConnectProgress(15, Loc.Of("Cached edge expired; hunting fresh edge IPs..."));
        }
        else if (line.Contains("hunting for a working", StringComparison.OrdinalIgnoreCase))
        {
            _candidatesFound = 0;
        _serverEndpointAddress = "";
        _reportedMasqueHttp400 = false;
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
            RecordEdgeEndpoint(best);
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
        else if (line.Contains("trying inner MASQUE edge", StringComparison.OrdinalIgnoreCase))
        {
            SetConnectProgress(82, Loc.Of("Outer connected (1/2) -> Connecting inner MASQUE (2/2)..."));
        }
        else if (line.Contains("[inner]", StringComparison.OrdinalIgnoreCase))
        {
            if (line.Contains("connecting tcp to", StringComparison.OrdinalIgnoreCase))
                SetConnectProgress(85, Loc.Of("MASQUE [2/2]: Connecting to inner edge..."));
            else if (line.Contains("tls established", StringComparison.OrdinalIgnoreCase))
                SetConnectProgress(88, Loc.Of("MASQUE [2/2]: Inner TLS established..."));
            else if (line.Contains("connect-ip status: 200", StringComparison.OrdinalIgnoreCase))
                SetConnectProgress(94, Loc.Of("MASQUE [2/2]: Inner tunnel established (200 OK)..."));
        }
        else if (line.Contains("inner MASQUE tunnel established", StringComparison.OrdinalIgnoreCase))
        {
            SetConnectProgress(96, Loc.Of("Both MASQUE hops established!"));
        }
        else if (line.Contains("masque-in-masque ready", StringComparison.OrdinalIgnoreCase) || line.Contains("warp-in-warp ready", StringComparison.OrdinalIgnoreCase))
        {
            SetConnectProgress(98, Loc.Of("Double-hop tunnel ready!"));
        }
        else if (line.Contains("connect-ip request sent", StringComparison.OrdinalIgnoreCase))
        {
            SetConnectProgress(88, Loc.Of("MASQUE tunnel requested..."));
        }
        else if (line.Contains("connect-ip status: 200", StringComparison.OrdinalIgnoreCase))
        {
            SetConnectProgress(90, Loc.Of("MASQUE connected (200), confirming data-plane..."));
        }
        else if (line.Contains("connect-ip status: 400", StringComparison.OrdinalIgnoreCase)
                 && !string.IsNullOrEmpty(UpstreamProxyOverride)
                 && !_reportedMasqueHttp400)
        {
            _reportedMasqueHttp400 = true;
            Log("[Aether] Upstream proxy returned HTTP 400 for WARP MASQUE connect-ip (Cloudflare Workers/CDN relays cannot carry WARP MASQUE; try a VPS-backed V2Ray node or Psiphon over V2Ray).");
        }
        else if (line.Contains("tunnel validated", StringComparison.OrdinalIgnoreCase) || line.Contains("data-plane", StringComparison.OrdinalIgnoreCase))
        {
            SetConnectProgress(96, Loc.Of("Data-plane confirmed! Exposing proxy..."));
        }
        else if (line.Contains("handshake_confirmed", StringComparison.OrdinalIgnoreCase))
        {
            SetConnectProgress(100, Loc.Of("Connected"));
        }
        else if (line.Contains("socks5 server listening", StringComparison.OrdinalIgnoreCase))
        {
            SetConnectProgress(30, Loc.Of("Local proxy listening, negotiating tunnel..."));
        }
        else if (line.Contains("tunnel established", StringComparison.OrdinalIgnoreCase)
                 || line.Contains("connected to", StringComparison.OrdinalIgnoreCase)
                 || line.Contains("serving socks5", StringComparison.OrdinalIgnoreCase))
        {
            AetherExtras.SnapshotLastConn(_activeLastConnWorkDir, _activeLastConnBucket);
        }
    }

    protected override void OnTunnelConnected(int socksPort, int httpPort, CancellationToken ct)
    {
        AetherExtras.SnapshotLastConn(_activeLastConnWorkDir, _activeLastConnBucket);
        base.OnTunnelConnected(socksPort, httpPort, ct);
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
            "balanced" => "balanced",
            "thorough" => "thorough",
            
            "stealth" => "verified",
            "verified" => "verified",
            "ironclad" => "ironclad",
            _ => "turbo",
        };

        public static string NormalizeTransport(string? transport) =>
        (transport ?? "").Trim().ToLowerInvariant() switch
        {
            "h3" or "http3" or "http/3" or "quic" => "h3",
            _ => "h2",
        };
}
