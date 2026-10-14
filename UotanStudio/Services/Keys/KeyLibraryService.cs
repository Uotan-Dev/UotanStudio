using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using UotanStudio.Comm.Protocols.Adb;
using UotanStudio.Comm.Protocols.Hdc;
using UotanStudio.Firmware.AndroidMeta.Apk;
using UotanStudio.Firmware.BootSecurity.Avb.Signing;
using UotanStudio.Firmware.Core.IO;
using UotanStudio.Hash.Digests;

namespace UotanStudio.Services.Keys;

/// <summary>Supported key purposes; purpose determines the exported public key format.
/// <para>受支持的密钥用途；用途决定导出的公钥格式。</para></summary>
public enum ManagedKeyKind { Adb, Hdc, Payload, Jar, Apk, Zip, Avb }

/// <summary>Export bundle choices for a freshly generated APK signing certificate.
/// <para>新生成 APK 签名证书的导出格式组合。</para></summary>
public enum ApkCertificateExportFormat
{
    /// <summary>JKS + PKCS12 + PEM files. <para>JKS + PKCS12 + PEM 全部格式。</para></summary>
    All,
    /// <summary>Java keystore only. <para>仅 JKS。</para></summary>
    Jks,
    /// <summary>Pkcs12 only. <para>仅 PKCS12。</para></summary>
    Pkcs12,
    /// <summary>PEM private key and certificate only. <para>仅 PEM 私钥与证书。</para></summary>
    Pem,
}

/// <summary>Public metadata for a key pair. Private key contents are never stored in metadata.
/// <para>密钥对的公开元数据。私钥内容绝不存入元数据。</para></summary>
/// <param name="Id">The key identifier.<para>密钥标识符。</para></param>
/// <param name="Name">The display name.<para>显示名称。</para></param>
/// <param name="Kind">The key purpose.<para>密钥用途。</para></param>
/// <param name="Bits">The key size in bits.<para>以位表示的密钥长度。</para></param>
/// <param name="Fingerprint">The public-key fingerprint.<para>公钥指纹。</para></param>
/// <param name="Created">The creation timestamp.<para>创建时间戳。</para></param>
public sealed record ManagedKey(string Id, string Name, ManagedKeyKind Kind, int Bits, string Fingerprint, DateTimeOffset Created)
{
    public string Summary => Bits is 256 or 384 or 521
        ? $"{Kind.ToString().ToUpperInvariant()} · EC P-{Bits}"
        : $"{Kind.ToString().ToUpperInvariant()} · RSA {Bits}";
}

[JsonSerializable(typeof(ManagedKey))]
internal partial class KeyLibraryJsonContext : JsonSerializerContext;

/// <summary>Owns independent key directories and atomically publishes complete key pairs.
/// <para>管理各自独立的密钥目录，并以原子方式发布完整密钥对。</para></summary>
public sealed class KeyLibraryService
{
    public string Root { get; }

    public KeyLibraryService(string? root = null) => Root = Path.GetFullPath(root ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UotanStudio", "keys"));

    public IReadOnlyList<ManagedKey> List()
    {
        if (!Directory.Exists(Root)) return [];
        return [.. Directory.EnumerateDirectories(Root)
            .Where(p => Guid.TryParseExact(Path.GetFileName(p), "N", out _))
            .Select(p => ReadMetadata(Path.Combine(p, "key.json")))
            .OrderByDescending(k => k.Created)];
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

    /// <summary>Paths written for one generated APK signing certificate bundle.
    /// <para>一次生成的 APK 签名证书导出文件的路径集合。</para></summary>
    /// <param name="Key">The registered library key.<para>登记的库条目。</para></param>
    /// <param name="ExportedFiles">The exported file paths.<para>导出文件的路径列表。</para></param>
    public sealed record ApkCertificateBundle(ManagedKey Key, IReadOnlyList<string> ExportedFiles);

    /// <summary>
    /// Generates an APK signing key pair and self-signed certificate, registers it in the library, and
    /// exports the requested formats without overwriting existing files.
    /// <para>生成 APK 签名密钥对与自签证书，登记进密钥库，并按请求的格式导出，绝不覆盖已存在的文件。</para>
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
            List<string> exported = [];
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

    /// <summary>Loads a bounded PEM/DER private key and verifies that private operations are available.
    /// <para>加载有界的 PEM/DER 私钥，并验证私有运算可用。</para></summary>
    /// <param name="path">Path to the private key file.<para>私钥文件路径。</para></param>
    /// <param name="password">Password for an encrypted key.<para>加密私钥的密码。</para></param>
    /// <returns>The verified RSA private key.<para>已验证的 RSA 私钥。</para></returns>
    public static RSA LoadPrivateKey(string path, string password = "")
    {
        var key = LoadAnyPrivateKey(path, password);
        try
        {
            if (key is not RSA rsa) throw new InvalidDataException("此用途需要 RSA 私钥。");
            rsa.SignData("UotanStudio key import"u8, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            return rsa;
        }
        catch { key.Dispose(); throw; }
    }

    /// <summary>Loads a bounded PEM/DER private key as RSA or EC (PKCS#8 plain/encrypted, PKCS#1, SEC1).
    /// <para>以 RSA 或 EC 加载有界 PEM/DER 私钥（PKCS#8 明文/加密、PKCS#1、SEC1）。</para></summary>
    /// <param name="path">Path to the private key file.<para>私钥文件路径。</para></param>
    /// <param name="password">Password for an encrypted key.<para>加密私钥的密码。</para></param>
    /// <returns>The loaded asymmetric key.<para>加载得到的非对称密钥。</para></returns>
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
            key.Dispose();
            throw new InvalidDataException("私钥不包含可用的私有材料。", ex);
        }
    }

    public string PrivatePath(ManagedKey key) => Path.Combine(KeyDirectory(key.Id), PrivateName(key.Kind));
    public string PublicText(ManagedKey key) => Encoding.UTF8.GetString(ReadBounded(PrivatePath(key) + ".pub"));

    /// <summary>Returns the validated libavb public-key blob for an AVB library entry.
    /// <para>返回 AVB 库条目经过校验的 libavb 公钥数据块。</para></summary>
    /// <param name="key">The AVB library entry.<para>AVB 库条目。</para></param>
    /// <returns>The path to the public-key blob.<para>公钥数据块的路径。</para></returns>
    public string EnsureAvbPublicKey(ManagedKey key)
    {
        if (key.Kind != ManagedKeyKind.Avb)
        {
            throw new InvalidOperationException("Only AVB keys have an avb_pkmd.bin public-key blob.");
        }
        var path = Path.Combine(KeyDirectory(key.Id), "avb_pkmd.bin");
        if (File.Exists(path))
        {
            using var imported = AvbRsaPublicKeyCodec.Import(ReadBounded(path));
            if (!string.Equals(
                key.Fingerprint,
                "SHA256:" + Convert.ToHexString(Sha256.ComputeHash(imported.ExportSubjectPublicKeyInfo())),
                StringComparison.Ordinal))
            {
                throw new InvalidDataException("AVB public-key blob does not match the library entry.");
            }
            return path;
        }

        var temporary = TempFileHelper.CreateAdjacentPath(path);
        try
        {
            using var privateKey = LoadPrivateKey(PrivatePath(key));
            Write(temporary, AvbRsaPublicKeyCodec.Export(privateKey));
            try { File.Move(temporary, path, overwrite: false); }
            catch (IOException) when (File.Exists(path)) { }
        }
        finally
        {
            TempFileHelper.SafeDelete(temporary);
        }
        return EnsureAvbPublicKey(key);
    }

    /// <summary>Certificate file path for a Payload key; the file may be absent.
    /// <para>Payload 密钥的证书文件路径；该文件可能不存在。</para></summary>
    /// <param name="key">The Payload library entry.<para>Payload 库条目。</para></param>
    /// <returns>The certificate file path.<para>证书文件路径。</para></returns>
    public string CertificatePath(ManagedKey key) => PrivatePath(key) + ".crt";

    /// <summary>Returns the certificate path, generating a self-signed certificate when it is absent.
    /// <para>返回证书路径；证书缺失时生成自签证书。</para></summary>
    /// <param name="key">The Payload, JAR or ZIP library entry.<para>Payload、JAR 或 ZIP 库条目。</para></param>
    /// <returns>The certificate file path.<para>证书文件路径。</para></returns>
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

    private static string CreateCertificate(string name, AsymmetricAlgorithm key)
    {
        var subject = new X500DistinguishedNameBuilder();
        subject.AddCommonName(name);
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

    /// <summary>Moves a key into a recoverable local trash directory; exported copies are unaffected.
    /// <para>把密钥移入可恢复的本地回收目录；已导出的副本不受影响。</para></summary>
    /// <param name="key">The library entry to move.<para>要移动的库条目。</para></param>
    public void Remove(ManagedKey key)
    {
        var trash = Path.Combine(Root, ".deleted");
        Directory.CreateDirectory(trash);
        Directory.Move(KeyDirectory(key.Id), Path.Combine(trash, key.Id + "-" + Guid.NewGuid().ToString("N")));
    }

    /// <summary>Permanently deletes one key directory; never follows directory links or affects exports.
    /// <para>永久删除单个密钥目录；绝不跟随目录链接，也不影响已导出的副本。</para></summary>
    /// <param name="key">The library entry to delete.<para>要删除的库条目。</para></param>
    public void Delete(ManagedKey key)
    {
        var directory = KeyDirectory(key.Id);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("不能删除链接目录 / Cannot delete a linked key directory.");
        if (Directory.EnumerateDirectories(directory).Any()) throw new IOException("密钥目录包含未知子目录 / Unexpected key subdirectory.");
        foreach (var file in Directory.EnumerateFiles(directory)) File.Delete(file);
        Directory.Delete(directory);
    }

    /// <summary>Exports to a new directory, never replacing existing files.
    /// <para>导出到新目录，绝不替换已存在的文件。</para></summary>
    /// <param name="key">The library entry to export.<para>要导出的库条目。</para></param>
    /// <param name="destination">The directory that receives the export.<para>接收导出内容的目录。</para></param>
    /// <param name="includePrivate">Whether the private key is included.<para>是否包含私钥。</para></param>
    /// <returns>The created export directory path.<para>创建的导出目录路径。</para></returns>
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

    /// <summary>Finds an existing library key by fingerprint.
    /// <para>按指纹查找库中已有的密钥。</para></summary>
    /// <param name="kind">The key purpose to match.<para>要匹配的密钥用途。</para></param>
    /// <param name="fingerprint">The fingerprint to match, compared ignoring case.
    /// <para>要匹配的指纹，按忽略大小写的方式比较。</para></param>
    /// <returns>The matching key, or null when not found.<para>匹配的密钥；未找到时为 null。</para></returns>
    public ManagedKey? FindByFingerprint(ManagedKeyKind kind, string fingerprint)
    {
        if (string.IsNullOrEmpty(fingerprint)) return null;
        return List().FirstOrDefault(k => k.Kind == kind &&
            string.Equals(k.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Computes the SPKI SHA-256 fingerprint of the current runtime identity private key for a
    /// purpose; null when the file is missing or unreadable.
    /// <para>计算指定用途当前运行时身份私钥的 SPKI SHA-256 指纹；文件缺失或不可读时为 null。</para></summary>
    /// <param name="kind">The key purpose; must be Adb or Hdc.<para>密钥用途；必须为 Adb 或 Hdc。</para></param>
    /// <returns>The fingerprint in the same form as <see cref="ManagedKey.Fingerprint"/>, or null.
    /// <para>与 <see cref="ManagedKey.Fingerprint"/> 同形的指纹；否则为 null。</para></returns>
    public static string? GetRuntimeIdentityFingerprint(ManagedKeyKind kind)
    {
        if (kind is not (ManagedKeyKind.Adb or ManagedKeyKind.Hdc)) return null;
        var path = kind == ManagedKeyKind.Adb ? AdbAuthentication.DefaultKeyFilePath : HdcAuthentication.DefaultKeyFilePath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try
        {
            using var rsa = LoadPrivateKey(path);
            return "SHA256:" + Convert.ToHexString(Sha256.ComputeHash(rsa.ExportSubjectPublicKeyInfo()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or System.Security.Cryptography.CryptographicException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>Imports a runtime identity private key; returns the existing entry when a same-kind,
    /// same-fingerprint key already exists.
    /// <para>导入运行时身份私钥；若库中已存在同指纹同用途的密钥则直接返回该条目。</para></summary>
    /// <param name="name">The display name for the imported key.<para>导入密钥的显示名称。</para></param>
    /// <param name="kind">The key purpose; must be Adb or Hdc.<para>密钥用途；必须为 Adb 或 Hdc。</para></param>
    /// <param name="sourcePrivateKeyPath">The runtime private key file to import.
    /// <para>要导入的运行时私钥文件路径。</para></param>
    /// <returns>The newly imported or already existing key.<para>新导入或已存在的密钥。</para></returns>
    public ManagedKey ImportIdentity(string name, ManagedKeyKind kind, string sourcePrivateKeyPath)
    {
        using var rsa = LoadPrivateKey(sourcePrivateKeyPath);
        var fingerprint = "SHA256:" + Convert.ToHexString(Sha256.ComputeHash(rsa.ExportSubjectPublicKeyInfo()));
        if (FindByFingerprint(kind, fingerprint) is { } existing) return existing;
        return Publish(name, kind, rsa);
    }

    /// <summary>Publishes a library key as the runtime identity, atomically replacing the target private
    /// and public key files.
    /// <para>将库中的密钥发布为运行时身份，原子替换目标私钥与公钥文件。</para></summary>
    /// <param name="key">The library key to publish; must be an Adb or Hdc key.
    /// <para>要发布的库中密钥；必须为 Adb 或 Hdc 密钥。</para></param>
    public void PublishDefault(ManagedKey key) => PublishDefault(key, key.Kind == ManagedKeyKind.Adb
        ? AdbAuthentication.DefaultKeyFilePath : HdcAuthentication.DefaultKeyFilePath);

    /// <summary>Publishes a library key to an explicit runtime identity path.
    /// <para>把库中密钥发布到显式指定的运行时身份路径。</para></summary>
    /// <param name="key">The library key to publish; must be an Adb or Hdc key.
    /// <para>要发布的库中密钥；必须为 Adb 或 Hdc 密钥。</para></param>
    /// <param name="target">The runtime private key file path to replace.
    /// <para>要替换的运行时私钥文件路径。</para></param>
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
            "SHA256:" + Convert.ToHexString(Sha256.ComputeHash(key.ExportSubjectPublicKeyInfo())), DateTimeOffset.UtcNow);
        var staging = Path.Combine(Root, ".create-" + key2.Id);
        Directory.CreateDirectory(staging);
        try
        {
            var path = Path.Combine(staging, PrivateName(kind));
            var privateText = kind is ManagedKeyKind.Apk or ManagedKeyKind.Payload or ManagedKeyKind.Zip or ManagedKeyKind.Avb
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
            if (kind == ManagedKeyKind.Avb)
            {
                Write(Path.Combine(staging, "avb_pkmd.bin"), AvbRsaPublicKeyCodec.Export((RSA)key));
            }
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
        ManagedKeyKind.Avb => "avb-key.pem",
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
            ManagedKeyKind.Apk => bits is 2048 or 3072 or 4096 or 256 or 384 or 521,
            ManagedKeyKind.Zip => bits is 2048 or 4096,
            ManagedKeyKind.Avb => bits is 2048 or 4096,
            _ => false
        }))
            throw new ArgumentException("密钥位数与用途不匹配：ADB 2048、HDC 3072、Payload/JAR/ZIP/AVB 2048/4096、APK RSA 2048–4096 或 EC 256/384/521。");
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