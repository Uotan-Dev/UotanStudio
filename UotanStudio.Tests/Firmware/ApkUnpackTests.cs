using System.IO.Compression;
using UotanStudio.Firmware.AndroidMeta.Apk;
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
    public async Task Unpack_WithUnparsableResourceTable_ExtractsEntriesWithoutReadingIt()
    {
        var path = CreateApk(archive =>
        {
            using (var entry = archive.CreateEntry("classes.dex").Open()) entry.Write([1, 2, 3, 4]);
            using (var entry = archive.CreateEntry("resources.arsc").Open()) entry.Write(new byte[64]);
        });
        var output = Path.Combine(Path.GetTempPath(), "apk-unpack-out-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = await new ApkFormatHandler().UnpackAsync(path, output, cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(result.Success, result.Summary);
            Assert.Equal([1, 2, 3, 4], await File.ReadAllBytesAsync(Path.Combine(output, "classes.dex"), TestContext.Current.CancellationToken));
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
        using (var stream = File.Create(path))
        using (var writer = new BinaryWriter(stream))
        {
            var data = new byte[] { 1, 2, 3 };
            writer.Write(0x04034b50);
            writer.Write((ushort)20);
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write(0u);
            writer.Write(64L);
            writer.Write(64L);
            writer.Write((ushort)12);
            writer.Write((ushort)0);
            writer.Write("classes.dex"u8);
            writer.Write(data);
            writer.Flush();
            stream.SetLength(stream.Position);
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