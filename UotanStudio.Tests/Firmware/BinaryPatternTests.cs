using UotanStudio.Firmware.BoundedContexts.Patching;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class BinaryPatternTests
{
    [Fact]
    public async Task Replace_BitMask_PreservesUnselectedBitsAndSource()
    {
        using var source = new MemoryStream([0xA1, 0xB2, 0xC3]);
        using var output = new MemoryStream();
        var result = await BinaryPattern.ReplaceAsync(source, output, "A0 B0", "F0 F0", "0F 00", "0F F0",
            0, -1, 1, 0, 1, TestContext.Current.CancellationToken);
        Assert.Equal(new long[] { 0 }, result);
        Assert.Equal(new byte[] { 0xAF, 0x02, 0xC3 }, output.ToArray());
        Assert.Equal(new byte[] { 0xA1, 0xB2, 0xC3 }, source.ToArray());
    }

    [Theory]
    [InlineData("AA AA", "00 00", 2)]
    [InlineData("AA", "00 00", 3)]
    [InlineData("AA", "00", 1)]
    public async Task Replace_InvalidWindowsOrContract_DoesNotWriteOutput(string find, string replacement, int count)
    {
        using var source = new MemoryStream([0xAA, 0xAA, 0xAA]);
        using var output = new MemoryStream();
        output.WriteByte(0x77);
        await Assert.ThrowsAsync<InvalidDataException>(() => BinaryPattern.ReplaceAsync(source, output, find, "",
            replacement, "", 0, -1, 1, 0, count, TestContext.Current.CancellationToken));
        Assert.Equal(new byte[] { 0x77 }, output.ToArray());
    }

    [Fact]
    public async Task Find_MaskedOverlappingMatches_RespectsAbsoluteAlignmentRangeAndSkip()
    {
        using var source = new MemoryStream([0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0xA6]);
        var result = await BinaryPattern.FindAsync(source, "A0 A0", "F0 F0", 1, 5, 2, 1, 1,
            TestContext.Current.CancellationToken);
        Assert.Equal(new long[] { 4 }, result);
    }

    [Fact]
    public async Task Find_PatternAcrossBufferBoundary_FindsExactlyOnceWithShortReads()
    {
        var bytes = new byte[262144];
        new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }.CopyTo(bytes, 131071);
        using var source = new ShortReadStream(bytes);
        var result = await BinaryPattern.FindAsync(source, "DE AD BE EF", "", 0, -1, 1, 0, 1,
            TestContext.Current.CancellationToken);
        Assert.Equal(new long[] { 131071 }, result);
        Assert.True(source.MaxRequested <= 131075);
    }

    [Theory]
    [InlineData("", "", 0, -1, 1, 0, 0)]
    [InlineData("GG", "", 0, -1, 1, 0, 0)]
    [InlineData("AA", "0000", 0, -1, 1, 0, 0)]
    [InlineData("AA", "00", 0, -1, 1, 0, 0)]
    [InlineData("AA", "", -1, -1, 1, 0, 0)]
    [InlineData("AA", "", 0, 5, 1, 0, 0)]
    [InlineData("AA", "", 0, -1, 0, 0, 0)]
    [InlineData("AA", "", 0, -1, 1, 0, 0)]
    [InlineData("AA", "", 0, -1, 1, 0, 3)]
    [InlineData("AA", "", 0, -1, 1, 3, 0)]
    public async Task Find_InvalidContract_Rejects(string find, string mask, long offset, long length, int alignment, int skip, int count)
    {
        using var source = new MemoryStream([0xAA, 0xAA]);
        await Assert.ThrowsAsync<InvalidDataException>(() => BinaryPattern.FindAsync(source, find, mask,
            offset, length, alignment, skip, count, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Find_Cancelled_DoesNotRead()
    {
        using var source = new ShortReadStream(new byte[100]);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BinaryPattern.FindAsync(source, "AA", "", 0, -1, 1, 0, 0, cancellation.Token));
        Assert.Equal(0, source.MaxRequested);
    }

    private sealed class ShortReadStream(byte[] bytes) : MemoryStream(bytes)
    {
        public int MaxRequested { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            MaxRequested = Math.Max(MaxRequested, buffer.Length);
            return base.ReadAsync(buffer[..Math.Min(buffer.Length, 97)], cancellationToken);
        }
    }
}