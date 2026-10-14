using UotanStudio.Firmware.FileSystem;
using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Firmware.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.FileSystem.Erofs.Models.Inodes;
using UotanStudio.Firmware.FileSystem.Erofs.Parser;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Guards the fragmented tail shape: a compressed file whose final window fits one physical cluster stores
/// that window in the shared packed inode and names it from the inode's last index rows, a file whose whole
/// payload fits one physical cluster becomes a whole-file fragment with the simplified map header, and a
/// fragments build that ends up with nothing to pack fails loudly instead of advertising a packed inode the
/// image does not carry. The preserved-rebuild path is covered in both directions.
/// <para>守护 fragment 尾部形态：末尾窗口能装进一个物理簇的压缩文件把该窗口存放在共享 packed inode 中并由
/// inode 的最后几行索引指名；整份负载能装进一个物理簇的文件成为带简化映射头的整文件 fragment；最终无物可打包
/// 的 fragments 构建显式失败，而不是 advertise 一个镜像并未携带的 packed inode。保留式重建路径按两个方向覆盖。</para>
/// </summary>
public sealed class ErofsTailFragmentTests
{
    private const int BlockSize = 4096;

    private const ulong PinnedBuildTime = 1_700_000_000UL;

    [Fact]
    public void Build_CompressibleFileTail_PlacesFinalWindowInPackedInode()
    {
        var payload = Compressible(200_000);
        using var source = new TempDirectory();
        source.WriteBytes("big.bin", payload);
        var options = PinnedOptions();
        options.Fragments = true;

        var image = new ErofsBuilder().Build(source.Path, options);

        using var parser = new ErofsParser(image);
        Assert.NotEqual(0UL, parser.Superblock.PackedNid);
        var record = parser.ReadRebuildOptions().SourceInodesByPath!["big.bin"];
        Assert.Equal(ErofsFragmentShape.Tail, record.SourceFragmentShape);
        Assert.Equal(ErofsDataLayout.CompactIndexes, record.DataLayout);
        Assert.Equal(payload, parser.ReadFileBytes("big.bin"));
    }

    [Fact]
    public void Build_SubBlockCompressibleFileWithFragments_ProducesWholeFileFragment()
    {
        var payload = Compressible(3000);
        using var source = new TempDirectory();
        source.WriteBytes("small.bin", payload);
        var options = PinnedOptions();
        options.Fragments = true;

        var image = new ErofsBuilder().Build(source.Path, options);

        using var parser = new ErofsParser(image);
        Assert.NotEqual(0UL, parser.Superblock.PackedNid);
        var record = parser.ReadRebuildOptions().SourceInodesByPath!["small.bin"];
        Assert.Equal(ErofsFragmentShape.WholeFile, record.SourceFragmentShape);
        Assert.Equal(ErofsDataLayout.Compressed, record.DataLayout);
        Assert.Equal(payload, parser.ReadFileBytes("small.bin"));
    }

    [Fact]
    public void Build_FragmentsWithoutQualifyingNode_FailsLoudly()
    {
        using var source = new TempDirectory();
        source.WriteBytes("random.bin", Incompressible(3 * BlockSize));
        var options = PinnedOptions();
        options.Fragments = true;

        var exception = Assert.Throws<FileSystemUnsupportedFeatureException>(
            () => new ErofsBuilder().Build(source.Path, options));

        Assert.Contains("no node qualified", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_FragmentsWithAllFilesAsFragments_KeepsPackingSubBlockFiles()
    {
        var payload = Compressible(3000);
        using var source = new TempDirectory();
        source.WriteBytes("small.bin", payload);
        var options = PinnedOptions();
        options.Fragments = true;
        options.AllFilesAsFragments = true;

        var image = new ErofsBuilder().Build(source.Path, options);

        using var parser = new ErofsParser(image);
        Assert.NotEqual(0UL, parser.Superblock.PackedNid);
        Assert.Equal(ErofsFragmentShape.WholeFile,
            parser.ReadRebuildOptions().SourceInodesByPath!["small.bin"].SourceFragmentShape);
        Assert.Equal(payload, parser.ReadFileBytes("small.bin"));
    }

    [Fact]
    public void Build_FragmentsAcceptedByValidateAlongsideTailPacking()
    {
        var options = new ErofsOptions
        {
            BlockSize = BlockSize,
            Compress = true,
            Fragments = true,
            TailPacking = true,
        };

        options.Validate();

        Assert.True(options.TailPacking);
        Assert.True(options.Fragments);
    }

    [Fact]
    public void PreservedRebuild_TailFragmentSource_KeepsTailFragment()
    {
        var payload = Compressible(200_000);
        using var source = new TempDirectory();
        source.WriteBytes("big.bin", payload);
        var options = PinnedOptions();
        options.Fragments = true;
        var image = new ErofsBuilder().Build(source.Path, options);

        using var extracted = new TempDirectory();
        byte[] rebuilt;
        using (var parser = new ErofsParser(image))
        {
            var restored = parser.ReadRebuildOptions();
            parser.ExtractToDirectory(extracted.Path);
            rebuilt = new ErofsBuilder().Build(extracted.Path, restored);
        }

        using var reparsed = new ErofsParser(rebuilt);
        Assert.Equal(ErofsFragmentShape.Tail,
            reparsed.ReadRebuildOptions().SourceInodesByPath!["big.bin"].SourceFragmentShape);
        Assert.Equal(payload, reparsed.ReadFileBytes("big.bin"));
    }

    [Fact]
    public void PreservedRebuild_WholeFileFragmentSource_KeepsWholeFileFragment()
    {
        var payload = Compressible(3000);
        using var source = new TempDirectory();
        source.WriteBytes("small.bin", payload);
        var options = PinnedOptions();
        options.Fragments = true;
        var image = new ErofsBuilder().Build(source.Path, options);

        using var extracted = new TempDirectory();
        byte[] rebuilt;
        using (var parser = new ErofsParser(image))
        {
            var restored = parser.ReadRebuildOptions();
            parser.ExtractToDirectory(extracted.Path);
            rebuilt = new ErofsBuilder().Build(extracted.Path, restored);
        }

        using var reparsed = new ErofsParser(rebuilt);
        Assert.Equal(ErofsFragmentShape.WholeFile,
            reparsed.ReadRebuildOptions().SourceInodesByPath!["small.bin"].SourceFragmentShape);
        Assert.Equal(payload, reparsed.ReadFileBytes("small.bin"));
    }

    [Fact]
    public void PreservedRebuild_InventedTailFragment_FailsLoudly()
    {
        var payload = Compressible(200_000);
        using var source = new TempDirectory();
        source.WriteBytes("big.bin", payload);
        var options = PinnedOptions();
        options.Fragments = true;
        options.SourceInodes = new Dictionary<ulong, ErofsInode>
        {
            [ulong.MaxValue - 1] = SourceRecord("big.bin", (ulong)payload.Length, ErofsFragmentShape.None),
        };
        options.SourceInodesByPath = new Dictionary<string, ErofsInode>(StringComparer.Ordinal)
        {
            ["big.bin"] = options.SourceInodes[ulong.MaxValue - 1],
        };

        var exception = Assert.Throws<FileSystemUnsupportedFeatureException>(
            () => new ErofsBuilder().Build(source.Path, options));

        Assert.Contains("would invent a packed fragment", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PreservedRebuild_MissingTailFragment_FailsLoudly()
    {
        using var source = new TempDirectory();
        source.WriteBytes("random.bin", Incompressible(3 * BlockSize));
        var options = PinnedOptions();
        options.Fragments = true;
        options.SourceInodes = new Dictionary<ulong, ErofsInode>
        {
            [ulong.MaxValue - 1] = SourceRecord("random.bin", 3UL * BlockSize, ErofsFragmentShape.Tail, compressed: false),
        };
        options.SourceInodesByPath = new Dictionary<string, ErofsInode>(StringComparer.Ordinal)
        {
            ["random.bin"] = options.SourceInodes[ulong.MaxValue - 1],
        };

        Assert.ThrowsAny<FileSystemException>(() => new ErofsBuilder().Build(source.Path, options));
    }

    private static ErofsOptions PinnedOptions() => new()
    {
        BlockSize = BlockSize,
        Compress = true,
        Algorithm = ErofsCompressionAlgorithm.Lz4,
        MaxPclusterSize = BlockSize,
        SuperblockChecksum = false,
        Uuid = new byte[16],
        BuildTime = PinnedBuildTime,
    };

    private static ErofsInode SourceRecord(string path, ulong size, ErofsFragmentShape shape, bool compressed = true)
    {
        var record = new ErofsInode
        {
            Layer = ErofsInodeLayer.Source,
            DataLayout = ErofsDataLayout.CompactIndexes,
            Idata = new ErofsCompressedIdata { CompactedIndexes = true },
            Size = size,
            Mode = ErofsInode.ModeRegularFile | 0x1A4,
            SourcePath = path,
            SourceCompressed = compressed,
            SourceFragmentShape = shape,
            LogicalClusterSize = BlockSize,
        };
        return record;
    }

    private static byte[] Incompressible(int length)
    {
        var payload = new byte[length];
        var state = 0x9E3779B9u;
        for (var i = 0; i < payload.Length; i++)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            payload[i] = (byte)(state >> 24);
        }

        return payload;
    }

    private static byte[] Compressible(int length)
    {
        var payload = new byte[length];
        var position = 0;
        for (var i = 0; position < payload.Length; i++)
        {
            var line = System.Text.Encoding.ASCII.GetBytes(
                $"UotanStudio EROFS tail fragment probe line {i:D8}\n");
            var count = Math.Min(line.Length, payload.Length - position);
            line.AsSpan(0, count).CopyTo(payload.AsSpan(position));
            position += count;
        }

        return payload;
    }
}