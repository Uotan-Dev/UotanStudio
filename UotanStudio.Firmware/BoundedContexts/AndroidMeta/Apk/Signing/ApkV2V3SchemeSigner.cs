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
    /// <summary>SDK range for v3 signers without explicit rotation (apksigner defaults).</summary>
    /// <para>无显式密钥轮换时 v3 签名者的 SDK 范围（apksigner 默认值）。</para>
    public const int V3DefaultMinSdk = 28;
    public const int V3DefaultMaxSdk = int.MaxValue;

    /// <summary>
    /// Builds the v2 signer block value (length-prefixed signer sequence) for one signer.
    /// <para>为单个签名者构建 v2 签名者块值（带长度前缀的 signer 序列）。</para>
    /// </summary>
    /// <param name="key">User-provided signing key. 用户提供的签名密钥。</param>
    /// <param name="certificateChain">Certificate chain, leaf first. 证书链，叶子在前。</param>
    /// <param name="algorithm">Signature algorithm. 签名算法。</param>
    /// <param name="contentDigest">Content digest already computed for the algorithm's hash. 已按算法哈希计算好的内容摘要。</param>
    /// <param name="protectV3">Requires the v3 block on supported platforms. 在受支持的平台要求存在 v3 分块。</param>
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
    /// <param name="key">User-provided signing key. 用户提供的签名密钥。</param>
    /// <param name="certificateChain">Certificate chain, leaf first. 证书链，叶子在前。</param>
    /// <param name="algorithm">Signature algorithm. 签名算法。</param>
    /// <param name="contentDigest">Content digest already computed for the algorithm's hash. 已按算法哈希计算好的内容摘要。</param>
    /// <param name="minSdk">Minimum SDK protected by this signer. 该签名者保护的最低 SDK。</param>
    /// <param name="maxSdk">Maximum SDK protected by this signer. 该签名者保护的最高 SDK。</param>
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

    // ---- v2 structure ---------------------------------------------------------------

    // Builds v2 signed-data as a concatenation of two-level length-prefixed digests,
    // certificates and attributes, matching apksig's encodeAsSequenceOfLengthPrefixedElements.
    // 构建 v2 签名数据：digests、certificates、attributes 按 apksig 带两级长度前缀的序列规则拼接。
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

    // Builds the v2 signer element from length-prefixed signed data, signatures and public key, in the
    // exact field order the v2 spec requires; the enclosing sequence supplies the signer length prefix.
    // 按 v2 规范字段顺序将带长度前缀的 signed data、signatures 与公钥拼成 signer 元素。
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

    // ---- v3 structure ---------------------------------------------------------------

    // Builds v3 signed-data: digests, certificates, min/max SDK and empty additional attributes, in the
    // field order required by the official v3 spec; the signature covers the signed-data content only.
    // 按 v3 规范字段顺序构建签名数据：digests、certificates、min/max SDK、空附加属性。
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

    // Builds the v3 signer element in the field order the official v3 spec requires: signed data,
    // min SDK, max SDK, signatures, public key.
    // 按官方 v3 规范字段顺序构建 signer 元素：signed data、min SDK、max SDK、signatures、公钥。
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

    // ---- crypto ---------------------------------------------------------------------

    /// <summary>
    /// Signs the signed-data chunk with the user's key using the algorithm's hash and padding.
    /// <para>使用用户密钥按算法的哈希与填充方式对签名数据分块签名。</para>
    /// </summary>
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

    // ---- helpers --------------------------------------------------------------------

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