using System.Security.Cryptography;

namespace UotanStudio.Firmware.HarmonyMeta.Hap;

/// <summary>
/// Key algorithm choices for a generated HAP signing keystore.
/// <para>生成的 HAP 签名 keystore 可选的密钥算法。</para>
/// </summary>
public enum HapSigningKeyType
{
    /// <summary>ECDSA P-256, the HarmonyOS debug default. <para>ECDSA P-256，HarmonyOS 调试默认。</para></summary>
    EcP256,

    /// <summary>ECDSA P-384. <para>ECDSA P-384。</para></summary>
    EcP384,

    /// <summary>RSA 2048. <para>RSA 2048。</para></summary>
    Rsa2048,

    /// <summary>RSA 3072. <para>RSA 3072。</para></summary>
    Rsa3072,

    /// <summary>RSA 4096. <para>RSA 4096。</para></summary>
    Rsa4096,
}

/// <summary>
/// Options for generating one HAP signing keystore and its certificate signing request.
/// <para>生成一套 HAP 签名 keystore 与证书签名请求的选项。</para>
/// </summary>
/// <param name="KeyType">Key algorithm. <para>密钥算法。</para></param>
/// <param name="CommonName">Certificate subject common name; defaults to <c>DebugKey</c>, matching the official
/// debug flow. <para>证书主题通用名；默认 <c>DebugKey</c>，与官方调试流程一致。</para></param>
/// <param name="KeyAlias">Keystore alias; defaults to <c>debugKey</c>. <para>keystore 别名；默认 <c>debugKey</c>。</para></param>
/// <param name="KeyPassword">Keystore and key password. <para>keystore 与密钥口令。</para></param>
/// <param name="ValidityYears">Self-signed placeholder certificate validity. <para>自签占位证书有效期。</para></param>
public sealed record HapSigningKeyOptions(
    HapSigningKeyType KeyType = HapSigningKeyType.EcP256,
    string CommonName = "DebugKey",
    string KeyAlias = "debugKey",
    string KeyPassword = "",
    int ValidityYears = 25)
{
    /// <summary>Gets the hash algorithm matching the key strength.
    /// <para>与密钥强度匹配的哈希算法。</para></summary>
    internal HashAlgorithmName Hash => KeyType is HapSigningKeyType.Rsa4096 or HapSigningKeyType.EcP384
        ? HashAlgorithmName.SHA384
        : HashAlgorithmName.SHA256;
}