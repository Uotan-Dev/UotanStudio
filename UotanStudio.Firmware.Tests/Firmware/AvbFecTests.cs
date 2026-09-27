using System.Diagnostics;
using UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse.IO;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.Avb.Build;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.Avb.Models;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.Avb.Verification;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class AvbFecTests
{
    public static bool IndependentToolsAvailable =>
        File.Exists(Environment.GetEnvironmentVariable("UOTAN_AVBTOOL"))
        && (File.Exists(Environment.GetEnvironmentVariable("UOTAN_AVB_PYTHON"))
            && File.Exists(Environment.GetEnvironmentVariable("UOTAN_FEC_TOOL"))
            || WslFecAvailable);

    public static bool WslFecAvailable =>
        File.Exists(Environment.GetEnvironmentVariable("UOTAN_FEC_WSL_TOOL"))
        && File.Exists(Environment.GetEnvironmentVariable("UOTAN_FEC_WSL_LOADER"))
        && File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe"));

    [Theory]
    [InlineData(1U, 2)]
    [InlineData(2U, 2)]
    [InlineData(4U, 260)]
    [InlineData(254U, 2)]
    public async Task WriteParityAsync_ManagedTree_VerifiesParityAndDetectsTampering(uint roots, int blocks)
    {
        var token = TestContext.Current.CancellationToken;
        var (bytes, source) = AvbPartitionVerificationTests.TreeFixture(blocks);
        var fecOffset = bytes.Length;
        using var image = new MemoryStream(bytes, writable: false);
        using var fec = new MemoryStream();
        image.Position = 7;
        await AvbFecCodec.WriteParityAsync(image, fec, fecOffset, roots, token);
        Assert.Equal(7, image.Position);
        Assert.Equal(AvbFecCodec.CalculateParitySize(fecOffset, roots), fec.Length);
        var descriptor = WithFec(source, (ulong)fecOffset, (ulong)fec.Length, roots);
        var raw = new byte[checked(fecOffset + (int)fec.Length)];
        bytes.CopyTo(raw, 0);
        fec.ToArray().CopyTo(raw, fecOffset);
        using var partition = new MemoryStream(raw, writable: true);
        partition.Position = 11;
        var valid = await AvbPartitionVerifier.VerifyWithFecAsync(partition, descriptor, token);
        Assert.True(valid.ContentIsValid, valid.ErrorMessage);
        Assert.True(valid.StoredTreeIsValid);
        Assert.True(valid.FecIsValid);
        Assert.Equal(11, partition.Position);

        raw[fecOffset + 17] ^= 1;
        var invalid = await AvbPartitionVerifier.VerifyWithFecAsync(partition, descriptor, token);
        Assert.True(invalid.ContentIsValid);
        Assert.False(invalid.FecIsValid);
        Assert.Contains("FEC", invalid.ErrorMessage);
        Assert.Equal(11, partition.Position);
    }

    [Fact]
    public async Task VerifyWithFecAsync_WrongSize_FailsParityWithoutChangingContentResult()
    {
        var token = TestContext.Current.CancellationToken;
        var (bytes, source) = AvbPartitionVerificationTests.TreeFixture(2);
        var descriptor = WithFec(source, (ulong)bytes.Length, 4096, 2);
        using var image = new MemoryStream([.. bytes, .. new byte[4096]], writable: false);

        var contentOnly = await AvbPartitionVerifier.VerifyAsync(image, descriptor, token);
        var withFec = await AvbPartitionVerifier.VerifyWithFecAsync(image, descriptor, token);

        Assert.True(contentOnly.ContentIsValid);
        Assert.Null(contentOnly.FecIsValid);
        Assert.True(withFec.ContentIsValid);
        Assert.False(withFec.FecIsValid);
    }

    [Fact]
    public async Task WriteParityAsync_CancellationMidRead_RestoresSourcePosition()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var input = new CancelOnReadStream(new byte[8192], cancellation);
        using var output = new MemoryStream();
        input.Position = 3;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => AvbFecCodec.WriteParityAsync(input, output, input.Length, 2, cancellation.Token));

        Assert.Equal(3, input.Position);
        Assert.True(input.CanRead);
    }

    [Theory]
    [InlineData(253, 8192)]
    [InlineData(254, 16384)]
    public void CalculateParitySize_MultipleRounds_MatchesUpstreamGeometry(int blocks, long paritySize)
    {
        Assert.Equal(paritySize, AvbFecCodec.CalculateParitySize((long)blocks * 4096, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => AvbFecCodec.CalculateParitySize(4097, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => AvbFecCodec.CalculateParitySize(4096, 255));
    }

    [Theory(Skip = "Set UOTAN_AVBTOOL with UOTAN_AVB_PYTHON/UOTAN_FEC_TOOL, or UOTAN_FEC_WSL_TOOL/UOTAN_FEC_WSL_LOADER, for independent avbtool/fec interoperability.", SkipUnless = nameof(IndependentToolsAvailable))]
    [InlineData(1U, 2)]
    [InlineData(2U, 2)]
    [InlineData(4U, 260)]
    public async Task VerifyWithFecAsync_AvbtoolFecImage_MatchesIndependentParity(uint roots, int blocks)
    {
        var root = Path.Combine(Path.GetTempPath(), "uotan-avb-fec-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var token = TestContext.Current.CancellationToken;
            var path = Path.Combine(root, "system.img");
            var keyPath = Path.Combine(root, "key.pem");
            using var key = SigningTestKeys.Create();
            await File.WriteAllTextAsync(keyPath, key.ExportRSAPrivateKeyPem(), token);
            var data = Enumerable.Range(0, blocks * 4096).Select(index => (byte)(index * 37)).ToArray();
            await File.WriteAllBytesAsync(path, data, token);
            await RunAvbtoolAsync(root, ["add_hashtree_footer", "--image", path, "--partition_name", "system",
                "--partition_size", "8388608", "--salt", "01020304", "--algorithm", "SHA256_RSA2048",
                "--key", keyPath, "--hash_algorithm", "sha256", "--fec_num_roots",
                roots.ToString(System.Globalization.CultureInfo.InvariantCulture)], token);
            await RunAvbtoolAsync(root, ["verify_image", "--image", path, "--key", keyPath], token);
            var footer = AvbFooter.TryReadFile(path)!.Value;
            AvbDescriptorInfo descriptor;
            byte[] independent;
            await using (var image = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var metadata = new byte[checked((int)footer.VbmetaSize)];
                image.Position = (long)footer.VbmetaOffset;
                await image.ReadExactlyAsync(metadata, token);
                descriptor = Assert.Single(AvbSignatureVerifier.ExtractDescriptors(metadata));
                Assert.Equal(roots, descriptor.FecNumRoots);
                Assert.Equal((ulong)AvbFecCodec.CalculateParitySize((long)descriptor.FecOffset, roots), descriptor.FecSize);
                image.Position = 13;
                var result = await AvbPartitionVerifier.VerifyWithFecAsync(image, descriptor, token);
                Assert.True(result.ContentIsValid, result.ErrorMessage);
                Assert.True(result.FecIsValid);
                Assert.Equal(13, image.Position);

                using var parity = new MemoryStream();
                await AvbFecCodec.WriteParityAsync(image, parity, (long)descriptor.FecOffset, roots, token);
                var generated = parity.ToArray();
                independent = new byte[generated.Length];
                image.Position = (long)descriptor.FecOffset;
                await image.ReadExactlyAsync(independent, token);
                Assert.Equal(independent, generated);
            }
            var paths = new Dictionary<string, string> { ["system"] = path };
            var options = new AvbChainVerifyOptions
            {
                RootPartitionName = "system",
                PartitionPaths = paths
            };
            var validGraph = await AvbChainVerifier.VerifyAsync(options, token);
            Assert.True(validGraph.Success, string.Join("\n", validGraph.Errors));
            Assert.True(Assert.Single(validGraph.Partitions).Content.FecIsValid);

            var sparsePath = path + ".sparse";
            using (var raw = SparseReader.FromRawFile(path, blockSize: 4096))
            {
                await using var sparse = new FileStream(sparsePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                SparseWriter.WriteToStream(raw, sparse, includeCrc: true);
                await sparse.FlushAsync(token);
            }
            paths["system"] = sparsePath;
            var sparseGraph = await AvbChainVerifier.VerifyAsync(options, token);
            Assert.True(sparseGraph.Success, string.Join("\n", sparseGraph.Errors));
            Assert.True(Assert.Single(sparseGraph.Partitions).Content.FecIsValid);
            paths["system"] = path;

            await using (var image = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                image.Position = (long)descriptor.FecOffset + 23;
                await image.WriteAsync(new byte[] { (byte)(independent[23] ^ 1) }, token);
                await image.FlushAsync(token);
            }
            var invalidGraph = await AvbChainVerifier.VerifyAsync(options, token);
            Assert.False(invalidGraph.Success);
            Assert.True(Assert.Single(invalidGraph.Partitions).Content.ContentIsValid);
            Assert.False(Assert.Single(invalidGraph.Partitions).Content.FecIsValid);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact(Skip = "Set UOTAN_FEC_WSL_TOOL and UOTAN_FEC_WSL_LOADER to compare 254-root parity with independent fec.", SkipUnless = nameof(WslFecAvailable))]
    public async Task WriteParityAsync_MaximumRoots_MatchesIndependentFec()
    {
        var root = Path.Combine(Path.GetTempPath(), "uotan-fec-roots-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var token = TestContext.Current.CancellationToken;
            var source = Path.Combine(root, "source.img");
            var encoded = Path.Combine(root, "fec.bin");
            var (bytes, _) = AvbPartitionVerificationTests.TreeFixture(2);
            await File.WriteAllBytesAsync(source, bytes, token);
            var tool = await ToWslPathAsync(Environment.GetEnvironmentVariable("UOTAN_FEC_WSL_TOOL")!, token);
            var loader = await ToWslPathAsync(Environment.GetEnvironmentVariable("UOTAN_FEC_WSL_LOADER")!, token);
            await RunWslAsync([loader, "--library-path", Path.GetDirectoryName(tool)!, tool,
                "--encode", "--roots", "254", await ToWslPathAsync(source, token), await ToWslPathAsync(encoded, token)], token);
            var independent = await File.ReadAllBytesAsync(encoded, token);
            using var image = new MemoryStream(bytes, writable: false);
            using var parity = new MemoryStream();
            await AvbFecCodec.WriteParityAsync(image, parity, bytes.Length, 254, token);
            Assert.Equal(parity.Length + 4096, independent.Length);
            Assert.Equal(independent.AsSpan(0, (int)parity.Length).ToArray(), parity.ToArray());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static AvbDescriptorInfo WithFec(AvbDescriptorInfo original, ulong offset, ulong size, uint roots) => new()
    {
        Tag = original.Tag,
        PartitionName = original.PartitionName,
        HashAlgorithm = original.HashAlgorithm,
        Salt = original.Salt,
        RootDigest = original.RootDigest,
        ImageSize = original.ImageSize,
        TreeOffset = original.TreeOffset,
        TreeSize = original.TreeSize,
        DataBlockSize = original.DataBlockSize,
        HashBlockSize = original.HashBlockSize,
        DmVerityVersion = original.DmVerityVersion,
        FecNumRoots = roots,
        FecOffset = offset,
        FecSize = size
    };

    private static async Task RunAvbtoolAsync(string directory, IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        if (Environment.GetEnvironmentVariable("UOTAN_FEC_WSL_TOOL") is { Length: > 0 })
        {
            await RunWslAvbtoolAsync(directory, arguments, cancellationToken);
            return;
        }
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("UOTAN_AVB_PYTHON")!)
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(Environment.GetEnvironmentVariable("UOTAN_AVBTOOL")!);
        start.Environment["PATH"] = Path.GetDirectoryName(Environment.GetEnvironmentVariable("UOTAN_FEC_TOOL")!)!
            + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start)!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
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
        Assert.True(process.ExitCode == 0, await stdout + Environment.NewLine + await stderr);
    }

    private static async Task RunWslAvbtoolAsync(string directory, IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var tool = await ToWslPathAsync(Environment.GetEnvironmentVariable("UOTAN_FEC_WSL_TOOL")!, cancellationToken);
        var loader = await ToWslPathAsync(Environment.GetEnvironmentVariable("UOTAN_FEC_WSL_LOADER")!, cancellationToken);
        var bin = Path.Combine(directory, "bin");
        Directory.CreateDirectory(bin);
        var script = Path.Combine(bin, "fec");
        await File.WriteAllTextAsync(script,
            "#!/bin/sh\nexec '" + loader + "' --library-path '" + Path.GetDirectoryName(tool) + "' '" + tool + "' \"$@\"\n",
            cancellationToken);
        await RunWslAsync(["chmod", "+x", await ToWslPathAsync(script, cancellationToken)], cancellationToken);
        var command = new List<string>
        {
            "env", "PATH=" + await ToWslPathAsync(bin, cancellationToken) + ":/usr/bin:/bin",
            "/usr/bin/python3", await ToWslPathAsync(Environment.GetEnvironmentVariable("UOTAN_AVBTOOL")!, cancellationToken)
        };
        foreach (var argument in arguments)
        {
            command.Add(Path.IsPathFullyQualified(argument) ? await ToWslPathAsync(argument, cancellationToken) : argument);
        }
        await RunWslAsync(command, cancellationToken);
    }

    private static async Task<string> ToWslPathAsync(string path, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("wsl.exe")
        {
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
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        Assert.True(process.ExitCode == 0, await error);
        return (await output).Trim();
    }

    private static async Task RunWslAsync(IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("wsl.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("--exec");
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start)!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
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
        Assert.True(process.ExitCode == 0, await stdout + Environment.NewLine + await stderr);
    }

    private sealed class CancelOnReadStream(byte[] buffer, CancellationTokenSource source) : MemoryStream(buffer, writable: false)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken = default)
        {
            var count = base.Read(destination.Span);
            source.Cancel();
            return ValueTask.FromResult(count);
        }
    }
}