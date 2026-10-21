using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UotanStudio.Services.Projects.Schemes;

/// <summary>
/// Signs and verifies scheme packages with the publisher identity and fingerprint rules shared by visual plugins.
/// <para>使用与可视化插件相同的发布者身份与指纹规则对 Scheme 软件包签名与验签。</para>
/// </summary>
/// <remarks>
/// The canonical payload is the serialized package with <see cref="SchemePackage.Signature"/> removed and the
/// publisher block included, so any edit to a signed package invalidates its signature until it is signed again.
/// Signing and verification temporarily clear the in-memory signature; one package instance must not be signed or
/// verified concurrently with another operation on the same instance.
/// <para>规范化载荷是移除 <see cref="SchemePackage.Signature"/> 并保留发布者块的序列化软件包，因此任何改动都会使既有签名失效，
/// 直到再次签名。签名与验签会临时清除内存中的签名；同一软件包实例不应与针对该实例的其他操作并发签名或验签。</para>
/// </remarks>
public static class SchemeSigner
{
    private const int MinimumRsaBits = 2048;
    private const int MaximumRsaBits = 4096;
    private const int MaximumPublisherNameLength = 128;

    /// <summary>
    /// Signs a scheme package and records the publisher identity and signature on it.
    /// <para>对 Scheme 软件包签名，并在软件包上记录发布者身份与签名。</para>
    /// </summary>
    /// <param name="package">Package to sign; its existing signature is replaced.
    /// <para>要签名的软件包；其既有签名会被替换。</para></param>
    /// <param name="privateKey">RSA key of 2048 to 4096 bits, or a NIST P-256 ECDSA key.
    /// <para>2048 到 4096 位的 RSA 密钥，或 NIST P-256 ECDSA 密钥。</para></param>
    /// <param name="publisherId">Stable publisher identifier using lowercase ASCII letters, digits, dots, underscores or hyphens, 3 to 128 characters.
    /// <para>稳定的发布者标识，使用小写 ASCII 字母、数字、点、下划线或连字符，长度 3 到 128。</para></param>
    /// <param name="publisherName">Display name of 1 to 128 characters.
    /// <para>1 到 128 个字符的显示名称。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="package"/> or <paramref name="privateKey"/> is <see langword="null"/>.
    /// <para><paramref name="package"/> 或 <paramref name="privateKey"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException">The publisher id, publisher name, key algorithm or key size is unsupported.
    /// <para>发布者标识、发布者名称、密钥算法或密钥长度不受支持。</para></exception>
    public static void Sign(SchemePackage package, AsymmetricAlgorithm privateKey, string publisherId,
        string publisherName)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(privateKey);
        if (!VisualPluginValidation.IsValidPluginId(publisherId))
        {
            throw new ArgumentException(
                "Publisher id must use lowercase ASCII letters, digits, dots, underscores, or hyphens.",
                nameof(publisherId));
        }
        var name = publisherName?.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaximumPublisherNameLength)
        {
            throw new ArgumentException("Publisher name must contain between 1 and 128 characters.",
                nameof(publisherName));
        }
        var algorithm = AlgorithmFor(privateKey)
            ?? throw new ArgumentException("Signature keys must be RSA 2048-4096 or ECDSA P-256.", nameof(privateKey));

        package.Publisher = new VisualPluginPublisher
        {
            Id = publisherId,
            Name = name,
            Fingerprint = PluginSignature.ComputeFingerprint(privateKey),
            PublicKey = PluginSignature.ExportPublicKey(privateKey)
        };
        package.Signature = null;
        var payload = ComputePayload(package);
        var value = privateKey switch
        {
            RSA rsa => rsa.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pss),
            ECDsa ec => ec.SignData(payload, HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation),
            _ => throw new ArgumentException("Unsupported signature key.", nameof(privateKey))
        };
        package.Signature = new VisualPluginSignature
        {
            Algorithm = algorithm,
            Value = Convert.ToBase64String(value)
        };
    }

    /// <summary>
    /// Verifies the signature carried by a scheme package, if any.
    /// <para>校验 Scheme 软件包携带的签名（若有）。</para>
    /// </summary>
    /// <param name="package">Package to check; the signature covers metadata, inputs, assets, stages and the publisher block.
    /// <para>待检查的软件包；签名覆盖元数据、输入、资产、阶段与发布者块。</para></param>
    /// <returns>The verification outcome; an unsigned package reports <see cref="PluginSignatureVerification.IsSigned"/>
    /// <see langword="false"/> with <see cref="PluginSignatureVerification.IsValid"/> <see langword="true"/>.
    /// <para>校验结果；未签名包返回 <see cref="PluginSignatureVerification.IsSigned"/> 为 <see langword="false"/> 且
    /// <see cref="PluginSignatureVerification.IsValid"/> 为 <see langword="true"/>。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="package"/> is <see langword="null"/>.
    /// <para><paramref name="package"/> 为 <see langword="null"/>。</para></exception>
    public static PluginSignatureVerification Verify(SchemePackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var signature = package.Signature;
        if (signature is null || string.IsNullOrEmpty(signature.Algorithm) || string.IsNullOrEmpty(signature.Value))
        {
            return new PluginSignatureVerification(false, true, null);
        }
        var publisher = package.Publisher;
        if (publisher is null || string.IsNullOrWhiteSpace(publisher.PublicKey)
            || string.IsNullOrWhiteSpace(publisher.Fingerprint))
        {
            return new PluginSignatureVerification(true, false, "Scheme signature has no publisher identity.");
        }
        if (!PluginSignatureAlgorithms.IsKnown(signature.Algorithm))
        {
            return new PluginSignatureVerification(true, false, "Scheme signature uses an unsupported algorithm.");
        }
        if (signature.Value.Length > PluginSignature.MaximumSignatureLength)
        {
            return new PluginSignatureVerification(true, false, "Scheme signature value is malformed.");
        }
        if (!PluginSignature.IsValidFingerprint(publisher.Fingerprint)
            || !PluginSignature.IsValidPublicKey(publisher.PublicKey))
        {
            return new PluginSignatureVerification(true, false,
                "Scheme publisher fingerprint or public key is malformed.");
        }
        try
        {
            using var key = PluginSignature.ImportPublicKey(publisher.PublicKey);
            if (!string.Equals(PluginSignature.ComputeFingerprint(key), publisher.Fingerprint, StringComparison.Ordinal))
            {
                return new PluginSignatureVerification(true, false,
                    "Scheme publisher fingerprint does not match its public key.");
            }
            var payload = ComputePayload(package);
            var value = Convert.FromBase64String(signature.Value);
            var valid = signature.Algorithm switch
            {
                PluginSignatureAlgorithms.RsaPssSha256 => key is RSA rsa
                    && rsa.VerifyData(payload, value, HashAlgorithmName.SHA256, RSASignaturePadding.Pss),
                PluginSignatureAlgorithms.EcdsaP256Sha256 => key is ECDsa ec && ec.KeySize == 256
                    && ec.VerifyData(payload, value, HashAlgorithmName.SHA256,
                        DSASignatureFormat.IeeeP1363FixedFieldConcatenation),
                _ => false
            };
            return valid
                ? new PluginSignatureVerification(true, true, null)
                : new PluginSignatureVerification(true, false, "Scheme signature does not match its payload.");
        }
        catch (Exception error) when (error is CryptographicException or InvalidDataException or FormatException
                                          or ArgumentException)
        {
            return new PluginSignatureVerification(true, false,
                "Scheme signature cannot be verified: " + error.Message);
        }
    }

    /// <summary>
    /// Computes the canonical bytes a scheme signature covers.
    /// <para>计算 Scheme 签名所覆盖的规范化字节。</para>
    /// </summary>
    /// <param name="package">Package whose signature field is ignored; optional members are normalized first.
    /// <para>忽略其签名字段的软件包；可选成员会先被规范化。</para></param>
    /// <returns>Compact JSON of the package with the signature removed and every other member retained.
    /// <para>移除签名并保留其他全部成员后的紧凑 JSON。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="package"/> is <see langword="null"/>.
    /// <para><paramref name="package"/> 为 <see langword="null"/>。</para></exception>
    public static byte[] ComputePayload(SchemePackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        package.Normalize();
        var signature = package.Signature;
        package.Signature = null;
        try
        {
            return JsonSerializer.SerializeToUtf8Bytes(package,
                SchemeSignatureJsonContext.Default.SchemePackage);
        }
        finally
        {
            package.Signature = signature;
        }
    }

    /// <summary>
    /// Resolves the trust relationship of a scheme publisher using the visual-plugin publisher pins.
    /// <para>使用可视化插件的发布者固定记录解析 Scheme 发布者的信任关系。</para>
    /// </summary>
    /// <param name="package">Package to inspect; an unsigned package reports <see cref="PublisherTrustStatus.Unsigned"/>.
    /// <para>待检查的软件包；未签名包返回 <see cref="PublisherTrustStatus.Unsigned"/>。</para></param>
    /// <param name="trustStore">Publisher pin store to consult. <para>要查询的发布者固定记录库。</para></param>
    /// <returns>The trust relationship for the embedded publisher identity.
    /// <para>内嵌发布者身份对应的信任关系。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="package"/> or <paramref name="trustStore"/> is <see langword="null"/>.
    /// <para><paramref name="package"/> 或 <paramref name="trustStore"/> 为 <see langword="null"/>。</para></exception>
    public static PublisherTrustStatus GetTrustStatus(SchemePackage package, PublisherTrustStore trustStore)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(trustStore);
        if (!package.IsSigned)
        {
            return PublisherTrustStatus.Unsigned;
        }
        var publisher = package.Publisher;
        if (publisher is null || string.IsNullOrWhiteSpace(publisher.Id)
            || string.IsNullOrWhiteSpace(publisher.Fingerprint))
        {
            return PublisherTrustStatus.UnknownPublisher;
        }
        var record = trustStore.Find(publisher.Id);
        if (record is null)
        {
            return PublisherTrustStatus.UnknownPublisher;
        }
        return string.Equals(record.Fingerprint, publisher.Fingerprint, StringComparison.OrdinalIgnoreCase)
            ? PublisherTrustStatus.Trusted
            : PublisherTrustStatus.FingerprintMismatch;
    }

    /// <summary>
    /// Pins the publisher identity of a signed scheme, replacing a rotated fingerprint.
    /// <para>固定已签名 Scheme 的发布者身份；指纹轮换时替换旧指纹。</para>
    /// </summary>
    /// <param name="package">Signed package whose publisher is pinned; the caller must have obtained explicit user confirmation.
    /// <para>要固定其发布者的已签名软件包；调用方必须已经取得用户的显式确认。</para></param>
    /// <param name="trustStore">Publisher pin store to update. <para>要更新的发布者固定记录库。</para></param>
    /// <returns>The pin now stored for the publisher. <para>当前为该发布者保存的固定记录。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="package"/> or <paramref name="trustStore"/> is <see langword="null"/>.
    /// <para><paramref name="package"/> 或 <paramref name="trustStore"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="InvalidOperationException">The package carries no signature or publisher identity.
    /// <para>软件包未携带签名或发布者身份。</para></exception>
    /// <exception cref="ArgumentException">The publisher id, name or fingerprint is malformed.
    /// <para>发布者标识、名称或指纹格式非法。</para></exception>
    public static PublisherTrustRecord TrustPublisher(SchemePackage package, PublisherTrustStore trustStore)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(trustStore);
        if (!package.IsSigned || package.Publisher is null)
        {
            throw new InvalidOperationException("Only a signed scheme package carries a publisher to pin.");
        }
        var manifest = new VisualPluginManifest
        {
            Id = package.Id,
            Name = package.Name,
            Version = package.Version,
            Publisher = package.Publisher,
            Signature = package.Signature
        };
        return trustStore.Trust(manifest);
    }

    private static string? AlgorithmFor(AsymmetricAlgorithm key) => key switch
    {
        RSA rsa when rsa.KeySize is >= MinimumRsaBits and <= MaximumRsaBits =>
            PluginSignatureAlgorithms.RsaPssSha256,
        ECDsa ec when ec.KeySize == 256 => PluginSignatureAlgorithms.EcdsaP256Sha256,
        _ => null
    };
}

[JsonSerializable(typeof(SchemePackage))]
[JsonSerializable(typeof(VisualPluginSignature))]
[JsonSourceGenerationOptions(
    WriteIndented = false,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class SchemeSignatureJsonContext : JsonSerializerContext;