using System.Security.Cryptography;

namespace UotanStudio.Firmware.BoundedContexts.AndroidMeta.Apk.Signing;

/// <summary>
/// Selects the v2/v3 signature algorithm for a signer key, mirroring apksigner defaults.
/// <para>按 apksigner 默认策略为签名密钥选择 v2/v3 签名算法。</para>
/// </summary>
public static class ApkSignatureAlgorithmSelector
{
    /// <summary>
    /// Follows apksig: deterministic RSA PKCS#1; SHA-512 for RSA above 3072 bits or EC above 256 bits.
    /// <para>遵循 apksig：RSA 使用 PKCS#1；RSA 超过 3072 位或 EC 超过 256 位时使用 SHA-512。</para>
    /// </summary>
    /// <param name="key">User-provided signing key. 用户提供的签名密钥。</param>
    /// <param name="minSdkVersion">APK minimum SDK version. APK 的最低 SDK 版本。</param>
    public static ApkSignatureConstants.SignatureAlgorithm Select(AsymmetricAlgorithm key, int minSdkVersion)
    {
        return key switch
        {
            RSA rsa => rsa.KeySize > 3072
                ? ApkSignatureConstants.SignatureAlgorithm.RsaPkcs1Sha512
                : ApkSignatureConstants.SignatureAlgorithm.RsaPkcs1Sha256,
            ECDsa ec => ec.KeySize > 256
                ? ApkSignatureConstants.SignatureAlgorithm.EcdsaSha512
                : ApkSignatureConstants.SignatureAlgorithm.EcdsaSha256,
            _ => throw new NotSupportedException(
                $"Key type {key.GetType().Name} is not supported; expected RSA or ECDSA. 密钥类型 {key.GetType().Name} 不受支持，期望 RSA 或 ECDSA。"),
        };
    }
}