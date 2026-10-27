using UotanStudio.Firmware.BootSecurity.Avb;
using UotanStudio.Firmware.BootSecurity.Avb.Signing;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class AvbFooterRemovalTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private string _root => _temp.Path;

    [Fact]
    public async Task RemoveFooterAsync_SignedImage_RestoresOriginalBytesAndSize()
    {
        var token = TestContext.Current.CancellationToken;
        using var key = SigningTestKeys.Create();
        var original = BuildContent(4097, 13);
        var imagePath = _temp.WriteBytes("boot.img", original);
        var signedPath = Path.Combine(_root, "boot.signed.img");
        var signResult = await AvbSigner.AddHashFooterAsync(imagePath, "boot", key, signedPath, token);
        Assert.True(signResult.Success, signResult.ErrorMessage);
        var outputPath = Path.Combine(_root, "restored.img");

        var result = await AvbSigner.RemoveFooterAsync(signedPath, outputPath, cancellationToken: token);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(original.Length, result.OutputSize);
        Assert.Equal(original, await File.ReadAllBytesAsync(outputPath, token));
    }

    [Fact]
    public async Task RemoveFooterAsync_OutputEqualsInput_ReturnsFailureAndKeepsSignedImage()
    {
        var token = TestContext.Current.CancellationToken;
        using var key = SigningTestKeys.Create();
        var imagePath = _temp.WriteBytes("boot.img", BuildContent(4097, 13));
        var signedPath = Path.Combine(_root, "boot.signed.img");
        var signResult = await AvbSigner.AddHashFooterAsync(imagePath, "boot", key, signedPath, token);
        Assert.True(signResult.Success, signResult.ErrorMessage);
        var before = await File.ReadAllBytesAsync(signedPath, token);

        var result = await AvbSigner.RemoveFooterAsync(signedPath, signedPath, cancellationToken: token);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
        Assert.Equal(before, await File.ReadAllBytesAsync(signedPath, token));
        Assert.True(AvbFooter.TryLoad(before).HasValue);
    }

    [Fact]
    public async Task RemoveFooterAsync_ExistingOutputWithoutOverwrite_ReturnsFailureAndPreservesOutput()
    {
        var token = TestContext.Current.CancellationToken;
        using var key = SigningTestKeys.Create();
        var imagePath = _temp.WriteBytes("boot.img", BuildContent(4097, 13));
        var signedPath = Path.Combine(_root, "boot.signed.img");
        var signResult = await AvbSigner.AddHashFooterAsync(imagePath, "boot", key, signedPath, token);
        Assert.True(signResult.Success, signResult.ErrorMessage);
        var outputPath = Path.Combine(_root, "existing.img");
        await File.WriteAllBytesAsync(outputPath, [9, 8, 7], token);

        var result = await AvbSigner.RemoveFooterAsync(signedPath, outputPath, overwrite: false, token);

        Assert.False(result.Success);
        Assert.Equal(new byte[] { 9, 8, 7 }, await File.ReadAllBytesAsync(outputPath, token));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp-*"));
    }

    [Fact]
    public async Task RemoveFooterAsync_CanceledBeforeStart_PreservesExistingOutput()
    {
        var token = TestContext.Current.CancellationToken;
        using var key = SigningTestKeys.Create();
        var imagePath = _temp.WriteBytes("boot.img", BuildContent(4097, 13));
        var signedPath = Path.Combine(_root, "boot.signed.img");
        var signResult = await AvbSigner.AddHashFooterAsync(imagePath, "boot", key, signedPath, token);
        Assert.True(signResult.Success, signResult.ErrorMessage);
        var outputPath = Path.Combine(_root, "existing.img");
        await File.WriteAllBytesAsync(outputPath, [9, 8, 7], token);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => AvbSigner.RemoveFooterAsync(signedPath, outputPath, cancellationToken: cancellation.Token));

        Assert.Equal(new byte[] { 9, 8, 7 }, await File.ReadAllBytesAsync(outputPath, token));
    }

    public void Dispose() => _temp.Dispose();

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