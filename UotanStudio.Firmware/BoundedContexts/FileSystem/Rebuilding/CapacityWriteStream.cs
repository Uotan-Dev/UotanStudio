namespace UotanStudio.Firmware.BoundedContexts.FileSystem.Rebuilding;

/// <summary>Bounds the image address space while allowing builders to rewrite earlier blocks.</summary>
internal sealed class CapacityWriteStream : Stream
{
    private readonly Stream _output;
    private readonly long _capacity;
    private readonly CancellationToken _token;

    internal CapacityWriteStream(Stream output, long capacity, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        if (!output.CanWrite || !output.CanSeek)
        {
            throw new ArgumentException("Image output must be writable and seekable.", nameof(output));
        }
        _output = output;
        _capacity = capacity;
        _token = token;
        Validate(output.Length);
        Validate(output.Position);
    }

    public override bool CanRead => _output.CanRead;
    public override bool CanSeek => true;
    public override bool CanWrite => true;
    public override long Length => _output.Length;
    public override long Position { get => _output.Position; set { Validate(value); _output.Position = value; } }
    public override void Flush() { _token.ThrowIfCancellationRequested(); _output.Flush(); }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        _token.ThrowIfCancellationRequested();
        Validate(Position);
        var remaining = _capacity - Position;
        if (remaining == 0)
        {
            return 0;
        }
        return _output.Read(buffer[..(int)Math.Min(buffer.Length, remaining)]);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var basis = origin switch
        {
            SeekOrigin.Begin => 0,
            SeekOrigin.Current => Position,
            SeekOrigin.End => Length,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        long position;
        try { position = checked(basis + offset); }
        catch (OverflowException error) { throw new IOException("Image seek exceeds its capacity.", error); }
        Position = position;
        return position;
    }

    public override void SetLength(long value)
    {
        Validate(value);
        _output.SetLength(value);
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        Validate(Position);
        if (buffer.Length > _capacity - Position)
        {
            throw new IOException("Rebuilt image exceeds the specified partition capacity.");
        }
        _output.Write(buffer);
    }

    private void Validate(long position)
    {
        _token.ThrowIfCancellationRequested();
        if (position < 0 || position > _capacity)
        {
            throw new IOException("Rebuilt image exceeds the specified partition capacity.");
        }
    }
}