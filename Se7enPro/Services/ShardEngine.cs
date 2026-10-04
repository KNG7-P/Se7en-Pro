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

    private const string SubscriptionUrl = "https://raw.githubusercontent.com/mbm110/MSN-GUARD/master/remote/shard-nodes.txt";
    private const string FallbackSubscriptionUrl = "https://raw.githubusercontent.com/patterniha/Free-Configs/main/configs.txt";
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

    public event EventHandler<ShardPoolInfo>? PoolUpdated;
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
        if (State != ConnectionState.Connected && State != ConnectionState.Connecting)
        {
            return Task.CompletedTask;
        }

        
        
        
        
        return Task.Run(async () =>
        {
            try
            {
                await StopAsync();
                await StartAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to restart SHARD core after rotation");
            }
        });
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

    public override IReadOnlyList<string> CoreProcessNames { get; } = OperatingSystem.IsWindows()
        ? new[] { EngineProcessNames.Shard, "xray.exe" }
        : new[] { EngineProcessNames.Shard, "xray", "xray.exe" };

    protected override string EngineDisplayName => "SHARD";

    protected override string WorkSubdirectory => "shard";

    protected override TimeSpan ReadyTimeout => TimeSpan.FromSeconds(35);
    private static readonly IReadOnlyList<int> ReservedEnginePorts = new[] { DefaultListenPort };

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

            PoolUpdated?.Invoke(this, GetPoolSummary());

            
            if (!File.Exists(ConfigCachePath) || (DateTime.UtcNow - _lastCheckUtc).TotalHours >= 6)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(2000);
                        await RefreshSubscriptionAsync(force: false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Background auto-refresh of SHARD subscription failed");
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
                
                
                
                
                
                
                if (!string.IsNullOrWhiteSpace(policyJson) && TryReadPolicyEdges(policyJson, out _))
                {
                    await File.WriteAllTextAsync(PolicyCachePath, policyJson);
                }
                else
                {
                    _logger.LogWarning(
                        "Remote SHARD policy did not contain a usable 'edges' array; keeping the existing one.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not refresh SHARD remote policy; keeping existing.");
            }

            
            try
            {
                string? configsText = null;
                try
                {
                    configsText = await client.GetStringAsync(SubscriptionUrl);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Primary SHARD subscription URL failed; trying fallback URL.");
                }

                if (string.IsNullOrWhiteSpace(configsText))
                {
                    configsText = await client.GetStringAsync(FallbackSubscriptionUrl);
                }

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
            var summary = new ShardPoolInfo(_cachedNodeCount, _cachedPathCount, _lastCheckUtc, success);
            PoolUpdated?.Invoke(this, summary);
            return summary;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private static bool TryReadPolicyEdges(string content, out List<string> edges)
    {
        edges = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(content);
            if (doc.RootElement.TryGetProperty("edges", out var edgesProp) &&
                edgesProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in edgesProp.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String) continue;
                    var ip = item.GetString()?.Trim();
                    if (!string.IsNullOrEmpty(ip) && IPAddress.TryParse(ip, out _))
                    {
                        edges.Add(ip);
                    }
                }
            }
        }
        catch
        {
            edges.Clear();
        }

        return edges.Count > 0;
    }

    private List<string> GetEdgeIps()
    {
        try
        {
            var path = File.Exists(PolicyCachePath) ? PolicyCachePath : DefaultPolicyPath;
            if (File.Exists(path) && TryReadPolicyEdges(File.ReadAllText(path), out var list))
            {
                return list;
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

        private string? NormalizeCustomCdnIp()
    {
        var raw = _settings.Settings.ShardCustomCfIp?.Trim() ?? "";
        if (raw.Length == 0) return null;

        if (raw.Contains('/') || raw.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length != 1)
        {
            _logger.LogWarning(
                "Ignoring SHARD custom Cloudflare IP \"{Raw}\": expected a single IP or hostname", raw);
            return null;
        }

        return raw;
    }

    private List<ShardNode> ExpandNodes(List<ShardNode> nodes)
    {
        var customIp = NormalizeCustomCdnIp();
        if (customIp is not null)
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

    private ShardNode? _selectedWinner;
    private bool _lastConnectAttemptFailed;

    private string ResolveXrayBinary()
    {
        var resShard = System.IO.Path.Combine(AppDir, "Resources", "shard");
        var binName = OperatingSystem.IsWindows() ? "xray.exe" : "xray";
        var sourceExe = System.IO.Path.Combine(resShard, binName);
        if (!File.Exists(sourceExe)) sourceExe = System.IO.Path.Combine(AppDir, "Resources", "xray", binName);
        if (!File.Exists(sourceExe)) sourceExe = System.IO.Path.Combine(resShard, "xray.exe");
        if (!File.Exists(sourceExe)) sourceExe = System.IO.Path.Combine(AppDir, "Resources", "xray", "xray.exe");
        return sourceExe;
    }

    private static string BuildProbeConfig(IReadOnlyList<ShardNode> candidates, int basePort)
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

        for (var i = 0; i < candidates.Count; i++)
        {
            var node = candidates[i];
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

    private static async Task<bool> ProbeCandidateAsync(int socksPort, int timeoutMs, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);
            var token = cts.Token;

            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, socksPort, token);
            await using var stream = client.GetStream();

            // SOCKS5 Greeting: noauth
            await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, token);
            var methodResp = new byte[2];
            await stream.ReadExactlyAsync(methodResp, token);
            if (methodResp[0] != 0x05 || methodResp[1] != 0x00) return false;

            // SOCKS5 Connect to cp.cloudflare.com:80 (ATYP = 3)
            const string host = "cp.cloudflare.com";
            const int port = 80;
            var hostBytes = Encoding.ASCII.GetBytes(host);
            var req = new byte[4 + 1 + hostBytes.Length + 2];
            req[0] = 0x05;
            req[1] = 0x01; // CONNECT
            req[2] = 0x00;
            req[3] = 0x03; // Domain
            req[4] = (byte)hostBytes.Length;
            Array.Copy(hostBytes, 0, req, 5, hostBytes.Length);
            req[5 + hostBytes.Length] = (byte)(port >> 8);
            req[6 + hostBytes.Length] = (byte)(port & 0xFF);
            await stream.WriteAsync(req, token);

            // SOCKS5 Reply
            var reply = new byte[4];
            await stream.ReadExactlyAsync(reply, token);
            if (reply[1] != 0x00) return false;

            // Consume bound address
            var boundLen = reply[3] switch
            {
                0x01 => 4,
                0x04 => 16,
                0x03 => stream.ReadByte(),
                _ => 0
            };
            if (boundLen > 0)
            {
                var bound = new byte[boundLen + 2];
                await stream.ReadExactlyAsync(bound, token);
            }

            // Send HTTP GET request
            var httpRequest = Encoding.ASCII.GetBytes(
                $"GET /generate_204 HTTP/1.1\r\nHost: {host}\r\nUser-Agent: Mozilla/5.0\r\nAccept: */*\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(httpRequest, token);

            // Read HTTP response status line
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 256, leaveOpen: true);
            var statusLine = await reader.ReadLineAsync(token);
            return statusLine != null && (statusLine.Contains(" 204") || statusLine.Contains(" 200"));
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> AwaitPortListeningAsync(int port, int timeoutMs, Process proc, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline && !proc.HasExited && !ct.IsCancellationRequested)
        {
            try
            {
                using var client = new TcpClient();
                var connectTask = client.ConnectAsync(IPAddress.Loopback, port);
                var completed = await Task.WhenAny(connectTask, Task.Delay(200, ct));
                if (completed == connectTask && client.Connected)
                {
                    return true;
                }
            }
            catch { }
            await Task.Delay(100, ct);
        }
        return false;
    }

    private async Task<ShardNode?> RaceCandidatesAsync(
        IReadOnlyList<ShardNode> candidates, string workDir, CancellationToken ct)
    {
        if (candidates.Count == 0) return null;

        var sourceExe = ResolveXrayBinary();
        if (!File.Exists(sourceExe)) return null;

        var probeConfigPath = System.IO.Path.Combine(workDir, "probe.json");

        for (var slice = 0; slice < MaxRaceSlices; slice++)
        {
            if (ct.IsCancellationRequested) return null;

            var sliceCandidates = candidates.Skip(slice * RaceWidth).Take(RaceWidth).ToList();
            if (sliceCandidates.Count == 0) break;

            _logger.LogInformation("Racing SHARD candidate slice {Slice} ({Count} candidates)...",
                slice + 1, sliceCandidates.Count);

            var probeJson = BuildProbeConfig(sliceCandidates, ProbeBasePort);
            await File.WriteAllTextAsync(probeConfigPath, probeJson, new UTF8Encoding(false), ct);

            Process? probeProc = null;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = sourceExe,
                    Arguments = $"run -c \"{probeConfigPath}\"",
                    WorkingDirectory = workDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                var xrayAssetDir = System.IO.Path.Combine(AppDir, "Resources", "shard");
                if (!Directory.Exists(xrayAssetDir)) xrayAssetDir = System.IO.Path.Combine(AppDir, "Resources", "xray");
                if (Directory.Exists(xrayAssetDir)) psi.Environment["XRAY_LOCATION_ASSET"] = xrayAssetDir;

                probeProc = Process.Start(psi);
                if (probeProc == null || probeProc.HasExited) continue;

                var listening = await AwaitPortListeningAsync(ProbeBasePort, 3500, probeProc, ct);
                if (!listening || probeProc.HasExited) continue;

                using var raceCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                raceCts.CancelAfter(TimeSpan.FromMilliseconds(ProbeTimeoutMs * 2));
                var raceToken = raceCts.Token;

                var winnerTcs = new TaskCompletionSource<ShardNode>(TaskCreationOptions.RunContinuationsAsynchronously);

                var tasks = sliceCandidates.Select(async (node, index) =>
                {
                    var port = ProbeBasePort + index;
                    var sw = Stopwatch.StartNew();
                    var ok = await ProbeCandidateAsync(port, ProbeTimeoutMs, raceToken);
                    if (ok)
                    {
                        sw.Stop();
                        _logger.LogInformation(
                            "Candidate {Index} ({Proto} to {Address}:{Port} via {Host}) verified live in {Elapsed}ms",
                            index, node.Protocol, node.Address, node.Port, node.Host, sw.ElapsedMilliseconds);
                        winnerTcs.TrySetResult(node);
                    }
                }).ToList();

                var winnerTask = winnerTcs.Task;
                var allProbesTask = Task.WhenAll(tasks);

                var completed = await Task.WhenAny(winnerTask, allProbesTask, Task.Delay(ProbeTimeoutMs * 2, raceToken));
                if (completed == winnerTask && winnerTask.IsCompletedSuccessfully)
                {
                    var winner = await winnerTask;
                    return winner;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error during SHARD probe race slice {Slice}", slice + 1);
            }
            finally
            {
                if (probeProc != null)
                {
                    try
                    {
                        if (!probeProc.HasExited)
                        {
                            probeProc.Kill(entireProcessTree: true);
                            await probeProc.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
                        }
                    }
                    catch { }
                    probeProc.Dispose();
                }
            }
        }

        return null;
    }

    protected override async Task BeforeStartAsync(string workDir, CancellationToken ct)
    {
        _selectedWinner = null;

        var rawNodes = LoadNodesFromCacheOrSeed();
        if (rawNodes.Count == 0)
        {
            await RefreshSubscriptionAsync(force: true);
            rawNodes = LoadNodesFromCacheOrSeed();
        }

        if (rawNodes.Count == 0) return;

        // If a previous connection attempt failed to establish a working tunnel, advance the rotation cursor
        if (_lastConnectAttemptFailed)
        {
            AdvanceRotationCursor(RaceWidth);
            _lastConnectAttemptFailed = false;
        }

        var customIp = NormalizeCustomCdnIp();
        if (customIp is not null)
        {
            var offset = _rotationOffset % rawNodes.Count;
            _selectedWinner = rawNodes[offset] with { Address = customIp };
            return;
        }

        var candidates = ExpandNodes(rawNodes);
        var diversified = Diversify(candidates);
        if (diversified.Count == 0) return;

        var offsetIdx = _rotationOffset % diversified.Count;
        var rotated = diversified.Skip(offsetIdx).Concat(diversified.Take(offsetIdx)).ToList();

        Log($"[SHARD] Loaded {rotated.Count} candidate edge routes; discovering fastest route...");
        SetConnectProgress(15, Loc.Of("Selecting SHARD edge route..."));

        try
        {
            var winner = await RaceCandidatesAsync(rotated, workDir, ct);
            if (winner != null)
            {
                _selectedWinner = winner;
                Log($"[SHARD] Confirmed working edge endpoint: {winner.Address}:{winner.Port} ({winner.Protocol.ToUpperInvariant()}) via {winner.Host}");
                return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SHARD probe race encountered an error, falling back to candidate selection");
        }

        Log("[SHARD] Candidate probe race found no responsive node within budget; falling back to candidate route.");
        _selectedWinner = SelectBestCandidate(candidates);
    }

    protected override PreparedLaunch Prepare(string workDir, int socksPort, int httpPort)
    {
        var sourceExe = ResolveXrayBinary();
        var exePath = StageFile(sourceExe, System.IO.Path.Combine(workDir, EngineProcessNames.Shard));

        var rawNodes = LoadNodesFromCacheOrSeed();
        if (rawNodes.Count == 0)
        {
            throw new InvalidOperationException("No SHARD nodes available in cache or seed list.");
        }

        var candidates = ExpandNodes(rawNodes);
        var winner = _selectedWinner ?? SelectBestCandidate(candidates);
        _selectedWinner = null;
        _lastConnectAttemptFailed = true;

        _logger.LogInformation("Selected SHARD node: {Proto} to {Address}:{Port} (Host: {Host})",
            winner.Protocol, winner.Address, winner.Port, winner.Host);
        Log($"[SHARD] Selected edge endpoint: {winner.Address}:{winner.Port} ({winner.Protocol.ToUpperInvariant()}) via {winner.Host}");
        SetConnectProgress(35, Loc.Of("Configuring SHARD live tunnel..."));

        CurrentRouteIp = winner.Address;
        CurrentRouteSni = !string.IsNullOrEmpty(winner.ServerName) ? winner.ServerName : winner.Host;

        var configJson = BuildLiveConfig(winner, socksPort, httpPort);
        var configPath = System.IO.Path.Combine(workDir, "config.json");
        File.WriteAllText(configPath, configJson, new UTF8Encoding(false));

        var env = new Dictionary<string, string>();
        var xrayAssetDir = System.IO.Path.Combine(AppDir, "Resources", "shard");
        if (!Directory.Exists(xrayAssetDir)) xrayAssetDir = System.IO.Path.Combine(AppDir, "Resources", "xray");
        if (Directory.Exists(xrayAssetDir)) env["XRAY_LOCATION_ASSET"] = xrayAssetDir;

        return new PreparedLaunch(
            exePath,
            new[] { "run", "-c", configPath },
            workDir,
            HttpProxyPort: httpPort,
            SocksPortOverride: socksPort,
            EnvironmentVariables: env
        );
    }

    protected override void OnTunnelConnected(int socksPort, int httpPort, CancellationToken ct)
    {
        _lastConnectAttemptFailed = false;
        base.OnTunnelConnected(socksPort, httpPort, ct);
    }

    protected override void OnCoreLine(string line)
    {
        base.OnCoreLine(line);
        if (line.Contains("started", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("app/dispatcher/default: start", StringComparison.OrdinalIgnoreCase))
        {
            SetConnectProgress(70, Loc.Of("SHARD edge gateway listening..."));
        }
    }

    private static List<ShardNode> Diversify(List<ShardNode> nodes)
    {
        // Group by endpoint identity (protocol, credential, host, path, port) to avoid edge IP clustering
        var groups = new Dictionary<string, List<ShardNode>>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes)
        {
            var identity = $"{node.Protocol}|{node.Credential}|{node.Host}|{node.Path}|{node.Port}";
            if (!groups.TryGetValue(identity, out var list))
            {
                list = new List<ShardNode>();
                groups[identity] = list;
            }
            list.Add(node);
        }

        var outList = new List<ShardNode>(nodes.Count);
        var maxPerEndpoint = groups.Values.Count > 0 ? groups.Values.Max(v => v.Count) : 0;
        for (var round = 0; round < maxPerEndpoint; round++)
        {
            var addedThisRound = false;
            foreach (var variants in groups.Values)
            {
                if (round < variants.Count)
                {
                    outList.Add(variants[round]);
                    addedThisRound = true;
                }
            }
            if (!addedThisRound) break;
        }
        return outList;
    }

    private ShardNode SelectBestCandidate(List<ShardNode> candidates)
    {
        var customIp = NormalizeCustomCdnIp();
        if (customIp is not null && candidates.Count > 0)
        {
            var offset = _rotationOffset % candidates.Count;
            return candidates[offset] with { Address = customIp };
        }

        var diversified = Diversify(candidates);
        if (diversified.Count == 0) return candidates[0];

        var offsetIdx = _settings.Settings.ShardRotateIp ? (_rotationOffset % diversified.Count) : 0;
        var rotated = diversified.Skip(offsetIdx).Concat(diversified.Take(offsetIdx)).ToList();

        var previousIp = CurrentRouteIp;
        if (!string.IsNullOrEmpty(previousIp) && _rotationOffset > 0)
        {
            var diffIp = rotated.Where(c => !string.Equals(c.Address, previousIp, StringComparison.OrdinalIgnoreCase)).ToList();
            var sameIp = rotated.Where(c => string.Equals(c.Address, previousIp, StringComparison.OrdinalIgnoreCase)).ToList();
            rotated = diffIp.Concat(sameIp).ToList();
        }

        var testSlice = rotated.Take(4).ToList();
        if (testSlice.Count == 1) return testSlice[0];

        try
        {
            using var cts = new CancellationTokenSource(300);
            var tasks = testSlice.Select(async node =>
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    using var tcp = new TcpClient();
                    await tcp.ConnectAsync(node.Address, node.Port, cts.Token);
                    return (Node: node, LatencyMs: sw.ElapsedMilliseconds, Success: true);
                }
                catch
                {
                    return (Node: node, LatencyMs: 9999L, Success: false);
                }
            }).ToList();

            var results = Task.WhenAll(tasks).GetAwaiter().GetResult();
            var best = results.Where(r => r.Success).OrderBy(r => r.LatencyMs).FirstOrDefault();
            if (best.Node != null)
            {
                _logger.LogInformation("Selected fastest edge IP {Address}:{Port} (latency {Latency}ms)",
                    best.Node.Address, best.Node.Port, best.LatencyMs);
                return best.Node;
            }
        }
        catch { }

        return rotated[0];
    }

    private string BuildLiveConfig(ShardNode node, int socksPort, int httpPort)
    {
        var bindAddr = _settings.Settings.AllowLanConnections ? "0.0.0.0" : "127.0.0.1";
        var inbounds = new JsonArray
        {
            new JsonObject
            {
                ["tag"] = "in-socks",
                ["listen"] = bindAddr,
                ["port"] = socksPort,
                ["protocol"] = "socks",
                ["settings"] = new JsonObject
                {
                    ["auth"] = "noauth",
                    ["udp"] = true,
                    ["ip"] = bindAddr
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
                ["listen"] = bindAddr,
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
            BuildOutbound(node, "proxy", mux: false)
        };

        if (!isSmartSplit)
        {
            // Plain SHARD DoH resolver chained through proxy outbound (Xray 26.9+ dialerProxy)
            outbounds.Add(new JsonObject
            {
                ["tag"] = "doh-resolver",
                ["protocol"] = "dns",
                ["settings"] = new JsonObject
                {
                    ["address"] = "https://cloudflare-dns.com/dns-query",
                    ["port"] = 443,
                    ["userLevel"] = 1
                },
                ["streamSettings"] = new JsonObject
                {
                    ["sockopt"] = new JsonObject
                    {
                        ["dialerProxy"] = "proxy"
                    }
                }
            });
        }

        outbounds.Add(new JsonObject
        {
            ["tag"] = "blackhole",
            ["protocol"] = "blackhole"
        });

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

        if (!isSmartSplit)
        {
            root["dns"] = new JsonObject
            {
                ["queryStrategy"] = "UseIP",
                ["useSystemHosts"] = true,
                ["serveStale"] = true,
                ["servers"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["tag"] = "doh-resolver",
                        ["address"] = "https://cloudflare-dns.com/dns-query",
                        ["timeoutMs"] = 12000
                    }
                }
            };

            root["routing"] = new JsonObject
            {
                ["rules"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "field",
                        ["port"] = "53",
                        ["outboundTag"] = "doh-resolver"
                    }
                }
            };
        }
        else
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
                        ["address"] = "https://1.1.1.1/dns-query",
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
                        ["address"] = "https://cloudflare-dns.com/dns-query",
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
