using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Models;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Packer;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Parser;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Vendor boot version 3/4 validation closure: byte-identical repacks of synthetic images with multi-ramdisk
/// tables (including DLKM-type entries), the AOSP boot_image.h ramdisk type semantics, and rejection of every
/// invalid variant: entry stride other than 108 bytes, empty DTB on version 3, version 4 page sizes outside
/// {4096, 16384}, new bootconfig payloads above the 4096-byte mkbootimg convention, out-of-bounds table entries
/// and malformed board ids.
/// <para>Vendor Boot 版本 3/4 校验收口：含多 RAMDisk 表（含 DLKM 类型条目）合成镜像的逐字节一致重打包、AOSP
/// boot_image.h 的 RAMDisk 类型语义，以及对每种非法变体的拒绝：条目步长非 108 字节、版本 3 空 DTB、版本 4 页大小不在
/// {4096, 16384} 内、新 bootconfig 载荷超过 4096 字节的 mkbootimg 约定、越界表条目与畸形 board id。</para>
/// </summary>
public class VendorBootValidationTests
{
    private const int V4HeaderSize = 2128;

    private static readonly byte[] VendorRamdisk = BuildBytes(3072, 5);
    private static readonly byte[] VendorDtb = BuildBytes(1024, 17);
    private static readonly byte[] SmallBootconfig = BuildBytes(256, 23);

    private static byte[] BuildBytes(int length, int factor)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++)
        {
            data[i] = (byte)((i * factor + 11) & 0xFF);
        }

        return data;
    }

    private static byte[] PackToArray(VendorBootPacker packer)
    {
        using var ms = new MemoryStream();
        packer.PackToStream(ms);
        return ms.ToArray();
    }

    private static VendorBootHeader NewHeader(uint version, uint pageSize = 4096) => new()
    {
        HeaderVersion = version,
        PageSize = pageSize,
        KernelLoadAddress = 0x00100000,
        RamdiskLoadAddress = 0x01000000,
        TagsLoadAddress = 0x00000100,
        VendorCmdline = "androidboot.vendor=validation",
        VendorName = "uotan",
    };

    private static VendorRamdiskTableEntry NewEntry(uint offset, uint size, uint type, string name) => new()
    {
        RamdiskOffset = offset,
        RamdiskSize = size,
        RamdiskType = type,
        RamdiskName = name,
    };

    private static VendorBootPacker NewV4Packer(params VendorRamdiskTableEntry[] entries)
    {
        var packer = new VendorBootPacker
        {
            Header = NewHeader(4),
            VendorRamdisk = VendorRamdisk,
            Dtb = VendorDtb,
            Bootconfig = SmallBootconfig,
        };
        foreach (var entry in entries)
        {
            packer.RamdiskTable.Add(entry);
        }

        return packer;
    }

    private static byte[] BuildRawV4Image(
        uint entrySize, uint bootconfigSize, uint vendorRamdiskSize = 64, uint dtbSize = 32,
        uint tableSize = 4096, uint entryNum = 1)
    {
        var bootconfigAligned = (bootconfigSize + 4095u) / 4096u * 4096u;
        var image = new byte[16384 + (int)bootconfigAligned];
        Encoding.ASCII.GetBytes("VNDRBOOT").CopyTo(image, 0);
        var span = image.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(8, 4), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(12, 4), 4096);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(24, 4), vendorRamdiskSize);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(2096, 4), V4HeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(2100, 4), dtbSize);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(2112, 4), tableSize);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(2116, 4), entryNum);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(2120, 4), entrySize);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(2124, 4), bootconfigSize);
        BuildBytes((int)vendorRamdiskSize, 3).CopyTo(span.Slice(4096, (int)vendorRamdiskSize));
        BuildBytes((int)dtbSize, 7).CopyTo(span.Slice(8192, (int)dtbSize));
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(12288, 4), vendorRamdiskSize);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(12292, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(12296, 4), 1);
        Encoding.ASCII.GetBytes("platform").CopyTo(span.Slice(12300, 8));
        if (bootconfigSize > 0)
        {
            BuildBytes((int)bootconfigSize, 29).CopyTo(span.Slice(16384, (int)bootconfigSize));
        }

        return image;
    }

    [Fact]
    public void VendorBootV3_RoundTrip_RepackIsByteIdentical()
    {
        var packer = new VendorBootPacker
        {
            Header = NewHeader(3),
            VendorRamdisk = VendorRamdisk,
            Dtb = VendorDtb,
        };
        var image = PackToArray(packer);

        using var parser = new VendorBootParser();
        parser.Parse(image);
        var repacked = VendorBootPacker.Repack(parser);

        Assert.Equal(image, repacked);
    }

    [Fact]
    public void VendorBootV4_MultiRamdiskTableWithDlkmEntry_RoundTripIsByteIdenticalAndResolvesAospTypes()
    {
        var packer = NewV4Packer(
            NewEntry(0, 1024, (uint)VendorRamdiskType.Platform, "generic"),
            NewEntry(1024, 1024, (uint)VendorRamdiskType.Recovery, "recovery"),
            NewEntry(2048, 1024, (uint)VendorRamdiskType.Dlkm, "vendor_dlkm"));
        var image = PackToArray(packer);

        using var parser = new VendorBootParser();
        parser.Parse(image);

        Assert.Equal(3, parser.RamdiskTable.Count);
        Assert.Equal(VendorRamdiskType.Platform, parser.RamdiskTable[0].ResolvedRamdiskType);
        Assert.Equal(VendorRamdiskType.Recovery, parser.RamdiskTable[1].ResolvedRamdiskType);
        Assert.Equal(VendorRamdiskType.Dlkm, parser.RamdiskTable[2].ResolvedRamdiskType);
        Assert.Equal(108u, parser.Header.VendorRamdiskTableEntrySize);

        var repacked = VendorBootPacker.Repack(parser);
        Assert.Equal(image, repacked);
    }

    [Fact]
    public void VendorBootV4_VendorExtensionRamdiskType_IsUnresolvedButPreservedByteIdentically()
    {
        var packer = NewV4Packer(NewEntry(0, 1024, 9, "vendor_ext"));
        var image = PackToArray(packer);

        using var parser = new VendorBootParser();
        parser.Parse(image);

        Assert.Null(parser.RamdiskTable[0].ResolvedRamdiskType);
        Assert.Equal(9u, parser.RamdiskTable[0].RamdiskType);
        Assert.Equal(image, VendorBootPacker.Repack(parser));
    }

    [Fact]
    public void VendorBootV4_EntryStrideOtherThan108_IsRejectedWithInvalidDataException()
    {
        var data = BuildRawV4Image(entrySize: 112, bootconfigSize: 0);
        using var parser = new VendorBootParser();

        Assert.Throws<InvalidDataException>(() => parser.Parse(data));
    }

    [Fact]
    public void VendorBootV3_EmptyDtb_IsRejectedWithArgumentException()
    {
        var packer = new VendorBootPacker
        {
            Header = NewHeader(3),
            VendorRamdisk = VendorRamdisk,
        };

        var exception = Assert.Throws<ArgumentException>(() => PackToArray(packer));
        Assert.Contains("DTB", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(2048u)]
    [InlineData(8192u)]
    [InlineData(65536u)]
    public void VendorBootV4_PageSizeOutsideFourKbAndSixteenKb_IsRejectedWithArgumentException(uint pageSize)
    {
        var packer = NewV4Packer(NewEntry(0, 64, 1, "generic"));
        packer.Header.PageSize = pageSize;

        var exception = Assert.Throws<ArgumentException>(() => PackToArray(packer));
        Assert.Contains("4096 or 16384", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void VendorBootV4_NewBootconfigAboveFourKilobyteConvention_IsRejectedWithArgumentException()
    {
        var packer = NewV4Packer();
        packer.Header.VendorBootconfigSize = 0;
        packer.Bootconfig = BuildBytes(4097, 13);

        var exception = Assert.Throws<ArgumentException>(() => PackToArray(packer));
        Assert.Contains("4096", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void VendorBootV4_SourceBootconfigAboveFourKilobyte_RoundTripsByteIdentically()
    {
        var data = BuildRawV4Image(entrySize: 108, bootconfigSize: 5000);

        using var parser = new VendorBootParser();
        parser.Parse(data);

        Assert.Equal(5000u, parser.Header.VendorBootconfigSize);
        Assert.Equal(BuildBytes(5000, 29), parser.Bootconfig);
        Assert.Equal(data, VendorBootPacker.Repack(parser));
    }

    [Fact]
    public void VendorBootV4_TableEntryBeyondVendorRamdisk_IsRejectedWithArgumentException()
    {
        var packer = NewV4Packer(NewEntry(2048, 2048, 1, "oversized"));

        var exception = Assert.Throws<ArgumentException>(() => PackToArray(packer));
        Assert.Contains("exceeds the vendor ramdisk region", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void VendorBootV4_BoardIdWithWrongWordCount_IsRejectedWithArgumentException()
    {
        var entry = NewEntry(0, 64, 1, "short_board");
        entry.RamdiskBoardId = new uint[15];
        var packer = NewV4Packer(entry);

        var exception = Assert.Throws<ArgumentException>(() => PackToArray(packer));
        Assert.Contains("16 unsigned words", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void VendorBootV4_ZeroEntryTable_KeepsEmptyStrideFieldParseableAndRepacks()
    {
        var packer = NewV4Packer();
        var image = PackToArray(packer);

        using var parser = new VendorBootParser();
        parser.Parse(image);

        Assert.Empty(parser.RamdiskTable);
        Assert.Equal(0u, parser.Header.VendorRamdiskTableEntryNum);
        Assert.Equal(image, VendorBootPacker.Repack(parser));
    }
}