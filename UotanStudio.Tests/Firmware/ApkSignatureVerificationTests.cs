using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using UotanStudio.Compress.Zip;
using UotanStudio.Firmware.AndroidMeta.Apk;
using UotanStudio.Firmware.AndroidMeta.Apk.Signing;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class ApkSignatureVerificationTests
{
    [Fact]
    public async Task Verify_OwnSignedApk_AllEnabledSchemesValid()
    {
        var temp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(temp);
        try
        {
            var (keyPath, certPath) = CreateKeyPair(temp);
            var inputApk = await BuildUnsignedApkAsync(temp);
            var signedApk = Path.Combine(temp, "signed.apk");
            ApkSigner.Sign(inputApk, signedApk, ApkSignerKeyMaterial.Load(keyPath, null, certPath, null), null, TestContext.Current.CancellationToken);

            var verification = ApkSignatureVerifier.Verify(signedApk, TestContext.Current.CancellationToken);
            Assert.Equal(ApkSchemeStatus.Valid, verification.V1);
            Assert.Equal(ApkSchemeStatus.Valid, verification.V2);
            Assert.Equal(ApkSchemeStatus.Valid, verification.V3);
            Assert.Empty(verification.Problems);
            Assert.NotEmpty(verification.SignerSubjects);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public async Task Verify_TamperedContentEachScheme_ReportsInvalid()
    {
        var temp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(temp);
        try
        {
            var (keyPath, certPath) = CreateKeyPair(temp);
            var inputApk = await BuildUnsignedApkAsync(temp);

            var v2Only = Path.Combine(temp, "v2only.apk");
            ApkSigner.Sign(inputApk, v2Only, ApkSignerKeyMaterial.Load(keyPath, null, certPath, null),
                new ApkSigningOptions { EnableV1 = false, EnableV3 = false }, TestContext.Current.CancellationToken);
            TamperStoredPayload(v2Only);
            var v2Result = ApkSignatureVerifier.Verify(v2Only, TestContext.Current.CancellationToken);
            Assert.Equal(ApkSchemeStatus.Invalid, v2Result.V2);
            Assert.Equal(ApkSchemeStatus.NotPresent, v2Result.V3);
            Assert.Contains(v2Result.Problems, p => p.StartsWith("v2", StringComparison.Ordinal));

            var v1Only = Path.Combine(temp, "v1only.apk");
            ApkSigner.Sign(inputApk, v1Only, ApkSignerKeyMaterial.Load(keyPath, null, certPath, null),
                new ApkSigningOptions { EnableV2 = false, EnableV3 = false }, TestContext.Current.CancellationToken);
            TamperStoredPayload(v1Only);
            var v1Result = ApkSignatureVerifier.Verify(v1Only, TestContext.Current.CancellationToken);
            Assert.Equal(ApkSchemeStatus.Invalid, v1Result.V1);
            Assert.Contains(v1Result.Problems, p => p.StartsWith("v1: entry ", StringComparison.Ordinal));

            var v3Only = Path.Combine(temp, "v3only.apk");
            ApkSigner.Sign(inputApk, v3Only, ApkSignerKeyMaterial.Load(keyPath, null, certPath, null),
                new ApkSigningOptions { EnableV1 = false, EnableV2 = false }, TestContext.Current.CancellationToken);
            TamperStoredPayload(v3Only);
            var v3Result = ApkSignatureVerifier.Verify(v3Only, TestContext.Current.CancellationToken);
            Assert.Equal(ApkSchemeStatus.Invalid, v3Result.V3);
            Assert.True(v3Result.V3MinSdk >= 28);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public async Task Verify_StrippedApk_ReportsNotPresent()
    {
        var temp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(temp);
        try
        {
            var (keyPath, certPath) = CreateKeyPair(temp);
            var inputApk = await BuildUnsignedApkAsync(temp);
            var signedApk = Path.Combine(temp, "signed.apk");
            ApkSigner.Sign(inputApk, signedApk, ApkSignerKeyMaterial.Load(keyPath, null, certPath, null), null, TestContext.Current.CancellationToken);
            var strippedApk = Path.Combine(temp, "stripped.apk");
            ApkEntryStripper.Rewrite(signedApk, strippedApk, new ApkEntryStripper.RewriteRequest { StripV1SignatureEntries = true }, TestContext.Current.CancellationToken);

            var verification = ApkSignatureVerifier.Verify(strippedApk, TestContext.Current.CancellationToken);
            Assert.Equal(ApkSchemeStatus.NotPresent, verification.V1);
            Assert.Equal(ApkSchemeStatus.NotPresent, verification.V2);
            Assert.Equal(ApkSchemeStatus.NotPresent, verification.V3);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public async Task Verify_UnsignedApk_ReportsNotPresentEverywhere()
    {
        var temp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(temp);
        try
        {
            var inputApk = await BuildUnsignedApkAsync(temp);
            var verification = ApkSignatureVerifier.Verify(inputApk, TestContext.Current.CancellationToken);
            Assert.Equal(ApkSchemeStatus.NotPresent, verification.V1);
            Assert.Equal(ApkSchemeStatus.NotPresent, verification.V2);
            Assert.Equal(ApkSchemeStatus.NotPresent, verification.V3);
            Assert.Empty(verification.Problems);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    /// <summary>Flips one byte of the stored <c>assets/payload.bin</c> payload by decoding its local file header offsets.
    /// <para>按本地文件头偏移解码并翻转存储条目 <c>assets/payload.bin</c> 载荷的一个字节。</para></summary>
    private static void TamperStoredPayload(string apkPath)
    {
        var bytes = File.ReadAllBytes(apkPath);
        var dataStart = -1;
        for (var candidate = 0; candidate + 30 <= bytes.Length && dataStart < 0; candidate++)
        {
            if (bytes[candidate] != 0x50 || bytes[candidate + 1] != 0x4B
                || bytes[candidate + 2] != 0x03 || bytes[candidate + 3] != 0x04)
            {
                continue;
            }
            var method = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(candidate + 8, 2));
            var compressedSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(candidate + 18, 4));
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(candidate + 26, 2));
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(candidate + 28, 2));
            if (method != 0 || compressedSize == 0 || candidate + 30 + nameLength > bytes.Length)
            {
                continue;
            }
            var name = bytes.AsSpan(candidate + 30, nameLength);
            if (name.SequenceEqual("assets/payload.bin"u8))
            {
                dataStart = candidate + 30 + nameLength + extraLength;
            }
        }
        Assert.True(dataStart >= 0, "the fixture must contain a stored payload entry to tamper with");
        bytes[dataStart] ^= 0xFF;
        File.WriteAllBytes(apkPath, bytes);
    }

    /// <summary>Generates one self-signed RSA key/certificate pair in PEM files for signing.
    /// <para>生成一对自签 RSA PEM 密钥/证书文件用于签名。</para></summary>
    private static (string KeyPath, string CertPath) CreateKeyPair(string directory)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=UotanStudio Verify Test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
        var keyPath = Path.Combine(directory, "test-key.pem");
        var certPath = Path.Combine(directory, "test-cert.pem");
        File.WriteAllText(keyPath, certificate.GetRSAPrivateKey()!.ExportPkcs8PrivateKeyPem());
        File.WriteAllText(certPath, certificate.ExportCertificatePem());
        return (keyPath, certPath);
    }

    /// <summary>Builds a minimal unsigned APK with two stored (uncompressed) entries.
    /// <para>构建最小未签名 APK，含两个存储（未压缩）条目。</para></summary>
    private static async Task<string> BuildUnsignedApkAsync(string directory)
    {
        var apkPath = Path.Combine(directory, "unsigned.apk");
        var payloadBytes = "hello uotan verification"u8.ToArray();
        var fillerBytes = new byte[4096];
        Random.Shared.NextBytes(fillerBytes);
        await using (var stream = File.Create(apkPath))
        using (var archive = ZipContainer.Create(stream))
        {
            WriteStoredEntry(archive, "assets/payload.bin", payloadBytes);
            WriteStoredEntry(archive, "assets/filler.bin", fillerBytes);
        }
        return apkPath;
    }

    /// <summary>Writes one stored ZIP entry through the shared container facade.
    /// <para>经共享容器门面写出一个存储 ZIP 条目。</para></summary>
    private static void WriteStoredEntry(ZipArchive archive, string name, byte[] content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
        using var output = entry.Open();
        output.Write(content);
    }
}