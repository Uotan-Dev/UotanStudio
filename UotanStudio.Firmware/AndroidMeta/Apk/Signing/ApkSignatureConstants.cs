namespace UotanStudio.Firmware.AndroidMeta.Apk.Signing;

/// <summary>
/// Constants of the APK Signature Scheme (v1/v2/v3) per the Android platform documentation.
/// <para>APK 签名方案（v1/v2/v3）常量，依据 Android 平台官方文档定义。</para>
/// </summary>
public static class ApkSignatureConstants
{
    /// <summary>
    /// Magic footer of the APK Signing Block ("APK Sig Block 42", 16 bytes).
    /// <para>APK 签名分块的 magic 尾部（"APK Sig Block 42"，16 字节）。</para>
    /// </summary>
    /// <value>The exact 16 ASCII bytes written before the trailing block size, used to recognise a
    /// signing block. <para>写在尾部长度字段之前的固定 16 个 ASCII 字节，用于识别签名分块。</para></value>
    public static ReadOnlySpan<byte> SigningBlockMagic => "APK Sig Block 42"u8;

    /// <summary>Block ID of the APK Signature Scheme v2 signature (0x7109871a).
    /// <para>APK 签名方案 v2 签名的分块 ID（0x7109871a）。</para></summary>
    public const uint V2BlockId = 0x7109871a;

    /// <summary>Block ID of the APK Signature Scheme v3 signature (0xf05368c0).
    /// <para>APK 签名方案 v3 签名的分块 ID（0xf05368c0）。</para></summary>
    public const uint V3BlockId = 0xf05368c0;

    /// <summary>Chunk size (1 MiB) used by the two-level content digest.
    /// <para>两级内容摘要使用的分块大小（1 MiB）。</para></summary>
    public const long ContentDigestChunkSize = 1024 * 1024;

    /// <summary>
    /// APK Signature Scheme v2/v3 signature algorithm IDs.
    /// <para>APK 签名方案 v2/v3 的签名算法 ID。</para>
    /// </summary>
    public enum SignatureAlgorithm : uint
    {
        /// <summary>RSASSA-PSS with SHA2-256 digest, SHA2-256 MGF1, 32 bytes salt, trailer 0xbc.
        /// <para>RSASSA-PSS，SHA2-256 摘要、SHA2-256 MGF1、32 字节盐、trailer 0xbc。</para></summary>
        RsaPssSha256 = 0x0101,

        /// <summary>RSASSA-PSS with SHA2-512 digest, SHA2-512 MGF1, 64 bytes salt, trailer 0xbc.
        /// <para>RSASSA-PSS，SHA2-512 摘要、SHA2-512 MGF1、64 字节盐、trailer 0xbc。</para></summary>
        RsaPssSha512 = 0x0102,

        /// <summary>RSASSA-PKCS1-v1_5 with SHA2-256 digest (deterministic; build systems).
        /// <para>RSASSA-PKCS1-v1_5，SHA2-256 摘要（确定性，适用于构建系统）。</para></summary>
        RsaPkcs1Sha256 = 0x0103,

        /// <summary>RSASSA-PKCS1-v1_5 with SHA2-512 digest (deterministic; build systems).
        /// <para>RSASSA-PKCS1-v1_5，SHA2-512 摘要（确定性，适用于构建系统）。</para></summary>
        RsaPkcs1Sha512 = 0x0104,

        /// <summary>ECDSA with SHA2-256 digest. <para>ECDSA，SHA2-256 摘要。</para></summary>
        EcdsaSha256 = 0x0201,

        /// <summary>ECDSA with SHA2-512 digest. <para>ECDSA，SHA2-512 摘要。</para></summary>
        EcdsaSha512 = 0x0202,

        /// <summary>DSA with SHA2-256 digest. <para>DSA，SHA2-256 摘要。</para></summary>
        DsaSha256 = 0x0301,
    }

    /// <summary>
    /// Content digest length in bytes for the given signature algorithm ID.
    /// <para>按签名算法 ID 返回内容摘要的字节长度。</para>
    /// </summary>
    /// <param name="algorithm">v2/v3 signature algorithm ID. <para>v2/v3 签名算法 ID。</para></param>
    /// <returns>32 for the SHA-256 algorithms, 64 for the SHA-512 algorithms.
    /// <para>SHA-256 系列算法为 32，SHA-512 系列算法为 64。</para></returns>
    /// <exception cref="ArgumentException">The value is not a known algorithm ID.
    /// <para>该值不是已知的算法 ID。</para></exception>
    public static int DigestSizeFor(SignatureAlgorithm algorithm) => algorithm switch
    {
        SignatureAlgorithm.RsaPssSha256 or SignatureAlgorithm.RsaPkcs1Sha256
            or SignatureAlgorithm.EcdsaSha256 or SignatureAlgorithm.DsaSha256 => 32,
        SignatureAlgorithm.RsaPssSha512 or SignatureAlgorithm.RsaPkcs1Sha512
            or SignatureAlgorithm.EcdsaSha512 => 64,
        _ => throw new ArgumentException($"Unknown signature algorithm ID: 0x{(uint)algorithm:x4}"),
    };

    /// <summary>
    /// Hash algorithm name ("SHA256"/"SHA512") for the given signature algorithm ID.
    /// <para>按签名算法 ID 返回哈希算法名称（"SHA256"/"SHA512"）。</para>
    /// </summary>
    /// <param name="algorithm">v2/v3 signature algorithm ID. <para>v2/v3 签名算法 ID。</para></param>
    /// <returns>"SHA256" for the SHA-256 algorithms or "SHA512" for the SHA-512 algorithms, ready for
    /// <see cref="System.Security.Cryptography.HashAlgorithmName"/>.
    /// <para>SHA-256 系列返回 "SHA256"，SHA-512 系列返回 "SHA512"，可直接用于
    /// <see cref="System.Security.Cryptography.HashAlgorithmName"/>。</para></returns>
    /// <exception cref="ArgumentException">The value is not a known algorithm ID.
    /// <para>该值不是已知的算法 ID。</para></exception>
    public static string HashNameFor(SignatureAlgorithm algorithm) => algorithm switch
    {
        SignatureAlgorithm.RsaPssSha256 or SignatureAlgorithm.RsaPkcs1Sha256
            or SignatureAlgorithm.EcdsaSha256 or SignatureAlgorithm.DsaSha256 => "SHA256",
        SignatureAlgorithm.RsaPssSha512 or SignatureAlgorithm.RsaPkcs1Sha512
            or SignatureAlgorithm.EcdsaSha512 => "SHA512",
        _ => throw new ArgumentException($"Unknown signature algorithm ID: 0x{(uint)algorithm:x4}"),
    };
}