using System;
using System.Collections.Generic;
using System.IO;
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

        var torrcPath = Path.Combine(workDir, "torrc");
        File.WriteAllText(torrcPath,
            BuildTorrc(socksPort, httpPort, torData, geoip, geoip6, lyrebird, conjure));

        var exit = NormalizeExitCountry(_settings.Settings.TorExitCountry);
        ConnectedServerRegion = exit.Length == 2 ? exit.ToUpperInvariant() : "";
        CurrentRouteIp = "";
        _bootstrapText = "Bootstrapping…";
        CurrentRouteSni = _bootstrapText;
        RaiseRouteChanged();

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

    internal static string? Socks5ProxyOverride;

    private string BuildTorrc(
        int socksPort, int httpPort, string torData,
        string geoip, string geoip6, string lyrebird, string conjure)
    {
        var s = _settings.Settings;
        var sb = new StringBuilder();

        sb.AppendLine($"SocksPort 127.0.0.1:{socksPort}");
        var bridges = ParseBridges(s.TorBridges);
        if (!string.IsNullOrEmpty(Socks5ProxyOverride))
        {
            if (bridges.Count == 0)
            {
                sb.AppendLine($"Socks5Proxy {Socks5ProxyOverride}");
            }
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

        if (bridges.Count > 0)
        {
            sb.AppendLine("UseBridges 1");

            sb.AppendLine($"ClientTransportPlugin obfs4,meek_lite,webtunnel,scramblesuit,snowflake exec {P(lyrebird)}");
            sb.AppendLine($"ClientTransportPlugin conjure exec {P(conjure)}");
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

    private static string P(string path) => path.Replace('\\', '/');
}
