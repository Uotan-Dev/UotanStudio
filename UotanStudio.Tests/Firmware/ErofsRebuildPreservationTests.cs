using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Firmware.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.FileSystem.Erofs.Models;
using UotanStudio.Firmware.FileSystem.Erofs.Models.Inodes;
using UotanStudio.Firmware.FileSystem.Erofs.Parser;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Verifies that the preserved-metadata rebuild chain inherits per-inode compression and inline
/// decisions by image path, instead of re-inferring them from the directory tree.
/// <para>验证 preserved-metadata 重建链路按镜像路径继承逐 inode 压缩与内联决策，
/// 而不是从目录树重新推断。</para>
/// </summary>
public class ErofsRebuildPreservationTests
{
    [Fact]
    public void Capture_Restore_PreservesPerInodePathsAndStrategies()
    {
        using var source = new TempDirectory();
        var payload = Enumerable.Repeat((byte)0x77, 20 * 1024).ToArray();
        source.WriteBytes("data.bin", payload);
        source.WriteBytes("small.txt", "tiny"u8.ToArray());
        var image = new ErofsBuilder().Build(source.Path, new ErofsOptions
        {
            Algorithm = ErofsCompressionAlgorithm.Lz4,
            LegacyCompatibility = true,
        });

        ErofsOptions captured;
        using (var parser = new ErofsParser(image))
        {
            captured = parser.ReadRebuildOptions();
        }
        Assert.True(captured.SourceInodes is { Count: > 0 });
        Assert.All(captured.SourceInodes!.Values, inode => Assert.False(string.IsNullOrEmpty(inode.SourcePath)));

        var document = new UotanStudio.Firmware.FileSystem.Metadata.ErofsRebuildParameters();
        SetInodes(document, captured);
        var restored = Restore(document);

        Assert.NotNull(restored.SourceInodesByPath);
        foreach (var (nid, original) in captured.SourceInodes!)
        {
            if (!restored.SourceInodesByPath!.TryGetValue(original.SourcePath!, out var record))
            {
                Assert.Fail($"The restored path table is missing inode {original.SourcePath}.");
            }

            Assert.Equal(original.SourceCompressed, record.SourceCompressed);
            Assert.Equal(original.DataLayout == ErofsDataLayout.Inline, record.DataLayout == ErofsDataLayout.Inline);
            Assert.Equal(original.SourceAlgorithm, record.SourceAlgorithm);
            Assert.Equal(original.LogicalClusterSize, record.LogicalClusterSize);
            Assert.Same(record, restored.SourceInodes![nid]);
        }
    }

    [Fact]
    public void Restore_LegacyDocumentWithoutPaths_FallsBackToNidTable()
    {
        var document = new UotanStudio.Firmware.FileSystem.Metadata.ErofsRebuildParameters();
        document.Inodes.Add(new UotanStudio.Firmware.FileSystem.Metadata.ErofsRebuildInode
        {
            Nid = 7,
            Compressed = true,
            Algorithm = "Lz4",
            LogicalClusterSize = 4096,
        });
        var restored = Restore(document);

        Assert.Null(restored.SourceInodesByPath);
        Assert.True(restored.SourceInodes!.ContainsKey(7));
        Assert.True(restored.SourceInodes![7].SourceCompressed);
    }

    [Fact]
    public void Build_BindsPreservedStrategiesByPathWithoutEditPlan()
    {
        using var source = new TempDirectory();
        var payload = Enumerable.Repeat((byte)0x3C, 20 * 1024).ToArray();
        source.WriteBytes("data.bin", payload);
        source.WriteBytes("small.txt", "tiny"u8.ToArray());
        var original = new ErofsBuilder().Build(source.Path, new ErofsOptions
        {
            Algorithm = ErofsCompressionAlgorithm.Lz4,
            LegacyCompatibility = true,
        });

        var extracted = Path.Combine(source.Path, "extracted");
        var originalSuperblock = ErofsSuperblock.FromImage(original);
        using (var parser = new ErofsParser(original))
        {
            parser.ExtractToDirectory(extracted);
        }
        using (var parser = new ErofsParser(original))
        {
            var options = parser.ReadRebuildOptions();
            var repacked = new ErofsBuilder().Build(extracted, options);
            var repackedSuperblock = ErofsSuperblock.FromImage(repacked);
            Assert.Equal(originalSuperblock.FeatureIncompat, repackedSuperblock.FeatureIncompat);
            Assert.Equal(originalSuperblock.FeatureCompat, repackedSuperblock.FeatureCompat);
            Assert.False(options.Features.IsAuto);
            Assert.True(options.FeaturesAuthoritative);
            Assert.Equal(originalSuperblock.FeatureIncompat, options.Features.IncompatBits);
            Assert.Equal(0u, options.AddedIncompatBits);
            Assert.Equal(0u, options.DroppedCompatBits);

            using var verify = new ErofsParser(repacked);
            Assert.Equal(payload, verify.ReadFileBytes("data.bin"));
            Assert.Equal("tiny", System.Text.Encoding.UTF8.GetString(verify.ReadFileBytes("small.txt")));
        }
    }

    private static void SetInodes(UotanStudio.Firmware.FileSystem.Metadata.ErofsRebuildParameters document, ErofsOptions captured)
    {
        foreach (var inode in captured.SourceInodes!.OrderBy(pair => pair.Key))
        {
            document.Inodes.Add(new UotanStudio.Firmware.FileSystem.Metadata.ErofsRebuildInode
            {
                Nid = inode.Key,
                Path = inode.Value.SourcePath,
                MTime = inode.Value.CTime,
                MTimeNanoseconds = inode.Value.CTimeNsec,
                Extended = inode.Value.IsExtended,
                Compressed = inode.Value.SourceCompressed,
                Algorithm = inode.Value.SourceAlgorithm.ToString(),
                CompactIndexes = inode.Value.DataLayout == ErofsDataLayout.CompactIndexes,
                InlineData = inode.Value.DataLayout == ErofsDataLayout.Inline,
                LogicalClusterSize = inode.Value.LogicalClusterSize,
            });
        }
    }

    private static ErofsOptions Restore(UotanStudio.Firmware.FileSystem.Metadata.ErofsRebuildParameters document)
    {
        var method = typeof(UotanStudio.Firmware.FileSystem.Erofs.ErofsRebuildMetadata)
            .GetMethod("Restore", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        return (ErofsOptions)method!.Invoke(null, [document])!;
    }
}