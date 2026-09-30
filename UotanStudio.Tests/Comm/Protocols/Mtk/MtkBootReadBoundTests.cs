using UotanStudio.Comm.Protocols.Mtk;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk;

public sealed class MtkBootReadBoundTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(1024 * 1024 + 1)]
    [InlineData(int.MaxValue)]
    public void ValidateBootReadLength_OutsideBoundedReceive_ThrowsBeforeAllocation(int count)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MtkSession.ValidateBootReadLength(count));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4096)]
    [InlineData(1024 * 1024)]
    public void ValidateBootReadLength_WithinBoundedReceive_AcceptsTheLargestNegotiatedPacket(int count)
    {
        MtkSession.ValidateBootReadLength(count);
    }
}