using UotanStudio.Firmware.BootSecurity.Avb;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class AvbFecRepairTests
{
    private const int Block = 4096;

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public async Task RepairAsync_KnownBadBlocks_ReconstructsOriginalBytes(int roots)
    {
        var token = TestContext.Current.CancellationToken;
        var blocks = 6L;
        var (image, paritySize, original) = await BuildSignedImage(blocks, (uint)roots, token);
        var storedParity = image[(int)(blocks * Block)..].ToArray();
        var bad = new long[] { 0, 2 };
        foreach (var block in bad)
        {
            Corrupt(image, block);
        }
        using var stream = new MemoryStream(image, writable: true);

        var repaired = await AvbFecCodec.RepairAsync(stream, blocks * Block, paritySize, (uint)roots, bad, token);

        Assert.True(repaired);
        Assert.Equal(original, image[..(int)(blocks * Block)]);
        Assert.Equal(storedParity, image[(int)(blocks * Block)..]);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public async Task RepairAsync_InterleavedAcrossRounds_ReconstructsOriginalBytes(int roots)
    {
        var token = TestContext.Current.CancellationToken;
        var dataSymbols = 255 - roots;
        var blocks = dataSymbols + 3L;
        var rounds = (blocks + dataSymbols - 1) / dataSymbols;
        Assert.True(rounds >= 2);
        var (image, paritySize, original) = await BuildSignedImage(blocks, (uint)roots, token);
        var bad = new long[] { 0, 1, 2, 3 };
        foreach (var block in bad)
        {
            Corrupt(image, block);
        }
        using var stream = new MemoryStream(image, writable: true);

        var repaired = await AvbFecCodec.RepairAsync(stream, blocks * Block, paritySize, (uint)roots, bad, token);

        Assert.True(repaired);
        Assert.Equal(original, image[..(int)(blocks * Block)]);
    }

    [Theory]
    [InlineData(2, 3)]
    [InlineData(4, 5)]
    public async Task RepairAsync_ErasuresExceedRoundCapacity_ReturnsFalseWithoutMutation(int roots, int badCount)
    {
        var token = TestContext.Current.CancellationToken;
        var blocks = 8L;
        var (image, paritySize, original) = await BuildSignedImage(blocks, (uint)roots, token);
        var storedParity = image[(int)(blocks * Block)..].ToArray();
        var bad = Enumerable.Range(0, badCount).Select(block => (long)block).ToArray();
        foreach (var block in bad)
        {
            Corrupt(image, block);
        }
        using var stream = new MemoryStream(image, writable: true);

        var repaired = await AvbFecCodec.RepairAsync(stream, blocks * Block, paritySize, (uint)roots, bad, token);

        Assert.False(repaired);
        Assert.NotEqual(original, image[..(int)(blocks * Block)]);
        Assert.Equal(storedParity, image[(int)(blocks * Block)..]);
    }

    [Fact]
    public async Task RepairAsync_BlockOutsideProtectedPrefix_Throws()
    {
        var token = TestContext.Current.CancellationToken;
        var (image, paritySize, _) = await BuildSignedImage(4, 2, token);
        using var stream = new MemoryStream(image, writable: true);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => AvbFecCodec.RepairAsync(stream, 4 * Block, paritySize, 2, [4], token));
    }

    private static async Task<(byte[] Image, int ParitySize, byte[] Original)> BuildSignedImage(long blocks, uint roots, CancellationToken token)
    {
        var covered = blocks * Block;
        var paritySize = (int)AvbFecCodec.CalculateParitySize(covered, roots);
        var data = new byte[covered];
        for (var index = 0; index < covered; index++)
        {
            data[index] = (byte)(index * 31 + (index / Block) * 7);
        }
        var image = new byte[covered + paritySize];
        data.AsSpan().CopyTo(image);
        await using (var source = new MemoryStream(data, writable: false))
        await using (var destination = new MemoryStream(image, writable: true))
        {
            destination.Position = covered;
            await AvbFecCodec.WriteParityAsync(source, destination, covered, roots, token);
        }
        return (image, paritySize, data);
    }

    private static void Corrupt(byte[] image, long block)
    {
        var offset = (int)(block * Block);
        for (var index = 0; index < Block; index++)
        {
            image[offset + index] = (byte)(0xE7 ^ index);
        }
    }
}