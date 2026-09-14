using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Se7enPro.Models;

namespace Se7enPro.Services;

public sealed record ShardNode(
    string Protocol,
    string Credential,
    string Address,
    int Port,
    string Network,
    string Security,
    string Path,
    string Host,
    string ServerName,
    string Fingerprint,
    string CipherSuites,
    string FinalMask,
    string Alpn,
    string Label)
{
    public string Key => $"{Protocol}|{Credential}|{Address}|{Port}|{Network}|{Security}|{Path}|{Host}";
    public string DisplayName => $"{Address}:{Port}";
}

public sealed record ShardPoolInfo(int NodeCount, int PathCount, DateTime LastCheckUtc, bool Success);

public sealed class ShardEngine : LocalSocksEngineBase
{
    public const int DefaultListenPort = 1824;
    private const int ProbeBasePort = 21100;
    private const int RaceWidth = 12;
    private const int MaxRaceSlices = 3;
    private const int ProbeTimeoutMs = 2500;

    private const string SubscriptionUrl = "https://raw.githubusercontent.com/patterniha/Free-Configs/main/configs.txt";
    private const string PolicyUrl = "https://raw.githubusercontent.com/mbm110/MSN-GUARD/master/remote/policy.json";

    private static readonly string[] DefaultEdges = new[]
    {
        "104.21.70.21",
        "104.21.33.59",
        "188.114.97.0",
        "188.114.97.6",
        "172.67.141.182",
        "172.67.217.240",
        "104.16.132.229",
        "104.16.133.229",
        "188.114.96.1",
        "172.64.155.209"
    };

    private static readonly HashSet<int> CdnPorts = new()
    {
        80, 8080, 8880, 2052, 2082, 2086, 2095,
        443, 2053, 2083, 2087, 2096, 8443
    };

    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private DateTime _lastCheckUtc = DateTime.MinValue;
    private int _cachedNodeCount = 45;
    private int _cachedPathCount = 270;
    private int _rotationOffset = 0;

    public void AdvanceRotationCursor(int step = 1)
    {
        _rotationOffset += step;
    }

    public Task RotateNodeAsync()
    {
        AdvanceRotationCursor();
        _logger.LogInformation("Rotating SHARD candidate cursor (new offset: {Offset})", _rotationOffset);
        if (State == ConnectionState.Connected || State == ConnectionState.Connecting)
        {
            RestartCore("User rotated node / requested new exit IP", resetFailureCount: true);
        }
        return Task.CompletedTask;
    }

    public ShardEngine(
        ILogger<ShardEngine> logger,
        ISettingsService settings,
        IChildProcessGuard childGuard)
        : base(logger, settings, childGuard)
    {
        _ = Task.Run(() => InitializePoolStatsAsync());
    }

    public override ConnectionMethod Method => ConnectionMethod.Shard;

    public override IReadOnlyList<string> CoreProcessNames { get; } = new[]
    {
        EngineProcessNames.Shard,
        "xray.exe"
    };

    protected override string EngineDisplayName => "SHARD";

    protected override string WorkSubdirectory => "shard";

    protected override TimeSpan ReadyTimeout => TimeSpan.FromSeconds(35);

    protected override IReadOnlyList<int> ReservedEnginePorts => new[] { DefaultListenPort };

    protected override bool ShouldSuppressCoreLogLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return true;
        if (line.Contains("The feature WebSocket transport", StringComparison.OrdinalIgnoreCase) &&
            line.Contains("is deprecated", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (line.Contains("proxy/http: failed to read http request", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (line.Contains("invalid port", StringComparison.OrdinalIgnoreCase) &&
            line.Contains("after host", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (line.Contains("app/proxyman/inbound: connection ends", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (line.Contains("infra/conf/serial: Reading config", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return false;
    }

    private string LocalCacheDir
    {
        get
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var dir = System.IO.Path.Combine(localAppData, "Se7en");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private string ConfigCachePath => System.IO.Path.Combine(LocalCacheDir, "shard-configs.txt");
    private string PolicyCachePath => System.IO.Path.Combine(LocalCacheDir, "shard-policy.json");

    private string SeedPath => System.IO.Path.Combine(AppDir, "Resources", "shard", "shard-seed.txt");
    private string DefaultPolicyPath => System.IO.Path.Combine(AppDir, "Resources", "shard", "policy.json");

    public ShardPoolInfo GetPoolSummary()
    {
        var custom = _settings.Settings.ShardCustomCfIp?.Trim() ?? "";
        var edgeCount = !string.IsNullOrEmpty(custom) ? 1 : Math.Max(1, GetEdgeIps().Count);
        var pathCount = _cachedNodeCount * edgeCount;
        return new ShardPoolInfo(_cachedNodeCount, pathCount, _lastCheckUtc, true);
    }

    private async Task InitializePoolStatsAsync()
    {
        try
        {
            var nodes = LoadNodesFromCacheOrSeed();
            _cachedNodeCount = nodes.Count > 0 ? nodes.Count : 45;
            var custom = _settings.Settings.ShardCustomCfIp?.Trim() ?? "";
            var edgeCount = !string.IsNullOrEmpty(custom) ? 1 : Math.Max(1, GetEdgeIps().Count);
            _cachedPathCount = _cachedNodeCount * edgeCount;

            if (File.Exists(ConfigCachePath))
            {
                _lastCheckUtc = File.GetLastWriteTimeUtc(ConfigCachePath);
            }

            var needsSync = !File.Exists(ConfigCachePath) || (DateTime.UtcNow - _lastCheckUtc).TotalHours >= 6;
            if (needsSync)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(2500);
                        _logger.LogInformation("Background auto-syncing SHARD pool from cloud...");
                        await RefreshSubscriptionAsync(force: false);
                    }
                    catch (Exception syncEx)
                    {
                        _logger.LogWarning(syncEx, "Background SHARD auto-sync failed on startup.");
                    }
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to initialize SHARD pool stats");
        }
    }

    public async Task<ShardPoolInfo> RefreshSubscriptionAsync(bool force = false)
    {
        await _refreshLock.WaitAsync();
        try
        {
            var now = DateTime.UtcNow;
            if (!force && (now - _lastCheckUtc).TotalHours < 6 && File.Exists(ConfigCachePath))
            {
                return GetPoolSummary();
            }

            _logger.LogInformation("Refreshing SHARD subscription from GitHub...");
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 Se7enPro/1.0");

            var success = false;

            try
            {
                var policyJson = await client.GetStringAsync(PolicyUrl);
                if (!string.IsNullOrWhiteSpace(policyJson))
                {
                    await File.WriteAllTextAsync(PolicyCachePath, policyJson);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not refresh SHARD remote policy; keeping existing.");
            }

            try
            {
                var configsText = await client.GetStringAsync(SubscriptionUrl);
                if (!string.IsNullOrWhiteSpace(configsText))
                {
                    var parsed = ParseNodes(configsText);
                    if (parsed.Count > 0)
                    {
                        await File.WriteAllTextAsync(ConfigCachePath, configsText);
                        _cachedNodeCount = parsed.Count;
                        _lastCheckUtc = DateTime.UtcNow;
                        success = true;
                    }
                    else
                    {
                        _logger.LogWarning("Remote SHARD subscription returned 0 parsable nodes; preserving existing pool.");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not refresh SHARD subscription nodes; keeping existing.");
            }

            var liveNodes = LoadNodesFromCacheOrSeed();
            if (liveNodes.Count > 0) _cachedNodeCount = liveNodes.Count;
            else if (_cachedNodeCount <= 0) _cachedNodeCount = 45;

            var custom = _settings.Settings.ShardCustomCfIp?.Trim() ?? "";
            var edgeCount = !string.IsNullOrEmpty(custom) ? 1 : Math.Max(1, GetEdgeIps().Count);
            _cachedPathCount = _cachedNodeCount * edgeCount;
            return new ShardPoolInfo(_cachedNodeCount, _cachedPathCount, _lastCheckUtc, success);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private List<string> GetEdgeIps()
    {
        try
        {
            var path = File.Exists(PolicyCachePath) ? PolicyCachePath : DefaultPolicyPath;
            if (File.Exists(path))
            {
                var content = File.ReadAllText(path);
                using var doc = JsonDocument.Parse(content);
                if (doc.RootElement.TryGetProperty("edges", out var edgesProp) && edgesProp.ValueKind == JsonValueKind.Array)
                {
                    var list = new List<string>();
                    foreach (var item in edgesProp.EnumerateArray())
                    {
                        var ip = item.GetString()?.Trim();
                        if (!string.IsNullOrEmpty(ip) && IPAddress.TryParse(ip, out _))
                        {
                            list.Add(ip);
                        }
                    }
                    if (list.Count > 0) return list;
                }
            }
        }
        catch { }

        return DefaultEdges.ToList();
    }

    private List<ShardNode> LoadNodesFromCacheOrSeed()
    {
        if (File.Exists(ConfigCachePath))
        {
            try
            {
                var content = File.ReadAllText(ConfigCachePath);
                if (!string.IsNullOrWhiteSpace(content))
                {
                    var parsed = ParseNodes(content);
                    if (parsed.Count > 0) return parsed;
                }
            }
            catch { }
        }

        if (File.Exists(SeedPath))
        {
            try
            {
                var content = File.ReadAllText(SeedPath);
                if (!string.IsNullOrWhiteSpace(content))
                {
                    var parsed = ParseNodes(content);
                    if (parsed.Count > 0) return parsed;
                }
            }
            catch { }
        }

        return new List<ShardNode>();
    }

    private List<ShardNode> ParseNodes(string text)
    {
        var result = new List<ShardNode>();
        var seen = new HashSet<string>();

        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            line = line.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith("#")) continue;

            var node = ParseOneNode(line);
            if (node != null && seen.Add(node.Key))
            {
                result.Add(node);
            }
        }

        return result;
    }

    private ShardNode? ParseOneNode(string line)
    {
        try
        {
            var colonSlash = line.IndexOf("://", StringComparison.Ordinal);
            if (colonSlash <= 0) return null;

            var scheme = line[..colonSlash].ToLowerInvariant();
            if (scheme != "vless" && scheme != "trojan") return null;

            var rest = line[(colonSlash + 3)..];
            var hashIdx = rest.IndexOf('#');
            var label = hashIdx >= 0 ? Uri.UnescapeDataString(rest[(hashIdx + 1)..]) : "";
            var withoutLabel = hashIdx >= 0 ? rest[..hashIdx] : rest;

            var atIdx = withoutLabel.IndexOf('@');
            if (atIdx <= 0) return null;

            var credential = Uri.UnescapeDataString(withoutLabel[..atIdx]);
            var hostPortAndQuery = withoutLabel[(atIdx + 1)..];

            var qIdx = hostPortAndQuery.IndexOf('?');
            var hostPort = qIdx >= 0 ? hostPortAndQuery[..qIdx] : hostPortAndQuery;
            var query = qIdx >= 0 ? hostPortAndQuery[(qIdx + 1)..] : "";

            var lastColon = hostPort.LastIndexOf(':');
            if (lastColon <= 0) return null;

            var address = hostPort[..lastColon];
            if (!int.TryParse(hostPort[(lastColon + 1)..], out var port) || port is < 1 or > 65535) return null;

            var queryParams = ParseQuery(query);
            var host = queryParams.GetValueOrDefault("host", "");
            var sni = queryParams.GetValueOrDefault("sni", "");
            var security = queryParams.GetValueOrDefault("security", "none").ToLowerInvariant();
            var network = queryParams.GetValueOrDefault("type", "tcp").ToLowerInvariant();
            if (network != "ws") return null;

            var path = queryParams.GetValueOrDefault("path", "/");
            if (string.IsNullOrEmpty(path)) path = "/";

            var serverName = !string.IsNullOrEmpty(sni) ? sni : host;
            var fp = queryParams.GetValueOrDefault("fp", "");
            var cs = queryParams.GetValueOrDefault("cs", "");
            var fm = queryParams.GetValueOrDefault("fm", "");
            var alpn = queryParams.GetValueOrDefault("alpn", "");

            return new ShardNode(
                Protocol: scheme,
                Credential: credential,
                Address: address,
                Port: port,
                Network: network,
                Security: security,
                Path: path,
                Host: host,
                ServerName: serverName,
                Fingerprint: fp,
                CipherSuites: cs,
                FinalMask: fm,
                Alpn: alpn,
                Label: label
            );
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(query)) return dict;

        var pairs = query.Split('&');
        foreach (var pair in pairs)
        {
            if (string.IsNullOrEmpty(pair)) continue;
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;

            var key = pair[..eq].Trim();
            var val = Uri.UnescapeDataString(pair[(eq + 1)..]);
            dict[key] = val;
        }

        return dict;
    }

    private List<ShardNode> ExpandNodes(List<ShardNode> nodes)
    {
        var customIp = _settings.Settings.ShardCustomCfIp?.Trim() ?? "";
        if (!string.IsNullOrEmpty(customIp) && !customIp.Contains(' ') && !customIp.Contains('/'))
        {
            _logger.LogInformation("Applying user custom Cloudflare IP/host to all SHARD configs: {CustomIp}", customIp);
            return nodes.Select(n => n with { Address = customIp }).ToList();
        }

        var edges = GetEdgeIps();
        if (edges.Count > 1 && _rotationOffset > 0)
        {
            var shift = _rotationOffset % edges.Count;
            edges = edges.Skip(shift).Concat(edges.Take(shift)).ToList();
        }

        var expanded = new List<ShardNode>();
        foreach (var node in nodes)
        {
            if (string.IsNullOrEmpty(node.Host) || !CdnPorts.Contains(node.Port))
            {
                expanded.Add(node);
                continue;
            }

            foreach (var edge in edges)
            {
                expanded.Add(node with { Address = edge });
            }
        }

        return expanded;
    }

    protected override PreparedLaunch Prepare(string workDir, int socksPort, int httpPort)
    {
        var resShard = System.IO.Path.Combine(AppDir, "Resources", "shard");
        var sourceExe = System.IO.Path.Combine(resShard, "xray.exe");
        if (!File.Exists(sourceExe))
        {
            sourceExe = System.IO.Path.Combine(AppDir, "Resources", "xray", "xray.exe");
        }

        var exePath = StageFile(sourceExe, System.IO.Path.Combine(workDir, EngineProcessNames.Shard));

        if (!File.Exists(ConfigCachePath))
        {
            Log("[SHARD] First-run detected — auto-fetching node pool from cloud...");
            SetConnectProgress(5, "Fetching latest SHARD nodes...");
            try { RefreshSubscriptionAsync(force: true).GetAwaiter().GetResult(); }
            catch (Exception ex) { _logger.LogWarning(ex, "SHARD auto-refresh failed; falling back to built-in seed."); }
        }

        var rawNodes = LoadNodesFromCacheOrSeed();
        if (rawNodes.Count == 0)
        {
            throw new InvalidOperationException("No SHARD nodes available in cache or seed list.");
        }

        var candidates = ExpandNodes(rawNodes);

        Log($"[SHARD] Loaded {candidates.Count} expanded edge routes; racing fastest candidate...");
        SetConnectProgress(10, "Discovering SHARD edge candidates...");

        var winner = RaceCandidates(exePath, workDir, candidates);
        _logger.LogInformation("Selected winning SHARD node: {Proto} to {Address}:{Port} (Host: {Host})",
            winner.Protocol, winner.Address, winner.Port, winner.Host);
        Log($"[SHARD] Selected fastest edge: {winner.Address}:{winner.Port} ({winner.Protocol.ToUpperInvariant()}) via {winner.Host}");
        SetConnectProgress(35, "Configuring SHARD live tunnel...");

        CurrentRouteIp = winner.Address;
        CurrentRouteSni = !string.IsNullOrEmpty(winner.ServerName) ? winner.ServerName : winner.Host;

        var configJson = BuildLiveConfig(winner, socksPort, httpPort);
        var configPath = System.IO.Path.Combine(workDir, "config.json");
        File.WriteAllText(configPath, configJson, new UTF8Encoding(false));

        var env = new Dictionary<string, string>();
        var xrayAssetDir = System.IO.Path.Combine(AppDir, "Resources", "xray");
        if (Directory.Exists(xrayAssetDir)) env["XRAY_LOCATION_ASSET"] = xrayAssetDir;

        return new PreparedLaunch(
            exePath,
            new[] { "run", "-c", configPath },
            workDir,
            HttpProxyPort: httpPort,
            CoreTargetSocksPort: socksPort,
            EnvironmentVariables: env
        );
    }

    private static List<ShardNode> Diversify(List<ShardNode> nodes)
    {
        var byEdge = new Dictionary<string, List<ShardNode>>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes)
        {
            if (!byEdge.TryGetValue(node.Address, out var list))
            {
                list = new List<ShardNode>();
                byEdge[node.Address] = list;
            }
            list.Add(node);
        }

        var outList = new List<ShardNode>(nodes.Count);
        var maxPerEdge = byEdge.Values.Count > 0 ? byEdge.Values.Max(v => v.Count) : 0;
        for (var i = 0; i < maxPerEdge; i++)
        {
            foreach (var list in byEdge.Values)
            {
                if (i < list.Count)
                {
                    outList.Add(list[i]);
                }
            }
        }
        return outList;
    }

    private ShardNode RaceCandidates(string exePath, string workDir, List<ShardNode> candidates)
    {
        var diversified = Diversify(candidates);
        if (diversified.Count == 0) return candidates[0];

        var offset = _settings.Settings.ShardRotateIp ? (_rotationOffset % diversified.Count) : 0;
        var rotated = new List<ShardNode>(diversified.Count);
        rotated.AddRange(diversified.Skip(offset));
        rotated.AddRange(diversified.Take(offset));

        var previousIp = CurrentRouteIp;
        if (!string.IsNullOrEmpty(previousIp) && _rotationOffset > 0)
        {
            var diffIp = rotated.Where(c => !string.Equals(c.Address, previousIp, StringComparison.OrdinalIgnoreCase)).ToList();
            var sameIp = rotated.Where(c => string.Equals(c.Address, previousIp, StringComparison.OrdinalIgnoreCase)).ToList();
            rotated = diffIp.Concat(sameIp).ToList();
        }

        for (var sliceIndex = 0; sliceIndex < MaxRaceSlices; sliceIndex++)
        {
            var slice = rotated.Skip(sliceIndex * RaceWidth).Take(RaceWidth).ToList();
            if (slice.Count == 0) break;

            Log($"[SHARD] Benchmarking {slice.Count} candidate nodes (batch {sliceIndex + 1}/{MaxRaceSlices})...");
            SetConnectProgress(15 + (sliceIndex * 8), $"Benchmarking SHARD edge nodes (batch {sliceIndex + 1})...");

            var winner = RaceSlice(exePath, workDir, slice, sliceIndex);
            if (winner != null)
            {
                return winner;
            }
        }

        _logger.LogWarning("No candidate answered probe across {Slices} slices; falling back to first candidate.", MaxRaceSlices);
        return rotated[0];
    }

    private ShardNode? RaceSlice(string exePath, string workDir, List<ShardNode> slice, int sliceIndex)
    {
        if (slice.Count == 1) return slice[0];

        try
        {
            var probeConfigJson = BuildProbeConfig(slice, ProbeBasePort);
            var probeConfigPath = System.IO.Path.Combine(workDir, $"probe_{sliceIndex}.json");
            File.WriteAllText(probeConfigPath, probeConfigJson, new UTF8Encoding(false));

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = $"run -c \"{probeConfigPath}\"",
                WorkingDirectory = workDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            var xrayAssetDir = System.IO.Path.Combine(AppDir, "Resources", "xray");
            if (Directory.Exists(xrayAssetDir)) psi.EnvironmentVariables["XRAY_LOCATION_ASSET"] = xrayAssetDir;

            using var probeProc = Process.Start(psi);
            if (probeProc == null) return null;

            try
            {

                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 2000)
                {
                    if (probeProc.HasExited) break;
                    try
                    {
                        using var s = new TcpClient();
                        s.Connect(IPAddress.Loopback, ProbeBasePort);
                        break;
                    }
                    catch
                    {
                        Thread.Sleep(50);
                    }
                }

                if (!probeProc.HasExited)
                {
                    var winnerIndex = ProbeEndpoints(slice.Count, ProbeBasePort, ProbeTimeoutMs);
                    if (winnerIndex >= 0 && winnerIndex < slice.Count)
                    {
                        _logger.LogInformation("SHARD probe race winner in slice {SliceIndex}, index {Index}: {Address}:{Port}",
                            sliceIndex, winnerIndex, slice[winnerIndex].Address, slice[winnerIndex].Port);
                        return slice[winnerIndex];
                    }
                }
            }
            finally
            {
                try
                {
                    if (!probeProc.HasExited)
                    {
                        probeProc.Kill();
                        probeProc.WaitForExit(1000);
                    }
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SHARD probe race encountered an error on slice {SliceIndex}", sliceIndex);
        }

        return null;
    }

    private static int ProbeEndpoints(int count, int basePort, int timeoutMs)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        var tasks = new List<Task<int>>();

        for (var i = 0; i < count; i++)
        {
            var port = basePort + i;
            var index = i;
            tasks.Add(Task.Run(async () =>
            {
                var ok = await ProbeSinglePortAsync(port, timeoutMs, cts.Token);
                return ok ? index : -1;
            }, cts.Token));
        }

        while (tasks.Count > 0)
        {
            var completed = Task.WhenAny(tasks).GetAwaiter().GetResult();
            tasks.Remove(completed);
            try
            {
                var result = completed.GetAwaiter().GetResult();
                if (result >= 0)
                {
                    cts.Cancel();
                    return result;
                }
            }
            catch { }
        }

        return -1;
    }

    private static async Task<bool> ProbeSinglePortAsync(int socksPort, int timeoutMs, CancellationToken ct)
    {
        var targets = new (string host, string path, int port)[]
        {
            ("cp.cloudflare.com", "/generate_204", 80),
            ("www.gstatic.com", "/generate_204", 80),
        };

        foreach (var (host, path, port) in targets)
        {
            try
            {
                using var client = new TcpClient();
                using var timeoutCts = new CancellationTokenSource(timeoutMs);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

                await client.ConnectAsync(IPAddress.Loopback, socksPort, linked.Token);
                var stream = client.GetStream();

                await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, linked.Token);
                var authResp = new byte[2];
                if (!await ReadExactAsync(stream, authResp, 0, 2, linked.Token)) return false;
                if (authResp[0] != 0x05 || authResp[1] != 0x00) return false;

                var hostBytes = Encoding.ASCII.GetBytes(host);
                var connectReq = new List<byte> { 0x05, 0x01, 0x00, 0x03, (byte)hostBytes.Length };
                connectReq.AddRange(hostBytes);
                connectReq.Add((byte)(port >> 8));
                connectReq.Add((byte)(port & 0xFF));

                await stream.WriteAsync(connectReq.ToArray(), linked.Token);

                var head = new byte[4];
                if (!await ReadExactAsync(stream, head, 0, 4, linked.Token)) return false;
                if (head[1] != 0x00) return false;

                var atyp = head[3];
                int remainToDrain;
                if (atyp == 0x01) remainToDrain = 4 + 2;
                else if (atyp == 0x03)
                {
                    var lenBuf = new byte[1];
                    if (!await ReadExactAsync(stream, lenBuf, 0, 1, linked.Token)) return false;
                    remainToDrain = lenBuf[0] + 2;
                }
                else if (atyp == 0x04) remainToDrain = 16 + 2;
                else return false;

                var drainBuf = new byte[remainToDrain];
                if (!await ReadExactAsync(stream, drainBuf, 0, remainToDrain, linked.Token)) return false;

                var httpReq = $"GET {path} HTTP/1.1\r\nHost: {host}\r\nConnection: close\r\n\r\n";
                var reqBytes = Encoding.ASCII.GetBytes(httpReq);
                await stream.WriteAsync(reqBytes, linked.Token);

                var respBuffer = new byte[256];
                var read = await stream.ReadAsync(respBuffer, linked.Token);
                if (read <= 0) continue;

                var respStr = Encoding.ASCII.GetString(respBuffer, 0, read);
                if (respStr.Contains(" 204") || respStr.Contains(" 200")) return true;
            }
            catch
            {

            }
        }

        return false;
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, int offset, int count, CancellationToken ct)
    {
        var total = 0;
        while (total < count)
        {
            var r = await stream.ReadAsync(buffer.AsMemory(offset + total, count - total), ct);
            if (r <= 0) return false;
            total += r;
        }
        return true;
    }

    private string BuildProbeConfig(List<ShardNode> nodes, int basePort)
    {
        var inbounds = new JsonArray();
        var outbounds = new JsonArray
        {
            new JsonObject
            {
                ["tag"] = "blackhole",
                ["protocol"] = "blackhole"
            }
        };
        var rules = new JsonArray();

        for (var i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            var inTag = $"in-{i}";
            var outTag = $"out-{i}";

            inbounds.Add(new JsonObject
            {
                ["tag"] = inTag,
                ["listen"] = "127.0.0.1",
                ["port"] = basePort + i,
                ["protocol"] = "socks",
                ["settings"] = new JsonObject
                {
                    ["auth"] = "noauth",
                    ["udp"] = false
                }
            });

            outbounds.Add(BuildOutbound(node, outTag, mux: false));

            rules.Add(new JsonObject
            {
                ["type"] = "field",
                ["inboundTag"] = new JsonArray { inTag },
                ["outboundTag"] = outTag
            });
        }

        var root = new JsonObject
        {
            ["log"] = new JsonObject
            {
                ["loglevel"] = "none",
                ["access"] = "none"
            },
            ["inbounds"] = inbounds,
            ["outbounds"] = outbounds,
            ["routing"] = new JsonObject
            {
                ["rules"] = rules
            }
        };

        return root.ToJsonString();
    }

    private string BuildLiveConfig(ShardNode node, int socksPort, int httpPort)
    {
        var inbounds = new JsonArray
        {
            new JsonObject
            {
                ["tag"] = "in-socks",
                ["listen"] = "127.0.0.1",
                ["port"] = socksPort,
                ["protocol"] = "socks",
                ["settings"] = new JsonObject
                {
                    ["auth"] = "noauth",
                    ["udp"] = true,
                    ["ip"] = "127.0.0.1"
                },
                ["sniffing"] = new JsonObject
                {
                    ["enabled"] = true,
                    ["destOverride"] = new JsonArray { "http", "tls", "quic" },
                    ["routeOnly"] = true
                }
            }
        };

        if (httpPort > 0)
        {
            inbounds.Add(new JsonObject
            {
                ["tag"] = "in-http",
                ["listen"] = "127.0.0.1",
                ["port"] = httpPort,
                ["protocol"] = "http",
                ["sniffing"] = new JsonObject
                {
                    ["enabled"] = true,
                    ["destOverride"] = new JsonArray { "http", "tls", "quic" },
                    ["routeOnly"] = true
                }
            });
        }

        var isSmartSplit = _settings.Settings.ShardSmartSplit;
        var outbounds = new JsonArray
        {
            BuildOutbound(node, "proxy", mux: false),
            new JsonObject
            {
                ["tag"] = "blackhole",
                ["protocol"] = "blackhole"
            }
        };

        if (isSmartSplit)
        {

            outbounds.Add(new JsonObject
            {
                ["tag"] = "direct-frag",
                ["protocol"] = "freedom",
                ["streamSettings"] = new JsonObject
                {
                    ["finalmask"] = new JsonObject
                    {
                        ["tcp"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["type"] = "fragment",
                                ["settings"] = new JsonObject
                                {
                                    ["packets"] = "tlshello",
                                    ["lengths"] = new JsonArray { "5", "1" },
                                    ["delays"] = new JsonArray { "0" },
                                    ["maxSplit"] = "0"
                                }
                            },
                            new JsonObject
                            {
                                ["type"] = "fragment",
                                ["settings"] = new JsonObject
                                {
                                    ["packets"] = "1-1",
                                    ["lengths"] = new JsonArray { "43", "1" },
                                    ["delays"] = new JsonArray { "1" },
                                    ["maxSplit"] = "522"
                                }
                            }
                        }
                    },
                    ["sockopt"] = new JsonObject
                    {
                        ["domainStrategy"] = "ForceIP",
                        ["happyEyeballs"] = new JsonObject
                        {
                            ["tryDelayMs"] = 300,
                            ["prioritizeIPv6"] = true,
                            ["interleave"] = 2,
                            ["maxConcurrentTry"] = 20
                        }
                    }
                }
            });

            outbounds.Add(new JsonObject
            {
                ["tag"] = "direct-plain",
                ["protocol"] = "freedom"
            });

            outbounds.Add(new JsonObject
            {
                ["tag"] = "dns-out",
                ["protocol"] = "dns",
                ["settings"] = new JsonObject
                {
                    ["nonIPQuery"] = "drop",
                    ["userLevel"] = 1
                }
            });
        }

        var root = new JsonObject
        {
            ["log"] = new JsonObject
            {
                ["loglevel"] = "warning",
                ["access"] = "none"
            },
            ["inbounds"] = inbounds,
            ["outbounds"] = outbounds
        };

        if (isSmartSplit)
        {
            root["dns"] = new JsonObject
            {
                ["queryStrategy"] = "UseSystem",
                ["useSystemHosts"] = true,
                ["serveStale"] = true,
                ["hosts"] = new JsonObject
                {
                    ["cloudflare-dns.com"] = "challenges.cloudflare.com"
                },
                ["servers"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["tag"] = "shard-dns",
                        ["address"] = "tcp://8.8.8.8",
                        ["domains"] = new JsonArray
                        {
                            "geosite:youtube",
                            "geosite:google",
                            "geosite:instagram",
                            "geosite:facebook",
                            "geosite:twitter",
                            "geosite:tiktok",
                            "geosite:spotify",
                            "geosite:netflix",
                            "geosite:amazon",
                            "geosite:apple",
                            "geosite:openai",
                            "geosite:anthropic",
                            "geosite:xai",
                            "geosite:google-deepmind",
                            "geosite:reddit",
                            "geosite:twitch",
                            "geosite:discord",
                            "geosite:pinterest",
                            "geosite:medium",
                            "geosite:soundcloud",
                            "geosite:bbc",
                            "geosite:signal",
                            "geosite:clubhouse",
                            "geosite:quora",
                            "geosite:patreon",
                            "geosite:binance",
                            "geosite:coingecko",
                            "domain:reddit.com",
                            "domain:redd.it",
                            "domain:redditmedia.com",
                            "domain:twitch.tv",
                            "domain:discord.com",
                            "domain:discord.gg",
                            "domain:pinterest.com",
                            "domain:medium.com",
                            "domain:soundcloud.com",
                            "domain:clients6.google.com",
                            "full:content.googleapis.com",
                            "domain:googlevideo.com",
                            "domain:ytimg.com",
                            "domain:ggpht.com",
                            "geosite:telegram",
                            "geosite:whatsapp"
                        },
                        ["skipFallback"] = true,
                        ["finalQuery"] = true,
                        ["timeoutMs"] = 12000
                    },
                    new JsonObject
                    {
                        ["address"] = "localhost",
                        ["domains"] = new JsonArray
                        {
                            "domain:ir",
                            "geosite:category-ir",
                            "full:challenges.cloudflare.com"
                        },
                        ["skipFallback"] = true,
                        ["finalQuery"] = true
                    },
                    new JsonObject
                    {
                        ["tag"] = "doh",
                        ["address"] = "tcp://1.1.1.1",
                        ["timeoutMs"] = 12000
                    }
                }
            };

            root["routing"] = new JsonObject
            {
                ["domainStrategy"] = "IPIfNonMatch",
                ["rules"] = new JsonArray
                {

                    new JsonObject
                    {
                        ["type"] = "field",
                        ["ip"] = new JsonArray { "8.8.8.8", "8.8.4.4", "1.1.1.1", "1.0.0.1", "9.9.9.9" },
                        ["port"] = "53",
                        ["outboundTag"] = "proxy"
                    },

                    new JsonObject
                    {
                        ["type"] = "field",
                        ["inboundTag"] = new JsonArray { "shard-dns" },
                        ["outboundTag"] = "proxy"
                    },

                    new JsonObject
                    {
                        ["type"] = "field",
                        ["inboundTag"] = new JsonArray { "doh" },
                        ["outboundTag"] = "proxy"
                    },

                    new JsonObject
                    {
                        ["type"] = "field",
                        ["port"] = "53",
                        ["outboundTag"] = "dns-out"
                    },

                    new JsonObject
                    {
                        ["type"] = "field",
                        ["domain"] = new JsonArray { "domain:ir", "geosite:category-ir" },
                        ["outboundTag"] = "direct-plain"
                    },
                    new JsonObject
                    {
                        ["type"] = "field",
                        ["ip"] = new JsonArray { "geoip:ir", "geoip:private" },
                        ["outboundTag"] = "direct-plain"
                    },

                    new JsonObject
                    {
                        ["type"] = "field",
                        ["ip"] = new JsonArray { "10.10.34.0/24" },
                        ["outboundTag"] = "blackhole"
                    },

                    new JsonObject
                    {
                        ["type"] = "field",
                        ["domain"] = new JsonArray
                        {
                            "geosite:youtube",
                            "geosite:google",
                            "geosite:instagram",
                            "geosite:facebook",
                            "geosite:twitter",
                            "geosite:tiktok",
                            "geosite:spotify",
                            "geosite:netflix",
                            "geosite:amazon",
                            "geosite:apple",
                            "geosite:openai",
                            "geosite:anthropic",
                            "geosite:xai",
                            "geosite:google-deepmind",
                            "geosite:reddit",
                            "geosite:twitch",
                            "geosite:discord",
                            "geosite:pinterest",
                            "geosite:medium",
                            "geosite:soundcloud",
                            "geosite:bbc",
                            "geosite:signal",
                            "geosite:clubhouse",
                            "geosite:quora",
                            "geosite:patreon",
                            "geosite:binance",
                            "geosite:coingecko",
                            "domain:reddit.com",
                            "domain:redd.it",
                            "domain:redditmedia.com",
                            "domain:twitch.tv",
                            "domain:discord.com",
                            "domain:discord.gg",
                            "domain:pinterest.com",
                            "domain:medium.com",
                            "domain:soundcloud.com",
                            "geosite:telegram",
                            "geosite:whatsapp",
                            "domain:googlevideo.com",
                            "domain:ytimg.com",
                            "domain:ggpht.com",
                            "domain:clients6.google.com",
                            "full:content.googleapis.com"
                        },
                        ["outboundTag"] = "proxy"
                    },

                    new JsonObject
                    {
                        ["type"] = "field",
                        ["ip"] = new JsonArray
                        {
                            "geoip:telegram",
                            "geoip:facebook",
                            "15.197.128.0/17",
                            "3.33.128.0/17"
                        },
                        ["outboundTag"] = "proxy"
                    },

                    new JsonObject
                    {
                        ["type"] = "field",
                        ["network"] = "tcp",
                        ["port"] = "443",
                        ["outboundTag"] = "direct-frag"
                    },

                    new JsonObject
                    {
                        ["type"] = "field",
                        ["network"] = "tcp",
                        ["outboundTag"] = "direct-plain"
                    },

                    new JsonObject
                    {
                        ["type"] = "field",
                        ["network"] = "udp",
                        ["outboundTag"] = "direct-plain"
                    }
                }
            };
        }

        return root.ToJsonString();
    }

    private static JsonObject BuildOutbound(ShardNode node, string tag, bool mux)
    {
        var settings = new JsonObject();
        if (node.Protocol == "vless")
        {
            settings["vnext"] = new JsonArray
            {
                new JsonObject
                {
                    ["address"] = node.Address,
                    ["port"] = node.Port,
                    ["users"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["id"] = node.Credential,
                            ["encryption"] = "none"
                        }
                    }
                }
            };
        }
        else if (node.Protocol == "trojan")
        {
            settings["servers"] = new JsonArray
            {
                new JsonObject
                {
                    ["address"] = node.Address,
                    ["port"] = node.Port,
                    ["password"] = node.Credential
                }
            };
        }

        var streamSettings = new JsonObject
        {
            ["network"] = node.Network,
            ["security"] = node.Security
        };

        if (!string.IsNullOrEmpty(node.FinalMask))
        {
            try
            {
                var fmNode = JsonNode.Parse(node.FinalMask);
                if (fmNode != null) streamSettings["finalmask"] = fmNode;
            }
            catch { }
        }

        if (node.Security == "tls")
        {
            var tlsSettings = new JsonObject
            {
                ["serverName"] = node.ServerName,
                ["allowInsecure"] = false
            };
            if (!string.IsNullOrEmpty(node.Fingerprint)) tlsSettings["fingerprint"] = node.Fingerprint;
            if (!string.IsNullOrEmpty(node.CipherSuites)) tlsSettings["cipherSuites"] = node.CipherSuites;
            if (!string.IsNullOrEmpty(node.Alpn))
            {
                var alpnArray = new JsonArray();
                foreach (var a in node.Alpn.Split(','))
                {
                    var trimmed = a.Trim();
                    if (!string.IsNullOrEmpty(trimmed)) alpnArray.Add(trimmed);
                }
                tlsSettings["alpn"] = alpnArray;
            }
            streamSettings["tlsSettings"] = tlsSettings;
        }

        var wsPath = node.Path;
        if (string.IsNullOrEmpty(wsPath)) wsPath = "/";
        else if (!wsPath.StartsWith("/")) wsPath = "/" + wsPath;

        var wsHost = !string.IsNullOrEmpty(node.Host) ? node.Host : node.ServerName;

        streamSettings["wsSettings"] = new JsonObject
        {
            ["path"] = wsPath,
            ["host"] = wsHost
        };

        var outbound = new JsonObject
        {
            ["tag"] = tag,
            ["protocol"] = node.Protocol,
            ["settings"] = settings,
            ["streamSettings"] = streamSettings
        };

        if (mux && node.Protocol == "vless")
        {
            outbound["mux"] = new JsonObject
            {
                ["enabled"] = true,
                ["concurrency"] = 8,
                ["xudpConcurrency"] = 16,
                ["xudpProxyUDP443"] = "skip"
            };
        }

        return outbound;
    }
}
