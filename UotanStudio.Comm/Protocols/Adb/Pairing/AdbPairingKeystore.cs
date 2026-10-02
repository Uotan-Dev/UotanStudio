using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;

namespace UotanStudio.Comm.Protocols.Adb.Pairing;

/// <summary>
/// Persists the TLS client certificate used by the serverless direct pairing path:
/// a self-signed certificate bound to the default ADB RSA key, presented during
/// pairing and later as the TLS client certificate (the device matches its public
/// key against the pairing-registered key). Files live in a dedicated subdirectory
/// of the AOSP <c>~/.android</c> key directory so the adb server's own files are
/// never touched.
/// <para>持久化无 server 直连配对路径使用的 TLS 客户端证书：绑定默认 ADB RSA 密钥的
/// 自签证书，配对时出示、之后作为 TLS 客户端证书（设备按配对登记的公钥匹配）。
/// 文件存放在 AOSP <c>~/.android</c> 密钥目录的专用子目录中，绝不触碰 adb server
/// 自己的文件。</para>
/// </summary>
internal static class AdbPairingKeystore
{
    private const string ClientCertificateFileName = "client-cert.pem";
    private const int CertificateValidityYears = 10;

    /// <summary>
    /// Gets the directory holding the pairing keystore files
    /// (<c>%USERPROFILE%/.android/uotan/pairing</c> or the platform equivalent).
    /// <para>获取配对密钥库目录（<c>%USERPROFILE%/.android/uotan/pairing</c> 或
    /// 各平台等价路径）。</para>
    /// </summary>
    /// <value>The composed directory path; it is created on demand.
    /// <para>组合得到的目录路径；按需创建。</para></value>
    public static string KeystoreDirectory => Path.Combine(AdbAuthentication.DefaultKeyDirectory, "uotan", "pairing");

    /// <summary>
    /// Loads the client certificate, generating and persisting a new self-signed
    /// certificate bound to the default ADB RSA key when none exists. The same
    /// certificate bytes must be presented at pairing time and in every later TLS
    /// handshake, so the certificate file is written once and reused.
    /// <para>加载客户端证书；不存在时生成绑定默认 ADB RSA 密钥的自签证书并持久化。
    /// 配对时与后续每次 TLS 握手必须出示同一份证书字节，因此证书文件只写一次并复用。</para>
    /// </summary>
    /// <returns>The client certificate with its private key.
    /// <para>带私钥的客户端证书。</para></returns>
    public static X509Certificate2 LoadOrCreateClientCertificate()
    {
        Directory.CreateDirectory(KeystoreDirectory);
        var path = Path.Combine(KeystoreDirectory, ClientCertificateFileName);
        if (File.Exists(path))
        {
            return CreateWithPrivateKey(File.ReadAllText(path));
        }

        using var authentication = AdbAuthentication.LoadOrCreate();
        var certificate = GenerateSelfSigned(authentication.RsaKey);
        WriteAtomically(path, certificate.ExportCertificatePem());
        return certificate;
    }

    private static X509Certificate2 GenerateSelfSigned(RSA rsa)
    {
        var parameters = rsa.ExportParameters(true);
        var subject = new X509Name($"CN={Environment.UserName}@{Environment.MachineName}");
        var serial = BigInteger.Arbitrary(64);
        var notBefore = DateTime.UtcNow.AddDays(-1);
        var notAfter = notBefore.AddYears(CertificateValidityYears);
        var generator = new X509V3CertificateGenerator();
        generator.SetSerialNumber(serial);
        generator.SetIssuerDN(subject);
        generator.SetSubjectDN(subject);
        generator.SetNotBefore(notBefore);
        generator.SetNotAfter(notAfter);
        generator.SetPublicKey(DotNetUtilities.GetRsaPublicKey(rsa.ExportParameters(false)));
        var signer = new Asn1SignatureFactory("SHA256withRSA", ToPrivateKeyParameter(parameters), null);
        var generated = generator.Generate(signer);
        var certificate = X509CertificateLoader.LoadCertificate(generated.GetEncoded());
        return certificate.CopyWithPrivateKey(rsa);
    }

    private static Org.BouncyCastle.Crypto.AsymmetricKeyParameter ToPrivateKeyParameter(RSAParameters parameters)
        => new RsaPrivateCrtKeyParameters(
            ToBigInteger(parameters.Modulus),
            ToBigInteger(parameters.Exponent),
            ToBigInteger(parameters.D),
            ToBigInteger(parameters.P),
            ToBigInteger(parameters.Q),
            ToBigInteger(parameters.DP),
            ToBigInteger(parameters.DQ),
            ToBigInteger(parameters.InverseQ));

    private static BigInteger ToBigInteger(byte[]? value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new BigInteger(1, value);
    }

    private static X509Certificate2 CreateWithPrivateKey(string pem)
    {
        var der = ImportPemCertificate(pem);
        using var authentication = AdbAuthentication.LoadOrCreate();
        return X509CertificateLoader.LoadCertificate(der).CopyWithPrivateKey(authentication.RsaKey);
    }

    private static byte[] ImportPemCertificate(string pem)
    {
        const string header = "-----BEGIN CERTIFICATE-----";
        const string footer = "-----END CERTIFICATE-----";
        var start = pem.IndexOf(header, StringComparison.Ordinal);
        var end = pem.IndexOf(footer, StringComparison.Ordinal);
        if (start < 0 || end < 0 || end <= start)
        {
            throw new IOException("The pairing certificate file is not a PEM certificate.");
        }

        var base64 = pem[(start + header.Length)..end].Trim();
        try
        {
            return Convert.FromBase64String(base64);
        }
        catch (FormatException ex)
        {
            throw new IOException("The pairing certificate file is corrupted.", ex);
        }
    }

    private static void WriteAtomically(string path, string contents)
    {
        var temporary = path + "-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, contents, new System.Text.UTF8Encoding(false));
            try { File.Move(temporary, path, overwrite: true); }
            catch (IOException) when (File.Exists(path)) { }
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}