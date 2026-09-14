using UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Proto;

namespace UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Core;

internal sealed class ExtentReadStream : Stream
{
    private readonly Stream _source;
    private readonly (long Offset, long Length, long LogicalStart)[] _ranges;
    private readonly long _length;
    private long _position;

    internal ExtentReadStream(Stream source, IEnumerable<Extent> extents, uint blockSize)
    {
        _source = source;
        long position = 0;
        _ranges = extents.Select(extent =>
        {
            var size = checked((long)extent.NumBlocks * blockSize);
            var offset = extent.StartBlock == ulong.MaxValue ? -1 : checked((long)extent.StartBlock * blockSize);
            if (offset >= 0 && (offset > source.Length || size > source.Length - offset))
                throw new InvalidDataException("Source extent exceeds the base image length.");
            var range = (offset, size, position);
            position = checked(position + size);
            return range;
        }).ToArray();
        _length = position;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;
    public override long Position { get => _position; set => Seek(value, SeekOrigin.Begin); }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer)
    {
        if (_position == _length || buffer.IsEmpty) return 0;
        // Find the first range whose logical end exceeds the position. Empty
        // extents can share a start with the next range and must be skipped.
        var low = 0;
        var high = _ranges.Length;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            var candidate = _ranges[middle];
            if (_position >= candidate.LogicalStart && _position - candidate.LogicalStart >= candidate.Length)
                low = middle + 1;
            else
                high = middle;
        }
        if (low < _ranges.Length)
        {
            var range = _ranges[low];
            var inside = _position - range.LogicalStart;
            var count = (int)Math.Min(buffer.Length, range.Length - inside);
            if (range.Offset < 0) buffer[..count].Clear();
            else
            {
                _source.Position = checked(range.Offset + inside);
                _source.ReadExactly(buffer[..count]);
            }
            _position += count;
            return count;
        }
        throw new InvalidDataException("Invalid extent read position.");
    }
    public override long Seek(long offset, SeekOrigin origin)
    {
        var position = checked(offset + (origin == SeekOrigin.Begin ? 0 : origin == SeekOrigin.Current ? _position : origin == SeekOrigin.End ? _length : throw new ArgumentOutOfRangeException(nameof(origin))));
        if (position < 0 || position > _length) throw new IOException("Source extent seek exceeds its range.");
        return _position = position;
    }
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}