using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace UotanStudio.Firmware.HarmonyMeta.Hap;

/// <summary>
/// Signing material resolved from files: the keystore private key, the application certificate chain (leaf first)
/// and the signed profile. The chain leaf is bound to the keystore key here so the signer receives one consistent
/// certificate. <para>从文件解析的签名材料：keystore 私钥、应用证书链（叶子在前）与已签 Profile。
/// 链叶子在此与 keystore 私钥绑定，使签名器拿到一致的证书。</para>
/// </summary>
/// <remarks>
/// Loading is bounded: every input file is capped, PKCS#12 keys stay ephemeral where the platform supports it, and
/// the leaf/keystore SPKI must agree before signing proceeds. Dispose releases the key and the parsed certificates.
/// <para>加载有界：每个输入文件限额，PKCS#12 密钥在平台支持时保持临时态，签名前校验叶子与 keystore 的
/// SPKI 一致。Dispose 释放密钥与已解析证书。</para>
/// </remarks>
public sealed class HapSignerMaterial : IDisposable
{
    private const int MaximumKeystoreBytes = 4 << 20;
    private const int MaximumCertificateBytes = 1 << 20;
    private const int MaximumProfileBytes = 4 << 20;
    private const int MaximumChainCertificates = 8;

    private HapSignerMaterial(X509Certificate2 keystoreCertificate, AsymmetricAlgorithm key,
        X509Certificate2 signingCertificate, IReadOnlyList<X509Certificate2> chain, byte[] profile)
    {
        KeystoreCertificate = keystoreCertificate;
        Key = key;
        SigningCertificate = signingCertificate;
        Chain = chain;
        Profile = profile;
    }

    /// <summary>Gets the certificate stored in the keystore. <para>keystore 内的证书。</para></summary>
    public X509Certificate2 KeystoreCertificate { get; }

    /// <summary>Gets the private key from the keystore. <para>keystore 内的私钥。</para></summary>
    public AsymmetricAlgorithm Key { get; }

    /// <summary>Gets the leaf certificate bound to the key. <para>与私钥绑定的叶子证书。</para></summary>
    public X509Certificate2 SigningCertificate { get; }

    /// <summary>Gets the embedded chain certificates (leaf excluded). <para>内嵌的链上证书（不含叶子）。</para></summary>
    public IReadOnlyList<X509Certificate2> Chain { get; }

    /// <summary>Gets the raw profile bytes embedded verbatim. <para>逐字内嵌的原始 Profile 字节。</para></summary>
    public byte[] Profile { get; }

    /// <summary>
    /// Loads the material from a PKCS#12/JKS keystore, an application certificate chain and a profile.
    /// <para>从 PKCS#12/JKS keystore、应用证书链与 Profile 加载材料。</para>
    /// </summary>
    /// <param name="keystoreBytes">Keystore file bytes. <para>keystore 文件字节。</para></param>
    /// <param name="keystorePassword">Keystore password. <para>keystore 口令。</para></param>
    /// <param name="keyAlias">Key alias matched against the store's friendly name.
    /// <para>与 store 友好名匹配的密钥别名。</para></param>
    /// <param name="certificateBytes">Certificate chain file bytes, PEM or DER, leaf first.
    /// <para>证书链文件字节，PEM 或 DER，叶子在前。</para></param>
    /// <param name="profileBytes">Signed profile bytes. <para>已签 Profile 字节。</para></param>
    /// <returns>Loaded material. <para>已加载的材料。</para></returns>
    /// <exception cref="InvalidDataException">An input is out of budget, unparsable, or the chain leaf disagrees
    /// with the keystore key. <para>输入越界、无法解析，或链叶子与 keystore 密钥不一致。</para></exception>
    public static HapSignerMaterial Load(byte[] keystoreBytes, string keystorePassword, string keyAlias,
        byte[] certificateBytes, byte[] profileBytes)
    {
        if (keystoreBytes.Length is 0 or > MaximumKeystoreBytes)
        {
            throw new InvalidDataException($"Keystore size {keystoreBytes.Length} is out of range. keystore 大小 {keystoreBytes.Length} 越界。");
        }

        if (certificateBytes.Length is 0 or > MaximumCertificateBytes)
        {
            throw new InvalidDataException($"Certificate size {certificateBytes.Length} is out of range. 证书大小 {certificateBytes.Length} 越界。");
        }

        if (profileBytes.Length is 0 or > MaximumProfileBytes)
        {
            throw new InvalidDataException($"Profile size {profileBytes.Length} is out of range. Profile 大小 {profileBytes.Length} 越界。");
        }

        var (keystoreCertificate, key) = LoadKeystoreKey(keystoreBytes, keystorePassword, keyAlias);
        var chain = LoadCertificateChain(certificateBytes);
        var leaf = chain[0];
        if (!leaf.PublicKey.EncodedKeyValue.RawData.AsSpan().SequenceEqual(keystoreCertificate.PublicKey.EncodedKeyValue.RawData))
        {
            throw new InvalidDataException("The certificate chain leaf does not match the keystore key. 证书链叶子与 keystore 密钥不匹配。");
        }

        // The chain file carries public certificates only; bind the keystore key to the leaf so the signer
        // receives one certificate that can actually sign.
        X509Certificate2 signingCertificate = key switch
        {
            RSA rsa => leaf.CopyWithPrivateKey(rsa),
            ECDsa ecdsa => leaf.CopyWithPrivateKey(ecdsa),
            DSA dsa => leaf.CopyWithPrivateKey(dsa),
            _ => throw new InvalidDataException("The keystore key is neither RSA nor ECDSA. keystore 密钥既非 RSA 也非 ECDSA。"),
        };
        leaf.Dispose();
        return new HapSignerMaterial(keystoreCertificate, key, signingCertificate, chain.Skip(1).ToList(), profileBytes);
    }

    private static (X509Certificate2, AsymmetricAlgorithm) LoadKeystoreKey(byte[] bytes, string password, string alias)
    {
        X509Certificate2Collection collection;
        try
        {
            collection = X509CertificateLoader.LoadPkcs12Collection(bytes, password);
        }
        catch (CryptographicException exception)
        {
            throw new InvalidDataException($"The keystore cannot be opened with the supplied password: {exception.Message}. " +
                $"给定口令无法打开 keystore：{exception.Message}。");
        }

        foreach (var certificate in collection)
        {
            if (!certificate.HasPrivateKey)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(alias)
                && string.Equals(certificate.FriendlyName, alias, StringComparison.Ordinal))
            {
                return (certificate, ExtractKey(certificate));
            }
        }

        foreach (var certificate in collection)
        {
            if (certificate.HasPrivateKey)
            {
                return (certificate, ExtractKey(certificate));
            }
        }

        throw new InvalidDataException("The keystore carries no private key entry. keystore 不含私钥条目。");
    }

    private static AsymmetricAlgorithm ExtractKey(X509Certificate2 certificate)
    {
        var rsa = certificate.GetRSAPrivateKey();
        if (rsa is not null)
        {
            return rsa;
        }

        var ecdsa = certificate.GetECDsaPrivateKey();
        if (ecdsa is not null)
        {
            return ecdsa;
        }

        throw new InvalidDataException("The keystore key is neither RSA nor ECDSA. keystore 密钥既非 RSA 也非 ECDSA。");
    }

    private static List<X509Certificate2> LoadCertificateChain(byte[] bytes)
    {
        var certificates = new List<X509Certificate2>();
        var text = System.Text.Encoding.Latin1.GetString(bytes);
        const string beginMarker = "-----BEGIN CERTIFICATE-----";
        const string endMarker = "-----END CERTIFICATE-----";
        var position = 0;
        while (certificates.Count < MaximumChainCertificates)
        {
            var begin = text.IndexOf(beginMarker, position, StringComparison.Ordinal);
            if (begin < 0)
            {
                break;
            }

            var end = text.IndexOf(endMarker, begin, StringComparison.Ordinal);
            if (end < 0)
            {
                throw new InvalidDataException("The certificate chain PEM block is unterminated. 证书链 PEM 块未闭合。");
            }

            var body = text[(begin + beginMarker.Length)..end];
            var der = Convert.FromBase64String(string.Concat(body.Where(IsBase64Character)));
            certificates.Add(X509CertificateLoader.LoadCertificate(der));
            position = end + endMarker.Length;
        }

        if (certificates.Count == 0)
        {
            try
            {
                certificates.Add(X509CertificateLoader.LoadCertificate(bytes));
            }
            catch (CryptographicException exception)
            {
                throw new InvalidDataException($"The certificate file is neither PEM nor DER: {exception.Message}. " +
                    $"证书文件既非 PEM 也非 DER：{exception.Message}。");
            }
        }

        return certificates;
    }

    private static bool IsBase64Character(char value)
        => char.IsAsciiLetterOrDigit(value) || value is '+' or '/' or '=';

    /// <inheritdoc />
    public void Dispose()
    {
        (Key as RSA)?.Dispose();
        (Key as ECDsa)?.Dispose();
        foreach (var certificate in Chain)
        {
            certificate.Dispose();
        }

        SigningCertificate.Dispose();
        KeystoreCertificate.Dispose();
    }
}