using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Firmware.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.FileSystem.Erofs.Parser;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public class ErofsZtailpackingTests
{
    private const int BlockSize = 4096;

    [Fact]
    public void Build_ZtailpackedTail_ReadsBackThroughTheManagedParser()
    {
        // A compressible file whose final compressed cluster is smaller than one block
        // is the shape ztailpacking stores beside the index. The parser computes the
        // inline tail's position from the index table end, while the writer places it
        // right-aligned in the inode record; when those two anchors differ the read
        // back fails, so this round trip pins the agreement.
        var payload = new byte[24 * 1024];
        var pattern = "the quick brown fox jumps over the lazy dog 0123456789\n"u8;
        for (var offset = 0; offset + pattern.Length <= payload.Length; offset += pattern.Length)
        {
            pattern.CopyTo(payload.AsSpan(offset));
        }

        using var source = new TempDirectory();
        source.WriteBytes("zt.bin", payload);

        var image = new ErofsBuilder().Build(source.Path, new ErofsOptions
        {
            BlockSize = BlockSize,
            Compress = true,
            Algorithm = ErofsCompressionAlgorithm.Lz4,
            CompressionLevel = 1,
            MaxPclusterSize = BlockSize,
            CompressedTailPacking = true,
            SuperblockChecksum = false,
            Uuid = new byte[16],
            BuildTime = 1_700_000_000,
        });

        using var parser = new ErofsParser(image);
        Assert.Equal(payload, parser.ReadFileBytes("zt.bin"));
    }

    [Fact]
    public void Build_CompactIndexZtailpackedTail_ReadsBackThroughTheManagedParser()
    {
        // The compact-index form is where the inode-record and index-end anchors can
        // diverge most: the inode record is a fixed 32 or 64 bytes while the compacted
        // index packs variable counts of entries.
        var payload = new byte[32 * 1024];
        var pattern = "lorem ipsum dolor sit amet consectetur adipiscing elit\n"u8;
        for (var offset = 0; offset + pattern.Length <= payload.Length; offset += pattern.Length)
        {
            pattern.CopyTo(payload.AsSpan(offset));
        }

        using var source = new TempDirectory();
        source.WriteBytes("zc.bin", payload);

        var image = new ErofsBuilder().Build(source.Path, new ErofsOptions
        {
            BlockSize = BlockSize,
            Compress = true,
            Algorithm = ErofsCompressionAlgorithm.Lz4,
            CompressionLevel = 1,
            MaxPclusterSize = BlockSize,
            UseCompactedIndexes = true,
            CompressedTailPacking = true,
            SuperblockChecksum = false,
            Uuid = new byte[16],
            BuildTime = 1_700_000_000,
        });

        using var parser = new ErofsParser(image);
        Assert.Equal(payload, parser.ReadFileBytes("zc.bin"));
    }
}