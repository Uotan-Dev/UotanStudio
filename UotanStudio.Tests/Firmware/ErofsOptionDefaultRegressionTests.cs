using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Firmware.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.FileSystem.Erofs.Models;
using UotanStudio.Firmware.FileSystem.Rebuilding;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Guards that the option surface the later EROFS features consume is inert at its defaults: an explicit
/// default value must produce the same image bytes as an absent one, and a packing option map that carries
/// none of the new keys must leave every new option at its default. Without this the added members could
/// change existing output while every other test still passed.
/// <para>守护后续 EROFS 特性所消费的选项面在默认值下是惰性的：显式写出默认值必须与不写产生逐字节相同的镜像，
/// 且不含新键的打包选项映射必须让每个新选项保持默认值。缺少该约束时，新增成员可能改变既有产物而其他测试
/// 仍全部通过。</para>
/// </summary>
public sealed class ErofsOptionDefaultRegressionTests
{
    private const int BlockSize = 4096;

    [Fact]
    public void Build_ExplicitDefaultOptionValues_MatchImplicitDefaults()
    {
        var payload = CompressiblePayload(96 * 1024);
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", payload);

        var implicitDefaults = new ErofsBuilder().Build(source.Path, new ErofsOptions
        {
            BlockSize = BlockSize,
            Compress = true,
            Algorithm = ErofsCompressionAlgorithm.Lz4,
            Uuid = new byte[16],
            BuildTime = 1_700_000_000UL,
        });
        var explicitDefaults = new ErofsBuilder().Build(source.Path, new ErofsOptions
        {
            BlockSize = BlockSize,
            Compress = true,
            Algorithm = ErofsCompressionAlgorithm.Lz4,
            Uuid = new byte[16],
            BuildTime = 1_700_000_000UL,
            UseExtentIndexes = ErofsTriStateFeature.Auto,
            ChunkSize = 0,
            BlobDeviceIds = null,
            InodeFormat = ErofsInodeFormat.Auto,
            ChunkIndexKind = ErofsChunkIndexKind.Auto,
            CompressHints = null,
            DedupeMode = ErofsDedupeMode.WholeFile,
        });

        Assert.Equal(implicitDefaults, explicitDefaults);
    }

    [Fact]
    public void ApplyErofsFeatureKeys_WithoutNewKeys_LeavesThemAtDefaults()
    {
        var options = new ErofsOptions { BlockSize = BlockSize };

        FileSystemPackingOptions.ApplyErofsFeatureKeys(new Dictionary<string, string>(), options, null);

        Assert.Equal(ErofsTriStateFeature.Auto, options.UseExtentIndexes);
        Assert.Equal(0, options.ChunkSize);
        Assert.Null(options.BlobDeviceIds);
        Assert.Equal(ErofsInodeFormat.Auto, options.InodeFormat);
        Assert.Equal(ErofsChunkIndexKind.Auto, options.ChunkIndexKind);
        Assert.Null(options.CompressHints);
        Assert.Equal(ErofsDedupeMode.WholeFile, options.DedupeMode);
    }

    private static byte[] CompressiblePayload(int length)
    {
        var payload = new byte[length];
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)((i * 17 + i / 5) & 0x3F);
        }
        return payload;
    }
}