using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.Avb;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Pac;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public class BoundedFormatSecurityTests
{
    [Fact]
    public void AvbFooter_LoadsStandardAospBigEndianFooter()
    {
        var image = new byte[128];
        var footer = image.AsSpan(64, AvbFooter.Size);
        "AVBf"u8.CopyTo(footer);
        BinaryPrimitives.WriteUInt32BigEndian(footer[4..8], 1);
        BinaryPrimitives.WriteUInt32BigEndian(footer[8..12], 0);
        BinaryPrimitives.WriteUInt64BigEndian(footer[12..20], 0x1122334455667788);
        BinaryPrimitives.WriteUInt64BigEndian(footer[20..28], 0x20);
        BinaryPrimitives.WriteUInt64BigEndian(footer[28..36], 0x30);

        var result = AvbFooter.TryLoad(image);

        Assert.NotNull(result);
        Assert.Equal(1U, result.Value.VersionMajor);
        Assert.Equal(0U, result.Value.VersionMinor);
        Assert.Equal(0x1122334455667788UL, result.Value.OriginalImageSize);
        Assert.Equal(0x20UL, result.Value.VbmetaOffset);
        Assert.Equal(0x30UL, result.Value.VbmetaSize);
    }

    [Fact]
    public void PacExtractor_RejectsTraversalOutputName()
    {
        var path = Path.GetTempFileName();
        var output = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var head = new SprdHead
            {
                PacSize = SprdHead.Size + SprdFile.StructFixedSize + 1,
                FileCount = 1,
                DirOffset = SprdHead.Size,
                PacMagic = 0xFFFAFFFA
            };
            var bytes = new byte[head.PacSize];
            head.ToBytes().CopyTo(bytes, 0);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(SprdHead.Size, 4), SprdFile.StructFixedSize);
            Encoding.Unicode.GetBytes("../escape").CopyTo(bytes, SprdHead.Size + 4 + 512);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(SprdHead.Size + 1540, 4), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(SprdHead.Size + 1552, 4), (uint)(bytes.Length - 1));
            File.WriteAllBytes(path, bytes);

            Assert.Throws<InvalidDataException>(() => PacExtractor.Extract(path, output));
        }
        finally
        {
            File.Delete(path);
            if (Directory.Exists(output)) Directory.Delete(output, true);
        }
    }

    [Fact]
    public void PacExtractor_ExtractsSafeNestedOutputName()
    {
        var path = Path.GetTempFileName();
        var output = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var head = new SprdHead
            {
                PacSize = SprdHead.Size + SprdFile.StructFixedSize + 1,
                FileCount = 1,
                DirOffset = SprdHead.Size,
                PacMagic = 0xFFFAFFFA
            };
            var bytes = new byte[head.PacSize];
            head.ToBytes().CopyTo(bytes, 0);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(SprdHead.Size, 4), SprdFile.StructFixedSize);
            Encoding.Unicode.GetBytes("nested/file.bin").CopyTo(bytes, SprdHead.Size + 4 + 512);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(SprdHead.Size + 1540, 4), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(SprdHead.Size + 1552, 4), (uint)(bytes.Length - 1));
            bytes[^1] = 0x5A;
            File.WriteAllBytes(path, bytes);

            Assert.Equal(1, PacExtractor.Extract(path, output));
            Assert.Equal(new byte[] { 0x5A }, File.ReadAllBytes(Path.Combine(output, "nested", "file.bin")));
        }
        finally
        {
            File.Delete(path);
            if (Directory.Exists(output)) Directory.Delete(output, true);
        }
    }

    [Fact]
    public void VendorBootV3_TruncatedHeaderIsRejected()
    {
        var data = Header(2111, 4096);
        using var parser = new VendorBootParser();
        Assert.Throws<ImageParseException>(() => parser.Parse(data));
    }

    [Fact]
    public void VendorBootV3_ComponentPastEndIsRejected()
    {
        var data = Header(4096, 4096);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(24, 4), 1);
        using var parser = new VendorBootParser();
        Assert.Throws<ImageParseException>(() => parser.Parse(data));
    }

    [Fact]
    public void VendorBootV3_InvalidPageSizeIsRejected()
    {
        var data = Header(2112, 3000);
        using var parser = new VendorBootParser();
        Assert.Throws<ImageParseException>(() => parser.Parse(data));
    }

    [Fact]
    public void VendorBootV3_OversizedComponentIsRejectedBeforeAllocation()
    {
        var data = Header(2112, 4096);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(24, 4), 512U * 1024 * 1024 + 1);

        using var parser = new VendorBootParser();
        Assert.Throws<ImageParseException>(() => parser.Parse(data));
    }

    [Fact]
    public void VendorBootComponentLengthsUsePrivateParseSnapshot()
    {
        var data = VendorBootV3([1, 2, 3, 4], [5, 6]);
        using var parser = new VendorBootParser();
        parser.Parse(data);

        parser.Header.VendorRamdiskSize = 1000;
        parser.Header.DtbSize = 2000;

        Assert.Equal(4, parser.GetComponentLength(VendorBootComponent.VendorRamdisk));
        Assert.Equal(2, parser.GetComponentLength(VendorBootComponent.Dtb));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, parser.VendorRamdisk);
    }

    [Fact]
    public void VendorBootGetComponentLengthRequiresParsedSource()
    {
        using var parser = new VendorBootParser();
        Assert.Throws<InvalidOperationException>(() =>
            parser.GetComponentLength(VendorBootComponent.VendorRamdisk));
    }

    [Fact]
    public void VendorBootV4_OversizedMetadataRegionIsRejected()
    {
        var data = new byte[4096];
        Encoding.ASCII.GetBytes("VNDRBOOT").CopyTo(data, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8, 4), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12, 4), 4096);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(2096, 4), 2128);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(2112, 4), 64U * 1024 * 1024 + 1);

        using var parser = new VendorBootParser();
        Assert.Throws<ImageParseException>(() => parser.Parse(data));
    }

    [Fact]
    public void VendorBootV4_ZeroEntryTruncatedReservedTableIsRejected()
    {
        var data = new byte[4096];
        Encoding.ASCII.GetBytes("VNDRBOOT").CopyTo(data, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8, 4), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12, 4), 4096);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(2096, 4), 2128);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(2112, 4), 16); // truncated reserved table
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(2116, 4), 0);  // no entries

        using var parser = new VendorBootParser();
        Assert.Throws<ImageParseException>(() => parser.Parse(data));
    }

    [Fact]
    public void VendorBootV4_ZeroEntryReservedTablePositionsBootconfig()
    {
        var data = new byte[8193];
        Encoding.ASCII.GetBytes("VNDRBOOT").CopyTo(data, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8, 4), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12, 4), 4096);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(2096, 4), 2128);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(2112, 4), 16); // reserved table bytes
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(2116, 4), 0);  // no entries
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(2124, 4), 1);  // bootconfig size
        data[8192] = 0x5A;

        using var parser = new VendorBootParser();
        parser.Parse(data);

        Assert.Empty(parser.RamdiskTable);
        Assert.Equal(new byte[] { 0x5A }, parser.Bootconfig);
    }

    [Fact]
    public void VendorBootV3_StreamingExtractionMatchesByteArray()
    {
        var ramdisk = Enumerable.Range(0, 100).Select(i => (byte)i).ToArray();
        var dtb = Enumerable.Range(0, 50).Select(i => (byte)(i + 200)).ToArray();
        var data = VendorBootV3(ramdisk, dtb);
        using var parser = new VendorBootParser();
        parser.Parse(data);

        using var ramdiskStream = parser.ExtractComponentStream(VendorBootComponent.VendorRamdisk);
        Assert.Equal(ramdisk.Length, ramdiskStream.Length);
        Assert.Equal(ramdisk, ReadAll(ramdiskStream));

        using var dtbStream = parser.ExtractComponentStream(VendorBootComponent.Dtb);
        Assert.Equal(dtb, ReadAll(dtbStream));

        // The byte[] compatibility entry remains equivalent to the streamed bytes.
        Assert.Equal(ramdisk, parser.VendorRamdisk);
        Assert.Equal(dtb, parser.Dtb);
    }

    [Fact]
    public void VendorBootV3_StreamingExtractionIsRangeBounded()
    {
        var ramdisk = new byte[] { 1, 2, 3, 4 };
        var dtb = new byte[] { 5, 6, 7, 8, 9, 10 };
        var data = VendorBootV3(ramdisk, dtb);
        using var parser = new VendorBootParser();
        parser.Parse(data);

        using var stream = parser.ExtractComponentStream(VendorBootComponent.VendorRamdisk);
        Assert.Equal(ramdisk.Length, stream.Length);
        var buffer = new byte[8];
        var read = stream.Read(buffer);
        Assert.Equal(ramdisk.Length, read);
        Assert.Equal(ramdisk, buffer[..read]);
        Assert.Equal(0, stream.Read(buffer)); // EOF exactly at the component bound
        Assert.ThrowsAny<IOException>(() => stream.Seek(stream.Length + 1, SeekOrigin.Begin));
    }

    [Fact]
    public void VendorBootV3_ExtractComponentToFileMatchesByteArray()
    {
        var ramdisk = new byte[] { 0xAA, 0xBB, 0xCC };
        var data = VendorBootV3(ramdisk, null);
        using var parser = new VendorBootParser();
        parser.Parse(data);

        var path = Path.GetTempFileName();
        try
        {
            parser.ExtractComponentToFile(path, VendorBootComponent.VendorRamdisk);
            Assert.Equal(ramdisk, File.ReadAllBytes(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void VendorBootV4_StreamingExtractionOfRamdiskTableEntryMatches()
    {
        var ramdisk = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        const int pageSize = 4096;
        const int tableOffset = 8192; // align(4096 + 256, 4096)
        const int entrySize = 332;
        var data = new byte[tableOffset + entrySize];
        Encoding.ASCII.GetBytes("VNDRBOOT").CopyTo(data, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8, 4), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12, 4), pageSize);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(24, 4), (uint)ramdisk.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(2096, 4), 2128);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(2100, 4), 0); // dtb size
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(2112, 4), entrySize); // table size
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(2116, 4), 1); // entry count
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(2120, 4), entrySize); // entry size
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(2124, 4), 0); // bootconfig size
        ramdisk.CopyTo(data, 4096);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(tableOffset, 4), 100); // ramdisk size
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(tableOffset + 4, 4), 0); // ramdisk offset
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(tableOffset + 8, 4), 1); // ramdisk type

        using var parser = new VendorBootParser();
        parser.Parse(data);

        var entry = Assert.Single(parser.RamdiskTable);
        using var stream = parser.ExtractRamdiskStream(entry);
        Assert.Equal(100, stream.Length);
        Assert.Equal(ramdisk[..100], ReadAll(stream));
    }

    private static byte[] ReadAll(Stream stream)
    {
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] VendorBootV3(byte[]? ramdisk, byte[]? dtb)
    {
        const int pageSize = 4096;
        const int headerSize = 2112;
        var ramdiskLength = ramdisk?.Length ?? 0;
        var dtbLength = dtb?.Length ?? 0;
        var ramdiskOffset = (headerSize + pageSize - 1) / pageSize * pageSize;
        var dtbOffset = (ramdiskOffset + ramdiskLength + pageSize - 1) / pageSize * pageSize;
        var data = new byte[dtbOffset + dtbLength];
        Encoding.ASCII.GetBytes("VNDRBOOT").CopyTo(data, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8, 4), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12, 4), pageSize);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(24, 4), (uint)ramdiskLength);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(2096, 4), headerSize);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(2100, 4), (uint)dtbLength);
        ramdisk?.CopyTo(data, ramdiskOffset);
        dtb?.CopyTo(data, dtbOffset);
        return data;
    }

    private static byte[] Header(int length, uint pageSize)
    {
        var data = new byte[length];
        Encoding.ASCII.GetBytes("VNDRBOOT").CopyTo(data, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8, 4), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12, 4), pageSize);
        if (length >= 2100)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(2096, 4), 2112);
        }
        return data;
    }
}