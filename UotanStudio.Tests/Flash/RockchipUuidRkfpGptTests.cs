using System.Buffers.Binary;
using UotanStudio.Hash.Crc32Ieee;
using UotanStudio.Services.Flash;
using Xunit;

namespace UotanStudio.Tests.Flash;

/// <summary>
/// Cases for the remaining vendor parameter special cases: <c>uuid:</c> records feeding GPT unique GUIDs,
/// the private <c>RKFP</c> GPT variant decoder and the parameter-to-GPT conversion the vendor <c>gpt</c>
/// command performs.
/// <para>其余厂商 parameter 特判用例：<c>uuid:</c> 记录提供的 GPT 唯一 GUID、私有 <c>RKFP</c> GPT 变体解码器，
/// 以及厂商 <c>gpt</c> 命令执行的 parameter 到 GPT 转换。</para>
/// </summary>
public sealed class RockchipUuidRkfpGptTests
{
    private const string SampleTable =
        "CMDLINE: mtdparts=rk29xxnand:0x00002000@0x00004000(uboot),0x00008000@0x00006000(rootfs),-@0x0000E000(userdata:grow)\n" +
        "uuid:rootfs=614e0000-0000-4b53-8000-1d28000054a9\n";

    [Fact]
    public void ParseText_UuidRecord_AttachesGuidToBareName()
    {
        var partitions = RockchipParameterParser.ParseText(SampleTable);

        var rootfs = Assert.Single(partitions, p => p.BareName == "rootfs");
        Assert.Equal(new Guid("614e0000-0000-4b53-8000-1d28000054a9"), rootfs.UniqueGuid);
        var uboot = Assert.Single(partitions, p => p.BareName == "uboot");
        Assert.Null(uboot.UniqueGuid);
    }

    [Fact]
    public void ParseText_UuidRecordWithGrowName_MatchesBeforeSuffixDrop()
    {
        var partitions = RockchipParameterParser.ParseText(
            "CMDLINE: mtdparts=rk29xxnand:-@0x0000E000(userdata:grow)\n" +
            "uuid:userdata=12345678-1234-5678-1234-567812345678\n");

        var grow = Assert.Single(partitions);
        Assert.True(grow.IsGrow);
        Assert.Equal(new Guid("12345678-1234-5678-1234-567812345678"), grow.UniqueGuid);
    }

    [Fact]
    public void ParseText_MalformedUuid_Throws()
    {
        Assert.Throws<InvalidDataException>(() => RockchipParameterParser.ParseText(
            "CMDLINE: mtdparts=rk29xxnand:0x00002000@0x00004000(uboot)\n" +
            "uuid:uboot=not-a-guid\n"));
    }

    [Fact]
    public void ParseText_UuidRecordWithoutEquals_Throws()
    {
        Assert.Throws<InvalidDataException>(() => RockchipParameterParser.ParseText(
            "CMDLINE: mtdparts=rk29xxnand:0x00002000@0x00004000(uboot)\n" +
            "uuid:uboot\n"));
    }

    [Fact]
    public void ParseText_LegacyNulSeparatedText_ParsesUuidRecords()
    {
        var partitions = RockchipParameterParser.ParseText(
            "CMDLINE: mtdparts=rk29xxnand:0x00002000@0x00004000(uboot)\0uuid:uboot=00000000-0000-0000-0000-000000000001");

        var entry = Assert.Single(partitions);
        Assert.Equal(new Guid("00000000-0000-0000-0000-000000000001"), entry.UniqueGuid);
    }

    [Fact]
    public void ParseRkfpTable_NonMagicBlock_ReturnsEmpty()
    {
        var block = new byte[512];

        Assert.Empty(FlashService.ParseRkfpTable(block));
    }

    [Fact]
    public void ParseRkfpTable_VendorLayout_DecodesNamesAndRanges()
    {
        var block = new byte[36 * 512];
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(0), 0x50464B52);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(28), 44);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(32), 2);

        WriteEntry(block, 512, "loader", 0x40, 0x100);
        WriteEntry(block, 512 + 44, "system", 0x140, 0x2000);

        var partitions = FlashService.ParseRkfpTable(block);

        Assert.Equal(2, partitions.Count);
        Assert.Equal("loader", partitions[0].Name);
        Assert.Equal(0x40, partitions[0].OffsetSectors);
        Assert.Equal(0x100, partitions[0].SizeSectors);
        Assert.Equal("system", partitions[1].Name);
        Assert.Equal(0x140, partitions[1].OffsetSectors);
        Assert.Equal(0x2000, partitions[1].SizeSectors);
    }

    [Fact]
    public void ParseRkfpTable_TruncatedEntryArray_Throws()
    {
        var block = new byte[36 * 512];
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(0), 0x50464B52);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(20), 40);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(28), 44);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(32), 4);

        Assert.Throws<InvalidDataException>(() => FlashService.ParseRkfpTable(block));
    }

    [Fact]
    public void BuildGptFromParameter_WritesHeaderEntriesAndCrcs()
    {
        var partitions = RockchipParameterParser.ParseText(SampleTable);
        var totalSectors = 0x400000L;

        var gpt = FlashService.BuildGptFromParameter(partitions, totalSectors);

        Assert.Equal(34 * 512, gpt.Length);
        Assert.Equal(0x55, gpt[510]);
        Assert.Equal(0xAA, gpt[511]);
        Assert.Equal(0x5452415020494645UL, BinaryPrimitives.ReadUInt64LittleEndian(gpt.AsSpan(512)));
        Assert.Equal((ulong)(totalSectors - 1), BinaryPrimitives.ReadUInt64LittleEndian(gpt.AsSpan(512 + 32)));
        Assert.Equal((ulong)(totalSectors - 34), BinaryPrimitives.ReadUInt64LittleEndian(gpt.AsSpan(512 + 48)));
        Assert.Equal(128u, BinaryPrimitives.ReadUInt32LittleEndian(gpt.AsSpan(512 + 80)));

        var uboot = FindEntry(gpt, "uboot");
        Assert.Equal(0x4000UL, BinaryPrimitives.ReadUInt64LittleEndian(uboot.AsSpan(32)));
        Assert.Equal(0x5FFFUL, BinaryPrimitives.ReadUInt64LittleEndian(uboot.AsSpan(40)));
        var grow = FindEntry(gpt, "userdata");
        Assert.Equal((ulong)(totalSectors - 34), BinaryPrimitives.ReadUInt64LittleEndian(grow.AsSpan(40)));
        var rootfs = FindEntry(gpt, "rootfs");
        Assert.Equal(new Guid("614e0000-0000-4b53-8000-1d28000054a9").ToByteArray(), rootfs[16..32].ToArray());

        var storedHeaderCrc = BinaryPrimitives.ReadUInt32LittleEndian(gpt.AsSpan(512 + 16));
        BinaryPrimitives.WriteUInt32LittleEndian(gpt.AsSpan(512 + 16), 0);
        var headerCrc = Crc32Ieee.Compute(gpt.AsSpan(512, 92));
        Assert.Equal(headerCrc, storedHeaderCrc);
        var entriesCrc = Crc32Ieee.Compute(gpt.AsSpan(1024, 128 * 128));
        Assert.Equal(entriesCrc, BinaryPrimitives.ReadUInt32LittleEndian(gpt.AsSpan(512 + 88)));
    }

    [Fact]
    public void BuildGptFromParameter_BootableMarker_SetsAttributeBit2()
    {
        var partitions = RockchipParameterParser.ParseText(
            "CMDLINE: mtdparts=rk29xxnand:0x00002000@0x00004000(uboot:bootable)");

        var gpt = FlashService.BuildGptFromParameter(partitions, 0x400000L);

        var uboot = FindEntry(gpt, "uboot");
        Assert.Equal(1UL << 2, BinaryPrimitives.ReadUInt64LittleEndian(uboot.AsSpan(48)));
    }

    [Fact]
    public void BuildGptFromParameter_EntryOutsideUsableArea_Throws()
    {
        var partitions = RockchipParameterParser.ParseText(
            "CMDLINE: mtdparts=rk29xxnand:0x00002000@0x00004000(uboot)");

        Assert.Throws<ArgumentException>(() => FlashService.BuildGptFromParameter(partitions, 0x5000L));
    }

    private static byte[] FindEntry(byte[] gpt, string name)
    {
        for (var i = 0; i < 128; i++)
        {
            var entry = gpt.AsSpan(1024 + i * 128, 128);
            if (System.Text.Encoding.Unicode.GetString(entry[56..]).TrimEnd('\0') == name)
            {
                return entry.ToArray();
            }
        }

        throw new InvalidOperationException($"No GPT entry named {name}.");
    }

    private static void WriteEntry(byte[] block, int offset, string name, uint start, uint count)
    {
        System.Text.Encoding.Unicode.GetBytes(name).CopyTo(block, offset);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(offset + 36), start);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(offset + 40), count);
    }
}