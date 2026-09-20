using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Compression;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Temporary (probe-only) layout probe for comparing ErofsBuilder's compressed-block alignment
/// against erofs-utils mkfs.erofs output. It just materializes images to D:\Download iris not.
/// It is not a permanent product test and is expected to be removed after investigation.
/// <para>临时（仅探针）布局探针，用于对比 ErofsBuilder 与 erofs-utils mkfs.erofs 在压缩数据块
/// 对齐/布局上的差异。仅把镜像落盘到 D:\Download。它不是永久产品测试，调查结束后应删除。</para>
/// </summary>
public class ErofsLayoutProbeTests
{
    private const string OutputDir = @"D:\Download";

    private static string CreateSourceTree()
    {
        var srcRoot = Path.Combine(Path.GetTempPath(), "erofs-probe-src-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(srcRoot);
        using var fs = File.Create(Path.Combine(srcRoot, "payload.bin"));
        Span<byte> block = new byte[4096];
        block.Fill((byte)'A');
        var remaining = 12000;
        while (remaining > 0)
        {
            var take = Math.Min(remaining, block.Length);
            fs.Write(block.Slice(0, take));
            remaining -= take;
        }

        return srcRoot;
    }

    [Fact]
    public void Probe_BuildLegacyLz4_ToDownload()
    {
        var src = CreateSourceTree();
        try
        {
            Directory.CreateDirectory(OutputDir);
            var outPath = Path.Combine(OutputDir, "ours_lt2.img");
            new ErofsBuilder().Build(src, outPath, new ErofsOptions
            {
                BlockSize = 4096,
                Compress = true,
                Algorithm = ErofsCompressionAlgorithm.Lz4,
                LegacyCompatibility = true,
            });
            Assert.True(File.Exists(outPath), $"Expected output image '{outPath}' to exist.");
        }
        finally
        {
            try { Directory.Delete(src, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Probe_NonLegacyLz4_ToDownload()
    {
        var src = CreateSourceTree();
        try
        {
            Directory.CreateDirectory(OutputDir);
            var outPath = Path.Combine(OutputDir, "ours_nonlegacy.img");
            new ErofsBuilder().Build(src, outPath, new ErofsOptions
            {
                BlockSize = 4096,
                Compress = true,
                Algorithm = ErofsCompressionAlgorithm.Lz4,
            });
            Assert.True(File.Exists(outPath), $"Expected output image '{outPath}' to exist.");
        }
        finally
        {
            try { Directory.Delete(src, recursive: true); } catch { }
        }
    }
}