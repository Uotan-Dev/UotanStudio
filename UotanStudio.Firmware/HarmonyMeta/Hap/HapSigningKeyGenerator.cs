using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace UotanStudio.Firmware.HarmonyMeta.Hap;

/// <summary>Generated HAP signing material written to disk.
/// <para>写入磁盘的 HAP 签名材料。</para></summary>
/// <param name="KeystorePath">PKCS#12 keystore path. <para>PKCS#12 keystore 路径。</para></param>
/// <param name="CsrPath">Certificate signing request path (legacy PEM header).
/// <para>证书签名请求路径（legacy PEM 头）。</para></param>
public sealed record HapSigningKeyMaterial(string KeystorePath, string CsrPath);

/// <summary>
/// Generates the local half of a HAP signing identity: a keystore holding the private key and a certificate
/// signing request for the cloud-issued application certificate. The private key never leaves the keystore.
/// <para>生成 HAP 签名身份的本地部分：持有私钥的 keystore，以及供云端签发应用证书的证书签名请求。
/// 私钥从不离开 keystore。</para>
/// </summary>
/// <remarks>
/// The keystore carries a self-signed placeholder certificate so PKCS#12 packaging is possible; after the cloud
/// issues the real certificate, the chain replaces the placeholder at signing time and the placeholder is never
/// embedded. The CSR uses the legacy <c>NEW CERTIFICATE REQUEST</c> PEM header the AGC tooling expects.
/// <para>keystore 内含自签占位证书以便 PKCS#12 打包；云端签发真实证书后，签名时以证书链替换占位证书，
/// 占位证书不会被内嵌。CSR 使用 AGC 工具链期望的 legacy <c>NEW CERTIFICATE REQUEST</c> PEM 头。</para>
/// </remarks>
public static class HapSigningKeyGenerator
{
    /// <summary>
    /// Generates the keystore and CSR into <paramref name="outputDirectory"/>.
    /// <para>把 keystore 与 CSR 生成到 <paramref name="outputDirectory"/>。</para>
    /// </summary>
    /// <param name="options">Generation options. <para>生成选项。</para></param>
    /// <param name="outputDirectory">Target directory; created when absent.
    /// <para>目标目录；不存在时创建。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>Paths of the generated files. <para>生成文件的路径。</para></returns>
    /// <exception cref="ArgumentException">The password is empty or the common name is invalid.
    /// <para>口令为空或通用名非法。</para></exception>
    public static HapSigningKeyMaterial Generate(HapSigningKeyOptions options, string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        if (options.KeyPassword.Length is 0 or > 128)
        {
            throw new ArgumentException("The keystore password must be 1-128 characters. keystore 口令须为 1-128 个字符。", nameof(options));
        }

        if (options.CommonName.Trim().Length is 0 or > 200 || options.CommonName.Contains('/'))
        {
            throw new ArgumentException("The common name must be 1-200 characters without '/'. 通用名须为 1-200 个字符且不含 '/'。", nameof(options));
        }

        Directory.CreateDirectory(outputDirectory);
        cancellationToken.ThrowIfCancellationRequested();

        using var key = CreateKey(options.KeyType);
        var request = key switch
        {
            RSA rsa => new CertificateRequest($"CN={options.CommonName.Trim()}", rsa, options.Hash, RSASignaturePadding.Pkcs1),
            ECDsa ecdsa => new CertificateRequest($"CN={options.CommonName.Trim()}", ecdsa, options.Hash),
            _ => throw new InvalidOperationException("Unsupported key type. 不支持的密钥类型。"),
        };

        var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        var certificate = request.CreateSelfSigned(notBefore, notBefore.AddYears(options.ValidityYears));
        if (OperatingSystem.IsWindows())
        {
            certificate.FriendlyName = options.KeyAlias;
        }

        using (certificate)
        {
            var keystorePath = Path.Combine(outputDirectory, "debug.p12");
            var stagedKeystore = keystorePath + ".tmp";
            var csrPath = Path.Combine(outputDirectory, "debug.csr");
            var stagedCsr = csrPath + ".tmp";
            try
            {
                File.WriteAllBytes(stagedKeystore, certificate.Export(X509ContentType.Pkcs12, options.KeyPassword));
                var csr = request.CreateSigningRequest();
                File.WriteAllText(stagedCsr, BuildLegacyCsrPem(csr));
                File.Move(stagedKeystore, keystorePath);
                File.Move(stagedCsr, csrPath);
                return new HapSigningKeyMaterial(keystorePath, csrPath);
            }
            finally
            {
                TryDelete(stagedKeystore);
                TryDelete(stagedCsr);
            }
        }
    }

    private static AsymmetricAlgorithm CreateKey(HapSigningKeyType type) => type switch
    {
        HapSigningKeyType.EcP256 => ECDsa.Create(ECCurve.NamedCurves.nistP256),
        HapSigningKeyType.EcP384 => ECDsa.Create(ECCurve.NamedCurves.nistP384),
        HapSigningKeyType.Rsa2048 => RSA.Create(2048),
        HapSigningKeyType.Rsa3072 => RSA.Create(3072),
        HapSigningKeyType.Rsa4096 => RSA.Create(4096),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    private static string BuildLegacyCsrPem(byte[] csr)
    {
        var body = Convert.ToBase64String(csr, Base64FormattingOptions.InsertLineBreaks);
        return $"-----BEGIN NEW CERTIFICATE REQUEST-----\n{body}\n-----END NEW CERTIFICATE REQUEST-----\n";
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
    }
}