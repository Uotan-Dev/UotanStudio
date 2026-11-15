using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using UotanStudio.Compress.Zip;
using UotanStudio.Hash.Digests;

namespace UotanStudio.Firmware.AndroidMeta.Apk.Signing;

/// <summary>
/// Status of one APK signature scheme after verification.
/// <para>验签后单个 APK 签名方案的状态。</para></summary>
public enum ApkSchemeStatus
{
    /// <summary>No artifacts of the scheme exist in the file. <para>文件中不存在该方案的工件。</para></summary>
    NotPresent,

    /// <summary>The scheme verified successfully. <para>该方案验签通过。</para></summary>
    Valid,

    /// <summary>The scheme exists but failed verification; the reason is in the problem list.
    /// <para>方案存在但验签失败；原因见问题列表。</para></summary>
    Invalid,

    /// <summary>The scheme exists but this implementation cannot verify it (for example DSA keys).
    /// <para>方案存在但本实现无法验签（例如 DSA 密钥）。</para></summary>
    Unsupported
}

/// <summary>
/// The per-scheme verification result for one APK file.
/// <para>单个 APK 文件的逐方案验签结果。</para></summary>
public sealed class ApkSignatureVerification
{
    /// <summary>v1 (JAR) scheme status. <para>v1（JAR）方案状态。</para></summary>
    public ApkSchemeStatus V1 { get; init; }

    /// <summary>v2 scheme status. <para>v2 方案状态。</para></summary>
    public ApkSchemeStatus V2 { get; init; }

    /// <summary>v3 scheme status. <para>v3 方案状态。</para></summary>
    public ApkSchemeStatus V3 { get; init; }

    /// <summary>Signature certificate subjects (leaf, RFC 2253 format) of the schemes that verified.
    /// <para>验签通过方案的签名证书主题（叶子，RFC 2253 格式）。</para></summary>
    public IReadOnlyList<string> SignerSubjects { get; init; } = [];

    /// <summary>Human-readable problems per failed scheme; empty when everything verified.
    /// <para>失败方案的可读问题；全部通过时为空。</para></summary>
    public IReadOnlyList<string> Problems { get; init; } = [];

    /// <summary>The v3 signer's declared minimum SDK, when present. <para>v3 签名者声明的最低 SDK（存在时）。</para></summary>
    public int? V3MinSdk { get; init; }

    /// <summary>The v3 signer's declared maximum SDK, when present. <para>v3 签名者声明的最高 SDK（存在时）。</para></summary>
    public int? V3MaxSdk { get; init; }

    /// <summary>Whether the v3 block carries the anti-stripping protection attribute.
    /// <para>v3 分块是否携带防剥离保护属性。</para></summary>
    public bool V3HasProtectionAttribute { get; init; }
}

/// <summary>
/// Verifies the v1 (JAR), v2 and v3 APK signature schemes of a file: recomputes digests, validates
/// the recorded signatures against the signer certificates and reports one status per scheme.
/// <para>校验文件的 v1（JAR）、v2 与 v3 APK 签名方案：重算摘要、对照签名者证书验证已记录的签名，
/// 并按方案报告状态。</para></summary>
/// <remarks>
/// v1 verification recomputes the SHA-256 digest of every entry listed in <c>META-INF/MANIFEST.MF</c>
/// and checks the detached CMS over the <c>.SF</c> file. v2/v3 verification recomputes the chunked
/// content digest with the same pipeline the signer uses and validates the recorded signature with
/// the embedded certificate. DSA-based signatures and ZIP64 containers are reported as
/// <see cref="ApkSchemeStatus.Unsupported"/> rather than failing.
/// <para>v1 验签重算 <c>META-INF/MANIFEST.MF</c> 所列每个条目的 SHA-256 摘要并校验对 <c>.SF</c> 的分离式 CMS。
/// v2/v3 验签用与签名器相同的分块摘要管线重算内容摘要，并用内嵌证书验证已记录签名。DSA 签名与 ZIP64
/// 容器按 <see cref="ApkSchemeStatus.Unsupported"/> 报告而不视为失败。</para>
/// </remarks>
public static class ApkSignatureVerifier
{
    /// <summary>Verifies the signature schemes of an APK file.
    /// <para>校验 APK 文件的签名方案。</para></summary>
    /// <param name="path">Existing APK path; the file is only read. <para>已存在的 APK 路径；文件只读。</para></param>
    /// <param name="cancellationToken">Cancels between entry hashing passes.
    /// <para>在逐条目哈希之间取消。</para></param>
    /// <returns>The per-scheme result. <para>逐方案结果。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null or white space.
    /// <para><paramref name="path"/> 为 null 或空白。</para></exception>
    /// <exception cref="FileNotFoundException">The file does not exist. <para>文件不存在。</para></exception>
    /// <exception cref="InvalidDataException">The file is not a readable ZIP container or is ZIP64.
    /// <para>文件不是可读的 ZIP 容器，或为 ZIP64。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.
    /// <para><paramref name="cancellationToken"/> 被取消。</para></exception>
    public static ApkSignatureVerification Verify(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path)) throw new FileNotFoundException($"APK file not found: {path}", path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        return Verify(stream, cancellationToken);
    }

    /// <summary>Verifies the signature schemes of an APK stream.
    /// <para>校验 APK 流的签名方案。</para></summary>
    /// <param name="stream">Readable, seekable APK stream; it is repositioned but not closed.
    /// <para>可读、可定位的 APK 流；只重定位、不关闭。</para></param>
    /// <param name="cancellationToken">Cancels between entry hashing passes.
    /// <para>在逐条目哈希之间取消。</para></param>
    /// <returns>The per-scheme result. <para>逐方案结果。</para></returns>
    public static ApkSignatureVerification Verify(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        cancellationToken.ThrowIfCancellationRequested();
        var problems = new List<string>();
        var subjects = new List<string>();

        var (v2, v3, v3MinSdk, v3MaxSdk, v3Protected) = VerifyBlockSchemes(stream, problems, subjects, cancellationToken);
        var v1 = VerifyV1Scheme(stream, problems, subjects, cancellationToken);
        return new ApkSignatureVerification
        {
            V1 = v1,
            V2 = v2,
            V3 = v3,
            V3MinSdk = v3MinSdk,
            V3MaxSdk = v3MaxSdk,
            V3HasProtectionAttribute = v3Protected,
            SignerSubjects = subjects,
            Problems = problems
        };
    }

    /// <summary>Verifies the v2 and v3 signing-block schemes and appends per-scheme problems.
    /// <para>校验 v2/v3 签名块方案并按方案追加问题。</para></summary>
    private static (ApkSchemeStatus V2, ApkSchemeStatus V3, int? V3MinSdk, int? V3MaxSdk, bool V3Protected) VerifyBlockSchemes(
        Stream stream, List<string> problems, List<string> subjects, CancellationToken cancellationToken)
    {
        ApkZipSections sections;
        try
        {
            sections = ApkZipSections.Locate(stream);
        }
        catch (InvalidDataException ex)
        {
            problems.Add($"zip layout: {ex.Message}");
            return (ApkSchemeStatus.NotPresent, ApkSchemeStatus.NotPresent, null, null, false);
        }

        (long Offset, byte[] Contents)? block;
        try
        {
            block = ApkSigningBlock.Find(stream, sections);
        }
        catch (InvalidDataException ex)
        {
            problems.Add($"signing block: {ex.Message}");
            return (ApkSchemeStatus.NotPresent, ApkSchemeStatus.NotPresent, null, null, false);
        }
        if (block is null)
        {
            return (ApkSchemeStatus.NotPresent, ApkSchemeStatus.NotPresent, null, null, false);
        }

        var pairs = ApkSigningBlock.ParsePairs(block.Value.Contents);
        var v2Value = pairs.FirstOrDefault(p => p.Id == ApkSignatureConstants.V2BlockId).Value;
        var v3Value = pairs.FirstOrDefault(p => p.Id == ApkSignatureConstants.V3BlockId).Value;
        var eocd = new byte[sections.EocdSize];
        stream.Seek(sections.EocdOffset, SeekOrigin.Begin);
        stream.ReadExactly(eocd);

        List<ApkV2V3SignatureReader.BlockSigner> v3Signers = [];
        if (v3Value is not null)
        {
            try
            {
                v3Signers = ApkV2V3SignatureReader.ParseSigners(v3Value, v3: true);
            }
            catch (InvalidDataException)
            {
                v3Signers = [];
            }
        }
        var firstV3 = v3Signers.FirstOrDefault();

        var v2 = v2Value is not null
            ? VerifyBlockSigners(stream, v2Value, v3: false, sections, block.Value.Offset, eocd, problems, subjects, "v2")
            : ApkSchemeStatus.NotPresent;
        var v3 = v3Value is not null
            ? VerifyBlockSigners(stream, v3Value, v3: true, sections, block.Value.Offset, eocd, problems, subjects, "v3")
            : ApkSchemeStatus.NotPresent;
        cancellationToken.ThrowIfCancellationRequested();
        return (v2, v3, firstV3?.MinSdk, firstV3?.MaxSdk, firstV3?.HasProtectionAttribute ?? false);
    }

    /// <summary>Verifies every signer recorded in one v2/v3 block value.
    /// <para>校验一个 v2/v3 分块值里记录的每个签名者。</para></summary>
    private static ApkSchemeStatus VerifyBlockSigners(
        Stream stream, byte[] blockValue, bool v3, in ApkZipSections sections, long blockOffset, byte[] eocd,
        List<string> problems, List<string> subjects, string scheme)
    {
        List<ApkV2V3SignatureReader.BlockSigner> signers;
        try
        {
            signers = ApkV2V3SignatureReader.ParseSigners(blockValue, v3);
        }
        catch (InvalidDataException ex)
        {
            problems.Add($"{scheme}: malformed signer structure ({ex.Message})");
            return ApkSchemeStatus.Invalid;
        }
        if (signers.Count == 0)
        {
            problems.Add($"{scheme}: no signer entries");
            return ApkSchemeStatus.Invalid;
        }

        var anyValid = false;
        var anyUnsupported = false;
        foreach (var signer in signers)
        {
            var status = VerifyBlockSigner(stream, signer, v3, sections, blockOffset, eocd, problems, subjects, scheme);
            if (status == ApkSchemeStatus.Valid) anyValid = true;
            if (status == ApkSchemeStatus.Unsupported) anyUnsupported = true;
        }
        if (anyValid) return ApkSchemeStatus.Valid;
        return anyUnsupported ? ApkSchemeStatus.Unsupported : ApkSchemeStatus.Invalid;
    }

    /// <summary>Verifies one v2/v3 signer: signature over signed-data, recorded digest against the recomputed content digest and the public-key match.
    /// <para>校验单个 v2/v3 签名者：对签名数据的签名、记录摘要与重算内容摘要的一致性以及公钥匹配。</para></summary>
    private static ApkSchemeStatus VerifyBlockSigner(
        Stream stream, ApkV2V3SignatureReader.BlockSigner signer, bool v3,
        in ApkZipSections sections, long blockOffset, byte[] eocd, List<string> problems, List<string> subjects, string scheme)
    {
        if (signer.Signatures.Count == 0)
        {
            problems.Add($"{scheme}: signer without signatures");
            return ApkSchemeStatus.Invalid;
        }
        X509Certificate2? leaf = null;
        if (signer.Certificates.Count > 0)
        {
            try
            {
                leaf = X509CertificateLoader.LoadCertificate(signer.Certificates[0]);
            }
            catch (CryptographicException)
            {
                problems.Add($"{scheme}: signer certificate cannot be parsed");
                return ApkSchemeStatus.Invalid;
            }
        }
        var verifiedAny = false;
        var unsupported = false;
        foreach (var (algorithmId, signature) in signer.Signatures)
        {
            if (!Enum.IsDefined(typeof(ApkSignatureConstants.SignatureAlgorithm), algorithmId))
            {
                unsupported = true;
                continue;
            }
            var algorithm = (ApkSignatureConstants.SignatureAlgorithm)algorithmId;
            if (algorithm == ApkSignatureConstants.SignatureAlgorithm.DsaSha256 || leaf is null)
            {
                unsupported = true;
                continue;
            }
            if (!VerifyBlockSignature(leaf, algorithm, signer.SignedData, signature))
            {
                problems.Add($"{scheme}: signature does not verify against the signer certificate (algorithm 0x{algorithmId:x4})");
                continue;
            }
            (uint AlgorithmId, byte[] Digest)? expected = null;
            foreach (var digest in signer.Digests)
            {
                if (digest.AlgorithmId == algorithmId)
                {
                    expected = digest;
                    break;
                }
            }
            if (expected is null)
            {
                problems.Add($"{scheme}: no recorded digest for algorithm 0x{algorithmId:x4}");
                continue;
            }
            var computed = ApkContentDigest.ComputeApkDigest(
                stream,
                blockOffset,
                sections.CentralDirectoryOffset,
                blockOffset,
                sections.CentralDirectorySize,
                eocd,
                ApkSignatureConstants.HashNameFor(algorithm));
            if (!computed.AsSpan().SequenceEqual(expected.Value.Digest))
            {
                problems.Add($"{scheme}: recomputed content digest does not match the recorded digest (algorithm 0x{algorithmId:x4})");
                continue;
            }
            if (signer.PublicKey.Length > 0
                && !leaf.PublicKey.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(signer.PublicKey))
            {
                problems.Add($"{scheme}: signer certificate public key does not match the recorded public key");
                continue;
            }
            verifiedAny = true;
            subjects.Add(leaf.Subject);
        }
        return verifiedAny ? ApkSchemeStatus.Valid : unsupported ? ApkSchemeStatus.Unsupported : ApkSchemeStatus.Invalid;
    }

    /// <summary>Validates one v2/v3 signature over the signed-data using the certificate's public key.
    /// <para>用证书公钥验证一条对签名数据的 v2/v3 签名。</para></summary>
    private static bool VerifyBlockSignature(X509Certificate2 certificate, ApkSignatureConstants.SignatureAlgorithm algorithm, byte[] signedData, byte[] signature)
    {
        var hashName = new HashAlgorithmName(ApkSignatureConstants.HashNameFor(algorithm));
        try
        {
            return algorithm switch
            {
                ApkSignatureConstants.SignatureAlgorithm.RsaPssSha256
                    or ApkSignatureConstants.SignatureAlgorithm.RsaPssSha512
                    => certificate.GetRSAPublicKey()?.VerifyData(signedData, signature, hashName, RSASignaturePadding.Pss) == true,
                ApkSignatureConstants.SignatureAlgorithm.RsaPkcs1Sha256
                    or ApkSignatureConstants.SignatureAlgorithm.RsaPkcs1Sha512
                    => certificate.GetRSAPublicKey()?.VerifyData(signedData, signature, hashName, RSASignaturePadding.Pkcs1) == true,
                ApkSignatureConstants.SignatureAlgorithm.EcdsaSha256
                    or ApkSignatureConstants.SignatureAlgorithm.EcdsaSha512
                    => certificate.GetECDsaPublicKey()?.VerifyData(signedData, signature, hashName, DSASignatureFormat.Rfc3279DerSequence) == true,
                _ => false
            };
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>Verifies the v1 (JAR) scheme: entry digests against MANIFEST.MF, section digests against the .SF file and the detached CMS signature.
    /// <para>校验 v1（JAR）方案：逐条目摘要对照 MANIFEST.MF、段摘要对照 .SF，以及分离式 CMS 签名。</para></summary>
    private static ApkSchemeStatus VerifyV1Scheme(Stream stream, List<string> problems, List<string> subjects, CancellationToken cancellationToken)
    {
        byte[]? manifestBytes = null;
        string? signatureFileEntry = null;
        string? cmsEntry = null;
        using (var archive = ZipContainer.OpenRead(stream, leaveOpen: true))
        {
            var manifestEntry = archive.Entries.FirstOrDefault(e => string.Equals(e.FullName, "META-INF/MANIFEST.MF", StringComparison.OrdinalIgnoreCase));
            if (manifestEntry is null)
            {
                return ApkSchemeStatus.NotPresent;
            }
            using (var input = manifestEntry.Open())
            using (var buffer = new MemoryStream())
            {
                input.CopyTo(buffer);
                manifestBytes = buffer.ToArray();
            }
            var signatureFile = archive.Entries.FirstOrDefault(e => ApkEntryStripper.IsV1SignatureEntry(e.FullName)
                && e.FullName.EndsWith(".SF", StringComparison.OrdinalIgnoreCase));
            if (signatureFile is null)
            {
                problems.Add("v1: MANIFEST.MF present without a .SF signature file");
                return ApkSchemeStatus.Invalid;
            }
            signatureFileEntry = signatureFile.FullName;
            var certificateBlock = archive.Entries.FirstOrDefault(e => ApkEntryStripper.IsV1SignatureEntry(e.FullName)
                && Path.GetExtension(e.FullName) is ".RSA" or ".EC" or ".DSA");
            cmsEntry = certificateBlock?.FullName;
        }

        byte[] sfBytes;
        using (var archive = ZipContainer.OpenRead(stream, leaveOpen: true))
        {
            var entry = archive.Entries.First(e => string.Equals(e.FullName, signatureFileEntry, StringComparison.OrdinalIgnoreCase));
            using var input = entry.Open();
            using var buffer = new MemoryStream();
            input.CopyTo(buffer);
            sfBytes = buffer.ToArray();
        }

        var manifest = ApkV1ManifestFile.Parse(manifestBytes);
        var signatureFileSections = ApkV1ManifestFile.Parse(sfBytes);
        IReadOnlyDictionary<string, string> mainAttributes = signatureFileSections.FirstOrDefault(s => s.Name is null)?.Attributes
            ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var manifestDigest = Convert.FromBase64String(mainAttributes.GetValueOrDefault("SHA-256-Digest-Manifest", string.Empty));
        if (manifestDigest.Length > 0)
        {
            var computed = Sha256.ComputeHash(manifestBytes);
            if (!computed.AsSpan().SequenceEqual(manifestDigest))
            {
                problems.Add("v1: MANIFEST.MF does not match the .SF whole-file digest");
            }
        }

        var entryDigests = manifest.Where(s => s.Name is not null).ToDictionary(s => s.Name!, s => s.Attributes.GetValueOrDefault("SHA-256-Digest", string.Empty));
        var verifiedEntries = 0;
        var failedEntries = 0;
        using (var archive = ZipContainer.OpenRead(stream, leaveOpen: true))
        {
            foreach (var zipEntry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (ApkEntryStripper.IsV1SignatureEntry(zipEntry.FullName)) continue;
                if (!entryDigests.TryGetValue(zipEntry.FullName, out var expectedBase64)) continue;
                var expected = Convert.FromBase64String(expectedBase64);
                // Entry digests go through the repository hash facade; the stream overload hashes incrementally so a
                // large uncompressed APK entry never lands in memory whole.
                byte[] computed;
                try
                {
                    using var input = zipEntry.Open();
                    computed = Sha256.ComputeHash(input);
                }
                catch (InvalidDataException ex)
                {
                    failedEntries++;
                    problems.Add($"v1: entry data cannot be read for {zipEntry.FullName} ({ex.Message})");
                    continue;
                }
                if (computed.AsSpan().SequenceEqual(expected))
                {
                    verifiedEntries++;
                }
                else
                {
                    failedEntries++;
                    problems.Add($"v1: entry digest mismatch for {zipEntry.FullName}");
                }
            }
        }

        if (cmsEntry is null)
        {
            problems.Add("v1: no CMS signature block (.RSA/.EC/.DSA) found for the .SF file");
            return ApkSchemeStatus.Invalid;
        }
        if (!VerifyV1Cms(stream, cmsEntry, sfBytes, subjects, problems))
        {
            return ApkSchemeStatus.Invalid;
        }
        return failedEntries == 0 ? ApkSchemeStatus.Valid : ApkSchemeStatus.Invalid;
    }

    /// <summary>Checks the detached CMS signature over the .SF bytes and collects the signer subject.
    /// <para>校验对 .SF 字节的分离式 CMS 签名并收集签名者主题。</para></summary>
    private static bool VerifyV1Cms(Stream stream, string cmsEntryName, byte[] sfBytes, List<string> subjects, List<string> problems)
    {
        byte[] cmsBytes;
        using (var archive = ZipContainer.OpenRead(stream, leaveOpen: true))
        {
            var entry = archive.Entries.First(e => string.Equals(e.FullName, cmsEntryName, StringComparison.OrdinalIgnoreCase));
            using var input = entry.Open();
            using var buffer = new MemoryStream();
            input.CopyTo(buffer);
            cmsBytes = buffer.ToArray();
        }
        try
        {
            var signedCms = new SignedCms(new ContentInfo(sfBytes), detached: true);
            signedCms.Decode(cmsBytes);
            signedCms.CheckSignature(verifySignatureOnly: true);
            var certificate = signedCms.SignerInfos.Count > 0
                ? signedCms.SignerInfos[0].Certificate ?? signedCms.Certificates[0]
                : signedCms.Certificates[0];
            subjects.Add(certificate.Subject);
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or InvalidOperationException or IndexOutOfRangeException)
        {
            problems.Add($"v1: CMS signature check failed ({ex.Message})");
            return false;
        }
    }
}