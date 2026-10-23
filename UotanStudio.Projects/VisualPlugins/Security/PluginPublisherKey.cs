using System.Security.Cryptography;

namespace UotanStudio.Projects;

/// <summary>
/// Key generation and exchange primitives for plugin publishers; the private key stays outside a package.
/// <para>插件发布者的密钥生成与交换原语；私钥不会进入包。</para>
/// </summary>
/// <remarks>
/// Both supported algorithms are implemented by the platform cryptographic providers, so they are safe under
/// Native AOT and trimming without dynamic code.
/// <para>两种受支持的算法都由平台加密提供程序实现，因此在 Native AOT 与裁剪下无需动态代码即可安全运行。</para>
/// </remarks>
public static class PluginPublisherKey
{
    /// <summary>Default RSA key size for a new publisher key. <para>新发布者密钥的默认 RSA 长度。</para></summary>
    public const int DefaultRsaBits = 3072;

    /// <summary>Largest RSA key size accepted for signing. <para>可用于签名的最大 RSA 长度。</para></summary>
    public const int MaximumRsaBits = 4096;

    /// <summary>Smallest RSA key size accepted for signing. <para>可用于签名的最小 RSA 长度。</para></summary>
    public const int MinimumRsaBits = 2048;

    /// <summary>Maximum accepted private-key text length. <para>允许的私钥文本最大长度。</para></summary>
    public const int MaximumPrivateKeyLength = 16384;

    /// <summary>
    /// Creates a new RSA publisher key.
    /// <para>创建新的 RSA 发布者密钥。</para>
    /// </summary>
    /// <param name="bits">Key size; must be between <see cref="MinimumRsaBits"/> and <see cref="MaximumRsaBits"/>.
    /// <para>密钥长度；必须位于 <see cref="MinimumRsaBits"/> 与 <see cref="MaximumRsaBits"/> 之间。</para></param>
    /// <returns>A new disposable RSA key. <para>新的可释放 RSA 密钥。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bits"/> is outside the accepted range.
    /// <para><paramref name="bits"/> 超出允许范围。</para></exception>
    public static RSA CreateRsa(int bits = DefaultRsaBits)
    {
        if (bits is < MinimumRsaBits or > MaximumRsaBits)
        {
            throw new ArgumentOutOfRangeException(nameof(bits),
                $"Publisher RSA keys must be between {MinimumRsaBits} and {MaximumRsaBits} bits.");
        }
        return RSA.Create(bits);
    }

    /// <summary>
    /// Creates a new NIST P-256 ECDSA publisher key.
    /// <para>创建新的 NIST P-256 ECDSA 发布者密钥。</para>
    /// </summary>
    /// <returns>A new disposable ECDSA key. <para>新的可释放 ECDSA 密钥。</para></returns>
    public static ECDsa CreateEcdsaP256() => ECDsa.Create(ECCurve.NamedCurves.nistP256);

    /// <summary>
    /// Exports a private key as unencrypted PKCS#8 PEM.
    /// <para>把私钥导出为未加密的 PKCS#8 PEM。</para>
    /// </summary>
    /// <param name="key">RSA or ECDSA private key. <para>RSA 或 ECDSA 私钥。</para></param>
    /// <returns>The PKCS#8 PEM text. <para>PKCS#8 PEM 文本。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.
    /// <para><paramref name="key"/> 为 <see langword="null"/>。</para></exception>
    public static string ExportPrivateKeyPem(AsymmetricAlgorithm key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return key.ExportPkcs8PrivateKeyPem();
    }

    /// <summary>
    /// Imports an unencrypted PKCS#8 PEM RSA or ECDSA private key.
    /// <para>导入未加密的 PKCS#8 PEM RSA 或 ECDSA 私钥。</para>
    /// </summary>
    /// <param name="pem">PEM text, bounded by <see cref="MaximumPrivateKeyLength"/>.
    /// <para>PEM 文本，受 <see cref="MaximumPrivateKeyLength"/> 限制。</para></param>
    /// <returns>A disposable key holding the private material. <para>持有私钥材料的可释放密钥。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="pem"/> is null, empty or longer than the bound.
    /// <para><paramref name="pem"/> 为 null、空或超出长度上限。</para></exception>
    /// <exception cref="InvalidDataException">The text is not an unencrypted RSA or ECDSA PKCS#8 private key.
    /// <para>文本不是未加密的 RSA 或 ECDSA PKCS#8 私钥。</para></exception>
    public static AsymmetricAlgorithm ImportPrivateKeyPem(string pem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pem);
        if (pem.Length > MaximumPrivateKeyLength)
        {
            throw new ArgumentException("Publisher private key exceeds its size limit.", nameof(pem));
        }
        var rsa = RSA.Create();
        if (TryImportPem(rsa, pem))
        {
            return rsa;
        }
        rsa.Dispose();
        var ec = ECDsa.Create();
        if (TryImportPem(ec, pem))
        {
            return ec;
        }
        ec.Dispose();
        throw new InvalidDataException("Publisher private key is not an unencrypted PKCS#8 PEM RSA or ECDSA key.");
    }

    /// <summary>
    /// Exports a public key as SubjectPublicKeyInfo PEM.
    /// <para>把公钥导出为 SubjectPublicKeyInfo PEM。</para>
    /// </summary>
    /// <param name="key">RSA or ECDSA key. <para>RSA 或 ECDSA 密钥。</para></param>
    /// <returns>The PEM text. <para>PEM 文本。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.
    /// <para><paramref name="key"/> 为 <see langword="null"/>。</para></exception>
    public static string ExportPublicKeyPem(AsymmetricAlgorithm key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return key.ExportSubjectPublicKeyInfoPem();
    }

    /// <summary>
    /// Imports a SubjectPublicKeyInfo PEM RSA or ECDSA public key.
    /// <para>导入 SubjectPublicKeyInfo PEM RSA 或 ECDSA 公钥。</para>
    /// </summary>
    /// <param name="pem">PEM text, bounded by <see cref="MaximumPrivateKeyLength"/>.
    /// <para>PEM 文本，受 <see cref="MaximumPrivateKeyLength"/> 限制。</para></param>
    /// <returns>A disposable public key. <para>可释放的公钥。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="pem"/> is null, empty or longer than the bound.
    /// <para><paramref name="pem"/> 为 null、空或超出长度上限。</para></exception>
    /// <exception cref="InvalidDataException">The text is not an RSA or ECDSA SubjectPublicKeyInfo.
    /// <para>文本不是 RSA 或 ECDSA SubjectPublicKeyInfo。</para></exception>
    public static AsymmetricAlgorithm ImportPublicKeyPem(string pem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pem);
        if (pem.Length > MaximumPrivateKeyLength)
        {
            throw new ArgumentException("Publisher public key exceeds its size limit.", nameof(pem));
        }
        var rsa = RSA.Create();
        if (TryImportPem(rsa, pem))
        {
            return rsa;
        }
        rsa.Dispose();
        var ec = ECDsa.Create();
        if (TryImportPem(ec, pem))
        {
            return ec;
        }
        ec.Dispose();
        throw new InvalidDataException("Publisher public key is not an RSA or ECDSA SubjectPublicKeyInfo PEM.");
    }

    private static bool TryImportPem(AsymmetricAlgorithm key, string pem)
    {
        try
        {
            key.ImportFromPem(pem);
            return true;
        }
        catch (Exception error) when (error is CryptographicException or ArgumentException)
        {
            return false;
        }
    }
}