using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs;
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
        // A source with mixed compressed/inline inodes must round-trip its per-inode strategy
        // through the metadata document, keyed by image path.
        // 混合压缩/内联 inode 的源镜像，其逐 inode 策略必须以镜像路径为键经元数据文档往返。
        using var source = new TempDirectory();
        var payload = Enumerable.Repeat((byte)0x77, 20 * 1024).ToArray();
        source.WriteBytes("data.bin", payload);
        source.WriteBytes("small.txt", "tiny");
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
        Assert.All(captured.SourceInodes!.Values, inode => Assert.False(string.IsNullOrEmpty(inode.Path)));

        var document = new UotanStudio.Firmware.SharedKernel.ErofsRebuildParameters();
        SetInodes(document, captured);
        var restored = Restore(document);

        Assert.NotNull(restored.SourceInodesByPath);
        foreach (var (nid, original) in captured.SourceInodes!)
        {
            var record = Assert.NotNull(restored.SourceInodesByPath!.GetValueOrDefault(original.Path!));
            Assert.Equal(original.Compressed, record.Compressed);
            Assert.Equal(original.InlineData, record.InlineData);
            Assert.Equal(original.Algorithm, record.Algorithm);
            Assert.Equal(original.LogicalClusterSize, record.LogicalClusterSize);
            // The nid table stays populated for legacy edit-plan binding.
            // nid 表仍被填充以支持旧式 editPlan 绑定。
            Assert.Same(record, restored.SourceInodes![nid]);
        }
    }

    [Fact]
    public void Restore_LegacyDocumentWithoutPaths_FallsBackToNidTable()
    {
        // metadata.json documents written before path capture carry no Path fields; restore must
        // still build the nid table and leave the path table empty.
        // 路径采集之前写出的 metadata.json 不带 Path 字段；恢复时仍应构建 nid 表且路径表为空。
        var document = new UotanStudio.Firmware.SharedKernel.ErofsRebuildParameters();
        document.Inodes.Add(new UotanStudio.Firmware.SharedKernel.ErofsRebuildInode
        {
            Nid = 7,
            Compressed = true,
            Algorithm = "Lz4",
            LogicalClusterSize = 4096,
        });
        var restored = Restore(document);

        Assert.Null(restored.SourceInodesByPath);
        Assert.True(restored.SourceInodes!.ContainsKey(7));
        Assert.True(restored.SourceInodes![7].Compressed);
    }

    [Fact]
    public void Build_BindsPreservedStrategiesByPathWithoutEditPlan()
    {
        // Repacking the extracted tree of a legacy image (no edit plan) must inherit each file's
        // stored inline/compressed decision by path, reproducing the source storage shape.
        // 对 legacy 镜像的解包目录重打包（无 editPlan）时，必须按路径继承每个文件的内联/压缩
        // 决策，复现源存储形态。
        using var source = new TempDirectory();
        var payload = Enumerable.Repeat((byte)0x3C, 20 * 1024).ToArray();
        source.WriteBytes("data.bin", payload);
        source.WriteBytes("small.txt", "tiny");
        var original = new ErofsBuilder().Build(source.Path, new ErofsOptions
        {
            Algorithm = ErofsCompressionAlgorithm.Lz4,
            LegacyCompatibility = true,
        });

        var extracted = Path.Combine(source.Path, "extracted");
        using (var parser = new ErofsParser(original))
        {
            parser.ExtractToDirectory(extracted);
        }
        using (var parser = new ErofsParser(original))
        {
            var options = parser.ReadRebuildOptions();
            var repacked = new ErofsBuilder().Build(extracted, options);
            var superblock = UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.ErofsSuperblock.FromImage(repacked);
            Assert.Equal(UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.ErofsSuperblock.IncompatLz4_0Padding,
                superblock.FeatureIncompat);

            using var verify = new ErofsParser(repacked);
            Assert.Equal(payload, verify.ReadFileBytes("data.bin"));
            Assert.Equal("tiny", verify.ReadFileText("small.txt"));
        }
    }

    private static void SetInodes(UotanStudio.Firmware.SharedKernel.ErofsRebuildParameters document, ErofsOptions captured)
    {
        foreach (var inode in captured.SourceInodes!.OrderBy(pair => pair.Key))
        {
            document.Inodes.Add(new UotanStudio.Firmware.SharedKernel.ErofsRebuildInode
            {
                Nid = inode.Key,
                Path = inode.Value.Path,
                MTime = inode.Value.Mtime,
                MTimeNanoseconds = inode.Value.MtimeNanoseconds,
                Extended = inode.Value.Extended,
                Compressed = inode.Value.Compressed,
                Algorithm = inode.Value.Algorithm.ToString(),
                CompactIndexes = inode.Value.CompactIndexes,
                InlineData = inode.Value.InlineData,
                LogicalClusterSize = inode.Value.LogicalClusterSize,
            });
        }
    }

    private static ErofsOptions Restore(UotanStudio.Firmware.SharedKernel.ErofsRebuildParameters document)
    {
        var method = typeof(UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.ErofsRebuildMetadata)
            .GetMethod("Restore", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        return (ErofsOptions)method!.Invoke(null, [document])!;
    }
}
