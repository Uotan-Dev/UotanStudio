using System.Formats.Tar;
using System.IO.Compression;
using UotanStudio.Compress;
using UotanStudio.Firmware.Archive;
using UotanStudio.Firmware.Archive.Browsing;
using UotanStudio.Firmware.Archive.Models;
using UotanStudio.Tests.Comm.TestDoubles;
using UotanStudio.Views.Controls;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class ArchiveBrowserTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(".zip")]
    [InlineData(".rar")]
    [InlineData(".7z")]
    [InlineData(".arj")]
    public void SharpCompressArchiveExtensions_RouteToFileManager(string extension)
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "archive" + extension);
        File.WriteAllBytes(path, []);
        Assert.Equal(EditorOpenMode.FileSystem, FileOpenMode.ResolveMode(path));
    }

    [Fact]
    public void SupportsPath_ApkExtension_IsRecognizedAsBrowsableArchive()
    {
        Assert.True(ArchiveBrowser.SupportsPath("sample.apk"));
    }

    [Theory]
    [InlineData(".tar", CompressionFormat.None)]
    [InlineData(".tgz", CompressionFormat.Gzip)]
    [InlineData(".tar.gz", CompressionFormat.Gzip)]
    [InlineData(".txz", CompressionFormat.Xz)]
    [InlineData(".tar.xz", CompressionFormat.Xz)]
    [InlineData(".tbz2", CompressionFormat.Bzip2)]
    [InlineData(".tar.zst", CompressionFormat.Zstd)]
    [InlineData(".tar.lz4", CompressionFormat.Lz4)]
    [InlineData(".tar.br", CompressionFormat.Brotli)]
    public void TarFromFramework_ListsAndExtractsSelectedSubtree(string extension, CompressionFormat compression)
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "archive" + extension);
        using var raw = CreateTar();
        using (var output = File.Create(path)) CompressionService.Compress(raw, output, compression);
        Assert.Equal(EditorOpenMode.FileSystem, FileOpenMode.ResolveMode(path));
        using var browser = ArchiveBrowser.Open(path, Cancellation);
        Assert.Contains(browser.Members, entry => entry.Path == "nested/hello.txt" && entry.Size == 5);
        Assert.Contains(browser.Members, entry => entry.Path == "other.txt");
        var destination = Path.Combine(temp.Path, "out");
        browser.Extract("nested", destination, Cancellation);
        Assert.Equal("hello", File.ReadAllText(Path.Combine(destination, "nested", "hello.txt")));
        Assert.False(File.Exists(Path.Combine(destination, "other.txt")));
        browser.Extract("other.txt", destination, Cancellation);
        Assert.Equal("other", File.ReadAllText(Path.Combine(destination, "other.txt")));
    }

    [Theory]
    [InlineData(ArchiveFormat.NewAscii)]
    [InlineData(ArchiveFormat.CrcAscii)]
    [InlineData(ArchiveFormat.OldAscii)]
    [InlineData(ArchiveFormat.Binary)]
    public void Cpio_ReportsPayloadLengthAndExtracts(ArchiveFormat format)
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "ramdisk.cpio");
        using (var output = File.Create(path)) new CpioArchive().SaveAll(output,
            [new ArchiveEntry { Name = "./init", Data = "payload"u8.ToArray(), Metadata = new() { FileType = CpioFileType.Regular, Mode = 0x81a4 } }], format);
        using var browser = ArchiveBrowser.Open(path, Cancellation);
        Assert.Equal(new ArchiveBrowser.Member("init", 7, CpioFileType.Regular), Assert.Single(browser.Members));
        browser.Extract("init", Path.Combine(temp.Path, "out"), Cancellation);
        Assert.Equal("payload", File.ReadAllText(Path.Combine(temp.Path, "out", "init")));
    }

    [Theory]
    [InlineData(".xz", CompressionFormat.Xz)]
    [InlineData(".gz", CompressionFormat.Gzip)]
    [InlineData(".bz2", CompressionFormat.Bzip2)]
    [InlineData(".zst", CompressionFormat.Zstd)]
    [InlineData(".br", CompressionFormat.Brotli)]
    [InlineData(".lzma", CompressionFormat.Lzma)]
    [InlineData(".lz4", CompressionFormat.Lz4)]
    [InlineData(".lz4_legacy", CompressionFormat.Lz4Legacy)]
    [InlineData(".lzo", CompressionFormat.Lzop)]
    [InlineData(".zlib", CompressionFormat.Zlib)]
    [InlineData(".deflate", CompressionFormat.Deflate)]
    public void CompressedSingleFile_ListsPayloadAndReleasesScratch(string extension, CompressionFormat compression)
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "hello.txt" + extension);
        using (var input = new MemoryStream("hello"u8.ToArray()))
        using (var output = File.Create(path)) CompressionService.Compress(input, output, compression);
        using (var browser = ArchiveBrowser.Open(path, Cancellation))
        {
            Assert.Equal("hello.txt", Assert.Single(browser.Members).Path);
            browser.Extract("hello.txt", Path.Combine(temp.Path, "out"), Cancellation);
            Assert.Equal("hello", File.ReadAllText(Path.Combine(temp.Path, "out", "hello.txt")));
        }
        File.Delete(path);
    }

    [Fact]
    public void ZipArchive_ListsDirectoriesAndStreamsSelectedEntries()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "super.zip");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            archive.CreateEntry("META-INF/");
            var script = archive.CreateEntry("META-INF/com/google/android/updater-script");
            using (var writer = new StreamWriter(script.Open())) writer.Write("ui_print('super');");
            var image = archive.CreateEntry("super.img");
            using (var stream = image.Open()) stream.Write([1, 2, 3, 4]);
        }

        Assert.Equal(EditorOpenMode.FileSystem, FileOpenMode.ResolveMode(path));
        using var browser = ArchiveBrowser.Open(path, Cancellation);
        Assert.Contains(browser.Members, entry => entry.Path == "META-INF" && entry.Kind == CpioFileType.Directory);
        Assert.Contains(browser.Members, entry => entry.Path == "META-INF/com/google/android/updater-script" && entry.Size > 0);
        Assert.Contains(browser.Members, entry => entry.Path == "super.img" && entry.Size == 4);

        var destination = Path.Combine(temp.Path, "out");
        browser.Extract("META-INF", destination, Cancellation);
        Assert.Equal("ui_print('super');", File.ReadAllText(Path.Combine(destination, "META-INF", "com", "google", "android", "updater-script")));
        browser.Extract("super.img", destination, Cancellation);
        Assert.Equal([1, 2, 3, 4], File.ReadAllBytes(Path.Combine(destination, "super.img")));
    }

    [Fact]
    public void ZipArchive_RejectsTraversalMembers()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "unsafe.zip");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        using (var stream = archive.CreateEntry("../escape").Open()) stream.WriteByte(1);

        Assert.Throws<ArchiveCorruptedException>(() => ArchiveBrowser.Open(path, Cancellation));
    }

    [Fact]
    public void ExpansionLimitAndCancellation_AreEnforced()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "large.gz");
        using (var input = new MemoryStream(new byte[4096]))
        using (var output = File.Create(path)) CompressionService.Compress(input, output, CompressionFormat.Gzip);
        var limit = Assert.Throws<CompressionException>(() => ArchiveBrowser.Open(path, Cancellation, 100));
        Assert.IsType<IOException>(limit.InnerException);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => ArchiveBrowser.Open(path, cancelled.Token));
        using var source = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public void TraversalAndMalformedInput_AreRejected()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "unsafe.tar");
        using (var output = File.Create(path))
        using (var writer = new TarWriter(output))
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "../escape") { DataStream = new MemoryStream([1]) });
        Assert.Throws<ArchiveCorruptedException>(() => ArchiveBrowser.Open(path, Cancellation));
        File.WriteAllText(path, "invalid");
        Assert.ThrowsAny<Exception>(() => ArchiveBrowser.Open(path, Cancellation));
    }

    [Fact]
    public void TarExtension_UnrecognisedHeader_FallsBackToTarContainerListing()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "legacy.tar");
        File.WriteAllBytes(path, new byte[512]);
        using var browser = ArchiveBrowser.Open(path, Cancellation);
        Assert.Equal("TAR", browser.FormatName);
        Assert.Empty(browser.Members);
    }

    private static MemoryStream CreateTar()
    {
        var stream = new MemoryStream();
        using (var writer = new TarWriter(stream, leaveOpen: true))
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "nested/hello.txt") { DataStream = new MemoryStream("hello"u8.ToArray()) });
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "other.txt") { DataStream = new MemoryStream("other"u8.ToArray()) });
        }
        stream.Position = 0;
        return stream;
    }
}