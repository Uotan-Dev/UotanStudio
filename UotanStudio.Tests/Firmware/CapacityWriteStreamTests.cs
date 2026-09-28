using UotanStudio.Firmware.FileSystem.Rebuilding;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class CapacityWriteStreamTests
{
    [Fact]
    public void Write_RewritesEarlierBlocks_BoundsExtentInsteadOfCumulativeWrites()
    {
        using var output = new MemoryStream();
        using (var stream = new CapacityWriteStream(output, 4, TestContext.Current.CancellationToken))
        {
            stream.Write([1, 2, 3, 4]);
            stream.Seek(-4, SeekOrigin.End);
            stream.Write([5, 6, 7, 8]);
            Assert.Throws<IOException>(() => stream.WriteByte(9));
            Assert.Throws<IOException>(() => stream.SetLength(5));
            Assert.Throws<IOException>(() => stream.Position = 5);
            Assert.Throws<IOException>(() => stream.Seek(long.MaxValue, SeekOrigin.Current));
            Assert.Throws<IOException>(() => stream.Seek(-5, SeekOrigin.End));
            Assert.Equal(4, stream.Position);
            Assert.Equal(4, stream.Length);
        }
        Assert.Equal(new byte[] { 5, 6, 7, 8 }, output.ToArray());
        Assert.True(output.CanWrite);
    }

    [Fact]
    public void SetLength_Canceled_DoesNotExpandOutput()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var output = new MemoryStream();
        using var stream = new CapacityWriteStream(output, 1024, cancellation.Token);
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => stream.SetLength(1024));
        Assert.ThrowsAny<OperationCanceledException>(() => stream.WriteByte(1));
        Assert.Equal(0, output.Length);
    }
}