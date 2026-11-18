using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Se7enPro.Services.Foxy;

internal sealed class FoxyH2Stream : IDisposable
{
    private sealed class StreamWrapper : Stream
    {
        private readonly FoxyH2Stream _stream;

        public StreamWrapper(FoxyH2Stream stream) => _stream = stream;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) =>
            _stream.ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).GetAwaiter().GetResult();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            _stream.ReadAsync(buffer, ct);

        public override void Write(byte[] buffer, int offset, int count) =>
            _stream.WriteAsync(buffer.AsMemory(offset, count), CancellationToken.None).GetAwaiter().GetResult();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) =>
            _stream.WriteAsync(buffer, ct);

        public ValueTask SendEndStreamAsync(CancellationToken ct) => _stream.SendEndStreamAsync(ct);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _stream.Dispose();
            base.Dispose(disposing);
        }
    }

    private const int MaxDataChunk = 16384;
    private const int WindowUpdateBatchBytes = 16384;

    private readonly int _streamId;
    private readonly FoxyH2Session _session;
    private readonly ILogger _logger;

    private readonly TaskCompletionSource<int> _statusTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly object _inboundLock = new();
    private readonly Queue<ArraySegment<byte>> _inboundQueue = new();
    private readonly SemaphoreSlim _inboundSignal = new(0);

    private ArraySegment<byte> _currentChunk;
    private bool _completed;
    private Exception? _error;
    private volatile bool _disposed;
    private volatile bool _writeClosed;
    private volatile bool _endStreamSent;
    private long _sendWindow;
    private int _pendingWindowUpdate;

    public FoxyH2Stream(int streamId, FoxyH2Session session, ILogger logger, int initialSendWindow = 65535)
    {
        _streamId = streamId;
        _session = session;
        _logger = logger;
        _sendWindow = initialSendWindow;
    }

    internal long SendWindow => Volatile.Read(ref _sendWindow);

    internal void AddSendWindow(long delta) => Interlocked.Add(ref _sendWindow, delta);

    internal void EnsurePositiveSendWindow(long minWindow)
    {
        var cur = Volatile.Read(ref _sendWindow);
        if (cur < minWindow) Volatile.Write(ref _sendWindow, minWindow);
    }

    public Task<int> WaitForStatusAsync(CancellationToken ct) => _statusTcs.Task.WaitAsync(ct);

    public void OnHeadersReceived(int status) => _statusTcs.TrySetResult(status);

    public void FailUnprocessed(string reason)
    {
        _writeClosed = true;
        var ex = new IOException(reason);
        _statusTcs.TrySetException(ex);
        lock (_inboundLock)
        {
            _error = ex;
            _inboundSignal.Release();
        }
    }

    public void OnDataReceived(byte[] data, bool isEnd)
    {
        if (data.Length != 0)
        {
            lock (_inboundLock)
            {
                _inboundQueue.Enqueue(new ArraySegment<byte>(data));
                _inboundSignal.Release();
            }
        }

        if (isEnd)
        {
            lock (_inboundLock)
            {
                _completed = true;
                _inboundSignal.Release();
            }
            _session.RemoveStream(_streamId);
        }
    }

    public void OnResetReceived()
    {
        var ex = new IOException("HTTP/2 stream was reset by the remote peer.");
        _writeClosed = true;
        _statusTcs.TrySetException(ex);
        lock (_inboundLock)
        {
            _error = ex;
            _inboundSignal.Release();
        }
    }

    internal async ValueTask SendEndStreamAsync(CancellationToken ct)
    {
        if (_disposed || _writeClosed || _endStreamSent) return;
        _endStreamSent = true;
        try
        {
            await _session.SendDataFrameAsync(_streamId, ReadOnlyMemory<byte>.Empty, endStream: true, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not send END_STREAM on stream {StreamId}", _streamId);
        }
    }

    public Stream AsStream() => new StreamWrapper(this);

    public static async Task SendEndStreamIfHttp2Async(Stream stream, CancellationToken ct)
    {
        if (stream is StreamWrapper wrapper)
            await wrapper.SendEndStreamAsync(ct).ConfigureAwait(false);
    }

    internal async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
    {
        if (_disposed) return 0;

        while (true)
        {
            var take = 0;
            var queueEmpty = false;
            lock (_inboundLock)
            {
                if (_error is not null) throw _error;

                if (_currentChunk.Count > 0)
                {
                    take = Math.Min(_currentChunk.Count, buffer.Length);
                    _currentChunk.AsSpan(0, take).CopyTo(buffer.Span);
                    _currentChunk = _currentChunk.Slice(take);
                }
                else if (_inboundQueue.Count > 0)
                {
                    _currentChunk = _inboundQueue.Dequeue();
                    take = Math.Min(_currentChunk.Count, buffer.Length);
                    _currentChunk.AsSpan(0, take).CopyTo(buffer.Span);
                    _currentChunk = _currentChunk.Slice(take);
                }
                else if (_completed)
                {
                    return 0;
                }

                queueEmpty = _currentChunk.Count == 0 && _inboundQueue.Count == 0;
            }

            if (take > 0)
            {
                var pending = Interlocked.Add(ref _pendingWindowUpdate, take);
                if (pending >= WindowUpdateBatchBytes || (queueEmpty && pending >= 4096))
                {
                    var flush = Interlocked.Exchange(ref _pendingWindowUpdate, 0);
                    if (flush > 0)
                        _ = _session.SendWindowUpdatePairAsync(_streamId, flush);
                }
                return take;
            }

            await _inboundSignal.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    internal async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct)
    {
        for (var offset = 0; offset < buffer.Length; offset += MaxDataChunk)
        {
            if (_disposed || _writeClosed) return;
            var length = Math.Min(buffer.Length - offset, MaxDataChunk);
            await _session.SendDataFrameAsync(_streamId, buffer.Slice(offset, length), endStream: false, ct)
                .ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        int unconsumed = 0;
        bool wasCompleted;
        lock (_inboundLock)
        {
            wasCompleted = _completed;
            unconsumed = _currentChunk.Count;
            _currentChunk = default;
            while (_inboundQueue.Count > 0)
            {
                unconsumed += _inboundQueue.Dequeue().Count;
            }
            _completed = true;
            _inboundSignal.Release();
        }
        var connCredit = unconsumed + Math.Max(0, Interlocked.Exchange(ref _pendingWindowUpdate, 0));
        if (connCredit > 0)
        {
            _ = _session.SendWindowUpdateAsync(0, connCredit, CancellationToken.None);
        }
        _session.RemoveStream(_streamId);
        if (!(wasCompleted && _endStreamSent))
        {
            _ = _session.SendRstStreamAsync(_streamId, 0, CancellationToken.None);
        }
    }
}
