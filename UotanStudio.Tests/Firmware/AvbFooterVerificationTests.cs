using System.Buffers.Binary;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.Avb;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.Avb.Models;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.Avb.Verification;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>Tests strict verification of footer-referenced VBMeta images. <para>测试 footer 引用的 VBMeta 镜像的严格验证。</para></summary>
public class AvbFooterVerificationTests
{
    [Fact]
    public void Verify_StandardFooterWithAlgorithmNone_IsUnsignedAndInvalid()
    {
        var image = BuildFooterImage(BuildVbmeta(algorithm: 0));

        var result = AvbVerifier.Verify(image);

        Assert.True(result.IsSigned);
        Assert.False(result.IsValid);
        Assert.Equal(0U, result.VBMetaHeader?.AlgorithmType);
        Assert.Contains("unsigned", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Verify_OutOfRangeAuthenticationOrAuxiliaryData_Fails(bool authentication)
    {
        var vbmeta = BuildVbmeta(algorithm: 1);
        BinaryPrimitives.WriteUInt64BigEndian(vbmeta.AsSpan(authentication ? 12 : 20, 8), 1);
        var image = BuildFooterImage(vbmeta);

        var result = AvbVerifier.Verify(image);

        Assert.True(result.IsSigned);
        Assert.False(result.IsValid);
        Assert.Contains(authentication ? "authentication" : "auxiliary", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Verify_UnknownAlgorithm_Fails()
    {
        var image = BuildFooterImage(BuildVbmeta(algorithm: uint.MaxValue));

        var result = AvbVerifier.Verify(image);

        Assert.True(result.IsSigned);
        Assert.False(result.IsValid);
        Assert.Contains("Unknown", result.ErrorMessage);
    }

    [Fact]
    public void Verify_FooterSizeTooLargeForByteArray_ReturnsInvalidWithoutOverflow()
    {
        var image = BuildFooterImage(BuildVbmeta(algorithm: 1));
        BinaryPrimitives.WriteUInt64BigEndian(
            image.AsSpan(image.Length - AvbFooter.Size + 28, 8),
            (ulong)int.MaxValue + 1);

        var exception = Record.Exception(() => AvbVerifier.Verify(image));
        var result = AvbVerifier.Verify(image);

        Assert.Null(exception);
        Assert.False(result.IsValid);
        Assert.Contains("size", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VerifyVBMeta_AlgorithmNoneWithTrailingPadding_IsUnsignedAndInvalid()
    {
        var vbmeta = BuildVbmeta(algorithm: 0);
        Array.Resize(ref vbmeta, vbmeta.Length + 64);

        var result = AvbVerifier.VerifyVBMeta(vbmeta);

        Assert.True(result.IsSigned);
        Assert.False(result.IsValid);
        Assert.Contains("unsigned", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Verify_UnsupportedFooterMajor_IsRejectedWithVersionError()
    {
        var image = BuildFooterImage(BuildVbmeta(algorithm: 0));
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(image.Length - AvbFooter.Size + 4, 4), 2);

        Assert.Null(AvbFooter.TryLoad(image));
        var result = AvbVerifier.Verify(image);

        Assert.False(result.IsSigned);
        Assert.False(result.IsValid);
        Assert.Contains("Unsupported", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2.0", result.ErrorMessage!);
    }

    [Fact]
    public async Task FormatHandler_StandaloneAlgorithmNone_CanHandleAndUnpack()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var imagePath = Path.Combine(root, "standalone.img");
        var outputPath = Path.Combine(root, "out");
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllBytesAsync(imagePath, BuildVbmeta(algorithm: 0), TestContext.Current.CancellationToken);
            var handler = new AvbFormatHandler();

            Assert.True(handler.CanHandle(imagePath));
            var unpack = await handler.UnpackAsync(imagePath, outputPath, cancellationToken: TestContext.Current.CancellationToken);

            Assert.True(unpack.Success, unpack.Summary);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static byte[] BuildVbmeta(uint algorithm)
    {
        var vbmeta = new byte[AvbVBMetaImageHeader.Size];
        "AVB0"u8.CopyTo(vbmeta);
        BinaryPrimitives.WriteUInt32BigEndian(vbmeta.AsSpan(4, 4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(vbmeta.AsSpan(28, 4), algorithm);
        return vbmeta;
    }

    private static byte[] BuildFooterImage(byte[] vbmeta)
    {
        const int originalSize = 64;
        var image = new byte[originalSize + vbmeta.Length + AvbFooter.Size];
        vbmeta.CopyTo(image, originalSize);
        var footer = image.AsSpan(image.Length - AvbFooter.Size);
        "AVBf"u8.CopyTo(footer);
        BinaryPrimitives.WriteUInt32BigEndian(footer.Slice(4, 4), 1);
        BinaryPrimitives.WriteUInt64BigEndian(footer.Slice(12, 8), originalSize);
        BinaryPrimitives.WriteUInt64BigEndian(footer.Slice(20, 8), originalSize);
        BinaryPrimitives.WriteUInt64BigEndian(footer.Slice(28, 8), (ulong)vbmeta.Length);
        return image;
    }
}