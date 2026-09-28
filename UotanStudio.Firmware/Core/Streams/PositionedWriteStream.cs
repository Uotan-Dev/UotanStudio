using Microsoft.Win32.SafeHandles;

namespace UotanStudio.Firmware.Core.Streams;

/// <summary>A bounded non-owning write cursor over a preallocated file range.</summary>
internal sealed class PositionedWriteStream(SafeFileHandle handle, long start, long length) : Stream
{
    private long _position;
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => length;
    public override long Position { get => _position; set => throw new NotSupportedException(); }
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        Validate(buffer.Length);
        RandomAccess.Write(handle, buffer, checked(start + _position));
        _position += buffer.Length;
    }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Validate(buffer.Length);
        await RandomAccess.WriteAsync(handle, buffer, checked(start + _position), cancellationToken).ConfigureAwait(false);
        _position += buffer.Length;
    }
    private void Validate(int count)
    {
        if (start < 0 || length < 0 || count > length - _position)
            throw new InvalidDataException("Write exceeds the assigned file range.");
    }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}