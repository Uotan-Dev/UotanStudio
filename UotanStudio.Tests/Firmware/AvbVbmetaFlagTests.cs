using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.BootSecurity.Avb;
using UotanStudio.Firmware.BootSecurity.Avb.Signing;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class AvbVbmetaFlagTests
{
    [Fact]
    public async Task SetVbmetaFlagAsync_StandaloneVbmeta_TogglesBitAndPreservesOtherBytes()
    {
        var original = BuildStandaloneVbmeta(0x00010000);
        var path = Path.Combine(Path.GetTempPath(), "avb_flag_" + Guid.NewGuid().ToString("N") + ".img");
        try
        {
            await File.WriteAllBytesAsync(path, original, TestContext.Current.CancellationToken);

            var enabled = await AvbSigner.SetVbmetaFlagAsync(
                path, AvbVbmetaFlag.HashtreeDisabled, disabled: true, TestContext.Current.CancellationToken);

            Assert.True(enabled.Success, enabled.ErrorMessage);
            Assert.Equal(path, enabled.OutputPath);
            Assert.Equal("flags-patched", enabled.AlgorithmName);
            Assert.Equal(original.Length, enabled.OutputSize);

            var expected = (byte[])original.Clone();
            BinaryPrimitives.WriteUInt32BigEndian(expected.AsSpan(120, 4), 0x00010001);
            Assert.Equal(expected, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));

            var cleared = await AvbSigner.SetVbmetaFlagAsync(
                path, AvbVbmetaFlag.HashtreeDisabled, disabled: false, TestContext.Current.CancellationToken);

            Assert.True(cleared.Success, cleared.ErrorMessage);
            Assert.Equal(original, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SetVbmetaFlagAsync_FooterImage_PatchesVbmetaDeclaredByFooter()
    {
        const int vbmetaOffset = 4096;
        var original = BuildFooterImage(vbmetaOffset, flags: 0);
        var path = Path.Combine(Path.GetTempPath(), "avb_flag_footer_" + Guid.NewGuid().ToString("N") + ".img");
        try
        {
            await File.WriteAllBytesAsync(path, original, TestContext.Current.CancellationToken);

            var result = await AvbSigner.SetVbmetaFlagAsync(
                path, AvbVbmetaFlag.VerificationDisabled, disabled: true, TestContext.Current.CancellationToken);

            Assert.True(result.Success, result.ErrorMessage);
            var expected = (byte[])original.Clone();
            BinaryPrimitives.WriteUInt32BigEndian(expected.AsSpan(vbmetaOffset + 120, 4), 0x2);
            var patched = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(expected, patched);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SetVbmetaFlagAsync_BothFlags_MatchesFastbootFlagsByte()
    {
        var original = BuildStandaloneVbmeta(0);
        var path = Path.Combine(Path.GetTempPath(), "avb_flag_both_" + Guid.NewGuid().ToString("N") + ".img");
        try
        {
            await File.WriteAllBytesAsync(path, original, TestContext.Current.CancellationToken);

            Assert.True((await AvbSigner.SetVbmetaFlagAsync(
                path, AvbVbmetaFlag.HashtreeDisabled, disabled: true, TestContext.Current.CancellationToken)).Success);
            Assert.True((await AvbSigner.SetVbmetaFlagAsync(
                path, AvbVbmetaFlag.VerificationDisabled, disabled: true, TestContext.Current.CancellationToken)).Success);

            var patched = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(0x03, patched[123]);
            Assert.Equal(0x00000003U, BinaryPrimitives.ReadUInt32BigEndian(patched.AsSpan(120, 4)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SetVbmetaFlagAsync_NonAvbImage_FailsWithoutWriting()
    {
        var original = new byte[512];
        Array.Fill(original, (byte)0xAA);
        var path = Path.Combine(Path.GetTempPath(), "avb_flag_invalid_" + Guid.NewGuid().ToString("N") + ".img");
        try
        {
            await File.WriteAllBytesAsync(path, original, TestContext.Current.CancellationToken);

            var result = await AvbSigner.SetVbmetaFlagAsync(
                path, AvbVbmetaFlag.VerificationDisabled, disabled: true, TestContext.Current.CancellationToken);

            Assert.False(result.Success);
            Assert.Equal(original, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static byte[] BuildStandaloneVbmeta(uint flags)
    {
        var image = new byte[512];
        "AVB0"u8.CopyTo(image);
        Encoding.ASCII.GetBytes("avbtool 1.3.0").CopyTo(image, 128);
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(120, 4), flags);
        return image;
    }

    private static byte[] BuildFooterImage(int vbmetaOffset, uint flags)
    {
        var image = new byte[8192];
        "AVB0"u8.CopyTo(image.AsSpan(vbmetaOffset));
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(vbmetaOffset + 120, 4), flags);
        var footerOffset = image.Length - AvbFooter.Size;
        "AVBf"u8.CopyTo(image.AsSpan(footerOffset));
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(footerOffset + 4, 4), AvbFooter.SupportedVersionMajor);
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(footerOffset + 8, 4), AvbFooter.SupportedVersionMinor);
        BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(footerOffset + 12, 8), 1024);
        BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(footerOffset + 20, 8), (ulong)vbmetaOffset);
        BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(footerOffset + 28, 8), 2048);
        return image;
    }
}