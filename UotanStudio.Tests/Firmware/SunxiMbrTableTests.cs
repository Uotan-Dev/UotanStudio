using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.VendorFirmware.Allwinner.ImageWty;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class SunxiMbrTableTests
{
    [Fact]
    public void Parse_ReadsHeaderAndPartitionEntries()
    {
        var table = BuildTable(("bootloader", 0x1000, 64), ("rootfs", 0x2000, 4096), ("private", 0x2200, 128));

        var parsed = SunxiMbrTable.Parse(table);

        Assert.Equal(0xDEADBEEFu, parsed.Crc32);
        Assert.Equal(0x300u, parsed.Version);
        Assert.Equal(1u, parsed.Copy);
        Assert.Equal(3, parsed.Partitions.Count);
        Assert.Equal("DISK", parsed.Partitions[0].ClassName);
        Assert.Equal("bootloader", parsed.Partitions[0].Name);
        Assert.Equal(0x1000ul, parsed.Partitions[0].FirstSector);
        Assert.Equal(64ul, parsed.Partitions[0].SectorCount);
        Assert.False(parsed.Partitions[0].ReadOnly);
        Assert.Equal(0x2000ul, parsed.Partitions[1].FirstSector);
        Assert.Equal(4096ul, parsed.Partitions[1].SectorCount);
        Assert.Equal("private", parsed.Partitions[2].Name);
        Assert.True(parsed.Partitions[2].ReadOnly);
        Assert.True(parsed.Partitions[2].KeyData);
        Assert.Equal(0x5Au, parsed.Partitions[2].UserType);
    }

    [Fact]
    public void Parse_RejectsForeignMagic()
    {
        var table = new byte[SunxiMbrTable.TableSize];
        "sunxi-MB"u8.CopyTo(table.AsSpan(8));

        Assert.Throws<InvalidDataException>(() => SunxiMbrTable.Parse(table));
    }

    [Fact]
    public void Parse_RejectsShortBuffers()
    {
        Assert.Throws<InvalidDataException>(() => SunxiMbrTable.Parse(new byte[16]));
    }

    [Fact]
    public void Parse_RejectsCountsAboveThePackedLimit()
    {
        var table = new byte[SunxiMbrTable.TableSize];
        "softw411"u8.CopyTo(table.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(24), SunxiMbrTable.MaximumPartitions + 1);

        Assert.Throws<InvalidDataException>(() => SunxiMbrTable.Parse(table));
    }

    [Fact]
    public void Parse_RejectsTablesEndingBeforeTheDeclaredPartitions()
    {
        var table = new byte[SunxiMbrTable.HeaderSize + SunxiMbrTable.PartitionEntrySize];
        "softw411"u8.CopyTo(table.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(24), 3);

        Assert.Throws<InvalidDataException>(() => SunxiMbrTable.Parse(table));
    }

    [Fact]
    public async Task ParseAsync_ReadsTheSameTableFromAStream()
    {
        var table = BuildTable(("env", 0x400, 32));
        using var source = new MemoryStream(table);

        var parsed = await SunxiMbrTable.ParseAsync(source, TestContext.Current.CancellationToken);

        Assert.Single(parsed.Partitions);
        Assert.Equal("env", parsed.Partitions[0].Name);
        Assert.Equal(0x400ul, parsed.Partitions[0].FirstSector);
        Assert.Equal(32ul, parsed.Partitions[0].SectorCount);
    }

    [Fact]
    public void HasMagic_RequiresTheSoftw411BytesAtOffsetEight()
    {
        var table = new byte[SunxiMbrTable.HeaderSize];
        Assert.False(SunxiMbrTable.HasMagic(table));
        "softw411"u8.CopyTo(table.AsSpan(8));
        Assert.True(SunxiMbrTable.HasMagic(table));
    }

    private static byte[] BuildTable(params (string Name, ulong FirstSector, ulong SectorCount)[] partitions)
    {
        var table = new byte[SunxiMbrTable.TableSize];
        "softw411"u8.CopyTo(table.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(table, 0xDEADBEEF);
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(4), 0x300);
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(16), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(24), (uint)partitions.Length);
        for (var index = 0; index < partitions.Length; index++)
        {
            var entry = SunxiMbrTable.HeaderSize + (index * SunxiMbrTable.PartitionEntrySize);
            var (name, firstSector, sectorCount) = partitions[index];
            BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(entry), (uint)(firstSector >> 32));
            BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(entry + 4), (uint)firstSector);
            BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(entry + 8), (uint)(sectorCount >> 32));
            BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(entry + 12), (uint)sectorCount);
            Encoding.ASCII.GetBytes("DISK").CopyTo(table, entry + 16);
            Encoding.ASCII.GetBytes(name).CopyTo(table, entry + 32);
            if (!name.Equals("private", StringComparison.Ordinal))
            {
                continue;
            }

            BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(entry + 48), 0x5A);
            BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(entry + 52), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(entry + 56), 1);
        }

        return table;
    }
}