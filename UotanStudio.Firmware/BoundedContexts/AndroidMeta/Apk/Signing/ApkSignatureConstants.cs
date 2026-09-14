namespace UotanStudio.Firmware.BoundedContexts.AndroidMeta.Apk.Signing;

/// <summary>
/// Constants of the APK Signature Scheme (v1/v2/v3) per the Android platform documentation.
/// <para>APK 签名方案（v1/v2/v3）常量，依据 Android 平台官方文档定义。</para>
/// </summary>
public static class ApkSignatureConstants
{
    /// <summary>Magic footer of the APK Signing Block ("APK Sig Block 42", 16 bytes).</summary>
    /// <para>APK 签名分块的 magic 尾部（"APK Sig Block 42"，16 字节）。</para>
    /// </summary>
    public static ReadOnlySpan<byte> SigningBlockMagic => "APK Sig Block 42"u8;

    /// <summary>Block ID of the APK Signature Scheme v2 signature (0x7109871a).</summary>
    /// <para>APK 签名方案 v2 签名的分块 ID（0x7109871a）。</para>
    public const uint V2BlockId = 0x7109871a;

    /// <summary>Block ID of the APK Signature Scheme v3 signature (0xf05368c0).</summary>
    /// <para>APK 签名方案 v3 签名的分块 ID（0xf05368c0）。</para>
    public const uint V3BlockId = 0xf05368c0;

    /// <summary>Chunk size (1 MiB) used by the two-level content digest.</summary>
    /// <para>两级内容摘要使用的分块大小（1 MiB）。</para>
    public const long ContentDigestChunkSize = 1024 * 1024;

    /// <summary>
    /// APK Signature Scheme v2/v3 signature algorithm IDs.
    /// <para>APK 签名方案 v2/v3 的签名算法 ID。</para>
    /// </summary>
    public enum SignatureAlgorithm : uint
    {
        /// <summary>RSASSA-PSS with SHA2-256 digest, SHA2-256 MGF1, 32 bytes salt, trailer 0xbc.</summary>
        /// <para>RSASSA-PSS，SHA2-256 摘要、SHA2-256 MGF1、32 字节盐、trailer 0xbc。</para>
        RsaPssSha256 = 0x0101,

        /// <summary>RSASSA-PSS with SHA2-512 digest, SHA2-512 MGF1, 64 bytes salt, trailer 0xbc.</summary>
        /// <para>RSASSA-PSS，SHA2-512 摘要、SHA2-512 MGF1、64 字节盐、trailer 0xbc。</para>
        RsaPssSha512 = 0x0102,

        /// <summary>RSASSA-PKCS1-v1_5 with SHA2-256 digest (deterministic; build systems).</summary>
        /// <para>RSASSA-PKCS1-v1_5，SHA2-256 摘要（确定性，适用于构建系统）。</para>
        RsaPkcs1Sha256 = 0x0103,

        /// <summary>RSASSA-PKCS1-v1_5 with SHA2-512 digest (deterministic; build systems).</summary>
        /// <para>RSASSA-PKCS1-v1_5，SHA2-512 摘要（确定性，适用于构建系统）。</para>
        RsaPkcs1Sha512 = 0x0104,

        /// <summary>ECDSA with SHA2-256 digest.</summary>
        /// <para>ECDSA，SHA2-256 摘要。</para>
        EcdsaSha256 = 0x0201,

        /// <summary>ECDSA with SHA2-512 digest.</summary>
        /// <para>ECDSA，SHA2-512 摘要。</para>
        EcdsaSha512 = 0x0202,

        /// <summary>DSA with SHA2-256 digest.</summary>
        /// <para>DSA，SHA2-256 摘要。</para>
        DsaSha256 = 0x0301,
    }

    /// <summary>
    /// Content digest length in bytes for the given signature algorithm ID.
    /// <para>按签名算法 ID 返回内容摘要的字节长度。</para>
    /// </summary>
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
    public static string HashNameFor(SignatureAlgorithm algorithm) => algorithm switch
    {
        SignatureAlgorithm.RsaPssSha256 or SignatureAlgorithm.RsaPkcs1Sha256
            or SignatureAlgorithm.EcdsaSha256 or SignatureAlgorithm.DsaSha256 => "SHA256",
        SignatureAlgorithm.RsaPssSha512 or SignatureAlgorithm.RsaPkcs1Sha512
            or SignatureAlgorithm.EcdsaSha512 => "SHA512",
        _ => throw new ArgumentException($"Unknown signature algorithm ID: 0x{(uint)algorithm:x4}"),
    };
}