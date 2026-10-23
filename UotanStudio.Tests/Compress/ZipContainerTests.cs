using System.IO.Compression;
using UotanStudio.Compress.Zip;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Compress;

public sealed class ZipContainerTests
{
    [Theory]
    [InlineData("boot.img", "boot.img")]
    [InlineData("images/system.img", "images/system.img")]
    [InlineData("images\\system.img", "images/system.img")]
    [InlineData("images\\sub\\system.img", "images/sub/system.img")]
    public void NormalizeEntryPath_RelativeName_ReturnsForwardSeparators(string entryName, string expected)
    {
        var normalized = ZipContainer.NormalizeEntryPath(entryName);

        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/boot.img")]
    [InlineData("\\boot.img")]
    [InlineData("../boot.img")]
    [InlineData("images/../boot.img")]
    [InlineData("images/./boot.img")]
    [InlineData("images//system.img")]
    [InlineData("C:/boot.img")]
    [InlineData("images/")]
    public void NormalizeEntryPath_UnsafeName_ThrowsArgumentException(string entryName)
    {
        Assert.Throws<ArgumentException>(() => ZipContainer.NormalizeEntryPath(entryName));
    }

    [Fact]
    public void OpenRead_WithoutOptions_ReturnsEveryEntry()
    {
        using var temp = new TempDirectory();
        var path = CreateArchive(temp.Path, "plain.zip",
            ("boot.img", Bytes(1, 2, 3)), ("sub/system.img", Bytes(4, 5)));

        using var archive = ZipContainer.OpenRead(path);

        Assert.Equal(2, archive.Entries.Count);
        Assert.Equal(["boot.img", "sub/system.img"], archive.Entries.Select(entry => entry.FullName).ToArray());
    }

    [Fact]
    public void OpenRead_WithoutOptions_AppliesNoDefaultEntryBound()
    {
        using var temp = new TempDirectory();
        var entries = Enumerable.Range(0, 150).Select(index => ($"entry-{index:D3}.bin", Bytes(0))).ToArray();
        var path = CreateArchive(temp.Path, "many.zip", entries);

        using var archive = ZipContainer.OpenRead(path);

        Assert.Equal(150, archive.Entries.Count);
    }

    [Fact]
    public void OpenRead_EntryCountAboveBound_ThrowsInvalidDataException()
    {
        using var temp = new TempDirectory();
        var path = CreateArchive(temp.Path, "count.zip", ("one.bin", Bytes(1)), ("two.bin", Bytes(2)));

        Assert.Throws<InvalidDataException>(() =>
            ZipContainer.OpenRead(path, new ZipContainerOptions(MaxEntries: 1)));
    }

    [Fact]
    public void OpenRead_EntryBytesAboveBound_ThrowsInvalidDataException()
    {
        using var temp = new TempDirectory();
        var path = CreateArchive(temp.Path, "entry-bytes.zip", ("payload.bin", Bytes(0, 0, 0, 0, 0, 0, 0, 0)));

        Assert.Throws<InvalidDataException>(() =>
            ZipContainer.OpenRead(path, new ZipContainerOptions(MaxEntryBytes: 4)));
    }

    [Fact]
    public void OpenRead_TotalBytesAboveBound_ThrowsInvalidDataException()
    {
        using var temp = new TempDirectory();
        var path = CreateArchive(temp.Path, "total-bytes.zip",
            ("one.bin", Bytes(0, 0, 0, 0, 0, 0, 0, 0)), ("two.bin", Bytes(0, 0, 0, 0, 0, 0, 0, 0)));

        Assert.Throws<InvalidDataException>(() =>
            ZipContainer.OpenRead(path, new ZipContainerOptions(MaxTotalBytes: 12)));
    }

    [Fact]
    public void OpenRead_NegativeBound_ThrowsArgumentOutOfRangeException()
    {
        using var temp = new TempDirectory();
        var path = CreateArchive(temp.Path, "negative.zip", ("boot.img", Bytes(1)));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ZipContainer.OpenRead(path, new ZipContainerOptions(MaxEntries: -1)));
    }

    [Fact]
    public void OpenRead_LeaveOpenStream_KeepsSourceOpenAfterDispose()
    {
        using var buffer = new MemoryStream();
        using (var created = ZipContainer.Create(buffer, leaveOpen: true))
        {
            using var entry = created.CreateEntry("boot.img", CompressionLevel.NoCompression).Open();
            entry.Write(Bytes(9));
        }

        buffer.Position = 0;
        using (var archive = ZipContainer.OpenRead(buffer, leaveOpen: true))
        {
            Assert.Single(archive.Entries);
        }

        Assert.True(buffer.CanRead);
    }

    [Fact]
    public void Create_ExistingFile_ThrowsIOExceptionAndKeepsContent()
    {
        using var temp = new TempDirectory();
        var path = temp.Write("existing.zip", "original");

        Assert.Throws<IOException>(() => ZipContainer.Create(path));

        Assert.Equal("original", File.ReadAllText(path));
    }

    [Fact]
    public void CreateFromDirectory_NestedFiles_StoresForwardSlashRelativeNames()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "source");
        Directory.CreateDirectory(source);
        File.WriteAllBytes(Path.Combine(source, "boot.img"), Bytes(1, 2));
        Directory.CreateDirectory(Path.Combine(source, "sub"));
        File.WriteAllBytes(Path.Combine(source, "sub", "system.img"), Bytes(3, 4, 5));
        var destination = Path.Combine(temp.Path, "out.zip");

        ZipContainer.CreateFromDirectory(source, destination);

        using var archive = ZipContainer.OpenRead(destination);
        Assert.Equal(["boot.img", "sub/system.img"], archive.Entries.Select(entry => entry.FullName).ToArray());
        using var entry = archive.GetEntry("sub/system.img")!.Open();
        using var content = new MemoryStream();
        entry.CopyTo(content);
        Assert.Equal(Bytes(3, 4, 5), content.ToArray());
    }

    [Fact]
    public void CreateFromDirectory_ExistingDestination_ThrowsIOExceptionAndKeepsContent()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "source");
        Directory.CreateDirectory(source);
        File.WriteAllBytes(Path.Combine(source, "boot.img"), Bytes(1));
        var destination = temp.Write("out.zip", "original");

        Assert.Throws<IOException>(() => ZipContainer.CreateFromDirectory(source, destination));

        Assert.Equal("original", File.ReadAllText(destination));
    }

    [Fact]
    public void CreateFromDirectory_TotalBoundExceeded_ThrowsAndRemovesPartialArchive()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "source");
        Directory.CreateDirectory(source);
        File.WriteAllBytes(Path.Combine(source, "boot.img"), Bytes(0, 0, 0, 0, 0, 0, 0, 0, 0, 0));
        var destination = Path.Combine(temp.Path, "out.zip");

        Assert.Throws<InvalidDataException>(() =>
            ZipContainer.CreateFromDirectory(source, destination, new ZipContainerOptions(MaxTotalBytes: 4)));

        Assert.False(File.Exists(destination));
    }

    [Fact]
    public void CopyEntryTo_EntryWithinBound_WritesDeclaredBytes()
    {
        using var temp = new TempDirectory();
        var path = CreateArchive(temp.Path, "copy.zip", ("boot.img", Bytes(7, 8, 9)));
        using var archive = ZipContainer.OpenRead(path);
        using var destination = new MemoryStream();

        ZipContainer.CopyEntryTo(archive.GetEntry("boot.img")!, destination, maximumBytes: 16, TestContext.Current.CancellationToken);

        Assert.Equal(Bytes(7, 8, 9), destination.ToArray());
    }

    [Fact]
    public void CopyEntryTo_BoundBelowDeclaredLength_ThrowsInvalidDataException()
    {
        using var temp = new TempDirectory();
        var path = CreateArchive(temp.Path, "copy-bound.zip", ("boot.img", Bytes(7, 8, 9)));
        using var archive = ZipContainer.OpenRead(path);
        using var destination = new MemoryStream();

        Assert.Throws<InvalidDataException>(() =>
            ZipContainer.CopyEntryTo(archive.GetEntry("boot.img")!, destination, maximumBytes: 2, TestContext.Current.CancellationToken));

        Assert.Equal(0L, destination.Length);
    }

    [Fact]
    public void CopyEntryTo_NegativeBound_ThrowsArgumentOutOfRangeException()
    {
        using var temp = new TempDirectory();
        var path = CreateArchive(temp.Path, "copy-negative.zip", ("boot.img", Bytes(7)));
        using var archive = ZipContainer.OpenRead(path);
        using var destination = new MemoryStream();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ZipContainer.CopyEntryTo(archive.GetEntry("boot.img")!, destination, maximumBytes: -1, TestContext.Current.CancellationToken));
    }

    private static byte[] Bytes(params byte[] values) => values;

    private static string CreateArchive(string directory, string fileName, params (string Name, byte[] Content)[] entries)
    {
        var path = Path.Combine(directory, fileName);
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
        using (var archive = ZipContainer.Create(stream))
        {
            foreach (var (name, content) in entries)
            {
                using var entry = archive.CreateEntry(name, CompressionLevel.NoCompression).Open();
                entry.Write(content);
            }
        }

        return path;
    }
}