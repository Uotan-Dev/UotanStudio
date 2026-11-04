using UotanStudio.Compress;
using UotanStudio.DD;
using UotanStudio.DD.Images;
using Xunit;

namespace UotanStudio.Tests.DD;

public class ImageFormatResolverTests
{
    [Theory]
    [InlineData("system.img", ImageContainerKind.Raw)]
    [InlineData("system.ISO", ImageContainerKind.Raw)]
    [InlineData("rootfs.raw", ImageContainerKind.Raw)]
    [InlineData("package.zip", ImageContainerKind.Zip)]
    [InlineData("system.img.gz", ImageContainerKind.Compressed)]
    [InlineData("system.img.xz", ImageContainerKind.Compressed)]
    [InlineData("system.img.bz2", ImageContainerKind.Compressed)]
    public void Resolve_SupportedExtension_ReturnsExpectedContainer(string name, ImageContainerKind expected)
    {
        var format = ImageFormatResolver.Resolve(name);

        Assert.Equal(expected, format.Container);
    }

    [Fact]
    public void Resolve_GzipImage_SelectsGzipCodec()
    {
        var format = ImageFormatResolver.Resolve("system.img.gz");

        Assert.Equal(CompressionFormat.Gzip, format.Compression);
        Assert.True(format.RequiresDecoding);
    }

    [Fact]
    public void Resolve_RawImage_RequiresNoDecoding()
    {
        var format = ImageFormatResolver.Resolve("system.img");

        Assert.False(format.RequiresDecoding);
        Assert.Equal(CompressionFormat.None, format.Compression);
    }

    [Theory]
    [InlineData("disk.dmg")]
    [InlineData("disk.etch")]
    [InlineData("disk.vhd")]
    [InlineData("disk")]
    public void Resolve_UnsupportedExtension_ThrowsFlashException(string name)
    {
        Assert.Throws<FlashException>(() => ImageFormatResolver.Resolve(name));
    }
}