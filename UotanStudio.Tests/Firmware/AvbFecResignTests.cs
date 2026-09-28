using System.Diagnostics;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.Avb.Build;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.Avb.Models;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.Avb.Signing;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.Avb.Verification;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class AvbFecResignTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uotan-avb-fec-resign-" + Guid.NewGuid().ToString("N"));

    public static bool IndependentToolsAvailable =>
        File.Exists(Environment.GetEnvironmentVariable("UOTAN_AVBTOOL")) &&
        File.Exists(Environment.GetEnvironmentVariable("UOTAN_AVB_PYTHON")) &&
        (File.Exists(Environment.GetEnvironmentVariable("UOTAN_FEC_TOOL")) ||
            File.Exists(Environment.GetEnvironmentVariable("UOTAN_FEC_WSL_TOOL")) &&
            File.Exists(Environment.GetEnvironmentVariable("UOTAN_FEC_WSL_LOADER")) &&
            File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe"))) &&
        Directory.Exists(Environment.GetEnvironmentVariable("UOTAN_AVB_OPENSSL_DIRECTORY"));

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
        var key = (RsaKeyParameters)DotNetUtilities.GetRsaKeyPair(rsa).Private;
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
        var key = (RsaKeyParameters)DotNetUtilities.GetRsaKeyPair(rsa).Private;

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
        var key = (RsaKeyParameters)DotNetUtilities.GetRsaKeyPair(rsa).Private;
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
        var key = (RsaKeyParameters)DotNetUtilities.GetRsaKeyPair(rsa).Private;

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
        var key = (RsaKeyParameters)DotNetUtilities.GetRsaKeyPair(rsa).Private;
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

    [Fact(Skip = "Set UOTAN_AVBTOOL, UOTAN_AVB_PYTHON, UOTAN_AVB_OPENSSL_DIRECTORY and UOTAN_FEC_TOOL or UOTAN_FEC_WSL_TOOL/UOTAN_FEC_WSL_LOADER for independent avbtool/fec checks.", SkipUnless = nameof(IndependentToolsAvailable))]
    public async Task ResignImageAsync_IndependentAvbtoolAndFec_VerifySignedImageAndParity()
    {
        var token = TestContext.Current.CancellationToken;
        var source = Path.Combine(_root, "source.img");
        var output = Path.Combine(_root, "system.img");
        var prefix = Path.Combine(_root, "prefix.img");
        var encoded = Path.Combine(_root, "encoded.fec");
        var keyPath = Path.Combine(_root, "key.pem");
        await File.WriteAllBytesAsync(source, Enumerable.Range(0, 129 * 4096 - 13).Select(i => (byte)(i * 17)).ToArray(), token);
        using var rsa = SigningTestKeys.Create();
        await File.WriteAllTextAsync(keyPath, rsa.ExportRSAPrivateKeyPem(), token);
        var key = (RsaKeyParameters)DotNetUtilities.GetRsaKeyPair(rsa).Private;
        var signed = await AvbResigner.ResignImageAsync(source, output, key, new AvbResignOptions
        {
            Mode = AvbResignMode.Hashtree,
            PartitionName = "system",
            Salt = [1, 2, 3, 4],
            FecNumRoots = 2
        }, token);
        Assert.True(signed.Success, signed.ErrorMessage);
        await RunToolAsync(Environment.GetEnvironmentVariable("UOTAN_AVB_PYTHON")!,
            [Environment.GetEnvironmentVariable("UOTAN_AVBTOOL")!, "verify_image", "--image", output, "--key", keyPath], token);

        var footer = AvbFooter.TryReadFile(output)!.Value;
        await using var image = new FileStream(output, FileMode.Open, FileAccess.Read, FileShare.Read);
        image.Position = (long)footer.VbmetaOffset;
        var vbmeta = new byte[(int)footer.VbmetaSize];
        await image.ReadExactlyAsync(vbmeta, token);
        var descriptor = Assert.Single(AvbSignatureVerifier.ReadDescriptorsStrict(vbmeta));
        var info = await RunToolAsync(Environment.GetEnvironmentVariable("UOTAN_AVB_PYTHON")!,
            [Environment.GetEnvironmentVariable("UOTAN_AVBTOOL")!, "info_image", "--image", output], token);
        Assert.Matches(@"FEC num roots:\s+2\b", info);
        Assert.Matches($@"FEC offset:\s+{descriptor.FecOffset}\b", info);
        Assert.Matches($@"FEC size:\s+{descriptor.FecSize}\s+bytes", info);
        image.Position = 0;
        await using (var target = new FileStream(prefix, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            var remaining = (long)descriptor.FecOffset;
            var buffer = new byte[65536];
            while (remaining > 0)
            {
                var count = (int)Math.Min(buffer.Length, remaining);
                await image.ReadExactlyAsync(buffer.AsMemory(0, count), token);
                await target.WriteAsync(buffer.AsMemory(0, count), token);
                remaining -= count;
            }
        }
        if (File.Exists(Environment.GetEnvironmentVariable("UOTAN_FEC_TOOL")))
        {
            await RunToolAsync(Environment.GetEnvironmentVariable("UOTAN_FEC_TOOL")!,
                ["--encode", "--roots", "2", prefix, encoded], token);
        }
        else
        {
            var tool = await ToWslPathAsync(Environment.GetEnvironmentVariable("UOTAN_FEC_WSL_TOOL")!, token);
            var loader = await ToWslPathAsync(Environment.GetEnvironmentVariable("UOTAN_FEC_WSL_LOADER")!, token);
            await RunToolAsync("wsl.exe", ["--exec", loader, "--library-path", Path.GetDirectoryName(tool)!,
                tool, "--encode", "--roots", "2", await ToWslPathAsync(prefix, token),
                await ToWslPathAsync(encoded, token)], token);
        }
        var independent = await File.ReadAllBytesAsync(encoded, token);
        var parity = new byte[(int)descriptor.FecSize];
        image.Position = (long)descriptor.FecOffset;
        await image.ReadExactlyAsync(parity, token);
        Assert.Equal(parity.Length + 4096, independent.Length);
        Assert.Equal(independent.AsSpan(0, parity.Length).ToArray(), parity);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private async Task<string> ToWslPathAsync(string path, CancellationToken token)
    {
        var start = new ProcessStartInfo("wsl.exe")
        {
            WorkingDirectory = _root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("--exec");
        start.ArgumentList.Add("wslpath");
        start.ArgumentList.Add("-a");
        start.ArgumentList.Add(path);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(token);
        var stderr = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        Assert.True(process.ExitCode == 0, await stderr);
        return (await stdout).Trim();
    }

    private async Task<string> RunToolAsync(string fileName, IEnumerable<string> arguments, CancellationToken token)
    {
        var start = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = _root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.Environment["PATH"] = Environment.GetEnvironmentVariable("UOTAN_AVB_OPENSSL_DIRECTORY") +
            Path.PathSeparator + Path.GetDirectoryName(Environment.GetEnvironmentVariable("UOTAN_FEC_TOOL")) +
            Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start)!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var stdout = process.StandardOutput.ReadToEndAsync(token);
        var stderr = process.StandardError.ReadToEndAsync(token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
        var text = await stdout + Environment.NewLine + await stderr;
        Assert.True(process.ExitCode == 0, text);
        return text;
    }
}