using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Se7enPro.Models;

namespace Se7enPro.Services.Lantern;

public sealed class LanternEngine : LocalSocksEngineBase
{
    public const int DefaultListenPort = 1850;
    public const int DefaultHttpPort = 1851;

    public static string? UpstreamProxyOverride { get; set; }

    private readonly IChildProcessGuard _childGuard;
    private List<string> _knownEndpointIps = new();
    private string _detectedIp = "";
    private string _detectedCountry = "";

    public LanternEngine(
        ILogger<LanternEngine> logger,
        ISettingsService settings,
        IChildProcessGuard childGuard)
        : base(logger, settings, childGuard)
    {
        _childGuard = childGuard;
        RefreshAvailableRegions();
    }

    public override ConnectionMethod Method => ConnectionMethod.Lantern;

    public override IReadOnlyList<string> CoreProcessNames { get; } =
        new[] { EngineProcessNames.Lantern, "lanternd.exe", "sing-box.exe" };

    protected override string EngineDisplayName => "Lantern";

    protected override string WorkSubdirectory => "lantern";

    protected override TimeSpan ReadyTimeout => TimeSpan.FromSeconds(60);

    public override string ServerEndpointAddress => string.IsNullOrEmpty(UpstreamProxyOverride) ? _detectedIp : "";

    public override IReadOnlyList<string> ServerEndpointAddresses => string.IsNullOrEmpty(UpstreamProxyOverride) ? _knownEndpointIps : Array.Empty<string>();

    public override IReadOnlyList<string> AvailableEgressRegions
    {
        get
        {
            if (_availableRegions.Count == 0)
            {
                RefreshAvailableRegions();
            }
            return base.AvailableEgressRegions;
        }
    }

    public static bool IsNodeFreeForIran(JsonNode? item, string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return false;
        if (item != null && item["ir_free"]?.GetValue<bool>() == true) return true;
        if (item != null && item["free"]?.GetValue<bool>() == true) return true;

        var t = tag.ToLowerInvariant();
        if (t.Contains("ir-free") || t.Contains("alicloud")) return true;
        if (t.Contains("-free-") || t.EndsWith("-free")) return true;
        if (t.Contains("free-vps") || t.Contains("linode-free") || t.Contains("oci-free")) return true;
        return false;
    }

    public void RefreshAvailableRegions()
    {
        try
        {
            var resLantern = Path.Combine(AppDir, "Resources", "lantern");
            var workDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Se7en", WorkSubdirectory);
            try { Directory.CreateDirectory(workDir); } catch { }

            var serversPath = ResolveServersJson(workDir, resLantern);
            if (!File.Exists(serversPath))
                serversPath = Path.Combine(resLantern, "servers-seed.json");

            if (File.Exists(serversPath) && JsonNode.Parse(File.ReadAllText(serversPath)) is JsonArray arr)
            {
                var regions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in arr)
                {
                    var type = item?["type"]?.GetValue<string>() ?? "";
                    if (type is not ("vless" or "hysteria2" or "shadowsocks")) continue;

                    var tag = item?["outbound"]?["tag"]?.GetValue<string>()
                           ?? item?["tag"]?.GetValue<string>() ?? "";
                    if (IsNodeFreeForIran(item, tag))
                    {
                        var cc = ResolveNodeCountryCode(item, tag);
                        if (!string.IsNullOrWhiteSpace(cc)) regions.Add(cc);
                    }
                }
                if (regions.Count > 0)
                {
                    SetAvailableRegions(regions.OrderBy(c => c));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to refresh Lantern available regions");
        }

        if (_availableRegions.Count == 0)
        {
            SetAvailableRegions(new[] { "DE", "GB", "NL", "US" });
        }
    }

    protected override Task BeforeStartAsync(string workDir, CancellationToken ct)
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("lanternd"))
            {
                using (p)
                {
                    try
                    {
                        var file = p.MainModule?.FileName;
                        if (!string.IsNullOrEmpty(file) && StartupReaper.ShouldReap(file))
                        {
                            p.Kill();
                        }
                    }
                    catch { }
                }
            }
        }
        catch { }

        try
        {
            Directory.CreateDirectory(workDir);
            var resLantern = Path.Combine(AppDir, "Resources", "lantern");
            var localServers = Path.Combine(workDir, "servers.json");
            var needRefresh = !File.Exists(localServers);
            if (!needRefresh)
            {
                try
                {
                    var fi = new FileInfo(localServers);
                    needRefresh = (DateTime.UtcNow - fi.LastWriteTimeUtc > TimeSpan.FromHours(2)) || fi.Length < 1024;
                }
                catch
                {
                    needRefresh = true;
                }
            }

            if (needRefresh)
            {
                return TryRefreshServersViaDaemonAsync(workDir, resLantern, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Lantern pre-start server refresh skipped");
        }
        return Task.CompletedTask;
    }

    private async Task TryRefreshServersViaDaemonAsync(string workDir, string resLantern, CancellationToken ct)
    {
        var daemonPath = Path.Combine(resLantern, "lanternd.exe");
        if (!File.Exists(daemonPath)) return;

        Process? proc = null;
        try
        {
            SetConnectProgress(12, Loc.Of("Refreshing Lantern nodes..."));
            var psi = new ProcessStartInfo
            {
                FileName = daemonPath,
                Arguments = $"run --data-path \"{workDir}\" --log-path \"{workDir}\" --log-level warn",
                WorkingDirectory = workDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            if (!string.IsNullOrWhiteSpace(UpstreamProxyOverride))
            {
                psi.EnvironmentVariables["ALL_PROXY"] = UpstreamProxyOverride;
                psi.EnvironmentVariables["HTTP_PROXY"] = UpstreamProxyOverride;
                psi.EnvironmentVariables["HTTPS_PROXY"] = UpstreamProxyOverride;
            }
            proc = Process.Start(psi);
            if (proc == null) return;
            _childGuard.Adopt(proc);

            var localServers = Path.Combine(workDir, "servers.json");
            var startWrite = File.Exists(localServers) ? File.GetLastWriteTimeUtc(localServers) : DateTime.MinValue;
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 4500 && !ct.IsCancellationRequested && !proc.HasExited)
            {
                await Task.Delay(250, ct);
                if (File.Exists(localServers))
                {
                    var fi = new FileInfo(localServers);
                    if (fi.LastWriteTimeUtc > startWrite && fi.Length > 1024)
                    {
                        var content = await File.ReadAllTextAsync(localServers, ct);
                        if (content.Contains("\"outbound\"", StringComparison.Ordinal))
                        {
                            Log("[Lantern] Fetched fresh server list via Lantern daemon.");
                            break;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Lantern daemon server refresh did not complete within budget");
        }
        finally
        {
            if (proc != null)
            {
                try
                {
                    if (!proc.HasExited)
                    {
                        proc.Kill(entireProcessTree: true);
                        await proc.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));
                    }
                }
                catch { }
                proc.Dispose();
            }
        }
    }

    protected override PreparedLaunch Prepare(string workDir, int socksPort, int httpPort)
    {
        Directory.CreateDirectory(workDir);
        IdentityFileGuard.RestrictToCurrentUser(workDir, out _);
        RefreshAvailableRegions();

        var resLantern = Path.Combine(AppDir, "Resources", "lantern");
        var singBoxDir = Path.Combine(AppDir, "Resources", "sing_box");
        var singBoxExe = Path.Combine(singBoxDir, "sing-box.exe");
        if (!File.Exists(singBoxExe))
        {
            throw new FileNotFoundException("Sing-box core binary not found at " + singBoxExe);
        }

        var exePath = StageExecutable(singBoxExe, Path.Combine(workDir, EngineProcessNames.Lantern));
        var serversPath = ResolveServersJson(workDir, resLantern);
        if (!File.Exists(serversPath))
        {
            throw new FileNotFoundException("Lantern servers configuration not found at " + serversPath);
        }

        var (outbounds, detectedIp, detectedCountry) = BuildLanternOutbounds(serversPath, resLantern, UpstreamProxyOverride);
        if (outbounds.Count == 0)
        {
            throw new InvalidOperationException("No valid Lantern outbounds (VLESS, Hysteria 2, Shadowsocks) could be parsed from servers list.");
        }

        _detectedIp = detectedIp;
        _detectedCountry = detectedCountry;
        ConnectedServerRegion = detectedCountry;
        CurrentRouteIp = detectedIp;
        CurrentRouteSni = string.IsNullOrEmpty(detectedCountry)
            ? "Lantern v10 (Auto)"
            : $"Lantern v10 ({detectedCountry})";
        RaiseRouteChanged();

        var listenHost = "127.0.0.1";
        var inbounds = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "socks",
                ["tag"] = "socks-in",
                ["listen"] = listenHost,
                ["listen_port"] = socksPort
            },
            new JsonObject
            {
                ["type"] = "mixed",
                ["tag"] = "mixed-in",
                ["listen"] = listenHost,
                ["listen_port"] = httpPort
            }
        };

        var dnsConfig = new JsonObject
        {
            ["servers"] = new JsonArray
            {
                new JsonObject
                {
                    ["tag"] = "dns_remote",
                    ["type"] = "https",
                    ["server"] = "1.1.1.1",
                    ["server_port"] = 443,
                    ["path"] = "/dns-query",
                    ["detour"] = "auto"
                },
                new JsonObject
                {
                    ["tag"] = "dns_fallback",
                    ["type"] = "https",
                    ["server"] = "8.8.8.8",
                    ["server_port"] = 443,
                    ["path"] = "/dns-query",
                    ["detour"] = "auto"
                }
            },
            ["final"] = "dns_remote",
            ["strategy"] = "ipv4_only"
        };

        var routeRules = new JsonArray
        {
            new JsonObject { ["action"] = "sniff" },
            new JsonObject
            {
                ["protocol"] = "dns",
                ["action"] = "hijack-dns"
            },
            new JsonObject
            {
                ["port"] = 53,
                ["action"] = "hijack-dns"
            }
        };

        var routeConfig = new JsonObject
        {
            ["rules"] = routeRules,
            ["final"] = "auto",
            ["default_domain_resolver"] = "dns_remote",
            ["auto_detect_interface"] = true
        };

        var rootConfig = new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = "info" },
            ["dns"] = dnsConfig,
            ["inbounds"] = inbounds,
            ["outbounds"] = outbounds,
            ["route"] = routeConfig
        };

        var runConfigPath = Path.Combine(workDir, "lantern_run.json");
        File.WriteAllText(runConfigPath, rootConfig.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
        }));

        var selectedRegion = (!string.IsNullOrWhiteSpace(_settings.Settings.LanternRegion)
            ? _settings.Settings.LanternRegion
            : _settings.Settings.EgressRegion)?.Trim().ToUpperInvariant();
        var regionLabel = (!string.IsNullOrEmpty(selectedRegion) && selectedRegion != "AUTO")
            ? $" [Region: {selectedRegion}]"
            : " [Auto Region]";
        var chainLabel = !string.IsNullOrEmpty(UpstreamProxyOverride)
            ? $" (Chained via outer SOCKS: {UpstreamProxyOverride})"
            : "";

        Log($"Launching Lantern core ({outbounds.Count - (string.IsNullOrEmpty(UpstreamProxyOverride) ? 1 : 2)} nodes loaded){regionLabel}{chainLabel}...");
        SetConnectProgress(25, Loc.Of("Connecting to Lantern network..."));

        return new PreparedLaunch(
            exePath,
            new[] { "run", "-c", runConfigPath },
            workDir,
            HttpProxyPort: httpPort,
            SocksPortOverride: socksPort);
    }

    private string ResolveServersJson(string workDir, string resLantern)
    {
        try { Directory.CreateDirectory(workDir); } catch { }
        var workServers = Path.Combine(workDir, "servers.json");
        var seedServers = Path.Combine(resLantern, "servers-seed.json");
        var programDataServers = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Lantern", "servers.json");
        var appDataServers = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lantern", "servers.json");
        var localAppDataServers = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lantern", "servers.json");

        var candidates = new[] { workServers, programDataServers, appDataServers, localAppDataServers, seedServers }
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var merged = new JsonArray();
        var seenEndpoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in candidates)
        {
            try
            {
                var text = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(text) || !text.Contains("\"outbound\"", StringComparison.Ordinal))
                    continue;

                if (JsonNode.Parse(text) is not JsonArray arr) continue;

                foreach (var item in arr)
                {
                    if (item?["outbound"] is JsonObject obj)
                    {
                        var server = obj["server"]?.GetValue<string>() ?? "";
                        var port = obj["server_port"]?.ToString() ?? "";
                        var tag = obj["tag"]?.GetValue<string>() ?? "";
                        var key = !string.IsNullOrEmpty(server) ? $"{server}:{port}" : tag;
                        if (!string.IsNullOrEmpty(key) && seenEndpoints.Add(key))
                        {
                            merged.Add(JsonNode.Parse(item.ToJsonString()));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not parse Lantern server list at {Path}", path);
            }
        }

        if (merged.Count > 0)
        {
            try
            {
                File.WriteAllText(workServers, merged.ToJsonString());
                return workServers;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not write merged Lantern servers to workDir");
            }
        }

        foreach (var c in candidates)
        {
            if (File.Exists(c)) return c;
        }

        return workServers;
    }

    private static string ResolveNodeCountryCode(JsonNode? item, string tag)
    {
        var cc = item?["location"]?["country_code"]?.GetValue<string>()?.Trim().ToUpperInvariant();
        if (!string.IsNullOrWhiteSpace(cc) && cc.Length == 2) return cc;

        if (string.IsNullOrWhiteSpace(tag)) return "";
        var parts = tag.ToLowerInvariant().Split('-', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            switch (part)
            {
                case "lon": case "lhr": case "lgw": case "man": case "cardiff": case "london": case "uk": case "gb":
                    return "GB";
                case "fra": case "ber": case "muc": case "dus": case "frankfurt": case "germany": case "de":
                    return "DE";
                case "ams": case "rtm": case "amsterdam": case "netherlands": case "nl": case "vulpix":
                    return "NL";
                case "lax": case "sjc": case "sfo": case "sea": case "ord": case "iad": case "ewr": case "jfk":
                case "mia": case "dfw": case "atl": case "phx": case "den": case "us": case "usa":
                    return "US";
                case "par": case "cdg": case "mrs": case "marseille": case "france": case "fr":
                    return "FR";
                case "sto": case "arn": case "stockholm": case "sweden": case "se":
                    return "SE";
                case "sin": case "sgp": case "singapore": case "sg":
                    return "SG";
                case "tyo": case "nrt": case "hnd": case "kix": case "tokyo": case "japan": case "jp":
                    return "JP";
                case "yyz": case "yvr": case "ymq": case "toronto": case "canada": case "ca":
                    return "CA";
                case "gru": case "gig": case "brazil": case "br":
                    return "BR";
                case "bom": case "del": case "maa": case "blr": case "mumbai": case "india": case "in":
                    return "IN";
                case "mil": case "mxp": case "fco": case "milan": case "italy": case "it":
                    return "IT";
                case "icn": case "sel": case "korea": case "kr":
                    return "KR";
                case "ala": case "nqz": case "kazakhstan": case "kz":
                    return "KZ";
                case "mex": case "mty": case "mexico": case "mx":
                    return "MX";
                case "tlv": case "jrs": case "israel": case "il":
                    return "IL";
                case "scl": case "chile": case "cl":
                    return "CL";
            }
        }
        return "";
    }

    private (JsonArray Outbounds, string DetectedIp, string DetectedCountry) BuildLanternOutbounds(
        string serversPath, string resLantern, string? upstreamProxy)
    {
        var node = JsonNode.Parse(File.ReadAllText(serversPath));
        var outbounds = new JsonArray();
        var candidateTags = new List<string>();
        var detectedIp = "";
        var detectedCountry = "";
        string? detourTag = null;

        if (!string.IsNullOrWhiteSpace(upstreamProxy))
        {
            var cleanProxy = upstreamProxy.Trim();
            if (cleanProxy.StartsWith("socks5://", StringComparison.OrdinalIgnoreCase))
                cleanProxy = cleanProxy.Substring("socks5://".Length);
            else if (cleanProxy.StartsWith("socks://", StringComparison.OrdinalIgnoreCase))
                cleanProxy = cleanProxy.Substring("socks://".Length);

            var parts = cleanProxy.Split(':');
            var host = parts[0];
            var port = parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : 1080;
            detourTag = "upstream-chain";
            outbounds.Add(new JsonObject
            {
                ["type"] = "socks",
                ["tag"] = detourTag,
                ["server"] = host,
                ["server_port"] = port
            });
        }

        var selectedRegion = (!string.IsNullOrWhiteSpace(_settings.Settings.LanternRegion)
            ? _settings.Settings.LanternRegion
            : _settings.Settings.EgressRegion)?.Trim().ToUpperInvariant();
        if (selectedRegion is not ("DE" or "NL"))
        {
            selectedRegion = "AUTO";
        }
        var autoRegion = string.IsNullOrEmpty(selectedRegion) || selectedRegion == "AUTO";

        var allCandidates = new List<(JsonObject Outbound, string Tag, string Country, string Ip, bool IsIrOptimized, int DelayMs)>();
        var seenTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        CollectFromJsonArray(node as JsonArray);

        var seedPath = Path.Combine(resLantern, "servers-seed.json");
        if (File.Exists(seedPath) && !string.Equals(serversPath, seedPath, StringComparison.OrdinalIgnoreCase))
        {
            try { CollectFromJsonArray(JsonNode.Parse(File.ReadAllText(seedPath)) as JsonArray); } catch { }
        }

        allCandidates = allCandidates.OrderBy(c =>
        {
            var type = c.Outbound["type"]?.GetValue<string>() ?? "";
            return type switch
            {
                "hysteria2" => 0,
                "vless" => 1,
                "shadowsocks" => 2,
                _ => 3
            };
        }).ThenBy(c =>
        {
            return c.IsIrOptimized ? 0 : 1;
        }).ThenBy(c =>
        {
            return c.DelayMs < int.MaxValue ? c.DelayMs : 999999;
        }).ToList();

        var chosen = SelectLanternCandidates(
            allCandidates,
            selectedRegion,
            autoRegion,
            string.IsNullOrWhiteSpace(upstreamProxy)
                ? (ip, port) => ProbeTcpEndpointMsAsync(ip, port, timeoutMs: 2000)
                : null,
            Log);

        CaptureKnownEndpoints(chosen.Select(c => (c.Outbound, c.Tag, c.Country, c.Ip, c.IsIrOptimized)).ToList());

        foreach (var (outboundObj, tag, country, ip, _, _) in chosen)
        {
            outbounds.Add(outboundObj);
            candidateTags.Add(tag);
            if (string.IsNullOrEmpty(detectedIp) && !autoRegion)
            {
                detectedIp = ip;
                detectedCountry = (!string.IsNullOrEmpty(selectedRegion)) ? selectedRegion : country;
            }
        }

        if (candidateTags.Count > 0)
        {
            var urlTest = new JsonObject
            {
                ["type"] = "urltest",
                ["tag"] = "auto",
                ["outbounds"] = new JsonArray(candidateTags.Select(t => (JsonNode)JsonValue.Create(t)!).ToArray()),
                ["url"] = "https://www.gstatic.com/generate_204",
                ["interval"] = "30s",
                ["idle_timeout"] = "10m",
                ["tolerance"] = 250,
                ["interrupt_exist_connections"] = false
            };
            outbounds.Insert(0, urlTest);
        }

        return (Outbounds: outbounds, DetectedIp: detectedIp, DetectedCountry: detectedCountry);

        void CollectFromJsonArray(JsonArray? arr)
        {
            if (arr == null) return;
            foreach (var item in arr)
            {
                if (item?["outbound"] is JsonObject obj)
                {
                    var type = item["type"]?.GetValue<string>() ?? "";
                    if (type is not ("vless" or "hysteria2" or "shadowsocks")) continue;

                    var tag = obj["tag"]?.GetValue<string>() ?? "";
                    if (!string.IsNullOrEmpty(tag) && seenTags.Add(tag))
                    {
                        var clone = JsonNode.Parse(obj.ToJsonString())!.AsObject();
                        clone["connect_timeout"] = "5s";
                        var server = clone["server"]?.GetValue<string>() ?? "";
                        if (!string.IsNullOrWhiteSpace(server) && !server.Contains(':'))
                        {
                            if (!string.IsNullOrEmpty(detourTag))
                            {
                                clone["detour"] = detourTag;
                            }
                            var country = ResolveNodeCountryCode(item, tag);
                            var isIr = IsNodeFreeForIran(item, tag);
                            int delayMs = item?["selection_history"]?["last_success_delay_ms"]?.GetValue<int>() ?? int.MaxValue;
                            if (delayMs <= 0) delayMs = int.MaxValue;
                            allCandidates.Add((clone, tag, country, server, isIr, delayMs));
                        }
                    }
                }
            }
        }
    }

    private void CaptureKnownEndpoints(List<(JsonObject Outbound, string Tag, string Country, string Ip, bool IsIrOptimized)> chosen)
    {
        _knownEndpointIps = CollectEndpointIps(chosen.Select(c => c.Ip));
        if (_knownEndpointIps.Count > 0)
        {
            Log($"Lantern: {_knownEndpointIps.Count} node endpoint IP(s) will be pinned direct before tunnel routes apply.");
        }
    }

    private static List<string> CollectEndpointIps(IEnumerable<string> raw)
    {
        var list = new List<string>();
        foreach (var r in raw)
        {
            if (string.IsNullOrWhiteSpace(r)) continue;
            var host = r.Trim();
            var colon = host.LastIndexOf(':');
            if (colon > 0 && !host.Contains(']')) host = host.Substring(0, colon);
            if (IPAddress.TryParse(host, out var ip) && !list.Contains(ip.ToString()))
            {
                list.Add(ip.ToString());
            }
        }
        return list;
    }

    private const int MaxUrlTestOutbounds = 14;
    private static readonly string[] PreferredEuropeanRegions =
        ["DE", "NL", "GB", "FR", "SE", "IT", "FI", "PL", "CH", "AT"];

    internal static async Task<int> ProbeTcpEndpointMsAsync(string host, int port, int timeoutMs = 2000)
    {
        if (string.IsNullOrWhiteSpace(host) || port <= 0) return int.MaxValue;
        try
        {
            using var tcp = new System.Net.Sockets.TcpClient();
            using var cts = new CancellationTokenSource(timeoutMs);
            var sw = Stopwatch.StartNew();
            await tcp.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            sw.Stop();
            return (int)Math.Max(1, sw.ElapsedMilliseconds);
        }
        catch
        {
            return int.MaxValue;
        }
    }

    internal static List<(JsonObject Outbound, string Tag, string Country, string Ip, bool IsIrOptimized, int DelayMs)>
        SelectLanternCandidates(
            List<(JsonObject Outbound, string Tag, string Country, string Ip, bool IsIrOptimized, int DelayMs)> allCandidates,
            string selectedRegion,
            bool autoRegion,
            Func<string, int, Task<int>>? tcpProbeAsync,
            Action<string>? log = null)
    {
        if (allCandidates.Count == 0) return allCandidates;

        var rttByTag = new Dictionary<string, int>(StringComparer.Ordinal);
        if (tcpProbeAsync is not null)
        {
            var probeTasks = allCandidates.Select(async c =>
            {
                var type = c.Outbound["type"]?.GetValue<string>() ?? "";
                if (string.Equals(type, "hysteria2", StringComparison.OrdinalIgnoreCase))
                    return (c.Tag, Rtt: int.MaxValue - 1);

                var port = c.Outbound["server_port"]?.GetValue<int>() ?? 0;
                var rtt = await tcpProbeAsync(c.Ip, port).ConfigureAwait(false);
                return (c.Tag, Rtt: rtt);
            });

            foreach (var (tag, rtt) in Task.WhenAll(probeTasks).GetAwaiter().GetResult())
                rttByTag[tag] = rtt;
        }

        int ScoreTier((JsonObject Outbound, string Tag, string Country, string Ip, bool IsIrOptimized, int DelayMs) c)
        {
            if (!rttByTag.TryGetValue(c.Tag, out var rtt)) return 0;
            if (rtt < int.MaxValue - 1) return 0;
            if (rtt == int.MaxValue - 1) return 1;
            return 2;
        }

        int ScoreMs((JsonObject Outbound, string Tag, string Country, string Ip, bool IsIrOptimized, int DelayMs) c)
        {
            if (rttByTag.TryGetValue(c.Tag, out var rtt) && rtt < int.MaxValue - 1) return rtt;
            return c.DelayMs;
        }

        int RegionPriority(string country)
        {
            for (var i = 0; i < PreferredEuropeanRegions.Length; i++)
            {
                if (string.Equals(country, PreferredEuropeanRegions[i], StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return PreferredEuropeanRegions.Length;
        }

        List<(JsonObject Outbound, string Tag, string Country, string Ip, bool IsIrOptimized, int DelayMs)> RankPool(
            IEnumerable<(JsonObject Outbound, string Tag, string Country, string Ip, bool IsIrOptimized, int DelayMs)> pool,
            bool preferReachableOnly)
        {
            var list = pool.ToList();
            if (preferReachableOnly && tcpProbeAsync is not null)
            {
                var reachable = list.Where(c => ScoreTier(c) < 2).ToList();
                if (reachable.Count > 0) list = reachable;
            }

            return list
                .OrderBy(ScoreTier)
                .ThenBy(c => RegionPriority(c.Country))
                .ThenBy(c => !c.IsIrOptimized)
                .ThenBy(ScoreMs)
                .ThenBy(c => c.DelayMs)
                .Take(MaxUrlTestOutbounds)
                .ToList();
        }

        if (!autoRegion && !string.IsNullOrWhiteSpace(selectedRegion))
        {
            var regionCandidates = allCandidates
                .Where(c => string.Equals(c.Country, selectedRegion, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (tcpProbeAsync is not null)
            {
                var reachableInRegion = regionCandidates.Where(c => ScoreTier(c) < 2).ToList();
                if (reachableInRegion.Count > 0)
                    return RankPool(reachableInRegion, preferReachableOnly: false);

                var globalHealthy = RankPool(allCandidates, preferReachableOnly: true);
                if (globalHealthy.Any(c => ScoreTier(c) < 2))
                {
                    var normRegion = selectedRegion.Trim().ToUpperInvariant();
                    log?.Invoke(regionCandidates.Count > 0
                        ? $"Region '{normRegion}' has no reachable sing-box nodes ({regionCandidates.Count} offline); falling back to nearest reachable servers."
                        : $"Region '{normRegion}' has no sing-box-compatible nodes; falling back to nearest reachable servers.");
                    return globalHealthy;
                }
            }

            if (regionCandidates.Count > 0)
                return RankPool(regionCandidates, preferReachableOnly: false);
        }

        return RankPool(allCandidates, preferReachableOnly: true);
    }

    protected override void Log(string line) => base.Log(LogTags.Tag(LogTags.Lantern, line));
}
