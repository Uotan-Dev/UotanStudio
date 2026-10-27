using System.Security.Cryptography;
using UotanStudio.Firmware.BootSecurity.Avb.Signing;
using UotanStudio.Firmware.BootSecurity.Avb.Verification;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class AvbImageVerifierTests : IDisposable
{
    private const int Block = 4096;
    private readonly TempDirectory _temp = new();
    private string _root => _temp.Path;

    [Fact]
    public async Task VerifyAsync_HashSignedImage_ReportsValidSignatureAndContent()
    {
        var token = TestContext.Current.CancellationToken;
        using var key = SigningTestKeys.Create();
        var signedPath = await SignHashImageAsync("boot", 4097, 31, key);

        var result = await AvbImageVerifier.VerifyAsync(signedPath, key, token);

        Assert.True(result.IsSigned);
        Assert.True(result.SignatureIsValid);
        Assert.True(result.ContentIsValid);
        Assert.True(result.IsValid);
        Assert.Null(result.ErrorMessage);
        Assert.Null(result.FecIsValid);
        Assert.Equal("boot", Assert.Single(result.Descriptors).PartitionName);
    }

    [Fact]
    public async Task VerifyAsync_HashtreeSignedImage_ReportsValidSignatureAndContent()
    {
        var token = TestContext.Current.CancellationToken;
        using var key = SigningTestKeys.Create();
        var imagePath = _temp.WriteBytes("system.img", BuildContent(3 * Block, 11));
        var signedPath = Path.Combine(_root, "system.signed.img");
        var signResult = await AvbSigner.AddHashtreeFooterAsync(
            imagePath, "system", key, signedPath, cancellationToken: token);
        Assert.True(signResult.Success, signResult.ErrorMessage);

        var result = await AvbImageVerifier.VerifyAsync(signedPath, key, token);

        Assert.True(result.IsSigned);
        Assert.True(result.SignatureIsValid);
        Assert.True(result.ContentIsValid);
        Assert.True(result.IsValid);
        var descriptor = Assert.Single(result.Descriptors);
        Assert.Equal("system", descriptor.PartitionName);
        Assert.Equal((ulong)(3 * Block), descriptor.ImageSize);
        Assert.Equal((uint)Block, descriptor.DataBlockSize);
        Assert.True(descriptor.TreeSize > 0);
    }

    [Fact]
    public async Task VerifyAsync_FlippedContentByte_ReportsInvalidContentButValidSignature()
    {
        var token = TestContext.Current.CancellationToken;
        using var key = SigningTestKeys.Create();
        var signedPath = await SignHashImageAsync("boot", 4097, 31, key);
        var signedBytes = await File.ReadAllBytesAsync(signedPath, token);
        signedBytes[10] ^= 1;
        await File.WriteAllBytesAsync(signedPath, signedBytes, token);

        var result = await AvbImageVerifier.VerifyAsync(signedPath, cancellationToken: token);

        Assert.True(result.IsSigned);
        Assert.True(result.SignatureIsValid);
        Assert.False(result.ContentIsValid);
        Assert.False(result.IsValid);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task VerifyAsync_DifferentTrustedPublicKey_ReportsInvalidSignature()
    {
        var token = TestContext.Current.CancellationToken;
        using var signer = SigningTestKeys.Create();
        using var other = SigningTestKeys.Create(2048, alternate: true);
        var signedPath = await SignHashImageAsync("boot", 4097, 31, signer);

        var result = await AvbImageVerifier.VerifyAsync(signedPath, other, token);

        Assert.True(result.IsSigned);
        Assert.False(result.SignatureIsValid);
        Assert.False(result.IsValid);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task VerifyAsync_UnsignedImage_ReportsNotSigned()
    {
        var token = TestContext.Current.CancellationToken;
        var imagePath = _temp.WriteBytes("plain.img", BuildContent(4097, 3));

        var result = await AvbImageVerifier.VerifyAsync(imagePath, cancellationToken: token);

        Assert.False(result.IsSigned);
        Assert.False(result.IsValid);
        Assert.Null(result.Footer);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task VerifyAsync_CanceledBeforeStart_ThrowsOperationCanceledException()
    {
        var token = TestContext.Current.CancellationToken;
        var imagePath = _temp.WriteBytes("plain.img", BuildContent(Block, 3));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => AvbImageVerifier.VerifyAsync(imagePath, cancellationToken: cancellation.Token));
    }

    public void Dispose() => _temp.Dispose();

    private async Task<string> SignHashImageAsync(string partitionName, int length, int seed, RSA key)
    {
        var token = TestContext.Current.CancellationToken;
        var imagePath = _temp.WriteBytes(partitionName + ".img", BuildContent(length, seed));
        var signedPath = Path.Combine(_root, partitionName + ".signed.img");
        var signResult = await AvbSigner.AddHashFooterAsync(imagePath, partitionName, key, signedPath, token);
        Assert.True(signResult.Success, signResult.ErrorMessage);
        return signedPath;
    }

    private static byte[] BuildContent(int length, int seed)
    {
        var content = new byte[length];
        for (var index = 0; index < content.Length; index++)
        {
            content[index] = (byte)(seed + (index * 31));
        }

        return content;
    }
}