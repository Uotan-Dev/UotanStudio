using UotanStudio.Services.Flash;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class FlashPackageReaderTests
{
    [Fact]
    public void Read_ImagesFolder_IgnoresUnrelatedAndEmptyFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "flash-reader-" + Guid.NewGuid().ToString("N"));
        var images = Path.Combine(root, "images");
        Directory.CreateDirectory(images);
        try
        {
            File.WriteAllBytes(Path.Combine(images, "boot.img"), [1, 2, 3]);
            File.WriteAllBytes(Path.Combine(images, "system_a.img"), [4]);
            File.WriteAllBytes(Path.Combine(images, "invalid-name.img"), [5]);
            File.WriteAllBytes(Path.Combine(images, "empty.img"), []);
            File.WriteAllBytes(Path.Combine(root, "other.img"), [6]);

            var entries = FlashPackageReader.Read(root);

            Assert.Equal(["boot", "system_a"], entries.Select(entry => entry.Partition));
            Assert.Equal([3L, 1L], entries.Select(entry => entry.Length));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Read_CompetingRawAndCompressedImage_RejectsAmbiguousPartition()
    {
        var root = Path.Combine(Path.GetTempPath(), "flash-reader-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllBytes(Path.Combine(root, "boot.img"), [1]);
            File.WriteAllBytes(Path.Combine(root, "boot.img.zst"), [2]);

            Assert.Throws<InvalidDataException>(() => FlashPackageReader.Read(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("super_empty.img")]
    [InlineData("super_empty.img.zst")]
    public void Read_SuperEmptyImage_RejectsUnsupportedWipeWorkflow(string name)
    {
        var root = Path.Combine(Path.GetTempPath(), "flash-reader-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllBytes(Path.Combine(root, name), [1]);

            Assert.Throws<InvalidDataException>(() => FlashPackageReader.Read(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Read_ValidSuperEmptyImage_ListsSpecialStepSource()
    {
        var root = Path.Combine(Path.GetTempPath(), "flash-reader-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "SUPER_EMPTY.img");
            File.WriteAllBytes(path, FlashSuperEmptyServiceTests.EmptyMetadata());

            var entry = Assert.Single(FlashPackageReader.Read(root));

            Assert.Equal("SUPER_EMPTY", entry.Partition);
            Assert.Equal(path, entry.Path);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}