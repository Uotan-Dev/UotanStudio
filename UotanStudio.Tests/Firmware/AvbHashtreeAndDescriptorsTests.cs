using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.Avb;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// AVB hashtree builder and mixed descriptor tests: hand-computed dm-verity root digests, descriptor
/// serialize → parse round-trips through <see cref="AvbSignatureVerifier.ExtractDescriptors"/>,
/// <see cref="AvbSigner.AddHashtreeFooterAsync"/> end-to-end, and a conditional WSL avbtool
/// cross-validation (Assert.Skip when unavailable).
/// <para>AVB 哈希树构建器与混合描述符测试：手工计算的 dm-verity 根摘要、经
/// <see cref="AvbSignatureVerifier.ExtractDescriptors"/> 的描述符序列化→解析回环、
/// <see cref="AvbSigner.AddHashtreeFooterAsync"/> 端到端，以及 WSL avbtool 条件交叉验证
/// （不可用时 Assert.Skip）。</para>
/// </summary>
public class AvbHashtreeAndDescriptorsTests
{
    [Fact]
    public void Build_SingleBlock_RootIsBlockDigest()
    {
        var salt = new byte[32];
        RandomNumberGenerator.Fill(salt);
        var data = new byte[100];
        new Random(7).NextBytes(data);

        var result = AvbHashtreeBuilder.Build(data, salt);

        // 单个数据块：树为空，根摘要即该块（补零到 4096）的摘要。
        // Single data block: the tree is empty and the root digest is the block digest itself.
        var block = new byte[4096];
        data.CopyTo(block, 0);
        var expected = SHA256.HashData([.. salt, .. block]);
        Assert.Equal(expected, result.RootDigest);
        Assert.Empty(result.TreeBytes);
        Assert.Equal(0, result.TreeSize);
        Assert.Equal(4096, result.ImageSize);
    }

    [Fact]
    public void Build_TwoBlocks_RootMatchesHandComputedTree()
    {
        var salt = new byte[32];
        RandomNumberGenerator.Fill(salt);
        var data = new byte[8192];
        new Random(11).NextBytes(data);

        var result = AvbHashtreeBuilder.Build(data, salt);

        // Level 0：两个数据块摘要（64 字节）零填充到 4096；root = SHA256(salt || level0 块)。
        // Level 0: two block digests (64 bytes) zero-padded to 4096; root = SHA256(salt || level0 block).
        var d0 = SHA256.HashData([.. salt, .. data.AsSpan(0, 4096).ToArray()]);
        var d1 = SHA256.HashData([.. salt, .. data.AsSpan(4096, 4096).ToArray()]);
        var level0 = new byte[4096];
        d0.CopyTo(level0, 0);
        d1.CopyTo(level0, 32);
        var expectedRoot = SHA256.HashData([.. salt, .. level0]);
        Assert.Equal(expectedRoot, result.RootDigest);
        Assert.Equal(4096, result.TreeSize);
        Assert.Equal(4096, result.TreeBytes.Length);
    }

    [Fact]
    public void Build_ByteArrayStreamAndPathOverloads_Agree()
    {
        var salt = new byte[32];
        RandomNumberGenerator.Fill(salt);
        var data = new byte[200 * 1024];
        new Random(42).NextBytes(data);

        var fromBytes = AvbHashtreeBuilder.Build(data, salt);
        using (var stream = new MemoryStream(data, writable: false))
        {
            var fromStream = AvbHashtreeBuilder.Build(stream, salt);
            Assert.Equal(fromBytes.RootDigest, fromStream.RootDigest);
            Assert.Equal(fromBytes.TreeBytes, fromStream.TreeBytes);
            Assert.Equal(fromBytes.TreeSize, fromStream.TreeSize);
            Assert.Equal(fromBytes.ImageSize, fromStream.ImageSize);
        }

        using var temp = new TempDirectory();
        var path = temp.WriteBytes("image.img", data);
        var fromPath = AvbHashtreeBuilder.Build(path, salt);
        Assert.Equal(fromBytes.RootDigest, fromPath.RootDigest);
        Assert.Equal(fromBytes.TreeBytes, fromPath.TreeBytes);
        Assert.Equal(fromBytes.TreeSize, fromPath.TreeSize);
        Assert.Equal(fromBytes.ImageSize, fromPath.ImageSize);
    }

    [Fact]
    public async Task MixedDescriptors_SerializeAndExtract_RoundTrip()
    {
        using var rsa = RSA.Create(2048);
        var privateKey = (RsaKeyParameters)DotNetUtilities.GetRsaKeyPair(rsa).Private;
        using var temp = new TempDirectory();
        var vbmetaPath = temp.WriteBytes("vbmeta.img", BuildMinimalVbmeta());
        var outputPath = Path.Combine(temp.Path, "out-vbmeta.img");

        var salt = new byte[32];
        RandomNumberGenerator.Fill(salt);
        var digest = new byte[32];
        RandomNumberGenerator.Fill(digest);
        var rootDigest = new byte[32];
        RandomNumberGenerator.Fill(rootDigest);
        var publicKey = new byte[64];
        RandomNumberGenerator.Fill(publicKey);

        var descriptors = new object[]
        {
            new AvbHashDescriptor { PartitionName = "system", ImageSize = 12345, HashAlgorithm = "sha256", Salt = salt, Digest = digest },
            new AvbHashtreeDescriptor { PartitionName = "vendor", ImageSize = 4096, TreeOffset = 4096, TreeSize = 4096, DataBlockSize = 4096, HashBlockSize = 4096, Salt = salt, RootDigest = rootDigest },
            new AvbKernelCmdlineDescriptor { Flags = 0, KernelCmdline = "androidboot.veritymode=enforcing" },
            new AvbChainPartitionDescriptor { RollbackIndexLocation = 3, PartitionName = "boot", PublicKey = publicKey },
            new AvbPropertyDescriptor { Key = "com.android.build", Value = "2026"u8.ToArray() },
        };

        var result = await AvbSigner.SignVbmetaAsync<object>(
            vbmetaPath, privateKey, outputPath, descriptors, TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.ErrorMessage ?? "SignVbmetaAsync failed");

        var vbmeta = await File.ReadAllBytesAsync(outputPath, TestContext.Current.CancellationToken);
        var extracted = AvbSignatureVerifier.ExtractDescriptors(vbmeta);

        Assert.Equal(5, extracted.Count);

        var hash = extracted[0];
        Assert.Equal(2ul, hash.Tag);
        Assert.Equal("system", hash.PartitionName);
        Assert.Equal("sha256", hash.HashAlgorithm);
        Assert.Equal(12345ul, hash.ImageSize);
        Assert.Equal(salt, hash.Salt);
        Assert.Equal(digest, hash.Digest);

        var hashtree = extracted[1];
        Assert.Equal(1ul, hashtree.Tag);
        Assert.Equal("vendor", hashtree.PartitionName);
        Assert.Equal("sha256", hashtree.HashAlgorithm);
        Assert.Equal(4096ul, hashtree.TreeOffset);
        Assert.Equal(4096ul, hashtree.TreeSize);
        Assert.Equal(1u, hashtree.DmVerityVersion);
        Assert.Equal(4096u, hashtree.DataBlockSize);
        Assert.Equal(4096u, hashtree.HashBlockSize);
        Assert.Equal(salt, hashtree.Salt);
        Assert.Equal(rootDigest, hashtree.RootDigest);

        var cmdline = extracted[2];
        Assert.Equal(3ul, cmdline.Tag);
        Assert.Equal("androidboot.veritymode=enforcing", cmdline.KernelCmdline);

        var chain = extracted[3];
        Assert.Equal(4ul, chain.Tag);
        Assert.Equal("boot", chain.PartitionName);
        Assert.Equal(3u, chain.RollbackIndexLocation);
        Assert.Equal(publicKey, chain.PublicKey);

        var property = extracted[4];
        Assert.Equal(0ul, property.Tag);
        Assert.Equal("com.android.build", property.Key);
        Assert.Equal("2026"u8.ToArray(), property.Value);
    }

    [Fact]
    public async Task AddHashtreeFooterAsync_VerifyPasses_AndExtractsHashtreeDescriptor()
    {
        using var rsa = RSA.Create(2048);
        var privateKey = (RsaKeyParameters)DotNetUtilities.GetRsaKeyPair(rsa).Private;
        using var temp = new TempDirectory();
        var imagePath = temp.WriteBytes("source.img", BuildPatternedImage(512 * 1024));
        var outputPath = Path.Combine(temp.Path, "out.img");

        var salt = new byte[32];
        RandomNumberGenerator.Fill(salt);

        var result = await AvbSigner.AddHashtreeFooterAsync(
            imagePath, "test", privateKey, outputPath, salt, TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.ErrorMessage ?? "AddHashtreeFooterAsync failed");

        var signed = await File.ReadAllBytesAsync(outputPath, TestContext.Current.CancellationToken);
        var verify = AvbVerifier.Verify(signed);
        Assert.True(verify.IsSigned, verify.ErrorMessage ?? "image not recognized as signed");
        Assert.True(verify.IsValid, verify.ErrorMessage ?? "verification failed");

        // ExtractDescriptors 需要 vbmeta 段本身（AVB0 头在段首），先经 footer 定位。
        // ExtractDescriptors expects the vbmeta blob itself (AVB0 magic at its start);
        // locate it via the footer first.
        var footer = AvbFooter.TryLoad(signed);
        Assert.NotNull(footer);
        var vbmeta = signed.AsSpan(
            checked((int)footer.Value.VbmetaOffset), checked((int)footer.Value.VbmetaSize)).ToArray();
        var hashtree = AvbSignatureVerifier.ExtractDescriptors(vbmeta).FirstOrDefault(d => d.Tag == 1);
        Assert.NotNull(hashtree);
        Assert.Equal("test", hashtree.PartitionName);

        // tree_offset == 对齐后镜像大小；树大小与根摘要与独立构建一致。
        // tree_offset equals the aligned image size; tree size and root digest match an independent build.
        var expectedTree = AvbHashtreeBuilder.Build(imagePath, salt, 4096, 4096);
        Assert.Equal((ulong)expectedTree.ImageSize, hashtree.TreeOffset);
        Assert.Equal((ulong)expectedTree.TreeSize, hashtree.TreeSize);
        Assert.Equal(expectedTree.RootDigest, hashtree.RootDigest);
    }

    [Fact]
    public async Task ManagedAddHashtreeFooter_IsAcceptedByAvbTool()
    {
        if (!WslTestHelpers.IsWslAvailable()) { Assert.Skip("WSL not available"); }
        if (!WslTestHelpers.IsAlpineAvailable()) { Assert.Skip("Alpine not available"); }
        if (!WslTestHelpers.EnsureAvbTool(out var reason)) { Assert.Skip(reason); }

        var root = WslTestHelpers.CreateTempDirectory();
        try
        {
            using var rsa = RSA.Create(2048);
            var privateKey = (RsaKeyParameters)DotNetUtilities.GetRsaKeyPair(rsa).Private;
            var keyPem = Path.Combine(root, "test key.pem");
            await File.WriteAllTextAsync(keyPem, rsa.ExportPkcs8PrivateKeyPem(), TestContext.Current.CancellationToken);
            var image = Path.Combine(root, "source.img");
            await File.WriteAllBytesAsync(image, BuildPatternedImage(512 * 1024), TestContext.Current.CancellationToken);
            // avbtool verify_image 按 <分区名>.img 在同目录定位原始镜像，输出文件须符合该约定。
            // avbtool verify_image locates the image as <partition_name>.img next to the vbmeta;
            // the output file must follow that convention.
            var signed = Path.Combine(root, "test.img");

            var result = await AvbSigner.AddHashtreeFooterAsync(
                image, "test", privateKey, signed, cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(result.Success, result.ErrorMessage ?? "AddHashtreeFooterAsync failed");

            var (output, exitCode) = WslTestHelpers.RunAvbTool(
                ["verify_image", "--image", WslTestHelpers.ToWslPath(signed), "--key", WslTestHelpers.ToWslPath(keyPem)]);
            Assert.True(exitCode == 0, $"avbtool verify_image rejected the managed hashtree image: {output}");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static byte[] BuildMinimalVbmeta()
    {
        var vbmeta = new byte[256];
        "AVB0"u8.CopyTo(vbmeta);
        return vbmeta;
    }

    private static byte[] BuildPatternedImage(int length)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++)
        {
            data[i] = (byte)(i * 13 + 5);
        }

        return data;
    }
}