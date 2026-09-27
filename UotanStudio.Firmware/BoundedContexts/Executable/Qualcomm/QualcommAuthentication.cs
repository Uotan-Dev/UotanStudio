using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace UotanStudio.Firmware.BoundedContexts.Executable.Qualcomm;

internal static class QualcommAuthentication
{
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
            var hashes = layout?.Version is 6 or 7 ? [HashAlgorithmName.SHA384] : new[] { HashAlgorithmName.SHA256, HashAlgorithmName.SHA1 };
            var result = VerifySignature(stream, baseOffset, signedEnd, signature, certs[0],
                layout, authority, hashes, token);
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
                    throw new InvalidDataException("Certificate count exceeds the limit.");
                }

                var remaining = data[offset..];
                AsnDecoder.ReadEncodedValue(remaining, AsnEncodingRules.DER, out _, out _, out var length);
                certificates.Add(X509CertificateLoader.LoadCertificate(remaining[..length]));
                offset += length;
            }

            if (certificates.Count == 0)
            {
                throw new InvalidDataException("Certificate package is empty.");
            }

            return true;
        }
        catch (Exception exception) when (exception is AsnContentException or CryptographicException or InvalidDataException)
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

    private static (bool Valid, QualcommVerificationStatus Status, string? Algorithm, string Error)
        VerifySignature(Stream stream, long baseOffset, long signedEnd, byte[] signature,
            X509Certificate2 leaf, QualcommMbnLayout? layout, string authority,
            HashAlgorithmName[] candidates, CancellationToken token)
    {
        using var rsa = leaf.GetRSAPublicKey();
        using var ecdsa = leaf.GetECDsaPublicKey();
        if (rsa is null && ecdsa is null)
        {
            return (false, QualcommVerificationStatus.Unsupported, null, "Unsupported leaf public key.");
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
                        : new[] { (mbn.Version == 7 ? 24 : 8, 4), (mbn.Version == 7 ? 28 : 12, 4),
                            (mbn.QualcommMetadataOffset, mbn.QualcommMetadataLength) };
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

            if (rsa is not null && signature.Length == rsa.KeySize / 8)
            {
                foreach (var padding in new[] { RSASignaturePadding.Pss, RSASignaturePadding.Pkcs1 })
                {
                    try
                    {
                        if (rsa.VerifyHash(digest, signature, hashName, padding))
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
                    if (ecdsa.VerifyHash(digest, signature, DSASignatureFormat.Rfc3279DerSequence))
                    {
                        return (true, QualcommVerificationStatus.Valid, $"ECDSA-{hashName.Name}", "");
                    }
                }
                catch (CryptographicException)
                {
                    continue;
                }
            }
        }

        return layout?.Version is 3 or 5
            ? (false, QualcommVerificationStatus.Unsupported, null,
                "Standard image signature mismatch; legacy Qualcomm HMAC-signature variants are not verified.")
            : (false, QualcommVerificationStatus.Invalid, null, "Image signature mismatch.");
    }
}