using System.IO.Compression;
using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Apk;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>Verifies that APK unpack extracts every ZIP entry with bounded output and zip-slip protection.
/// <para>验证 APK 解包完整解出全部 ZIP 条目，并实施有界输出与 zip-slip 防护。</para></summary>
public class ApkUnpackTests
{
    private static string CreateApk(Action<ZipArchive> populate)
    {
        var path = Path.Combine(Path.GetTempPath(), "apk-unpack-" + Guid.NewGuid().ToString("N") + ".apk");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        populate(archive);
        return path;
    }

    [Fact]
    public async Task Unpack_ExtractsAllEntries_AndMetadata()
    {
        var path = CreateApk(archive =>
        {
            using (var entry = archive.CreateEntry("classes.dex").Open()) entry.Write([1, 2, 3, 4]);
            using (var entry = archive.CreateEntry("res/values.xml").Open())
            using (var writer = new StreamWriter(entry)) writer.Write("<resources/>");
            using (var entry = archive.CreateEntry("empty.txt").Open()) { }
        });
        var output = Path.Combine(Path.GetTempPath(), "apk-unpack-out-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = await new ApkFormatHandler().UnpackAsync(path, output, cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(result.Success, result.Summary);
            Assert.Equal([1, 2, 3, 4], await File.ReadAllBytesAsync(Path.Combine(output, "classes.dex"), TestContext.Current.CancellationToken));
            Assert.Equal("<resources/>", File.ReadAllText(Path.Combine(output, "res", "values.xml")));
            Assert.Empty(await File.ReadAllTextAsync(Path.Combine(output, "empty.txt"), TestContext.Current.CancellationToken));
            Assert.True(File.Exists(Path.Combine(output, "apk-metadata.txt")));
        }
        finally
        {
            File.Delete(path);
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public async Task Unpack_RejectsEntryEscapingOutputDirectory()
    {
        var path = CreateApk(archive =>
        {
            using (var entry = archive.CreateEntry("classes.dex").Open()) entry.Write([1]);
            using (var entry = archive.CreateEntry("../escape.txt").Open()) entry.Write([2]);
        });
        var output = Path.Combine(Path.GetTempPath(), "apk-unpack-out-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = await new ApkFormatHandler().UnpackAsync(path, output, cancellationToken: TestContext.Current.CancellationToken);
            Assert.False(result.Success);
            Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(output)!, "escape.txt")));
        }
        finally
        {
            File.Delete(path);
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public async Task Unpack_TruncatesWhenEntryShorterThanDeclared()
    {
        var path = Path.Combine(Path.GetTempPath(), "apk-unpack-" + Guid.NewGuid().ToString("N") + ".apk");
        // 手工构造声明长度大于实际压缩数据的 ZIP，模拟提前 EOF。
        // Hand-build a ZIP whose declared size exceeds the actual stored data (early EOF).
        using (var stream = File.Create(path))
        using (var writer = new BinaryWriter(stream))
        {
            var data = new byte[] { 1, 2, 3 };
            writer.Write(0x04034b50); // local file header
            writer.Write((ushort)20); // version
            writer.Write((ushort)0); // flags
            writer.Write((ushort)0); // method: stored
            writer.Write((ushort)0); // time
            writer.Write((ushort)0); // date
            writer.Write(0u); // crc32 (unchecked here)
            writer.Write(64L); // declared uncompressed size (a lie)
            writer.Write(64L); // declared compressed size (a lie)
            writer.Write((ushort)12); // name length
            writer.Write((ushort)0); // extra length
            writer.Write("classes.dex"u8);
            writer.Write(data);
            writer.Flush();
            stream.SetLength(stream.Position); // drop the central directory entirely
        }
        var output = Path.Combine(Path.GetTempPath(), "apk-unpack-out-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = await new ApkFormatHandler().UnpackAsync(path, output, cancellationToken: TestContext.Current.CancellationToken);
            Assert.False(result.Success);
        }
        finally
        {
            File.Delete(path);
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        }
    }
}
