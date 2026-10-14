using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Firmware.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.FileSystem.Erofs.Models;
using UotanStudio.Firmware.FileSystem.Erofs.Parser;
using UotanStudio.Firmware.FileSystem.Metadata;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class ErofsMetaboxCompressedContainerAcceptanceTests
{
    private const int BlockSize = 4096;

    [Fact]
    public void Rebuild_CompressedMetaboxContainerSource_ReadsEveryResidentBackByteIdentical()
    {
        using var source = new TempDirectory();
        var random = new Random(20261014);
        var names = new List<string>();
        var expectedByName = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        for (var i = 0; i < 400; i++)
        {
            var name = $"resident-{i:D4}.bin";
            var payload = new byte[700];
            random.NextBytes(payload);
            names.Add(name);
            expectedByName[name] = payload;
            source.WriteBytes(name, payload);
        }

        var verbatim = new ErofsBuilder().Build(source.Path, MetaboxOptions());
        var extracted = Path.Combine(source.Path, "extracted");
        ErofsRebuildParameters profile;
        using (var parser = new ErofsParser(verbatim))
        {
            parser.ExtractToDirectory(extracted);
            profile = ErofsRebuildMetadata.Capture(parser.ReadRebuildOptions());
        }

        profile.SourceMetaboxCompressed = true;
        profile.SourceMetaboxAlgorithm = ErofsCompressionAlgorithm.Lz4.ToString();
        var restored = ErofsRebuildMetadata.Restore(profile);
        restored.Metabox = true;
        restored.MetaboxRequired = true;

        var rebuilt = new ErofsBuilder().Build(extracted, restored);

        using var reader = new ErofsParser(rebuilt);
        var container = reader.MetaboxContainer;
        Assert.NotNull(container);
        Assert.True(container!.Value.Compressed, "the rebuilt container inode must carry a compressed data layout.");
        Assert.Equal(ErofsCompressionAlgorithm.Lz4, container.Value.Algorithm);

        foreach (var name in names)
        {
            Assert.Equal(expectedByName[name], reader.ReadFileBytes(name));
        }
    }

    [Fact]
    public void Build_VerbatimMetaboxContainer_KeepsTheContainerUncompressed()
    {
        using var source = new TempDirectory();
        var random = new Random(20261015);
        for (var i = 0; i < 300; i++)
        {
            var payload = new byte[600];
            random.NextBytes(payload);
            source.WriteBytes($"resident-{i:D4}.bin", payload);
        }

        var image = new ErofsBuilder().Build(source.Path, MetaboxOptions());
        using var parser = new ErofsParser(image);
        var container = parser.MetaboxContainer;
        Assert.NotNull(container);
        Assert.False(container!.Value.Compressed);
        Assert.Equal(600, parser.ReadFileBytes("resident-0000.bin").Length);
    }

    private static ErofsOptions MetaboxOptions() => new()
    {
        Algorithm = ErofsCompressionAlgorithm.Lz4,
        Metabox = true,
        MetaboxRequired = true,
        MaxPclusterSize = BlockSize,
        Features = ErofsFeatureSet.FromWords(0,
            ErofsSuperblock.IncompatLz4_0Padding | ErofsSuperblock.IncompatBigPcluster | ErofsSuperblock.IncompatMetabox),
    };
}
