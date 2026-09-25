using UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.IO;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Proto;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public class ExtentReadStreamTests
{
    [Fact]
    public void RandomSeeksMatchConcatenatedExtentsIncludingEmptyRangesAndHoles()
    {
        var data = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        using var source = new MemoryStream(data);
        var random = new Random(42);
        var extents = new List<Extent>();
        var expected = new List<byte>();
        for (var i = 0; i < 1000; i++)
        {
            var count = random.Next(5);
            var start = i % 7 == 0 ? ulong.MaxValue : (ulong)random.Next(60);
            extents.Add(new Extent { StartBlock = start, NumBlocks = (ulong)count });
            expected.AddRange(start == ulong.MaxValue ? new byte[count * 4] : data.AsSpan((int)start * 4, count * 4).ToArray());
        }
        extents.Add(new Extent { StartBlock = 0, NumBlocks = 0 });
        using var stream = new ExtentReadStream(source, extents, 4);
        var snapshot = expected.ToArray();
        var buffer = new byte[37];
        for (var i = 0; i < 2000; i++)
        {
            var position = random.Next(snapshot.Length + 1);
            stream.Position = position;
            var read = stream.Read(buffer);
            Assert.Equal(snapshot.AsSpan(position, read).ToArray(), buffer.AsSpan(0, read).ToArray());
            Assert.Equal(position + read, stream.Position);
            Assert.Equal(position == snapshot.Length, read == 0);
        }
        stream.Position = 0;
        using var copied = new MemoryStream();
        stream.CopyTo(copied);
        Assert.Equal(snapshot, copied.ToArray());
    }

    [Fact]
    public void ReadsStopAtRangeBoundaryAndRetainTruncationAndSeekErrors()
    {
        using var source = new MemoryStream([1, 2, 3, 4]);
        using var stream = new ExtentReadStream(source,
            [new Extent { StartBlock = 2, NumBlocks = 2 }, new Extent { StartBlock = 0, NumBlocks = 2 }], 1);
        var buffer = new byte[4];
        Assert.Equal(2, stream.Read(buffer));
        Assert.Equal(new byte[] { 3, 4, 0, 0 }, buffer);
        Assert.Throws<IOException>(() => stream.Seek(-1, SeekOrigin.Begin));
        Assert.Equal(2, stream.Position);
        source.SetLength(1);
        Assert.Throws<EndOfStreamException>(() => stream.Read(buffer));
        Assert.Equal(2, stream.Position);
    }
}