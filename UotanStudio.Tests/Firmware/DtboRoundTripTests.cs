using System.Text;
using UotanStudio.Firmware;
using UotanStudio.Firmware.Common;
using UotanStudio.Firmware.Dt;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// DTBO (Android DT table image) round-trip tests. The layout is asserted against the
/// upstream AOSP <c>dt_table.h</c> / <c>mkdtboimg.py</c> definition (big-endian, 32-byte
/// header, 32-byte entry descriptors, blobs at dt_offset) — including a case hand-encoded
/// to that definition so the reader is not merely validated against our own writer.
/// <para>DTBO（Android DT 表镜像）回环测试。布局断言对齐上游 AOSP <c>dt_table.h</c> /
/// <c>mkdtboimg.py</c> 定义（大端、32 字节表头、32 字节条目描述符、数据块位于 dt_offset），
/// 并包含按上游定义手工编码的用例，避免读取端只被自家写入端验证。</para>
/// </summary>
public class DtboRoundTripTests
{
    private static byte[] BuildDtb(string marker)
    {
        var tree = new DeviceTree();
        tree.Root.AddPropertyString("compatible", marker);
        tree.Root.AddChild("soc").AddPropertyCells("reg", 0x1000_0000, 0x1000);
        return DtbFormat.Write(tree);
    }

    private static void WriteU32BE(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)(value >> 24);
        data[offset + 1] = (byte)(value >> 16);
        data[offset + 2] = (byte)(value >> 8);
        data[offset + 3] = (byte)value;
    }

    private static uint ReadU32BE(byte[] data, int offset) =>
        ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) | ((uint)data[offset + 2] << 8) | data[offset + 3];

    [Fact]
    public void PackRead_MultiEntry_RoundTrip()
    {
        var dtb0 = BuildDtb("board-a");
        var dtb1 = BuildDtb("board-b");
        var dtb2 = BuildDtb("board-c");

        var image = DtboFormat.Pack([dtb0, dtb1, dtb2], pageSize: 4096);

        var (header, entries, dtbs) = DtboFormat.Read(image);

        Assert.Equal(FdtConstants.DtboMagic, header.Magic);
        Assert.Equal((uint)image.Length, header.TotalSize);
        Assert.Equal((uint)FdtConstants.DtboHeaderSize, header.HeaderSize);
        Assert.Equal((uint)FdtConstants.DtboEntrySize, header.EntrySize);
        Assert.Equal(3u, header.EntryCount);
        Assert.Equal((uint)FdtConstants.DtboHeaderSize, header.EntriesOffset);
        Assert.Equal(4096u, header.PageSize);
        Assert.Equal(0u, header.Version);

        // mkdtboimg：首个数据块偏移 = header_size + count*entry_size，连续存放。
        // mkdtboimg: first blob offset = header_size + count*entry_size, blobs contiguous.
        var firstBlob = FdtConstants.DtboHeaderSize + 3 * FdtConstants.DtboEntrySize;
        Assert.Equal(3, entries.Count);
        Assert.Equal((uint)firstBlob, entries[0].DtOffset);
        Assert.Equal((uint)dtb0.Length, entries[0].DtSize);
        Assert.Equal((uint)(firstBlob + dtb0.Length), entries[1].DtOffset);
        Assert.Equal((uint)dtb1.Length, entries[1].DtSize);
        Assert.Equal((uint)(firstBlob + dtb0.Length + dtb1.Length), entries[2].DtOffset);
        Assert.Equal((uint)dtb2.Length, entries[2].DtSize);

        Assert.Equal(dtb0, dtbs[0]);
        Assert.Equal(dtb1, dtbs[1]);
        Assert.Equal(dtb2, dtbs[2]);
    }

    // mkdtboimg：首个数据块偏移 = header_size + count*entry_size，连续存放。
    // mkdtboimg: first blob offset = header_size + count*entry_size, blobs contiguous.
    private static uint FirstBlobOffset(int entryCount) =>
        (uint)(FdtConstants.DtboHeaderSize + entryCount * FdtConstants.DtboEntrySize);

    /// <summary>
    /// Hand-encodes a DTBO image exactly as AOSP mkdtboimg.py would: '>8I' big-endian
    /// header, per-entry descriptors with non-zero id/rev/custom, contiguous blobs.
    /// <para>完全按 AOSP mkdtboimg.py 的方式手工编码 DTBO：'>8I' 大端表头、带非零
    /// id/rev/custom 的条目描述符、连续数据块。</para>
    /// </summary>
    private static byte[] BuildUpstreamEncodedImage(out byte[] blob0, out byte[] blob1)
    {
        blob0 = BuildDtb("upstream-a");
        blob1 = BuildDtb("upstream-b");

        var headerSize = 32;
        var entrySize = 32;
        var blob0Offset = headerSize + 2 * entrySize;
        var blob1Offset = blob0Offset + blob0.Length;
        var totalSize = blob1Offset + blob1.Length;

        var image = new byte[totalSize];
        WriteU32BE(image, 0, FdtConstants.DtboMagic);
        WriteU32BE(image, 4, (uint)totalSize);
        WriteU32BE(image, 8, (uint)headerSize);
        WriteU32BE(image, 12, (uint)entrySize);
        WriteU32BE(image, 16, 2u);
        WriteU32BE(image, 20, (uint)headerSize); // dt_entries_offset = header_size（mkdtboimg）。
        WriteU32BE(image, 24, 2048u);
        WriteU32BE(image, 28, 0u);

        // entry 0: size, offset, id, rev, custom0..3
        WriteU32BE(image, headerSize + 0, (uint)blob0.Length);
        WriteU32BE(image, headerSize + 4, (uint)blob0Offset);
        WriteU32BE(image, headerSize + 8, 0x11111111);
        WriteU32BE(image, headerSize + 12, 0x00000001);
        WriteU32BE(image, headerSize + 16, 0x22222222);
        // entry 1
        WriteU32BE(image, headerSize + 32 + 0, (uint)blob1.Length);
        WriteU32BE(image, headerSize + 32 + 4, (uint)blob1Offset);
        WriteU32BE(image, headerSize + 32 + 8, 0x33333333);
        WriteU32BE(image, headerSize + 32 + 12, 0x00000002);

        Array.Copy(blob0, 0, image, blob0Offset, blob0.Length);
        Array.Copy(blob1, 0, image, blob1Offset, blob1.Length);
        return image;
    }

    [Fact]
    public void Read_UpstreamEncodedLayout_ExtractsBlobsAndMetadata()
    {
        var image = BuildUpstreamEncodedImage(out var blob0, out var blob1);

        var (header, entries, dtbs) = DtboFormat.Read(image);

        Assert.Equal(2u, header.EntryCount);
        Assert.Equal((uint)image.Length, header.TotalSize);
        Assert.Equal(0x11111111u, entries[0].Id);
        Assert.Equal(0x00000001u, entries[0].Rev);
        Assert.Equal(0x22222222u, entries[0].Custom0);
        Assert.Equal(0x33333333u, entries[1].Id);
        Assert.Equal(blob0, dtbs[0]);
        Assert.Equal(blob1, dtbs[1]);
    }

    [Fact]
    public void Read_V1_CompressedEntries_AreDecompressed()
    {
        var raw = BuildDtb("compressed-board");

        // zlib（flags=1）与 gzip（flags=2）压缩条目，version=1。
        // zlib (flags=1) and gzip (flags=2) entries with version=1.
        var zlibBlob = Compress(raw, zlib: true);
        var gzipBlob = Compress(raw, zlib: false);

        var headerSize = 32;
        var entrySize = 32;
        var blob0Offset = (uint)(headerSize + 2 * entrySize);
        var blob1Offset = blob0Offset + (uint)zlibBlob.Length;
        var totalSize = blob1Offset + (uint)gzipBlob.Length;

        var image = new byte[totalSize];
        WriteU32BE(image, 0, FdtConstants.DtboMagic);
        WriteU32BE(image, 4, totalSize);
        WriteU32BE(image, 8, (uint)headerSize);
        WriteU32BE(image, 12, (uint)entrySize);
        WriteU32BE(image, 16, 2u);
        WriteU32BE(image, 20, (uint)headerSize); // dt_entries_offset = header_size（mkdtboimg）。
        WriteU32BE(image, 24, 2048u);
        WriteU32BE(image, 28, 1u); // version 1 → flags 生效。

        WriteU32BE(image, headerSize, (uint)zlibBlob.Length);
        WriteU32BE(image, headerSize + 4, blob0Offset);
        WriteU32BE(image, headerSize + 16, DtboFormat.CompressionZlib);
        WriteU32BE(image, headerSize + 32, (uint)gzipBlob.Length);
        WriteU32BE(image, headerSize + 36, blob1Offset);
        WriteU32BE(image, headerSize + 48, DtboFormat.CompressionGzip);

        Array.Copy(zlibBlob, 0, image, blob0Offset, zlibBlob.Length);
        Array.Copy(gzipBlob, 0, image, blob1Offset, gzipBlob.Length);

        var (_, _, dtbs) = DtboFormat.Read(image);

        Assert.Equal(2, dtbs.Count);
        Assert.Equal(raw, dtbs[0]);
        Assert.Equal(raw, dtbs[1]);
    }

    private static byte[] Compress(byte[] data, bool zlib)
    {
        using var input = new MemoryStream(data, writable: false);
        using var output = new MemoryStream();
        Stream compressor = zlib
            ? new System.IO.Compression.ZLibStream(output, System.IO.Compression.CompressionLevel.SmallestSize)
            : new System.IO.Compression.GZipStream(output, System.IO.Compression.CompressionLevel.SmallestSize);
        input.CopyTo(compressor);
        compressor.Dispose();
        return output.ToArray();
    }

    [Fact]
    public void Read_BadMagic_Throws()
    {
        var image = DtboFormat.Pack([BuildDtb("x")]);
        image[0] = 0x00; // 破坏大端魔数首字节。
        Assert.ThrowsAny<InvalidDataException>(() => DtboFormat.Read(image));
    }

    [Fact]
    public void Read_TruncatedBlob_Throws()
    {
        var image = DtboFormat.Pack([BuildDtb("y")]);
        var truncated = image[..^16];
        // total_size 仍指向原始大小，数据块越界必须显式失败。
        // total_size still points at the original size; an out-of-bounds blob must fail loudly.
        Assert.ThrowsAny<InvalidDataException>(() => DtboFormat.Read(truncated));
    }

    [Fact]
    public async Task Handler_DetectsAndUnpacksRealDtboImage()
    {
        FirmwareFormatRegistry.RegisterAll();
        var root = Path.Combine(Path.GetTempPath(), "uotan-dtbo-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var image = DtboFormat.Pack([BuildDtb("handler-a"), BuildDtb("handler-b")]);
            var path = Path.Combine(root, "dtbo.img");
            await File.WriteAllBytesAsync(path, image, TestContext.Current.CancellationToken);

            var handler = FormatDetector.Detect(path);
            Assert.Equal("Device Tree (DTB/DTBO)", handler?.FormatName);

            var metadata = await handler!.ReadMetadataAsync(path, TestContext.Current.CancellationToken);
            Assert.True(metadata.Success, metadata.Summary);
            Assert.Equal("2", metadata.Metadata?["EntryCount"]);

            var outDir = Path.Combine(root, "out");
            var unpack = await handler.UnpackAsync(path, outDir, null, TestContext.Current.CancellationToken);
            Assert.True(unpack.Success, unpack.Summary);
            Assert.Equal(BuildDtb("handler-a"), await File.ReadAllBytesAsync(Path.Combine(outDir, "overlay_00.dtb"), TestContext.Current.CancellationToken));
            Assert.Equal(BuildDtb("handler-b"), await File.ReadAllBytesAsync(Path.Combine(outDir, "overlay_01.dtb"), TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
