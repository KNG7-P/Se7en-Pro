using System;
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

    /// <summary>
    /// Upstream resolvers consulted sequentially until an answer is received.
    /// </summary>
    private volatile List<DnsServerEntry> _relayTargets;

    private readonly TimeSpan _connectTimeout;
    private readonly TimeSpan _queryTimeout;

    private UdpClient? _listener;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private Task? _tcpLoop;
    private TcpListener? _tcpListener;
    private int _handled;

    public SocksDnsForwarder(
        int socksPort,
        string upstreamDnsIp = "1.1.1.1",
        TimeSpan? connectTimeout = null,
        TimeSpan? queryTimeout = null,
        IReadOnlyList<DnsServerEntry>? relayTargets = null)
    {
        _socksPort = socksPort;
        _upstreamDnsIp = upstreamDnsIp;
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(3);
        _queryTimeout = queryTimeout ?? TimeSpan.FromSeconds(5);
        _relayTargets = relayTargets is null
            ? new List<DnsServerEntry>()
            : relayTargets.ToList();
    }

        public int HandledQueries => _handled;

    /// <summary>
    /// Indicates whether no relay targets are configured.
    /// </summary>
    internal bool RelayTargetsAreEmpty => _relayTargets.Count == 0;

    /// <summary>
    /// Relays a single DNS query through the configured upstream resolvers.
    /// </summary>
    internal async Task<byte[]?> SendOneAsync(byte[] query, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        return await QueryUpstreamAsync(query, cts.Token);
    }

    /// <summary>
    /// Updates the list of upstream relay targets.
    /// </summary>
    public void UpdateRelayTargets(IReadOnlyList<DnsServerEntry> targets)
    {
        if (targets is null || targets.Count == 0)
        {
            Diag?.Invoke("dns: relay target update ignored — an empty list would resolve nothing");
            return;
        }

        _relayTargets = targets.ToList();
    }

    /// <summary>Snapshot of the current relay list, for diagnostics and tests.</summary>
    internal IReadOnlyList<DnsServerEntry> RelayTargetsSnapshot => _relayTargets;

        public Action<string>? Diag;

        public void Start()
    {
        if (_listener is not null) return;
        _listener = new UdpClient();
        _listener.ExclusiveAddressUse = true;
        _listener.Client.Bind(new IPEndPoint(IPAddress.Loopback, 53));

        
        
        
        
        
        
        try
        {
            _tcpListener = new TcpListener(IPAddress.Loopback, 53);
            _tcpListener.Start();
        }
        catch (SocketException)
        {
            
            
            _tcpListener = null;
        }

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        if (_tcpListener is not null)
        {
            _tcpLoop = Task.Run(() => TcpAcceptLoopAsync(_tcpListener, _cts.Token));
        }
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Dispose(); } catch { }
        _listener = null;
        try { _tcpListener?.Stop(); } catch { }
        _tcpListener = null;
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

                var answer = await AnswerQueryAsync(query, ct);
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

            
            
            _ = Task.Run(() => HandleQueryAsync(received.RemoteEndPoint, received.Buffer, ct), ct);
        }
    }

    private async Task HandleQueryAsync(IPEndPoint client, byte[] query, CancellationToken ct)
    {
        try
        {
            var answer = await AnswerQueryAsync(query, ct);
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

            if (parsed is { } q && split is not null && split.LocalDnsIp is not null)
            {
                var matched = MatchDomain(q.Name, split.Domains);
                var useLocal = split.ExcludeMode ? matched is not null : matched is null;
                var isAAAA = q.Type == 28;

                if (useLocal)
                {
                    
                    
                    
                    
                    if (isAAAA && split.ExcludeMode)
                    {
                        answer = BuildEmptyResponse(query, q.QuestionLength);
                    }
                    else
                    {
                        answer = await QueryLocalAsync(query, ct);
                        if (answer is null)
                        {
                            Diag?.Invoke($"split dns: local resolver ({split.LocalDnsIp}) did not answer "
                                         + $"'{q.Name}'; falling back to the tunnel path");
                            
                            
                            
                            
                            answer = await QueryUpstreamAsync(query, ct);
                        }
                        else
                        {
                            seen.AddRange(ExtractAnswerARecords(answer));
                        }
                    }
                }
                else
                {
                    answer = await QueryUpstreamAsync(query, ct);
                    if (answer is not null && !split.ExcludeMode && !isAAAA)
                    {
                        
                        
                        seen.AddRange(ExtractAnswerARecords(answer));
                        seenViaTunnel = true;
                    }
                }
            }
            else
            {
                answer = await QueryUpstreamAsync(query, ct);
            }

            if (answer is null) return null;
            Interlocked.Increment(ref _handled);

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
