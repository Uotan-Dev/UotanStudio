using UotanStudio.Firmware.FileSystem.Erofs;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public class ErofsCompressionLevelTests
{
    [Fact]
    public void Validate_PerCodecCeilings_AcceptUpstreamBestLevels()
    {
        Assert.Equal(12, ErofsCompressionLevel.Validate(ErofsCompressionAlgorithm.Lz4, 12));
        Assert.Equal(9, ErofsCompressionLevel.Validate(ErofsCompressionAlgorithm.Deflate, 9));
        Assert.Equal(22, ErofsCompressionLevel.Validate(ErofsCompressionAlgorithm.Zstd, 22));
        Assert.Equal(109, ErofsCompressionLevel.Validate(ErofsCompressionAlgorithm.Lzma, 109));
        Assert.Equal(0, ErofsCompressionLevel.Validate(ErofsCompressionAlgorithm.Lzma, 0));
    }

    [Fact]
    public void Validate_LzmaExtremePresetRange_IsAccepted()
    {
        Assert.Equal(100, ErofsCompressionLevel.Validate(ErofsCompressionAlgorithm.Lzma, 100));
        Assert.Equal(105, ErofsCompressionLevel.Validate(ErofsCompressionAlgorithm.Lzma, 105));
    }

    [Theory]
    [InlineData(13)]
    [InlineData(100)]
    public void Validate_DeflateAboveItsCeiling_IsCappedToNine(int requested)
    {
        Assert.Equal(9, ErofsCompressionLevel.Validate(ErofsCompressionAlgorithm.Deflate, requested));
    }

    [Fact]
    public void Validate_ZstdAboveItsCeiling_IsCappedToTwentyTwo()
    {
        Assert.Equal(22, ErofsCompressionLevel.Validate(ErofsCompressionAlgorithm.Zstd, 1000));
    }

    [Fact]
    public void Validate_LzmaBelowItsFloor_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ErofsCompressionLevel.Validate(ErofsCompressionAlgorithm.Lzma, -2));
    }

    [Fact]
    public void Validate_UndefinedAlgorithm_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ErofsCompressionLevel.Validate((ErofsCompressionAlgorithm)99, 1));
    }

    [Fact]
    public void Validate_Lz4_KeepsAnyLevelBecauseTheFormatRecordsNone()
    {
        Assert.Equal(1000, ErofsCompressionLevel.Validate(ErofsCompressionAlgorithm.Lz4, 1000));
        Assert.Equal(-5, ErofsCompressionLevel.Validate(ErofsCompressionAlgorithm.Lz4, -5));
    }

    [Fact]
    public void Build_HighCompressionLevels_ReachTheImageInsteadOfBeingClamped()
    {
        using var temp = new UotanStudio.Tests.Comm.TestDoubles.TempDirectory();
        temp.WriteBytes("payload.bin", [.. Enumerable.Repeat((byte)0x5A, 64 * 1024)]);

        var options = new ErofsOptions
        {
            Algorithm = ErofsCompressionAlgorithm.Zstd,
            CompressionLevel = 22,
            WindowBits = 20,
            BuildTime = 1_700_000_000,
        };
        _ = new UotanStudio.Firmware.FileSystem.Erofs.Builder.ErofsBuilder().Build(temp.Path, options);

        Assert.Equal(22, options.CompressionLevel);
    }

    [Fact]
    public void Validate_LzmaHighCompressionLevel_SurvivesOptionValidation()
    {
        var options = new ErofsOptions
        {
            Algorithm = ErofsCompressionAlgorithm.Lzma,
            CompressionLevel = 109,
        };
        options.Validate();

        Assert.Equal(109, options.CompressionLevel);
    }
}