using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Firmware.FileSystem.Metadata;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public class ErofsRebuildEncoderFidelityTests
{
    [Fact]
    public void Restore_LevelOneLz4Profile_KeepsFastEncoderInsteadOfForcingHighCompression()
    {
        var profile = new ErofsRebuildParameters
        {
            BlockSize = 4096,
            Compress = true,
            Algorithm = "Lz4",
            CompressionLevel = 1,
            Codecs =
            [
                new ErofsRebuildCodec
                {
                    Algorithm = "Lz4",
                    BlockSize = 4096,
                    MaxPclusterSize = 4096,
                    Lz4MaxDistance = 65535,
                    CompressionLevel = 1,
                },
            ],
        };

        var restored = ErofsRebuildMetadata.Restore(profile);

        Assert.Equal(1, restored.CompressionLevel);
        Assert.False(restored.Lz4HighCompression);
        var codec = restored.SourceCompressionOptions![ErofsCompressionAlgorithm.Lz4];
        Assert.Equal(1, codec.CompressionLevel);
        Assert.False(codec.Lz4HighCompression);
    }

    [Fact]
    public void Restore_ProfileWithoutEncoderDimension_InfersHighCompressionFromLevelAboveOne()
    {
        var profile = new ErofsRebuildParameters
        {
            BlockSize = 4096,
            Compress = true,
            Algorithm = "Lz4",
            CompressionLevel = 9,
            Codecs =
            [
                new ErofsRebuildCodec
                {
                    Algorithm = "Lz4",
                    BlockSize = 4096,
                    MaxPclusterSize = 4096,
                    CompressionLevel = 9,
                },
            ],
        };

        var restored = ErofsRebuildMetadata.Restore(profile);

        Assert.True(restored.Lz4HighCompression);
        var codec = restored.SourceCompressionOptions![ErofsCompressionAlgorithm.Lz4];
        Assert.True(codec.Lz4HighCompression);
        Assert.Equal(9, codec.CompressionLevel);
    }

    [Fact]
    public void CaptureRestore_RoundTrips_TheRecordedEncoderIdentity()
    {
        var options = new ErofsOptions
        {
            Compress = true,
            Algorithm = ErofsCompressionAlgorithm.Lz4,
            CompressionLevel = 9,
            Lz4HighCompression = true,
            SourceCompressionOptions = new Dictionary<ErofsCompressionAlgorithm, ErofsOptions>
            {
                [ErofsCompressionAlgorithm.Lz4] = new()
                {
                    Algorithm = ErofsCompressionAlgorithm.Lz4,
                    CompressionLevel = 9,
                    Lz4HighCompression = true,
                },
            },
        };

        var restored = ErofsRebuildMetadata.Restore(ErofsRebuildMetadata.Capture(options));

        Assert.True(restored.Lz4HighCompression);
        Assert.Equal(9, restored.CompressionLevel);
        var codec = restored.SourceCompressionOptions![ErofsCompressionAlgorithm.Lz4];
        Assert.True(codec.Lz4HighCompression);
        Assert.Equal(9, codec.CompressionLevel);
    }
}