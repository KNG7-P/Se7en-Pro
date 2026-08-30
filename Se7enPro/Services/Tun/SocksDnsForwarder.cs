using System;
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
    private readonly TimeSpan _connectTimeout;
    private readonly TimeSpan _queryTimeout;

    private UdpClient? _listener;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private int _handled;

    public SocksDnsForwarder(
        int socksPort,
        string upstreamDnsIp = "1.1.1.1",
        TimeSpan? connectTimeout = null,
        TimeSpan? queryTimeout = null)
    {
        _socksPort = socksPort;
        _upstreamDnsIp = upstreamDnsIp;
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(3);
        _queryTimeout = queryTimeout ?? TimeSpan.FromSeconds(5);
    }

    public int HandledQueries => _handled;

    public Action<string>? Diag;

    public void Start()
    {
        if (_listener is not null) return;
        _listener = new UdpClient();
        _listener.ExclusiveAddressUse = true;
        _listener.Client.Bind(new IPEndPoint(IPAddress.Loopback, 53));
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => ReceiveLoopAsync(_cts.Token));
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Dispose(); } catch { }
        _listener = null;
        try { _cts?.Dispose(); } catch { }
        _cts = null;
        _loop = null;
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
            var parsed = ParseQuestion(query);
            var split = _split;
            byte[]? answer;
            IPAddress? seen = null;

            if (parsed is { } q && split is not null && split.LocalDnsIp is not null)
            {
                var matched = MatchDomain(q.Name, split.Domains);
                var useLocal = split.ExcludeMode ? matched is not null : matched is null;
                var isAAAA = q.Type == 28;

                if (useLocal)
                {
                    if (isAAAA)
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
                            seen = ExtractAnswerARecords(answer).FirstOrDefault();
                        }
                    }
                }
                else
                {
                    answer = await QueryUpstreamAsync(query, ct);
                    if (answer is not null && !split.ExcludeMode && !isAAAA)
                    {

                        seen = ExtractAnswerARecords(answer).FirstOrDefault();
                    }
                }
            }
            else
            {
                answer = await QueryUpstreamAsync(query, ct);
            }

            if (answer is null) return;
            Interlocked.Increment(ref _handled);

            if (seen is not null && split is not null)
            {
                try { split.AddressSeen?.Invoke(seen, parsed?.Name ?? ""); } catch { }
            }

            var listener = _listener;
            if (listener is null) return;
            await listener.SendAsync(answer, answer.Length, client);
        }
        catch
        {

        }
    }

    public void Dispose() => Stop();
}
