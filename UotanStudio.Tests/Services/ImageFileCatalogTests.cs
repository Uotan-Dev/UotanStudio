using Sdcb.SimdCodecs.Png;
using UotanStudio.Services.Images;
using UotanStudio.Services.Ui.Workspace.Documents;
using UotanStudio.UiAbstractions;
using Xunit;

namespace UotanStudio.Tests.Services;

public class ImageFileCatalogTests
{
    [Theory]
    [InlineData(".jpg")]
    [InlineData(".jpeg")]
    [InlineData(".png")]
    [InlineData(".apng")]
    [InlineData(".webp")]
    [InlineData(".gif")]
    [InlineData(".bmp")]
    [InlineData(".ico")]
    [InlineData(".cur")]
    [InlineData(".tif")]
    [InlineData(".tiff")]
    [InlineData(".qoi")]
    [InlineData(".pbm")]
    [InlineData(".pfm")]
    [InlineData(".jp2")]
    [InlineData(".jbig2")]
    [InlineData(".heic")]
    [InlineData(".avif")]
    [InlineData(".jxl")]
    [InlineData(".PSD")]
    public void IsImageExtension_RegisteredSuffix_ReturnsTrue(string extension)
    {
        Assert.True(ImageFileCatalog.IsImageExtension(extension));
        Assert.Equal(EditorOpenMode.Image, DocumentOpenRegistry.ResolveMode("picture" + extension));
    }

    [Fact]
    public void IsImageExtension_NonImageSuffix_ReturnsFalse()
    {
        Assert.False(ImageFileCatalog.IsImageExtension(".img"));
        Assert.False(ImageFileCatalog.IsImageExtension(".bin"));
        Assert.False(ImageFileCatalog.IsImageExtension(string.Empty));
    }

    [Fact]
    public void IsDecodableImageHeader_GeneratedPng_ReturnsTrue()
    {
        var png = PngWriter.Encode(new byte[4 * 4 * 4], 4, 4);

        Assert.True(ImageFileCatalog.IsDecodableImageHeader(png));
        Assert.Equal(EditorOpenMode.Image, DocumentOpenRegistry.ResolveMode("mystery.unknown", png, null));
    }

    [Fact]
    public void Inspect_GeneratedPng_ReadsAttributesAndBuildsPreview()
    {
        var path = Path.Combine(Path.GetTempPath(), "uotan-image-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            File.WriteAllBytes(path, PngWriter.Encode(new byte[4 * 4 * 4], 4, 4));

            var snapshot = ImageFileInspector.Inspect(path, TestContext.Current.CancellationToken);

            Assert.Null(snapshot.ErrorKey);
            Assert.NotNull(snapshot.PreviewPng);
            Assert.True(snapshot.Properties.Decodable);
            Assert.Equal("PNG", snapshot.Properties.CodecFormat);
            Assert.Equal("4 x 4", snapshot.Properties.Dimensions);
            Assert.True(snapshot.Properties.FileSize > 0);
            Assert.NotEmpty(snapshot.Properties.MagicDescription);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void Inspect_MissingFile_ReturnsLocalizedErrorKey()
    {
        var snapshot = ImageFileInspector.Inspect(
            Path.Combine(Path.GetTempPath(), "uotan-image-missing-" + Guid.NewGuid().ToString("N") + ".png"),
            TestContext.Current.CancellationToken);

        Assert.Null(snapshot.PreviewPng);
        Assert.Equal("Dock_ImageViewer_Error", snapshot.ErrorKey);
    }
}