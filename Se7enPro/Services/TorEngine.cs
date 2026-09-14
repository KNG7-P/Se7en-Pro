using System;
using System.Collections.Generic;
using System.IO;
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

    protected override bool AutoProbeUpdatesConnectedServerRegion => false;

    protected override PreparedLaunch Prepare(string workDir, int socksPort, int httpPort)
    {
        var resTor = Path.Combine(AppDir, "Resources", "tor");

        var exePath = StageFile(
            Path.Combine(resTor, "tor.exe"),
            Path.Combine(workDir, EngineProcessNames.Tor));

        var dataDir = Path.Combine(workDir, "data");
        Directory.CreateDirectory(dataDir);
        var geoip = StageFile(Path.Combine(resTor, "data", "geoip"), Path.Combine(dataDir, "geoip"));
        var geoip6 = StageFile(Path.Combine(resTor, "data", "geoip6"), Path.Combine(dataDir, "geoip6"));

        var ptDir = Path.Combine(workDir, "pluggable_transports");
        Directory.CreateDirectory(ptDir);
        var lyrebird = StageFile(
            Path.Combine(resTor, "pluggable_transports", "lyrebird.exe"),
            Path.Combine(ptDir, EngineProcessNames.TorPtLyrebird));
        var conjure = StageFile(
            Path.Combine(resTor, "pluggable_transports", "conjure-client.exe"),
            Path.Combine(ptDir, EngineProcessNames.TorPtConjure));

        var torData = Path.Combine(workDir, "tordata");
        Directory.CreateDirectory(torData);

        var exit = NormalizeExitCountry(_settings.Settings.TorExitCountry);
        if (exit.Length == 2)
        {
            try
            {
                var stateFile = Path.Combine(torData, "state");
                if (File.Exists(stateFile))
                {
                    File.Delete(stateFile);
                }
            }
            catch { }
        }

        var torrcPath = Path.Combine(workDir, "torrc");
        File.WriteAllText(torrcPath,
            BuildTorrc(socksPort, httpPort, torData, geoip, geoip6, lyrebird, conjure));

        ConnectedServerRegion = exit.Length == 2 ? exit.ToUpperInvariant() : "";
        CurrentRouteIp = "";
        _bootstrapText = "Bootstrapping…";
        CurrentRouteSni = _bootstrapText;
        RaiseRouteChanged();

        Log(exit.Length == 2
            ? $"Launching Tor (exit country {exit.ToUpperInvariant()} — strict). If no exit relay is available in that country the bootstrap will not finish; pick Automatic to use any exit."
            : "Launching Tor (any exit country).");

        Dictionary<string, string>? env = null;
        var _socksOverride = TryGetSocks5ProxyOverride();
        if (!string.IsNullOrEmpty(_socksOverride))
        {
            env = new Dictionary<string, string>
            {
                ["TOR_PT_PROXY"] = $"socks5://{_socksOverride}"
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

    private static readonly object _overrideLock = new();
    private static string? _socks5ProxyOverride;
    internal static void SetSocks5ProxyOverride(string v) { lock (_overrideLock) _socks5ProxyOverride = v; }
    internal static void ClearSocks5ProxyOverride() { lock (_overrideLock) _socks5ProxyOverride = null; }
    private static string? TryGetSocks5ProxyOverride() { lock (_overrideLock) return _socks5ProxyOverride; }

    private string BuildTorrc(
        int socksPort, int httpPort, string torData,
        string geoip, string geoip6, string lyrebird, string conjure)
    {
        var s = _settings.Settings;
        var sb = new StringBuilder();

        sb.AppendLine($"SocksPort 127.0.0.1:{socksPort}");
        var bridges = ParseBridges(s.TorBridges);
        var socks5Override = TryGetSocks5ProxyOverride();
        if (!string.IsNullOrEmpty(socks5Override))
        {
            sb.AppendLine($"Socks5Proxy {socks5Override}");
        }

        sb.AppendLine($"HTTPTunnelPort 127.0.0.1:{httpPort}");
        sb.AppendLine($"DataDirectory {ToCleanPath(torData)}");
        sb.AppendLine($"GeoIPFile {ToCleanPath(geoip)}");
        sb.AppendLine($"GeoIPv6File {ToCleanPath(geoip6)}");
        sb.AppendLine("Log notice stdout");
        sb.AppendLine("ClientOnly 1");
        sb.AppendLine("AvoidDiskWrites 1");

        var exit = NormalizeExitCountry(s.TorExitCountry);
        if (exit.Length == 2)
        {
            sb.AppendLine($"ExitNodes {{{exit}}}");
            sb.AppendLine("StrictNodes 1");

            sb.AppendLine("MaxCircuitDirtiness 10");
        }

        if (string.IsNullOrEmpty(socks5Override) && bridges.Count > 0)
        {
            sb.AppendLine("UseBridges 1");

            var cleanLyrebird = ToCleanPath(lyrebird);
            var cleanConjure = ToCleanPath(conjure);
            sb.AppendLine($"ClientTransportPlugin obfs4,meek_lite,webtunnel,scramblesuit,snowflake exec {cleanLyrebird}");
            sb.AppendLine($"ClientTransportPlugin conjure exec {cleanConjure}");
            foreach (var bridge in bridges)
            {
                sb.AppendLine($"Bridge {bridge}");
            }
        }

        return sb.ToString();
    }

    private static string NormalizeExitCountry(string? raw)
    {
        var v = (raw ?? "").Trim().Trim('{', '}').ToLowerInvariant();
        return v is "auto" or "any" ? "" : v;
    }

    private static List<string> ParseBridges(string? raw)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(raw)) return result;

        foreach (var line in raw.Replace("\r\n", "\n").Split('\n'))
        {
            var t = line.Trim();
            if (t.Length == 0 || t.StartsWith("#")) continue;

            if (t.StartsWith("Bridge ", StringComparison.OrdinalIgnoreCase))
                t = t.Substring("Bridge ".Length).Trim();
            if (t.Length > 0) result.Add(t);
        }
        return result;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathName(string lpszLongPath, StringBuilder lpszShortPath, uint cchBuffer);

    private static string ToCleanPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        try
        {
            var sb = new StringBuilder(512);
            var res = GetShortPathName(path, sb, (uint)sb.Capacity);
            if (res > 0 && res < sb.Capacity)
            {
                return sb.ToString();
            }
        }
        catch { }
        return path.Replace('/', '\\');
    }
}
