using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Firmware.BlockImage.Gpt;
using Xunit;

namespace UotanStudio.Tests.Protocols;

public sealed class FirehoseGptSlotTests
{
    [Fact]
    public void DetectActiveSlot_RealBootAAttributes_ReturnsA()
    {
        var snapshot = CreateSnapshot(
            Boot(0, "boot_a", 0x0077000000000000UL),
            Boot(1, "boot_b", 0));

        Assert.Equal("a", FirehoseProtocol.DetectActiveSlot(snapshot));
    }

    [Fact]
    public void DetectActiveSlot_BootBCarriesActiveBit_ReturnsB()
    {
        var snapshot = CreateSnapshot(
            Boot(0, "boot_a", 0),
            Boot(1, "boot_b", 1UL << 50));

        Assert.Equal("b", FirehoseProtocol.DetectActiveSlot(snapshot));
    }

    [Theory]
    [InlineData(1UL << 6)]
    [InlineData(1UL << 54)]
    [InlineData((1UL << 6) | (1UL << 54))]
    public void DetectActiveSlot_OnlyLegacyOrSuccessfulBits_ReturnsNull(ulong attributes)
    {
        var snapshot = CreateSnapshot(
            Boot(0, "boot_a", attributes),
            Boot(1, "boot_b", 0));

        Assert.Null(FirehoseProtocol.DetectActiveSlot(snapshot));
    }

    [Fact]
    public void DetectActiveSlot_ActiveBitOnUnrelatedPartition_ReturnsNull()
    {
        var snapshot = CreateSnapshot(Boot(0, "vendor_boot_a", 1UL << 50));

        Assert.Null(FirehoseProtocol.DetectActiveSlot(snapshot));
    }

    [Fact]
    public void DetectActiveSlot_NullSnapshot_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => FirehoseProtocol.DetectActiveSlot(null!));
    }

    private static FirehoseGptSnapshot CreateSnapshot(params GptPartition[] partitions)
    {
        var table = new GptTable(
            Guid.NewGuid(),
            4096,
            256,
            [.. partitions],
            "metadata",
            92,
            6,
            249,
            (uint)Math.Max(partitions.Length, 1),
            128,
            2,
            253);
        return new FirehoseGptSnapshot(
            null!,
            new FirehoseStorageGeometry(0, 4096, 256),
            0,
            table);
    }

    private static GptPartition Boot(int index, string name, ulong attributes)
    {
        var first = 34UL + (ulong)index * 32;
        return new GptPartition(
            index,
            Guid.NewGuid(),
            Guid.NewGuid(),
            first,
            first + 31,
            attributes,
            name,
            (long)(first * 4096),
            32L * 4096);
    }
}