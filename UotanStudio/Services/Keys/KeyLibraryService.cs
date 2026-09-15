using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using UotanStudio.Comm.Protocols.Adb;
using UotanStudio.Comm.Protocols.Hdc;
using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Apk.Signing;

namespace UotanStudio.Services.Keys;

/// <summary>Supported key purposes; purpose determines the exported public key format.</summary>
public enum ManagedKeyKind { Adb, Hdc, Payload, Jar, Apk, Zip }

/// <summary>Export bundle choices for a freshly generated APK signing certificate.</summary>
/// <summary>新生成 APK 签名证书的导出格式组合。</summary>
public enum ApkCertificateExportFormat
{
    /// <summary>JKS + PKCS12 + PEM files. <para>JKS + PKCS12 + PEM 全部格式。</para></summary>
    All,
    /// <summary>Java keystore only. <para>仅 JKS（.jks）。</para></summary>
    Jks,
    /// <summary>Pkcs12 only. <para>仅 PKCS12（.p12）。</para></summary>
    Pkcs12,
    /// <summary>PEM private key + certificate only. <para>仅 PEM（.pk8 + .pem）。</para></summary>
    Pem,
}

/// <summary>Public metadata for a key pair. Private key contents are never stored in metadata.</summary>
public sealed record ManagedKey(string Id, string Name, ManagedKeyKind Kind, int Bits, string Fingerprint, DateTimeOffset Created)
{
    // EC curve sizes (256/384/521) never collide with RSA key sizes (2048+).
    // EC 曲线位数（256/384/521）与 RSA 位数（2048+）不会重叠。
    public string Summary => Bits is 256 or 384 or 521
        ? $"{Kind.ToString().ToUpperInvariant()} · EC P-{Bits}"
        : $"{Kind.ToString().ToUpperInvariant()} · RSA {Bits}";
}

[JsonSerializable(typeof(ManagedKey))]
internal partial class KeyLibraryJsonContext : JsonSerializerContext;

/// <summary>Owns independent key directories and atomically publishes complete key pairs.</summary>
public sealed class KeyLibraryService
{
    public string Root { get; }

    public KeyLibraryService(string? root = null) => Root = Path.GetFullPath(root ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UotanStudio", "keys"));

    public IReadOnlyList<ManagedKey> List()
    {
        if (!Directory.Exists(Root)) return [];
        return Directory.EnumerateDirectories(Root)
            .Where(p => Guid.TryParseExact(Path.GetFileName(p), "N", out _))
            .Select(p => ReadMetadata(Path.Combine(p, "key.json")))
            .OrderByDescending(k => k.Created).ToArray();
    }

    public ManagedKey Generate(string name, ManagedKeyKind kind, int bits)
    {
        Validate(name, kind, bits);
        if (kind == ManagedKeyKind.Apk && bits is 256 or 384 or 521)
        {
            var curve = bits switch
            {
                256 => ECCurve.NamedCurves.nistP256,
                384 => ECCurve.NamedCurves.nistP384,
                _ => ECCurve.NamedCurves.nistP521,
            };
            using var ec = ECDsa.Create(curve);
            return Publish(name, kind, ec);
        }
        using var rsa = RSA.Create(bits);
        return Publish(name, kind, rsa);
    }

    public ManagedKey Import(string name, ManagedKeyKind kind, string path, string password)
    {
        using var key = LoadAnyPrivateKey(path, password);
        Validate(name, kind, key.KeySize);
        return Publish(name, kind, key);
    }

    /// <summary>Paths written for one generated APK signing certificate bundle.</summary>
    /// <summary>一次生成的 APK 签名证书导出文件的路径集合。</summary>
    public sealed record ApkCertificateBundle(ManagedKey Key, IReadOnlyList<string> ExportedFiles);

    /// <summary>
    /// Generates a fresh APK signing key pair + self-signed certificate, registers it in the
    /// key library, and exports the requested file formats into the output directory using the
    /// entry name as the file base. Existing files are never overwritten.
    /// <para>生成全新的 APK 签名密钥对与自签证书，登记进密钥库，并按请求的格式以条目名为基础文件名
    /// 导出到输出目录。绝不覆盖已存在的文件。</para>
    /// </summary>
    /// <param name="name">Library entry name and export base file name. <para>库条目名与导出基础文件名。</para></param>
    /// <param name="keyPassword">Optional private-key password (also protects JKS/PKCS12 integrity via the keystore password when provided separately). <para>可选私钥密码。</para></param>
    /// <param name="storePassword">Keystore password for JKS/PKCS12 integrity and encryption; required for JKS. <para>JKS/PKCS12 的密钥库密码；JKS 必填。</para></param>
    /// <param name="validityYears">Certificate validity in years (1–100). <para>证书有效年数（1–100）。</para></param>
    /// <param name="subject">Certificate subject fields (CN required; O/OU/L/ST/C optional). <para>证书主题字段（CN 必填，O/OU/L/ST/C 可选）。</para></param>
    /// <param name="keyType">Key algorithm selection. <para>密钥算法选择。</para></param>
    /// <param name="format">Export bundle choice. <para>导出格式组合。</para></param>
    /// <param name="outputDirectory">Directory for the exported files (created when missing). <para>导出文件目录（缺失时创建）。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The registered library key and the exported file paths. <para>登记的库条目与导出文件路径。</para></returns>
    public ApkCertificateBundle GenerateApkCertificate(
        string name,
        string? keyPassword,
        string? storePassword,
        int validityYears,
        ApkSigningKeyOptions subject,
        ApkSigningKeyType keyType,
        ApkCertificateExportFormat format,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        // Name validation only; key size is checked by the generator and re-checked on import.
        // 仅校验名称；位数由生成器校验并在导入时复核。
        Validate(name, ManagedKeyKind.Apk, 2048);
        cancellationToken.ThrowIfCancellationRequested();

        var trimmed = name.Trim();
        if (format is ApkCertificateExportFormat.Jks or ApkCertificateExportFormat.All && string.IsNullOrEmpty(storePassword))
            throw new ArgumentException("导出 JKS 必须提供密钥库密码。");

        var options = subject with
        {
            KeyType = keyType,
            KeyPassword = string.IsNullOrEmpty(keyPassword) ? null : keyPassword,
            ValidityYears = validityYears,
        };
        var staging = Path.Combine(Path.GetTempPath(), "uotan-apk-cert-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var generated = ApkSigningKeyGenerator.Generate(options, staging, "entry");
            var key = Import(name, ManagedKeyKind.Apk, generated.PrivateKeyPath, keyPassword ?? "");

            Directory.CreateDirectory(Path.GetFullPath(outputDirectory));
            var exported = new List<string>();
            using var privateKey = LoadAnyPrivateKey(generated.PrivateKeyPath, keyPassword ?? "");
            var pkcs8 = privateKey.ExportPkcs8PrivateKey();
            try
            {
                var certificate = LoadPemCertificate(generated.CertificatePath);
                using (certificate)
                {
                    var baseName = Path.Combine(Path.GetFullPath(outputDirectory), trimmed);
                    if (format is ApkCertificateExportFormat.Jks or ApkCertificateExportFormat.All)
                    {
                        var jks = JksKeyStoreWriter.Build(trimmed, pkcs8, [certificate.RawData], storePassword!);
                        var path = baseName + ".jks";
                        Write(path, jks);
                        CryptographicOperations.ZeroMemory(jks);
                        exported.Add(path);
                    }
                    if (format is ApkCertificateExportFormat.Pkcs12 or ApkCertificateExportFormat.All)
                    {
                        using var withKey = privateKey switch
                        {
                            RSA rsa => certificate.CopyWithPrivateKey(rsa),
                            ECDsa ec => certificate.CopyWithPrivateKey(ec),
                            _ => throw new InvalidOperationException("APK 证书仅支持 RSA 或 EC 密钥。"),
                        };
                        var pfx = withKey.Export(X509ContentType.Pkcs12, string.IsNullOrEmpty(storePassword) ? keyPassword : storePassword);
                        var path = baseName + ".p12";
                        Write(path, pfx);
                        CryptographicOperations.ZeroMemory(pfx);
                        exported.Add(path);
                    }
                    if (format is ApkCertificateExportFormat.Pem or ApkCertificateExportFormat.All)
                    {
                        var keyPath = baseName + ".pk8";
                        var certPath = baseName + ".pem";
                        Write(keyPath, File.ReadAllBytes(generated.PrivateKeyPath));
                        Write(certPath, File.ReadAllBytes(generated.CertificatePath));
                        exported.Add(keyPath);
                        exported.Add(certPath);
                    }
                }
                return new ApkCertificateBundle(key, exported);
            }
            finally { CryptographicOperations.ZeroMemory(pkcs8); }
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(staging)) File.Delete(file);
            Directory.Delete(staging);
        }
    }

    private static X509Certificate2 LoadPemCertificate(string pemPath)
    {
        var text = File.ReadAllText(pemPath);
        var start = text.IndexOf("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal);
        var headerEnd = text.IndexOf('\n', start);
        var end = text.IndexOf("-----END CERTIFICATE-----", start, StringComparison.Ordinal);
        if (start < 0 || headerEnd < 0 || end < 0) throw new InvalidDataException("证书 PEM 格式错误。");
        var base64 = string.Concat(text[(headerEnd + 1)..end].Where(static c => !char.IsWhiteSpace(c)));
        return X509CertificateLoader.LoadCertificate(Convert.FromBase64String(base64));
    }

    /// <summary>Loads a bounded PEM/DER private key and verifies that private operations are available. The caller disposes the returned key.</summary>
    public static RSA LoadPrivateKey(string path, string password = "")
    {
        var key = LoadAnyPrivateKey(path, password);
        try
        {
            if (key is not RSA rsa) throw new InvalidDataException("此用途需要 RSA 私钥。");
            // Exercise the private operation before returning.
            rsa.SignData("UotanStudio key import"u8, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            return rsa;
        }
        catch { key.Dispose(); throw; }
    }

    /// <summary>Loads a bounded PEM/DER private key as RSA or EC (PKCS#8 plain/encrypted, PKCS#1, SEC1). The caller disposes the returned key.</summary>
    /// <summary>以 RSA 或 EC 加载有界 PEM/DER 私钥（PKCS#8 明文/加密、PKCS#1、SEC1）。调用方负责释放返回的密钥。</summary>
    public static AsymmetricAlgorithm LoadAnyPrivateKey(string path, string password = "")
    {
        var bytes = ReadBounded(path);
        try
        {
            var text = Encoding.UTF8.GetString(bytes);
            if (text.Contains("-----BEGIN", StringComparison.Ordinal))
            {
                if (text.Contains("ENCRYPTED PRIVATE KEY", StringComparison.Ordinal))
                {
                    var encryptedRsa = RSA.Create();
                    if (TryImport(() => encryptedRsa.ImportFromEncryptedPem(text, password))) return VerifyPrivate(encryptedRsa);
                    encryptedRsa.Dispose();
                    var encryptedEc = ECDsa.Create();
                    if (TryImport(() => encryptedEc.ImportFromEncryptedPem(text, password))) return VerifyPrivate(encryptedEc);
                    encryptedEc.Dispose();
                    throw new InvalidDataException("无法使用给定密码导入加密私钥。");
                }

                var plainRsa = RSA.Create();
                if (TryImport(() => plainRsa.ImportFromPem(text))) return VerifyPrivate(plainRsa);
                plainRsa.Dispose();
                var plainEc = ECDsa.Create();
                if (TryImport(() => plainEc.ImportFromPem(text))) return VerifyPrivate(plainEc);
                plainEc.Dispose();
                throw new InvalidDataException("不支持的私钥 PEM 格式。");
            }

            // DER: RSA PKCS#8/PKCS#1, then EC PKCS#8/SEC1; trailing data is rejected.
            // DER：先试 RSA PKCS#8/PKCS#1，再试 EC PKCS#8/SEC1；拒绝尾随数据。
            int read = 0;
            var derRsa = RSA.Create();
            if (TryImport(() => derRsa.ImportPkcs8PrivateKey(bytes, out read))
                || TryImport(() => derRsa.ImportRSAPrivateKey(bytes, out read)))
            {
                if (read != bytes.Length) { derRsa.Dispose(); throw new InvalidDataException("密钥文件包含多余数据。"); }
                return VerifyPrivate(derRsa);
            }
            derRsa.Dispose();
            var derEc = ECDsa.Create();
            if (TryImport(() => derEc.ImportPkcs8PrivateKey(bytes, out read))
                || TryImport(() => derEc.ImportECPrivateKey(bytes, out read)))
            {
                if (read != bytes.Length) { derEc.Dispose(); throw new InvalidDataException("密钥文件包含多余数据。"); }
                return VerifyPrivate(derEc);
            }
            derEc.Dispose();
            throw new InvalidDataException("不支持的私钥 DER 格式。");
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static bool TryImport(Action import)
    {
        try { import(); return true; }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException) { return false; }
    }

    // Reject public-only inputs and exercise the private operation before publication.
    // 拒绝仅含公钥的输入，发布前先执行一次私钥运算。
    private static AsymmetricAlgorithm VerifyPrivate(AsymmetricAlgorithm key)
    {
        try
        {
            switch (key)
            {
                case RSA rsa:
                    rsa.SignData("UotanStudio key import"u8, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                    break;
                case ECDsa ec:
                    ec.SignData("UotanStudio key import"u8, HashAlgorithmName.SHA256);
                    break;
            }
            return key;
        }
        catch (CryptographicException ex)
        {
            // Public-only material parses as a key but cannot sign; surface it as invalid
            // material so callers only ever see this loader's documented failure type.
            // 仅含公钥的材料能解析但不能签名；统一转换为无效材料异常，使调用方只需处理本加载器声明的失败类型。
            key.Dispose();
            throw new InvalidDataException("私钥不包含可用的私有材料。", ex);
        }
    }

    public string PrivatePath(ManagedKey key) => Path.Combine(KeyDirectory(key.Id), PrivateName(key.Kind));
    public string PublicText(ManagedKey key) => Encoding.UTF8.GetString(ReadBounded(PrivatePath(key) + ".pub"));

    /// <summary>Certificate file path for a Payload key; the file may not exist for keys created before certificates.</summary>
    public string CertificatePath(ManagedKey key) => PrivatePath(key) + ".crt";

    /// <summary>Returns the certificate path, generating the self-signed certificate for a legacy Payload/JAR key on first use.</summary>
    public string EnsureCertificate(ManagedKey key)
    {
        if (key.Kind is not (ManagedKeyKind.Payload or ManagedKeyKind.Jar or ManagedKeyKind.Zip))
            throw new InvalidOperationException("Only Payload, JAR and ZIP keys carry certificates.");
        var path = CertificatePath(key);
        if (File.Exists(path)) return path;
        var directory = KeyDirectory(key.Id);
        var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using var privateKey = LoadAnyPrivateKey(PrivatePath(key));
            Write(temporary, CreateCertificate(key.Name, privateKey));
            try { File.Move(temporary, path, overwrite: false); }
            catch (IOException) when (File.Exists(path)) { }
        }
        finally { File.Delete(temporary); }
        return path;
    }

    // Self-signed certificate sharing the private key's SPKI (RSA PKCS#1 or ECDSA), for OTA zip
    // outer signatures (signapk -w), the otacert entry and APK signing key entries.
    private static string CreateCertificate(string name, AsymmetricAlgorithm key)
    {
        var subject = new X500DistinguishedNameBuilder();
        subject.AddCommonName(name);
        // SHA-256 for RSA ≤3072 / EC P-256, SHA-512 above — mirrors ApkSignatureAlgorithmSelector.
        // RSA ≤3072 位与 EC P-256 用 SHA-256，更高位数用 SHA-512 —— 与 ApkSignatureAlgorithmSelector 一致。
        var hash = key.KeySize > 3072 || (key is ECDsa && key.KeySize > 256) ? HashAlgorithmName.SHA512 : HashAlgorithmName.SHA256;
        CertificateRequest request = key switch
        {
            RSA rsa => new CertificateRequest(subject.Build(), rsa, hash, RSASignaturePadding.Pkcs1),
            ECDsa ec => new CertificateRequest(subject.Build(), ec, hash),
            _ => throw new NotSupportedException("证书创建仅支持 RSA 或 EC 密钥。"),
        };
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
        return certificate.ExportCertificatePem();
    }

    public ManagedKey Rename(ManagedKey key, string name)
    {
        Validate(name, key.Kind, key.Bits);
        var updated = key with { Name = name.Trim() };
        var directory = KeyDirectory(key.Id);
        var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            Write(temporary, JsonSerializer.Serialize(updated, KeyLibraryJsonContext.Default.ManagedKey));
            File.Move(temporary, Path.Combine(directory, "key.json"), true);
        }
        finally { File.Delete(temporary); }
        return updated;
    }

    /// <summary>Moves a key into a recoverable local trash directory; exported copies are unaffected.</summary>
    public void Remove(ManagedKey key)
    {
        var trash = Path.Combine(Root, ".deleted");
        Directory.CreateDirectory(trash);
        Directory.Move(KeyDirectory(key.Id), Path.Combine(trash, key.Id + "-" + Guid.NewGuid().ToString("N")));
    }

    /// <summary>Permanently deletes only this library entry; never follows directory links or affects exports.</summary>
    public void Delete(ManagedKey key)
    {
        var directory = KeyDirectory(key.Id);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("不能删除链接目录 / Cannot delete a linked key directory.");
        // Owned key directories are flat. Reject unexpected subdirectories rather than recursively deleting them.
        if (Directory.EnumerateDirectories(directory).Any()) throw new IOException("密钥目录包含未知子目录 / Unexpected key subdirectory.");
        foreach (var file in Directory.EnumerateFiles(directory)) File.Delete(file);
        Directory.Delete(directory);
    }

    /// <summary>Exports to a new directory, never replacing existing files.</summary>
    public string Export(ManagedKey key, string destination, bool includePrivate)
    {
        var target = Path.Combine(Path.GetFullPath(destination), key.Kind.ToString().ToLowerInvariant() + "-" + key.Id);
        var staging = Path.Combine(Path.GetFullPath(destination), ".key-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var fileName = PrivateName(key.Kind);
            Write(Path.Combine(staging, fileName + ".pub"), PublicText(key));
            var certificate = Path.Combine(KeyDirectory(key.Id), fileName + ".crt");
            if (File.Exists(certificate)) File.Copy(certificate, Path.Combine(staging, fileName + ".crt"));
            if (includePrivate)
            {
                var bytes = ReadBounded(PrivatePath(key));
                try { Write(Path.Combine(staging, fileName), bytes); }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
            Directory.Move(staging, target);
            return target;
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }

    /// <summary>按指纹查找库中已有的密钥。 Finds an existing library key by fingerprint.</summary>
    /// <param name="kind">The key purpose to match. 要匹配的密钥用途。</param>
    /// <param name="fingerprint">The fingerprint to match, compared ordinally ignoring case. 要匹配的指纹，忽略大小写的序数比较。</param>
    /// <returns>The matching key, or null when not found. 匹配的密钥；未找到时为 null。</returns>
    public ManagedKey? FindByFingerprint(ManagedKeyKind kind, string fingerprint)
    {
        if (string.IsNullOrEmpty(fingerprint)) return null;
        return List().FirstOrDefault(k => k.Kind == kind &&
            string.Equals(k.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Computes the SPKI SHA-256 fingerprint of the current runtime identity private key for a purpose; null when the file is missing or unreadable.</summary>
    /// <summary>计算指定用途当前运行时身份私钥的 SPKI SHA-256 指纹；文件缺失或不可读时为 null。</summary>
    /// <param name="kind">The key purpose; must be Adb or Hdc. 密钥用途；必须为 Adb 或 Hdc。</param>
    /// <returns>The fingerprint in the same "SHA256:" form as <see cref="ManagedKey.Fingerprint"/>, or null. 与 <see cref="ManagedKey.Fingerprint"/> 相同格式的 "SHA256:" 指纹；否则为 null。</returns>
    public static string? GetRuntimeIdentityFingerprint(ManagedKeyKind kind)
    {
        if (kind is not (ManagedKeyKind.Adb or ManagedKeyKind.Hdc)) return null;
        var path = kind == ManagedKeyKind.Adb ? AdbAuthentication.DefaultKeyFilePath : HdcAuthentication.DefaultKeyFilePath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try
        {
            using var rsa = LoadPrivateKey(path);
            return "SHA256:" + Convert.ToHexString(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or System.Security.Cryptography.CryptographicException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>导入运行时身份私钥；若库中已存在同指纹同用途的密钥则直接返回该条目（幂等）。 Imports a runtime identity private key; returns the existing entry when a same-kind, same-fingerprint key already exists (idempotent).</summary>
    /// <param name="name">The display name for the imported key. 导入密钥的显示名称。</param>
    /// <param name="kind">The key purpose; must be Adb or Hdc. 密钥用途；必须为 Adb 或 Hdc。</param>
    /// <param name="sourcePrivateKeyPath">The runtime private key file to import. 要导入的运行时私钥文件路径。</param>
    /// <returns>The newly imported or already existing key. 新导入或已存在的密钥。</returns>
    public ManagedKey ImportIdentity(string name, ManagedKeyKind kind, string sourcePrivateKeyPath)
    {
        using var rsa = LoadPrivateKey(sourcePrivateKeyPath);
        var fingerprint = "SHA256:" + Convert.ToHexString(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo()));
        if (FindByFingerprint(kind, fingerprint) is { } existing) return existing;
        return Publish(name, kind, rsa);
    }

    /// <summary>将库中的密钥发布为运行时身份（原子替换目标私钥与公钥文件）。 Publishes a library key as the runtime identity (atomically replaces the target private and public key files).</summary>
    /// <param name="key">The library key to publish; must be an Adb or Hdc key. 要发布的库中密钥；必须为 Adb 或 Hdc 密钥。</param>
    public void PublishDefault(ManagedKey key) => PublishDefault(key, key.Kind == ManagedKeyKind.Adb
        ? AdbAuthentication.DefaultKeyFilePath : HdcAuthentication.DefaultKeyFilePath);

    /// <summary>Publishes a library key to an explicit runtime identity path; used by tests and the default overload.</summary>
    /// <summary>把库中密钥发布到显式指定的运行时身份路径；供测试与默认重载使用。</summary>
    /// <param name="key">The library key to publish; must be an Adb or Hdc key. 要发布的库中密钥；必须为 ADB 或 HDC 密钥。</param>
    /// <param name="target">The runtime private key file path to replace. 要替换的运行时私钥文件路径。</param>
    internal void PublishDefault(ManagedKey key, string target)
    {
        if (key.Kind is not (ManagedKeyKind.Adb or ManagedKeyKind.Hdc))
            throw new InvalidOperationException("仅 ADB/HDC 密钥可以发布为运行时身份。");
        var requiredBits = key.Kind == ManagedKeyKind.Adb ? 2048 : 3072;
        if (key.Bits != requiredBits) throw new InvalidDataException("密钥位数与用途不匹配。");
        var source = PrivatePath(key);
        var publicKey = source + ".pub";
        if (!File.Exists(publicKey)) throw new InvalidDataException("缺少公钥文件，无法发布。");

        var directory = Path.GetDirectoryName(target) ?? throw new InvalidDataException("无效的目标路径。");
        Directory.CreateDirectory(directory);
        var name = Path.GetFileName(target);
        var privateTemporary = Path.Combine(directory, "." + name + "-" + Guid.NewGuid().ToString("N") + ".tmp");
        var publicTemporary = Path.Combine(directory, "." + name + "-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var rsa = LoadPrivateKey(source))
            {
                if (rsa.KeySize != requiredBits) throw new InvalidDataException("密钥位数与用途不匹配。");
            }
            var publicBytes = ReadBounded(publicKey);
            try
            {
                Write(privateTemporary, ReadBounded(source));
                Write(publicTemporary, publicBytes);
                File.Move(privateTemporary, target, overwrite: true);
                File.Move(publicTemporary, target + ".pub", overwrite: true);
            }
            finally { CryptographicOperations.ZeroMemory(publicBytes); }

            if (key.Kind == ManagedKeyKind.Adb) { using var _ = AdbAuthentication.LoadOrCreate(target); }
            else { using var _ = HdcAuthentication.LoadOrCreate(target); }
        }
        finally
        {
            File.Delete(privateTemporary);
            File.Delete(publicTemporary);
        }
    }

    private ManagedKey Publish(string name, ManagedKeyKind kind, AsymmetricAlgorithm key)
    {
        Directory.CreateDirectory(Root);
        var key2 = new ManagedKey(Guid.NewGuid().ToString("N"), name.Trim(), kind, key.KeySize,
            "SHA256:" + Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo())), DateTimeOffset.UtcNow);
        var staging = Path.Combine(Root, ".create-" + key2.Id);
        Directory.CreateDirectory(staging);
        try
        {
            var path = Path.Combine(staging, PrivateName(kind));
            // APK and ZIP keys keep PKCS#8 PEM so a single format covers RSA/EC material (ZIP
            // signing needs an RSA key for OTA; read via LoadPrivateKey); Payload inherited the format.
            // APK 与 ZIP 密钥统一存 PKCS#8 PEM（兼容 RSA 与 EC）；ZIP 用于 OTA 签名需要 RSA。Payload 沿用该格式。
            var privateText = kind is ManagedKeyKind.Apk or ManagedKeyKind.Payload or ManagedKeyKind.Zip
                ? key.ExportPkcs8PrivateKeyPem()
                : ((RSA)key).ExportRSAPrivateKeyPem();
            Write(path, privateText);
            string publicText;
            if (kind == ManagedKeyKind.Adb)
            {
                var rsa = (RSA)key;
                using var publicRsa = RSA.Create();
                publicRsa.ImportParameters(rsa.ExportParameters(false));
                using var auth = new AdbAuthentication(publicRsa);
                publicText = Encoding.UTF8.GetString(auth.BuildPublicKeyPayload()).TrimEnd('\0') + "\n";
            }
            else publicText = key.ExportSubjectPublicKeyInfoPem() + "\n";
            Write(path + ".pub", publicText);
            if (kind is ManagedKeyKind.Payload or ManagedKeyKind.Jar or ManagedKeyKind.Apk or ManagedKeyKind.Zip)
                Write(Path.Combine(staging, PrivateName(kind) + ".crt"), CreateCertificate(name, key));
            Write(Path.Combine(staging, "key.json"), JsonSerializer.Serialize(key2, KeyLibraryJsonContext.Default.ManagedKey));
            Directory.Move(staging, KeyDirectory(key2.Id));
            return key2;
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }

    private static string PrivateName(ManagedKeyKind kind) => kind switch
    {
        ManagedKeyKind.Adb => "adbkey",
        ManagedKeyKind.Hdc => "hdckey",
        ManagedKeyKind.Payload => "payload-key.pem",
        ManagedKeyKind.Jar => "jar-key.pem",
        ManagedKeyKind.Apk => "apk-key.pem",
        ManagedKeyKind.Zip => "zip-key.pem",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private string KeyDirectory(string id) => Guid.TryParseExact(id, "N", out _) ? Path.Combine(Root, id)
        : throw new InvalidDataException("无效的密钥标识。");

    private static ManagedKey ReadMetadata(string path)
    {
        var key = JsonSerializer.Deserialize(ReadBounded(path), KeyLibraryJsonContext.Default.ManagedKey)
            ?? throw new InvalidDataException("无法读取密钥记录。");
        Validate(key.Name, key.Kind, key.Bits);
        if (Path.GetFileName(Path.GetDirectoryName(path)) != key.Id) throw new InvalidDataException("密钥记录与目录不一致。");
        return key;
    }

    private static byte[] ReadBounded(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > 64 * 1024) throw new InvalidDataException("密钥文件不能超过 64 KiB。");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static void Validate(string name, ManagedKeyKind kind, int bits)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 80 || name.Any(char.IsControl))
            throw new ArgumentException("请输入 1–80 个字符的密钥名称。");
        if (!(kind switch
        {
            ManagedKeyKind.Adb => bits == 2048,
            ManagedKeyKind.Hdc => bits == 3072,
            ManagedKeyKind.Payload => bits is 2048 or 4096,
            ManagedKeyKind.Jar => bits is 2048 or 4096,
            // APK signing accepts RSA 2048–4096 and EC curves P-256/P-384/P-521.
            // APK 签名接受 RSA 2048–4096 与 EC 曲线 P-256/P-384/P-521。
            ManagedKeyKind.Apk => bits is 2048 or 3072 or 4096 or 256 or 384 or 521,
            // OTA ZIP signing uses a platform-style RSA key.
            // OTA ZIP 签名使用平台风格的 RSA 密钥。
            ManagedKeyKind.Zip => bits is 2048 or 4096,
            _ => false
        }))
            throw new ArgumentException("密钥位数与用途不匹配：ADB 2048、HDC 3072、Payload/JAR/ZIP 2048/4096、APK RSA 2048–4096 或 EC 256/384/521。");
    }

    private static void Write(string path, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        try { Write(path, bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static void Write(string path, byte[] bytes)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var file = new FileStream(path, options);
        file.Write(bytes);
        file.Flush(true);
    }
}