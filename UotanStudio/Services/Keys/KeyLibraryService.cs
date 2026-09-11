using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using UotanStudio.Comm.Protocols.Adb;

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