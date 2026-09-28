using System.Buffers.Binary;
using UotanStudio.Firmware.BootSecurity.Avb.Signing;
using UotanStudio.Services.Flash;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class FlashVbmetaImageTests
{
    [Fact]
    public async Task PrepareAsync_ValidImage_LeavesSourceUntouchedAndSetsFlags()
    {
        var root = Path.Combine(Path.GetTempPath(), "flash-vbmeta-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "vbmeta.img");
        string? output = null;
        try
        {
            var bytes = new byte[256];
            "AVB0"u8.CopyTo(bytes);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4, 4), 1);
            await File.WriteAllBytesAsync(source, bytes, TestContext.Current.CancellationToken);

            output = await FlashVbmetaImage.PrepareAsync(source, TestContext.Current.CancellationToken);

            Assert.Equal(bytes, File.ReadAllBytes(source));
            var patched = File.ReadAllBytes(output);
            Assert.Equal(bytes.Length, patched.Length);
            Assert.Equal(AvbSigner.VbmetaFlagHashtreeDisabled | AvbSigner.VbmetaFlagVerificationDisabled,
                BinaryPrimitives.ReadUInt32BigEndian(patched.AsSpan(120, 4)));
        }
        finally
        {
            if (output is not null) File.Delete(output);
            Directory.Delete(root, recursive: true);
        }
    }
}