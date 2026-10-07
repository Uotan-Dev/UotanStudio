using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

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
        return WriteAtomically(path, certificate.ExportCertificatePem())
            ? certificate
            : CreateWithPrivateKey(File.ReadAllText(path));
    }

    private static X509Certificate2 GenerateSelfSigned(RSA rsa)
    {
        var subject = new X500DistinguishedName($"CN={Environment.UserName}@{Environment.MachineName}");
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        return request.CreateSelfSigned(notBefore, notBefore.AddYears(CertificateValidityYears));
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

    private static bool WriteAtomically(string path, string contents)
    {
        var temporary = path + "-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, contents, new System.Text.UTF8Encoding(false));
            try
            {
                File.Move(temporary, path, overwrite: false);
                return true;
            }
            catch (IOException) when (File.Exists(path))
            {
                return false;
            }
            catch (UnauthorizedAccessException) when (File.Exists(path))
            {
                return false;
            }
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}