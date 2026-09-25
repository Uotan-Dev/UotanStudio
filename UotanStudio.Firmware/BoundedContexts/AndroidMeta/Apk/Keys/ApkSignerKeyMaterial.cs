using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace UotanStudio.Firmware.BoundedContexts.AndroidMeta.Apk.Keys;

/// <summary>
/// One signer's custom key material: user-provided private key and X.509 certificate chain.
/// No keys are bundled; every path is supplied by the user.
/// <para>单个签名者的自定义密钥材料：由用户提供的私钥与 X.509 证书链。不内置任何密钥，全部路径由用户指定。</para>
/// </summary>
public sealed class ApkSignerKeyMaterial : IDisposable
{
    private readonly IDisposable? _disposableKey;

    private ApkSignerKeyMaterial(AsymmetricAlgorithm key, IDisposable? disposableKey, X509Certificate2 certificate, IReadOnlyList<X509Certificate2> chain)
    {
        Key = key;
        _disposableKey = disposableKey;
        Certificate = certificate;
        CertificateChain = chain;
    }

    /// <summary>The signing key (RSA or ECDSA).
    /// <para>签名密钥（RSA 或 ECDSA）。</para></summary>
    /// <value>The imported asymmetric key, an <see cref="RSA"/> or <see cref="ECDsa"/> instance
    /// created during loading; disposed by <see cref="Dispose"/>.
    /// <para>导入的非对称密钥，为加载期间创建的 <see cref="RSA"/> 或 <see cref="ECDsa"/> 实例；由
    /// <see cref="Dispose"/> 释放。</para></value>
    public AsymmetricAlgorithm Key { get; }

    /// <summary>The signer certificate (leaf).
    /// <para>签名者证书（叶子证书）。</para></summary>
    /// <value>The parsed leaf certificate, identical to the first entry of
    /// <see cref="CertificateChain"/>; disposed by <see cref="Dispose"/>.
    /// <para>解析出的叶子证书，与 <see cref="CertificateChain"/> 首项为同一实例；由
    /// <see cref="Dispose"/> 释放。</para></value>
    public X509Certificate2 Certificate { get; }

    /// <summary>The certificate chain to embed (leaf first).
    /// <para>要嵌入的证书链（叶子在前）。</para></summary>
    /// <value>A single-element list holding <see cref="Certificate"/>; intermediate certificates are
    /// not extracted from the input file.
    /// <para>仅含 <see cref="Certificate"/> 的单元素列表；不会从输入文件中提取中间证书。</para></value>
    public IReadOnlyList<X509Certificate2> CertificateChain { get; }

    /// <summary>
    /// Loads custom key material from user-supplied files: private key (PKCS#8 plain/encrypted,
    /// PKCS#1 or SEC1, PEM or DER) and certificate (PEM / DER / PFX). All inputs come from the user.
    /// <para>从用户提供的文件加载自定义密钥材料：私钥（PKCS#8 明文/加密、PKCS#1 或 SEC1，PEM 或 DER）
    /// 与证书（PEM/DER/PFX）。输入全部由用户提供。</para>
    /// </summary>
    /// <param name="keyPath">Private key file path (.pk8/.key/.pem).
    /// <para>私钥文件路径（.pk8/.key/.pem）。</para></param>
    /// <param name="keyPassword">Optional private key password (encrypted PKCS#8).
    /// <para>可选私钥密码（加密 PKCS#8）。</para></param>
    /// <param name="certPath">Certificate file path (.pem/.crt/.cer/.der/.pfx/.p12).
    /// <para>证书文件路径。</para></param>
    /// <param name="certPassword">Optional certificate (PFX) password.
    /// <para>可选证书（PFX）密码。</para></param>
    /// <returns>Loaded material whose key and certificate Subject Public Key Info were verified to
    /// be byte-identical; the caller owns the returned instance and must dispose it.
    /// <para>已加载的密钥材料；其公钥信息与证书逐字节一致的校验已通过；实例由调用方持有并负责释放。</para></returns>
    /// <exception cref="FileNotFoundException"><paramref name="keyPath"/> or <paramref name="certPath"/> is blank or does not exist. <para><paramref name="keyPath"/> 或 <paramref name="certPath"/> 为空或不存在。</para></exception>
    /// <exception cref="InvalidDataException">Either input file exceeds 4 MiB. <para>任一输入文件超过 4 MiB。</para></exception>
    /// <exception cref="CryptographicException">The key cannot be imported with the given password or format, the certificate block is malformed, or the key does not match the certificate. <para>无法按给定密码或格式导入私钥、证书块格式错误，或私钥与证书不匹配。</para></exception>
    /// <exception cref="FormatException">A PEM key body is not valid Base64. <para>PEM 私钥正文不是有效的 Base64。</para></exception>
    public static ApkSignerKeyMaterial Load(string keyPath, string? keyPassword, string certPath, string? certPassword)
    {
        if (string.IsNullOrWhiteSpace(keyPath) || !File.Exists(keyPath))
        {
            throw new FileNotFoundException($"Private key file not found: {keyPath}. 未找到私钥文件：{keyPath}", keyPath);
        }

        if (string.IsNullOrWhiteSpace(certPath) || !File.Exists(certPath))
        {
            throw new FileNotFoundException($"Certificate file not found: {certPath}. 未找到证书文件：{certPath}", certPath);
        }

        AsymmetricAlgorithm key = LoadRsaOrEcKey(keyPath, keyPassword, out var keyDisposable);
        X509Certificate2? certificate = null;
        try
        {
            certificate = LoadCertificate(certPath, certPassword);
            if (!key.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(certificate.PublicKey.ExportSubjectPublicKeyInfo()))
                throw new CryptographicException("Private key does not match certificate. 私钥与证书不匹配。");
            return new ApkSignerKeyMaterial(key, keyDisposable, certificate, [certificate]);
        }
        catch
        {
            keyDisposable?.Dispose();
            certificate?.Dispose();
            throw;
        }
    }

    private static AsymmetricAlgorithm LoadRsaOrEcKey(string path, string? password, out IDisposable? disposable)
    {
        var der = ReadKeyDerBytes(path);
        disposable = null;

        if (password is { Length: > 0 })
        {
            var rsaEnc = RSA.Create();
            if (TryImport(() => rsaEnc.ImportEncryptedPkcs8PrivateKey(password, der, out _)))
            {
                disposable = rsaEnc;
                return rsaEnc;
            }

            rsaEnc.Dispose();
            var ecEnc = ECDsa.Create();
            if (TryImport(() => ecEnc.ImportEncryptedPkcs8PrivateKey(password, der, out _)))
            {
                disposable = ecEnc;
                return ecEnc;
            }

            ecEnc.Dispose();
            throw new CryptographicException(
                $"Unable to import the encrypted private key '{path}' with the provided password. 无法使用给定密码导入加密私钥 '{path}'。");
        }

        var rsa = RSA.Create();
        if (TryImport(() => rsa.ImportPkcs8PrivateKey(der, out _))
            || TryImport(() => rsa.ImportRSAPrivateKey(der, out _)))
        {
            disposable = rsa;
            return rsa;
        }

        rsa.Dispose();
        var ec = ECDsa.Create();
        if (TryImport(() => ec.ImportPkcs8PrivateKey(der, out _))
            || TryImport(() => ec.ImportECPrivateKey(der, out _)))
        {
            disposable = ec;
            return ec;
        }

        ec.Dispose();
        throw new CryptographicException(
            $"Unsupported private key format in '{path}'; expected PKCS#8 or PKCS#1 (PEM or DER). '{path}' 中的私钥格式不受支持，期望 PKCS#8 或 PKCS#1（PEM 或 DER）。");
    }

    private static byte[] ReadKeyDerBytes(string path)
    {
        ValidateMaterialSize(path);
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length > 0 && bytes[0] == 0x30)
        {
            return bytes;
        }

        var text = System.Text.Encoding.ASCII.GetString(bytes);
        const string begin = "-----BEGIN ";
        const string end = "-----END ";
        var start = text.IndexOf(begin, StringComparison.Ordinal);
        if (start < 0)
        {
            throw new CryptographicException($"No PEM block or DER key found in '{path}'. '{path}' 中未找到 PEM 块或 DER 密钥。");
        }

        var lineEnd = text.IndexOf('\n', start);
        var stop = text.IndexOf(end, start + 1, StringComparison.Ordinal);
        if (lineEnd < 0 || stop < 0)
        {
            throw new CryptographicException($"Malformed PEM block in '{path}'. '{path}' 中的 PEM 块格式错误。");
        }

        var base64 = string.Concat(text[(lineEnd + 1)..stop].Where(static c => !char.IsWhiteSpace(c)));
        return Convert.FromBase64String(base64);
    }

    private static bool TryImport(Action import)
    {
        try
        {
            import();
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static X509Certificate2 LoadCertificate(string path, string? password)
    {
        ValidateMaterialSize(path);
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is ".pfx" or ".p12")
        {
            using var pfx = X509CertificateLoader.LoadPkcs12FromFile(path, password, X509KeyStorageFlags.EphemeralKeySet);
            return X509CertificateLoader.LoadCertificate(pfx.RawData);
        }

        var bytes = File.ReadAllBytes(path);
        if (bytes.Length > 0 && bytes[0] == 0x30)
        {
            return X509CertificateLoader.LoadCertificate(bytes);
        }

        var pem = System.Text.Encoding.ASCII.GetString(bytes);
        const string begin = "-----BEGIN CERTIFICATE-----";
        const string end = "-----END CERTIFICATE-----";
        var start = pem.IndexOf(begin, StringComparison.Ordinal);
        if (start < 0)
        {
            throw new CryptographicException($"No certificate found in '{path}'. '{path}' 中未找到证书。");
        }

        var lineEnd = pem.IndexOf('\n', start);
        var stop = pem.IndexOf(end, start, StringComparison.Ordinal);
        if (lineEnd < 0 || stop < 0)
        {
            throw new CryptographicException($"Malformed certificate PEM in '{path}'. '{path}' 中的证书 PEM 格式错误。");
        }

        var base64 = string.Concat(pem[(lineEnd + 1)..stop].Where(static c => !char.IsWhiteSpace(c)));
        return X509CertificateLoader.LoadCertificate(Convert.FromBase64String(base64));
    }

    /// <summary>
    /// Releases the created signing key instance and the leaf certificate; because the chain's only
    /// entry is the same certificate object, it is disposed exactly once.
    /// <para>释放加载期间创建的签名密钥实例与叶子证书；证书链中唯一条目即同一证书对象，因此只释放一次。</para>
    /// </summary>
    public void Dispose()
    {
        _disposableKey?.Dispose();
        Certificate.Dispose();
    }

    private static void ValidateMaterialSize(string path)
    {
        if (new FileInfo(path).Length > 4 * 1024 * 1024)
            throw new InvalidDataException("Signing material exceeds 4 MiB. 签名材料超过 4 MiB。");
    }
}