using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Se7enPro.Services.Foxy;

public enum HelloSplitMode
{
    Off = 0,
    Sni = 1,
    FirstByte = 2,
    Record = 3,
}

public static class HelloSplitHelper
{
    private const int TlsRecordHeaderLength = 5;

    public static bool IsClientHello(ReadOnlySpan<byte> data) =>
        data.Length > TlsRecordHeaderLength &&
        data[0] == 0x16 &&
        data[1] == 0x03 &&
        data[5] == 0x01;

    public static int FindSniServerNameOffset(ReadOnlySpan<byte> p)
    {
        if (!IsClientHello(p))
            return -1;

        var at = 5 + 4 + 2 + 32;

        static bool SkipLengthPrefixed(ReadOnlySpan<byte> buf, ref int pos, int lengthBytes)
        {
            if (pos + lengthBytes > buf.Length)
                return false;
            var n = 0;
            for (var i = 0; i < lengthBytes; i++)
                n = (n << 8) | buf[pos + i];
            pos += lengthBytes + n;
            return pos <= buf.Length;
        }

        if (!SkipLengthPrefixed(p, ref at, 1) ||
            !SkipLengthPrefixed(p, ref at, 2) ||
            !SkipLengthPrefixed(p, ref at, 1) ||
            at + 2 > p.Length)
        {
            return -1;
        }

        var extensionsLength = BinaryPrimitives.ReadUInt16BigEndian(p.Slice(at, 2));
        at += 2;
        var end = Math.Min(at + extensionsLength, p.Length);

        while (at + 4 <= end)
        {
            var kind = BinaryPrimitives.ReadUInt16BigEndian(p.Slice(at, 2));
            var size = BinaryPrimitives.ReadUInt16BigEndian(p.Slice(at + 2, 2));
            var body = at + 4;
            if (body + size > end)
                return -1;

            if (kind == 0)
            {
                if (size < 6 || p[body + 2] != 0)
                    return -1;
                var nameLen = BinaryPrimitives.ReadUInt16BigEndian(p.Slice(body + 3, 2));
                if (nameLen == 0 || body + 5 + nameLen > end)
                    return -1;
                return body + 5;
            }

            at = body + size;
        }

        return -1;
    }

    public static IReadOnlyList<byte[]> BuildSegments(ReadOnlySpan<byte> p, HelloSplitMode mode)
    {
        if (mode == HelloSplitMode.Off || !IsClientHello(p) || p.Length <= 1)
            return new[] { p.ToArray() };

        if (mode == HelloSplitMode.FirstByte)
            return new[] { p[..1].ToArray(), p[1..].ToArray() };

        var sniStart = FindSniServerNameOffset(p);
        if (sniStart <= 0 || sniStart + 1 >= p.Length)
        {
            return new[] { p[..1].ToArray(), p[1..].ToArray() };
        }

        if (mode == HelloSplitMode.Record)
        {
            var recordPayloadLen = BinaryPrimitives.ReadUInt16BigEndian(p.Slice(3, 2));
            if (recordPayloadLen == p.Length - TlsRecordHeaderLength)
            {
                var cut = sniStart + 1;
                var rec1 = new byte[cut];
                p[..3].CopyTo(rec1);
                BinaryPrimitives.WriteUInt16BigEndian(rec1.AsSpan(3, 2), (ushort)(cut - TlsRecordHeaderLength));
                p.Slice(TlsRecordHeaderLength, cut - TlsRecordHeaderLength).CopyTo(rec1.AsSpan(TlsRecordHeaderLength));

                var secondPayloadLen = p.Length - cut;
                var rec2 = new byte[TlsRecordHeaderLength + secondPayloadLen];
                p[..3].CopyTo(rec2);
                BinaryPrimitives.WriteUInt16BigEndian(rec2.AsSpan(3, 2), (ushort)secondPayloadLen);
                p[cut..].CopyTo(rec2.AsSpan(TlsRecordHeaderLength));

                return new[] { rec1, rec2 };
            }
        }

        return new[]
        {
            p[..sniStart].ToArray(),
            p.Slice(sniStart, 1).ToArray(),
            p[(sniStart + 1)..].ToArray(),
        };
    }
}

internal sealed class HelloSplitStream : Stream
{
    private readonly Stream _inner;
    private readonly HelloSplitMode _mode;
    private bool _passthrough;

    public HelloSplitStream(Stream inner, HelloSplitMode mode = HelloSplitMode.Sni)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _mode = mode;
        if (_mode == HelloSplitMode.Off)
            _passthrough = true;
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => _inner.Length;
    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        _inner.ReadAsync(buffer, cancellationToken);
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        _inner.ReadAsync(buffer, offset, count, cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => _inner.SetLength(value);

    public override void Write(byte[] buffer, int offset, int count)
    {
        if (!_passthrough)
        {
            _passthrough = true;
            var span = buffer.AsSpan(offset, count);
            if (HelloSplitHelper.IsClientHello(span))
            {
                foreach (var seg in HelloSplitHelper.BuildSegments(span, _mode))
                {
                    _inner.Write(seg, 0, seg.Length);
                    _inner.Flush();
                }
                return;
            }
        }
        _inner.Write(buffer, offset, count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (!_passthrough)
        {
            _passthrough = true;
            if (HelloSplitHelper.IsClientHello(buffer))
            {
                foreach (var seg in HelloSplitHelper.BuildSegments(buffer, _mode))
                {
                    _inner.Write(seg);
                    _inner.Flush();
                }
                return;
            }
        }
        _inner.Write(buffer);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!_passthrough)
        {
            _passthrough = true;
            if (HelloSplitHelper.IsClientHello(buffer.Span))
            {
                var segments = HelloSplitHelper.BuildSegments(buffer.Span, _mode);
                foreach (var seg in segments)
                {
                    await _inner.WriteAsync(seg, cancellationToken).ConfigureAwait(false);
                    await _inner.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                return;
            }
        }
        await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _inner.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
