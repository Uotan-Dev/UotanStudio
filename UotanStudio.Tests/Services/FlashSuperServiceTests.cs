using UotanStudio.Firmware.BoundedContexts.BlockImage.Lp;
using UotanStudio.Services.Flash;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class FlashSuperServiceTests
{
    [Fact]
    public async Task ExtractAsync_SelectedSuperPartition_DoesNotMaterializeOthers()
    {
        var root = Path.Combine(Path.GetTempPath(), "flash-super-" + Guid.NewGuid().ToString("N"));
        var images = Path.Combine(root, "images");
        Directory.CreateDirectory(images);
        try
        {
            File.WriteAllBytes(Path.Combine(images, "system.img"), [1, 2, 3]);
            File.WriteAllBytes(Path.Combine(images, "vendor.img"), [4, 5, 6]);
            var path = Path.Combine(root, "super.img");
            var packed = await new LpFormatHandler().PackAsync(images, path, TestContext.Current.CancellationToken);
            Assert.True(packed.Success, packed.Summary);

            var listed = await FlashSuperService.ListAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(["system", "vendor"], listed.Select(partition => partition.Name));

            var output = await FlashSuperService.ExtractAsync(path, ["system"], root, TestContext.Current.CancellationToken);
            Assert.Equal([1, 2, 3], File.ReadAllBytes(Path.Combine(output, "system.img")).Take(3));
            Assert.False(File.Exists(Path.Combine(output, "vendor.img")));
            Assert.Empty(Directory.GetDirectories(root, "*.tmp"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ExtractAsync_UnknownPartition_DoesNotPublishPartialOutput()
    {
        var root = Path.Combine(Path.GetTempPath(), "flash-super-" + Guid.NewGuid().ToString("N"));
        var images = Path.Combine(root, "images");
        Directory.CreateDirectory(images);
        try
        {
            File.WriteAllBytes(Path.Combine(images, "system.img"), [1]);
            var path = Path.Combine(root, "super.img");
            var packed = await new LpFormatHandler().PackAsync(images, path, TestContext.Current.CancellationToken);
            Assert.True(packed.Success, packed.Summary);

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                FlashSuperService.ExtractAsync(path, ["missing"], root, TestContext.Current.CancellationToken));
            Assert.Empty(Directory.GetDirectories(root, "*.tmp"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
