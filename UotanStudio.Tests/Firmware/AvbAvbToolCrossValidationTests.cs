using System.Security.Cryptography;
using Org.BouncyCastle.Security;
using UotanStudio.Firmware;
using UotanStudio.Firmware.Avb;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Cross-validation between the managed AVB implementation and Google's official avbtool,
/// run inside WSL (conditional, Assert.Skip when WSL/Alpine/avbtool are unavailable):
/// 1. avbtool add_hash_footer produces an image that <see cref="AvbVerifier.Verify"/> accepts.
/// 2. <see cref="AvbSigner.AddHashFooterAsync"/> output passes avbtool verify_image.
/// <para>托管 AVB 实现与 Google 官方 avbtool 的交叉验证，在 WSL 内运行（WSL/Alpine/avbtool
/// 不可用时按 <see cref="Assert.Skip"/> 跳过）：avbtool 生成的镜像须通过
/// <see cref="AvbVerifier.Verify"/>；<see cref="AvbSigner.AddHashFooterAsync"/> 的输出须通过
/// avbtool verify_image。</para>
/// </summary>
public class AvbAvbToolCrossValidationTests
{
    private const int PartitionSize = 1024 * 1024;

    [Fact]
    public async Task AvbToolAddHashFooter_IsAcceptedByManagedVerifier()
    {
        SkipUnlessWslAvbToolAvailable(out var root);
        try
        {
            var keyPem = Path.Combine(root, "test key.pem");
            await File.WriteAllTextAsync(keyPem, ExportPkcs8Pem(2048), TestContext.Current.CancellationToken);
            var image = Path.Combine(root, "system.img");
            await File.WriteAllBytesAsync(image, BuildPatternedImage(512 * 1024), TestContext.Current.CancellationToken);

            var (output, exitCode) = WslTestHelpers.RunAvbTool(
                ["add_hash_footer", "--image", WslTestHelpers.ToWslPath(image),
                 "--partition_name", "test", "--partition_size", PartitionSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
                 "--algorithm", "SHA256_RSA2048", "--key", WslTestHelpers.ToWslPath(keyPem)]);
            Assert.True(exitCode == 0, $"avbtool add_hash_footer failed: {output}");

            var result = AvbVerifier.Verify(await File.ReadAllBytesAsync(image, TestContext.Current.CancellationToken));
            Assert.True(result.IsSigned, result.ErrorMessage ?? "image not recognized as signed");
            Assert.True(result.IsValid, result.ErrorMessage ?? "signature verification failed");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ManagedAddHashFooter_IsAcceptedByAvbTool()
    {
        SkipUnlessWslAvbToolAvailable(out var root);
        try
        {
            using var rsa = RSA.Create(2048);
            var privateKey = (Org.BouncyCastle.Crypto.Parameters.RsaKeyParameters)
                DotNetUtilities.GetRsaKeyPair(rsa).Private;
            var keyPem = Path.Combine(root, "test key.pem");
            await File.WriteAllTextAsync(keyPem, rsa.ExportPkcs8PrivateKeyPem(), TestContext.Current.CancellationToken);

            var image = Path.Combine(root, "source.img");
            await File.WriteAllBytesAsync(image, BuildPatternedImage(512 * 1024), TestContext.Current.CancellationToken);
            // avbtool verify_image 按 <分区名>.img 在同目录定位原始镜像，输出文件须符合该约定。
            // avbtool verify_image locates the image as <partition_name>.img next to the vbmeta;
            // the output file must follow that convention.
            var signed = Path.Combine(root, "test.img");

            var result = await AvbSigner.AddHashFooterAsync(image, "test", privateKey, signed, TestContext.Current.CancellationToken);
            Assert.True(result.Success, result.ErrorMessage ?? "AvbSigner.AddHashFooterAsync failed");

            var (output, exitCode) = WslTestHelpers.RunAvbTool(
                ["verify_image", "--image", WslTestHelpers.ToWslPath(signed), "--key", WslTestHelpers.ToWslPath(keyPem)]);
            Assert.True(exitCode == 0, $"avbtool verify_image rejected the managed-signed image: {output}");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void SkipUnlessWslAvbToolAvailable(out string root)
    {
        if (!WslTestHelpers.IsWslAvailable()) { Assert.Skip("WSL not available"); }
        if (!WslTestHelpers.IsAlpineAvailable()) { Assert.Skip("Alpine not available"); }
        if (!WslTestHelpers.EnsureAvbTool(out var reason)) { Assert.Skip(reason); }
        root = WslTestHelpers.CreateTempDirectory();
    }

    private static string ExportPkcs8Pem(int keySize)
    {
        using var rsa = RSA.Create(keySize);
        return rsa.ExportPkcs8PrivateKeyPem();
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