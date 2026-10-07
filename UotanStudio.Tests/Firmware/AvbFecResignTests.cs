using System.Diagnostics;
using UotanStudio.Firmware.BootSecurity.Avb;
using UotanStudio.Firmware.BootSecurity.Avb.Signing;
using UotanStudio.Firmware.BootSecurity.Avb.Verification;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class AvbFecResignTests
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uotan-avb-fec-resign-" + Guid.NewGuid().ToString("N"));

    public AvbFecResignTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData(1U, 4096, 4096, 8193)]
    [InlineData(2U, 512, 4096, 129 * 512 - 13)]
    [InlineData(4U, 4096, 512, 129 * 4096 - 13)]
    [InlineData(254U, 4096, 4096, 4097)]
    public async Task ResignImageAsync_FecEnabled_WritesDisjointSignedRegionsAndVerifiableParity(
        uint roots, int dataBlockSize, int hashBlockSize, int length)
    {
        var token = TestContext.Current.CancellationToken;
        var source = Path.Combine(_root, "source.img");
        var output = Path.Combine(_root, "output.img");
        var content = Enumerable.Range(0, length).Select(i => (byte)(i * 19)).ToArray();
        await File.WriteAllBytesAsync(source, content, token);
        using var rsa = SigningTestKeys.Create();
        var key = rsa;
        var options = new AvbResignOptions
        {
            Mode = AvbResignMode.Hashtree,
            PartitionName = "system",
            Salt = [1, 2, 3, 4],
            DataBlockSize = dataBlockSize,
            HashBlockSize = hashBlockSize,
            FecNumRoots = roots
        };

        var signed = await AvbResigner.ResignImageAsync(source, output, key, options, token);

        Assert.True(signed.Success, signed.ErrorMessage);
        var bytes = await File.ReadAllBytesAsync(output, token);
        Assert.Equal(content, bytes[..content.Length]);
        var footer = AvbFooter.TryLoad(bytes)!.Value;
        Assert.Equal((ulong)content.Length, footer.OriginalImageSize);
        var vbmeta = bytes.AsSpan((int)footer.VbmetaOffset, (int)footer.VbmetaSize).ToArray();
        Assert.True(AvbSignatureVerifier.Verify(vbmeta).SignatureIsValid);
        var descriptor = Assert.Single(AvbSignatureVerifier.ReadDescriptorsStrict(vbmeta));
        Assert.Equal(roots, descriptor.FecNumRoots);
        Assert.Equal((ulong)AvbFecCodec.CalculateParitySize((long)descriptor.FecOffset, roots), descriptor.FecSize);
        Assert.Equal(0UL, descriptor.FecOffset % 4096);
        Assert.True(descriptor.ImageSize <= descriptor.TreeOffset);
        Assert.True(descriptor.TreeOffset + descriptor.TreeSize <= descriptor.FecOffset);
        Assert.True(descriptor.FecOffset + descriptor.FecSize <= footer.VbmetaOffset);
        Assert.True(footer.VbmetaOffset + footer.VbmetaSize <= (ulong)bytes.Length - 4096);
        using (var image = new MemoryStream(bytes, writable: false))
        {
            var verified = await AvbPartitionVerifier.VerifyWithFecAsync(image, descriptor, token);
            Assert.True(verified.ContentIsValid, verified.ErrorMessage);
            Assert.True(verified.FecIsValid);
        }

        var repeat = Path.Combine(_root, "repeat.img");
        var resigned = await AvbResigner.ResignImageAsync(output, repeat, key, options, token);
        Assert.True(resigned.Success, resigned.ErrorMessage);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(repeat, token));
    }

    [Fact]
    public async Task ResignImageAsync_FecDisabledByDefault_PreservesLegacyLayout()
    {
        var token = TestContext.Current.CancellationToken;
        var source = Path.Combine(_root, "source.img");
        var output = Path.Combine(_root, "output.img");
        await File.WriteAllBytesAsync(source, new byte[8192], token);
        using var rsa = SigningTestKeys.Create();
        var key = rsa;

        var result = await AvbResigner.ResignImageAsync(source, output, key,
            new AvbResignOptions { Mode = AvbResignMode.Hashtree, PartitionName = "system", Salt = [] }, token);

        Assert.True(result.Success, result.ErrorMessage);
        var footer = AvbFooter.TryReadFile(output)!.Value;
        await using var image = new FileStream(output, FileMode.Open, FileAccess.Read, FileShare.Read);
        image.Position = (long)footer.VbmetaOffset;
        var vbmeta = new byte[(int)footer.VbmetaSize];
        await image.ReadExactlyAsync(vbmeta, token);
        var descriptor = Assert.Single(AvbSignatureVerifier.ReadDescriptorsStrict(vbmeta));
        Assert.Equal(0U, descriptor.FecNumRoots);
        Assert.Equal(0UL, descriptor.FecOffset);
        Assert.Equal(0UL, descriptor.FecSize);
    }

    [Fact]
    public async Task ResignImageAsync_ExactFecCapacity_SucceedsAndOneBlockLessPreservesOutput()
    {
        var token = TestContext.Current.CancellationToken;
        var source = Path.Combine(_root, "source.img");
        var output = Path.Combine(_root, "output.img");
        await File.WriteAllBytesAsync(source, new byte[4097], token);
        using var rsa = SigningTestKeys.Create();
        var key = rsa;
        var options = new AvbResignOptions
        {
            Mode = AvbResignMode.Hashtree,
            PartitionName = "system",
            Salt = [],
            FecNumRoots = 2
        };
        var first = await AvbResigner.ResignImageAsync(source, output, key, options, token);
        Assert.True(first.Success, first.ErrorMessage);
        var expected = await File.ReadAllBytesAsync(output, token);
        var exact = await AvbResigner.ResignImageAsync(source, output, key, new AvbResignOptions
        {
            Mode = options.Mode,
            PartitionName = options.PartitionName,
            Salt = options.Salt,
            FecNumRoots = options.FecNumRoots,
            PartitionSize = first.OutputSize
        }, token);
        Assert.True(exact.Success, exact.ErrorMessage);
        Assert.Equal(expected, await File.ReadAllBytesAsync(output, token));

        var shortCapacity = await AvbResigner.ResignImageAsync(source, output, key, new AvbResignOptions
        {
            Mode = options.Mode,
            PartitionName = options.PartitionName,
            Salt = options.Salt,
            FecNumRoots = options.FecNumRoots,
            PartitionSize = first.OutputSize - 4096
        }, token);
        Assert.False(shortCapacity.Success);
        Assert.Equal(expected, await File.ReadAllBytesAsync(output, token));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp-*"));
    }

    [Theory]
    [InlineData(AvbResignMode.Hash, 2U)]
    [InlineData(AvbResignMode.Hashtree, 255U)]
    public async Task ResignImageAsync_InvalidFecOptions_PreservesExistingOutput(AvbResignMode mode, uint roots)
    {
        var token = TestContext.Current.CancellationToken;
        var source = Path.Combine(_root, "source.img");
        var output = Path.Combine(_root, "output.img");
        await File.WriteAllBytesAsync(source, new byte[8192], token);
        await File.WriteAllBytesAsync(output, [9, 8, 7], token);
        using var rsa = SigningTestKeys.Create();
        var key = rsa;

        var result = await AvbResigner.ResignImageAsync(source, output, key,
            new AvbResignOptions { Mode = mode, PartitionName = "system", FecNumRoots = roots }, token);

        Assert.False(result.Success);
        Assert.Equal(new byte[] { 9, 8, 7 }, await File.ReadAllBytesAsync(output, token));
    }

    [Fact]
    public async Task ResignImageAsync_CanceledDuringFecWork_PreservesExistingOutput()
    {
        var token = TestContext.Current.CancellationToken;
        var source = Path.Combine(_root, "source.img");
        var output = Path.Combine(_root, "output.img");
        await File.WriteAllBytesAsync(source, new byte[8 * 1024 * 1024], token);
        await File.WriteAllBytesAsync(output, [9, 8, 7], token);
        using var rsa = SigningTestKeys.Create();
        var key = rsa;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var operation = AvbResigner.ResignImageAsync(source, output, key, new AvbResignOptions
        {
            Mode = AvbResignMode.Hashtree,
            PartitionName = "system",
            Salt = [],
            FecNumRoots = 254
        }, cancellation.Token);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            while (!operation.IsCompleted && Directory.GetFiles(_root, "*.tmp-*").Length == 0)
            {
                await Task.Delay(5, deadline.Token);
            }
            Assert.False(operation.IsCompleted);
        }
        finally
        {
            cancellation.Cancel();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.Equal(new byte[] { 9, 8, 7 }, await File.ReadAllBytesAsync(output, token));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp-*"));
    }
}