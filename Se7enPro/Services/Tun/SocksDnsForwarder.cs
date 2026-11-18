using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Se7enPro.Services;

internal sealed partial class SocksDnsForwarder : IDisposable
{
    private readonly int _socksPort;
    private readonly string _upstreamDnsIp;
    private readonly string _socksUser;
    private readonly string _socksPass;

    private volatile List<DnsServerEntry> _relayTargets;

    private readonly TimeSpan _connectTimeout;
    private readonly TimeSpan _queryTimeout;

    private UdpClient? _listener;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private Task? _tcpLoop;
    private TcpListener? _tcpListener;
    private int _handled;

    private readonly SemaphoreSlim _querySlots = new(MaxConcurrentQueries, MaxConcurrentQueries);
    private readonly ConcurrentDictionary<string, (byte[] Answer, DateTime ExpireAt)> _dnsCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task<byte[]?>> _inFlightQueries = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _targetCooldownUntilTicks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _targetFailureStreaks = new(StringComparer.OrdinalIgnoreCase);
    private volatile string? _preferredTargetKey;
    private volatile bool _suppressIpv6Queries;
    private volatile bool _stopped;

    private const int MaxConcurrentQueries = 128;
    private const int FailuresBeforeCooldown = 2;
    private const int CooldownSeconds = 60;

    private IPAddress _bindAddress = IPAddress.Parse(PreferredLoopbackAddress);

    private const string PreferredLoopbackAddress = "127.0.0.9";

    private static readonly IPAddress[] LoopbackCandidates =
    {
        IPAddress.Parse(PreferredLoopbackAddress),
        IPAddress.Loopback,
    };

    internal IPAddress BindAddress => _bindAddress;

    public SocksDnsForwarder(
        int socksPort,
        string upstreamDnsIp = "1.1.1.1",
        TimeSpan? connectTimeout = null,
        TimeSpan? queryTimeout = null,
        IReadOnlyList<DnsServerEntry>? relayTargets = null,
        string? socksUsername = null,
        string? socksPassword = null,
        bool suppressIpv6Queries = false)
    {
        _socksPort = socksPort;
        _upstreamDnsIp = upstreamDnsIp;
        _socksUser = socksUsername ?? "";
        _socksPass = socksPassword ?? "";
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(3);
        _queryTimeout = queryTimeout ?? TimeSpan.FromSeconds(5);
        _suppressIpv6Queries = suppressIpv6Queries;
        _relayTargets = relayTargets is null
            ? new List<DnsServerEntry>()
            : relayTargets.ToList();
    }

    internal void SetSuppressIpv6Queries(bool suppress) => _suppressIpv6Queries = suppress;

        public int HandledQueries => _handled;

    internal bool RelayTargetsAreEmpty => _relayTargets.Count == 0;

    internal Task<byte[]?> ProcessQueryForTestAsync(byte[] query, CancellationToken ct)
        => AnswerQueryAsync(query, ct);

    internal async Task<byte[]?> SendOneAsync(byte[] query, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        return await QueryUpstreamAsync(query, cts.Token);
    }

    public void UpdateRelayTargets(IReadOnlyList<DnsServerEntry> targets)
    {
        if (targets is null || targets.Count == 0)
        {
            Diag?.Invoke("dns: relay target update ignored — an empty list would resolve nothing");
            return;
        }

        _relayTargets = targets.ToList();
        _targetCooldownUntilTicks.Clear();
        _preferredTargetKey = null;
    }

    internal IReadOnlyList<DnsServerEntry> RelayTargetsSnapshot => _relayTargets;

        public Action<string>? Diag;

        public void Start()
    {
        if (_listener is not null) return;

        Exception? lastBindError = null;
        foreach (var candidate in LoopbackCandidates)
        {
            var udp = new UdpClient();
            try
            {
                udp.ExclusiveAddressUse = true;
                udp.Client.Bind(new IPEndPoint(candidate, 53));
                _listener = udp;
                _bindAddress = candidate;
                break;
            }
            catch (SocketException ex)
            {
                lastBindError = ex;
                try { udp.Dispose(); } catch { }
            }
        }

        if (_listener is null)
        {
            throw new InvalidOperationException(
                "Could not bind the local DNS forwarder on 127.0.0.9:53 or 127.0.0.1:53. "
                + "Another DNS service is using the port; stop it or change its port.",
                lastBindError);
        }

        if (!ReferenceEquals(_bindAddress, LoopbackCandidates[0]))
        {
            Diag?.Invoke($"dns: {LoopbackCandidates[0]}:53 unavailable "
                         + $"({lastBindError?.Message}); using {_bindAddress}:53");
        }

        try
        {
            _tcpListener = new TcpListener(_bindAddress, 53);
            _tcpListener.Start();
        }
        catch (SocketException ex)
        {
            Diag?.Invoke($"dns: TCP listener bind to {_bindAddress}:53 failed ({ex.Message}); "
                         + "truncated/TCP DNS queries will be refused");
            _tcpListener = null;
        }

        _stopped = false;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        if (_tcpListener is not null)
        {
            _tcpLoop = Task.Run(() => TcpAcceptLoopAsync(_tcpListener, _cts.Token));
        }
    }

    public void Stop()
    {
        _stopped = true;
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Dispose(); } catch { }
        _listener = null;
        try { _tcpListener?.Stop(); } catch { }
        _tcpListener = null;
        DisposeDohClient();
        _inFlightQueries.Clear();
        try { _cts?.Dispose(); } catch { }
        _cts = null;
        _loop = null;
        _tcpLoop = null;
    }

        private async Task TcpAcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient? client = null;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct);
                var accepted = client;
                client = null;
                _ = Task.Run(() => HandleTcpClientAsync(accepted, ct), CancellationToken.None);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { break; }
            catch
            {
                try { await Task.Delay(50, ct); }
                catch (OperationCanceledException) { break; }
            }
            finally
            {
                if (client is not null) { try { client.Dispose(); } catch { } }
            }
        }
    }

    private async Task HandleTcpClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                await using var stream = client.GetStream();

                var lengthBuf = new byte[2];
                await ReadExactlyAsync(stream, lengthBuf, ct);
                var queryLen = (lengthBuf[0] << 8) | lengthBuf[1];
                if (queryLen is <= 0 or > 65535) return;

                var query = new byte[queryLen];
                await ReadExactlyAsync(stream, query, ct);

                var answer = await AnswerQueryAsync(query, ct) ?? BuildServFailResponse(query);
                if (answer is null) return;

                var framed = new byte[2 + answer.Length];
                framed[0] = (byte)(answer.Length >> 8);
                framed[1] = (byte)(answer.Length & 0xFF);
                Buffer.BlockCopy(answer, 0, framed, 2, answer.Length);
                await stream.WriteAsync(framed, ct);
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (SocketException) { }
            catch { }
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var listener = _listener;
        if (listener is null) return;

        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await listener.ReceiveAsync(ct);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch { continue; }

            
            if (!_querySlots.Wait(0)) continue;

            _ = Task.Run(async () =>
            {
                try { await HandleQueryAsync(received.RemoteEndPoint, received.Buffer, ct); }
                finally { _querySlots.Release(); }
            }, CancellationToken.None);
        }
    }

    private async Task HandleQueryAsync(IPEndPoint client, byte[] query, CancellationToken ct)
    {
        try
        {
            var answer = await AnswerQueryAsync(query, ct) ?? BuildServFailResponse(query);
            if (answer is null) return;

            var listener = _listener;
            if (listener is null) return;
            await listener.SendAsync(answer, answer.Length, client);
        }
        catch
        {
            
            
        }
    }

        private async Task<byte[]?> AnswerQueryAsync(byte[] query, CancellationToken ct)
    {
        try
        {
            var parsed = ParseQuestion(query);
            var split = _split;
            byte[]? answer;
            var seen = new List<IPAddress>();
            bool seenViaTunnel = false;

            string? cacheKey = null;
            if (parsed is { } pq && query.Length >= 2)
            {
                var pol = _split is null ? "-" : (_split.ExcludeMode ? "E" : "I") + _split.Domains.Count;
                cacheKey = $"{(pq.Name ?? "").TrimEnd('.')}:{pq.Type}:{pol}";
                if (_dnsCache.TryGetValue(cacheKey, out var cached) && DateTime.UtcNow < cached.ExpireAt)
                {
                    var cachedResp = (byte[])cached.Answer.Clone();
                    cachedResp[0] = query[0];
                    cachedResp[1] = query[1];
                    Interlocked.Increment(ref _handled);
                    if (split is not null && split.Domains.Count > 0 && pq.Type != 28 && pq.Name is not null)
                    {
                        var matchedCached = MatchDomain(pq.Name, split.Domains);
                        if (matchedCached is not null)
                        {
                            var viaTun = !split.ExcludeMode;
                            foreach (var ip in ExtractAnswerARecords(cachedResp))
                            {
                                try { split.AddressSeen?.Invoke(ip, pq.Name, viaTun); } catch { }
                            }
                        }
                    }
                    return cachedResp;
                }
            }

            if (parsed is { } q && split is not null && split.Domains.Count > 0)
            {
                var matched = MatchDomain(q.Name, split.Domains);
                var useLocal = split.ExcludeMode ? matched is not null : matched is null;
                var isAAAA = q.Type == 28;

                if (isAAAA && (!useLocal || _suppressIpv6Queries))
                {
                    answer = BuildEmptyResponse(query, q.QuestionLength);
                }
                else if (useLocal && split.LocalDnsIp is not null)
                {
                    answer = await QueryLocalAsync(query, ct);
                    if (answer is null)
                    {
                        Diag?.Invoke($"split dns: local resolver ({split.LocalDnsIp}) did not answer "
                                     + $"'{q.Name}'; falling back to the tunnel path");
                        answer = await QueryUpstreamCoalescedAsync(query, cacheKey, ct);
                    }
                    else if (split.ExcludeMode && matched is not null && !isAAAA)
                    {
                        seen.AddRange(ExtractAnswerARecords(answer));
                        seenViaTunnel = false;
                    }
                }
                else
                {
                    answer = await QueryUpstreamCoalescedAsync(query, cacheKey, ct);
                    if (answer is not null && matched is not null && !isAAAA)
                    {
                        seen.AddRange(ExtractAnswerARecords(answer));
                        seenViaTunnel = !split.ExcludeMode;
                    }
                }
            }
            else if (parsed is { Type: 28, QuestionLength: var qLen } && _suppressIpv6Queries)
            {
                answer = BuildEmptyResponse(query, qLen);
            }
            else
            {
                answer = await QueryUpstreamCoalescedAsync(query, cacheKey, ct);
            }

            if (answer is null) return null;
            Interlocked.Increment(ref _handled);

            if (cacheKey is not null && answer.Length >= 12 && (answer[3] & 0x0F) == 0 && !IsSinkholedAnswer(answer))
            {
                _dnsCache[cacheKey] = (answer, DateTime.UtcNow.AddSeconds(60));
                if (_dnsCache.Count > 4096)
                {
                    var now = DateTime.UtcNow;
                    foreach (var kvp in _dnsCache)
                    {
                        if (kvp.Value.ExpireAt < now) _dnsCache.TryRemove(kvp.Key, out _);
                    }
                }
            }

            if (seen.Count > 0 && split is not null)
            {
                var name = parsed?.Name ?? "";
                foreach (var ip in seen)
                {
                    
                    
                    try { split.AddressSeen?.Invoke(ip, name, seenViaTunnel); } catch { }
                }
            }

            return answer;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose() => Stop();
}
