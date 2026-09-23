namespace UotanStudio.Comm.Protocols.Common;

/// <summary>A write-only view that rejects output beyond its caller-owned bounded memory.</summary>
internal sealed class FixedBufferWriteStream(Memory<byte> destination) : Stream
{
    internal int Written { get; private set; }
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => Written;
    public override long Position { get => Written; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length > destination.Length - Written)
        {
            throw new InvalidDataException("A block read exceeded its bounded destination.");
        }
        buffer.CopyTo(destination.Span[Written..]);
        Written += buffer.Length;
    }
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Write(buffer.Span);
        return ValueTask.CompletedTask;
    }
}