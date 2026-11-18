using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Se7enPro.Services.Foxy;

public sealed class FoxyH2Session : IAsyncDisposable
{
    private static readonly byte[] H2Preface = Encoding.ASCII.GetBytes("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n");
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(15);

    private readonly ILogger _logger;
    private readonly string _edgeHost;
    private readonly int _edgePort;
    private readonly UpstreamProxySettings? _proxySettings;
    private readonly IPAddress? _avoidAddress;

    private TcpClient? _tcpClient;
    private SslStream? _sslStream;

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<int, FoxyH2Stream> _activeStreams = new();
    private readonly ConcurrentDictionary<int, byte[]> _pendingHeaderBlocks = new();
    private readonly List<(string Name, string Value)> _hpackDynamicTable = new();
    private readonly CancellationTokenSource _sessionCts = new();
    private readonly object _windowLock = new();
    private readonly SemaphoreSlim _sendWindowSignal = new(0, 1);
    private int _peerInitialWindowSize = 65535;
    private long _connSendWindow = 65535;

    private int _nextStreamId = 1;
    private volatile bool _disposed;
    private int _failedRaised;
    private volatile bool _goAwayReceived;
    private volatile bool _draining;
    private volatile bool _goAwayFatal;
    private volatile string _proxyPassToken = "";
    private int _pingsOutstanding;
    private int _drainNotified;

    public string EdgeHost => _edgeHost;

    public int ActiveStreamCount => _activeStreams.Count;

    public bool IsConnected =>
        !_disposed && !_goAwayReceived && _tcpClient is { Connected: true } && _sslStream is not null;

    public bool CanOpenNewStreams =>
        !_disposed && !_draining && !_goAwayFatal && _tcpClient is { Connected: true } && _sslStream is not null;

    public IPAddress? RemoteAddress => (_tcpClient?.Client?.RemoteEndPoint as IPEndPoint)?.Address;

    public event EventHandler<string>? SessionDraining;
    public event EventHandler<string>? SessionFailed;

    public FoxyH2Session(
        string edgeHost,
        int edgePort,
        UpstreamProxySettings? proxySettings,
        ILogger logger,
        IPAddress? avoidAddress = null)
    {
        _edgeHost = edgeHost;
        _edgePort = edgePort > 0 ? edgePort : 2499;
        _proxySettings = proxySettings;
        _logger = logger;
        _avoidAddress = avoidAddress;
    }

    private void SignalSendWindow()
    {
        if (_sendWindowSignal.CurrentCount == 0)
        {
            try { _sendWindowSignal.Release(); } catch (SemaphoreFullException) { }
        }
    }

    internal void RaiseFailed(string reason)
    {
        if (_disposed || Interlocked.Exchange(ref _failedRaised, 1) == 1) return;
        _logger.LogWarning("HTTP/2 tunnel session to {Host} ended: {Reason}", _edgeHost, reason);
        try
        {
            SessionFailed?.Invoke(this, reason);
        }
        catch
        {
        }
    }

    public void UpdateProxyPassToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return;
        _proxyPassToken = token;
        _logger.LogInformation("Proxy pass updated in-place for the session to {Host}.", _edgeHost);
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Connecting HTTP/2 tunnel to Fastly edge {Host}:{Port}...", _edgeHost, _edgePort);

        _tcpClient = new TcpClient { NoDelay = true };
        try
        {
            _tcpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            _tcpClient.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 15);
            _tcpClient.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 5);
            _tcpClient.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
        }
        catch
        {
        }
        if (_proxySettings is { Enabled: true, Host.Length: > 0 })
        {
            await ConnectViaProxyAsync(_tcpClient, _proxySettings, _edgeHost, _edgePort, ct).ConfigureAwait(false);
        }
        else
        {
            var addresses = await FoxyNetwork.ResolveHostAddressesAsync(_edgeHost, ct).ConfigureAwait(false);
            if (_avoidAddress is not null && addresses.Length > 1)
                addresses = addresses.OrderBy(ip => ip.Equals(_avoidAddress) ? 1 : 0).ToArray();
            if (addresses.Length == 0)
                await _tcpClient.ConnectAsync(_edgeHost, _edgePort, ct).ConfigureAwait(false);
            else
                await _tcpClient.ConnectAsync(addresses, _edgePort, ct).ConfigureAwait(false);
        }

        _sslStream = new SslStream(_tcpClient.GetStream(), leaveInnerStreamOpen: false, ValidateEdgeCertificate);
        await _sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = _edgeHost,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            ApplicationProtocols = [SslApplicationProtocol.Http2],
        }, ct).ConfigureAwait(false);

        if (_sslStream.NegotiatedApplicationProtocol != SslApplicationProtocol.Http2)
        {
            _logger.LogWarning("Edge negotiated ALPN '{Alpn}' instead of h2; sending the HTTP/2 preface anyway.",
                _sslStream.NegotiatedApplicationProtocol);
        }

        await _sslStream.WriteAsync(H2Preface, ct).ConfigureAwait(false);

        await WriteFrameAsync(4, 0, 0,
            new byte[] { 0, 2, 0, 0, 0, 0, 0, 4, 1, 0, 0, 0, 0, 5, 0, 4, 0, 0 }, ct).ConfigureAwait(false);
        await WriteFrameAsync(8, 0, 0, new byte[] { 0, 254, 0, 0 }, ct).ConfigureAwait(false);

        _ = Task.Run(ReaderLoopAsync);
        _ = Task.Run(KeepAliveLoopAsync);

        _logger.LogInformation("HTTP/2 tunnel session established with {Host}:{Port}.", _edgeHost, _edgePort);
    }

    private bool ValidateEdgeCertificate(object sender, System.Security.Cryptography.X509Certificates.X509Certificate? cert,
        System.Security.Cryptography.X509Certificates.X509Chain? chain, SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None) return true;
        _logger.LogWarning("Rejected TLS certificate for edge {Host} ({Errors}).", _edgeHost, errors);
        return false;
    }

    private async Task KeepAliveLoopAsync()
    {
        var ct = _sessionCts.Token;
        try
        {
            while (!ct.IsCancellationRequested && IsConnected)
            {
                await Task.Delay(KeepAliveInterval, ct).ConfigureAwait(false);
                if (!IsConnected) break;

                if (Interlocked.Exchange(ref _pingsOutstanding, 0) != 0)
                {
                    RaiseFailed("edge stopped answering PING - connection is dead");
                    break;
                }

                Interlocked.Increment(ref _pingsOutstanding);
                await WriteFrameAsync(6, 0, 0, new byte[8], ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!_disposed) RaiseFailed("keepalive failed: " + ex.Message);
        }
    }

    public async Task<Stream> OpenConnectStreamAsync(
        string targetHost, int targetPort, string? proxyPassToken = null, CancellationToken ct = default)
    {
        if (!CanOpenNewStreams)
        {
            if (!_draining) RaiseFailed("HTTP/2 session is not accepting new streams (closed).");
            throw new InvalidOperationException("HTTP/2 session is not accepting new streams.");
        }

        var token = string.IsNullOrWhiteSpace(proxyPassToken) ? _proxyPassToken : proxyPassToken;
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("No proxy pass token available for CONNECT.");

        var authority = targetHost.Contains(':') ? $"[{targetHost}]:{targetPort}" : $"{targetHost}:{targetPort}";

        int streamId;
        FoxyH2Stream channel;
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            streamId = Interlocked.Add(ref _nextStreamId, 2) - 2;
            if (streamId <= 0) throw new InvalidOperationException("HTTP/2 stream ids exhausted; reconnect required.");

            channel = new FoxyH2Stream(streamId, this, _logger, Volatile.Read(ref _peerInitialWindowSize));
            _activeStreams[streamId] = channel;
            await WriteFrameLockedAsync(1, 4, streamId,
                HpackCodec.EncodeConnectHeaders(authority, token), ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(12));
            var status = await channel.WaitForStatusAsync(cts.Token).ConfigureAwait(false);

            switch (status)
            {
                case 429:
                    throw new FirefoxQuotaExceededException("Account proxy quota exceeded (HTTP 429).");
                case 403:
                    throw new FirefoxTargetRejectedException($"Edge rejected tunnel target (HTTP {status}).");
                case 401 or 407:
                    throw new FirefoxAuthTokenInvalidException($"Edge rejected proxy pass (HTTP {status}).");
            }

            if (status is < 200 or >= 300)
                throw new InvalidOperationException($"Edge rejected CONNECT to {authority} with HTTP {status}.");

            return channel.AsStream();
        }
        catch
        {
            _activeStreams.TryRemove(streamId, out _);
            channel.Dispose();
            throw;
        }
    }

    internal async Task SendDataFrameAsync(int streamId, ReadOnlyMemory<byte> data, bool endStream, CancellationToken ct)
    {
        if (data.Length == 0)
        {
            await WriteFrameAsync(0, endStream ? (byte)1 : (byte)0, streamId, data, ct).ConfigureAwait(false);
            return;
        }

        var offset = 0;
        while (offset < data.Length)
        {
            var remaining = data.Length - offset;
            int chunkLen = remaining;
            if (_activeStreams.TryGetValue(streamId, out var streamObj))
            {
                var windowAllocated = false;
                for (var waitAttempt = 0; waitAttempt < 16; waitAttempt++)
                {
                    lock (_windowLock)
                    {
                        var avail = Math.Min(_connSendWindow, streamObj.SendWindow);
                        if (avail > 0)
                        {
                            chunkLen = (int)Math.Min(remaining, avail);
                            _connSendWindow -= chunkLen;
                            streamObj.AddSendWindow(-chunkLen);
                            windowAllocated = true;
                            break;
                        }
                    }

                    using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    waitCts.CancelAfter(TimeSpan.FromMilliseconds(500));
                    try
                    {
                        await _sendWindowSignal.WaitAsync(waitCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                    }
                }

                if (!windowAllocated)
                {
                    var fallbackSlice = Math.Min(remaining, 16384);
                    lock (_windowLock)
                    {
                        var avail = Math.Min(_connSendWindow, streamObj.SendWindow);
                        if (avail <= 0)
                        {
                            _connSendWindow = Math.Max(_connSendWindow, fallbackSlice);
                            streamObj.EnsurePositiveSendWindow(fallbackSlice);
                        }
                        chunkLen = (int)Math.Min(remaining, Math.Min(_connSendWindow, streamObj.SendWindow));
                        _connSendWindow -= chunkLen;
                        streamObj.AddSendWindow(-chunkLen);
                    }
                }
            }

            var isFinalChunk = (offset + chunkLen) >= data.Length;
            var flags = (isFinalChunk && endStream) ? (byte)1 : (byte)0;
            await WriteFrameAsync(0, flags, streamId, data.Slice(offset, chunkLen), ct).ConfigureAwait(false);
            offset += chunkLen;
        }
    }

    internal Task SendWindowUpdateAsync(int streamId, int delta, CancellationToken ct)
    {
        if (delta <= 0) return Task.CompletedTask;
        return WriteFrameAsync(8, 0, streamId, EncodeWindowUpdatePayload(delta), ct);
    }

    internal async Task SendWindowUpdatePairAsync(int streamId, int delta)
    {
        if (delta <= 0 || _disposed || _sslStream is null) return;
        var payload = EncodeWindowUpdatePayload(delta);
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_sslStream is null || _disposed) return;
            await WriteFrameRawAsync(_sslStream, 8, 0, 0, payload, CancellationToken.None).ConfigureAwait(false);
            await WriteFrameRawAsync(_sslStream, 8, 0, streamId, payload, CancellationToken.None).ConfigureAwait(false);
            await _sslStream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
        }
        finally
        {
            _writeLock.Release();
        }
    }

    internal int CloseOldestStreams(int maxCount)
    {
        if (maxCount <= 0) return 0;
        var closed = 0;
        foreach (var id in _activeStreams.Keys.OrderBy(k => k).Take(maxCount))
        {
            if (_activeStreams.TryGetValue(id, out var stream))
            {
                stream.Dispose();
                closed++;
            }
        }
        return closed;
    }

    private static byte[] EncodeWindowUpdatePayload(int delta) => new[]
    {
        (byte)((delta >> 24) & 0x7F), (byte)((delta >> 16) & 0xFF),
        (byte)((delta >> 8) & 0xFF), (byte)(delta & 0xFF),
    };

    internal Task SendRstStreamAsync(int streamId, uint errorCode, CancellationToken ct) =>
        WriteFrameAsync(3, 0, streamId,
            new[]
            {
                (byte)((errorCode >> 24) & 0xFF), (byte)((errorCode >> 16) & 0xFF),
                (byte)((errorCode >> 8) & 0xFF), (byte)(errorCode & 0xFF),
            }, ct);

    internal void RemoveStream(int streamId)
    {
        _activeStreams.TryRemove(streamId, out _);
        _pendingHeaderBlocks.TryRemove(streamId, out _);
        if (_draining && _activeStreams.IsEmpty)
        {
            _goAwayReceived = true;
            RaiseFailed("edge finished its graceful drain and no streams remain");
        }
    }

    private async Task WriteFrameAsync(byte type, byte flags, int streamId, ReadOnlyMemory<byte> payload,
        CancellationToken ct)
    {
        if (_sslStream is null) return;
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await WriteFrameLockedAsync(type, flags, streamId, payload, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task WriteFrameLockedAsync(byte type, byte flags, int streamId, ReadOnlyMemory<byte> payload,
        CancellationToken ct)
    {
        var stream = _sslStream;
        if (stream is null) return;

        await WriteFrameRawAsync(stream, type, flags, streamId, payload, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task WriteFrameRawAsync(
        SslStream stream, byte type, byte flags, int streamId, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        var length = payload.Length;
        await stream.WriteAsync(new[]
        {
            (byte)((length >> 16) & 0xFF), (byte)((length >> 8) & 0xFF), (byte)(length & 0xFF),
            type, flags,
            (byte)((streamId >> 24) & 0x7F), (byte)((streamId >> 16) & 0xFF),
            (byte)((streamId >> 8) & 0xFF), (byte)(streamId & 0xFF),
        }, ct).ConfigureAwait(false);
        if (length > 0) await stream.WriteAsync(payload, ct).ConfigureAwait(false);
    }

    private async Task ReaderLoopAsync()
    {
        var header = new byte[9];
        var ct = _sessionCts.Token;
        try
        {
            while (!ct.IsCancellationRequested && _sslStream is not null)
            {
                await ReadExactAsync(_sslStream, header, 9, ct).ConfigureAwait(false);
                var length = (header[0] << 16) | (header[1] << 8) | header[2];
                var type = header[3];
                var flags = header[4];
                var streamId = ((header[5] & 0x7F) << 24) | (header[6] << 16) | (header[7] << 8) | header[8];

                var payload = Array.Empty<byte>();
                if (length > 0)
                {
                    payload = new byte[length];
                    await ReadExactAsync(_sslStream, payload, length, ct).ConfigureAwait(false);
                }

                ProcessIncomingFrame(type, flags, streamId, payload);
                if (_goAwayReceived) break;
            }
        }
        catch (Exception ex) when (!_disposed)
        {
            _logger.LogDebug(ex, "HTTP/2 tunnel reader ended: {Message}", ex.Message);
            RaiseFailed(ex.Message);
        }
        finally
        {
            await DisposeAsync().ConfigureAwait(false);
        }
    }

    private void ProcessIncomingFrame(byte type, byte flags, int streamId, byte[] payload)
    {
        switch (type)
        {
            case 0:
            {
                var padded = (flags & 8) != 0 && payload.Length != 0 ? payload[0] : 0;
                if (_activeStreams.TryGetValue(streamId, out var channel))
                {
                    var isEnd = (flags & 1) != 0;
                    var padBytes = (flags & 8) != 0 ? 1 : 0;
                    var dataLength = payload.Length - padBytes - padded;
                    var data = dataLength > 0 && padBytes + dataLength <= payload.Length
                        ? payload.AsSpan(padBytes, dataLength).ToArray()
                        : Array.Empty<byte>();
                    var nonDataBytes = payload.Length - data.Length;
                    if (nonDataBytes > 0)
                    {
                        _ = SendWindowUpdateAsync(0, nonDataBytes, CancellationToken.None);
                        _ = SendWindowUpdateAsync(streamId, nonDataBytes, CancellationToken.None);
                    }
                    channel.OnDataReceived(data, isEnd);
                }
                else if (payload.Length != 0)
                {
                    _ = SendWindowUpdateAsync(0, payload.Length, CancellationToken.None);
                }
                break;
            }
            case 1:
            {
                if (!_activeStreams.TryGetValue(streamId, out var channel)) break;

                var pos = 0;
                var remaining = payload.Length;
                if ((flags & 8) != 0 && remaining > 0)
                {
                    var pad = payload[pos++];
                    remaining -= pad + 1;
                }
                if ((flags & 0x20) != 0 && remaining >= 5)
                {
                    pos += 5;
                    remaining -= 5;
                }

                var endHeaders = (flags & 0x04) != 0;
                var block = remaining > 0 && pos + remaining <= payload.Length
                    ? payload.AsSpan(pos, remaining).ToArray()
                    : Array.Empty<byte>();
                if (!endHeaders)
                {
                    _pendingHeaderBlocks.AddOrUpdate(streamId,
                        block,
                        (id, existing) => existing.Concat(block).ToArray());
                    break;
                }
                int status;
                try
                {
                    status = HpackCodec.DecodeStatusCode(block, _hpackDynamicTable);
                }
                catch
                {
                    status = 0;
                }
                channel.OnHeadersReceived(status);
                break;
            }
            case 9:
            {
                if (_activeStreams.TryGetValue(streamId, out var channel))
                {
                    var combined = _pendingHeaderBlocks.AddOrUpdate(streamId,
                        payload,
                        (id, existing) => existing.Concat(payload).ToArray());
                    if ((flags & 0x04) != 0)
                    {
                        int status;
                        try { status = HpackCodec.DecodeStatusCode(combined, _hpackDynamicTable); }
                        catch { status = 0; }
                        _pendingHeaderBlocks.TryRemove(streamId, out _);
                        if (status != 0 || combined.Length > 0) channel.OnHeadersReceived(status);
                    }
                }
                break;
            }
            case 3:
                _pendingHeaderBlocks.TryRemove(streamId, out _);
                if (_activeStreams.TryGetValue(streamId, out var reset)) reset.OnResetReceived();
                break;
            case 4:
                if ((flags & 1) == 0)
                {
                    for (var i = 0; i + 6 <= payload.Length; i += 6)
                    {
                        var settingId = (payload[i] << 8) | payload[i + 1];
                        var val = (uint)((payload[i + 2] << 24) | (payload[i + 3] << 16) | (payload[i + 4] << 8) | payload[i + 5]);
                        if (settingId == 0x4 && val <= int.MaxValue)
                        {
                            var newInit = (int)val;
                            lock (_windowLock)
                            {
                                var delta = newInit - _peerInitialWindowSize;
                                _peerInitialWindowSize = newInit;
                                foreach (var s in _activeStreams.Values)
                                {
                                    s.AddSendWindow(delta);
                                }
                            }
                            SignalSendWindow();
                        }
                    }
                    _ = WriteFrameAsync(4, 1, 0, ReadOnlyMemory<byte>.Empty, CancellationToken.None);
                }
                break;
            case 8:
                if (payload.Length >= 4)
                {
                    var increment = ((payload[0] & 0x7F) << 24) | (payload[1] << 16) | (payload[2] << 8) | payload[3];
                    if (increment > 0)
                    {
                        lock (_windowLock)
                        {
                            if (streamId == 0)
                            {
                                _connSendWindow += increment;
                            }
                            else if (_activeStreams.TryGetValue(streamId, out var s))
                            {
                                s.AddSendWindow(increment);
                            }
                        }
                        SignalSendWindow();
                    }
                }
                break;
            case 6:
                if ((flags & 1) == 0)
                    _ = WriteFrameAsync(6, 1, 0, payload, CancellationToken.None);
                else
                    Interlocked.Exchange(ref _pingsOutstanding, 0);
                break;
            case 7:
            {
                uint lastStreamId = 0;
                uint errorCode = 0;
                if (payload.Length >= 4)
                    lastStreamId = (uint)(((payload[0] << 24) | (payload[1] << 16) | (payload[2] << 8) | payload[3])
                                          & 0x7FFFFFFF);
                if (payload.Length >= 8)
                    errorCode = (uint)((payload[4] << 24) | (payload[5] << 16) | (payload[6] << 8) | payload[7]);

                if (errorCode is 0 or 5)
                {
                    _draining = true;
                    foreach (var id in _activeStreams.Keys.ToArray())
                    {
                        if (id > (int)lastStreamId && _activeStreams.TryRemove(id, out var failed))
                            failed.FailUnprocessed($"stream {id} was not processed by the edge (last {lastStreamId})");
                    }

                    var drainReason = DescribeGoAway(lastStreamId, errorCode, _activeStreams.Count);
                    _logger.LogInformation(
                        "Edge {Host} is draining gracefully (last-stream-id={Last}, error={Error}); keeping {Open} live stream(s).",
                        _edgeHost, lastStreamId, errorCode, _activeStreams.Count);

                    if (_activeStreams.IsEmpty)
                    {
                        _goAwayReceived = true;
                        RaiseFailed("edge finished its graceful drain and no streams remain");
                        break;
                    }

                    if (Interlocked.Exchange(ref _drainNotified, 1) == 0)
                    {
                        try { SessionDraining?.Invoke(this, drainReason); } catch { }
                    }
                }
                else
                {
                    var reason = DescribeGoAway(lastStreamId, errorCode, _activeStreams.Count);
                    _goAwayReceived = true;
                    _goAwayFatal = true;
                    _logger.LogWarning("HTTP/2 GOAWAY from {Host}: {Reason}", _edgeHost, reason);
                    RaiseFailed(reason);
                }
                break;
            }
        }
    }

    private static string DescribeGoAway(uint lastStreamId, uint errorCode, int openStreams) => errorCode != 0
        ? $"edge sent GOAWAY (error {DescribeHttp2Error(errorCode)}, last-stream-id {lastStreamId}, {openStreams} open)"
        : lastStreamId == 0
            ? "edge sent GOAWAY (no error, idle teardown)"
            : $"edge sent GOAWAY (no error, last-stream-id {lastStreamId}, {openStreams} open - graceful drain)";

    private static string DescribeHttp2Error(uint code) => code switch
    {
        0 => "NO_ERROR", 1 => "PROTOCOL_ERROR", 2 => "INTERNAL_ERROR", 3 => "FLOW_CONTROL_ERROR",
        4 => "SETTINGS_TIMEOUT", 5 => "STREAM_CLOSED", 6 => "FRAME_SIZE_ERROR", 7 => "REFUSED_STREAM",
        8 => "CANCEL", 9 => "COMPRESSION_ERROR", 10 => "CONNECT_ERROR", 11 => "ENHANCE_YOUR_CALM",
        12 => "INADEQUATE_SECURITY", 13 => "HTTP_1_1_REQUIRED", _ => $"0x{code:X}",
    };

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, int count, CancellationToken ct)
    {
        var read = 0;
        while (read < count)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read, count - read), ct).ConfigureAwait(false);
            if (n == 0) throw new EndOfStreamException("HTTP/2 connection reached EOF.");
            read += n;
        }
    }

    private static async Task ConnectViaProxyAsync(
        TcpClient client, UpstreamProxySettings proxy, string destHost, int destPort, CancellationToken ct)
    {
        var addresses = await FoxyNetwork.ResolveHostAddressesAsync(proxy.Host, ct).ConfigureAwait(false);
        if (addresses.Length == 0)
            await client.ConnectAsync(proxy.Host, proxy.Port, ct).ConfigureAwait(false);
        else
            await client.ConnectAsync(addresses, proxy.Port, ct).ConfigureAwait(false);

        var stream = client.GetStream();

        if (string.Equals(proxy.Type, "http", StringComparison.OrdinalIgnoreCase))
        {
            var request = $"CONNECT {destHost}:{destPort} HTTP/1.1\r\nHost: {destHost}:{destPort}\r\n";
            if (!string.IsNullOrEmpty(proxy.Username))
            {
                var basic = Convert.ToBase64String(
                    Encoding.UTF8.GetBytes(proxy.Username + ":" + proxy.Password));
                request += "Proxy-Authorization: Basic " + basic + "\r\n";
            }
            request += "\r\n";

            await stream.WriteAsync(Encoding.ASCII.GetBytes(request), ct).ConfigureAwait(false);
            var pending = new List<byte>();
            var lineBuf = new List<byte>();
            var isFirstLine = true;
            while (true)
            {
                var b = new byte[1];
                var n = await stream.ReadAsync(b, ct).ConfigureAwait(false);
                if (n == 0) break;
                lineBuf.Add(b[0]);
                if (lineBuf.Count >= 2 && lineBuf[lineBuf.Count - 2] == '\r' && b[0] == '\n')
                {
                    var line = Encoding.ASCII.GetString(lineBuf.ToArray());
                    if (line.TrimEnd('\r', '\n').Length == 0) break;
                    if (isFirstLine)
                    {
                        isFirstLine = false;
                        if (!line.Contains(" 200", StringComparison.Ordinal))
                            throw new IOException("HTTP proxy rejected the CONNECT tunnel: " + line.Trim());
                    }
                    lineBuf.Clear();
                }
            }
            return;
        }

        var hasAuth = !string.IsNullOrEmpty(proxy.Username);
        await stream.WriteAsync(hasAuth ? new byte[] { 5, 2, 0, 2 } : new byte[] { 5, 1, 0 }, ct)
            .ConfigureAwait(false);

        var method = new byte[2];
        await ReadExactAsync(stream, method, 2, ct).ConfigureAwait(false);
        if (method[0] != 5) throw new IOException("Invalid SOCKS5 greeting response.");

        if (method[1] == 2 && hasAuth)
        {
            var user = Encoding.UTF8.GetBytes(proxy.Username!);
            var pass = Encoding.UTF8.GetBytes(proxy.Password ?? "");
            var auth = new byte[3 + user.Length + pass.Length];
            auth[0] = 1;
            auth[1] = (byte)user.Length;
            Buffer.BlockCopy(user, 0, auth, 2, user.Length);
            auth[2 + user.Length] = (byte)pass.Length;
            Buffer.BlockCopy(pass, 0, auth, 3 + user.Length, pass.Length);

            await stream.WriteAsync(auth, ct).ConfigureAwait(false);
            var authResp = new byte[2];
            await ReadExactAsync(stream, authResp, 2, ct).ConfigureAwait(false);
            if (authResp[1] != 0) throw new IOException("SOCKS5 proxy authentication failed.");
        }
        else if (method[1] != 0)
        {
            throw new IOException($"SOCKS5 proxy rejected authentication method {method[1]}.");
        }

        var host = Encoding.ASCII.GetBytes(destHost);
        var connect = new byte[5 + host.Length + 2];
        connect[0] = 5;
        connect[1] = 1;
        connect[2] = 0;
        connect[3] = 3;
        connect[4] = (byte)host.Length;
        Buffer.BlockCopy(host, 0, connect, 5, host.Length);
        connect[5 + host.Length] = (byte)(destPort >> 8);
        connect[6 + host.Length] = (byte)(destPort & 0xFF);

        await stream.WriteAsync(connect, ct).ConfigureAwait(false);

        var reply = new byte[4];
        await ReadExactAsync(stream, reply, 4, ct).ConfigureAwait(false);
        if (reply[1] != 0) throw new IOException($"SOCKS5 CONNECT failed with status {reply[1]}.");

        var tail = reply[3] switch
        {
            1 => 4,
            4 => 16,
            3 => await ReadLengthByteAsync(stream, ct).ConfigureAwait(false),
            _ => 0,
        };
        if (tail > 0) await ReadExactAsync(stream, new byte[tail + 2], tail + 2, ct).ConfigureAwait(false);
    }

    private static async Task<int> ReadLengthByteAsync(Stream stream, CancellationToken ct)
    {
        var b = new byte[1];
        await ReadExactAsync(stream, b, 1, ct).ConfigureAwait(false);
        return b[0];
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _sessionCts.Cancel();
        _sessionCts.Dispose();

        foreach (var stream in _activeStreams.Values) stream.Dispose();
        _activeStreams.Clear();

        if (_sslStream is not null)
        {
            try
            {
                await _sslStream.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
            }
            _sslStream = null;
        }

        if (_tcpClient is not null)
        {
            try
            {
                _tcpClient.Dispose();
            }
            catch
            {
            }
            _tcpClient = null;
        }

        _writeLock.Dispose();
        _sendWindowSignal.Dispose();
    }
}
