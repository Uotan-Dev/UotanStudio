using UotanStudio.Firmware.BoundedContexts.FileSystem.Sparse;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>Checks bounded reads from large logical images without allocating their contents.</summary>
public class SparseLargeStreamTests
{
    [Theory]
    [InlineData(0L)]
    [InlineData(2_147_483_648L)]
    [InlineData(4_294_967_296L)]
    public void StreamProvider_ReadsLargeRangesAndPadsNeitherShortReadsNorEnd(long offset)
    {
        const long size = 11_190_403_072;
        using var input = new PatternStream(size + 97);
        using var provider = new StreamDataProvider(input, 97, size);
        var buffer = new byte[4096];

        Assert.Equal(buffer.Length, provider.Read(offset, buffer));
        Assert.Equal(Enumerable.Range(0, buffer.Length).Select(i => (byte)((offset + 97 + i) % 251)), buffer);
        Assert.Equal(offset + 97 + buffer.Length, input.Position);

        using var slice = provider.GetSubProvider(offset, size - offset);
        Assert.Equal(buffer.Length, slice.Read(0, buffer));
        Assert.Equal((byte)((offset + 97) % 251), buffer[0]);
        Assert.Equal(3, provider.Read(size - 3, buffer));
        Assert.Equal(0, provider.Read(size, buffer));
        Assert.True(input.CanRead);
    }

    private sealed class PatternStream(long length) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get; set; }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            var count = (int)Math.Min(buffer.Length, Length - Position);
            for (var i = 0; i < count; i++) buffer[i] = (byte)((Position + i) % 251);
            Position += count;
            return count;
        }
        public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => Position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
