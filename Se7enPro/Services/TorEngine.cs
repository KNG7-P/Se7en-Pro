using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Se7enPro.Models;

namespace Se7enPro.Services;

public sealed class TorEngine : LocalSocksEngineBase
{
    public TorEngine(
        ILogger<TorEngine> logger,
        ISettingsService settings,
        IChildProcessGuard childGuard)
        : base(logger, settings, childGuard)
    {
    }

    public override ConnectionMethod Method => ConnectionMethod.Tor;

    public override IReadOnlyList<string> CoreProcessNames { get; } = new[]
    {
        EngineProcessNames.Tor,
        EngineProcessNames.TorPtLyrebird,
        EngineProcessNames.TorPtConjure,
    };

    protected override string EngineDisplayName => "Tor";

    protected override string WorkSubdirectory => "tor";

    
    
    protected override TimeSpan ReadyTimeout => TimeSpan.FromSeconds(150);

    protected override PreparedLaunch Prepare(string workDir, int socksPort, int httpPort)
    {
        var resTor = Path.Combine(AppDir, "Resources", "tor");
        var torBin = OperatingSystem.IsWindows() ? "tor.exe" : "tor";
        var torSource = Path.Combine(resTor, torBin);
        if (!File.Exists(torSource)) torSource = Path.Combine(resTor, "tor.exe");

        
        var exePath = StageFile(
            torSource,
            Path.Combine(workDir, EngineProcessNames.Tor));

        var dataDir = Path.Combine(workDir, "data");
        Directory.CreateDirectory(dataDir);
        var geoip = StageFile(Path.Combine(resTor, "data", "geoip"), Path.Combine(dataDir, "geoip"));
        var geoip6 = StageFile(Path.Combine(resTor, "data", "geoip6"), Path.Combine(dataDir, "geoip6"));

        var ptDir = Path.Combine(workDir, "pluggable_transports");
        Directory.CreateDirectory(ptDir);
        var lyrebirdBin = OperatingSystem.IsWindows() ? "lyrebird.exe" : "lyrebird";
        var lyrebirdSource = Path.Combine(resTor, "pluggable_transports", lyrebirdBin);
        if (!File.Exists(lyrebirdSource)) lyrebirdSource = Path.Combine(resTor, "pluggable_transports", "lyrebird.exe");
        var lyrebird = StageFile(
            lyrebirdSource,
            Path.Combine(ptDir, EngineProcessNames.TorPtLyrebird));

        string conjure = "";
        var conjureBin = OperatingSystem.IsWindows() ? "conjure-client.exe" : "conjure-client";
        var conjureSource = Path.Combine(resTor, "pluggable_transports", conjureBin);
        if (!File.Exists(conjureSource)) conjureSource = Path.Combine(resTor, "pluggable_transports", "conjure-client.exe");
        if (File.Exists(conjureSource))
        {
            conjure = StageFile(conjureSource, Path.Combine(ptDir, EngineProcessNames.TorPtConjure));
        }

        var torData = Path.Combine(workDir, "tordata");
        Directory.CreateDirectory(torData);

        var torrcPath = Path.Combine(workDir, "torrc");
        File.WriteAllText(torrcPath,
            BuildTorrc(socksPort, httpPort, torData, geoip, geoip6, lyrebird, conjure));

        
        
        
        var exit = NormalizeExitCountry(_settings.Settings.TorExitCountry);
        ConnectedServerRegion = exit.Length == 2 ? exit.ToUpperInvariant() : "";
        CurrentRouteIp = "";
        _bootstrapText = "Bootstrapping…";
        CurrentRouteSni = _bootstrapText;
        RaiseRouteChanged();
        SetConnectProgress(10, Loc.Of("Tor: Starting onion router..."));

        Log(exit.Length == 2
            ? $"Launching Tor (exit country {exit.ToUpperInvariant()} — strict). If no exit relay is available in that country the bootstrap will not finish; pick Automatic to use any exit."
            : "Launching Tor (any exit country).");

        Dictionary<string, string>? env = null;
        if (!string.IsNullOrEmpty(Socks5ProxyOverride))
        {
            env = new Dictionary<string, string>
            {
                ["TOR_PT_PROXY"] = $"socks5://{Socks5ProxyOverride}"
            };
        }

        return new PreparedLaunch(exePath, new[] { "-f", torrcPath }, workDir,
            HttpProxyPort: httpPort,
            EnvironmentVariables: env);
    }

    private string _bootstrapText = "";

        protected override void OnCoreLine(string line)
    {
        var i = line.IndexOf("Bootstrapped ", StringComparison.Ordinal);
        if (i < 0) return;

        var text = line.Substring(i + "Bootstrapped ".Length).Trim();

        
        var colon = text.IndexOf(':');
        if (colon > 0) text = text.Substring(0, colon).Trim();
        if (text.Length == 0) return;

        _bootstrapText = "Bootstrapped " + text;
        CurrentRouteSni = _bootstrapText;
        RaiseRouteChanged();

        var pctEnd = text.IndexOf('%');
        if (pctEnd > 0 && int.TryParse(text.Substring(0, pctEnd), out var pct))
        {
            SetConnectProgress(pct, $"Tor: {_bootstrapText}");
        }
    }

    
    
    
    private static readonly object OverrideLock = new();
    private static string? _socks5ProxyOverride;

    internal static string? Socks5ProxyOverride
    {
        get { lock (OverrideLock) return _socks5ProxyOverride; }
        set { lock (OverrideLock) _socks5ProxyOverride = value; }
    }

    private string BuildTorrc(
        int socksPort, int httpPort, string torData,
        string geoip, string geoip6, string lyrebird, string conjure)
    {
        var s = _settings.Settings;
        var sb = new StringBuilder();

        sb.AppendLine($"SocksPort 127.0.0.1:{socksPort}");
        var isChained = !string.IsNullOrEmpty(Socks5ProxyOverride);
        if (isChained)
        {
            
            sb.AppendLine($"Socks5Proxy {Socks5ProxyOverride}");
        }

        
        
        
        
        
        sb.AppendLine($"HTTPTunnelPort 127.0.0.1:{httpPort}");
        sb.AppendLine($"DataDirectory {P(torData)}");
        sb.AppendLine($"GeoIPFile {P(geoip)}");
        sb.AppendLine($"GeoIPv6File {P(geoip6)}");
        sb.AppendLine("Log notice stdout");
        sb.AppendLine("ClientOnly 1");
        sb.AppendLine("AvoidDiskWrites 1");

        var exit = NormalizeExitCountry(s.TorExitCountry);
        if (exit.Length == 2)
        {
            sb.AppendLine($"ExitNodes {{{exit}}}");
            sb.AppendLine("StrictNodes 1");
            
            
            
            sb.AppendLine("MaxCircuitDirtiness 60");
        }

        if (!isChained)
        {
            var bridges = ParseBridges(s.TorBridges);
            if (bridges.Count > 0)
            {
                sb.AppendLine("UseBridges 1");
                
                
                
                
                sb.AppendLine(
                    $"ClientTransportPlugin obfs4,meek_lite,webtunnel,snowflake exec {P(lyrebird)}");
                if (!string.IsNullOrEmpty(conjure))
                {
                    sb.AppendLine($"ClientTransportPlugin conjure exec {P(conjure)}");
                }
                foreach (var bridge in bridges)
                {
                    sb.AppendLine($"Bridge {bridge}");
                }
            }
        }
        else if (ParseBridges(s.TorBridges).Count > 0)
        {
            
            
            
            Log("Bridge lines are ignored while Tor runs through the outer hop:"
              + " the chain's own transport is what hides the connection.");
        }

        return sb.ToString();
    }

        private static string NormalizeExitCountry(string? raw)
    {
        var v = (raw ?? "").Trim().Trim('{', '}').ToLowerInvariant();
        return v is "auto" or "any" ? "" : v;
    }

    private static readonly HashSet<string> KnownBridgeTransports = new(StringComparer.OrdinalIgnoreCase)
    {
        "obfs4", "obfs3", "obfs", "meek_lite", "webtunnel", "snowflake", "conjure",
    };

    private List<string> ParseBridges(string? raw)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(raw)) return result;

        foreach (var line in raw.Replace("\r\n", "\n").Split('\n'))
        {
            var t = line.Trim();
            if (t.Length == 0 || t.StartsWith("#")) continue;
            
            if (t.StartsWith("Bridge ", StringComparison.OrdinalIgnoreCase))
                t = t.Substring("Bridge ".Length).Trim();
            if (t.Length == 0) continue;

            
            
            
            
            
            if (IsValidBridgeLine(t))
            {
                result.Add(t);
            }
            else
            {
                Log($"Ignoring an invalid bridge line: \"{t}\"");
            }
        }
        return result;
    }

        private static bool IsValidBridgeLine(string line)
    {
        var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts.Length > 5) return false;
        if (!KnownBridgeTransports.Contains(parts[0])) return false;

        
        
        if (parts[0] is "snowflake" or "meek_lite" or "webtunnel" or "conjure")
        {
            if (!IsFingerprint(parts[1]) && !TryParseHostPort(parts[1], out _)) return false;
        }
        else if (!TryParseHostPort(parts[1], out _))
        {
            return false;
        }

        
        if (parts.Length >= 3 && !IsFingerprint(parts[2]) && !TryParseHostPort(parts[2], out _))
        {
            return false;
        }

        return true;
    }

    private static bool IsFingerprint(string token) =>
        token.Length == 40 && token.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'));

    private static bool TryParseHostPort(string token, out string host)
    {
        host = "";
        var colon = token.LastIndexOf(':');
        if (colon <= 0 || colon == token.Length - 1) return false;
        if (!int.TryParse(token[(colon + 1)..], out var port) || port is < 1 or > 65535) return false;
        host = token[..colon];
        return host.Length > 0;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern uint GetShortPathName(
        string lpszLongPath,
        [Out] StringBuilder lpszShortPath,
        uint cchBuffer);

    private static string P(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var full = Path.GetFullPath(path);
            if (full.Contains(' '))
            {
                var sb = new StringBuilder(260);
                var length = GetShortPathName(full, sb, (uint)sb.Capacity);
                if (length > 0 && length < sb.Capacity)
                {
                    return sb.ToString();
                }
            }
            return full;
        }

        return path.Replace('\\', '/');
    }
}
