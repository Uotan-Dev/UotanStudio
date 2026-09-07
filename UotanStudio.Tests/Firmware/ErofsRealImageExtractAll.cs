using System.Buffers.Binary;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs;
using UotanStudio.Tests.Infrastructure;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// 真实固件语料全量解包测试：经 <see cref="FirmwareCorpus"/> 定位语料根目录下的 odm.img
/// （真实 EROFS 镜像），解包全部条目并逐一比对存在性与尺寸。语料不可用或镜像并非 EROFS
/// 时以 <see cref="Assert.Skip"/> 跳过；临时输出目录在 finally 中删除。
/// <para>Full-extraction test against the real firmware corpus: resolves odm.img (a real EROFS
/// image) via <see cref="FirmwareCorpus"/>, extracts every entry and compares each file's
/// existence and size. Skips via <see cref="Assert.Skip"/> when the corpus is unavailable or the
/// located image is not EROFS; the temporary output directory is removed in finally.</para>
/// </summary>
public class ErofsRealImageExtractAll
{
    /// <summary>EROFS 魔数 0xE0F5E1E2（小端），位于镜像偏移 1024 处。</summary>
    private const uint ErofsMagic = 0xE0F5E1E2;

    [Fact]
    public void ExtractAll_FilesMatchSizes()
    {
        var img = FirmwareCorpus.FindFile("odm.img");
        if (img is null)
        {
            Assert.Skip("odm.img not found in firmware corpus");
        }

        // 防御：其它语料根目录下的 odm.img 未必是 EROFS，先校验魔数再继续。
        using (var probe = File.OpenRead(img))
        {
            if (probe.Length < 1028) Assert.Skip("odm.img is not an erofs image");
            probe.Position = 1024;
            Span<byte> magic = stackalloc byte[4];
            probe.ReadExactly(magic);
            if (BinaryPrimitives.ReadUInt32LittleEndian(magic) != ErofsMagic)
            {
                Assert.Skip("odm.img is not an erofs image");
            }
        }

        var outDir = Path.Combine(Path.GetTempPath(), "erofs-corpus-" + Path.GetRandomFileName());
        try
        {
            Directory.CreateDirectory(outDir);

            using var parser = new ErofsParser(img);
            var files = parser.EnumerateFiles();
            parser.ExtractToDirectory(outDir);

            var failures = new List<string>();
            foreach (var f in files)
            {
                if (f.IsSymlink) continue;
                var p = Path.Combine(outDir, f.Path.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(p)) { failures.Add($"missing {f.Path}"); continue; }
                var len = new FileInfo(p).Length;
                if (len != f.Size) failures.Add($"size {f.Path}: {len} != {f.Size}");
            }
            Assert.Empty(failures);
            Assert.Equal(files.Count(f => !f.IsSymlink), Directory.GetFiles(outDir, "*", SearchOption.AllDirectories).Length);
        }
        finally
        {
            // 只清理本次创建的临时输出目录，不触碰语料或任何既有目录。
            if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
        }
    }
}