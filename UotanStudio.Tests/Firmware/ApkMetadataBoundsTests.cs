using System.IO.Compression;
using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Apk.Parsing;
using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>Checks metadata limits before allocating advertised output sizes. <para>检查在按声明大小分配之前实施元数据上限。</para></summary>
public class ApkMetadataBoundsTests
{
    [Fact]
    public void CompressedOversizeManifest_IsRejected()
    {
        var path = Path.Combine(Path.GetTempPath(), "apk-limit-" + Guid.NewGuid().ToString("N") + ".apk");
        try
        {
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            using (var output = archive.CreateEntry("AndroidManifest.xml", CompressionLevel.SmallestSize).Open())
            {
                var zeros = new byte[64 * 1024];
                for (var i = 0; i < 257; i++) output.Write(zeros);
            }
            var error = Assert.Throws<InvalidDataException>(() => ApkParser.Analyze(path));
            Assert.Contains("metadata limit", error.Message);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void OversizeDex_IsRejectedBeforeReadingWholeFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "dex-limit-" + Guid.NewGuid().ToString("N") + ".dex");
        try
        {
            using (var stream = File.Create(path)) stream.SetLength(256L * 1024 * 1024 + 1);
            Assert.Throws<InvalidDataException>(() => DexFile.Load(path));
        }
        finally { File.Delete(path); }
    }
}