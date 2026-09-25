using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Encodings;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Security;

namespace UotanStudio.Comm.Protocols.Hdc;

/// <summary>
/// Specifies the HDC authentication verification algorithm.
/// <para>指定 HDC 认证验证算法。</para>
/// </summary>
public enum HdcAuthVerifyType : byte
{
    /// <summary>
    /// Legacy RSA encryption mode (PKCS#1 v1.5).
    /// <para>RSA 加密模式（PKCS#1 v1.5，类型 0）。</para>
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
    /// Gets the default key directory (the per-user UotanStudio data directory), independent of the official HDC identity.
    /// <para>获取 UotanStudio 独立身份的用户数据目录，不读取官方 HDC 密钥。</para>
    /// </summary>
    /// <value>Path <c>&lt;LocalApplicationData&gt;/UotanStudio/Hdc</c>; the directory may not exist yet. <para>路径 <c>&lt;LocalApplicationData&gt;/UotanStudio/Hdc</c>；目录可能尚未创建。</para></value>
    public static string DefaultKeyDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UotanStudio", "Hdc");

    /// <summary>
    /// Gets the default private key file path (UotanStudio/Hdc/hdckey), independent of the official HDC identity.
    /// <para>获取 UotanStudio 独立身份的私钥文件路径。</para>
    /// </summary>
    /// <value><c>hdckey</c> inside <see cref="DefaultKeyDirectory"/>; the file may not exist yet. <para><see cref="DefaultKeyDirectory"/> 下的 <c>hdckey</c> 文件；文件可能尚未创建。</para></value>
    public static string DefaultKeyFilePath => Path.Combine(DefaultKeyDirectory, "hdckey");

    /// <summary>
    /// Gets the authentication verify type.
    /// <para>获取认证验证类型。</para>
    /// </summary>
    /// <value>The algorithm selected at construction; <see cref="SignToken(byte[])"/> uses it as the default. <para>构造时选定的算法；<see cref="SignToken(byte[])"/> 将其作为默认值使用。</para></value>
    public HdcAuthVerifyType VerifyType => _verifyType;

    /// <summary>
    /// Initializes authentication with the supplied 3072-bit RSA key using the
    /// primary <see cref="HdcAuthVerifyType.Rsa3072Sha512"/> verification type.
    /// <para>使用提供的 3072 位 RSA 密钥和主验证类型
    /// <see cref="HdcAuthVerifyType.Rsa3072Sha512"/> 初始化认证。</para>
    /// </summary>
    /// <param name="rsa">The RSA key to use. <para>待使用的 RSA 密钥。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="rsa"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">The key is not 3072 bits. <para>密钥不是 3072 位。</para></exception>
    public HdcAuthentication(RSA rsa)
        : this(rsa, HdcAuthVerifyType.Rsa3072Sha512)
    {
    }

    /// <summary>
    /// Initializes authentication with the supplied 3072-bit RSA key and the specified
    /// verification type.
    /// <para>使用提供的 3072 位 RSA 密钥和指定验证类型初始化认证。</para>
    /// </summary>
    /// <param name="rsa">The RSA key to use. <para>待使用的 RSA 密钥。</para></param>
    /// <param name="verifyType">The authentication verification algorithm. <para>认证验证算法。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="rsa"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">The key is not 3072 bits. <para>密钥不是 3072 位。</para></exception>
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
    /// <returns>A new <see cref="HdcAuthentication"/> instance. <para>新的 <see cref="HdcAuthentication"/> 实例。</para></returns>
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
    /// <para>私钥路径；默认为 <see cref="DefaultKeyFilePath"/>。</para>
    /// </param>
    /// <returns>The loaded or newly created authentication. <para>加载或新建的认证实例。</para></returns>
    public static HdcAuthentication LoadOrCreate(string? keyPath = null)
    {
        var path = keyPath ?? DefaultKeyFilePath;

        if (File.Exists(path))
        {
            var existing = LoadPrivateKey(path);
            try { EnsurePublicKey(path, existing); return existing; }
            catch { existing.Dispose(); throw; }
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, ".hdckey-" + Guid.NewGuid().ToString("N"));
        using var created = CreateNew();
        try
        {
            WriteKeyFile(temporaryPath, created._rsa.ExportRSAPrivateKeyPem());
            try { File.Move(temporaryPath, path, overwrite: false); }
            catch (IOException) when (File.Exists(path))
            {
            }
            var loaded = LoadPrivateKey(path);
            try
            {
                EnsurePublicKey(path, loaded);
                return loaded;
            }
            catch { loaded.Dispose(); throw; }
        }
        finally { File.Delete(temporaryPath); }
    }

    private static void EnsurePublicKey(string path, HdcAuthentication authentication)
    {
        if (File.Exists(path + ".pub")) return;
        var temporary = path + ".pub-" + Guid.NewGuid().ToString("N");
        try
        {
            WriteKeyFile(temporary, authentication._rsa.ExportSubjectPublicKeyInfoPem() + "\n");
            try { File.Move(temporary, path + ".pub", overwrite: false); }
            catch (IOException) when (File.Exists(path + ".pub")) { }
        }
        finally { File.Delete(temporary); }
    }

    /// <summary>
    /// Signs the token using the configured verification algorithm.
    /// When <see cref="_verifyType"/> is <see cref="HdcAuthVerifyType.Rsa3072Sha512"/>,
    /// performs RSA-PSS with SHA-512.
    /// When <see cref="_verifyType"/> is <see cref="HdcAuthVerifyType.RsaEncrypt"/>,
    /// performs legacy RSA PKCS#1 v1.5 encryption.
    /// <para>使用配置的验证算法对令牌签名。当 <see cref="_verifyType"/> 为
    /// <see cref="HdcAuthVerifyType.Rsa3072Sha512"/> 时使用 RSA-PSS SHA-512 签名；
    /// 当 <see cref="_verifyType"/> 为 <see cref="HdcAuthVerifyType.RsaEncrypt"/> 时使用
    /// RSA PKCS#1 v1.5 加密。</para>
    /// </summary>
    /// <param name="token">The token to sign. <para>待签名的令牌。</para></param>
    /// <returns>The signed payload. <para>签名后的负载。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="token"/> is empty. <para>令牌为空。</para></exception>
    public byte[] SignToken(byte[] token)
        => SignToken(token, _verifyType);

    /// <summary>
    /// Signs a daemon challenge with the negotiated upstream RSA algorithm.
    /// <para>使用协商的上游 RSA 算法对守护进程挑战签名。</para>
    /// </summary>
    /// <param name="token">Challenge bytes from the daemon handshake; must not be empty. <para>来自守护进程握手的挑战字节；不得为空。</para></param>
    /// <param name="verifyType">Negotiated algorithm; only <see cref="HdcAuthVerifyType.Rsa3072Sha512"/> and <see cref="HdcAuthVerifyType.RsaEncrypt"/> are implemented. <para>协商算法；仅实现了 <see cref="HdcAuthVerifyType.Rsa3072Sha512"/> 与 <see cref="HdcAuthVerifyType.RsaEncrypt"/>。</para></param>
    /// <returns>
    /// For RSA-3072, the PSS SHA-512 signature (384 bytes); for legacy mode, the PKCS#1 v1.5 blinded private encryption of the whole token (384 bytes).
    /// <para>RSA-3072 模式返回 PSS SHA-512 签名（384 字节）；<see cref="HdcAuthVerifyType.RsaEncrypt"/> 模式返回整个令牌的 PKCS#1 v1.5 盲化私钥加密结果（384 字节）。</para>
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="token"/> is <see langword="null"/> or empty. <para><paramref name="token"/> 为 <see langword="null"/> 或空。</para></exception>
    /// <exception cref="NotSupportedException"><paramref name="verifyType"/> selects an unimplemented algorithm; legacy mode additionally requires the token to fit the 3072-bit block. <para><paramref name="verifyType"/> 选择了未实现的算法；<see cref="HdcAuthVerifyType.RsaEncrypt"/> 模式还要求令牌适配 3072 位分组。</para></exception>
    public byte[] SignToken(byte[] token, HdcAuthVerifyType verifyType)
    {
        if (token is null || token.Length == 0)
        {
            throw new ArgumentException("Token must not be empty.", nameof(token));
        }

        if (verifyType == HdcAuthVerifyType.Rsa3072Sha512)
        {
            return _rsa.SignData(token, HashAlgorithmName.SHA512, RSASignaturePadding.Pss);
        }
        if (verifyType == HdcAuthVerifyType.RsaEncrypt)
        {
            var signer = new Pkcs1Encoding(new RsaBlindedEngine());
            signer.Init(true, DotNetUtilities.GetRsaKeyPair(_rsa).Private);
            return signer.ProcessBlock(token, 0, token.Length);
        }
        throw new NotSupportedException($"HDC authentication algorithm {verifyType} is not implemented.");
    }

    /// <summary>
    /// Builds the public key payload for the HDC handshake: Base64-encoded
    /// SubjectPublicKeyInfo (DER) suitable for the <c>buf</c> field of the handshake
    /// message.
    /// <para>构建 HDC 握手的公钥负载：Base64 编码的 SubjectPublicKeyInfo（DER），
    /// 适用于握手消息的 <c>buf</c> 字段。</para>
    /// </summary>
    /// <returns>The Base64-encoded public key payload bytes.
    /// <para>Base64 编码的公钥负载字节。</para></returns>
    public byte[] BuildDaemonPublicKeyPayload() => Encoding.UTF8.GetBytes("UotanStudio@" + Environment.MachineName + "\f" + _rsa.ExportSubjectPublicKeyInfoPem() + "\n");

    /// <summary>
    /// Builds the handshake public-key payload: the UTF-8 bytes of the Base64-encoded DER SubjectPublicKeyInfo.
    /// <para>构建握手公钥负载：Base64 编码 DER SubjectPublicKeyInfo 的 UTF-8 字节。</para>
    /// </summary>
    /// <returns>The UTF-8 encoded Base64 public key. <para>UTF-8 编码的 Base64 公钥。</para></returns>
    public byte[] BuildPublicKeyPayload()
    {
        var base64 = PublicKeyToBase64();
        return Encoding.UTF8.GetBytes(base64);
    }

    /// <summary>
    /// Exports the RSA public key as a Base64-encoded DER SubjectPublicKeyInfo string.
    /// <para>将 RSA 公钥导出为 Base64 编码的 DER SubjectPublicKeyInfo 字符串。</para>
    /// </summary>
    /// <returns>The Base64-encoded public key. <para>Base64 编码的公钥。</para></returns>
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
    /// <param name="path">The PEM private key file path. <para>PEM 私钥文件路径。</para></param>
    /// <returns>The loaded authentication. <para>加载的认证实例。</para></returns>
    /// <exception cref="FileNotFoundException">The key file does not exist. <para>密钥文件不存在。</para></exception>
    /// <exception cref="InvalidOperationException">The key is not 3072 bits. <para>密钥不是 3072 位。</para></exception>
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
    /// <param name="path">The destination file path. <para>目标文件路径。</para></param>
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
    /// Gets the default private key path (UotanStudio/Hdc/hdckey).
    /// <para>获取默认私钥路径（UotanStudio/Hdc/hdckey）。</para>
    /// </summary>
    /// <returns>The default private key path. <para>默认私钥路径。</para></returns>
    public static string GetKeyPath() => DefaultKeyFilePath;

    /// <summary>
    /// Gets the default public key path (UotanStudio/Hdc/hdckey.pub).
    /// <para>获取默认公钥路径（UotanStudio/Hdc/hdckey.pub）。</para>
    /// </summary>
    /// <returns>The default public key path. <para>默认公钥路径。</para></returns>
    public static string GetPublicKeyPath() => DefaultKeyFilePath + ".pub";

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