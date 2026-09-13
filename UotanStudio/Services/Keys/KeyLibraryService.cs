using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using UotanStudio.Comm.Protocols.Adb;
using UotanStudio.Comm.Protocols.Hdc;

namespace UotanStudio.Services.Keys;

/// <summary>Supported key purposes; purpose determines the exported public key format.</summary>
public enum ManagedKeyKind { Adb, Hdc, Payload }

/// <summary>Public metadata for a key pair. Private key contents are never stored in metadata.</summary>
public sealed record ManagedKey(string Id, string Name, ManagedKeyKind Kind, int Bits, string Fingerprint, DateTimeOffset Created)
{
    public string Summary => $"{Kind.ToString().ToUpperInvariant()} · RSA {Bits}";
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
        using var rsa = RSA.Create(bits);
        return Publish(name, kind, rsa);
    }

    public ManagedKey Import(string name, ManagedKeyKind kind, string path, string password)
    {
        using var rsa = LoadPrivateKey(path, password);
        Validate(name, kind, rsa.KeySize);
        return Publish(name, kind, rsa);
    }

    /// <summary>Loads a bounded PEM/DER private key and verifies that private operations are available. The caller disposes the returned key.</summary>
    public static RSA LoadPrivateKey(string path, string password = "")
    {
        var bytes = ReadBounded(path);
        var rsa = RSA.Create();
        try
        {
            var text = Encoding.UTF8.GetString(bytes);
            if (text.Contains("-----BEGIN", StringComparison.Ordinal))
            {
                if (text.Contains("ENCRYPTED PRIVATE KEY", StringComparison.Ordinal)) rsa.ImportFromEncryptedPem(text, password);
                else rsa.ImportFromPem(text);
            }
            else
            {
                int read;
                try { rsa.ImportPkcs8PrivateKey(bytes, out read); }
                catch (CryptographicException) { rsa.ImportRSAPrivateKey(bytes, out read); }
                if (read != bytes.Length) throw new InvalidDataException("密钥文件包含多余数据。");
            }
            // Reject public-only inputs and exercise the private operation before publication.
            rsa.SignData("UotanStudio key import"u8, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            return rsa;
        }
        catch { rsa.Dispose(); throw; }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public string PrivatePath(ManagedKey key) => Path.Combine(KeyDirectory(key.Id), PrivateName(key.Kind));
    public string PublicText(ManagedKey key) => Encoding.UTF8.GetString(ReadBounded(PrivatePath(key) + ".pub"));

    /// <summary>Certificate file path for a Payload key; the file may not exist for keys created before certificates.</summary>
    public string CertificatePath(ManagedKey key) => PrivatePath(key) + ".crt";

    /// <summary>Returns the certificate path, generating the self-signed certificate for a legacy Payload key on first use.</summary>
    public string EnsureCertificate(ManagedKey key)
    {
        if (key.Kind != ManagedKeyKind.Payload) throw new InvalidOperationException("Only Payload keys carry certificates.");
        var path = CertificatePath(key);
        if (File.Exists(path)) return path;
        var directory = KeyDirectory(key.Id);
        var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using var rsa = LoadPrivateKey(PrivatePath(key));
            Write(temporary, CreateCertificate(key.Name, rsa));
            try { File.Move(temporary, path, overwrite: false); }
            catch (IOException) when (File.Exists(path)) { }
        }
        finally { File.Delete(temporary); }
        return path;
    }

    // Self-signed RSA SHA-256 certificate sharing the private key's SPKI, for OTA zip
    // outer signatures (signapk -w) and the otacert entry.
    private static string CreateCertificate(string name, RSA rsa)
    {
        var subject = new X500DistinguishedNameBuilder();
        subject.AddCommonName(name);
        var request = new CertificateRequest(subject.Build(), rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
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

    private ManagedKey Publish(string name, ManagedKeyKind kind, RSA rsa)
    {
        Directory.CreateDirectory(Root);
        var key = new ManagedKey(Guid.NewGuid().ToString("N"), name.Trim(), kind, rsa.KeySize,
            "SHA256:" + Convert.ToHexString(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo())), DateTimeOffset.UtcNow);
        var staging = Path.Combine(Root, ".create-" + key.Id);
        Directory.CreateDirectory(staging);
        try
        {
            var path = Path.Combine(staging, PrivateName(kind));
            Write(path, kind == ManagedKeyKind.Payload ? rsa.ExportPkcs8PrivateKeyPem() : rsa.ExportRSAPrivateKeyPem());
            string publicText;
            if (kind == ManagedKeyKind.Adb)
            {
                using var publicRsa = RSA.Create();
                publicRsa.ImportParameters(rsa.ExportParameters(false));
                using var auth = new AdbAuthentication(publicRsa);
                publicText = Encoding.UTF8.GetString(auth.BuildPublicKeyPayload()).TrimEnd('\0') + "\n";
            }
            else publicText = rsa.ExportSubjectPublicKeyInfoPem() + "\n";
            Write(path + ".pub", publicText);
            if (kind == ManagedKeyKind.Payload) Write(Path.Combine(staging, PrivateName(kind) + ".crt"), CreateCertificate(name, rsa));
            Write(Path.Combine(staging, "key.json"), JsonSerializer.Serialize(key, KeyLibraryJsonContext.Default.ManagedKey));
            Directory.Move(staging, KeyDirectory(key.Id));
            return key;
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }

    private static string PrivateName(ManagedKeyKind kind) => kind switch
    {
        ManagedKeyKind.Adb => "adbkey",
        ManagedKeyKind.Hdc => "hdckey",
        ManagedKeyKind.Payload => "payload-key.pem",
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
            _ => false
        }))
            throw new ArgumentException("密钥位数与用途不匹配：ADB 2048、HDC 3072、Payload 2048/4096。");
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