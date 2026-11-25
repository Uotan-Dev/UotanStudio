using UotanStudio.Firmware.BootSecurity.Avb.Signing;
using UotanStudio.Firmware.BootSecurity.BootImage;
using UotanStudio.Firmware.BootSecurity.BootImage.Models;
using UotanStudio.Firmware.BootSecurity.BootImage.Parser;
using UotanStudio.Firmware.Core.Repacking;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class BootUnsignedAvbRepackTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    [Fact]
    public async Task PackAsync_SignedBootWithoutConsent_Refuses()
    {
        var (source, unpacked) = await PrepareSignedBootAsync();
        var handler = new BootImageFormatHandler();
        var unpack = await handler.UnpackAsync(source, unpacked, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(unpack.Success, unpack.Summary);
        var metadata = RepackMetadata.Read(unpacked);
        Assert.NotNull(metadata);
        Assert.False(metadata.CanPackDirectory);

        var result = await handler.PackAsync(unpacked, Path.Combine(_temp.Path, "out.img"), TestContext.Current.CancellationToken);
        Assert.False(result.Success);
        Assert.Contains("AVB footer", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PackAsync_SignedBootWithConsent_WritesUnsignedImage()
    {
        var (source, unpacked) = await PrepareSignedBootAsync();
        var handler = new BootImageFormatHandler();
        var unpack = await handler.UnpackAsync(source, unpacked, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(unpack.Success, unpack.Summary);
        var output = Path.Combine(_temp.Path, "out-unsigned.img");
        using var scope = RepackMetadata.UsePackingOverrides(
            new Dictionary<string, string> { ["AllowUnsignedAvbRepack"] = "1" });

        var result = await handler.PackAsync(unpacked, output, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Summary);
        Assert.True(File.Exists(output));
        Assert.Null(UotanStudio.Firmware.BootSecurity.Avb.AvbFooter.TryReadFile(output));
        using var parser = new BootImageParser();
        parser.ParseFile(output);
        Assert.Equal(BootImageVersion.V4, parser.Header.HeaderVersion);
    }

    private async Task<(string Source, string Unpacked)> PrepareSignedBootAsync()
    {
        var token = TestContext.Current.CancellationToken;
        using var key = SigningTestKeys.Create();
        var boot = Path.Combine(_temp.Path, "boot.img");
        var packer = new BootImagePacker
        {
            Header = new BootImageHeader { HeaderVersion = BootImageVersion.V4, KernelSize = 1024 },
            Kernel = Enumerable.Range(0, 1024).Select(i => (byte)i).ToArray(),
        };
        packer.PackToFile(boot);
        var signed = Path.Combine(_temp.Path, "boot.signed.img");
        var sign = await AvbSigner.AddHashFooterAsync(boot, "boot", key, signed, token);
        Assert.True(sign.Success, sign.ErrorMessage);
        return (signed, Path.Combine(_temp.Path, "unpacked"));
    }

    public void Dispose() => _temp.Dispose();
}