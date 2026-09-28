using System.Diagnostics;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;
using UotanStudio.Firmware.BootSecurity.Avb;
using UotanStudio.Firmware.BootSecurity.Avb.Signing;
using UotanStudio.Firmware.BootSecurity.Avb.Verification;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class AvbPartitionInteropTests
{
    public static bool ToolsAvailable => File.Exists(Environment.GetEnvironmentVariable("UOTAN_AVBTOOL"))
        && File.Exists(Environment.GetEnvironmentVariable("UOTAN_AVB_PYTHON"));
    public static bool SigningToolsAvailable => ToolsAvailable && Directory.Exists(Environment.GetEnvironmentVariable("UOTAN_AVB_OPENSSL_DIRECTORY"));

    [Theory(Skip = "Set UOTAN_AVBTOOL, UOTAN_AVB_PYTHON and UOTAN_AVB_OPENSSL_DIRECTORY to run independent re-sign interoperability.", SkipUnless = nameof(SigningToolsAvailable))]
    [InlineData(AvbResignMode.Hash, 4096)]
    [InlineData(AvbResignMode.Hashtree, 4096)]
    [InlineData(AvbResignMode.Hashtree, 512)]
    public async Task ResignImageAsync_AvbtoolSource_VerifiesManagedReplacementAndExplicitParameters(AvbResignMode mode, int blockSize)
    {
        var root = Path.Combine(Path.GetTempPath(), "uotan-avb-resign-interop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var token = TestContext.Current.CancellationToken;
            using var rsa = SigningTestKeys.Create();
            var keyPath = Path.Combine(root, "key.pem");
            var source = Path.Combine(root, "source.img");
            var output = Path.Combine(root, "system.img");
            var original = Enumerable.Range(0, blockSize * 129 - 13).Select(i => (byte)(i * 17)).ToArray();
            await File.WriteAllBytesAsync(source, original, token);
            await File.WriteAllTextAsync(keyPath, rsa.ExportRSAPrivateKeyPem(), token);
            await RunToolAsync(root, ["add_hash_footer", "--image", source, "--partition_name", "system",
                "--partition_size", "1048576", "--algorithm", "SHA256_RSA2048", "--key", keyPath, "--salt", "aabbccdd"], true, token);
            var key = (RsaKeyParameters)DotNetUtilities.GetRsaKeyPair(rsa).Private;
            var result = await AvbResigner.ResignImageAsync(source, output, key, new AvbResignOptions
            {
                Mode = mode,
                PartitionName = "system",
                Salt = [1, 2, 3, 4],
                RollbackIndex = 23,
                RollbackIndexLocation = 7,
                DoNotUseAb = true,
                DataBlockSize = blockSize,
                HashBlockSize = blockSize,
                PartitionSize = 1048576
            }, token);
            Assert.True(result.Success, result.ErrorMessage);
            await RunToolAsync(root, ["verify_image", "--image", output, "--key", keyPath], true, token);
            await RunToolAsync(root, ["erase_footer", "--image", output], true, token);
            Assert.Equal(original, await File.ReadAllBytesAsync(output, token));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact(Skip = "Set UOTAN_AVBTOOL, UOTAN_AVB_PYTHON and UOTAN_AVB_OPENSSL_DIRECTORY to run independent signed avbtool interoperability.", SkipUnless = nameof(SigningToolsAvailable))]
    public async Task VerifyAsync_AvbtoolSignedHash_SeparatesSignatureFromPartitionContent()
    {
        await VerifyInteropAsync("hash", "sha256", 4096, 17, signed: true);
    }

    [Theory(Skip = "Set UOTAN_AVBTOOL, UOTAN_AVB_PYTHON and UOTAN_AVB_OPENSSL_DIRECTORY to run independent signed avbtool interoperability.", SkipUnless = nameof(SigningToolsAvailable))]
    [InlineData("hash")]
    [InlineData("hashtree")]
    public async Task VerifyAsync_AvbtoolChainAndManagedRoot_AgreeWithSeparatePinnedSignerChecks(string kind)
    {
        var root = Path.Combine(Path.GetTempPath(), "uotan-avb-chain-interop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var token = TestContext.Current.CancellationToken;
            using var rootKey = SigningTestKeys.Create();
            using var childKey = SigningTestKeys.Create(alternate: true);
            var rootPem = Path.Combine(root, "root.pem");
            var childPem = Path.Combine(root, "child.pem");
            var rootPublic = Path.Combine(root, "root.avbpubkey");
            var childPublic = Path.Combine(root, "child.avbpubkey");
            var rootImage = Path.Combine(root, "vbmeta.img");
            var managedImage = Path.Combine(root, "managed.img");
            var system = Path.Combine(root, "system.img");
            await File.WriteAllTextAsync(rootPem, rootKey.ExportRSAPrivateKeyPem(), token);
            await File.WriteAllTextAsync(childPem, childKey.ExportRSAPrivateKeyPem(), token);
            await File.WriteAllBytesAsync(system, new byte[16384], token);
            await RunToolAsync(root, ["extract_public_key", "--key", rootPem, "--output", rootPublic], true, token);
            await RunToolAsync(root, ["extract_public_key", "--key", childPem, "--output", childPublic], true, token);
            var footerArguments = new List<string> { "add_" + kind + "_footer", "--image", system, "--partition_name", "system",
                "--partition_size", "1048576", "--algorithm", "SHA256_RSA2048", "--key", childPem,
                "--salt", "01020304", "--rollback_index", "9" };
            if (kind == "hashtree")
            {
                footerArguments.Add("--do_not_generate_fec");
            }
            await RunToolAsync(root, footerArguments, true, token);
            var chainArgument = "system:1:child.avbpubkey";
            await RunToolAsync(root, ["make_vbmeta_image", "--output", rootImage, "--algorithm", "SHA256_RSA2048",
                "--key", rootPem, "--chain_partition", chainArgument, "--rollback_index", "10"], true, token);
            await RunToolAsync(root, ["verify_image", "--image", rootImage, "--key", rootPem, "--expected_chain_partition", chainArgument], true, token);
            await RunToolAsync(root, ["verify_image", "--image", system, "--key", childPem], true, token);
            var rootBlob = await File.ReadAllBytesAsync(rootPublic, token);
            var childBlob = await File.ReadAllBytesAsync(childPublic, token);
            Assert.Equal(AvbRsaPublicKeyCodec.Export(rootKey), rootBlob);
            var paths = new Dictionary<string, string> { ["vbmeta"] = rootImage, ["system"] = system };
            var options = new AvbChainVerifyOptions
            {
                RootPartitionName = "vbmeta",
                PartitionPaths = paths,
                TrustedRootPublicKey = rootBlob,
                MinimumRollbackIndexes = new Dictionary<uint, ulong> { [0] = 10, [1] = 9 }
            };
            var result = await AvbChainVerifier.VerifyAsync(options, token);
            Assert.True(result.Success, string.Join("\n", result.Errors));
            Assert.True(result.RootKeyIsTrusted);
            Assert.True(result.RollbackIndexesAreValid);
            Assert.True(Assert.Single(result.Partitions).Content.ContentIsValid);

            var privateKey = (RsaPrivateCrtKeyParameters)DotNetUtilities.GetRsaKeyPair(rootKey).Private;
            var signed = await AvbSigner.SignVbmetaAsync<object>(rootImage, privateKey, managedImage,
                [new AvbChainPartitionDescriptor { PartitionName = "system", RollbackIndexLocation = 1, PublicKey = childBlob }], token);
            Assert.True(signed.Success, signed.ErrorMessage);
            await RunToolAsync(root, ["verify_image", "--image", managedImage, "--key", rootPem, "--expected_chain_partition", chainArgument], true, token);
            paths["vbmeta"] = managedImage;
            var managed = await AvbChainVerifier.VerifyAsync(options, token);
            Assert.True(managed.Success, string.Join("\n", managed.Errors));
            var header = managed.Images[0].Header!.Value;
            Assert.Equal(0UL, header.AuthenticationDataBlockSize % 64);
            Assert.Equal(0UL, header.AuxiliaryDataBlockSize % 64);
            Assert.Equal(256UL, header.SignatureSize);

            await using (var file = new FileStream(system, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                await file.WriteAsync(new byte[] { 1 }, token);
            }
            var tampered = await AvbChainVerifier.VerifyAsync(options, token);
            Assert.False(tampered.Success);
            Assert.All(tampered.Images, image => Assert.True(image.SignatureIsValid));
            Assert.False(Assert.Single(tampered.Partitions).Content.ContentIsValid);
            await RunToolAsync(root, ["verify_image", "--image", system, "--key", childPem], false, token);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory(Skip = "Set UOTAN_AVBTOOL and UOTAN_AVB_PYTHON to run independent avbtool interoperability.", SkipUnless = nameof(ToolsAvailable))]
    [InlineData("sha1", 4096, 1)]
    [InlineData("sha1", 512, 257)]
    [InlineData("sha256", 512, 257)]
    [InlineData("sha256", 4096, 129)]
    [InlineData("sha512", 4096, 129)]
    [InlineData("blake2b-256", 4096, 129)]
    public async Task VerifyAsync_AvbtoolHashtree_AgreesOnOriginalAndTamperedContent(string algorithm, int blockSize, int blocks)
    {
        await VerifyInteropAsync("hashtree", algorithm, blockSize, blocks);
    }

    [Theory(Skip = "Set UOTAN_AVBTOOL and UOTAN_AVB_PYTHON to run independent avbtool interoperability.", SkipUnless = nameof(ToolsAvailable))]
    [InlineData("sha256")]
    [InlineData("sha512")]
    public async Task VerifyAsync_AvbtoolHash_AgreesOnOriginalAndTamperedContent(string algorithm)
    {
        await VerifyInteropAsync("hash", algorithm, 4096, 17);
    }

    private static async Task VerifyInteropAsync(string kind, string algorithm, int blockSize, int blocks, bool signed = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "uotan-avb-interop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var token = TestContext.Current.CancellationToken;
            var path = Path.Combine(root, "system.img");
            var data = Enumerable.Range(0, blockSize * blocks - 13).Select(i => (byte)(i * 29)).ToArray();
            await File.WriteAllBytesAsync(path, data, token);
            var arguments = new List<string>
            {
                "add_" + kind + "_footer", "--image", path, "--partition_name", "system",
                "--partition_size", "4194304", "--salt", "001122334455", "--hash_algorithm", algorithm
            };
            if (kind == "hashtree")
            {
                arguments.AddRange(["--block_size", blockSize.ToString(System.Globalization.CultureInfo.InvariantCulture), "--do_not_generate_fec"]);
            }
            if (signed)
            {
                using var rsa = SigningTestKeys.Create(2048);
                var keyPath = Path.Combine(root, "key.pem");
                await File.WriteAllTextAsync(keyPath, rsa.ExportRSAPrivateKeyPem(), token);
                arguments.AddRange(["--algorithm", "SHA256_RSA2048", "--key", keyPath]);
            }
            await RunToolAsync(root, arguments, true, token);
            await RunToolAsync(root, ["verify_image", "--image", path], true, token);
            var footer = AvbFooter.TryReadFile(path)!.Value;
            var metadata = new byte[checked((int)footer.VbmetaSize)];
            await using var image = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            image.Position = (long)footer.VbmetaOffset;
            await image.ReadExactlyAsync(metadata, token);
            var descriptor = Assert.Single(AvbSignatureVerifier.ExtractDescriptors(metadata));
            Assert.Equal(algorithm, descriptor.HashAlgorithm);
            if (signed)
            {
                var signature = AvbSignatureVerifier.Verify(metadata);
                Assert.True(signature.HashIsValid, signature.ErrorMessage);
                Assert.True(signature.SignatureIsValid, signature.ErrorMessage);
            }
            var result = await AvbPartitionVerifier.VerifyAsync(image, descriptor, token);
            Assert.True(result.ContentIsValid, result.ErrorMessage);
            Assert.Equal(kind == "hashtree" ? true : (bool?)null, result.StoredTreeIsValid);
            Assert.Null(result.FecIsValid);

            image.Position = 7;
            await image.WriteAsync(new byte[] { (byte)(data[7] ^ 1) }, token);
            await image.FlushAsync(token);
            var tampered = await AvbPartitionVerifier.VerifyAsync(image, descriptor, token);
            Assert.False(tampered.ContentIsValid);
            if (signed)
            {
                image.Position = (long)footer.VbmetaOffset;
                await image.ReadExactlyAsync(metadata, token);
                Assert.True(AvbSignatureVerifier.Verify(metadata).SignatureIsValid);
            }
            await image.DisposeAsync();
            await RunToolAsync(root, ["verify_image", "--image", path], false, token);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task RunToolAsync(string directory, IEnumerable<string> arguments, bool success, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("UOTAN_AVB_PYTHON")!)
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(Environment.GetEnvironmentVariable("UOTAN_AVBTOOL")!);
        if (Environment.GetEnvironmentVariable("UOTAN_AVB_OPENSSL_DIRECTORY") is { Length: > 0 } openssl)
        {
            start.Environment["PATH"] = openssl + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        }
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start)!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
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
        var text = await output + Environment.NewLine + await error;
        Assert.True(success ? process.ExitCode == 0 : process.ExitCode != 0, text);
        if (!success)
        {
            Assert.Contains("does not match", text);
        }
    }
}