using System.Buffers.Binary;
using System.Formats.Asn1;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace UotanStudio.Firmware.Executable.Qualcomm;

internal static class QualcommAuthentication
{
    private const int MaxSignaturePaddingBytes = 128;

    internal static (QualcommAuthorityResult Qualcomm, QualcommAuthorityResult Oem,
        QualcommVerificationStatus MetadataRootHashStatus,
        IReadOnlyList<QualcommCertificateInfo> Certificates, IReadOnlyList<string> Diagnostics)
        Verify(Stream stream, long offset, QualcommMbnLayout layout, QualcommAnalysisOptions options, CancellationToken token)
    {
        var certificates = new List<QualcommCertificateInfo>();
        var diagnostics = new List<string>();
        var qti = VerifyOne(stream, offset, layout.QualcommSignatureOffset, layout.QualcommSignatureLength,
            layout.QualcommCertificateOffset, layout.QualcommCertificateLength,
            offset + layout.HashOffset + layout.HashLength, layout, "QTI", null,
            options, certificates, diagnostics, token, out _);
        var oem = VerifyOne(stream, offset, layout.OemSignatureOffset, layout.OemSignatureLength,
            layout.OemCertificateOffset, layout.OemCertificateLength,
            offset + layout.HashOffset + layout.HashLength, layout, "OEM", layout.OemRootSlot,
            options, certificates, diagnostics, token, out var metadataStatus);
        return (qti, oem, metadataStatus, certificates.AsReadOnly(), diagnostics.AsReadOnly());
    }

    internal static (QualcommAuthorityResult Result, IReadOnlyList<QualcommCertificateInfo> Certificates,
        IReadOnlyList<string> Diagnostics) VerifySbl(Stream stream, long signatureOffset, int signatureLength,
        long certificateOffset, int certificateLength, long signedLength, int? rootSlot,
        QualcommAnalysisOptions options,
        CancellationToken token)
    {
        var certificates = new List<QualcommCertificateInfo>();
        var diagnostics = new List<string>();
        var result = VerifyOne(stream, 0, signatureOffset, signatureLength,
            certificateOffset, certificateLength, signedLength, null,
            "OEM", rootSlot, options, certificates, diagnostics, token, out _);
        return (result, certificates.AsReadOnly(), diagnostics.AsReadOnly());
    }

    private static QualcommAuthorityResult VerifyOne(Stream stream, long baseOffset, long signatureOffset,
        int signatureLength, long certificateOffset, int certificateLength, long signedEnd,
        QualcommMbnLayout? layout, string authority, int? rootSlot,
        QualcommAnalysisOptions options, List<QualcommCertificateInfo> summaries,
        List<string> diagnostics, CancellationToken token, out QualcommVerificationStatus metadataStatus)
    {
        metadataStatus = QualcommVerificationStatus.NotPresent;
        if (certificateLength == 0)
        {
            if (authority == "OEM" && layout?.MetadataRootDigest is not null)
            {
                metadataStatus = QualcommVerificationStatus.Invalid;
                diagnostics.Add("OEM metadata declares a root digest without an embedded certificate.");
            }

            return new()
            {
                SignatureStatus = signatureLength == 0 ? QualcommVerificationStatus.NotPresent : QualcommVerificationStatus.Invalid,
                CertificateChainStatus = QualcommVerificationStatus.NotPresent,
                TrustedRootStatus = options.TrustedRootCertificateSha256.Count == 0
                    && options.TrustedRootCertificateSha384.Count == 0
                    ? QualcommVerificationStatus.NotChecked : QualcommVerificationStatus.NotPresent
            };
        }

        if (certificateLength > options.MaximumCertificateBytes)
        {
            diagnostics.Add($"{authority} certificate package exceeds the configured bound.");
            return new()
            {
                SignatureStatus = QualcommVerificationStatus.NotChecked,
                CertificateChainStatus = QualcommVerificationStatus.Unsupported
            };
        }

        var bytes = new byte[certificateLength];
        QualcommMbnLayout.ReadAt(stream, baseOffset + certificateOffset, bytes, token);
        if (!TryLoadCertificates(bytes, options.MaximumCertificateCount, out var certs, out var error))
        {
            diagnostics.Add($"{authority}: {error}");
            return new()
            {
                SignatureStatus = signatureLength == 0 ? QualcommVerificationStatus.NotPresent : QualcommVerificationStatus.NotChecked,
                CertificateChainStatus = QualcommVerificationStatus.Invalid
            };
        }

        try
        {
            for (var index = 0; index < certs.Count; index++)
            {
                var certificate = certs[index];
                summaries.Add(new(authority, certificate.Subject, certificate.Issuer,
                    Convert.ToHexString(SHA256.HashData(certificate.RawData)))
                {
                    Index = index,
                    SerialNumber = certificate.SerialNumber,
                    NotBefore = certificate.NotBefore.ToUniversalTime(),
                    NotAfter = certificate.NotAfter.ToUniversalTime(),
                    SignatureAlgorithmOid = certificate.SignatureAlgorithm.Value ?? "",
                    PublicKeyAlgorithmOid = certificate.PublicKey.Oid.Value ?? "",
                    Sha384 = Convert.ToHexString(SHA384.HashData(certificate.RawData)),
                    Attributes = QualcommMetadataReader.ReadAttributes(certificate.SubjectName.RawData)
                });
            }

            var chain = VerifyChain(certs,
                authority == "QTI" ? layout?.QualcommRootSlot : rootSlot, token);
            if (!chain.Valid)
            {
                diagnostics.Add($"{authority}: embedded certificate chain did not validate ({chain.Error}).");
            }

            if (authority == "OEM" && layout?.MetadataRootDigest is { } claimed)
            {
                metadataStatus = chain.Valid
                    ? VerifyMetadataRootDigest(claimed, layout.Value.MetadataRootAlgorithm, chain.RootRaw!)
                    : QualcommVerificationStatus.NotChecked;
                if (metadataStatus is QualcommVerificationStatus.Invalid or QualcommVerificationStatus.Unsupported)
                {
                    diagnostics.Add($"{authority}: signed metadata root-certificate digest is {metadataStatus}.");
                }
            }

            var rootSha384 = chain.Valid ? Convert.ToHexString(SHA384.HashData(chain.RootRaw!)) : null;
            var trust = options.TrustedRootCertificateSha256.Count == 0
                && options.TrustedRootCertificateSha384.Count == 0
                ? QualcommVerificationStatus.NotChecked
                : !chain.Valid ? QualcommVerificationStatus.NotChecked
                : metadataStatus == QualcommVerificationStatus.Invalid ? QualcommVerificationStatus.Invalid
                : options.TrustedRootCertificateSha256.Contains(chain.RootSha256!,
                    StringComparer.OrdinalIgnoreCase)
                    || options.TrustedRootCertificateSha384.Contains(rootSha384!, StringComparer.OrdinalIgnoreCase)
                    ? QualcommVerificationStatus.Valid : QualcommVerificationStatus.Invalid;

            if (signatureLength == 0)
            {
                return new()
                {
                    CertificateChainStatus = chain.Valid ? QualcommVerificationStatus.Valid :
                        chain.Ambiguous ? QualcommVerificationStatus.Unsupported : QualcommVerificationStatus.Invalid,
                    TrustedRootStatus = trust,
                    RootCertificateSha256 = chain.RootSha256,
                    RootCertificateSha384 = rootSha384
                };
            }

            var signature = new byte[signatureLength];
            QualcommMbnLayout.ReadAt(stream, baseOffset + signatureOffset, signature, token);
            var declaration = QualcommMetadataReader
                .ReadOrganizationalUnitHashDeclaration(certs[0].SubjectName.RawData);
            var result = VerifySignature(stream, baseOffset, signedEnd, signature, certs[0],
                layout, authority, BuildHashCandidates(layout?.Version, declaration),
                declaration.IsConflicting ? declaration.Names : null, token);
            if (!result.Valid)
            {
                diagnostics.Add($"{authority}: {result.Error}");
            }

            return new()
            {
                CertificateChainStatus = chain.Valid ? QualcommVerificationStatus.Valid :
                    chain.Ambiguous ? QualcommVerificationStatus.Unsupported : QualcommVerificationStatus.Invalid,
                TrustedRootStatus = trust,
                SignatureStatus = result.Status,
                SignatureAlgorithm = result.Algorithm,
                RootCertificateSha256 = chain.RootSha256,
                RootCertificateSha384 = rootSha384
            };
        }
        finally
        {
            foreach (var certificate in certs)
            {
                certificate.Dispose();
            }
        }
    }

    private static bool TryLoadCertificates(ReadOnlySpan<byte> data, int maximumCount,
        out List<X509Certificate2> certificates, out string error)
    {
        certificates = [];
        error = "";
        var offset = 0;
        try
        {
            while (offset < data.Length)
            {
                while (offset < data.Length && data[offset] is 0 or 0xff)
                {
                    offset++;
                }

                if (offset == data.Length)
                {
                    break;
                }

                if (certificates.Count == maximumCount)
                {
                    throw new ExecutableCorruptedException("Certificate count exceeds the limit.");
                }

                var remaining = data[offset..];
                AsnDecoder.ReadEncodedValue(remaining, AsnEncodingRules.DER, out _, out _, out var length);
                certificates.Add(X509CertificateLoader.LoadCertificate(remaining[..length]));
                offset += length;
            }

            if (certificates.Count == 0)
            {
                throw new ExecutableCorruptedException("Certificate package is empty.");
            }

            return true;
        }
        catch (Exception exception) when (exception is AsnContentException or CryptographicException or InvalidDataException or ExecutableException)
        {
            error = exception.Message;
            foreach (var certificate in certificates)
            {
                certificate.Dispose();
            }

            certificates.Clear();
            return false;
        }
    }

    private static (bool Valid, string? RootSha256, byte[]? RootRaw, bool Ambiguous, string Error) VerifyChain(
        IReadOnlyList<X509Certificate2> certificates, int? rootSlot, CancellationToken token)
    {
        var roots = certificates.Where(cert =>
            cert.SubjectName.RawData.AsSpan().SequenceEqual(cert.IssuerName.RawData)).ToArray();
        if (rootSlot is int slot && (slot < 0 || slot >= roots.Length))
        {
            return (false, null, null, false, "declared root slot exceeds the embedded root count");
        }

        var candidates = rootSlot.HasValue ? [roots[rootSlot.Value]] : roots;
        var validRoots = new List<X509Certificate2>();
        foreach (var root in candidates)
        {
            token.ThrowIfCancellationRequested();
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
            chain.ChainPolicy.CustomTrustStore.Add(root);
            foreach (var certificate in certificates.Skip(1))
            {
                if (!ReferenceEquals(certificate, root))
                {
                    chain.ChainPolicy.ExtraStore.Add(certificate);
                }
            }

            if (chain.Build(certificates[0])
                && chain.ChainElements.Count > 0
                && chain.ChainElements[^1].Certificate.RawData.AsSpan().SequenceEqual(root.RawData))
            {
                validRoots.Add(root);
            }
        }

        if (validRoots.Count == 1)
        {
            var raw = validRoots[0].RawData;
            return (true, Convert.ToHexString(SHA256.HashData(raw)), raw, false, "");
        }

        return (false, null, null, validRoots.Count > 1, validRoots.Count > 1
            ? "multiple valid roots without an authenticated selected slot"
            : roots.Length == 0 ? "no embedded self-issued root" :
                "invalid path, signature, certificate constraints or validity period");
    }

    private static QualcommVerificationStatus VerifyMetadataRootDigest(
        ReadOnlySpan<byte> claimed, uint algorithm, byte[] root)
    {
        byte[] digest = algorithm switch
        {
            1 => SHA1.HashData(root),
            2 => SHA256.HashData(root),
            3 => SHA384.HashData(root),
            4 => SHA512.HashData(root),
            _ => []
        };
        if (digest.Length == 0)
        {
            return QualcommVerificationStatus.Unsupported;
        }

        if (!CryptographicOperations.FixedTimeEquals(claimed[..digest.Length], digest)
            || claimed[digest.Length..].IndexOfAnyExcept((byte)0) >= 0)
        {
            return QualcommVerificationStatus.Invalid;
        }

        return QualcommVerificationStatus.Valid;
    }

    /// <summary>
    /// Orders the digest algorithms attempted for one image signature.
    /// A hash declared by exactly one distinct certificate organizational-unit value is attempted first for
    /// every MBN version, and a conflicting declaration contributes no candidate; MBN v6 and v7 still keep
    /// SHA-384 ahead of the defaults, so a declaration can only reorder candidates and can never widen the
    /// result of a verification.
    /// <para>排列单个镜像签名依次尝试的摘要算法。当证书 organizational unit 恰好以一种互不冲突的算法
    /// 声明时，所有 MBN 版本都会优先尝试该算法，声明冲突时不产生任何候选；MBN v6 与 v7 仍把
    /// SHA-384 排在默认序列之前，因此声明只能调整候选顺序，绝不能扩大验证结论。</para>
    /// </summary>
    /// <param name="version">MBN header version, or <see langword="null"/> for layouts without an MBN header.
    /// <para>MBN 头版本；无 MBN 头的布局为空。</para></param>
    /// <param name="declaration">Organizational-unit hash declarations.
    /// <para>organizational unit 的哈希声明。</para></param>
    /// <returns>Distinct candidate digests in attempt order. <para>按尝试顺序排列的去重候选摘要。</para></returns>
    private static HashAlgorithmName[] BuildHashCandidates(uint? version, QualcommOuHashDeclaration declaration)
    {
        var candidates = new List<HashAlgorithmName>(5);
        if (declaration.Algorithm is { } declared)
        {
            candidates.Add(declared);
        }

        if (version is 6 or 7)
        {
            candidates.Add(HashAlgorithmName.SHA384);
        }

        foreach (var candidate in new[]
                 {
                     HashAlgorithmName.SHA256, HashAlgorithmName.SHA384, HashAlgorithmName.SHA512,
                     HashAlgorithmName.SHA1
                 })
        {
            if (!candidates.Contains(candidate))
            {
                candidates.Add(candidate);
            }
        }

        return [.. candidates];
    }

    private static (bool Valid, QualcommVerificationStatus Status, string? Algorithm, string Error)
        VerifySignature(Stream stream, long baseOffset, long signedEnd, byte[] signature,
            X509Certificate2 leaf, QualcommMbnLayout? layout, string authority,
            HashAlgorithmName[] candidates, IReadOnlyList<string>? conflictingDeclarations,
            CancellationToken token)
    {
        using var rsa = leaf.GetRSAPublicKey();
        using var ecdsa = leaf.GetECDsaPublicKey();
        if (rsa is null && ecdsa is null)
        {
            return (false, QualcommVerificationStatus.Unsupported, null, "Unsupported leaf public key.");
        }

        var legacyApplicable = false;
        if (rsa is not null && layout?.Version is 3 or 5)
        {
            var legacy = VerifyLegacySignature(stream, baseOffset, signedEnd, signature, leaf, layout.Value,
                authority, token);
            if (legacy.Valid)
            {
                return (true, QualcommVerificationStatus.Valid, "RSA-QCOM-HMAC-SHA256", "");
            }

            legacyApplicable = legacy.Applicable;
        }

        byte[] rsaSignature = [];
        byte[] ecdsaSignature = [];
        if (rsa is not null)
        {
            if (!TryNormalizeRsaSignature(signature, rsa.KeySize, MaxSignaturePaddingBytes, out rsaSignature))
            {
                return (false, QualcommVerificationStatus.Invalid, null,
                    $"RSA signature length is incompatible with the {rsa.KeySize}-bit key: only 0x00 or 0xFF "
                    + $"filler of at most {MaxSignaturePaddingBytes} bytes is tolerated at one end.");
            }
        }
        else if (!TryTrimEcdsaDerSignature(signature, out ecdsaSignature))
        {
            return (false, QualcommVerificationStatus.Invalid, null,
                $"ECDSA signature is not a DER sequence followed by at most {MaxSignaturePaddingBytes} bytes "
                + "of 0x00 or 0xFF filler.");
        }

        Span<byte> zeros = stackalloc byte[4096];
        zeros.Clear();
        foreach (var hashName in candidates)
        {
            token.ThrowIfCancellationRequested();
            byte[] digest;
            using (var hash = IncrementalHash.CreateHash(hashName))
            {
                if (layout is null || layout.Value.Version == 3)
                {
                    QualcommImageAnalyzer.AppendRange(hash, stream, baseOffset, signedEnd - baseOffset, token);
                }
                else
                {
                    var mbn = layout.Value;
                    var masks = authority == "QTI"
                        ? new[] { (mbn.Version == 7 ? 32 : 28, 4), (mbn.Version == 7 ? 36 : 36, 4),
                            (mbn.OemMetadataOffset, mbn.OemMetadataLength) }
                        : [ (mbn.Version == 7 ? 24 : 8, 4), (mbn.Version == 7 ? 28 : 12, 4),
                            (mbn.QualcommMetadataOffset, mbn.QualcommMetadataLength) ];
                    var cursor = baseOffset;
                    foreach (var (offset, length) in masks.Where(mask => mask.Item2 > 0).OrderBy(mask => mask.Item1))
                    {
                        var start = baseOffset + offset;
                        if (start < cursor || start + length > signedEnd)
                        {
                            return (false, QualcommVerificationStatus.Invalid, null, "Signed-data mask is out of range.");
                        }

                        QualcommImageAnalyzer.AppendRange(hash, stream, cursor, start - cursor, token);
                        for (var remaining = length; remaining > 0;)
                        {
                            var chunk = Math.Min(remaining, zeros.Length);
                            hash.AppendData(zeros[..chunk]);
                            remaining -= chunk;
                        }

                        cursor = start + length;
                    }

                    QualcommImageAnalyzer.AppendRange(hash, stream, cursor, signedEnd - cursor, token);
                }

                digest = hash.GetHashAndReset();
            }

            if (rsa is not null)
            {
                foreach (var padding in new[] { RSASignaturePadding.Pss, RSASignaturePadding.Pkcs1 })
                {
                    try
                    {
                        if (rsa.VerifyHash(digest, rsaSignature, hashName, padding))
                        {
                            return (true, QualcommVerificationStatus.Valid, $"RSA-{padding.Mode}-{hashName.Name}", "");
                        }
                    }
                    catch (CryptographicException)
                    {
                        continue;
                    }
                }
            }
            else if (ecdsa is not null)
            {
                try
                {
                    if (ecdsa.VerifyHash(digest, ecdsaSignature, DSASignatureFormat.Rfc3279DerSequence))
                    {
                        return (true, QualcommVerificationStatus.Valid, $"ECDSA-{hashName.Name}", "");
                    }
                }
                catch (Exception exception) when (exception is CryptographicException or AsnContentException)
                {
                    continue;
                }
            }
        }

        if (conflictingDeclarations is not null)
        {
            return (false, QualcommVerificationStatus.Unsupported, null,
                "Image signature mismatch and its organizational-unit hash declarations ("
                + string.Join(", ", conflictingDeclarations)
                + ") conflict; an ambiguous untrusted declaration is not evidence of a bad signature.");
        }

        if (layout?.Version is 3 or 5)
        {
            return legacyApplicable
                ? (false, QualcommVerificationStatus.Invalid, null, "Image signature mismatch, including the legacy Qualcomm HMAC variant.")
                : (false, QualcommVerificationStatus.Unsupported, null,
                    "Standard image signature mismatch; legacy Qualcomm HMAC-signature variants are not verified.");
        }

        return (false, QualcommVerificationStatus.Invalid, null, "Image signature mismatch.");
    }
    private static (bool Valid, bool Applicable) VerifyLegacySignature(Stream stream, long baseOffset, long signedEnd, byte[] signature, X509Certificate2 certificate, QualcommMbnLayout layout, string authority, CancellationToken token)
    {
        if (!TryReadLegacyIdentifiers(certificate, out var softwareId, out var hardwareId)) return (false, false);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendSignedData(hash, stream, baseOffset, signedEnd, layout, authority, token);
        var first = hash.GetHashAndReset();
        Span<byte> identifier = stackalloc byte[8];
        Span<byte> input = stackalloc byte[40];
        BinaryPrimitives.WriteUInt64BigEndian(identifier, softwareId ^ 0x3636363636363636UL);
        identifier.CopyTo(input); first.CopyTo(input[8..]);
        var second = SHA256.HashData(input);
        BinaryPrimitives.WriteUInt64BigEndian(identifier, hardwareId ^ 0x5C5C5C5C5C5C5C5CUL);
        identifier.CopyTo(input); second.CopyTo(input[8..]);
        var third = SHA256.HashData(input);
        using var rsa = certificate.GetRSAPublicKey();
        if (rsa is null || !TryNormalizeRsaSignature(signature, rsa.KeySize, out var normalized)) return (false, true);
        return (VerifyRawPkcs1Digest(rsa, normalized, third), true);
    }

    private static bool TryReadLegacyIdentifiers(X509Certificate2 certificate, out ulong softwareId, out ulong hardwareId)
    {
        softwareId = 0; hardwareId = 0; var hasSoftwareId = false; var hasHardwareId = false;
        foreach (var attribute in QualcommMetadataReader.ReadAttributes(certificate.SubjectName.RawData))
        {
            if (!ulong.TryParse(attribute.Value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value)) continue;
            if (attribute.Name.Equals("SW_ID", StringComparison.OrdinalIgnoreCase)) { softwareId = value; hasSoftwareId = true; }
            else if (attribute.Name.Equals("HW_ID", StringComparison.OrdinalIgnoreCase)) { hardwareId = value; hasHardwareId = true; }
        }
        return hasSoftwareId && hasHardwareId;
    }

    private static void AppendSignedData(IncrementalHash hash, Stream stream, long baseOffset, long signedEnd, QualcommMbnLayout layout, string authority, CancellationToken token)
    {
        Span<byte> zeros = stackalloc byte[4096]; zeros.Clear();
        var masks = authority == "QTI"
            ? new[] { (layout.Version == 7 ? 32 : 28, 4), (layout.Version == 7 ? 36 : 36, 4), (layout.OemMetadataOffset, layout.OemMetadataLength) }
            : [(layout.Version == 7 ? 24 : 8, 4), (layout.Version == 7 ? 28 : 12, 4), (layout.QualcommMetadataOffset, layout.QualcommMetadataLength)];
        var cursor = baseOffset;
        foreach (var (offset, length) in masks.Where(mask => mask.Item2 > 0).OrderBy(mask => mask.Item1))
        {
            var start = baseOffset + offset;
            if (start < cursor || start + length > signedEnd) throw new ExecutableCorruptedException("Signed-data mask is out of range.");
            QualcommImageAnalyzer.AppendRange(hash, stream, cursor, start - cursor, token);
            for (var remaining = length; remaining > 0;) { var chunk = Math.Min(remaining, zeros.Length); hash.AppendData(zeros[..chunk]); remaining -= chunk; }
            cursor = start + length;
        }
        QualcommImageAnalyzer.AppendRange(hash, stream, cursor, signedEnd - cursor, token);
    }

    private static bool TryNormalizeRsaSignature(ReadOnlySpan<byte> signature, int keySize, out byte[] normalized)
        => TryNormalizeRsaSignature(signature, keySize, int.MaxValue, out normalized);

    /// <summary>
    /// Trims at most <paramref name="maximumPaddingBytes"/> bytes of homogeneous 0x00 or 0xFF filler from one
    /// end of an RSA signature so that it matches the key modulus length. A signature shorter than the
    /// modulus is always rejected, mixed filler byte values are rejected, and an excess larger than the bound
    /// is rejected. Rejecting an encoding is an encoding defect, so callers report it rather than a signature mismatch.
    /// <para>从 RSA 签名的一端裁剪最多 <paramref name="maximumPaddingBytes"/> 字节的单一 0x00 或 0xFF 填充，
    /// 使其长度与密钥模数一致。短于模数的签名一律拒绝，填充区域出现其它字节值时拒绝，超出上限的多余长度也
    /// 拒绝。编码被拒绝属于编码缺陷，调用方应报告缺陷而非签名不匹配。</para>
    /// </summary>
    /// <param name="signature">Signature bytes as stored by the container. <para>容器内保存的签名字节。</para></param>
    /// <param name="keySize">Public-key size in bits. <para>公钥位数。</para></param>
    /// <param name="maximumPaddingBytes">Maximum accepted filler length. <para>可接受的填充最大长度。</para></param>
    /// <param name="normalized">Modulus-sized signature bytes, or an empty array on rejection.
    /// <para>与模数等长的签名字节；被拒绝时为空数组。</para></param>
    /// <returns><see langword="true"/> when the encoding was accepted. <para>编码被接受时为 <see langword="true"/>。</para></returns>
    private static bool TryNormalizeRsaSignature(ReadOnlySpan<byte> signature, int keySize,
        int maximumPaddingBytes, out byte[] normalized)
    {
        normalized = [];
        var length = checked((keySize + 7) / 8);
        if (signature.Length == length)
        {
            normalized = signature.ToArray();
            return true;
        }

        if (signature.Length < length || signature.Length - length > maximumPaddingBytes)
        {
            return false;
        }

        if (IsPadding(signature[length..]))
        {
            normalized = signature[..length].ToArray();
            return true;
        }

        if (!IsPadding(signature[..^length]))
        {
            return false;
        }

        normalized = signature[^length..].ToArray();
        return true;
    }

    /// <summary>
    /// Trims at most <see cref="MaxSignaturePaddingBytes"/> bytes of homogeneous 0x00 or 0xFF filler from
    /// the tail of an ECDSA signature and returns the enclosed DER sequence. An exact DER signature passes
    /// through unchanged; malformed ASN.1 yields a rejection rather than an exception, because container
    /// bytes are untrusted input and the strict DER check stays inside <see cref="ECDsa"/>.
    /// <para>从 ECDSA 签名尾部裁剪最多 <see cref="MaxSignaturePaddingBytes"/> 字节的单一 0x00 或 0xFF
    /// 填充，并返回其中的 DER 序列。精确的 DER 签名原样通过；ASN.1 结构错误会被判定为拒绝而不抛出异常，
    /// 因为容器字节属于不可信输入，严格的 DER 检查留在 <see cref="ECDsa"/> 内部。</para>
    /// </summary>
    /// <param name="signature">Signature bytes as stored by the container. <para>容器内保存的签名字节。</para></param>
    /// <param name="trimmed">The DER sequence bytes including its header, or an empty array on rejection.
    /// <para>含头的 DER 序列字节；被拒绝时为空数组。</para></param>
    /// <returns><see langword="true"/> when the encoding was accepted. <para>编码被接受时为 <see langword="true"/>。</para></returns>
    private static bool TryTrimEcdsaDerSignature(ReadOnlySpan<byte> signature, out byte[] trimmed)
    {
        trimmed = [];
        if (signature.IsEmpty || signature[0] != 0x30)
        {
            return false;
        }

        try
        {
            AsnDecoder.ReadEncodedValue(signature, AsnEncodingRules.DER, out _, out _, out var consumed);
            if (consumed > signature.Length
                || signature.Length - consumed > MaxSignaturePaddingBytes
                || !IsPadding(signature[consumed..]))
            {
                return false;
            }

            trimmed = signature[..consumed].ToArray();
            return true;
        }
        catch (AsnContentException)
        {
            return false;
        }
    }

    private static bool VerifyRawPkcs1Digest(RSA rsa, ReadOnlySpan<byte> signature, ReadOnlySpan<byte> digest)
    {
        var parameters = rsa.ExportParameters(false);
        if (parameters.Modulus is null || parameters.Exponent is null) return false;
        var modulus = FromBigEndianUnsigned(parameters.Modulus);
        var exponent = FromBigEndianUnsigned(parameters.Exponent);
        var signatureValue = FromBigEndianUnsigned(signature);
        if (signatureValue.Sign < 0 || signatureValue >= modulus) return false;
        var encoded = ToBigEndianUnsigned(BigInteger.ModPow(signatureValue, exponent, modulus), parameters.Modulus.Length);
        if (encoded.Length < digest.Length + 11 || encoded[0] != 0 || encoded[1] != 1) return false;
        var separator = encoded.Length - digest.Length - 1;
        if (separator < 10 || encoded[separator] != 0) return false;
        for (var index = 2; index < separator; index++) if (encoded[index] != 0xFF) return false;
        return CryptographicOperations.FixedTimeEquals(encoded.AsSpan(separator + 1), digest);
    }

    private static BigInteger FromBigEndianUnsigned(ReadOnlySpan<byte> value)
    {
        var littleEndian = new byte[value.Length + 1];
        for (var index = 0; index < value.Length; index++) littleEndian[index] = value[value.Length - index - 1];
        return new BigInteger(littleEndian);
    }

    private static byte[] ToBigEndianUnsigned(BigInteger value, int length)
    {
        var littleEndian = value.ToByteArray(); var valueLength = littleEndian.Length;
        while (valueLength > 1 && littleEndian[valueLength - 1] == 0) valueLength--;
        if (valueLength > length) return [];
        var result = new byte[length];
        for (var index = 0; index < valueLength; index++) result[length - index - 1] = littleEndian[index];
        return result;
    }

    private static bool IsPadding(ReadOnlySpan<byte> data)
    {
        foreach (var value in data) if (value is not (0 or 0xff)) return false;
        return true;
    }
}