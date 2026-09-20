using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Apk.Keys;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Tests for generated APK signing material: key/certificate round trips through the signer's
/// key loader, encrypted keys, PFX export, validation failures and no-overwrite semantics.
/// <para>生成的 APK 签名材料测试：经签名器密钥加载器的密钥/证书回环、加密私钥、PFX 导出、参数校验失败与不覆盖语义。</para>
/// </summary>
public class ApkSigningKeyGenerationTests : IDisposable
{
    private readonly string _root;

    public ApkSigningKeyGenerationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "uotan-apk-keygen-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort 尽力清理 */ }
    }

    [Fact]
    public void Generate_Rsa2048_RoundTripsThroughSignerKeyLoader()
    {
        var files = ApkSigningKeyGenerator.Generate(
            new ApkSigningKeyOptions { CommonName = "Test RSA" }, _root, "rsa-test");

        Assert.True(File.Exists(files.PrivateKeyPath));
        Assert.True(File.Exists(files.CertificatePath));
        Assert.Null(files.PfxPath);

        using var material = ApkSignerKeyMaterial.Load(files.PrivateKeyPath, null, files.CertificatePath, null);
        var rsa = Assert.IsAssignableFrom<RSA>(material.Key);
        Assert.Equal(2048, rsa.KeySize);

        var certificate = X509CertificateLoader.LoadCertificateFromFile(files.CertificatePath);
        using (certificate)
        {
            Assert.Equal("CN=Test RSA", certificate.Subject);
            // Self-signed: issuer equals subject. 自签：签发者与主题一致。
            Assert.Equal(certificate.Subject, certificate.Issuer);
            Assert.Equal(certificate.PublicKey.ExportSubjectPublicKeyInfo(), rsa.ExportSubjectPublicKeyInfo());
        }
    }

    [Theory]
    [InlineData(ApkSigningKeyType.Rsa3072)]
    [InlineData(ApkSigningKeyType.Rsa4096)]
    [InlineData(ApkSigningKeyType.EcP256)]
    [InlineData(ApkSigningKeyType.EcP384)]
    [InlineData(ApkSigningKeyType.EcP521)]
    public void Generate_KeyTypes_RoundTrip(ApkSigningKeyType keyType)
    {
        var files = ApkSigningKeyGenerator.Generate(
            new ApkSigningKeyOptions { KeyType = keyType, CommonName = "Types" }, _root);

        using var material = ApkSignerKeyMaterial.Load(files.PrivateKeyPath, null, files.CertificatePath, null);
        switch (keyType)
        {
            case ApkSigningKeyType.Rsa3072:
                Assert.Equal(3072, Assert.IsAssignableFrom<RSA>(material.Key).KeySize);
                break;
            case ApkSigningKeyType.Rsa4096:
                Assert.Equal(4096, Assert.IsAssignableFrom<RSA>(material.Key).KeySize);
                break;
            default:
                Assert.IsAssignableFrom<ECDsa>(material.Key);
                break;
        }
    }

    [Fact]
    public void Generate_EncryptedKey_RoundTripsWithPassword()
    {
        var files = ApkSigningKeyGenerator.Generate(
            new ApkSigningKeyOptions { CommonName = "Encrypted", KeyPassword = "secret-pass" }, _root, "enc");

        using var pem = File.OpenText(files.PrivateKeyPath);
        Assert.Contains("ENCRYPTED PRIVATE KEY", pem.ReadToEnd(), StringComparison.Ordinal);

        using var material = ApkSignerKeyMaterial.Load(files.PrivateKeyPath, "secret-pass", files.CertificatePath, null);
        Assert.IsAssignableFrom<RSA>(material.Key);
        Assert.Throws<CryptographicException>(() =>
            ApkSignerKeyMaterial.Load(files.PrivateKeyPath, "wrong-pass", files.CertificatePath, null));
    }

    [Fact]
    public void Generate_PfxExport_ContainsPrivateKey()
    {
        var files = ApkSigningKeyGenerator.Generate(
            new ApkSigningKeyOptions { CommonName = "Pfx", KeyPassword = "pfx-pass" }, _root, exportPfx: true);

        Assert.NotNull(files.PfxPath);
        Assert.True(File.Exists(files.PfxPath));

        using var pfx = X509CertificateLoader.LoadPkcs12FromFile(files.PfxPath!, "pfx-pass", X509KeyStorageFlags.EphemeralKeySet);
        Assert.True(pfx.HasPrivateKey);
        // The generated private key file honors options.KeyPassword; load it with that password.
        // 生成的私钥文件遵循 options.KeyPassword，加载时须传入同一密码。
        using var material = ApkSignerKeyMaterial.Load(files.PrivateKeyPath, "pfx-pass", files.PfxPath!, "pfx-pass");
        Assert.Equal(material.Key.ExportSubjectPublicKeyInfo(), pfx.PublicKey.ExportSubjectPublicKeyInfo());
    }

    [Fact]
    public void Generate_SubjectFields_AppearInCertificate()
    {
        var files = ApkSigningKeyGenerator.Generate(
            new ApkSigningKeyOptions
            {
                CommonName = "Subject Fields",
                Organization = "Uotan Studio",
                OrganizationalUnit = "Firmware",
                Country = "CN",
                ValidityYears = 5,
            }, _root, "subject");

        using var certificate = X509CertificateLoader.LoadCertificateFromFile(files.CertificatePath);
        Assert.Contains("Uotan Studio", certificate.Subject, StringComparison.Ordinal);
        Assert.Contains("Firmware", certificate.Subject, StringComparison.Ordinal);
        Assert.Contains("CN=Subject Fields", certificate.Subject, StringComparison.Ordinal);
        // notAfter is notBefore + ValidityYears, so the year delta is exactly the requested validity.
        // notAfter = notBefore + ValidityYears，年份差即请求的有效年数。
        Assert.Equal(5, certificate.NotAfter.Year - certificate.NotBefore.Year);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("bad/slash")]
    public void Generate_InvalidCommonName_Throws(string commonName)
    {
        Assert.Throws<ArgumentException>(() => ApkSigningKeyGenerator.Generate(
            new ApkSigningKeyOptions { CommonName = commonName }, _root));
    }

    [Fact]
    public void Generate_InvalidValidityOrCountry_Throws()
    {
        Assert.Throws<ArgumentException>(() => ApkSigningKeyGenerator.Generate(
            new ApkSigningKeyOptions { CommonName = "V", ValidityYears = 0 }, _root));
        Assert.Throws<ArgumentException>(() => ApkSigningKeyGenerator.Generate(
            new ApkSigningKeyOptions { CommonName = "V", ValidityYears = 101 }, _root));
        Assert.Throws<ArgumentException>(() => ApkSigningKeyGenerator.Generate(
            new ApkSigningKeyOptions { CommonName = "V", Country = "CHN" }, _root));
    }

    [Fact]
    public void Generate_ExistingOutputFile_NeverOverwritten()
    {
        var target = Path.Combine(_root, "occupied");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "apk-signing.pk8"), "existing key");
        File.WriteAllText(Path.Combine(target, "apk-signing.pem"), "existing cert");

        Assert.Throws<IOException>(() => ApkSigningKeyGenerator.Generate(
            new ApkSigningKeyOptions { CommonName = "No Overwrite" }, target));

        Assert.Equal("existing key", File.ReadAllText(Path.Combine(target, "apk-signing.pk8")));
        Assert.Equal("existing cert", File.ReadAllText(Path.Combine(target, "apk-signing.pem")));
    }

    [Fact]
    public void Generate_DeterministicWellFormedOutput_NoTemporaryFilesLeft()
    {
        var files = ApkSigningKeyGenerator.Generate(
            new ApkSigningKeyOptions { CommonName = "Clean" }, _root, "clean", exportPfx: true);

        Assert.Equal(["clean.pem", "clean.pfx", "clean.pk8"],
            Directory.EnumerateFiles(_root).Select(Path.GetFileName).OrderBy(n => n).ToArray());
        _ = files;
    }
}