using System.Security.Cryptography;
using System.Text;

namespace UotanStudio.Comm.Protocols.Hdc;

/// <summary>
/// Specifies the HDC authentication verification algorithm.
/// <para>指定 HDC 认证验证算法。</para>
/// </summary>
public enum HdcAuthVerifyType : byte
{
    /// <summary>
    /// Legacy RSA encryption mode (PKCS#1 v1.5).
    /// <para>旧版 RSA 加密模式（PKCS#1 v1.5）。</para>
    /// </summary>
    RsaEncrypt = 0,

    /// <summary>
    /// RSA-3072 with PSS padding and SHA-512 digest (primary).
    /// <para>RSA-3072 配合 PSS 填充和 SHA-512 摘要（主模式）。</para>
    /// </summary>
    Rsa3072Sha512 = 1,

    /// <summary>
    /// PSK-TLS with AES-128-GCM-SHA-256.
    /// <para>PSK-TLS 搭配 AES-128-GCM-SHA-256。</para>
    /// </summary>
    PskTlsAes128GcmSha256 = 2,

    /// <summary>
    /// Unknown authentication type.
    /// <para>未知认证类型。</para>
    /// </summary>
    Unknown = 100,
}


/// <summary>
/// Manages HDC RSA-3072 authentication: key generation, loading, saving,
/// and RSA-PSS SHA-512 token signing. Aligned with the OpenHarmony HDC auth
/// implementation (developtools_hdc/auth.cpp).
/// <para>管理 HDC RSA-3072 认证：密钥生成、加载、保存以及 RSA-PSS SHA-512
/// 令牌签名。与 OpenHarmony HDC 认证实现（developtools_hdc/auth.cpp）对齐。</para>
/// </summary>
public sealed class HdcAuthentication : IDisposable
{
    /// <summary>
    /// RSA key size in bits used by HDC.
    /// <para>HDC 使用的 RSA 密钥位数。</para>
    /// </summary>
    private const int RSA_KEY_BITS = 3072;

    private readonly RSA _rsa;
    private readonly HdcAuthVerifyType _verifyType;

    /// <summary>
    /// Gets the default key directory (~/.hdc), matching the OpenHarmony HDC key location.
    /// <para>获取默认密钥目录（~/.hdc），与 OpenHarmony HDC 密钥位置一致。</para>
    /// </summary>
    public static string DefaultKeyDirectory => Path.Combine(GetHomeDirectory(), ".hdc");

    /// <summary>
    /// Gets the default private key file path (~/.hdc/keys), matching the OpenHarmony HDC
    /// key location.
    /// <para>获取默认私钥文件路径（~/.hdc/keys），与 OpenHarmony HDC 密钥位置一致。</para>
    /// </summary>
    public static string DefaultKeyFilePath => Path.Combine(DefaultKeyDirectory, "keys");

    /// <summary>
    /// Gets the authentication verify type.
    /// <para>获取认证验证类型。</para>
    /// </summary>
    public HdcAuthVerifyType VerifyType => _verifyType;

    /// <summary>
    /// Initializes authentication with the supplied 3072-bit RSA key using the
    /// primary <see cref="HdcAuthVerifyType.Rsa3072Sha512"/> verification type.
    /// <para>使用提供的 3072 位 RSA 密钥和主验证类型
    /// <see cref="HdcAuthVerifyType.Rsa3072Sha512"/> 初始化认证。</para>
    /// </summary>
    /// <param name="rsa">The RSA key to use. 待使用的 RSA 密钥。</param>
    /// <exception cref="ArgumentNullException"><paramref name="rsa"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">The key is not 3072 bits. 密钥不是 3072 位。</exception>
    public HdcAuthentication(RSA rsa)
        : this(rsa, HdcAuthVerifyType.Rsa3072Sha512)
    {
    }

    /// <summary>
    /// Initializes authentication with the supplied 3072-bit RSA key and the specified
    /// verification type.
    /// <para>使用提供的 3072 位 RSA 密钥和指定验证类型初始化认证。</para>
    /// </summary>
    /// <param name="rsa">The RSA key to use. 待使用的 RSA 密钥。</param>
    /// <param name="verifyType">The authentication verification algorithm. 认证验证算法。</param>
    /// <exception cref="ArgumentNullException"><paramref name="rsa"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">The key is not 3072 bits. 密钥不是 3072 位。</exception>
    public HdcAuthentication(RSA rsa, HdcAuthVerifyType verifyType)
    {
        _rsa = rsa ?? throw new ArgumentNullException(nameof(rsa));
        if (rsa.KeySize != RSA_KEY_BITS)
        {
            throw new ArgumentException(
                $"HDC authentication requires a {RSA_KEY_BITS}-bit RSA key, but got {rsa.KeySize}-bit.",
                nameof(rsa));
        }

        _verifyType = verifyType;
    }

    /// <summary>
    /// Creates authentication with a newly generated 3072-bit RSA key.
    /// <para>使用新生成的 3072 位 RSA 密钥创建认证。</para>
    /// </summary>
    /// <returns>A new <see cref="HdcAuthentication"/> instance. 新的 <see cref="HdcAuthentication"/> 实例。</returns>
    public static HdcAuthentication CreateNew()
    {
        var rsa = RSA.Create(RSA_KEY_BITS);
        return new HdcAuthentication(rsa);
    }

    /// <summary>
    /// Loads the authentication key from <paramref name="keyPath"/> (PEM, PKCS#1 or
    /// PKCS#8), or generates and persists a new 3072-bit key when the file does not
    /// exist. The public key is written next to it with a ".pub" extension.
    /// <para>从 <paramref name="keyPath"/> 加载认证密钥（PEM，PKCS#1 或 PKCS#8）；
    /// 文件不存在时生成并持久化新的 3072 位密钥。公钥以 ".pub" 扩展名写入同名文件。</para>
    /// </summary>
    /// <param name="keyPath">
    /// The private key path; defaults to <see cref="DefaultKeyFilePath"/>.
    /// 私钥路径；默认为 <see cref="DefaultKeyFilePath"/>。
    /// </param>
    /// <returns>The loaded or newly created authentication. 加载或新建的认证实例。</returns>
    public static HdcAuthentication LoadOrCreate(string? keyPath = null)
    {
        var path = keyPath ?? DefaultKeyFilePath;

        if (File.Exists(path))
        {
            return LoadPrivateKey(path);
        }

        var created = CreateNew();
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Write the private key with restrictive permissions from the moment of
        // creation.  On Unix we use .NET 10's FileStreamOptions.UnixCreateMode to
        // open the file with 0600 (user read/write only) atomically, avoiding the
        // "create 0644 then chmod" exposure window.  On Windows the ACL inherits
        // from the parent directory, which is the standard behaviour for %USERPROFILE%.
        WriteKeyFile(path, created._rsa.ExportRSAPrivateKeyPem());
        WriteKeyFile(path + ".pub", created.PublicKeyToBase64());
        return created;
    }

    /// <summary>
    /// Signs the token using the configured verification algorithm.
    /// <para>使用配置的验证算法对令牌签名。</para>
    /// When <see cref="_verifyType"/> is <see cref="HdcAuthVerifyType.Rsa3072Sha512"/>,
    /// performs RSA-PSS with SHA-512.
    /// 当 <see cref="_verifyType"/> 为 <see cref="HdcAuthVerifyType.Rsa3072Sha512"/> 时，
    /// 使用 RSA-PSS SHA-512 签名。
    /// When <see cref="_verifyType"/> is <see cref="HdcAuthVerifyType.RsaEncrypt"/>,
    /// performs legacy RSA PKCS#1 v1.5 encryption.
    /// 当 <see cref="_verifyType"/> 为 <see cref="HdcAuthVerifyType.RsaEncrypt"/> 时，
    /// 使用旧版 RSA PKCS#1 v1.5 加密。
    /// </summary>
    /// <param name="token">The token to sign. 待签名的令牌。</param>
    /// <returns>The signed payload. 签名后的负载。</returns>
    /// <exception cref="ArgumentException"><paramref name="token"/> is empty. 令牌为空。</exception>
    public byte[] SignToken(byte[] token)
    {
        if (token is null || token.Length == 0)
        {
            throw new ArgumentException("Token must not be empty.", nameof(token));
        }

        if (_verifyType == HdcAuthVerifyType.Rsa3072Sha512)
        {
            // RSA-PSS SHA-512 signature (primary HDC authentication mode).
            return _rsa.SignData(token, HashAlgorithmName.SHA512, RSASignaturePadding.Pss);
        }
        else
        {
            // Legacy mode: RSA PKCS#1 v1.5 encryption.
            return _rsa.Encrypt(token, RSAEncryptionPadding.Pkcs1);
        }
    }

    /// <summary>
    /// Builds the public key payload for the HDC handshake: Base64-encoded
    /// SubjectPublicKeyInfo (DER) suitable for the <c>buf</c> field of the handshake
    /// message.
    /// <para>构建 HDC 握手的公钥负载：Base64 编码的 SubjectPublicKeyInfo（DER），
    /// 适用于握手消息的 <c>buf</c> 字段。</para>
    /// </summary>
    /// <returns>The Base64-encoded public key payload bytes.
    /// Base64 编码的公钥负载字节。</returns>
    public byte[] BuildPublicKeyPayload()
    {
        var base64 = PublicKeyToBase64();
        return Encoding.UTF8.GetBytes(base64);
    }

    /// <summary>
    /// Exports the RSA public key as a Base64-encoded DER SubjectPublicKeyInfo string.
    /// <para>将 RSA 公钥导出为 Base64 编码的 DER SubjectPublicKeyInfo 字符串。</para>
    /// </summary>
    /// <returns>The Base64-encoded public key. Base64 编码的公钥。</returns>
    public string PublicKeyToBase64()
    {
        var der = _rsa.ExportSubjectPublicKeyInfo();
        return Convert.ToBase64String(der);
    }

    /// <summary>
    /// Loads the private key from the specified PEM file path and returns a new
    /// <see cref="HdcAuthentication"/> instance. Handles file-not-found and
    /// permission errors gracefully.
    /// <para>从指定 PEM 文件路径加载私钥并返回新的 <see cref="HdcAuthentication"/> 实例。
    /// 优雅地处理文件不存在和权限不足等错误。</para>
    /// </summary>
    /// <param name="path">The PEM private key file path. PEM 私钥文件路径。</param>
    /// <returns>The loaded authentication. 加载的认证实例。</returns>
    /// <exception cref="FileNotFoundException">The key file does not exist. 密钥文件不存在。</exception>
    /// <exception cref="InvalidOperationException">The key is not 3072 bits. 密钥不是 3072 位。</exception>
    public static HdcAuthentication LoadPrivateKey(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"HDC private key file not found: '{path}'.", path);
        }

        string pem;
        try
        {
            pem = File.ReadAllText(path);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException($"Access denied reading HDC key file: '{path}'.", ex);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException($"I/O error reading HDC key file: '{path}'.", ex);
        }

        var rsa = RSA.Create();
        try
        {
            rsa.ImportFromPem(pem);
        }
        catch (Exception ex)
        {
            rsa.Dispose();
            throw new InvalidOperationException($"Failed to parse PEM key at '{path}'.", ex);
        }

        if (rsa.KeySize != RSA_KEY_BITS)
        {
            var keySize = rsa.KeySize;
            rsa.Dispose();
            throw new InvalidOperationException(
                $"The HDC key at '{path}' is {keySize} bits; {RSA_KEY_BITS} bits are required.");
        }

        return new HdcAuthentication(rsa);
    }

    /// <summary>
    /// Saves the private key to the specified path in PEM format with restrictive
    /// file permissions.
    /// <para>以受限文件权限将私钥保存为 PEM 格式到指定路径。</para>
    /// </summary>
    /// <param name="path">The destination file path. 目标文件路径。</param>
    public void SavePrivateKey(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        WriteKeyFile(path, _rsa.ExportRSAPrivateKeyPem());
    }

    /// <summary>
    /// Gets the default private key path (~/.hdc/keys).
    /// <para>获取默认私钥路径（~/.hdc/keys）。</para>
    /// </summary>
    /// <returns>The default private key path. 默认私钥路径。</returns>
    public static string GetKeyPath() => DefaultKeyFilePath;

    /// <summary>
    /// Gets the default public key path (~/.hdc/keys.pub).
    /// <para>获取默认公钥路径（~/.hdc/keys.pub）。</para>
    /// </summary>
    /// <returns>The default public key path. 默认公钥路径。</returns>
    public static string GetPublicKeyPath() => DefaultKeyFilePath + ".pub";

    /// <summary>
    /// Resolves the user home directory across platforms.
    /// <para>跨平台解析用户主目录。</para>
    /// </summary>
    /// <returns>The user home directory path. 用户主目录路径。</returns>
    private static string GetHomeDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
        {
            return home;
        }

        home = Environment.GetEnvironmentVariable("USERPROFILE")
            ?? Environment.GetEnvironmentVariable("HOME")
            ?? string.Empty;

        return string.IsNullOrEmpty(home) ? "." : home;
    }

    /// <summary>
    /// Writes key text to <paramref name="path"/> with restrictive permissions from
    /// the moment of creation.  On Unix this uses .NET 10's
    /// <see cref="FileStreamOptions.UnixCreateMode"/> to atomically create the file
    /// with mode 0600 (user read/write only), eliminating the "create 0644 then chmod"
    /// exposure window.  On Windows the file inherits ACLs from the parent directory.
    /// <para>以创建即受限的权限将密钥文本写入 <paramref name="path"/>。Unix 下使用
    /// .NET 10 的 <see cref="FileStreamOptions.UnixCreateMode"/> 原子创建 0600 文件，
    /// 消除"先 0644 再 chmod"暴露窗口。Windows 下继承父目录 ACL。</para>
    /// </summary>
    /// <param name="path">Destination file path. 目标文件路径。</param>
    /// <param name="contents">The text contents to write. 待写入的文本内容。</param>
    private static void WriteKeyFile(string path, string contents)
    {
        var bytes = new UTF8Encoding(false).GetBytes(contents);
        WriteKeyFile(path, bytes);
    }

    /// <summary>
    /// Writes key bytes to <paramref name="path"/> with restrictive permissions from
    /// the moment of creation (see <see cref="WriteKeyFile(string, string)"/>).
    /// <para>以创建即受限的权限将密钥字节写入 <paramref name="path"/>
    /// （见 <see cref="WriteKeyFile(string, string)"/>）。</para>
    /// </summary>
    /// <param name="path">Destination file path. 目标文件路径。</param>
    /// <param name="bytes">The bytes to write. 待写入的字节。</param>
    private static void WriteKeyFile(string path, byte[] bytes)
    {
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllBytes(path, bytes);
            return;
        }

        // Unix: atomically create with 0600 via FileStreamOptions.UnixCreateMode.
        // Fall back to File.WriteAllBytes on older runtimes that lack UnixCreateMode.
        var options = new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
        };

        try
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        catch (PlatformNotSupportedException)
        {
            // Runtime does not support UnixCreateMode; proceed with default mode.
        }

        using (var fs = new FileStream(path, options))
        {
            fs.Write(bytes, 0, bytes.Length);
        }
    }

    /// <summary>
    /// Releases the underlying RSA key.
    /// <para>释放底层 RSA 密钥。</para>
    /// </summary>
    public void Dispose()
    {
        _rsa.Dispose();
        GC.SuppressFinalize(this);
    }
}
