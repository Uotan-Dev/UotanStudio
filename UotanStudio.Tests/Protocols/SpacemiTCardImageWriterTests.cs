using System.Text;
using UotanStudio.Comm.Protocols.SpacemiT;
using UotanStudio.Compress;
using UotanStudio.Firmware.BlockImage.Gpt;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Protocols;

public class SpacemiTCardImageWriterTests
{
    private static readonly Guid LinuxDataGuid = new("0FC63DAF-8483-4772-8E79-3D69D8477DE4");
    private static readonly Guid EfiSystemGuid = new("C12A7328-F81F-11D2-BA4B-00A0C93EC93B");

    [Fact]
    public void Write_K1StyleTable_ProducesGptImageWithHiddenBootinfo()
    {
        using var temp = new TempDirectory();
        var bootinfoBytes = Pattern(80, 1);
        var fsblBytes = Pattern(64 * 1024, 2);
        var bootfsBytes = Pattern(100 * 1024, 4);
        var rootfsBytes = Pattern(300 * 1024, 6);
        var bootinfoPath = temp.WriteBytes("bootinfo_sd.bin", bootinfoBytes);
        var fsblPath = temp.WriteBytes("fsbl.bin", fsblBytes);
        var bootfsPath = temp.WriteBytes("bootfs.ext4", bootfsBytes);
        var rootfsPath = temp.WriteBytes("rootfs.ext4", rootfsBytes);
        const string json = """
        {
          "version": "1.0",
          "format": "gpt",
          "partitions": [
            { "name": "bootinfo", "hidden": true, "offset": "0", "size": "80", "image": "bootinfo_sd.bin" },
            { "name": "fsbl", "hidden": true, "offset": "128K", "size": "-", "image": "fsbl.bin" },
            { "name": "bootfs", "offset": "4M", "size": "1M", "image": "bootfs.ext4" },
            { "name": "rootfs", "size": "-", "image": "rootfs.ext4" }
          ]
        }
        """;
        var table = ParseTable(json);
        var target = Path.Combine(temp.Path, "card.img");
        var collector = new ProgressCollector();

        var result = SpacemiTCardImageWriter.Write(table, reference => reference switch
        {
            "bootinfo_sd.bin" => FileContent(bootinfoPath),
            "fsbl.bin" => FileContent(fsblPath),
            "bootfs.ext4" => FileContent(bootfsPath),
            "rootfs.ext4" => FileContent(rootfsPath),
            _ => null
        }, target, collector, TestContext.Current.CancellationToken);

        var expectedImageBytes = (5L * 1024 * 1024 + 300L * 1024 + 34L * 512 + 4095) / 4096 * 4096;
        Assert.Equal(expectedImageBytes, result.ImageSizeBytes);
        Assert.Equal(4, result.Partitions.Count);
        Assert.Equal(new SpacemiTCardWrittenPartition("bootinfo", 0, 80, false), result.Partitions[0]);
        Assert.Equal(new SpacemiTCardWrittenPartition("fsbl", 128L * 1024, 64L * 1024, false), result.Partitions[1]);
        Assert.Equal(new SpacemiTCardWrittenPartition("bootfs", 4L * 1024 * 1024, 1024L * 1024, true), result.Partitions[2]);
        Assert.Equal(new SpacemiTCardWrittenPartition("rootfs", 5L * 1024 * 1024, 300L * 1024, true), result.Partitions[3]);

        var image = File.ReadAllBytes(target);
        Assert.Equal(expectedImageBytes, image.Length);
        Assert.Equal(bootinfoBytes, image.AsSpan(0, bootinfoBytes.Length).ToArray());
        Assert.Equal(0x55, image[510]);
        Assert.Equal(0xAA, image[511]);
        Assert.Equal(fsblBytes, image.AsSpan(128 * 1024, fsblBytes.Length).ToArray());
        Assert.Equal(bootfsBytes, image.AsSpan(4 * 1024 * 1024, bootfsBytes.Length).ToArray());
        Assert.Equal(-1, image.AsSpan(4 * 1024 * 1024 + bootfsBytes.Length, 1024 * 1024 - bootfsBytes.Length).IndexOfAnyExcept((byte)0));
        Assert.Equal(rootfsBytes, image.AsSpan(5 * 1024 * 1024, rootfsBytes.Length).ToArray());
        Assert.Equal("EFI PART", Encoding.ASCII.GetString(image.AsSpan((int)(expectedImageBytes - 512), 8)));

        GptTable reread;
        using (var read = File.OpenRead(target))
        {
            reread = GptReader.Read(read, 512, (ulong)(expectedImageBytes / 512), TestContext.Current.CancellationToken);
        }

        Assert.Equal(2, reread.Partitions.Count);
        var bootfs = reread.FindPartition("bootfs");
        Assert.Equal(4L * 1024 * 1024 / 512, (long)bootfs.FirstLba);
        Assert.Equal(4L * 1024 * 1024 / 512 + (1024L * 1024 + 511) / 512 - 1, (long)bootfs.LastLba);
        var rootfs = reread.FindPartition("rootfs");
        Assert.Equal((4L * 1024 * 1024 + 1024L * 1024) / 512, (long)rootfs.FirstLba);
        Assert.Equal((4L * 1024 * 1024 + 1024L * 1024) / 512 + (300L * 1024 + 511) / 512 - 1, (long)rootfs.LastLba);

        Assert.Equal("done", collector.Events[^1].Phase);
        Assert.Equal(expectedImageBytes, collector.Events[^1].BytesTotal);
        Assert.Contains(collector.Events, e => e.Phase == "write-partition" && e.Partition == "bootinfo");
        Assert.Contains(collector.Events, e => e.Phase == "write-partition" && e.Partition == "rootfs");
    }

    [Fact]
    public void Write_GzipContentWithoutDeclaredLength_WritesDecompressedBytes()
    {
        using var temp = new TempDirectory();
        var payload = Pattern(70000, 3);
        var gzip = CompressionService.Compress(payload, CompressionFormat.Gzip);
        const string json = """
        {
          "format": "gpt",
          "partitions": [
            { "name": "data", "offset": "2M", "size": "-", "image": "data.img" }
          ]
        }
        """;
        var table = ParseTable(json);
        var target = Path.Combine(temp.Path, "card.img");

        var result = SpacemiTCardImageWriter.Write(table, reference => reference == "data.img"
            ? new SpacemiTCardContent(() => new MemoryStream(gzip), null)
            : null, target, cancellationToken: TestContext.Current.CancellationToken);

        var expectedImageBytes = (2L * 1024 * 1024 + payload.Length + 34L * 512 + 4095) / 4096 * 4096;
        Assert.Equal(expectedImageBytes, result.ImageSizeBytes);
        var written = Assert.Single(result.Partitions);
        Assert.Equal(2L * 1024 * 1024, written.OffsetBytes);
        Assert.Equal(payload.Length, written.LengthBytes);
        Assert.True(written.InGpt);

        var image = File.ReadAllBytes(target);
        Assert.Equal(expectedImageBytes, image.Length);
        Assert.Equal(payload, image.AsSpan(2 * 1024 * 1024, payload.Length).ToArray());
        var partitionEndBytes = (2L * 1024 * 1024 + payload.Length + 511) / 512 * 512;
        Assert.Equal(-1, image.AsSpan((int)(2L * 1024 * 1024 + payload.Length), (int)(partitionEndBytes - 2L * 1024 * 1024 - payload.Length)).IndexOfAnyExcept((byte)0));

        using (var read = File.OpenRead(target))
        {
            var reread = GptReader.Read(read, 512, (ulong)(expectedImageBytes / 512), TestContext.Current.CancellationToken);
            var entry = Assert.Single(reread.Partitions);
            Assert.Equal(2L * 1024 * 1024 / 512, (long)entry.FirstLba);
            Assert.Equal(2L * 1024 * 1024 / 512 + (payload.Length + 511) / 512 - 1, (long)entry.LastLba);
        }
    }

    [Fact]
    public void Write_RawContentWithoutDeclaredLength_TakesSizingPass()
    {
        using var temp = new TempDirectory();
        var payload = Pattern(123457, 5);
        const string json = """
        {
          "format": "gpt",
          "partitions": [
            { "name": "media", "offset": "8M", "size": "-", "image": "media.img" }
          ]
        }
        """;
        var table = ParseTable(json);
        var target = Path.Combine(temp.Path, "card.img");

        var result = SpacemiTCardImageWriter.Write(table, reference => reference == "media.img"
            ? new SpacemiTCardContent(() => new MemoryStream(payload), null)
            : null, target, cancellationToken: TestContext.Current.CancellationToken);

        var expectedImageBytes = (8L * 1024 * 1024 + payload.Length + 34L * 512 + 4095) / 4096 * 4096;
        Assert.Equal(expectedImageBytes, result.ImageSizeBytes);
        var written = Assert.Single(result.Partitions);
        Assert.Equal(8L * 1024 * 1024, written.OffsetBytes);
        Assert.Equal(payload.Length, written.LengthBytes);
        Assert.True(written.InGpt);

        var image = File.ReadAllBytes(target);
        Assert.Equal(expectedImageBytes, image.Length);
        Assert.Equal(payload, image.AsSpan(8 * 1024 * 1024, payload.Length).ToArray());

        using (var read = File.OpenRead(target))
        {
            var reread = GptReader.Read(read, 512, (ulong)(expectedImageBytes / 512), TestContext.Current.CancellationToken);
            var entry = Assert.Single(reread.Partitions);
            Assert.Equal(8L * 1024 * 1024 / 512, (long)entry.FirstLba);
            Assert.Equal(8L * 1024 * 1024 / 512 + (payload.Length + 511) / 512 - 1, (long)entry.LastLba);
        }
    }

    /// <remarks>
    /// The type GUIDs follow the vendor card layout's own name-to-type table (gadget.yaml): firmware slots carry
    /// vendor-chosen types, the system-boot partition carries the standard EFI type, and unlisted names fall back
    /// to the Linux filesystem data type the same layout uses for its data partitions.
    /// <para>类型 GUID 遵循厂商卡布局自身的名称→类型表（gadget.yaml）：固件槽位携带厂商选定的类型，
    /// system-boot 分区携带标准 EFI 类型，未列出的名称回退到同一布局数据分区使用的 Linux 文件系统数据类型。</para>
    /// </remarks>
    [Fact]
    public void Write_Partitions_CarryVendorTypeGuidsAndDeterministicUniqueIds()
    {
        using var temp = new TempDirectory();
        var envBytes = Pattern(16 * 1024, 6);
        var espBytes = Pattern(64 * 1024, 7);
        var extraBytes = Pattern(32 * 1024, 8);
        var envPath = temp.WriteBytes("env.bin", envBytes);
        var espPath = temp.WriteBytes("esp.img", espBytes);
        var extraPath = temp.WriteBytes("extra.img", extraBytes);
        const string json = """
        {
          "format": "gpt",
          "partitions": [
            { "name": "env", "offset": "1M", "size": "64K", "image": "env.bin" },
            { "name": "ESP", "offset": "4M", "size": "64K", "image": "esp.img" },
            { "name": "extra", "size": "-", "image": "extra.img" }
          ]
        }
        """;
        var table = ParseTable(json);
        var first = Path.Combine(temp.Path, "card-1.img");
        var second = Path.Combine(temp.Path, "card-2.img");
        SpacemiTCardContent? Resolve(string reference) => reference switch
        {
            "env.bin" => FileContent(envPath),
            "esp.img" => FileContent(espPath),
            "extra.img" => FileContent(extraPath),
            _ => null
        };

        SpacemiTCardImageWriter.Write(table, Resolve, first, cancellationToken: TestContext.Current.CancellationToken);
        SpacemiTCardImageWriter.Write(table, Resolve, second, cancellationToken: TestContext.Current.CancellationToken);

        GptTable reread;
        using (var read = File.OpenRead(first))
        {
            reread = GptReader.Read(read, 512, (ulong)((4L * 1024 * 1024 + 96L * 1024 + 34L * 512 + 4095) / 4096 * 4096 / 512), TestContext.Current.CancellationToken);
        }

        // The type GUIDs mirror the vendor layout's own table: vendor-chosen types for the firmware slots, the
        // standard EFI type for the system-boot partition, and the Linux data type as the fallback.
        // 类型 GUID 仿照厂商布局自身的表：固件槽位用厂商选定类型，system-boot 分区用标准 EFI 类型，回退为
        // Linux 数据类型。
        Assert.Equal(3, reread.Partitions.Count);
        Assert.Equal(new("64C85018-D1B1-48BF-8D09-76A005AB67CC"), reread.FindPartition("env").TypeId);
        Assert.Equal(EfiSystemGuid, reread.FindPartition("ESP").TypeId);
        Assert.Equal(LinuxDataGuid, reread.FindPartition("extra").TypeId);

        GptTable rereadAgain;
        using (var read = File.OpenRead(second))
        {
            rereadAgain = GptReader.Read(read, 512, (ulong)((4L * 1024 * 1024 + 96L * 1024 + 34L * 512 + 4095) / 4096 * 4096 / 512), TestContext.Current.CancellationToken);
        }

        Assert.Equal(reread.FindPartition("env").Id, rereadAgain.FindPartition("env").Id);
        Assert.Equal(reread.FindPartition("ESP").Id, rereadAgain.FindPartition("ESP").Id);
        Assert.Equal(reread.FindPartition("extra").Id, rereadAgain.FindPartition("extra").Id);
    }

    [Fact]
    public void Write_MtdTable_ThrowsInvalidData()
    {
        using var temp = new TempDirectory();
        var imagePath = temp.WriteBytes("u-boot.bin", Pattern(1024, 9));
        const string json = """
        {
          "format": "mtd",
          "partitions": [
            { "name": "uboot", "offset": "0", "size": "1M", "image": "u-boot.bin" }
          ]
        }
        """;
        var table = ParseTable(json);
        var target = Path.Combine(temp.Path, "card.img");

        Assert.Throws<InvalidDataException>(() => SpacemiTCardImageWriter.Write(table,
            _ => FileContent(imagePath), target, cancellationToken: TestContext.Current.CancellationToken));

        Assert.False(File.Exists(target));
    }

    [Theory]
    [InlineData("100")]
    [InlineData("512")]
    [InlineData("0")]
    public void Write_VisiblePartitionOffsetUnalignedOrInsideMetadata_ThrowsInvalidData(string offset)
    {
        using var temp = new TempDirectory();
        var imagePath = temp.WriteBytes("small.bin", Pattern(1024, 10));
        var json = $$"""
        {
          "format": "gpt",
          "partitions": [
            { "name": "small", "offset": "{{offset}}", "size": "4K", "image": "small.bin" }
          ]
        }
        """;
        var table = ParseTable(json);
        var target = Path.Combine(temp.Path, "card.img");

        Assert.Throws<InvalidDataException>(() => SpacemiTCardImageWriter.Write(table,
            _ => FileContent(imagePath), target, cancellationToken: TestContext.Current.CancellationToken));

        Assert.False(File.Exists(target));
    }

    [Fact]
    public void Write_OverlappingVisiblePartitions_ThrowsInvalidData()
    {
        using var temp = new TempDirectory();
        var alphaPath = temp.WriteBytes("alpha.bin", Pattern(1024, 11));
        var betaPath = temp.WriteBytes("beta.bin", Pattern(1024, 12));
        const string json = """
        {
          "format": "gpt",
          "partitions": [
            { "name": "alpha", "offset": "4M", "size": "1M", "image": "alpha.bin" },
            { "name": "beta", "offset": "4M", "size": "2M", "image": "beta.bin" }
          ]
        }
        """;
        var table = ParseTable(json);
        var target = Path.Combine(temp.Path, "card.img");

        Assert.Throws<InvalidDataException>(() => SpacemiTCardImageWriter.Write(table, reference => reference switch
        {
            "alpha.bin" => FileContent(alphaPath),
            "beta.bin" => FileContent(betaPath),
            _ => null
        }, target, cancellationToken: TestContext.Current.CancellationToken));

        Assert.False(File.Exists(target));
    }

    private static SpacemiTPartitionTable ParseTable(string json) =>
        SpacemiTPartitionTable.Parse(new MemoryStream(Encoding.UTF8.GetBytes(json)));

    private static SpacemiTCardContent FileContent(string path) =>
        new(() => File.OpenRead(path), new FileInfo(path).Length);

    private static byte[] Pattern(int length, byte seed)
    {
        var data = new byte[length];
        for (var index = 0; index < length; index++)
        {
            data[index] = (byte)(index * 31 + seed);
        }

        return data;
    }

    private sealed class ProgressCollector : IProgress<SpacemiTCardImageProgress>
    {
        public List<SpacemiTCardImageProgress> Events { get; } = [];

        public void Report(SpacemiTCardImageProgress value) => Events.Add(value);
    }
}