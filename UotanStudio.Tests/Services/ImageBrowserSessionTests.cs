using System.Buffers.Binary;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4;
using UotanStudio.Firmware.BoundedContexts.FileSystem.F2fs;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Sparse;
using UotanStudio.Services.FileManagement;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>Validates browsing, selected extraction and resource ownership without constructing UI objects.</summary>
public sealed class ImageBrowserSessionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("EXT4", false)]
    [InlineData("EXT4", true)]
    [InlineData("EROFS", false)]
    [InlineData("EROFS", true)]
    [InlineData("F2FS", false)]
    [InlineData("F2FS", true)]
    public void FilesystemSession_ExtractsOnlySelectedSubtreeAndReleasesSource(string format, bool sparse)
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "source");
        Directory.CreateDirectory(Path.Combine(source, "nested"));
        byte[] payload = [0, 1, 2, 0xFF, 0x80, 0x7F];
        File.WriteAllBytes(Path.Combine(source, "nested", "blob.bin"), payload);
        File.WriteAllText(Path.Combine(source, "other.txt"), "not selected");
        var raw = Path.Combine(temp.Path, "raw.img");
        switch (format)
        {
            case "EXT4": Ext4Builder.Build(source, raw); break;
            case "EROFS": new ErofsBuilder().Build(source, raw); break;
            case "F2FS": F2fsBuilder.Build(source, raw); break;
        }
        var path = raw;
        if (sparse)
        {
            path = Path.Combine(temp.Path, "sparse.img");
            SparseImageConverter.ConvertRawToSparse(raw, path);
        }
        var destination = Path.Combine(temp.Path, "out");

        var session = ImageBrowserSession.Open(path, Token);
        try
        {
            Assert.Equal(format, session.FormatName);
            Assert.Equal(new FileInfo(raw).Length, session.ImageSize);
            Assert.Contains(session.Entries, entry => entry.Path == "nested/blob.bin"
                && entry.Size == payload.Length && entry.Kind == ImageBrowserEntryKind.File);
            session.Extract(["nested"], destination);
            Assert.Equal(payload, File.ReadAllBytes(Path.Combine(destination, "nested", "blob.bin")));
            Assert.False(File.Exists(Path.Combine(destination, "other.txt")));
        }
        finally { session.Dispose(); }

        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(exclusive.CanWrite);
        Assert.Throws<ObjectDisposedException>(() => session.Extract(["other.txt"], destination));
        Assert.False(File.Exists(Path.Combine(destination, "other.txt")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FrameworkTar_SessionListsAndExtractsSelectedEntries(bool compressed)
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, compressed ? "archive.tgz" : "archive.tar");
        using (var file = File.Create(path))
        {
            using Stream output = compressed ? new GZipStream(file, CompressionLevel.Fastest, leaveOpen: true) : file;
            using var writer = new TarWriter(output, leaveOpen: true);
            using var selected = new MemoryStream(Encoding.UTF8.GetBytes("selected"));
            using var other = new MemoryStream(Encoding.UTF8.GetBytes("other"));
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "nested/selected.txt") { DataStream = selected });
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "other.txt") { DataStream = other });
        }

        var destination = Path.Combine(temp.Path, "out");
        using (var session = ImageBrowserSession.Open(path, Token))
        {
            Assert.Contains(session.Entries, entry => entry.Path == "nested/selected.txt" && entry.Size == 8);
            session.Extract(["nested"], destination);
        }
        Assert.Equal("selected", File.ReadAllText(Path.Combine(destination, "nested", "selected.txt")));
        Assert.False(File.Exists(Path.Combine(destination, "other.txt")));
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(exclusive.CanWrite);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidExt4_ReleasesSourceWhenParserConstructionFails(bool sparse)
    {
        using var temp = new TempDirectory();
        var header = new byte[4096];
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(1080, 2), 0xEF53);
        var raw = temp.WriteBytes("invalid.img", header);
        var path = raw;
        if (sparse)
        {
            path = Path.Combine(temp.Path, "invalid-sparse.img");
            SparseImageConverter.ConvertRawToSparse(raw, path);
        }

        Assert.ThrowsAny<Exception>(() => ImageBrowserSession.Open(path, Token));

        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(exclusive.CanWrite);
    }

    [Fact]
    public void CancelledOpen_DoesNotReadMissingInput()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => ImageBrowserSession.Open("missing-image", cancellation.Token));
    }

    [Fact]
    public void Close_DefersReleaseUntilAcceptedOperationsFinishAndRejectsNewOnes()
    {
        var parser = new DisposalCounter();
        var source = new DisposalCounter();
        var session = new ImageBrowserSession("test", 0, parser, [], source);
        var first = session.AcquireLease();
        var second = session.AcquireLease();

        session.Dispose();
        session.Dispose();
        Assert.Equal(0, parser.Count);
        Assert.Equal(0, source.Count);
        Assert.Throws<ObjectDisposedException>(() => session.AcquireLease());
        first.Dispose();
        first.Dispose();
        Assert.Equal(0, parser.Count);
        second.Dispose();
        second.Dispose();
        Assert.Equal(1, parser.Count);
        Assert.Equal(1, source.Count);
    }

    [Fact]
    public void Close_ReleasesOwnedSourceEvenIfParserDisposalThrows()
    {
        var source = new DisposalCounter();
        var session = new ImageBrowserSession("test", 0, new DisposalCounter(throwOnDispose: true), [], source);

        Assert.Throws<IOException>(session.Dispose);

        Assert.Equal(1, source.Count);
        session.Dispose();
        Assert.Equal(1, source.Count);
    }

    private sealed class DisposalCounter(bool throwOnDispose = false) : IDisposable
    {
        internal int Count { get; private set; }
        public void Dispose()
        {
            Count++;
            if (throwOnDispose) throw new IOException("Disposal failure");
        }
    }
}