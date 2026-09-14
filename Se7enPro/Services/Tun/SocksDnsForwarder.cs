using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Se7enPro.Services;

internal sealed partial class SocksDnsForwarder : IDisposable
{
    private const int DefaultPort = 53;

    private readonly int _socksPort;
    private readonly string _upstreamDnsIp;
    private readonly TimeSpan _connectTimeout;
    private readonly TimeSpan _queryTimeout;
    private readonly int _listenPort;

    private UdpClient? _listener;
    private UdpClient? _listenerV6;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private Task? _loopV6;
    private int _handled;

    public SocksDnsForwarder(
        int socksPort,
        string upstreamDnsIp = "1.1.1.1",
        TimeSpan? connectTimeout = null,
        TimeSpan? queryTimeout = null,
        int listenPort = DefaultPort)
    {
        _socksPort = socksPort;
        _upstreamDnsIp = upstreamDnsIp;
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(3);
        _queryTimeout = queryTimeout ?? TimeSpan.FromSeconds(5);
        _listenPort = listenPort;
    }

    public int HandledQueries => _handled;

    public IPAddress? BoundAddress { get; private set; }

    public IPAddress? BoundAddressV6 { get; private set; }

    public Action<string>? Diag;

    private static readonly TimeSpan PreferredAddressGrace = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan PreferredAddressRetry = TimeSpan.FromMilliseconds(100);

    public void Start() => StartAsync(null, CancellationToken.None).GetAwaiter().GetResult();

    public Task StartAsync(IPAddress? preferredAddress, CancellationToken ct) =>
        StartAsync(preferredAddress, null, ct);

    public async Task StartAsync(IPAddress? preferredAddress, IPAddress? preferredAddressV6, CancellationToken ct)
    {
        if (_listener is not null) return;

        UdpClient? bound = null;
        if (preferredAddress is not null && !IPAddress.IsLoopback(preferredAddress))
        {
            bound = await TryBindPreferredAsync(preferredAddress, ct);
            if (bound is not null) BoundAddress = preferredAddress;
        }

        if (bound is null)
        {

            bound = Bind(IPAddress.Loopback);
            BoundAddress = IPAddress.Loopback;
        }

        _listener = bound;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => ReceiveLoopAsync(bound, _cts.Token));

        if (preferredAddressV6 is not null && !IPAddress.IsLoopback(preferredAddressV6))
        {
            var boundV6 = await TryBindPreferredAsync(preferredAddressV6, ct);
            if (boundV6 is not null)
            {
                _listenerV6 = boundV6;
                BoundAddressV6 = preferredAddressV6;
                _loopV6 = Task.Run(() => ReceiveLoopAsync(boundV6, _cts.Token));
            }
        }
    }

    private async Task<UdpClient?> TryBindPreferredAsync(IPAddress addr, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        var deadline = started + PreferredAddressGrace;
        var attempts = 0;
        SocketError last = SocketError.Success;
        while (true)
        {
            try
            {
                var client = Bind(addr);
                if (attempts > 0)
                {
                    Diag?.Invoke($"dns forwarder: {addr} became bindable after "
                                 + $"{(DateTime.UtcNow - started).TotalMilliseconds:0} ms ({attempts} retries)");
                }
                return client;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressNotAvailable
                                             && DateTime.UtcNow < deadline)
            {
                last = ex.SocketErrorCode;
                attempts++;
                await Task.Delay(PreferredAddressRetry, ct);
            }
            catch (SocketException ex)
            {
                last = ex.SocketErrorCode;
                break;
            }
        }

        Diag?.Invoke($"dns forwarder: cannot bind {addr}:{_listenPort} ({last}); falling back to {IPAddress.Loopback}");
        return null;
    }

    private UdpClient Bind(IPAddress addr)
    {
        var client = new UdpClient { ExclusiveAddressUse = true };
        try
        {
            client.Client.Bind(new IPEndPoint(addr, _listenPort));
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Dispose(); } catch { }
        try { _listenerV6?.Dispose(); } catch { }
        _listener = null;
        _listenerV6 = null;
        BoundAddress = null;
        BoundAddressV6 = null;
        try { _cts?.Dispose(); } catch { }
        _cts = null;
        _loop = null;
        _loopV6 = null;
    }

    private async Task ReceiveLoopAsync(UdpClient listener, CancellationToken ct)
    {
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

            _ = Task.Run(() => HandleQueryAsync(listener, received.RemoteEndPoint, received.Buffer, ct), ct);
        }
    }

    private async Task HandleQueryAsync(UdpClient listener, IPEndPoint client, byte[] query, CancellationToken ct)
    {
        try
        {
            var parsed = ParseQuestion(query);
            var split = _split;
            byte[]? answer;
            IReadOnlyList<IPAddress> seen = Array.Empty<IPAddress>();

            if (parsed is { } q && split is not null && split.LocalDnsIp is not null)
            {
                var matched = MatchDomain(q.Name, split.Domains);
                var useLocal = split.ExcludeMode ? matched is not null : matched is null;
                var isAAAA = q.Type == 28;

                if (useLocal)
                {
                    if (isAAAA && !split.CanPinLocalV6)
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

                            if (isAAAA && answer is not null)
                            {
                                answer = BuildEmptyResponse(query, q.QuestionLength);
                            }
                        }
                        else
                        {

                            seen = ExtractAnswerAddresses(answer);
                        }
                    }
                }
                else
                {
                    answer = await QueryUpstreamAsync(query, ct);
                    if (answer is not null && !split.ExcludeMode)
                    {

                        seen = ExtractAnswerAddresses(answer);
                    }
                }
            }
            else
            {
                answer = await QueryUpstreamAsync(query, ct);
            }

            if (answer is null) return;
            Interlocked.Increment(ref _handled);

            foreach (var addr in seen)
            {
                try { split?.AddressSeen?.Invoke(addr, parsed?.Name ?? ""); } catch { }
            }

            await listener.SendAsync(answer, answer.Length, client);
        }
        catch
        {

        }
    }

    public void Dispose() => Stop();
}
