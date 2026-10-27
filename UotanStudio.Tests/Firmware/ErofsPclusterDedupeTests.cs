using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Firmware.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.FileSystem.Erofs.Parser;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Guards the separation of the two dedupe comparisons: the repository's whole-file content-hash pass stays
/// gated on <c>Dedup</c> while <c>DedupeMode</c> is <c>WholeFile</c>, and the encoded-pcluster reuse this writer implements
/// is selected by <c>DedupeMode=Pcluster</c> alone so switching it on can never change a build
/// that leaves the mode at its default. The reuse is observable through the saved-byte statistic and the
/// image size, and the profile path carries the mode across a capture/restore round trip.
/// <para>守护两种去重比较的分离：仓库的整文件内容哈希处理仍由 <c>Dedup</c> 门控且仅在 <c>DedupeMode</c> 为
/// <c>WholeFile</c> 时生效，而本写入方实现的编码后 pcluster 复用仅由 <c>DedupeMode=Pcluster</c> 选择，因此启用它
/// 绝不会改变把模式留在默认值的构建。复用可通过省下字节的统计量与镜像大小观察，档案路径则让该模式跨采集/还原
/// 往返保留。</para>
/// </summary>
public sealed class ErofsPclusterDedupeTests
{
    private const int BlockSize = 4096;

    private const ulong PinnedBuildTime = 1_700_000_000UL;

    [Fact]
    public void Build_TwoIdenticalCompressibleFilesWithPclusterMode_ReusesEncodedFrames()
    {
        var payload = Compressible(3 * BlockSize);
        using var source = new TempDirectory();
        source.WriteBytes("a.bin", payload);
        source.WriteBytes("b.bin", payload);
        var options = PinnedOptions();
        options.DedupeMode = ErofsDedupeMode.Pcluster;

        var image = new ErofsBuilder().Build(source.Path, options);

        Assert.True(options.SavedByDeduplication > 0,
            $"expected the reused frames to be reported; saw {options.SavedByDeduplication}.");
        using var parser = new ErofsParser(image);
        Assert.Equal(payload, parser.ReadFileBytes("a.bin"));
        Assert.Equal(payload, parser.ReadFileBytes("b.bin"));
    }

    [Fact]
    public void Build_PclusterModeShrinksImageAgainstNoDedupe()
    {
        var payload = Compressible(3 * BlockSize);
        using var source = new TempDirectory();
        source.WriteBytes("a.bin", payload);
        source.WriteBytes("b.bin", payload);
        var dedupeOptions = PinnedOptions();
        dedupeOptions.DedupeMode = ErofsDedupeMode.Pcluster;
        var plainOptions = PinnedOptions();

        var deduped = new ErofsBuilder().Build(source.Path, dedupeOptions);
        var plain = new ErofsBuilder().Build(source.Path, plainOptions);

        Assert.True(deduped.Length < plain.Length,
            $"pcluster reuse must shrink the image; deduped={deduped.Length} plain={plain.Length}.");
        Assert.Null(plainOptions.SavedByDeduplication);
    }

    [Fact]
    public void Build_DefaultDedupeMode_KeepsWholeFileReuse()
    {
        var payload = Incompressible(3 * BlockSize);
        using var source = new TempDirectory();
        source.WriteBytes("a.bin", payload);
        source.WriteBytes("b.bin", payload);
        var dedupeOptions = PinnedOptions();
        dedupeOptions.Compress = false;
        dedupeOptions.Dedup = true;
        var plainOptions = PinnedOptions();
        plainOptions.Compress = false;

        var deduped = new ErofsBuilder().Build(source.Path, dedupeOptions);
        var plain = new ErofsBuilder().Build(source.Path, plainOptions);

        Assert.True(deduped.Length < plain.Length,
            $"whole-file reuse must still shrink the image; deduped={deduped.Length} plain={plain.Length}.");
        Assert.Null(dedupeOptions.SavedByDeduplication);
    }

    [Fact]
    public void Build_WholeFileModeWithoutIdenticalFiles_ReportsNoPclusterSavings()
    {
        using var source = new TempDirectory();
        source.WriteBytes("a.bin", Compressible(3 * BlockSize));
        source.WriteBytes("b.bin", Compressible(2 * BlockSize));
        var options = PinnedOptions();
        options.Dedup = true;

        var image = new ErofsBuilder().Build(source.Path, options);

        Assert.Null(options.SavedByDeduplication);
        using var parser = new ErofsParser(image);
        Assert.Equal(3 * BlockSize, parser.ReadFileBytes("a.bin").Length);
        Assert.Equal(2 * BlockSize, parser.ReadFileBytes("b.bin").Length);
    }

    [Fact]
    public void Build_WholeFileModeWithIdenticalCompressedFiles_DoesNotReusePclusters()
    {
        var payload = Compressible(3 * BlockSize);
        using var source = new TempDirectory();
        source.WriteBytes("a.bin", payload);
        source.WriteBytes("b.bin", payload);
        var options = PinnedOptions();
        options.Dedup = true;

        var image = new ErofsBuilder().Build(source.Path, options);

        Assert.Null(options.SavedByDeduplication);
        using var parser = new ErofsParser(image);
        Assert.Equal(payload, parser.ReadFileBytes("a.bin"));
        Assert.Equal(payload, parser.ReadFileBytes("b.bin"));
    }

    [Fact]
    public void Build_PclusterModeWithoutDuplicateFrames_ReportsNoSavings()
    {
        using var source = new TempDirectory();
        source.WriteBytes("a.bin", Compressible(3 * BlockSize));
        source.WriteBytes("b.bin", CompressibleDistinct(3 * BlockSize));
        var options = PinnedOptions();
        options.DedupeMode = ErofsDedupeMode.Pcluster;

        var image = new ErofsBuilder().Build(source.Path, options);

        Assert.Null(options.SavedByDeduplication);
        using var parser = new ErofsParser(image);
        Assert.Equal(3 * BlockSize, parser.ReadFileBytes("a.bin").Length);
        Assert.Equal(3 * BlockSize, parser.ReadFileBytes("b.bin").Length);
    }

    [Fact]
    public void MetadataCaptureRestore_PreservesDedupeMode()
    {
        var options = PinnedOptions();
        options.DedupeMode = ErofsDedupeMode.Pcluster;

        var restored = ErofsRebuildMetadata.Restore(ErofsRebuildMetadata.Capture(options));

        Assert.Equal(ErofsDedupeMode.Pcluster, restored.DedupeMode);
    }

    [Fact]
    public void MetadataRestore_ProfileWithoutDedupeMode_KeepsWholeFileDefault()
    {
        var options = PinnedOptions();
        var document = ErofsRebuildMetadata.Capture(options);
        document.DedupeMode = null;

        var restored = ErofsRebuildMetadata.Restore(document);

        Assert.Equal(ErofsDedupeMode.WholeFile, restored.DedupeMode);
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

    private static byte[] Compressible(int length) => Compressible(length, "alpha");

    private static byte[] CompressibleDistinct(int length) => Compressible(length, "beta");

    private static byte[] Compressible(int length, string marker)
    {
        var payload = new byte[length];
        var position = 0;
        for (var i = 0; position < payload.Length; i++)
        {
            var line = System.Text.Encoding.ASCII.GetBytes(
                $"UotanStudio EROFS pcluster dedupe frame {marker} {i:D8}\n");
            var count = Math.Min(line.Length, payload.Length - position);
            line.AsSpan(0, count).CopyTo(payload.AsSpan(position));
            position += count;
        }

        return payload;
    }
}