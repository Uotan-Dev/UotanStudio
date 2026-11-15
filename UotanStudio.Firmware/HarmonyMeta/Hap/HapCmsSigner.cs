using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

namespace UotanStudio.Firmware.HarmonyMeta.Hap;

/// <summary>
/// Builds the attached CMS SignedData the HAP signature block requires: the digest message as encapsulated content,
/// <c>contentType</c>/<c>signingTime</c>/<c>messageDigest</c> signed attributes and the full supplied certificate
/// chain embedded alongside the signer.
/// <para>构造 HAP 签名块所需的 attached CMS SignedData：摘要消息作为封装内容、
/// <c>contentType</c>/<c>signingTime</c>/<c>messageDigest</c> 已签属性，以及随签名者一并内嵌的完整证书链。</para>
/// </summary>
/// <remarks>
/// RSA keys sign with RSASSA-PSS and ECDSA keys with the DER-encoded signature the device verifier expects; the
/// signer is identified by issuer and serial number as the on-device parser requires.
/// <para>RSA 密钥以 RSASSA-PSS 签名，ECDSA 密钥以设备验签方期望的 DER 签名输出；签名者按设备解析器要求以
/// 颁发者与序列号标识。</para>
/// </remarks>
internal static class HapCmsSigner
{
    /// <summary>
    /// Signs the digest message with the supplied material.
    /// <para>使用给定材料对摘要消息签名。</para>
    /// </summary>
    /// <param name="digestMessage">The encoded digest message. <para>编码后的摘要消息。</para></param>
    /// <param name="signer">The signing certificate carrying its private key.
    /// <para>带私钥的签名证书。</para></param>
    /// <param name="chainCertificates">Additional chain certificates to embed.
    /// <para>需一并内嵌的链上证书。</para></param>
    /// <returns>The encoded CMS message. <para>编码后的 CMS 消息。</para></returns>
    /// <exception cref="InvalidOperationException">The certificate carries neither an RSA nor an ECDSA key.
    /// <para>证书既无 RSA 也无 ECDSA 密钥。</para></exception>
    public static byte[] Sign(byte[] digestMessage, X509Certificate2 signer, IReadOnlyList<X509Certificate2> chainCertificates)
    {
        var content = new ContentInfo(digestMessage);
        var signed = new SignedCms(content, detached: false);
        CmsSigner cmsSigner;
        var rsa = signer.GetRSAPrivateKey();
        if (rsa is not null)
        {
            cmsSigner = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, signer, rsa, RSASignaturePadding.Pss);
        }
        else
        {
            var ecdsa = signer.GetECDsaPrivateKey()
                ?? throw new InvalidOperationException("The signing certificate carries neither an RSA nor an ECDSA key. 签名证书既无 RSA 也无 ECDSA 密钥。");
            cmsSigner = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, signer, ecdsa);
        }

        cmsSigner.IncludeOption = X509IncludeOption.None;
        cmsSigner.SignedAttributes.Add(new Pkcs9SigningTime(DateTime.UtcNow));
        // The signer certificate must be embedded as well: the device verifier resolves the SignerIdentifier
        // (issuer + serial) from the certificates carried by the message itself.
        cmsSigner.Certificates.Add(signer);
        foreach (var certificate in chainCertificates)
        {
            cmsSigner.Certificates.Add(certificate);
        }

        signed.ComputeSignature(cmsSigner, silent: true);
        return signed.Encode();
    }

    /// <summary>
    /// Verifies a CMS message against the certificates embedded in it, without trusting the host store.
    /// <para>使用 CMS 内嵌证书验签，不依赖宿主信任存储。</para>
    /// </summary>
    /// <param name="encoded">The encoded CMS message. <para>编码后的 CMS 消息。</para></param>
    /// <param name="content">The expected attached content. <para>期望的 attached 内容。</para></param>
    /// <exception cref="CryptographicException">The signature is invalid or the content differs.
    /// <para>签名无效或内容不符。</para></exception>
    public static void Verify(byte[] encoded, byte[] content)
    {
        var signed = new SignedCms();
        signed.Decode(encoded);
        signed.CheckSignature(true);
        if (!signed.ContentInfo.Content.AsSpan().SequenceEqual(content))
        {
            var signedDigest = Convert.ToHexStringLower(signed.ContentInfo.Content.AsSpan()[^32..]);
            var expectedDigest = Convert.ToHexStringLower(content.AsSpan()[^32..]);
            throw new CryptographicException(
                $"The CMS attached content differs from the expected message: signed digest {signedDigest}, recomputed {expectedDigest}. " +
                $"CMS attached 内容与期望消息不符：已签摘要 {signedDigest}，重算 {expectedDigest}。");
        }
    }
}