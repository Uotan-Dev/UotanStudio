using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using UotanStudio.Firmware.Core.IO;

namespace UotanStudio.Firmware.AndroidMeta.Apk;

/// <summary>Signing key algorithm choices for generated APK signing material.</summary>
/// <para>生成 APK 签名材料时的密钥算法选项。</para>
public enum ApkSigningKeyType
{
    /// <summary>RSA 2048 bits.</summary>
    /// <para>RSA 2048 位。</para>
    Rsa2048,
    /// <summary>RSA 3072 bits.</summary>
    /// <para>RSA 3072 位。</para>
    Rsa3072,
    /// <summary>RSA 4096 bits.</summary>
    /// <para>RSA 4096 位。</para>
    Rsa4096,
    /// <summary>ECDSA NIST P-256.</summary>
    /// <para>ECDSA NIST P-256 曲线。</para>
    EcP256,
    /// <summary>ECDSA NIST P-384.</summary>
    /// <para>ECDSA NIST P-384 曲线。</para>
    EcP384,
    /// <summary>ECDSA NIST P-521.</summary>
    /// <para>ECDSA NIST P-521 曲线。</para>
    EcP521,
}

/// <summary>
/// Options for generating an APK signing key pair and self-signed certificate.
/// <para>生成 APK 签名密钥对与自签证书的选项。</para>
/// </summary>
public sealed record ApkSigningKeyOptions
{
    /// <summary>Key algorithm; RSA 2048 by default.</summary>
    /// <para>密钥算法；默认 RSA 2048。</para>
    /// <value>Algorithm of the generated key pair: RSA at 2048, 3072 or 4096 bits, or ECDSA on a NIST P-256, P-384 or P-521 curve.
    /// <para>生成密钥对的算法：RSA 2048、3072 或 4096 位，或 ECDSA NIST P-256、P-384、P-521 曲线。</para></value>
    public ApkSigningKeyType KeyType { get; init; } = ApkSigningKeyType.Rsa2048;

    /// <summary>Certificate common name (CN), required, 1–200 characters.</summary>
    /// <para>证书通用名称（CN），必填，1–200 个字符。</para>
    /// <value>Written trimmed into the certificate subject; <see cref="ApkSigningKeyGenerator.Generate"/> rejects values outside 1–200 characters, with control characters, or containing '/'.
    /// <para>去除首尾空白后写入证书主题；<see cref="ApkSigningKeyGenerator.Generate"/> 会拒绝超出 1–200 个字符、含控制字符或包含 '/' 的值。</para></value>
    public string CommonName { get; init; } = string.Empty;

    /// <summary>Optional organization (O) subject field.</summary>
    /// <para>可选的主题组织字段（O）。</para>
    /// <value>Added as the trimmed O RDN only when non-blank; omitted otherwise.
    /// <para>仅在非空白时以去除首尾空白的 O RDN 加入主题；否则省略。</para></value>
    public string? Organization { get; init; }

    /// <summary>Optional organizational unit (OU) subject field.</summary>
    /// <para>可选的主题组织单位字段（OU）。</para>
    /// <value>Added as the trimmed OU RDN only when non-blank; omitted otherwise.
    /// <para>仅在非空白时以去除首尾空白的 OU RDN 加入主题；否则省略。</para></value>
    public string? OrganizationalUnit { get; init; }

    /// <summary>Optional locality / city (L) subject field.</summary>
    /// <para>可选的城市或地区字段（L）。</para>
    /// <value>Added as the trimmed L RDN only when non-blank; omitted otherwise.
    /// <para>仅在非空白时以去除首尾空白的 L RDN 加入主题；否则省略。</para></value>
    public string? Locality { get; init; }

    /// <summary>Optional state or province (ST) subject field.</summary>
    /// <para>可选的州或省份字段（ST）。</para>
    /// <value>Added as the trimmed ST RDN only when non-blank; omitted otherwise.
    /// <para>仅在非空白时以去除首尾空白的 ST RDN 加入主题；否则省略。</para></value>
    public string? State { get; init; }

    /// <summary>Optional two-letter country code (C) subject field.</summary>
    /// <para>可选的两位国家代码字段（C）。</para>
    /// <value>Added as the trimmed C RDN only when non-blank; <see cref="ApkSigningKeyGenerator.Generate"/> rejects a non-blank value whose trimmed length is not two characters.
    /// <para>仅在非空白时以去除首尾空白的 C RDN 加入主题；<see cref="ApkSigningKeyGenerator.Generate"/> 会拒绝去空白后长度不是 2 的非空白值。</para></value>
    public string? Country { get; init; }

    /// <summary>Certificate validity in years (1–100); defaults to 30 years like typical debug certificates.</summary>
    /// <para>证书有效年数（1–100）；默认 30 年，与常见调试证书一致。</para>
    /// <value>Added to the validity start (one day before the current UTC time) to obtain the certificate's notAfter; values outside 1–100 are rejected.
    /// <para>加在有效期起点（当前 UTC 时间前一天）上得到证书的 notAfter；超出 1–100 的值会被拒绝。</para></value>
    public int ValidityYears { get; init; } = 30;

    /// <summary>Optional private key password; when set the key is exported as encrypted PKCS#8, otherwise plaintext PKCS#8.</summary>
    /// <para>可选私钥密码；设置后私钥导出为加密 PKCS#8，否则为明文 PKCS#8。</para>
    /// <value>A non-empty value encrypts the PEM with AES-256-CBC, SHA-256 and 100,000 PBKDF iterations, and is also used as the PFX password when <c>exportPfx</c> is set; null or empty yields plaintext PKCS#8 and an unauthenticated PFX.
    /// <para>非空值使用 AES-256-CBC、SHA-256 与 100000 次 PBKDF 迭代加密 PEM，并在 <c>exportPfx</c> 时同时作为 PFX 密码；null 或空值输出明文 PKCS#8 与无口令 PFX。</para></value>
    public string? KeyPassword { get; init; }
}

/// <summary>
/// Paths of the generated signing material written to disk.
/// <para>生成并写入磁盘的签名材料路径。</para>
/// </summary>
/// <param name="PrivateKeyPath">Path of the PKCS#8 PEM private key (<c>baseName</c>.pk8). <para>PKCS#8 PEM 私钥路径（<c>baseName</c>.pk8）。</para></param>
/// <param name="CertificatePath">Path of the self-signed certificate PEM (<c>baseName</c>.pem). <para>自签证书 PEM 路径（<c>baseName</c>.pem）。</para></param>
/// <param name="PfxPath">Path of the PKCS#12 bundle (<c>baseName</c>.pfx), or <see langword="null"/> when PFX export was not requested. <para>PKCS#12 合并文件路径（<c>baseName</c>.pfx）；未请求导出 PFX 时为 null。</para></param>
public sealed record ApkSigningKeyFiles(string PrivateKeyPath, string CertificatePath, string? PfxPath);

/// <summary>
/// Generates Android APK signing material: a private key (PKCS#8 PEM, optionally encrypted) and a
/// self-signed X.509 certificate sharing the key's public part, plus an optional PFX bundle.
/// Nothing is bundled; all outputs are freshly generated on demand.
/// <para>生成 Android APK 签名材料：私钥（PKCS#8 PEM，可选加密）与共享同一公钥的自签 X.509 证书，可选 PFX 合并输出。
/// 不内置任何密钥材料，全部输出按需生成。</para>
/// </summary>
public static class ApkSigningKeyGenerator
{
    /// <summary>
    /// Generates the key pair and certificate, round-trips them through
    /// <see cref="ApkSignerKeyMaterial.Load"/> before commit, and writes the files atomically
    /// (temp file + rename); existing files are never overwritten.
    /// <para>生成密钥对与证书，提交前先经 <see cref="ApkSignerKeyMaterial.Load"/> 回环自检，
    /// 再以临时文件 + 重命名原子写出；绝不覆盖已存在的文件。</para>
    /// </summary>
    /// <param name="options">Generation options. <para>生成选项。</para></param>
    /// <param name="outputDirectory">Target directory (created when missing). <para>目标目录（缺失时创建）。</para></param>
    /// <param name="baseName">Base file name without extension (optional; defaults to "apk-signing"). <para>不含扩展名的基础文件名（可选；默认 "apk-signing"）。</para></param>
    /// <param name="exportPfx">Also export a PFX containing the private key. <para>是否额外导出含私钥的 PFX。</para></param>
    /// <returns>Paths of the generated files. <para>生成文件的路径。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>. <para><paramref name="options"/> 为 null。</para></exception>
    /// <exception cref="ArgumentException">The common name, validity years, country code or base file name fails validation. <para>通用名称、有效年数、国家代码或基础文件名未通过校验。</para></exception>
    /// <exception cref="CryptographicException">The round-trip self-check cannot read the freshly generated key and certificate back as a matching pair. <para>回环自检无法把新生成的私钥与证书读回为匹配的密钥对。</para></exception>
    /// <exception cref="IOException">A destination file already exists, or the temporary files cannot be written or moved. <para>目标文件已存在，或临时文件无法写入或移动。</para></exception>
    public static ApkSigningKeyFiles Generate(ApkSigningKeyOptions options, string outputDirectory, string? baseName = null, bool exportPfx = false)
    {
        ArgumentNullException.ThrowIfNull(options);
        Validate(options, baseName);

        var name = string.IsNullOrWhiteSpace(baseName) ? "apk-signing" : baseName.Trim();
        var directory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(directory);
        var keyPath = Path.Combine(directory, name + ".pk8");
        var certPath = Path.Combine(directory, name + ".pem");
        var pfxPath = exportPfx ? Path.Combine(directory, name + ".pfx") : null;

        var keyPem = CreatePrivateKeyPem(options.KeyType, options.KeyPassword, out var disposableKey);
        try
        {
            var certificate = CreateSelfSignedCertificate(options, disposableKey);
            try
            {
                var certPem = certificate.ExportCertificatePem();
                byte[]? pfxBytes = null;
                if (pfxPath is not null)
                {
                    if (certificate.HasPrivateKey)
                    {
                        pfxBytes = certificate.Export(X509ContentType.Pkcs12, options.KeyPassword);
                    }
                    else
                    {
                        using var withKey = disposableKey switch
                        {
                            RSA rsa => certificate.CopyWithPrivateKey(rsa),
                            ECDsa ec => certificate.CopyWithPrivateKey(ec),
                            _ => throw new InvalidOperationException("Unsupported generated key type. 不支持生成的密钥类型。"),
                        };
                        pfxBytes = withKey.Export(X509ContentType.Pkcs12, options.KeyPassword);
                    }
                }

                Commit(keyPem, certPem, pfxBytes, keyPath, certPath, pfxPath, options.KeyPassword);
                return new ApkSigningKeyFiles(keyPath, certPath, pfxPath);
            }
            finally { certificate.Dispose(); }
        }
        finally { disposableKey?.Dispose(); }
    }

    private static void Commit(string keyPem, string certPem, byte[]? pfxBytes, string keyPath, string certPath, string? pfxPath, string? keyPassword)
    {
        var keyTemporary = TempFileHelper.CreateAdjacentPath(keyPath);
        var certTemporary = TempFileHelper.CreateAdjacentPath(certPath);
        var pfxTemporary = pfxPath is null ? null : TempFileHelper.CreateAdjacentPath(pfxPath);
        try
        {
            WriteAtomicIfAbsent(keyTemporary, Encoding.ASCII.GetBytes(keyPem));
            WriteAtomicIfAbsent(certTemporary, Encoding.ASCII.GetBytes(certPem));
            if (pfxBytes is not null && pfxTemporary is not null) WriteAtomicIfAbsent(pfxTemporary, pfxBytes);

            using (ApkSignerKeyMaterial.Load(keyTemporary, keyPassword, certTemporary, null))
            {
            }

            File.Move(keyTemporary, keyPath, overwrite: false);
            File.Move(certTemporary, certPath, overwrite: false);
            if (pfxBytes is not null && pfxTemporary is not null) File.Move(pfxTemporary, pfxPath!, overwrite: false);
        }
        finally
        {
            foreach (var temporary in new[] { keyTemporary, certTemporary, pfxTemporary })
            {
                if (temporary is not null) TryDelete(temporary);
            }
        }
    }

    private static string CreatePrivateKeyPem(ApkSigningKeyType type, string? password, out IDisposable? disposable)
    {
        disposable = null;
        if (type is ApkSigningKeyType.EcP256 or ApkSigningKeyType.EcP384 or ApkSigningKeyType.EcP521)
        {
            var curve = type switch
            {
                ApkSigningKeyType.EcP256 => ECCurve.NamedCurves.nistP256,
                ApkSigningKeyType.EcP384 => ECCurve.NamedCurves.nistP384,
                _ => ECCurve.NamedCurves.nistP521,
            };
            var ec = ECDsa.Create(curve);
            disposable = ec;
            return password is { Length: > 0 }
                ? ec.ExportEncryptedPkcs8PrivateKeyPem(password, new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 100_000))
                : ec.ExportPkcs8PrivateKeyPem();
        }

        var bits = type switch
        {
            ApkSigningKeyType.Rsa2048 => 2048,
            ApkSigningKeyType.Rsa3072 => 3072,
            ApkSigningKeyType.Rsa4096 => 4096,
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
        };
        var rsa = RSA.Create(bits);
        disposable = rsa;
        return password is { Length: > 0 }
            ? rsa.ExportEncryptedPkcs8PrivateKeyPem(password, new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 100_000))
            : rsa.ExportPkcs8PrivateKeyPem();
    }

    private static X509Certificate2 CreateSelfSignedCertificate(ApkSigningKeyOptions options, IDisposable? key)
    {
        var hash = options.KeyType is ApkSigningKeyType.Rsa4096
            or ApkSigningKeyType.EcP384
            or ApkSigningKeyType.EcP521
            ? HashAlgorithmName.SHA512
            : HashAlgorithmName.SHA256;

        var request = key switch
        {
            RSA rsa => new CertificateRequest(BuildSubject(options), rsa, hash, RSASignaturePadding.Pkcs1),
            ECDsa ec => new CertificateRequest(BuildSubject(options), ec, hash),
            _ => throw new InvalidOperationException("Unsupported generated key type. 不支持生成的密钥类型。"),
        };

        var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        var notAfter = notBefore.AddYears(options.ValidityYears);
        return request.CreateSelfSigned(notBefore, notAfter);
    }

    private static X500DistinguishedName BuildSubject(ApkSigningKeyOptions options)
    {
        var builder = new X500DistinguishedNameBuilder();
        builder.AddCommonName(options.CommonName.Trim());
        if (!string.IsNullOrWhiteSpace(options.Organization)) builder.AddOrganizationName(options.Organization!.Trim());
        if (!string.IsNullOrWhiteSpace(options.OrganizationalUnit)) builder.AddOrganizationalUnitName(options.OrganizationalUnit!.Trim());
        if (!string.IsNullOrWhiteSpace(options.Locality)) builder.AddLocalityName(options.Locality!.Trim());
        if (!string.IsNullOrWhiteSpace(options.State)) builder.AddStateOrProvinceName(options.State!.Trim());
        if (!string.IsNullOrWhiteSpace(options.Country)) builder.AddCountryOrRegion(options.Country!.Trim());
        return builder.Build();
    }

    private static void Validate(ApkSigningKeyOptions options, string? baseName)
    {
        if (string.IsNullOrWhiteSpace(options.CommonName) || options.CommonName.Trim().Length > 200 || options.CommonName.Any(char.IsControl))
            throw new ArgumentException("Common name must be 1–200 characters without control characters. 通用名称须为 1–200 个字符且不含控制字符。");

        if (options.CommonName.Trim().Length > 0 && options.CommonName.Contains('/'))
            throw new ArgumentException("Common name must not contain '/'. 通用名称不能包含 '/'。");

        if (options.ValidityYears is < 1 or > 100)
            throw new ArgumentException("Validity must be between 1 and 100 years. 有效期须在 1–100 年之间。");

        if (!string.IsNullOrWhiteSpace(options.Country) && options.Country!.Trim().Length != 2)
            throw new ArgumentException("Country must be a two-letter code. 国家代码须为两个字母。");

        if (!string.IsNullOrWhiteSpace(baseName))
        {
            var name = baseName.Trim();
            if (name.Length > 100 || name.Any(c => Path.GetInvalidFileNameChars().Contains(c)))
                throw new ArgumentException("Base file name contains invalid characters. 基础文件名包含非法字符。");
        }
    }

    private static void WriteAtomicIfAbsent(string path, byte[] bytes)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var file = new FileStream(path, options);
        file.Write(bytes);
        file.Flush(true);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}