using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Firmware.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.FileSystem.Erofs.Models;
using UotanStudio.Firmware.FileSystem.Erofs.Parser;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Pins the reachability of the interlaced plain-pcluster advise in this writer. The advise mirrors
/// <c>lib/compress.c</c> in erofs-utils, which raises it for a build that enables fragments, does not force
/// whole-file dedupe and keeps compressed data block-aligned; the writer honours that condition here, but
/// every payload that satisfies it is packed as a whole-file or tail fragment first, so no plain pcluster
/// survives to be rotated. These cases fail loudly if that ever changes, because the rotation then has to be
/// verified against the reader instead of assumed unreachable.
/// <para>钉住本写入方交错原样 pcluster advise 的可达性。该 advise 镜像 erofs-utils 的 <c>lib/compress.c</c>：
/// 启用 fragments、未强制整文件去重且压缩数据保持块对齐时置起；本写入方遵循该条件，但所有满足条件的负载
/// 都会先被打包为整文件或尾部 fragment，因此没有任何原样 pcluster 留存下来可供旋转。一旦该前提改变，这些
/// 用例会立刻失败，因为那时必须对照读取方验证旋转，而不能继续假定其不可达。</para>
/// </summary>
public sealed class ErofsInterlacedReachabilityTests
{
    private const int BlockSize = 4096;

    [Fact]
    public void Build_FragmentsEnabledWithCompression_LeavesNoRotatablePlainPcluster()
    {
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", MixedPayload(BlockSize * 9, 13));
        source.WriteBytes("small.bin", [1, 2, 3, 4, 5]);

        var options = new ErofsOptions
        {
            Algorithm = ErofsCompressionAlgorithm.Lz4,
            Fragments = true,
            OptimizeCompression = true,
            MaxPclusterSize = BlockSize,
        };
        Assert.True(options.InterlacedPlainPclusters, "the option predicate must hold for this build.");

        var image = new ErofsBuilder().Build(source.Path, options);
        using var parser = new ErofsParser(image);
        var records = parser.ReadRebuildOptions().SourceInodes!;

        var entries = records.Values
            .Where(record => record.SourcePath?.EndsWith(".bin", StringComparison.Ordinal) == true)
            .ToArray();
        Assert.NotEmpty(entries);

        foreach (var record in entries)
        {
            Assert.Equal(0, record.ZAdvise & ZErofsMapHeader.AdviseInterlacedPcluster);
            Assert.NotEqual(ErofsFragmentShape.None, record.SourceFragmentShape);
        }

        Assert.Equal([1, 2, 3, 4, 5], parser.ReadFileBytes("small.bin"));
        Assert.Equal(MixedPayload(BlockSize * 9, 13), parser.ReadFileBytes("data.bin"));
    }

    [Fact]
    public void Build_BigPclusterWithFragments_RejectsTheDeclarationInsteadOfRotating()
    {
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", MixedPayload(BlockSize * 9, 11));

        var options = new ErofsOptions
        {
            Algorithm = ErofsCompressionAlgorithm.Lz4,
            Fragments = true,
            MaxPclusterSize = BlockSize * 4,
            Features = ErofsFeatureSet.FromWords(0,
                ErofsSuperblock.IncompatLz4_0Padding | ErofsSuperblock.IncompatBigPcluster),
        };

        Assert.Throws<NotSupportedException>(() => new ErofsBuilder().Build(source.Path, options));
    }

    [Fact]
    public void Build_WithoutFragments_ReportsNoInterlacedAdviseInTheOptions()
    {
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", MixedPayload(BlockSize * 4, 17));

        var options = new ErofsOptions
        {
            Algorithm = ErofsCompressionAlgorithm.Lz4,
            Fragments = false,
            MaxPclusterSize = BlockSize,
        };
        Assert.False(options.InterlacedPlainPclusters);

        var image = new ErofsBuilder().Build(source.Path, options);
        using var parser = new ErofsParser(image);
        var records = parser.ReadRebuildOptions().SourceInodes!;
        var record = records.Values.Single(r =>
            r.SourcePath?.EndsWith("data.bin", StringComparison.Ordinal) == true);
        Assert.Equal(0, record.ZAdvise & ZErofsMapHeader.AdviseInterlacedPcluster);
        Assert.Equal(MixedPayload(BlockSize * 4, 17), parser.ReadFileBytes("data.bin"));
    }

    private static byte[] MixedPayload(int length, int modulus)
    {
        var payload = new byte[length];
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i % modulus == 0 ? 0x5A : (i * 7 + (i >> 3)) % 256);
        }

        return payload;
    }
}