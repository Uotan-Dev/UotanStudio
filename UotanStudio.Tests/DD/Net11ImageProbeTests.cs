using System.IO.Compression;
using UotanStudio.DD;
using UotanStudio.DD.Images;
using Xunit;

namespace UotanStudio.Tests.DD;

public sealed class Net11ImageProbeTests
{
    private static readonly byte[] Payload = [1, 2, 3];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Probe_EmptyOrDirectoryOnlyZip_ReportsZeroFiles(bool includeDirectory)
    {
        using var directory = new FlashTestDirectory();
        string[] names = includeDirectory ? ["folder/"] : [];
        var path = CreateArchive(directory, "empty.zip", names);

        var error = Assert.Throws<FlashException>(() => ImageProbe.Probe(path));

        Assert.Contains("archive contains 0 (none)", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Probe_SingleFileWithDirectories_SelectsOnlyFileEntry()
    {
        using var directory = new FlashTestDirectory();
        var path = CreateArchive(directory, "single.zip", ["folder/", "nested/folder/", "system.img"]);

        var image = ImageProbe.Probe(path);

        Assert.Equal(Payload.Length, image.Size);
        Assert.Equal("system.img", image.ZipEntryName);
        Assert.Equal(ImageContainerKind.Zip, image.Format.Container);
    }

    [Fact]
    public void Probe_MultipleFiles_ReportsAccurateCountAndLimitedNames()
    {
        using var directory = new FlashTestDirectory();
        var path = CreateArchive(directory, "multiple.zip",
            ["folder/", "first.img", "second.img", "third.img", "fourth.img", "fifth.img"]);

        var error = Assert.Throws<FlashException>(() => ImageProbe.Probe(path));

        Assert.Contains("archive contains 5", error.Message, StringComparison.Ordinal);
        Assert.Contains("first.img, second.img, third.img (2 more)", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("fourth.img", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("fifth.img", error.Message, StringComparison.Ordinal);
        Assert.InRange(error.Message.Length, 1, 512);
    }

    [Fact]
    public void Probe_TwoFiles_ListsBothNamesWithoutOmittedCount()
    {
        using var directory = new FlashTestDirectory();
        var path = CreateArchive(directory, "two.zip", ["first.img", "second.img"]);

        var error = Assert.Throws<FlashException>(() => ImageProbe.Probe(path));

        Assert.Contains("archive contains 2 (first.img, second.img)", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("more)", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Probe_LongEntryNames_TruncatesDiagnosticAndBoundsMessage()
    {
        using var directory = new FlashTestDirectory();
        var firstName = new string('a', 4096) + ".img";
        var secondName = new string('b', 4096) + ".img";
        var path = CreateArchive(directory, "long-names.zip", [firstName, secondName]);

        var error = Assert.Throws<FlashException>(() => ImageProbe.Probe(path));

        Assert.Contains("archive contains 2", error.Message, StringComparison.Ordinal);
        Assert.Contains(new string('a', 96) + "...", error.Message, StringComparison.Ordinal);
        Assert.Contains(new string('b', 96) + "...", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(firstName, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(secondName, error.Message, StringComparison.Ordinal);
        Assert.InRange(error.Message.Length, 1, 512);
    }

    private static string CreateArchive(FlashTestDirectory directory, string fileName, string[] names)
    {
        var path = directory.Combine(fileName);
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            foreach (var name in names)
            {
                var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
                if (name.EndsWith('/'))
                {
                    continue;
                }

                using var content = entry.Open();
                content.Write(Payload);
            }
        }

        return path;
    }
}