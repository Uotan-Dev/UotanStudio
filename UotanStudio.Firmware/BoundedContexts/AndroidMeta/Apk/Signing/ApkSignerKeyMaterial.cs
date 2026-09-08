using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace UotanStudio.Firmware.BoundedContexts.AndroidMeta.Apk.Signing;

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

    /// <summary>The signing key (RSA or ECDSA).</summary>
    /// <para>签名密钥（RSA 或 ECDSA）。</para>
    public AsymmetricAlgorithm Key { get; }

    /// <summary>The signer certificate (leaf).</summary>
    /// <para>签名者证书（叶子证书）。</para>
    public X509Certificate2 Certificate { get; }

    /// <summary>The certificate chain to embed (leaf first).</summary>
    /// <para>要嵌入的证书链（叶子在前）。</para>
    public IReadOnlyList<X509Certificate2> CertificateChain { get; }

    /// <summary>
    /// Loads custom key material from user-supplied files: private key (PKCS#8 plain/encrypted,
    /// PKCS#1 or SEC1, PEM or DER) and certificate (PEM / DER / PFX). All inputs come from the user.
    /// <para>从用户提供的文件加载自定义密钥材料：私钥（PKCS#8 明文/加密、PKCS#1 或 SEC1，PEM 或 DER）
    /// 与证书（PEM/DER/PFX）。输入全部由用户提供。</para>
    /// </summary>
    /// <param name="keyPath">Private key file path (.pk8/.key/.pem). 私钥文件路径（.pk8/.key/.pem）。</param>
    /// <param name="keyPassword">Optional private key password (encrypted PKCS#8). 可选私钥密码（加密 PKCS#8）。</param>
    /// <param name="certPath">Certificate file path (.pem/.crt/.cer/.der/.pfx/.p12). 证书文件路径。</param>
    /// <param name="certPassword">Optional certificate (PFX) password. 可选证书（PFX）密码。</param>
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
        // Both PEM and DER inputs are normalized to DER bytes; PEM blocks are base64-decoded.
        // PEM 与 DER 输入统一归一化为 DER 字节；PEM 块经 base64 解码。
        var der = ReadKeyDerBytes(path);
        disposable = null;

        if (password is { Length: > 0 })
        {
            // Encrypted PKCS#8 (-----BEGIN ENCRYPTED PRIVATE KEY-----).
            // 加密 PKCS#8（-----BEGIN ENCRYPTED PRIVATE KEY-----）。
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

        // Plaintext: try RSA (PKCS#8 then PKCS#1), then EC (PKCS#8 then SEC1).
        // 明文：先试 RSA（PKCS#8 再 PKCS#1），再试 EC（PKCS#8 再 SEC1）。
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
            // DER (SEQUENCE).
            // DER（SEQUENCE）。
            return bytes;
        }

        // PEM: decode the first BEGIN/END block body.
        // PEM：解码首个 BEGIN/END 块内容。
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
            // DER certificate.
            // DER 证书。
            return X509CertificateLoader.LoadCertificate(bytes);
        }

        // PEM certificate (first block is the leaf; a following chain is embedded as-is).
        // PEM 证书（首个块为叶子；后续链证书保持原样）。
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

    /// <inheritdoc />
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
