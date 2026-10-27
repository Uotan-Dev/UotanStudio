using System.Net;
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
    /// RSA encryption mode: the whole token is RSA-encrypted with the host private key.
    /// <para>RSA 加密模式：用主机私钥加密整个令牌。</para>
    /// </summary>
    RsaEncrypt = 0,

    /// <summary>
    /// RSA signature mode, negotiated as <c>authtype 1</c>; the host signs the daemon token with its private key.
    /// <para>RSA 签名模式，按 <c>authtype 1</c> 协商；主机用私钥对守护进程令牌签名。</para>
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
/// Manages the HDC host identity: key generation, loading, saving and token signing.
/// The key directory, key size and public-key payload follow the HDC handshake, so any HDC peer
/// and this application share one host identity.
/// <para>管理 HDC 主机身份：密钥生成、加载、保存与令牌签名。密钥目录、密钥位数与公钥负载
/// 均遵循 HDC 握手约定，因此任何 HDC 对端与本应用共用同一主机身份。</para>
/// </summary>
public sealed class HdcAuthentication : IDisposable
{
    /// <summary>
    /// RSA key size in bits used by HDC.
    /// <para>HDC 使用的 RSA 密钥位数。</para>
    /// </summary>
    /// <remarks>
    /// The daemon reads a 3072-bit host identity. A 4096-bit identity is not accepted: the Base64
    /// <c>RSAPublicKey</c> record uses a 128-word modulus table that peers reading it reject outright
    /// (they demand 64 words), and the handshake exchanges the PEM public key instead.
    /// <para>守护进程读取 3072 位主机身份。4096 位身份不被接受：Base64 <c>RSAPublicKey</c> 记录使用
    /// 128 字模数表，读取该记录的守护进程只接受 64 字并直接拒绝；握手交换的是 PEM 公钥。</para>
    /// </remarks>
    private const int RSA_KEY_BITS = 3072;

    /// <summary>
    /// Separates the host label from the public key inside the handshake <c>buf</c> field, the
    /// <c>HDC_HOST_DAEMON_BUF_SEPARATOR</c> byte <c>0x0C</c>.
    /// <para>在握手 <c>buf</c> 字段内分隔主机标注与公钥的字节，即
    /// <c>HDC_HOST_DAEMON_BUF_SEPARATOR</c> 的 <c>0x0C</c>。</para>
    /// </summary>
    private const string BufferSeparator = "\f";

    /// <summary>
    /// Gets the RSA key size in bits that the HDC host identity uses, the <c>RSA_KEY_BITS</c> width.
    /// Callers that offer or validate HDC keys must use this width so every HDC peer loads the key
    /// this application generates.
    /// <para>获取 HDC 主机身份使用的 RSA 密钥位数，即 <c>RSA_KEY_BITS</c> 宽度。提供或校验 HDC 密钥的
    /// 调用方必须使用该位数，任何 HDC 对端才能加载本应用生成的密钥。</para>
    /// </summary>
    /// <value><c>RSA_KEY_BITS</c>, in bits. <para><c>RSA_KEY_BITS</c>，单位为位。</para></value>
    public const int KeyBits = RSA_KEY_BITS;

    /// <summary>
    /// Gets the RSA key sizes this client accepts when loading or publishing an existing host identity.
    /// Generation always uses <see cref="KeyBits"/>; 4096-bit identities are accepted because the handshake
    /// carries the PEM public key and verifies an RSA-PSS/SHA-512 signature, which both widths satisfy.
    /// <para>获取加载或发布既有主机身份时本客户端接受的 RSA 密钥位数。生成始终使用 <see cref="KeyBits"/>；
    /// 接受 4096 位身份是因为握手传递 PEM 公钥并校验 RSA-PSS/SHA-512 签名，两种位数都能满足。</para>
    /// </summary>
    /// <value>The accepted widths, ascending. <para>可接受的位数，升序。</para></value>
    public static IReadOnlyList<int> SupportedKeyBits { get; } = [RSA_KEY_BITS, 4096];

    private static readonly UnixFileMode KeyDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute;

    private readonly RSA _rsa;
    private readonly HdcAuthVerifyType _verifyType;

    /// <summary>
    /// Gets the default key directory under the user home: <c>.harmony</c>, created with mode <c>0750</c>.
    /// <para>获取用户主目录下的默认密钥目录 <c>.harmony</c>，以 <c>0750</c> 权限创建。</para>
    /// </summary>
    /// <value>Path <c>&lt;UserProfile&gt;/.harmony</c>; the directory may not exist yet. <para>路径 <c>&lt;UserProfile&gt;/.harmony</c>；目录可能尚未创建。</para></value>
    public static string DefaultKeyDirectory => Path.Combine(GetHomeDirectory(), ".harmony");

    /// <summary>
    /// Gets the default private key file path that the HDC handshake expects.
    /// <para>获取 HDC 握手约定的默认私钥文件路径。</para>
    /// </summary>
    /// <value><c>hdckey</c> inside <see cref="DefaultKeyDirectory"/>; the file may not exist yet. <para><see cref="DefaultKeyDirectory"/> 下的 <c>hdckey</c> 文件；文件可能尚未创建。</para></value>
    public static string DefaultKeyFilePath => Path.Combine(DefaultKeyDirectory, "hdckey");

    /// <summary>
    /// Resolves the user home directory: <c>%USERPROFILE%</c> on Windows and <c>$HOME</c>, then the
    /// passwd entry, on Linux and macOS.
    /// <para>解析用户主目录：Windows 上取 <c>%USERPROFILE%</c>，Linux 与 macOS 上取 <c>$HOME</c>，
    /// 随后退回 passwd 条目。</para>
    /// </summary>
    /// <remarks>
    /// The managed profile lookup can fail for a service account without a profile or in a stripped
    /// container, so the environment fallback is platform-correct rather than a shared
    /// <c>USERPROFILE</c>-then-<c>HOME</c> chain.
    /// <para>托管的配置文件查询在无配置文件的服务账户或精简容器里可能失败，因此环境变量兜底按平台区分，
    /// 而不是统一的先 <c>USERPROFILE</c> 后 <c>HOME</c>。</para>
    /// </remarks>
    /// <returns>The user home directory path. <para>用户主目录路径。</para></returns>
    private static string GetHomeDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
        {
            return home;
        }

        home = Environment.GetEnvironmentVariable(OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME") ?? string.Empty;
        return string.IsNullOrEmpty(home) ? "." : home;
    }

    /// <summary>
    /// Gets the host name, which is the label the daemon shows in its connection-authorization prompt.
    /// <para>获取主机名，即守护进程在连接授权提示中显示的标注。</para>
    /// </summary>
    /// <value>The local host name. <para>本机主机名。</para></value>
    internal static string HostName => Dns.GetHostName();

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
    /// <exception cref="ArgumentException">The key width is not in <see cref="SupportedKeyBits"/>. <para>密钥位数不在 <see cref="SupportedKeyBits"/> 中。</para></exception>
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
    /// <exception cref="ArgumentException">The key width is not in <see cref="SupportedKeyBits"/>. <para>密钥位数不在 <see cref="SupportedKeyBits"/> 中。</para></exception>
    public HdcAuthentication(RSA rsa, HdcAuthVerifyType verifyType)
    {
        _rsa = rsa ?? throw new ArgumentNullException(nameof(rsa));
        if (!SupportedKeyBits.Contains(rsa.KeySize))
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

    /// <summary>Loads the authentication key from <paramref name="keyPath"/> (PEM, PKCS#1 or PKCS#8), or generates and persists a new 3072-bit key when the file does not exist. The public key is written next to it with a ".pub" extension.
    /// <para>从 <paramref name="keyPath"/> 加载认证密钥（PEM，PKCS#1 或 PKCS#8）； 文件不存在时生成并持久化新的 3072 位密钥。公钥以 ".pub" 扩展名写入同名文件。</para></summary>
    /// <remarks>A generated key is written as unencrypted PKCS#8 PEM (<c>BEGIN PRIVATE KEY</c>) and its sidecar as the PEM <c>SUBJECT PUBLIC KEY</c> block, so a key directory can be shared between HDC hosts.
    /// <para>生成的密钥以未加密的 PKCS#8 PEM（<c>BEGIN PRIVATE KEY</c>）写出，同名公钥写成 PEM <c>SUBJECT PUBLIC KEY</c> 块，因此密钥目录可在 HDC 主机之间共用。</para></remarks>
    /// <param name="keyPath">The private key path; defaults to <see cref="DefaultKeyFilePath"/>.
    /// <para>私钥路径；默认为 <see cref="DefaultKeyFilePath"/>。</para></param>
    /// <returns>The loaded or newly created authentication.<para>加载或新建的认证实例。</para></returns>
    public static HdcAuthentication LoadOrCreate(string? keyPath = null)
    {
        var path = keyPath ?? DefaultKeyFilePath;

        if (File.Exists(path))
        {
            var existing = LoadPrivateKey(path);
            try { EnsurePublicKey(path, existing); return existing; }
            catch { existing.Dispose(); throw; }
        }

        var directory = EnsureKeyDirectory(path);
        var temporaryPath = Path.Combine(directory, ".hdckey-" + Guid.NewGuid().ToString("N"));
        using var created = CreateNew();
        try
        {
            WriteKeyFile(temporaryPath, created._rsa.ExportPkcs8PrivateKeyPem() + "\n");
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

    private static string EnsureKeyDirectory(string keyPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(keyPath))!;
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(directory, KeyDirectoryMode);
        }

        return directory;
    }

    private static void EnsurePublicKey(string path, HdcAuthentication authentication)
    {
        var publicPath = path + ".pub";
        if (PublicKeyFileMatches(publicPath, authentication._rsa)) return;
        var temporary = publicPath + "-" + Guid.NewGuid().ToString("N");
        try
        {
            WriteKeyFile(temporary, BuildPublicKeyFileText(authentication._rsa));
            try { File.Move(temporary, publicPath, overwrite: true); }
            catch (IOException) when (PublicKeyFileMatches(publicPath, authentication._rsa)) { }
        }
        finally { File.Delete(temporary); }
    }

    /// <summary>
    /// Reports whether the sidecar public key file exists and is the PEM public half of the loaded identity.
    /// A file holding anything other than this key's PEM public half, including a Base64
    /// <c>RSAPublicKey</c> record, does not match and is replaced.
    /// <para>判断同名公钥文件是否存在且为已加载身份的 PEM 公钥。保存的不是该密钥 PEM 公钥的文件（包括 Base64
    /// <c>RSAPublicKey</c> 记录）不匹配，会被替换。</para>
    /// </summary>
    /// <param name="publicPath">The sidecar public key path. <para>同名公钥路径。</para></param>
    /// <param name="rsa">The loaded private identity. <para>已加载的私钥身份。</para></param>
    /// <returns><see langword="true"/> when the file already holds this key's PEM public half.
    /// <para>文件已保存该密钥的 PEM 公钥时返回 <see langword="true"/>。</para></returns>
    private static bool PublicKeyFileMatches(string publicPath, RSA rsa)
    {
        if (!File.Exists(publicPath)) return false;
        try
        {
            using var publicKey = RSA.Create();
            publicKey.ImportFromPem(File.ReadAllText(publicPath));
            return publicKey.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(rsa.ExportSubjectPublicKeyInfo());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or CryptographicException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Builds the ".pub" file text: the PEM <c>SubjectPublicKeyInfo</c> block with a single trailing
    /// newline. The daemon stores the handshake public key verbatim in its known-hosts record and
    /// compares it byte for byte on the next connection.
    /// <para>构建 ".pub" 文件文本：带单个结尾换行的 PEM <c>SubjectPublicKeyInfo</c> 块。
    /// 守护进程把握手公钥原样存入已知主机记录，并在下次连接时逐字节比对。</para>
    /// </summary>
    /// <param name="rsa">The key whose public half is written. <para>写出其公钥部分的密钥。</para></param>
    /// <returns>The file text, ending with the PEM closing line and its newline.
    /// <para>文件文本，以 PEM 结束行及其换行结尾。</para></returns>
    public static string BuildPublicKeyFileText(RSA rsa) => rsa.ExportSubjectPublicKeyInfoPem() + "\n";

    /// <summary>
    /// Signs the token using the configured verification algorithm.
    /// When <see cref="_verifyType"/> is <see cref="HdcAuthVerifyType.Rsa3072Sha512"/>, the token is signed
    /// with the private key; when it is <see cref="HdcAuthVerifyType.RsaEncrypt"/>, the whole token is
    /// RSA-encrypted with the private key.
    /// <para>使用配置的验证算法对令牌签名。当 <see cref="_verifyType"/> 为
    /// <see cref="HdcAuthVerifyType.Rsa3072Sha512"/> 时用私钥对令牌签名；
    /// 当 <see cref="_verifyType"/> 为 <see cref="HdcAuthVerifyType.RsaEncrypt"/> 时用私钥加密整个令牌。</para>
    /// </summary>
    /// <param name="token">The token to sign. <para>待签名的令牌。</para></param>
    /// <returns>The signed payload. <para>签名后的负载。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="token"/> is empty. <para>令牌为空。</para></exception>
    public byte[] SignToken(byte[] token)
        => SignToken(token, _verifyType);

    /// <summary>Signs a daemon challenge with the negotiated RSA algorithm.
    /// <para>使用协商的 RSA 算法对守护进程挑战签名。</para></summary>
    /// <param name="token">Challenge bytes from the daemon handshake; must not be empty.
    /// <para>来自守护进程握手的挑战字节；不得为空。</para></param>
    /// <param name="verifyType">Negotiated algorithm; only <see cref="HdcAuthVerifyType.Rsa3072Sha512"/> and <see cref="HdcAuthVerifyType.RsaEncrypt"/> are implemented.
    /// <para>协商算法；仅实现了 <see cref="HdcAuthVerifyType.Rsa3072Sha512"/> 与 <see cref="HdcAuthVerifyType.RsaEncrypt"/>。</para></param>
    /// <returns>For the RSA signature type, the signature over the token; for the RSA encryption type, the RSA private-key encryption of the whole token. Both are modulus-width: 384 bytes for the 3072-bit identity and 512 bytes for an accepted 4096-bit identity.
    /// <para>RSA 签名类型返回对令牌的签名；RSA 加密类型返回整个令牌的 RSA 私钥加密结果。两者均为模数宽度：3072 位身份为 384 字节，可接受的 4096 位身份为 512 字节。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="token"/> is <see langword="null"/> or empty.
    /// <para><paramref name="token"/> 为 <see langword="null"/> 或空。</para></exception>
    /// <exception cref="NotSupportedException"><paramref name="verifyType"/> selects an unimplemented algorithm.
    /// <para><paramref name="verifyType"/> 选择了未实现的算法。</para></exception>
    /// <exception cref="CryptographicException">The RSA encryption type requires a private key and a token that fits the type-1 block.
    /// <para>RSA 加密类型要求存在私钥，且令牌能装入 type-1 块。</para></exception>
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
            return RsaTypeOneSignature.Sign(_rsa, token);
        }
        throw new NotSupportedException($"HDC authentication algorithm {verifyType} is not implemented.");
    }

    /// <summary>Builds the <c>buf</c> payload of the HDC handshake public-key message: the local host name, the <c>\f</c> separator (<c>HDC_HOST_DAEMON_BUF_SEPARATOR</c>), and the PEM <c>SubjectPublicKeyInfo</c> block with its single trailing newline. The daemon splits on the first <c>\f</c>, shows the prefix as the requesting host in its authorization prompt, and keeps the remainder as the known-hosts record, so neither the label nor a stray newline may be invented here.
    /// <para>构建 HDC 握手公钥消息的 <c>buf</c> 负载：本机主机名、<c>\f</c> 分隔符（<c>HDC_HOST_DAEMON_BUF_SEPARATOR</c>）、以及带单个结尾换行的 PEM <c>SubjectPublicKeyInfo</c> 块。守护进程按第一个 <c>\f</c> 切分，把前缀作为请求主机显示在授权提示中，并把余下部分作为已知主机记录保存，因此这里的标注与换行都不能自行编造。</para></summary>
    /// <returns>The UTF-8 encoded handshake payload.<para>UTF-8 编码的握手负载。</para></returns>
    public byte[] BuildDaemonPublicKeyPayload()
        => Encoding.UTF8.GetBytes(HostName + BufferSeparator + BuildPublicKeyFileText(_rsa));

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
    /// <exception cref="InvalidOperationException">The key width is not in <see cref="SupportedKeyBits"/>.
    /// <para>密钥位数不在 <see cref="SupportedKeyBits"/> 中。</para></exception>
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

        if (!SupportedKeyBits.Contains(rsa.KeySize))
        {
            var keySize = rsa.KeySize;
            rsa.Dispose();
            throw new InvalidOperationException(
                $"The HDC key at '{path}' is {keySize} bits; {RSA_KEY_BITS} bits are required.");
        }

        return new HdcAuthentication(rsa);
    }

    /// <summary>
    /// Saves the private key to the specified path as unencrypted PKCS#8 PEM, with restrictive file
    /// permissions.
    /// <para>以受限文件权限将私钥按未加密的 PKCS#8 PEM 保存到指定路径。</para>
    /// </summary>
    /// <param name="path">The destination file path. <para>目标文件路径。</para></param>
    public void SavePrivateKey(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        WriteKeyFile(path, _rsa.ExportPkcs8PrivateKeyPem() + "\n");
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