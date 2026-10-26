using System.Buffers.Binary;
using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Firmware.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.FileSystem.Erofs.Models;
using UotanStudio.Firmware.FileSystem.Erofs.Parser;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public class ErofsExtentIntraFileDedupeTests
{
    private const int BlockSize = 4096;

    [Fact]
    public void Build_TwoIdenticalFilesWithExtents_SecondFileNamesTheReusedAbsoluteAddress()
    {
        var payload = new byte[40 * 1024];
        var pattern = "lorem ipsum dolor sit amet consectetur adipiscing elit 0123456789\n"u8;
        for (var offset = 0; offset + pattern.Length <= payload.Length; offset += pattern.Length)
        {
            pattern.CopyTo(payload.AsSpan(offset));
        }

        using var source = new TempDirectory();
        source.WriteBytes("a.bin", payload);
        source.WriteBytes("b.bin", payload);

        var image = Build(source.Path, 16384);

        using var parser = new ErofsParser(image);
        Assert.Equal(payload, parser.ReadFileBytes("a.bin"));
        Assert.Equal(payload, parser.ReadFileBytes("b.bin"));

        var aStart = ReadFirstExtentPhysical(image, parser, "a.bin");
        var bStart = ReadFirstExtentPhysical(image, parser, "b.bin");
        Assert.Equal(aStart, bStart);
    }

    [Fact]
    public void Build_RepeatedFrameInOneFileWithExtents_ReadsBackEachSegment()
    {
        var repeated = RepeatedFramePayload();
        using var source = new TempDirectory();
        source.WriteBytes("r.bin", repeated);

        foreach (var maxPcluster in new[] { 16384 })
        {
            var image = Build(source.Path, maxPcluster);
            using var parser = new ErofsParser(image);
            Assert.Equal(repeated, parser.ReadFileBytes("r.bin"));

            var addresses = ReadExtentPhysicals(image, parser, "r.bin");
            Assert.True(addresses.Count >= 2,
                $"expected several extents for the repeated payload, saw {addresses.Count}.");
        }
    }

    private static byte[] RepeatedFramePayload()
    {
        var block = new byte[16 * 1024];
        var pattern = "the quick brown fox jumps over the lazy dog 0123456789\n"u8;
        for (var offset = 0; offset + pattern.Length <= block.Length; offset += pattern.Length)
        {
            pattern.CopyTo(block.AsSpan(offset));
        }

        var random = new Random(20261021);
        var payload = new byte[3 * (block.Length + 8 * 1024)];
        var cursor = 0;
        for (var round = 0; round < 3; round++)
        {
            block.CopyTo(payload.AsSpan(cursor));
            cursor += block.Length;
            var gap = new byte[8 * 1024];
            random.NextBytes(gap);
            gap.CopyTo(payload.AsSpan(cursor));
            cursor += gap.Length;
        }

        return payload;
    }

    private static long ReadFirstExtentPhysical(byte[] image, ErofsParser parser, string path)
    {
        var addresses = ReadExtentPhysicals(image, parser, path);
        Assert.NotEmpty(addresses);
        return addresses[0];
    }

    private static List<long> ReadExtentPhysicals(byte[] image, ErofsParser parser, string path)
    {
        var mapHeaderOffset = checked((int)parser.CompressedMapHeaderOffset(path));
        var header = ZErofsMapHeader.FromBytes(image.AsSpan(mapHeaderOffset, ZErofsMapHeader.Size));
        var recordSize = ZErofsMapHeader.ExtentRecordSize(header.Advise);
        var addresses = new List<long>();
        var area = checked((int)ZErofsMapHeader.ExtentAreaBase(mapHeaderOffset, recordSize));
        if (recordSize <= 4)
        {
            addresses.Add(checked((long)BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(area, 8))));
            return addresses;
        }

        var count = checked((int)(BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(mapHeaderOffset))
            | ((ulong)header.ExtentsHi << 32)));
        for (var i = 0; i < count; i++)
        {
            var record = area + i * recordSize;
            var low = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(record + 4, 4));
            var high = recordSize >= 16
                ? BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(record + 8, 4))
                : 0u;
            addresses.Add(low | ((long)high << 32));
        }

        return addresses;
    }

    private static byte[] Build(string path, int maxPclusterSize) => new ErofsBuilder().Build(path, new ErofsOptions
    {
        BlockSize = BlockSize,
        Compress = true,
        Algorithm = ErofsCompressionAlgorithm.Lz4,
        CompressionLevel = 1,
        MaxPclusterSize = maxPclusterSize,
        Dedup = true,
        DedupeMode = ErofsDedupeMode.Pcluster,
        UseExtentIndexes = ErofsTriStateFeature.Enabled,
        SuperblockChecksum = false,
        Uuid = new byte[16],
        BuildTime = 1_700_000_000,
    });
}