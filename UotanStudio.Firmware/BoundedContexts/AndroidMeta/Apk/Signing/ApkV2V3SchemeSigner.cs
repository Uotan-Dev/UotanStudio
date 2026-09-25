using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace UotanStudio.Firmware.BoundedContexts.AndroidMeta.Apk.Signing;

/// <summary>
/// Builds APK Signature Scheme v2 and v3 signer blocks (length-prefixed little-endian structures)
/// and produces signatures over the signed-data chunk using the user's key.
/// <para>构建 APK 签名方案 v2/v3 签名者块（带长度前缀的小端结构），并使用用户密钥对签名数据分块完成签名。</para>
/// </summary>
public static class ApkV2V3SchemeSigner
{
    /// <summary>SDK range for v3 signers without explicit rotation (apksigner defaults).
    /// <para>无显式密钥轮换时 v3 签名者的 SDK 范围（apksigner 默认值）。</para></summary>
    public const int V3DefaultMinSdk = 28;

    /// <summary>Upper SDK bound of a v3 signer when no explicit range is given (apksigner default).
    /// <para>未显式指定范围时 v3 签名者保护的最高 SDK（apksigner 默认值）。</para></summary>
    public const int V3DefaultMaxSdk = int.MaxValue;

    /// <summary>
    /// Builds the v2 signer block value (length-prefixed signer sequence) for one signer.
    /// <para>为单个签名者构建 v2 签名者块值（带长度前缀的 signer 序列）。</para>
    /// </summary>
    /// <param name="key">User-provided signing key. <para>用户提供的签名密钥。</para></param>
    /// <param name="certificateChain">Certificate chain, leaf first. <para>证书链，叶子在前。</para></param>
    /// <param name="algorithm">Signature algorithm. <para>签名算法。</para></param>
    /// <param name="contentDigest">Content digest already computed for the algorithm's hash.
    /// <para>已按算法哈希计算好的内容摘要。</para></param>
    /// <param name="protectV3">Requires the v3 block on supported platforms.
    /// <para>在受支持的平台要求存在 v3 分块。</para></param>
    /// <returns>A uint32-length-prefixed signer sequence for the v2 block ID value.
    /// <para>用于 v2 分块 ID 值的 uint32 长度前缀 signer 序列。</para></returns>
    /// <exception cref="NotSupportedException">The key type cannot produce the selected algorithm's signature.
    /// <para>密钥类型无法产生所选算法的签名。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="algorithm"/> is not a known algorithm ID.
    /// <para><paramref name="algorithm"/> 不是已知的算法 ID。</para></exception>
    public static byte[] BuildV2Signer(
        AsymmetricAlgorithm key,
        IReadOnlyList<X509Certificate2> certificateChain,
        ApkSignatureConstants.SignatureAlgorithm algorithm,
        byte[] contentDigest,
        bool protectV3 = false)
    {
        var signedData = BuildSignedDataV2(algorithm, contentDigest, certificateChain, protectV3);
        var signature = SignSignedData(key, algorithm, signedData);
        return LengthPrefixSequence([BuildSignerV2(signedData, algorithm, signature, key)]);
    }

    /// <summary>
    /// Builds the v3 signer block value (length-prefixed signer sequence) for one signer.
    /// <para>为单个签名者构建 v3 签名者块值（带长度前缀的 signer 序列）。</para>
    /// </summary>
    /// <param name="key">User-provided signing key. <para>用户提供的签名密钥。</para></param>
    /// <param name="certificateChain">Certificate chain, leaf first. <para>证书链，叶子在前。</para></param>
    /// <param name="algorithm">Signature algorithm. <para>签名算法。</para></param>
    /// <param name="contentDigest">Content digest already computed for the algorithm's hash.
    /// <para>已按算法哈希计算好的内容摘要。</para></param>
    /// <param name="minSdk">Minimum SDK protected by this signer.
    /// <para>该签名者保护的最低 SDK。</para></param>
    /// <param name="maxSdk">Maximum SDK protected by this signer.
    /// <para>该签名者保护的最高 SDK。</para></param>
    /// <returns>A uint32-length-prefixed signer sequence for the v3 block ID value, embedding the
    /// min/max SDK bounds as little-endian uint32 fields. <para>用于 v3 分块 ID 值的 uint32 长度前缀
    /// signer 序列，内嵌小端 uint32 的最低/最高 SDK 边界字段。</para></returns>
    /// <exception cref="NotSupportedException">The key type cannot produce the selected algorithm's signature.
    /// <para>密钥类型无法产生所选算法的签名。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="algorithm"/> is not a known algorithm ID.
    /// <para><paramref name="algorithm"/> 不是已知的算法 ID。</para></exception>
    public static byte[] BuildV3Signer(
        AsymmetricAlgorithm key,
        IReadOnlyList<X509Certificate2> certificateChain,
        ApkSignatureConstants.SignatureAlgorithm algorithm,
        byte[] contentDigest,
        int minSdk = V3DefaultMinSdk,
        int maxSdk = V3DefaultMaxSdk)
    {
        var signedData = BuildSignedDataV3(algorithm, contentDigest, certificateChain, minSdk, maxSdk);
        var signature = SignSignedData(key, algorithm, signedData);
        return LengthPrefixSequence([BuildSignerV3(signedData, algorithm, signature, key, minSdk, maxSdk)]);
    }

    private static byte[] BuildSignedDataV2(
        ApkSignatureConstants.SignatureAlgorithm algorithm,
        byte[] contentDigest,
        IReadOnlyList<X509Certificate2> chain,
        bool protectV3)
    {
        Span<byte> algoId = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(algoId, (uint)algorithm);
        var digests = LengthPrefixSequence([
            Concat(algoId.ToArray(), LengthPrefix(contentDigest)),
        ]);

        var certificates = LengthPrefixSequence(chain.Select(static c => c.RawData));
        var attributes = protectV3
            ? LengthPrefixSequence([new byte[] { 0x0d, 0xf0, 0xef, 0xbe, 3, 0, 0, 0 }])
            : new byte[] { 0, 0, 0, 0 };

        return Concat(digests, certificates, attributes);
    }

    private static byte[] BuildSignerV2(
        byte[] signedData,
        ApkSignatureConstants.SignatureAlgorithm algorithm,
        byte[] signature,
        AsymmetricAlgorithm key)
    {
        Span<byte> algoId = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(algoId, (uint)algorithm);

        var signatures = LengthPrefixSequence([
            Concat(algoId.ToArray(), LengthPrefix(signature)),
        ]);
        var publicKey = LengthPrefix(ExportSubjectPublicKeyInfo(key, Array.Empty<X509Certificate2>()));

        return Concat(LengthPrefix(signedData), signatures, publicKey);
    }

    private static byte[] BuildSignedDataV3(
        ApkSignatureConstants.SignatureAlgorithm algorithm,
        byte[] contentDigest,
        IReadOnlyList<X509Certificate2> chain,
        int minSdk,
        int maxSdk)
    {
        Span<byte> algoId = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(algoId, (uint)algorithm);
        var digests = LengthPrefixSequence([
            Concat(algoId.ToArray(), LengthPrefix(contentDigest)),
        ]);

        var certificates = LengthPrefixSequence(chain.Select(static c => c.RawData));
        var attributes = new byte[] { 0, 0, 0, 0 };

        Span<byte> minSdkBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(minSdkBytes, (uint)minSdk);
        Span<byte> maxSdkBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(maxSdkBytes, (uint)maxSdk);

        return Concat(digests, certificates, minSdkBytes.ToArray(), maxSdkBytes.ToArray(), attributes);
    }

    private static byte[] BuildSignerV3(
        byte[] signedData,
        ApkSignatureConstants.SignatureAlgorithm algorithm,
        byte[] signature,
        AsymmetricAlgorithm key,
        int minSdk,
        int maxSdk)
    {
        Span<byte> algoId = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(algoId, (uint)algorithm);
        var signatures = LengthPrefixSequence([
            Concat(algoId.ToArray(), LengthPrefix(signature)),
        ]);
        var publicKey = LengthPrefix(ExportSubjectPublicKeyInfo(key, Array.Empty<X509Certificate2>()));

        Span<byte> minSdkBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(minSdkBytes, (uint)minSdk);
        Span<byte> maxSdkBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(maxSdkBytes, (uint)maxSdk);

        return Concat(LengthPrefix(signedData), minSdkBytes.ToArray(), maxSdkBytes.ToArray(), signatures, publicKey);
    }

    /// <summary>
    /// Signs the signed-data chunk with the user's key using the algorithm's hash and padding.
    /// <para>使用用户密钥按算法的哈希与填充方式对签名数据分块签名。</para>
    /// </summary>
    /// <param name="key">User-provided signing key. <para>用户提供的签名密钥。</para></param>
    /// <param name="algorithm">Signature algorithm fixing the hash and padding.
    /// <para>决定哈希与填充方式的签名算法。</para></param>
    /// <param name="signedData">Signed-data chunk bytes to sign over.
    /// <para>待签名的签名数据分块字节。</para></param>
    /// <returns>Raw signature bytes; RSA signatures follow the algorithm's PSS or PKCS#1 padding and
    /// ECDSA signatures use the RFC 3279 DER sequence encoding.
    /// <para>原始签名字节；RSA 按算法采用 PSS 或 PKCS#1 填充，ECDSA 使用 RFC 3279 DER 序列编码。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="algorithm"/> is not a known algorithm ID.
    /// <para><paramref name="algorithm"/> 不是已知的算法 ID。</para></exception>
    /// <exception cref="NotSupportedException">The key and algorithm combination is not RSA/PSS, RSA/PKCS#1
    /// or ECDSA. <para>密钥与算法组合不属于 RSA/PSS、RSA/PKCS#1 或 ECDSA。</para></exception>
    public static byte[] SignSignedData(
        AsymmetricAlgorithm key,
        ApkSignatureConstants.SignatureAlgorithm algorithm,
        byte[] signedData)
    {
        var hashName = new HashAlgorithmName(ApkSignatureConstants.HashNameFor(algorithm));
        return (key, algorithm) switch
        {
            (RSA rsa, ApkSignatureConstants.SignatureAlgorithm.RsaPssSha256)
                => rsa.SignData(signedData, hashName, RSASignaturePadding.Pss),
            (RSA rsa, ApkSignatureConstants.SignatureAlgorithm.RsaPssSha512)
                => rsa.SignData(signedData, hashName, RSASignaturePadding.Pss),
            (RSA rsa, ApkSignatureConstants.SignatureAlgorithm.RsaPkcs1Sha256)
                => rsa.SignData(signedData, hashName, RSASignaturePadding.Pkcs1),
            (RSA rsa, ApkSignatureConstants.SignatureAlgorithm.RsaPkcs1Sha512)
                => rsa.SignData(signedData, hashName, RSASignaturePadding.Pkcs1),
            (ECDsa ec, ApkSignatureConstants.SignatureAlgorithm.EcdsaSha256 or ApkSignatureConstants.SignatureAlgorithm.EcdsaSha512)
                => ec.SignData(signedData, hashName, DSASignatureFormat.Rfc3279DerSequence),
            _ => throw new NotSupportedException(
                $"Key type {key.GetType().Name} cannot produce this signature. 密钥类型 {key.GetType().Name} 无法产生该签名。"),
        };
    }

    private static byte[] ExportSubjectPublicKeyInfo(AsymmetricAlgorithm? key, IReadOnlyList<X509Certificate2> chain)
    {
        if (key is RSA rsa)
        {
            return rsa.ExportSubjectPublicKeyInfo();
        }

        if (key is ECDsa ec)
        {
            return ec.ExportSubjectPublicKeyInfo();
        }

        if (chain.Count > 0)
        {
            return chain[0].PublicKey.ExportSubjectPublicKeyInfo();
        }

        throw new InvalidOperationException("No public key available. 无可用公钥。");
    }

    private static byte[] LengthPrefix(byte[] value)
    {
        var result = new byte[4 + value.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0, 4), (uint)value.Length);
        value.CopyTo(result, 4);
        return result;
    }

    /// <summary>
    /// Wraps a sequence of entries with a single uint32 length prefix over their concatenation.
    /// <para>用单个 uint32 长度前缀包裹条目序列（前缀覆盖串联后的总长）。</para>
    /// </summary>
    private static byte[] LengthPrefixSequence(IEnumerable<byte[]> entries)
    {
        long total = 0;
        foreach (var entry in entries)
        {
            total += 4 + entry.Length;
        }

        if (total > int.MaxValue)
        {
            throw new InvalidOperationException("Certificate sequence exceeds supported size. 证书序列超出支持的大小。");
        }

        var result = new byte[4 + total];
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0, 4), (uint)total);
        var offset = 4;
        foreach (var entry in entries)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(offset, 4), (uint)entry.Length);
            entry.CopyTo(result, offset + 4);
            offset = checked(offset + 4 + entry.Length);
        }

        return result;
    }

    private static byte[] BuildLengthPrefixed(byte[] content) => LengthPrefix(content);

    private static byte[] Concat(params byte[][] parts)
    {
        long total = 0;
        foreach (var part in parts)
        {
            total += part.Length;
        }

        var result = new byte[total];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }

        return result;
    }
}