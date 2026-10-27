using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using UotanStudio.Hash.Digests;

namespace UotanStudio.Comm.Protocols.Adb;

/// <summary>
/// Derives the TLS client certificate an ADB host presents to a secure adbd service: version 3, subject
/// and issuer <c>C=US, O=Android, CN=Adb</c>, a ten-year validity, critical <c>basicConstraints CA:TRUE</c>,
/// critical key usage <c>keyCertSign, cRLSign, digitalSignature</c>, a subject key identifier over the
/// public key, and a SHA-256 self-signature made by that key.
/// <para>派生 ADB 主机向安全 adbd 服务出示的 TLS 客户端证书：版本 3，主题与颁发者为
/// <c>C=US, O=Android, CN=Adb</c>，有效期十年，关键的 <c>basicConstraints CA:TRUE</c>，关键用法
/// <c>keyCertSign, cRLSign, digitalSignature</c>，基于公钥的主题密钥标识符，以及由该密钥做出的
/// SHA-256 自签。</para>
/// </summary>
/// <remarks>
/// The certificate is not an identity of its own. adbd extracts the public key from it and matches the
/// SHA-256 fingerprint of that key against the authorized host keys, so the certificate is regenerated on
/// every handshake from <c>~/.android/adbkey</c> and never stored; nothing here persists a certificate
/// either. The serial number is fixed to 1, which a peer never reads, and .NET's certificate request
/// assigns its own.
/// <para>该证书并非独立身份。adbd 会从中取出公钥，并按该公钥的 SHA-256 指纹比对已授权主机密钥，因此每次
/// 握手都用 <c>~/.android/adbkey</c> 重新生成且从不保存；此处同样不持久化任何证书。序列号固定为 1，
/// 而对端从不读取该字段，.NET 的证书请求会自行分配序列号。</para>
/// </remarks>
internal static class AdbHostCertificate
{
    private const int LifetimeSeconds = 10 * 365 * 24 * 60 * 60;

    private const string IdentityName = "C=US, O=Android, CN=Adb";

    /// <summary>
    /// Creates the client certificate and private-key pair bound to <paramref name="rsa"/>.
    /// <para>创建与 <paramref name="rsa"/> 绑定的客户端证书与私钥对。</para>
    /// </summary>
    /// <param name="rsa">The host identity key, the same key that signs AUTH tokens.
    /// <para>主机身份密钥，即用于 AUTH 令牌签名的同一密钥。</para></param>
    /// <returns>A self-signed certificate carrying <paramref name="rsa"/> as its public key. The caller
    /// disposes it. <para>以 <paramref name="rsa"/> 作为公钥的自签证书；由调用方释放。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="rsa"/> is <see langword="null"/>.
    /// <para><paramref name="rsa"/> 为 <see langword="null"/>。</para></exception>
    internal static X509Certificate2 Create(RSA rsa)
    {
        ArgumentNullException.ThrowIfNull(rsa);
        var request = new CertificateRequest(
            new X500DistinguishedName(IdentityName), rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(
            certificateAuthority: true, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign,
            critical: true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(
            ComputeSubjectKeyIdentifier(rsa), critical: false));

        var notBefore = DateTimeOffset.UtcNow;
        return request.CreateSelfSigned(notBefore, notBefore.AddSeconds(LifetimeSeconds));
    }

    /// <summary>
    /// Computes the RFC 5280 method-one key identifier: the SHA-1 hash over the contents of the
    /// <c>subjectPublicKey</c> BIT STRING, excluding its tag, length and unused-bit octet.
    /// <para>计算 RFC 5280 方法一的密钥标识符：对 <c>subjectPublicKey</c> BIT STRING 的内容做 SHA-1，
    /// 不含其标签、长度与未使用位字节。</para>
    /// </summary>
    /// <param name="rsa">The identity key. <para>身份密钥。</para></param>
    /// <returns>The 20-byte identifier. <para>20 字节标识符。</para></returns>
    private static byte[] ComputeSubjectKeyIdentifier(RSA rsa)
    {
        var reader = new AsnReader(rsa.ExportSubjectPublicKeyInfo(), AsnEncodingRules.DER);
        var subjectPublicKeyInfo = reader.ReadSequence();
        subjectPublicKeyInfo.ReadSequence();
        return Sha1.ComputeHash(subjectPublicKeyInfo.ReadBitString(out _));
    }
}