using System.IO.Compression;
using UotanStudio.Compress;
using UotanStudio.Compress.Zip;
using UotanStudio.DD;
using UotanStudio.DD.Images;
using Xunit;

namespace UotanStudio.Tests.DD;

public class ImageProbeTests
{
    [Fact]
    public void Probe_RawImage_ReportsExactLength()
    {
        using var directory = new FlashTestDirectory();
        var bytes = FlashTestData.CreateImageData(4096, 3);
        var path = directory.WriteFile("system.img", bytes);

        var image = ImageProbe.Probe(path);

        Assert.Equal(bytes.Length, image.Size);
        Assert.Equal(bytes.Length, image.CompressedSize);
        Assert.Equal("system.img", image.DisplayName);
        Assert.False(image.IsSizeEstimated);
        Assert.Null(image.ZipEntryName);
    }

    [Fact]
    public void Probe_GzipImage_ReportsEstimatedSize()
    {
        using var directory = new FlashTestDirectory();
        var bytes = FlashTestData.CreateImageData(8192, 5);
        var path = directory.Combine("system.img.gz");
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
        using (var gzip = CompressionService.CreateCompressionStream(file, CompressionFormat.Gzip))
        {
            gzip.Write(bytes);
        }

        var image = ImageProbe.Probe(path);

        Assert.Null(image.Size);
        Assert.True(image.IsSizeEstimated);
        Assert.Equal(new FileInfo(path).Length, image.CompressedSize);
    }

    [Fact]
    public void Probe_SingleEntryZip_ReportsEntryLengthAndName()
    {
        using var directory = new FlashTestDirectory();
        var bytes = FlashTestData.CreateImageData(4096, 7);
        var path = directory.Combine("system.zip");
        using (var archive = ZipContainer.Create(path))
        {
            using var entry = archive.CreateEntry("system.img", CompressionLevel.Fastest).Open();
            entry.Write(bytes);
        }

        var image = ImageProbe.Probe(path);

        Assert.Equal(bytes.Length, image.Size);
        Assert.Equal("system.img", image.ZipEntryName);
        Assert.Equal(ImageContainerKind.Zip, image.Format.Container);
    }

    [Fact]
    public void Probe_MultiEntryZip_ThrowsFlashException()
    {
        using var directory = new FlashTestDirectory();
        var bytes = FlashTestData.CreateImageData(1024, 9);
        var path = directory.Combine("multi.zip");
        using (var archive = ZipContainer.Create(path))
        {
            foreach (var name in new[] { "first.img", "second.img" })
            {
                using var entry = archive.CreateEntry(name, CompressionLevel.Fastest).Open();
                entry.Write(bytes);
            }
        }

        Assert.Throws<FlashException>(() => ImageProbe.Probe(path));
    }

    [Fact]
    public void Probe_MissingFile_ThrowsFlashException()
    {
        Assert.Throws<FlashException>(() => ImageProbe.Probe(Path.Combine(Path.GetTempPath(), "uotan-missing-image.img")));
    }
}