using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Apk.Keys;
using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Apk.Packing;
using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Apk.Signing;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Tests for the managed APK signing stack: signing-block framing, chunked digests,
/// v2/v3 round trips and the v1 CMS artifacts.
/// <para>托管 APK 签名栈测试：签名分块结构、分块摘要、v2/v3 回环与 v1 CMS 工件。</para>
/// </summary>
public class ApkSigningTests : IDisposable
{
    private readonly string _root;
    private ApkSignerKeyMaterial? _keyMaterial;
    private ApkSignerKeyMaterial KeyMaterial => _keyMaterial ??= TestKeyFactory.CreateRsa(_root).Material;

    public ApkSigningTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "uotan-apk-signing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        _keyMaterial?.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort 尽力清理 */ }
    }

    [Fact]
    public void SigningBlock_BuildAndParse_RoundTrips()
    {
        var pairs = new List<(uint, byte[])>
        {
            (0x12345678u, [1, 2, 3, 4]),
            (ApkSignatureConstants.V2BlockId, new byte[64]),
        };

        var block = ApkSigningBlock.Build(pairs);
        var parsed = ApkSigningBlock.ParsePairs(block);

        Assert.Equal(2, parsed.Count);
        Assert.Equal(0x12345678u, parsed[0].Id);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, parsed[0].Value);
        Assert.Equal(ApkSignatureConstants.V2BlockId, parsed[1].Id);
        Assert.Equal(64, parsed[1].Value.Length);
        // Trailing magic must be present exactly once, in the LAST 16 bytes (spec layout:
        // [size 8B][magic 16B] immediately before the central directory).
        // 尾部 magic 应恰好出现一次，位于最后 16 字节（规范布局：紧邻中央目录的 [长度 8 字节][magic 16 字节]）。
        var magic = Encoding.ASCII.GetBytes("APK Sig Block 42");
        Assert.Equal(block.Length - 16, block.AsSpan().LastIndexOf(magic));
    }

    [Fact]
    public void Sign_RejectsAllSchemesDisabled_WithoutReplacingOutput()
    {
        var input = CreateMinimalApk("disabled.apk", out _);
        var output = Path.Combine(_root, "existing.apk");
        File.WriteAllText(output, "preserve");
        Assert.Throws<ArgumentException>(() => ApkSigner.Sign(input, output, KeyMaterial,
            new ApkSigningOptions { EnableV1 = false, EnableV2 = false, EnableV3 = false }, TestContext.Current.CancellationToken));
        Assert.Equal("preserve", File.ReadAllText(output));
        Assert.Throws<ArgumentException>(() => ApkSigner.Sign(input, input, KeyMaterial, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ContentDigest_EmptySectionsContributeNoChunks()
    {
        var eocd = new byte[22];
        using var input = new MemoryStream(eocd);
        var actual = ApkContentDigest.ComputeApkDigest(input, 0, 0, 0, 0, eocd, "SHA256", TestContext.Current.CancellationToken);
        var chunk = SHA256.HashData([0xa5, 22, 0, 0, 0, .. eocd]);
        var expected = SHA256.HashData([0x5a, 1, 0, 0, 0, .. chunk]);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void RepackAndSign_ReadsStoredAndDeflatedEntryContents()
    {
        var dir = Path.Combine(_root, "content");
        Directory.CreateDirectory(dir);
        var contents = new byte[2 * 1024 * 1024 + 17];
        RandomNumberGenerator.Fill(contents);
        File.WriteAllBytes(Path.Combine(dir, "payload.bin"), contents);
        File.WriteAllBytes(Path.Combine(dir, "resources.arsc"), contents);
        var unsigned = Path.Combine(_root, "content.apk");
        var signed = Path.Combine(_root, "content-signed.apk");
        ApkRepacker.Repack(dir, unsigned, cancellationToken: TestContext.Current.CancellationToken);
        ApkSigner.Sign(unsigned, signed, KeyMaterial, cancellationToken: TestContext.Current.CancellationToken);
        using var archive = System.IO.Compression.ZipFile.OpenRead(signed);
        foreach (var name in new[] { "payload.bin", "resources.arsc" })
        {
            using var entry = archive.GetEntry(name)!.Open();
            Assert.Equal(SHA256.HashData(contents), SHA256.HashData(entry));
        }
    }

    [Fact]
    public void KeyMaterial_RejectsMismatchedCertificate()
    {
        _ = KeyMaterial;
        var other = Path.Combine(_root, "other");
        Directory.CreateDirectory(other);
        using var otherMaterial = TestKeyFactory.CreateRsa(other, alternate: true).Material;
        Assert.Throws<CryptographicException>(() => ApkSignerKeyMaterial.Load(
            Path.Combine(_root, "test.pk8"), null, Path.Combine(other, "test.crt"), null));
    }

    [Fact]
    public void ContentDigest_ValidZip_IsDeterministicAndPreservesSource()
    {
        var apkPath = CreateMinimalApk("digest.apk", out var zipContentLength);
        using var stream = new FileStream(apkPath, FileMode.Open, FileAccess.Read);
        var sections = ApkZipSections.Locate(stream);
        Assert.Equal(stream.Length, sections.EocdOffset + sections.EocdSize);
        Assert.True(sections.CentralDirectoryOffset > 0);
        Assert.True(sections.CentralDirectorySize > 0);
        Assert.Equal(sections.EocdOffset, sections.CentralDirectoryOffset + sections.CentralDirectorySize);
        var eocd = ReadRange(stream, sections.EocdOffset, sections.EocdSize);

        var digest = ApkContentDigest.ComputeApkDigest(
            stream, zipContentLength, sections.CentralDirectoryOffset,
            sections.CentralDirectoryOffset, sections.CentralDirectorySize, eocd, "SHA256", TestContext.Current.CancellationToken);

        Assert.Equal(32, digest.Length);
        var repeated = ApkContentDigest.ComputeApkDigest(
            stream, zipContentLength, sections.CentralDirectoryOffset,
            sections.CentralDirectoryOffset, sections.CentralDirectorySize, eocd, "SHA256", TestContext.Current.CancellationToken);
        Assert.Equal(digest, repeated);
        // Source EOCD must stay untouched.
        // 源 EOCD 必须未被改动。
        var after = ReadRange(stream, sections.EocdOffset, sections.EocdSize);
        Assert.Equal(eocd, after);
    }

    [Fact]
    public void Sign_InitialAndRepeatedSigning_PreservesLayoutAndReplacesSignatures()
    {
        var input = CreateMinimalApk("resign-input.apk", out _);
        var first = Path.Combine(_root, "first-signed.apk");
        var second = Path.Combine(_root, "second-signed.apk");

        ApkSigner.Sign(input, first, KeyMaterial, new ApkSigningOptions(), TestContext.Current.CancellationToken);
        {
            Assert.True(File.Exists(first));
            using var initialStream = new FileStream(first, FileMode.Open, FileAccess.Read);
            var initialSections = ApkZipSections.Locate(initialStream);
            var initialFound = ApkSigningBlock.Find(initialStream, initialSections);
            Assert.NotNull(initialFound);

            var initialPairs = ApkSigningBlock.ParsePairs(initialFound!.Value.Contents);
            Assert.Contains(initialPairs, p => p.Id == ApkSignatureConstants.V2BlockId);
            Assert.Contains(initialPairs, p => p.Id == ApkSignatureConstants.V3BlockId);

            // The EOCD central-directory offset must point past the signing block.
            // EOCD 的中央目录偏移必须指向签名块之后。
            var blockEnd = initialFound.Value.Offset + initialFound.Value.Contents.Length;
            Assert.Equal(blockEnd, initialSections.CentralDirectoryOffset);
        }

        ApkSigner.Sign(first, second, KeyMaterial, new ApkSigningOptions(), TestContext.Current.CancellationToken);

        using var stream = new FileStream(second, FileMode.Open, FileAccess.Read);
        var sections = ApkZipSections.Locate(stream);
        var found = ApkSigningBlock.Find(stream, sections);
        Assert.NotNull(found);

        // Exactly one v2 pair: the stale block was stripped before re-signing.
        // 恰好一个 v2 对：旧签名块在重新签名前被剥离。
        var pairs = ApkSigningBlock.ParsePairs(found!.Value.Contents);
        Assert.Equal(1, pairs.Count(p => p.Id == ApkSignatureConstants.V2BlockId));
        Assert.Equal(1, pairs.Count(p => p.Id == ApkSignatureConstants.V3BlockId));
    }

    [Fact]
    public void Sign_V1Only_ProducesJarArtifacts()
    {
        var input = CreateMinimalApk("v1-input.apk", out _);
        var output = Path.Combine(_root, "v1-signed.apk");
        var options = new ApkSigningOptions
        {
            EnableV1 = true,
            EnableV2 = false,
            EnableV3 = false,
        };

        ApkSigner.Sign(input, output, KeyMaterial, options, TestContext.Current.CancellationToken);

        using var stream = new FileStream(output, FileMode.Open, FileAccess.Read);
        using var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Read);
        var names = archive.Entries.Select(e => e.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("META-INF/MANIFEST.MF", names);
        Assert.Contains("META-INF/UOTAN.SF", names);
        Assert.Contains("META-INF/UOTAN.RSA", names);
    }

    [Fact]
    public void V1SignatureFile_CarriesAndroidSignedAttribute()
    {
        var manifest = Encoding.ASCII.GetBytes("Manifest-Version: 1.0\r\n\r\n");
        var sf = ApkV1Signer.BuildSignatureFile(manifest, [2, 3]);
        var text = Encoding.ASCII.GetString(sf);

        Assert.Contains("X-Android-APK-Signed: 2, 3", text);
        Assert.Contains("SHA-256-Digest-Manifest: ", text);
    }

    [Fact]
    public void V1CmsSignature_VerifiesAgainstSignatureFile()
    {
        // The CMS over the SF must verify with the signer certificate.
        // 对 SF 的 CMS 必须能用签名者证书校验。
        var sf = Encoding.ASCII.GetBytes("Signature-Version: 1.0\r\n\r\n");
        var cmsBytes = ApkV1Signer.SignSignatureFile(sf, KeyMaterial);

        var signedCms = new SignedCms(new ContentInfo(sf), detached: true);
        signedCms.Decode(cmsBytes);
        signedCms.CheckSignature(true);

        Assert.Equal(sf, signedCms.ContentInfo.Content);
    }

    [Fact]
    public void Repack_ProducesZipReadableOutput_WithResourcesArscStored()
    {
        var input = Path.Combine(_root, "repack-src");
        Directory.CreateDirectory(Path.Combine(input, "res"));
        File.WriteAllText(Path.Combine(input, "AndroidManifest.xml"), "binary-xml-here");
        File.WriteAllText(Path.Combine(input, "res", "values.xml"), "<resources/>");
        File.WriteAllBytes(Path.Combine(input, "resources.arsc"), [0xAB, 0xCD, 0xEF, 0x00]);
        var output = Path.Combine(_root, "repacked.apk");

        ApkRepacker.Repack(input, output, cancellationToken: TestContext.Current.CancellationToken);

        using var stream = new FileStream(output, FileMode.Open, FileAccess.Read);
        using var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Read);
        Assert.Equal(3, archive.Entries.Count);
        var arsc = archive.Entries.Single(e => e.Name == "resources.arsc");
        Assert.Equal(System.IO.Compression.CompressionLevel.NoCompression, arsc is null ? default : ToLevel(arsc));

        static System.IO.Compression.CompressionLevel ToLevel(System.IO.Compression.ZipArchiveEntry entry)
            => entry.CompressedLength == entry.Length
                ? System.IO.Compression.CompressionLevel.NoCompression
                : System.IO.Compression.CompressionLevel.Optimal;
    }

    [Fact]
    public void Repack_AlignsStoredEntries()
    {
        var input = Path.Combine(_root, "align-src");
        Directory.CreateDirectory(input);
        File.WriteAllText(Path.Combine(input, "lib.so"), new string('x', 300));
        var output = Path.Combine(_root, "aligned.apk");

        ApkRepacker.Repack(input, output, new ApkRepackOptions
        {
            NativeLibraryAlignmentBytes = 4096,
        }, TestContext.Current.CancellationToken);

        // The .so entry data start must be 4096-byte aligned (zipalign semantics).
        // .so 条目数据起点必须按 4096 字节对齐（zipalign 语义）。
        using var stream = new FileStream(output, FileMode.Open, FileAccess.Read);
        var sections = ApkZipSections.Locate(stream);
        Assert.True(sections.CentralDirectoryOffset % 4096 == 0 || IsDataAligned(stream));
    }

    private static bool IsDataAligned(FileStream stream)
    {
        // Scan the first local header for the .so entry and check its data offset.
        // 扫描首个本地文件头并检查 .so 条目的数据偏移。
        Span<byte> header = stackalloc byte[30];
        stream.Seek(0, SeekOrigin.Begin);
        stream.ReadExactly(header);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x04034b50)
        {
            return false;
        }

        var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(26, 2));
        var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(28, 2));
        var dataOffset = 30 + nameLength + extraLength;
        return dataOffset % 4096 == 0;
    }

    // ---- helpers ------------------------------------------------------------------------

    /// <summary>
    /// Creates a minimal, valid APK (one text entry + central directory + EOCD) for signing tests.
    /// <para>为签名测试创建最小有效 APK（一个文本条目 + 中央目录 + EOCD）。</para>
    /// </summary>
    private string CreateMinimalApk(string fileName, out long zipContentLength)
    {
        var path = Path.Combine(_root, fileName);
        var entryName = "hello.txt";
        var entryContent = Encoding.UTF8.GetBytes("Hello, UotanStudio!");
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
        {
            Span<byte> local = stackalloc byte[30];
            var nameBytes = Encoding.UTF8.GetBytes(entryName);
            BinaryPrimitives.WriteUInt32LittleEndian(local, 0x04034b50);
            BinaryPrimitives.WriteUInt16LittleEndian(local.Slice(4, 2), 20);
            BinaryPrimitives.WriteUInt16LittleEndian(local.Slice(8, 2), 0); // stored
            BinaryPrimitives.WriteUInt32LittleEndian(local.Slice(14, 4), Crc32(entryContent));
            BinaryPrimitives.WriteUInt32LittleEndian(local.Slice(18, 4), (uint)entryContent.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(local.Slice(22, 4), (uint)entryContent.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(local.Slice(26, 2), (ushort)nameBytes.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(local.Slice(28, 2), 0);
            stream.Write(local);
            stream.Write(nameBytes);
            stream.Write(entryContent);

            var centralDirectoryOffset = stream.Position;
            Span<byte> central = stackalloc byte[46];
            BinaryPrimitives.WriteUInt32LittleEndian(central, 0x02014b50);
            BinaryPrimitives.WriteUInt16LittleEndian(central.Slice(4, 2), (3 << 8) | 20);
            BinaryPrimitives.WriteUInt16LittleEndian(central.Slice(6, 2), 20);
            BinaryPrimitives.WriteUInt32LittleEndian(central.Slice(16, 4), Crc32(entryContent));
            BinaryPrimitives.WriteUInt32LittleEndian(central.Slice(20, 4), (uint)entryContent.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(central.Slice(24, 4), (uint)entryContent.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(central.Slice(28, 2), (ushort)nameBytes.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(central.Slice(42, 4), 0);
            stream.Write(central);
            stream.Write(nameBytes);

            var centralDirectorySize = stream.Position - centralDirectoryOffset;
            Span<byte> eocd = stackalloc byte[22];
            BinaryPrimitives.WriteUInt32LittleEndian(eocd, 0x06054b50);
            BinaryPrimitives.WriteUInt16LittleEndian(eocd.Slice(8, 2), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(eocd.Slice(10, 2), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(eocd.Slice(12, 4), (uint)centralDirectorySize);
            BinaryPrimitives.WriteUInt32LittleEndian(eocd.Slice(16, 4), (uint)centralDirectoryOffset);
            BinaryPrimitives.WriteUInt16LittleEndian(eocd.Slice(20, 2), 0);
            stream.Write(eocd);
        }

        zipContentLength = 30 + Encoding.UTF8.GetByteCount(entryName) + entryContent.Length;
        return path;
    }

    private static byte[] ReadRange(Stream stream, long offset, long length)
    {
        var buffer = new byte[length];
        stream.Seek(offset, SeekOrigin.Begin);
        stream.ReadExactly(buffer);
        return buffer;
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        // System.IO.Hashing Crc32 (little-endian hash output matches ZIP requirements).
        // System.IO.Hashing 的 Crc32（小端哈希输出符合 ZIP 要求）。
        var crc = new System.IO.Hashing.Crc32();
        crc.Append(data);
        return BitConverter.ToUInt32(crc.GetCurrentHash());
    }
}

/// <summary>
/// Creates isolated test keys/certificates for signing tests (test-only; no key material
/// is bundled into the product).
/// <para>为签名测试生成确定性测试密钥/证书（仅测试使用；产品不内置任何密钥材料）。</para>
/// </summary>
internal static class TestKeyFactory
{
    public static (ApkSignerKeyMaterial Material, string KeyPath) CreateRsa(string root, bool alternate = false)
    {
        using var rsa = SigningTestKeys.Create(2048, alternate);
        var request = new CertificateRequest("CN=UotanStudio Test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));


        var keyPath = Path.Combine(root, "test.pk8");
        File.WriteAllBytes(keyPath, rsa.ExportPkcs8PrivateKey());
        var certPath = Path.Combine(root, "test.crt");
        File.WriteAllBytes(certPath, certificate.Export(X509ContentType.Cert));

        var material = ApkSignerKeyMaterial.Load(keyPath, null, certPath, null);
        return (material, keyPath);
    }
}