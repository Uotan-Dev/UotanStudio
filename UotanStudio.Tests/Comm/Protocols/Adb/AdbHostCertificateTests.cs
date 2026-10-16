using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using UotanStudio.Comm.Protocols.Adb;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Adb;

/// <summary>
/// Verifies that the TLS client certificate an ADB host presents is derived from the host identity key in
/// the shape upstream <c>GenerateX509Certificate()</c> emits, and that no certificate identity is persisted
/// anywhere: the peer authenticates the SHA-256 fingerprint of the key inside the certificate, so the
/// subject, serial and storage location of a stored certificate would carry no meaning.
/// <para>验证 ADB 主机出示的 TLS 客户端证书由主机身份密钥派生，形状与上游
/// <c>GenerateX509Certificate()</c> 一致，并且不持久化任何证书身份：对端认证的是证书内公钥的
/// SHA-256 指纹，被保存证书的标题、序列号与存放位置都不携带意义。</para>
/// </summary>
public sealed class AdbHostCertificateTests
{
    private const string AdbIdentityName = "C=US, O=Android, CN=Adb";

    [Fact]
    public void Create_IdentityKey_CarriesTheUpstreamAndroidAdbNameAndKey()
    {
        using var identity = AdbAuthentication.CreateNew();

        using var certificate = AdbHostCertificate.Create(identity.RsaKey);

        var expectedName = new X500DistinguishedName(AdbIdentityName).RawData;
        Assert.Equal(expectedName, certificate.SubjectName.RawData);
        Assert.Equal(expectedName, certificate.IssuerName.RawData);
        Assert.Equal("1.2.840.113549.1.1.11", certificate.SignatureAlgorithm.Value);
        Assert.Equal(
            identity.RsaKey.ExportSubjectPublicKeyInfo(),
            certificate.GetRSAPublicKey()!.ExportSubjectPublicKeyInfo());
        Assert.True(certificate.NotBefore <= DateTimeOffset.UtcNow);
        Assert.Equal(certificate.NotBefore.AddSeconds(10 * 365 * 24 * 60 * 60), certificate.NotAfter);
    }

    [Fact]
    public void Create_IdentityKey_BindsTheIdentityPrivateKeySoTheCertificateProvesKeyOwnership()
    {
        using var identity = AdbAuthentication.CreateNew();
        var challenge = "wire challenge"u8.ToArray();

        using var certificate = AdbHostCertificate.Create(identity.RsaKey);
        using var privateFromCertificate = certificate.GetRSAPrivateKey();

        Assert.NotNull(privateFromCertificate);
        var signature = privateFromCertificate.SignData(challenge, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        Assert.True(identity.RsaKey.VerifyData(challenge, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    [Fact]
    public void Create_IdentityKey_UsesUpstreamCriticalityForConstraintsAndKeyUsage()
    {
        using var identity = AdbAuthentication.CreateNew();

        using var certificate = AdbHostCertificate.Create(identity.RsaKey);

        var constraints = Assert.IsType<X509BasicConstraintsExtension>(
            certificate.Extensions.Single(extension => extension.Oid!.Value == "2.5.29.19"));
        Assert.True(constraints.CertificateAuthority);
        Assert.True(constraints.Critical);
        Assert.False(constraints.HasPathLengthConstraint);

        var usage = Assert.IsType<X509KeyUsageExtension>(
            certificate.Extensions.Single(extension => extension.Oid!.Value == "2.5.29.15"));
        Assert.Equal(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign,
            usage.KeyUsages);
        Assert.True(usage.Critical);

        var identifier = Assert.IsType<X509SubjectKeyIdentifierExtension>(
            certificate.Extensions.Single(extension => extension.Oid!.Value == "2.5.29.14"));
        Assert.False(identifier.Critical);
        Assert.Equal(ComputeExpectedIdentifier(identity.RsaKey), identifier.SubjectKeyIdentifier);
    }

    [Fact]
    public void CommAssembly_HasNoPersistedHostCertificateKeystore()
    {
        Assert.Null(typeof(AdbAuthentication).Assembly
            .GetType("UotanStudio.Comm.Protocols.Adb.Pairing.AdbPairingKeystore", throwOnError: false));
    }

    private static string ComputeExpectedIdentifier(RSA rsa)
    {
        var reader = new AsnReader(rsa.ExportSubjectPublicKeyInfo(), AsnEncodingRules.DER);
        var subjectPublicKeyInfo = reader.ReadSequence();
        subjectPublicKeyInfo.ReadSequence();
        var publicKeyBits = subjectPublicKeyInfo.ReadBitString(out _);
        return Convert.ToHexString(SHA1.HashData(publicKeyBits));
    }
}