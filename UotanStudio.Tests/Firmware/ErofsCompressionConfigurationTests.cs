using System.Buffers.Binary;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>Checks Linux EROFS compression configuration records. <para>检查 Linux EROFS 压缩配置记录。</para></summary>
public class ErofsCompressionConfigurationTests
{
    [Theory]
    [InlineData(ErofsCompressionAlgorithm.Lzma, false, 0)]
    [InlineData(ErofsCompressionAlgorithm.Deflate, false, 0)]
    [InlineData(ErofsCompressionAlgorithm.Zstd, false, 0)]
    [InlineData(ErofsCompressionAlgorithm.Lzma, true, 0)]
    [InlineData(ErofsCompressionAlgorithm.Deflate, true, 0)]
    [InlineData(ErofsCompressionAlgorithm.Zstd, true, 0)]
    [InlineData(ErofsCompressionAlgorithm.Lzma, false, 1)]
    [InlineData(ErofsCompressionAlgorithm.Deflate, false, 1)]
    [InlineData(ErofsCompressionAlgorithm.Zstd, false, 1)]
    [InlineData(ErofsCompressionAlgorithm.Lzma, true, 1)]
    [InlineData(ErofsCompressionAlgorithm.Deflate, true, 1)]
    [InlineData(ErofsCompressionAlgorithm.Zstd, true, 1)]
    public void Build_WritesKernelConfigurationBeforeInodes(ErofsCompressionAlgorithm algorithm, bool metabox, int extraDevices)
    {
        using var source = new TempDirectory();
        var payload = Enumerable.Repeat((byte)0x71, 16 * 1024).ToArray();
        source.WriteBytes("data.bin", payload);
        if (extraDevices > 0)
        {
            var random = new byte[8192];
            new Random(20260907).NextBytes(random);
            source.WriteBytes("random.bin", random);
        }
        var image = new ErofsBuilder().Build(source.Path, new ErofsOptions
        {
            Algorithm = algorithm,
            Metabox = metabox,
            ExtraDeviceCount = extraDevices
        });
        var superblock = ErofsSuperblock.FromImage(image);
        Assert.NotEqual(0u, superblock.FeatureIncompat & ErofsSuperblock.IncompatBigPcluster);
        Assert.Equal(1 << (int)algorithm, superblock.AvailableComprAlgs);

        // fs/erofs/erofs_fs.h: a le16 length followed by the algorithm-specific packed structure.
        var configurationOffset = 1024 + 128 + superblock.SbExtSlots * 16;
        var length = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(configurationOffset));
        Assert.Equal(algorithm == ErofsCompressionAlgorithm.Lzma ? 14 : 6, length);
        var configuration = image.AsSpan(configurationOffset + 2, length);
        if (algorithm == ErofsCompressionAlgorithm.Lzma)
        {
            Assert.InRange(BinaryPrimitives.ReadUInt32LittleEndian(configuration), 4096u, 8u << 20);
            Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(configuration[4..]));
            Assert.All(configuration[6..].ToArray(), value => Assert.Equal(0, value));
        }
        else
        {
            Assert.Equal(algorithm == ErofsCompressionAlgorithm.Deflate ? 15 : 0, configuration[0]);
            if (algorithm == ErofsCompressionAlgorithm.Zstd) Assert.InRange(configuration[1], (byte)0, (byte)10);
            Assert.All(configuration[2..].ToArray(), value => Assert.Equal(0, value));
        }
        Assert.True(superblock.RootNid * 32 >= configurationOffset + 2 + length);
        using var parser = new ErofsParser(image);
        Assert.Equal(payload, parser.ReadFileBytes("data.bin"));
    }
}