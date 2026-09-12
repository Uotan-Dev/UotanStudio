using Microsoft.Win32.SafeHandles;

namespace UotanStudio.Firmware.SharedKernel.Streams;

/// <summary>A non-owning cursor; raw files use positional I/O, other streams serialize only each read.</summary>
internal sealed class IndependentReadStream : Stream
{
    private readonly Stream _source;
    private readonly object _gate;
    private readonly SafeFileHandle? _file;
    private readonly long _length;
    private long _position;

    internal IndependentReadStream(Stream source, object gate)
    {
        _source = source;
        _gate = gate;
        lock (gate)
        {
            _length = source.Length;
            // Do not bypass a derived stream's transformation or validation overrides.
            if (source.GetType() == typeof(FileStream)) _file = ((FileStream)source).SafeFileHandle;
        }
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;
    public override long Position { get => _position; set { ArgumentOutOfRangeException.ThrowIfNegative(value); _position = value; } }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer)
    {
        if (_position >= _length) return 0;
        var target = buffer[..(int)Math.Min(buffer.Length, _length - _position)];
        int read;
        if (_file is not null) read = RandomAccess.Read(_file, target, _position);
        else
        {
            lock (_gate)
            {
                _source.Position = _position;
                read = _source.Read(target);
            }
        }
        _position += read;
        return read;
    }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_file is null) return Read(buffer.Span);
        if (_position >= _length) return 0;
        var read = await RandomAccess.ReadAsync(_file,
            buffer[..(int)Math.Min(buffer.Length, _length - _position)], _position, cancellationToken).ConfigureAwait(false);
        _position += read;
        return read;
    }
    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => checked(Position + offset),
            SeekOrigin.End => checked(Length + offset),
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        return Position;
    }
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}