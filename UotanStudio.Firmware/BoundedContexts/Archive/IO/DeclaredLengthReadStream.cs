namespace UotanStudio.Firmware.BoundedContexts.Archive.IO;

internal sealed class DeclaredLengthReadStream(Stream inner, long length, bool leaveOpen) : Stream
{
    private long _position;
    private bool _disposed;

    public override bool CanRead => !_disposed;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length { get { ThrowIfDisposed(); return length; } }
    public override long Position
    {
        get { ThrowIfDisposed(); return _position; }
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset > buffer.Length - count)
        {
            throw new ArgumentException("The buffer range is invalid.");
        }
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        ThrowIfDisposed();
        var count = (int)Math.Min(buffer.Length, length - _position);
        if (count == 0)
        {
            return 0;
        }
        var read = inner.Read(buffer[..count]);
        if (read == 0)
        {
            throw new EndOfStreamException($"Source ended after {_position} bytes; {length} bytes were declared.");
        }
        _position += read;
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var count = (int)Math.Min(buffer.Length, length - _position);
        if (count == 0)
        {
            return 0;
        }
        var read = await inner.ReadAsync(buffer[..count], cancellationToken).ConfigureAwait(false);
        if (read == 0)
        {
            throw new EndOfStreamException($"Source ended after {_position} bytes; {length} bytes were declared.");
        }
        _position += read;
        return read;
    }

    public override void Flush() => ThrowIfDisposed();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing && !leaveOpen)
        {
            inner.Dispose();
        }
        _disposed = true;
        base.Dispose(disposing);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}