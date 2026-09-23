using UotanStudio.Firmware.BoundedContexts.Compression.Compressors;
using UotanStudio.Services.Flash;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class FlashCompressedImageTests
{
    [Fact]
    public async Task PrepareAsync_ZstdImage_StreamsToSeparateTemporaryFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "flash-zst-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "boot.img.zst");
        string? output = null;
        try
        {
            var bytes = Enumerable.Repeat((byte)42, 4096).ToArray();
            using (var input = new MemoryStream(bytes))
            using (var file = File.Create(source))
            {
                ZstdCompressor.Compress(input, file);
            }

            output = await FlashCompressedImage.PrepareAsync(source, TestContext.Current.CancellationToken);

            Assert.NotEqual(Path.Combine(root, "boot.img"), output);
            Assert.Equal(bytes, File.ReadAllBytes(output));
            Assert.True(File.Exists(source));
        }
        finally
        {
            if (output is not null) File.Delete(output);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PrepareAsync_InvalidFrame_RejectsWithoutPublishingImage()
    {
        var source = Path.Combine(Path.GetTempPath(), "flash-invalid-" + Guid.NewGuid().ToString("N") + ".zst");
        try
        {
            File.WriteAllBytes(source, [1, 2, 3, 4]);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                FlashCompressedImage.PrepareAsync(source, TestContext.Current.CancellationToken));
        }
        finally { File.Delete(source); }
    }
}
