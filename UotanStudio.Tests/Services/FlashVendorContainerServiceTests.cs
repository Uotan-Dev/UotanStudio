using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Nb0.Packing;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.NTPi;
using UotanStudio.Services.Flash;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class FlashVendorContainerServiceTests
{
    [Theory]
    [InlineData(".nb0")]
    [InlineData(".ntpi")]
    public async Task ExtractAsync_SelectedImage_IgnoresOtherEntries(string extension)
    {
        var root = Path.Combine(Path.GetTempPath(), "flash-container-" + Guid.NewGuid().ToString("N"));
        var images = Path.Combine(root, "images");
        Directory.CreateDirectory(images);
        try
        {
            File.WriteAllBytes(Path.Combine(images, "boot.img"), [1, 2, 3, 4]);
            File.WriteAllBytes(Path.Combine(images, "vendor.img"), [5, 6, 7, 8]);
            File.WriteAllBytes(Path.Combine(images, "readme.txt"), [9]);
            var path = Path.Combine(root, "firmware" + extension);
            if (extension == ".nb0")
            {
                var builder = Nb0Packer.CreateBuilder();
                foreach (var file in Directory.GetFiles(images)) builder.AddFile(file, Path.GetFileName(file));
                builder.Build(path);
            }
            else
            {
                using var writer = NtpiWriter.CreateDefault();
                writer.Pack(images, path);
            }

            var entries = await FlashVendorContainerService.ListAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(["boot", "vendor"], entries.Select(entry => entry.Name));
            var output = await FlashVendorContainerService.ExtractAsync(path, ["boot.img"], root,
                TestContext.Current.CancellationToken);
            Assert.Equal([1, 2, 3, 4], File.ReadAllBytes(Path.Combine(output, "boot.img")));
            Assert.False(File.Exists(Path.Combine(output, "vendor.img")));
            Assert.False(File.Exists(Path.Combine(output, "readme.txt")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}