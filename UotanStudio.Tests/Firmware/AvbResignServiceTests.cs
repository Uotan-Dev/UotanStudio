using System.Security.Cryptography;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.Avb.Signing;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.Avb.Verification;
using UotanStudio.Services.Firmware;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class AvbResignServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uotan-avb-service-" + Guid.NewGuid().ToString("N"));

    public AvbResignServiceTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData(AvbResignMode.Hash)]
    [InlineData(AvbResignMode.Hashtree)]
    public async Task ResignAvbImageAsync_ExplicitOptions_ReachesBackendWithoutLosingParameters(AvbResignMode mode)
    {
        var token = TestContext.Current.CancellationToken;
        var input = Path.Combine(_root, "input.img");
        var output = Path.Combine(_root, "system.img");
        var pem = Path.Combine(_root, "key.pem");
        using var rsa = RSA.Create(2048);
        await File.WriteAllTextAsync(pem, rsa.ExportRSAPrivateKeyPem(), token);
        await File.WriteAllBytesAsync(input, new byte[8193], token);

        var result = await FirmwareService.Instance.ResignAvbImageAsync(input, pem, output, new AvbResignOptions
        {
            Mode = mode,
            PartitionName = "system",
            Salt = [1, 2, 3],
            RollbackIndex = 17,
            RollbackIndexLocation = 7,
            PartitionSize = 131072,
            DataBlockSize = 512,
            HashBlockSize = 4096
        }, token);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(131072, result.OutputSize);
        await using var image = File.OpenRead(output);
        var verified = AvbVerifier.VerifyFromStream(image, image.Length);
        Assert.True(verified.IsValid, verified.ErrorMessage);
        Assert.Equal(17UL, verified.VBMetaHeader!.Value.RollbackIndex);
        Assert.Equal(7U, verified.VBMetaHeader.Value.RollbackIndexLocation);
        image.Position = (long)verified.Footer!.Value.VbmetaOffset;
        var metadata = new byte[(int)verified.Footer.Value.VbmetaSize];
        await image.ReadExactlyAsync(metadata, token);
        var descriptor = Assert.Single(AvbSignatureVerifier.ReadDescriptorsStrict(metadata));
        Assert.Equal(new byte[] { 1, 2, 3 }, descriptor.Salt);
        var content = await AvbPartitionVerifier.VerifyAsync(image, descriptor, token);
        Assert.True(content.ContentIsValid, content.ErrorMessage);
    }

    [Fact]
    public async Task ResignAvbImageAsync_MissingPrivateKey_PreservesExistingOutput()
    {
        var token = TestContext.Current.CancellationToken;
        var output = Path.Combine(_root, "output.img");
        await File.WriteAllBytesAsync(output, [3, 2, 1], token);

        var result = await FirmwareService.Instance.ResignAvbImageAsync(
            Path.Combine(_root, "missing.img"), Path.Combine(_root, "missing.pem"), output,
            new AvbResignOptions { PartitionName = "system" }, token);

        Assert.False(result.Success);
        Assert.Equal(new byte[] { 3, 2, 1 }, await File.ReadAllBytesAsync(output, token));
    }

    [Fact]
    public async Task ResignAvbImageAsync_Canceled_PropagatesCancellation()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FirmwareService.Instance.ResignAvbImageAsync(
            "unused.img", "unused.pem", "unused-output.img",
            new AvbResignOptions { PartitionName = "boot" }, cancellation.Token));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}