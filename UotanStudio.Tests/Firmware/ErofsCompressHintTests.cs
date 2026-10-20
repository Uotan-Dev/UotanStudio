using System.Security.Cryptography;
using UotanStudio.Firmware.FileSystem;
using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Firmware.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.FileSystem.Erofs.Parser;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class ErofsCompressHintTests
{
    private const int BlockSize = 4096;

    private const ulong PinnedBuildTime = 1_700_000_000UL;

    [Fact]
    public void Build_CompressHintZeroPcluster_LeavesMatchedFileUncompressed()
    {
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", Compressible(8 * BlockSize));
        var options = PinnedOptions();
        options.CompressHints = [@"0 data\.bin"];

        var image = new ErofsBuilder().Build(source.Path, options);

        using var parser = new ErofsParser(image);
        var record = parser.ReadRebuildOptions().SourceInodesByPath!["data.bin"];
        Assert.False(record.IsCompressed);
        Assert.Equal(8 * BlockSize, parser.ReadFileBytes("data.bin").Length);
    }

    [Fact]
    public void Build_CompressHintZeroPcluster_LeavesUnmatchedFileCompressed()
    {
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", Compressible(8 * BlockSize));
        source.WriteBytes("other.bin", Compressible(8 * BlockSize));
        var options = PinnedOptions();
        options.CompressHints = [@"0 data\.bin"];

        var image = new ErofsBuilder().Build(source.Path, options);

        using var parser = new ErofsParser(image);
        var records = parser.ReadRebuildOptions().SourceInodesByPath!;
        Assert.False(records["data.bin"].IsCompressed);
        Assert.True(records["other.bin"].IsCompressed);
    }

    [Fact]
    public void Build_CompressHintFirstMatchingRuleWins()
    {
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", Compressible(8 * BlockSize));
        var options = PinnedOptions();
        options.CompressHints = [@"0 data\.bin", $@"{BlockSize} data\.bin"];

        var image = new ErofsBuilder().Build(source.Path, options);

        using var parser = new ErofsParser(image);
        var record = parser.ReadRebuildOptions().SourceInodesByPath!["data.bin"];
        Assert.False(record.IsCompressed);
    }

    [Fact]
    public void Build_CompressHintUnanchoredPattern_MatchesNestedPath()
    {
        using var source = new TempDirectory();
        source.WriteBytes("data/block.bin", Compressible(8 * BlockSize));
        var options = PinnedOptions();
        options.CompressHints = [@"0 block\.bin"];

        var image = new ErofsBuilder().Build(source.Path, options);

        using var parser = new ErofsParser(image);
        var record = parser.ReadRebuildOptions().SourceInodesByPath!["data/block.bin"];
        Assert.False(record.IsCompressed);
    }

    [Fact]
    public void Build_CompressHintCommentAndBlankLines_AreIgnoredWithoutWarnings()
    {
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", Compressible(8 * BlockSize));
        var options = PinnedOptions();
        options.CompressHints = ["# a comment", "", "   ", @"0 data\.bin"];

        var image = new ErofsBuilder().Build(source.Path, options);

        using var parser = new ErofsParser(image);
        var record = parser.ReadRebuildOptions().SourceInodesByPath!["data.bin"];
        Assert.False(record.IsCompressed);
        Assert.Empty(options.BuildWarnings);
    }

    [Fact]
    public void Build_CompressHintInvalidRegex_RecordsWarningAndKeepsGlobalCompression()
    {
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", Compressible(8 * BlockSize));
        var options = PinnedOptions();
        options.CompressHints = [@"4096 *.so"];

        var image = new ErofsBuilder().Build(source.Path, options);

        using var parser = new ErofsParser(image);
        var record = parser.ReadRebuildOptions().SourceInodesByPath!["data.bin"];
        Assert.True(record.IsCompressed);
        Assert.Equal(8 * BlockSize, parser.ReadFileBytes("data.bin").Length);
        var warning = Assert.Single(options.BuildWarnings);
        Assert.Contains("*.so", warning, StringComparison.Ordinal);
        Assert.Contains("not a usable regular expression", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_CompressHintMisalignedPclusterSize_RecordsWarningAndKeepsGlobalCompression()
    {
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", Compressible(8 * BlockSize));
        var options = PinnedOptions();
        options.CompressHints = [@"5000 data\.bin"];

        var image = new ErofsBuilder().Build(source.Path, options);

        using var parser = new ErofsParser(image);
        var record = parser.ReadRebuildOptions().SourceInodesByPath!["data.bin"];
        Assert.True(record.IsCompressed);
        var warning = Assert.Single(options.BuildWarnings);
        Assert.Contains("5000", warning, StringComparison.Ordinal);
        Assert.Contains("not a multiple", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_CompressHintAboveConfiguredMaximum_FailsLoudly()
    {
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", Compressible(8 * BlockSize));
        var options = PinnedOptions();
        options.CompressHints = [@"8192 data\.bin"];

        var exception = Assert.Throws<FileSystemUnsupportedFeatureException>(
            () => new ErofsBuilder().Build(source.Path, options));

        Assert.Contains("8192", exception.Message, StringComparison.Ordinal);
        Assert.Contains("configured maximum", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_CompressHintUnknownAlgorithmIndex_FailsLoudly()
    {
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", Compressible(8 * BlockSize));
        var options = PinnedOptions();
        options.CompressHints = [@"4096 5 data\.bin"];

        var exception = Assert.Throws<FileSystemUnsupportedFeatureException>(
            () => new ErofsBuilder().Build(source.Path, options));

        Assert.Contains("compression configuration 5", exception.Message, StringComparison.Ordinal);
        Assert.Contains("configures 1 codec", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_WithoutHints_ProducesIdenticalBytesToAbsentKey()
    {
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", Compressible(8 * BlockSize));
        source.WriteBytes("other.bin", Deterministic(3 * BlockSize));
        var withoutKey = PinnedOptions();
        var emptyKey = PinnedOptions();
        emptyKey.CompressHints = null;

        var first = new ErofsBuilder().Build(source.Path, withoutKey);
        var second = new ErofsBuilder().Build(source.Path, emptyKey);

        Assert.Equal(Convert.ToHexString(SHA256.HashData(first)), Convert.ToHexString(SHA256.HashData(second)));
        Assert.Empty(withoutKey.BuildWarnings);
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

    private static byte[] Compressible(int length) => Enumerable.Repeat((byte)0x5A, length).ToArray();

    private static byte[] Deterministic(int length)
    {
        var payload = new byte[length];
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)((i * 31 + i / 7) & 0xFF);
        }

        return payload;
    }
}