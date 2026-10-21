using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using UotanStudio.Hash.Digests;

namespace UotanStudio.Services.Projects;

/// <summary>
/// Stable signature algorithm names recorded in a package manifest.
/// <para>记录在包清单中的稳定签名算法名称。</para>
/// </summary>
public static class PluginSignatureAlgorithms
{
    /// <summary>RSA PKCS#1 v2.1 PSS padding over SHA-256. <para>基于 SHA-256 的 RSA PKCS#1 v2.1 PSS 填充。</para></summary>
    public const string RsaPssSha256 = "RSA-PSS-SHA256";

    /// <summary>ECDSA over NIST P-256 with SHA-256. <para>基于 NIST P-256 与 SHA-256 的 ECDSA。</para></summary>
    public const string EcdsaP256Sha256 = "ECDSA-P256-SHA256";

    /// <summary>
    /// Gets whether a manifest algorithm name is implemented by this host.
    /// <para>获取宿主是否实现了某个清单算法名称。</para>
    /// </summary>
    /// <param name="algorithm">Algorithm name from the manifest. <para>清单中的算法名称。</para></param>
    /// <returns><see langword="true"/> for a supported algorithm name.
    /// <para>受支持的算法名称返回 <see langword="true"/>。</para></returns>
    public static bool IsKnown(string? algorithm) => algorithm is RsaPssSha256 or EcdsaP256Sha256;
}

/// <summary>
/// Publisher identity embedded in a signed manifest; the public key makes the package self-verifying and
/// the fingerprint is what the trust store pins.
/// <para>内嵌在已签名清单中的发布者身份；公钥使包可自校验，指纹则是信任库固定的对象。</para>
/// </summary>
public sealed class VisualPluginPublisher
{
    /// <summary>Stable reverse-domain-style publisher identifier. <para>稳定的反向域名风格发布者标识。</para></summary>
    /// <value>Identity compared against the trust store; it must stay constant across package versions.
    /// <para>用于与信任库比对的身份；跨包版本必须保持不变。</para></value>
    public string Id { get; set; } = string.Empty;

    /// <summary>User-visible publisher name. <para>用户可见的发布者名称。</para></summary>
    /// <value>Display label shown beside the fingerprint; the trust key remains <see cref="Id"/>.
    /// <para>与指纹一同展示的标签；信任键仍是 <see cref="Id"/>。</para></value>
    public string Name { get; set; } = string.Empty;

    /// <summary>Public-key fingerprint in <c>SHA256:</c> plus uppercase hex form. <para>形如 <c>SHA256:</c> 加大写十六进制的公钥指纹。</para></summary>
    /// <value>SHA-256 over the SubjectPublicKeyInfo encoding; the trust store pins this value.
    /// <para>对 SubjectPublicKeyInfo 编码计算的 SHA-256；信任库固定该值。</para></value>
    public string Fingerprint { get; set; } = string.Empty;

    /// <summary>Base64 SubjectPublicKeyInfo encoding of the publisher key. <para>发布者公钥的 Base64 SubjectPublicKeyInfo 编码。</para></summary>
    /// <value>The key the signature is verified with; an RSA or P-256 ECDSA public key.
    /// <para>用于校验签名的公钥；RSA 或 P-256 ECDSA 公钥。</para></value>
    public string PublicKey { get; set; } = string.Empty;
}

/// <summary>
/// Detached or embedded package signature value.
/// <para>内嵌或分离的包签名值。</para>
/// </summary>
public sealed class VisualPluginSignature
{
    /// <summary>Algorithm name, one of <see cref="PluginSignatureAlgorithms"/>. <para>算法名称，取自 <see cref="PluginSignatureAlgorithms"/>。</para></summary>
    /// <value>The algorithm used to produce <see cref="Value"/>.
    /// <para>生成 <see cref="Value"/> 所用的算法。</para></value>
    public string Algorithm { get; set; } = string.Empty;

    /// <summary>Base64 signature bytes. <para>Base64 编码的签名字节。</para></summary>
    /// <value>The signature over the canonical package payload.
    /// <para>对规范化包载荷生成的签名。</para></value>
    public string Value { get; set; } = string.Empty;
}

/// <summary>
/// Outcome of verifying one package signature.
/// <para>校验单个包签名的结果。</para>
/// </summary>
/// <param name="IsSigned">Whether the package carries a signature. <para>包是否携带签名。</para></param>
/// <param name="IsValid">Whether verification succeeded; an unsigned package is not a verification failure.
/// <para>校验是否成功；未签名包不算校验失败。</para></param>
/// <param name="Error">Stable failure description, or <see langword="null"/> when valid.
/// <para>稳定的失败说明；有效时为 <see langword="null"/>。</para></param>
public sealed record PluginSignatureVerification(bool IsSigned, bool IsValid, string? Error);

/// <summary>
/// Canonical payload signing and verification for visual-plugin packages.
/// <para>可视化插件包规范化载荷的签名与校验。</para>
/// </summary>
/// <remarks>
/// The payload is the package manifest, graph, scripts and resource descriptors (including hashes), with every
/// signature field removed. Signature verification therefore rejects any edit to a signed package without
/// re-signing it. Signing and verification temporarily clear in-memory signatures, so one package instance must
/// not be signed or verified concurrently from several threads.
/// <para>载荷是包清单、图、脚本与资源描述符（含哈希），并移除全部签名字段。因此任何未重新签名的改动都会导致签名校验失败。
/// 签名与校验会临时清除内存中的签名字段，同一包实例不应被多个线程并发签名或校验。</para>
/// </remarks>
public static class PluginSignature
{
    /// <summary>Maximum accepted base64 signature length. <para>允许的 Base64 签名最大长度。</para></summary>
    public const int MaximumSignatureLength = 1024;

    /// <summary>Maximum accepted base64 public-key length. <para>允许的 Base64 公钥最大长度。</para></summary>
    public const int MaximumPublicKeyLength = 8192;

    /// <summary>Maximum accepted decoded public-key length. <para>允许的公钥解码后最大字节数。</para></summary>
    public const int MaximumPublicKeyBytes = 2048;

    /// <summary>Maximum accepted publisher fingerprint length. <para>允许的发布者指纹最大长度。</para></summary>
    public const int MaximumFingerprintLength = 128;

    private const string FingerprintPrefix = "SHA256:";
    private const int FingerprintHexLength = 64;
    private const int MinimumRsaBits = 2048;
    private const int MaximumRsaBits = 8192;

    /// <summary>
    /// Computes the <c>SHA256:</c> fingerprint of a public or private key.
    /// <para>计算公钥或私钥的 <c>SHA256:</c> 指纹。</para>
    /// </summary>
    /// <param name="key">RSA or ECDSA key; only public parameters are used.
    /// <para>RSA 或 ECDSA 密钥；仅使用公开参数。</para></param>
    /// <returns>The uppercase hexadecimal SHA-256 of the SubjectPublicKeyInfo encoding, prefixed with <c>SHA256:</c>.
    /// <para>SubjectPublicKeyInfo 编码的大写十六进制 SHA-256，前缀为 <c>SHA256:</c>。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.
    /// <para><paramref name="key"/> 为 <see langword="null"/>。</para></exception>
    public static string ComputeFingerprint(AsymmetricAlgorithm key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return FingerprintPrefix + Convert.ToHexString(Sha256.ComputeHash(key.ExportSubjectPublicKeyInfo()));
    }

    /// <summary>
    /// Exports a public or private key as base64 SubjectPublicKeyInfo.
    /// <para>把公钥或私钥导出为 Base64 SubjectPublicKeyInfo。</para>
    /// </summary>
    /// <param name="key">RSA or ECDSA key. <para>RSA 或 ECDSA 密钥。</para></param>
    /// <returns>The base64 encoding stored in <see cref="VisualPluginPublisher.PublicKey"/>.
    /// <para>存入 <see cref="VisualPluginPublisher.PublicKey"/> 的 Base64 编码。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.
    /// <para><paramref name="key"/> 为 <see langword="null"/>。</para></exception>
    public static string ExportPublicKey(AsymmetricAlgorithm key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    }

    /// <summary>
    /// Imports a base64 SubjectPublicKeyInfo as the matching RSA or ECDSA key.
    /// <para>把 Base64 SubjectPublicKeyInfo 导入为对应的 RSA 或 ECDSA 密钥。</para>
    /// </summary>
    /// <param name="encoded">Base64 public key produced by <see cref="ExportPublicKey(AsymmetricAlgorithm)"/>.
    /// <para>由 <see cref="ExportPublicKey(AsymmetricAlgorithm)"/> 生成的 Base64 公钥。</para></param>
    /// <returns>A disposable key that the caller owns. <para>由调用方负责释放的密钥。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="encoded"/> is null, empty or not valid base64.
    /// <para><paramref name="encoded"/> 为 null、空或不是有效 Base64。</para></exception>
    /// <exception cref="InvalidDataException">The bytes are neither a supported RSA nor a P-256 ECDSA public key.
    /// <para>字节既不是受支持的 RSA 公钥，也不是 P-256 ECDSA 公钥。</para></exception>
    public static AsymmetricAlgorithm ImportPublicKey(string encoded)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(encoded);
        var bytes = Convert.FromBase64String(encoded);
        if (bytes.Length is 0 or > MaximumPublicKeyBytes)
        {
            throw new InvalidDataException("Publisher public key is empty or exceeds its size limit.");
        }
        var rsa = RSA.Create();
        try
        {
            rsa.ImportSubjectPublicKeyInfo(bytes, out var read);
            if (read == bytes.Length)
            {
                return rsa;
            }
        }
        catch (CryptographicException)
        {
        }
        rsa.Dispose();
        var ec = ECDsa.Create();
        try
        {
            ec.ImportSubjectPublicKeyInfo(bytes, out var read);
            if (read == bytes.Length)
            {
                return ec;
            }
        }
        catch (CryptographicException)
        {
        }
        ec.Dispose();
        throw new InvalidDataException("Publisher public key is neither RSA nor ECDSA SubjectPublicKeyInfo.");
    }

    /// <summary>
    /// Gets whether a fingerprint uses the canonical <c>SHA256:</c> plus 64 hex digits form.
    /// <para>获取指纹是否采用规范的 <c>SHA256:</c> 加 64 位十六进制形式。</para>
    /// </summary>
    /// <param name="fingerprint">Fingerprint text to check. <para>待检查的指纹文本。</para></param>
    /// <returns><see langword="true"/> for a canonical fingerprint. <para>规范指纹返回 <see langword="true"/>。</para></returns>
    public static bool IsValidFingerprint(string? fingerprint)
    {
        if (fingerprint is null || fingerprint.Length != FingerprintPrefix.Length + FingerprintHexLength
            || !fingerprint.StartsWith(FingerprintPrefix, StringComparison.Ordinal))
        {
            return false;
        }
        foreach (var character in fingerprint.AsSpan(FingerprintPrefix.Length))
        {
            if (!char.IsAsciiHexDigit(character))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Gets whether a base64 public key is syntactically acceptable.
    /// <para>获取 Base64 公钥在语法上是否可接受。</para>
    /// </summary>
    /// <param name="publicKey">Base64 public key to check. <para>待检查的 Base64 公钥。</para></param>
    /// <returns><see langword="true"/> when the value is bounded base64; the key itself is not imported.
    /// <para>值是有界 Base64 时为 <see langword="true"/>；此时不会导入密钥本身。</para></returns>
    public static bool IsValidPublicKey(string? publicKey) =>
        IsBoundedBase64(publicKey, MaximumPublicKeyLength, MaximumPublicKeyBytes);

    /// <summary>
    /// Signs a package and records the publisher identity and signature in its manifest.
    /// <para>对包签名，并把发布者身份与签名写入其清单。</para>
    /// </summary>
    /// <param name="package">Package to sign. <para>待签名的包。</para></param>
    /// <param name="privateKey">RSA key of at least 2048 bits, or a P-256 ECDSA key.
    /// <para>至少 2048 位的 RSA 密钥，或 P-256 ECDSA 密钥。</para></param>
    /// <param name="publisherId">Stable publisher identifier matching the package id syntax.
    /// <para>与包标识语法一致的稳定发布者标识。</para></param>
    /// <param name="publisherName">Display name of 1 to 128 characters.
    /// <para>1 到 128 个字符的显示名称。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="package"/> or <paramref name="privateKey"/> is <see langword="null"/>.
    /// <para><paramref name="package"/> 或 <paramref name="privateKey"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException">The publisher id or name is invalid, or the key algorithm or size is unsupported.
    /// <para>发布者标识或名称无效，或密钥算法或长度不受支持。</para></exception>
    public static void Sign(VisualPluginPackage package, AsymmetricAlgorithm privateKey, string publisherId,
        string publisherName)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(privateKey);
        if (!VisualPluginValidation.IsValidPluginId(publisherId))
        {
            throw new ArgumentException(
                "Publisher id must use lowercase ASCII letters, digits, dots, underscores, or hyphens.", nameof(publisherId));
        }
        var name = publisherName?.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128)
        {
            throw new ArgumentException("Publisher name must contain between 1 and 128 characters.", nameof(publisherName));
        }
        var algorithm = AlgorithmFor(privateKey)
            ?? throw new ArgumentException("Signature keys must be RSA 2048-8192 or ECDSA P-256.", nameof(privateKey));

        package.Manifest.Publisher = new VisualPluginPublisher
        {
            Id = publisherId,
            Name = name,
            Fingerprint = ComputeFingerprint(privateKey),
            PublicKey = ExportPublicKey(privateKey)
        };
        package.Manifest.Signature = null;
        var payload = ComputePayload(package);
        var value = privateKey switch
        {
            RSA rsa => rsa.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pss),
            ECDsa ec => ec.SignData(payload, HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation),
            _ => throw new ArgumentException("Unsupported signature key.", nameof(privateKey))
        };
        package.Manifest.Signature = new VisualPluginSignature
        {
            Algorithm = algorithm,
            Value = Convert.ToBase64String(value)
        };
    }

    /// <summary>
    /// Verifies the signature carried by a package, if any.
    /// <para>校验包所携带的签名（若有）。</para>
    /// </summary>
    /// <param name="package">Package to check; the signature covers every manifest, graph, script and resource descriptor.
    /// <para>待检查的包；签名覆盖全部清单、图、脚本与资源描述符。</para></param>
    /// <returns>The verification outcome; an unsigned package reports <see cref="PluginSignatureVerification.IsSigned"/>
    /// <see langword="false"/> with <see cref="PluginSignatureVerification.IsValid"/> <see langword="true"/>.
    /// <para>校验结果；未签名包返回 <see cref="PluginSignatureVerification.IsSigned"/> 为 <see langword="false"/> 且
    /// <see cref="PluginSignatureVerification.IsValid"/> 为 <see langword="true"/>。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="package"/> is <see langword="null"/>.
    /// <para><paramref name="package"/> 为 <see langword="null"/>。</para></exception>
    public static PluginSignatureVerification Verify(VisualPluginPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var signature = package.Manifest?.Signature;
        if (signature is null || string.IsNullOrEmpty(signature.Algorithm) || string.IsNullOrEmpty(signature.Value))
        {
            return new PluginSignatureVerification(false, true, null);
        }
        var publisher = package.Manifest!.Publisher;
        if (publisher is null || string.IsNullOrWhiteSpace(publisher.PublicKey)
            || string.IsNullOrWhiteSpace(publisher.Fingerprint))
        {
            return new PluginSignatureVerification(true, false, "Plugin signature has no publisher identity.");
        }
        if (!PluginSignatureAlgorithms.IsKnown(signature.Algorithm))
        {
            return new PluginSignatureVerification(true, false, "Plugin signature uses an unsupported algorithm.");
        }
        if (!IsBoundedBase64(signature.Value, MaximumSignatureLength, MaximumSignatureLength))
        {
            return new PluginSignatureVerification(true, false, "Plugin signature value is malformed.");
        }
        if (!IsValidFingerprint(publisher.Fingerprint) || !IsValidPublicKey(publisher.PublicKey))
        {
            return new PluginSignatureVerification(true, false,
                "Plugin publisher fingerprint or public key is malformed.");
        }
        try
        {
            using var key = ImportPublicKey(publisher.PublicKey);
            if (!string.Equals(ComputeFingerprint(key), publisher.Fingerprint, StringComparison.Ordinal))
            {
                return new PluginSignatureVerification(true, false,
                    "Plugin publisher fingerprint does not match its public key.");
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
                : new PluginSignatureVerification(true, false, "Plugin signature does not match its payload.");
        }
        catch (Exception error) when (error is CryptographicException or InvalidDataException or FormatException
                                          or ArgumentException)
        {
            return new PluginSignatureVerification(true, false,
                "Plugin signature cannot be verified: " + error.Message);
        }
    }

    /// <summary>
    /// Computes the canonical bytes a signature covers.
    /// <para>计算签名所覆盖的规范化字节。</para>
    /// </summary>
    /// <param name="package">Package whose signature fields are ignored. <para>忽略其签名字段的包。</para></param>
    /// <returns>Compact JSON of the package with every embedded signature removed.
    /// <para>移除全部内嵌签名后的紧凑 JSON。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="package"/> is <see langword="null"/>.
    /// <para><paramref name="package"/> 为 <see langword="null"/>。</para></exception>
    public static byte[] ComputePayload(VisualPluginPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var saved = new List<VisualPluginSignature?>();
        foreach (var scope in Scopes(package, 0))
        {
            saved.Add(scope.Manifest?.Signature);
            if (scope.Manifest is not null)
            {
                scope.Manifest.Signature = null;
            }
        }
        try
        {
            return JsonSerializer.SerializeToUtf8Bytes(package,
                PluginSignaturePayloadJsonContext.Default.VisualPluginPackage);
        }
        finally
        {
            var index = 0;
            foreach (var scope in Scopes(package, 0))
            {
                if (scope.Manifest is not null && index < saved.Count)
                {
                    scope.Manifest.Signature = saved[index];
                }
                index++;
            }
        }
    }

    private static bool IsBoundedBase64(string? value, int maximumChars, int maximumBytes)
    {
        if (value is not { Length: > 0 } || value.Length > maximumChars)
        {
            return false;
        }
        var buffer = new byte[Math.Min(maximumBytes, value.Length)];
        return Convert.TryFromBase64String(value, buffer, out var written) && written > 0;
    }

    private static string? AlgorithmFor(AsymmetricAlgorithm key) => key switch
    {
        RSA rsa when rsa.KeySize is >= MinimumRsaBits and <= MaximumRsaBits =>
            PluginSignatureAlgorithms.RsaPssSha256,
        ECDsa ec when ec.KeySize == 256 => PluginSignatureAlgorithms.EcdsaP256Sha256,
        _ => null
    };

    private static IEnumerable<VisualPluginPackage> Scopes(VisualPluginPackage package, int depth)
    {
        if (depth > 32)
        {
            yield break;
        }
        yield return package;
        if (package.Graph?.Nodes is not { } nodes)
        {
            yield break;
        }
        foreach (var node in nodes)
        {
            if (node?.CompositePackage is not { } child)
            {
                continue;
            }
            foreach (var scope in Scopes(child, depth + 1))
            {
                yield return scope;
            }
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(VisualPluginPackage))]
internal partial class PluginSignaturePayloadJsonContext : JsonSerializerContext;