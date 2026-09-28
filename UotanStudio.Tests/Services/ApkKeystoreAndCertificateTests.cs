using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using UotanStudio.Firmware.AndroidMeta.Apk;
using UotanStudio.Services.Keys;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>
/// Tests for the JKS keystore writer/reader round trip, APK certificate generation with format
/// exports, and EC private key import into the managed key library.
/// <para>JKS 密钥库写入/读取回环、APK 证书生成与格式导出、托管密钥库导入 EC 私钥的测试。</para>
/// </summary>
public class ApkKeystoreAndCertificateTests : IDisposable
{
    private readonly string _root;

    public ApkKeystoreAndCertificateTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "uotan-apk-ks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void JksWriter_RoundTripsThroughReader()
    {
        using var rsa = RSA.Create(2048);
        var privateKey = rsa.ExportPkcs8PrivateKey();
        using var certificate = SelfSigned(rsa);
        var data = JksKeyStoreWriter.Build("my-alias", privateKey, [certificate.RawData], "store-pass",
            new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.Zero));

        var entries = JksKeyStoreReader.Read(data, "store-pass");
        var entry = Assert.Single(entries);
        Assert.Equal(1, entry.Tag);
        Assert.Equal("my-alias", entry.Alias);
        Assert.Equal(new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.Zero), entry.Creation);
        Assert.NotNull(entry.ProtectedPrivateKey);
        Assert.Single(entry.Certificates);
        Assert.Equal(certificate.RawData, entry.Certificates[0]);

        Assert.Throws<InvalidDataException>(() => JksKeyStoreReader.Read(data, "wrong"));
        var protectedKey = JksKeyStoreReader.Read(data, "store-pass")[0].ProtectedPrivateKey!;
        Assert.Throws<InvalidDataException>(() => JksKeyStoreReader.Unprotect(protectedKey, "wrong"));
        var decrypted = JksKeyStoreReader.Unprotect(protectedKey, "store-pass");
        using var loaded = RSA.Create();
        loaded.ImportPkcs8PrivateKey(decrypted, out var read);
        Assert.Equal(read, decrypted.Length);
        Assert.Equal(rsa.ExportSubjectPublicKeyInfo(), loaded.ExportSubjectPublicKeyInfo());
    }

    [Fact]
    public void JksWriter_ModifiedUtfAlias_SurvivesRoundTrip()
    {
        using var rsa = RSA.Create(2048);
        using var certificate = SelfSigned(rsa);
        var alias = "别名-ключ-\u0000キー";
        var data = JksKeyStoreWriter.Build(alias, rsa.ExportPkcs8PrivateKey(), [certificate.RawData], "p");
        Assert.Equal(alias, Assert.Single(JksKeyStoreReader.Read(data, "p")).Alias);
    }

    [Fact]
    public void JksWriter_RejectsInvalidInput()
    {
        using var rsa = RSA.Create(2048);
        var key = rsa.ExportPkcs8PrivateKey();
        using var certificate = SelfSigned(rsa);
        Assert.Throws<ArgumentException>(() => JksKeyStoreWriter.Build("", key, [certificate.RawData], "p"));
        Assert.Throws<ArgumentException>(() => JksKeyStoreWriter.Build("a", key, [certificate.RawData], ""));
        Assert.Throws<ArgumentException>(() => JksKeyStoreWriter.Build("a", key, [], "p"));
    }

    [Fact]
    public void GenerateApkCertificate_AllFormats_WritesCompleteBundle()
    {
        using var library = new KeyLibraryFixture(_root);
        var output = Path.Combine(_root, "out-rsa");
        var bundle = library.Library.GenerateApkCertificate("release-key", keyPassword: null, storePassword: "store-pass", validityYears: 10, new ApkSigningKeyOptions { CommonName = "Bundle RSA" }, ApkSigningKeyType.Rsa2048, ApkCertificateExportFormat.All, output, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ManagedKeyKind.Apk, bundle.Key.Kind);
        Assert.Equal(2048, bundle.Key.Bits);
        Assert.All(bundle.ExportedFiles, f => Assert.True(File.Exists(f), f));
        Assert.All(bundle.ExportedFiles, f => Assert.Equal(output, Path.GetDirectoryName(f)));
        Assert.Equal(["release-key.jks", "release-key.p12", "release-key.pem", "release-key.pk8"],
            bundle.ExportedFiles.Select(Path.GetFileName).Order().ToArray());

        var entries = JksKeyStoreReader.Read(File.ReadAllBytes(Path.Combine(output, "release-key.jks")), "store-pass");
        Assert.Equal("release-key", Assert.Single(entries).Alias);

        using var pfx = X509CertificateLoader.LoadPkcs12FromFile(
            Path.Combine(output, "release-key.p12"), "store-pass", X509KeyStorageFlags.EphemeralKeySet);
        Assert.True(pfx.HasPrivateKey);
        var libraryPublic = RSA.Create();
        try
        {
            libraryPublic.ImportFromPem(File.ReadAllText(Path.Combine(library.Library.PrivatePath(bundle.Key) + ".pub")));
            Assert.Equal(pfx.PublicKey.ExportSubjectPublicKeyInfo(), libraryPublic.ExportSubjectPublicKeyInfo());
        }
        finally { libraryPublic.Dispose(); }

        using var material = ApkSignerKeyMaterial.Load(
            Path.Combine(output, "release-key.pk8"), null, Path.Combine(output, "release-key.pem"), null);
        Assert.Equal(pfx.PublicKey.ExportSubjectPublicKeyInfo(), material.Key.ExportSubjectPublicKeyInfo());
    }

    [Fact]
    public void GenerateApkCertificate_EcCurve_FormatsAndLoads()
    {
        using var library = new KeyLibraryFixture(_root);
        var output = Path.Combine(_root, "out-ec");
        var bundle = library.Library.GenerateApkCertificate("ec-key", keyPassword: "key-pass", storePassword: "store-pass", validityYears: 5, new ApkSigningKeyOptions { CommonName = "Bundle EC" }, ApkSigningKeyType.EcP384, ApkCertificateExportFormat.All, output, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(384, bundle.Key.Bits);
        Assert.Equal(ManagedKeyKind.Apk, bundle.Key.Kind);

        var entries = JksKeyStoreReader.Read(File.ReadAllBytes(Path.Combine(output, "ec-key.jks")), "store-pass");
        Assert.Single(entries);
        using var material = ApkSignerKeyMaterial.Load(
            Path.Combine(output, "ec-key.pk8"), "key-pass", Path.Combine(output, "ec-key.pem"), null);
        Assert.IsType<ECDsa>(material.Key, exactMatch: false);
        Assert.Equal(384, ((ECDsa)material.Key).KeySize);
    }

    [Theory]
    [InlineData(ApkCertificateExportFormat.Jks, new string[] { ".jks" })]
    [InlineData(ApkCertificateExportFormat.Pkcs12, new string[] { ".p12" })]
    [InlineData(ApkCertificateExportFormat.Pem, new string[] { ".pk8", ".pem" })]
    public void GenerateApkCertificate_FormatSelection_ExportsOnlyRequestedFiles(ApkCertificateExportFormat format, string[] extensions)
    {
        using var library = new KeyLibraryFixture(_root);
        var output = Path.Combine(_root, "out-" + format);
        var bundle = library.Library.GenerateApkCertificate("fmt-key", null, "store-pass", 5, new ApkSigningKeyOptions { CommonName = "Fmt" }, ApkSigningKeyType.Rsa2048, format, output, TestContext.Current.CancellationToken);

        Assert.Equal(extensions.OrderBy(e => e), bundle.ExportedFiles.Select(Path.GetExtension).Order().ToArray());
    }

    [Fact]
    public void GenerateApkCertificate_JksWithoutStorePassword_Throws()
    {
        using var library = new KeyLibraryFixture(_root);
        Assert.Throws<ArgumentException>(() => library.Library.GenerateApkCertificate("no-store-pass", null, storePassword: null, 5, new ApkSigningKeyOptions { CommonName = "X" }, ApkSigningKeyType.Rsa2048, ApkCertificateExportFormat.Jks, _root, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Import_EcPrivateKey_CreatesApkEntry()
    {
        using var library = new KeyLibraryFixture(_root);
        var keyPath = Path.Combine(_root, "imported-ec.pem");
        using (var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256))
            File.WriteAllText(keyPath, ec.ExportPkcs8PrivateKeyPem());

        var imported = library.Library.Import("imported-ec", ManagedKeyKind.Apk, keyPath, "");
        Assert.Equal(256, imported.Bits);
        Assert.Equal(ManagedKeyKind.Apk, imported.Kind);
        Assert.Contains("EC P-256", imported.Summary, StringComparison.Ordinal);
        Assert.True(File.Exists(library.Library.PrivatePath(imported) + ".crt"));
    }

    [Fact]
    public void Import_EncryptedEcPrivateKey_WithPassword_CreatesApkEntry()
    {
        using var library = new KeyLibraryFixture(_root);
        var keyPath = Path.Combine(_root, "imported-enc-ec.pem");
        using (var ec = ECDsa.Create(ECCurve.NamedCurves.nistP384))
            File.WriteAllText(keyPath, ec.ExportEncryptedPkcs8PrivateKeyPem(
                "secret", new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 100_000)));

        var imported = library.Library.Import("imported-enc-ec", ManagedKeyKind.Apk, keyPath, "secret");
        Assert.Equal(384, imported.Bits);
    }

    [Fact]
    public void Import_PublicOnlyKey_IsRejected()
    {
        using var library = new KeyLibraryFixture(_root);
        var keyPath = Path.Combine(_root, "public-only.pem");
        using (var rsa = RSA.Create(2048))
            File.WriteAllText(keyPath, rsa.ExportSubjectPublicKeyInfoPem());
        Assert.Throws<InvalidDataException>(() => library.Library.Import("public-only", ManagedKeyKind.Apk, keyPath, ""));
    }

    [Fact]
    public void Generate_ApkEcSizes_ProduceExpectedCurve()
    {
        using var library = new KeyLibraryFixture(_root);
        foreach (var bits in new[] { 256, 384, 521 })
        {
            var key = library.Library.Generate($"ec-{bits}", ManagedKeyKind.Apk, bits);
            Assert.Equal(bits, key.Bits);
            var privatePem = File.ReadAllText(library.Library.PrivatePath(key));
            using var ec = ECDsa.Create();
            ec.ImportFromPem(privatePem);
            Assert.Equal(bits, ec.KeySize);
        }
    }

    private static X509Certificate2 SelfSigned(RSA rsa)
    {
        var request = new CertificateRequest(new X500DistinguishedName("CN=JksTest"), rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }

    private sealed class KeyLibraryFixture : IDisposable
    {
        public KeyLibraryFixture(string root)
        {
            Root = Path.Combine(root, "library-" + Guid.NewGuid().ToString("N"));
            Library = new KeyLibraryService(Root);
        }

        public string Root { get; }
        public KeyLibraryService Library { get; }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { }
        }
    }
}