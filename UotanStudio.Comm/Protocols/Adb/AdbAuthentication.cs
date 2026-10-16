using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using UotanStudio.Comm.Text;
using UotanStudio.Hash.Digests;

namespace UotanStudio.Comm.Protocols.Adb;

/// <summary>
/// Converts between managed RSA keys and the ADB wire format, and performs
/// ADB RSA-SHA1 token signing.
/// <para>在托管 RSA 密钥与 ADB 线上格式之间转换，并执行 ADB RSA-SHA1 令牌签名。</para>
/// <para>The host identity is exactly the RSA-2048 key pair that upstream adb keeps at
/// <c>&lt;home&gt;/.android/adbkey</c> plus its <c>.pub</c> sidecar
/// (<c>client/auth.cpp</c> and <c>adb_utils.cpp</c> in
/// <c>platform/packages/modules/adb</c>): the private half is PKCS#8 PEM written by
/// <c>PEM_write_PrivateKey</c>, the sidecar is the base64 <c>RSAPublicKey</c> record followed by a
/// space and the <c>loginname@hostname</c> label with no trailing newline, and the extra identities
/// a server may offer come from <c>ADB_VENDOR_KEYS</c>. Nothing else is persisted as a host
/// identity.</para>
/// <para>主机身份就是上游 adb 保存在 <c>&lt;主目录&gt;/.android/adbkey</c> 及其 <c>.pub</c>
/// 同名文件中的 RSA-2048 密钥对（来自 <c>platform/packages/modules/adb</c> 的
/// <c>client/auth.cpp</c> 与 <c>adb_utils.cpp</c>）：私钥是 <c>PEM_write_PrivateKey</c> 写出的
/// PKCS#8 PEM，同名公钥文件是 base64 的 <c>RSAPublicKey</c> 记录加一个空格再加
/// <c>loginname@hostname</c> 标注且结尾无换行，服务器可额外提供的身份来自
/// <c>ADB_VENDOR_KEYS</c>。除这些之外不存在被持久化的主机身份。</para>
/// </summary>
public sealed class AdbAuthentication : IDisposable
{
    private const int KeyLengthInDwords = 64;

    /// <summary>
    /// Gets the size, in bytes, of the AUTH token adbd issues, matching upstream <c>TOKEN_SIZE</c>.
    /// <para>获取 adbd 下发的 AUTH 令牌字节数，与上游 <c>TOKEN_SIZE</c> 一致。</para>
    /// </summary>
    /// <value>20 bytes, the SHA-1 sized challenge. <para>20 字节，即 SHA-1 宽度的挑战值。</para></value>
    public const int TokenSize = 20;

    /// <summary>
    /// Gets the environment variable upstream adb reads for extra host identities, holding
    /// key files or directories separated by <c>;</c> on Windows and <c>:</c> elsewhere.
    /// <para>获取上游 adb 读取额外主机身份的环境变量名，其中密钥文件或目录在 Windows 上以
    /// <c>;</c> 分隔、在其他平台上以 <c>:</c> 分隔。</para>
    /// </summary>
    /// <value>The name <c>ADB_VENDOR_KEYS</c>. <para>名称 <c>ADB_VENDOR_KEYS</c>。</para></value>
    public const string VendorKeysEnvironmentVariable = "ADB_VENDOR_KEYS";

    private static readonly UnixFileMode KeyDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute;

    private readonly RSA _rsa;

    /// <summary>
    /// Gets the default key directory under the user home directory, created with the upstream
    /// <c>0750</c> mode on Unix platforms (<c>adb_get_android_dir_path</c>).
    /// <para>获取用户主目录下的默认密钥目录，在 Unix 平台上按上游
    /// <c>adb_get_android_dir_path</c> 的 <c>0750</c> 权限创建。</para>
    /// </summary>
    /// <value>The composed directory path; the directory itself may not exist yet.
    /// <para>组合得到的目录路径；该目录本身可能尚不存在。</para></value>
    public static string DefaultKeyDirectory => Path.Combine(GetHomeDirectory(), ".android");

    /// <summary>
    /// Gets the default private key file path inside the default key directory.
    /// <para>获取默认密钥目录中的默认私钥文件路径。</para>
    /// </summary>
    /// <value>The path of the private key file named <c>adbkey</c>; the file may not exist yet.
    /// <para>名为 <c>adbkey</c> 的私钥文件路径；该文件可能尚不存在。</para></value>
    public static string DefaultKeyFilePath => Path.Combine(DefaultKeyDirectory, "adbkey");

    /// <summary>
    /// Initializes authentication with the supplied 2048-bit RSA key.
    /// <para>使用提供的 2048 位 RSA 密钥初始化认证。</para>
    /// </summary>
    /// <param name="rsa">The RSA key to use. <para>待使用的 RSA 密钥。</para></param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="rsa"/> is <c>null</c>.
    /// <para><paramref name="rsa"/> 为 <c>null</c>。</para>
    /// </exception>
    /// <exception cref="ArgumentException">The key is not 2048 bits. <para>密钥不是 2048 位。</para></exception>
    public AdbAuthentication(RSA rsa)
    {
        _rsa = rsa ?? throw new ArgumentNullException(nameof(rsa));
        if (rsa.KeySize != 2048)
        {
            throw new ArgumentException("ADB authentication requires a 2048-bit RSA key.", nameof(rsa));
        }
    }

    /// <summary>
    /// Creates authentication with a newly generated 2048-bit RSA key.
    /// <para>使用新生成的 2048 位 RSA 密钥创建认证。</para>
    /// </summary>
    /// <returns>A new <see cref="AdbAuthentication"/> instance. <para>新的 <see cref="AdbAuthentication"/> 实例。</para></returns>
    public static AdbAuthentication CreateNew()
    {
        var rsa = RSA.Create();
        rsa.KeySize = 2048;
        return new AdbAuthentication(rsa);
    }

    /// <summary>Gets the underlying RSA key. <para>获取底层 RSA 密钥。</para></summary>
    /// <value>The RSA key backing this authentication. <para>支撑本认证的 RSA 密钥。</para></value>
    internal RSA RsaKey => _rsa;

    /// <summary>
    /// Gets the private key file this identity was loaded from, or <see langword="null"/> for an
    /// identity built from an in-memory key. Upstream distinguishes the same way: the key at
    /// <c>get_user_key_path()</c> is the user identity whose public half is announced after the chain is
    /// exhausted, while the rest of the chain only signs challenges.
    /// <para>获取该身份加载来源的私钥文件路径；由内存密钥构建的身份为
    /// <see langword="null"/>。上游同样区分：来自 <c>get_user_key_path()</c> 的密钥是用户身份，身份链
    /// 耗尽后通告其公钥半部，其余身份只负责签名挑战。</para>
    /// </summary>
    /// <value>The originating key file path, or <see langword="null"/>.
    /// <para>来源密钥文件路径，或 <see langword="null"/>。</para></value>
    public string? KeyFilePath { get; private set; }

    /// <summary>
    /// Loads the authentication key from <paramref name="keyPath"/>, or generates
    /// and persists a new 2048-bit key when the file is absent. A generated key is written as
    /// unencrypted PKCS#8 PEM (<c>BEGIN PRIVATE KEY</c>), the format upstream <c>generate_key()</c>
    /// writes with <c>PEM_write_PrivateKey</c>; a stored key is accepted in either PKCS#1 or PKCS#8.
    /// <para>从 <paramref name="keyPath"/> 加载认证密钥；文件不存在时生成并持久化新的 2048 位密钥。
    /// 生成的密钥以未加密的 PKCS#8 PEM（<c>BEGIN PRIVATE KEY</c>）写出，即上游 <c>generate_key()</c>
    /// 用 <c>PEM_write_PrivateKey</c> 写出的格式；已存在的密钥接受 PKCS#1 或 PKCS#8。</para>
    /// </summary>
    /// <param name="keyPath">
    /// The private key path; defaults to <see cref="DefaultKeyFilePath"/>.
    /// <para>私钥路径；默认为 <see cref="DefaultKeyFilePath"/>。</para>
    /// </param>
    /// <returns>The loaded or newly created authentication. <para>加载或新建的认证实例。</para></returns>
    public static AdbAuthentication LoadOrCreate(string? keyPath = null)
    {
        var path = keyPath ?? DefaultKeyFilePath;

        if (File.Exists(path))
        {
            var rsa = RSA.Create();
            try
            {
                rsa.ImportFromPem(File.ReadAllText(path));
                var loaded = new AdbAuthentication(rsa);
                loaded.KeyFilePath = path;
                EnsurePublicKey(path, loaded);
                return loaded;
            }
            catch { rsa.Dispose(); throw; }
        }

        var directory = EnsureKeyDirectory(path);
        var temporary = Path.Combine(directory, ".adbkey-" + Guid.NewGuid().ToString("N"));
        using var created = CreateNew();
        try
        {
            WriteKeyFile(temporary, created._rsa.ExportPkcs8PrivateKeyPem() + "\n");
            try { File.Move(temporary, path, overwrite: false); }
            catch (IOException) when (File.Exists(path)) { }
            return LoadOrCreate(path);
        }
        finally { File.Delete(temporary); }
    }

    /// <summary>
    /// Loads an existing identity from <paramref name="keyPath"/> without creating or repairing anything on
    /// disk. Upstream keeps this split too: <c>load_key()</c> reads an <c>ADB_VENDOR_KEYS</c> entry and a
    /// file it cannot parse is simply skipped, while only <c>load_userkey()</c> generates a key, and only at
    /// the user key path.
    /// <para>从 <paramref name="keyPath"/> 加载已存在的身份，不创建也不修复磁盘上的任何内容。上游同样区分：
    /// <c>load_key()</c> 读取 <c>ADB_VENDOR_KEYS</c> 条目，无法解析的文件直接跳过；只有
    /// <c>load_userkey()</c> 会生成密钥，且只在用户密钥路径上。</para>
    /// </summary>
    /// <param name="keyPath">The private key path to read. <para>待读取的私钥路径。</para></param>
    /// <returns>The loaded identity. <para>加载的身份。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="keyPath"/> is empty.
    /// <para><paramref name="keyPath"/> 为空。</para></exception>
    /// <exception cref="FileNotFoundException">No key file exists at <paramref name="keyPath"/>.
    /// <para><paramref name="keyPath"/> 处不存在密钥文件。</para></exception>
    /// <exception cref="CryptographicException">The stored key is not a usable 2048-bit RSA key.
    /// <para>已存密钥不是可用的 2048 位 RSA 密钥。</para></exception>
    public static AdbAuthentication Load(string keyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPath);
        if (!File.Exists(keyPath))
        {
            throw new FileNotFoundException($"ADB identity key file not found: '{keyPath}'.", keyPath);
        }

        var rsa = RSA.Create();
        try
        {
            rsa.ImportFromPem(File.ReadAllText(keyPath));
            var loaded = new AdbAuthentication(rsa);
            loaded.KeyFilePath = keyPath;
            return loaded;
        }
        catch
        {
            rsa.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Loads the host identity chain an ADB server offers to a device: the user identity from
    /// <paramref name="keyPath"/> first, then every identity named by
    /// <see cref="VendorKeysEnvironmentVariable"/>, ordered by the ascending SHA-256 fingerprint of
    /// its public key and deduplicated by that fingerprint. Unreadable or wrong-width entries are
    /// skipped, as upstream adb skips a key file it cannot parse.
    /// <para>加载 ADB 服务器向设备提供的主机身份链：先是 <paramref name="keyPath"/> 中的用户身份，
    /// 再是 <see cref="VendorKeysEnvironmentVariable"/> 指名的所有身份，其余身份按其公钥
    /// SHA-256 指纹升序排列并按该指纹去重。无法读取或位数不匹配的条目会被跳过，正如上游 adb
    /// 跳过无法解析的密钥文件。</para>
    /// </summary>
    /// <remarks>
    /// Upstream keeps every private key in one fingerprint-keyed map (<c>g_keys</c> in
    /// <c>client/auth.cpp</c>), so its attempt order is the fingerprint order; the order has no
    /// protocol meaning because adbd accepts any authorized key. The user identity is hoisted to the
    /// front here so the default key is offered first, and the public-key payload announced after the
    /// chain is exhausted always comes from that user identity, matching
    /// <c>adb_auth_get_userkey()</c>.
    /// <para>上游把所有私钥存放在以指纹为键的同一个映射表（<c>client/auth.cpp</c> 的 <c>g_keys</c>）里，
    /// 因此其尝试顺序就是指纹顺序；该顺序没有协议含义，因为 adbd 接受任何已授权的密钥。此处把用户身份
    /// 提到最前以保证默认密钥优先被提供，且身份链耗尽后通告的公钥负载始终来自该用户身份，与
    /// <c>adb_auth_get_userkey()</c> 一致。</para>
    /// </remarks>
    /// <param name="keyPath">
    /// The user private key path; defaults to <see cref="DefaultKeyFilePath"/>.
    /// <para>用户私钥路径；默认为 <see cref="DefaultKeyFilePath"/>。</para>
    /// </param>
    /// <returns>The ordered identities, containing at least the user identity. The caller disposes them.
    /// <para>有序身份列表，至少包含用户身份；由调用方释放。</para></returns>
    /// <exception cref="IOException">The user key file exists but cannot be parsed.
    /// <para>用户密钥文件存在但无法解析。</para></exception>
    public static IReadOnlyList<AdbAuthentication> LoadIdentityChain(string? keyPath = null)
    {
        var userIdentity = LoadOrCreate(keyPath);
        var chain = new List<AdbAuthentication> { userIdentity };
        var seen = new HashSet<string>([Convert.ToHexString(userIdentity.PublicKeyFingerprint)]);
        foreach (var vendor in EnumerateVendorIdentities(seen))
        {
            chain.Add(vendor);
        }

        return chain;
    }

    private static IEnumerable<AdbAuthentication> EnumerateVendorIdentities(HashSet<string> seenFingerprints)
    {
        var raw = Environment.GetEnvironmentVariable(VendorKeysEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        var separator = OperatingSystem.IsWindows() ? ';' : ':';
        var identities = new List<AdbAuthentication>();
        foreach (var entry in raw.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var file in EnumerateVendorKeyFiles(entry))
            {
                AdbAuthentication? identity = null;
                try
                {
                    identity = Load(file);
                    if (!seenFingerprints.Add(Convert.ToHexString(identity.PublicKeyFingerprint)))
                    {
                        identity.Dispose();
                        identity = null;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException
                        or ArgumentException or CryptographicException)
                {
                    identity?.Dispose();
                    identity = null;
                }

                if (identity is not null)
                {
                    identities.Add(identity);
                }
            }
        }

        identities.Sort(static (left, right) =>
            left.PublicKeyFingerprint.AsSpan().SequenceCompareTo(right.PublicKeyFingerprint));
        return identities;
    }

    private static IEnumerable<string> EnumerateVendorKeyFiles(string entry)
    {
        if (Directory.Exists(entry))
        {
            return Directory.EnumerateFiles(entry)
                .Where(static path => Path.GetFileName(path).EndsWith(".adb_key", StringComparison.Ordinal))
                .ToArray();
        }

        return File.Exists(entry) ? [entry] : [];
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

    private static void EnsurePublicKey(string path, AdbAuthentication authentication)
    {
        if (File.Exists(path + ".pub")) return;
        var temporary = path + ".pub-" + Guid.NewGuid().ToString("N");
        try
        {
            WriteKeyFile(temporary, Encoding.UTF8.GetStringTrimNul(authentication.BuildPublicKeyPayload()));
            try { File.Move(temporary, path + ".pub", overwrite: false); }
            catch (IOException) when (File.Exists(path + ".pub")) { }
        }
        finally { File.Delete(temporary); }
    }

    /// <summary>
    /// Gets the SHA-256 fingerprint of the DER SubjectPublicKeyInfo encoding of this identity, the
    /// value upstream adb calls a key fingerprint (<c>hash_key</c> in <c>client/auth.cpp</c>) and the
    /// identity a device stores when the user accepts the connection prompt.
    /// <para>获取该身份 DER SubjectPublicKeyInfo 编码的 SHA-256 指纹，即上游 adb
    /// （<c>client/auth.cpp</c> 的 <c>hash_key</c>）所称的密钥指纹，也是用户接受连接授权时设备保存的身份。</para>
    /// </summary>
    /// <value>The 32-byte digest. <para>32 字节摘要。</para></value>
    public byte[] PublicKeyFingerprint => Sha256.ComputeHash(_rsa.ExportSubjectPublicKeyInfo());

    /// <summary>
    /// Signs the token with RSA-PKCS1 + SHA1 (ADB signature), the same construction as upstream
    /// <c>RSA_sign(NID_sha1, token, TOKEN_SIZE, ...)</c> in <c>adb_auth_sign</c>.
    /// <para>用私钥以 RSA-PKCS1 + SHA1 对令牌签名，与上游 <c>adb_auth_sign</c> 中
    /// <c>RSA_sign(NID_sha1, token, TOKEN_SIZE, ...)</c> 的构造一致。</para>
    /// </summary>
    /// <param name="token">The token to sign; must be exactly <see cref="TokenSize"/> bytes.
    /// <para>待签名的令牌；必须恰为 <see cref="TokenSize"/> 字节。</para></param>
    /// <returns>The RSA-SHA1 signature. <para>RSA-SHA1 签名。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="token"/> is null or not
    /// <see cref="TokenSize"/> bytes, which means the peer is not issuing adbd AUTH challenges.
    /// <para><paramref name="token"/> 为 <see langword="null"/> 或长度不是
    /// <see cref="TokenSize"/> 字节，说明对端并未下发 adbd 的 AUTH 挑战。</para></exception>
    public byte[] SignToken(byte[] token)
    {
        ArgumentNullException.ThrowIfNull(token);
        if (token.Length != TokenSize)
        {
            throw new ArgumentException(
                $"ADB AUTH tokens are {TokenSize} bytes; the peer sent {token.Length}.", nameof(token));
        }

        var hash = Sha1.ComputeHash(token);
        return _rsa.SignHash(hash, HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1);
    }

    /// <summary>
    /// Builds the ADB public key payload: base64(RSAPublicKey) + " user@host\0".
    /// <para>构建 ADB 公钥负载：base64(RSAPublicKey) + " user@host\0"。</para>
    /// </summary>
    /// <param name="comment">
    /// Optional comment appended after the key; defaults to "user@host".
    /// <para>追加在密钥后的可选注释；默认为 "user@host"。</para>
    /// </param>
    /// <returns>The public key payload bytes. <para>公钥负载字节。</para></returns>
    public byte[] BuildPublicKeyPayload(string? comment = null) => BuildPublicKeyPayload(_rsa, comment);

    /// <summary>
    /// Builds the ADB public key payload for an arbitrary RSA key:
    /// base64(RSAPublicKey) + " user@host\0".
    /// <para>为任意 RSA 密钥构建 ADB 公钥负载：base64(RSAPublicKey) + " user@host\0"。</para>
    /// </summary>
    /// <param name="rsa">The RSA key to encode. <para>待编码的 RSA 密钥。</para></param>
    /// <param name="comment">
    /// Optional comment appended after the key; defaults to "user@host".
    /// <para>追加在密钥后的可选注释；默认为 "user@host"。</para>
    /// </param>
    /// <returns>The public key payload bytes. <para>公钥负载字节。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="rsa"/> is <c>null</c>.
    /// <para><paramref name="rsa"/> 为 <c>null</c>。</para></exception>
    public static byte[] BuildPublicKeyPayload(RSA rsa, string? comment = null)
    {
        ArgumentNullException.ThrowIfNull(rsa);
        var publicKey = rsa.ExportParameters(false);
        (var n0inv, var n, var rr, var exponent) = ConvertRsaToAdb(publicKey);
        var adbKey = AdbRsaToBuffer(n0inv, n, rr, exponent);

        var userComment = comment ?? string.Empty;
        if (string.IsNullOrEmpty(userComment))
        {
            userComment = GetIdentityComment();
        }

        var payload = $"{Convert.ToBase64String(adbKey)} {userComment}\0";
        return Encoding.UTF8.GetBytes(payload);
    }

    /// <summary>
    /// Serializes an ADB RSAPublicKey structure to its wire buffer:
    /// len(4) + n0inv(4) + n(4*len) + rr(4*len) + exponent(4).
    /// <para>将 ADB RSAPublicKey 结构序列化为线上缓冲区：
    /// len(4) + n0inv(4) + n(4*len) + rr(4*len) + exponent(4)。</para>
    /// </summary>
    /// <param name="n0inv">The -1/n[0] modular inverse term of the ADB key structure. <para>ADB 密钥结构中的 -1/n[0] 逆元项。</para></param>
    /// <param name="n">The modulus split into 32-bit words. <para>按 32 位字拆分的模数。</para></param>
    /// <param name="rr">The r^2 mod n split into 32-bit words. <para>按 32 位字拆分的 r^2 mod n。</para></param>
    /// <param name="exponent">The public exponent. <para>公钥指数。</para></param>
    /// <returns>The serialized RSAPublicKey buffer. <para>序列化后的 RSAPublicKey 缓冲区。</para></returns>
    public static byte[] AdbRsaToBuffer(uint n0inv, uint[] n, uint[] rr, int exponent)
    {
        var len = n.Length;

        var buffer = new byte[12 + (8 * len)];
        using MemoryStream ms = new(buffer);
        using BinaryWriter bw = new(ms);

        bw.Write(len);
        bw.Write(n0inv);

        foreach (var element in n)
        {
            bw.Write(element);
        }

        foreach (var element in rr)
        {
            bw.Write(element);
        }

        bw.Write(exponent);
        return buffer;
    }

    /// <summary>
    /// Converts .NET RSA parameters into ADB RSAPublicKey fields (n0inv, n, rr, exponent).
    /// <para>将 .NET RSA 参数转换为 ADB RSAPublicKey 字段（n0inv、n、rr、exponent）。</para>
    /// </summary>
    /// <param name="parameters">The RSA parameters to convert. <para>待转换的 RSA 参数。</para></param>
    /// <returns>The ADB key fields. <para>ADB 密钥字段。</para></returns>
    public static (uint n0inv, uint[] n, uint[] rr, int exponent) ConvertRsaToAdb(RSAParameters parameters)
        => ConvertRsaToAdb(parameters, KeyLengthInDwords);

    /// <summary>
    /// Converts .NET RSA parameters into ADB RSAPublicKey fields with an explicit word count, so a
    /// 4096-bit identity can be encoded as the 128 words upstream HDC expects.
    /// <para>以显式字长把 .NET RSA 参数转换为 ADB RSAPublicKey 字段，使 4096 位身份能按上游 HDC
    /// 期望的 128 字编码。</para>
    /// </summary>
    /// <param name="parameters">The RSA parameters to convert. <para>待转换的 RSA 参数。</para></param>
    /// <param name="keyLengthInDwords">Word count of the modulus table. <para>模数表的字数。</para></param>
    /// <returns>The ADB key fields. <para>ADB 密钥字段。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="keyLengthInDwords"/> is not positive.
    /// <para><paramref name="keyLengthInDwords"/> 不为正数。</para></exception>
    public static (uint n0inv, uint[] n, uint[] rr, int exponent) ConvertRsaToAdb(RSAParameters parameters, int keyLengthInDwords)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(keyLengthInDwords);
        var modulus = parameters.Modulus ?? throw new ArgumentException("Modulus is missing.", nameof(parameters));
        var exponentBytes = parameters.Exponent ?? throw new ArgumentException("Exponent is missing.", nameof(parameters));

        var e = (int)FromBigEndianUnsigned(exponentBytes);

        var r32 = BigInteger.One << 32;
        var n = FromBigEndianUnsigned(modulus);
        var r = BigInteger.One << (keyLengthInDwords * 32);
        var rr = BigInteger.ModPow(r, 2, n);

        var remainder = BigInteger.Remainder(n, r32);
        var tn0inv = ModInverse(remainder, r32);
        var n0inv = (uint)(BigInteger.Negate(tn0inv) & uint.MaxValue);

        var nTable = new uint[keyLengthInDwords];
        var rrTable = new uint[keyLengthInDwords];

        for (var i = 0; i < keyLengthInDwords; i++)
        {
            rr = BigInteger.DivRem(rr, r32, out remainder);
            rrTable[i] = (uint)remainder;

            n = BigInteger.DivRem(n, r32, out remainder);
            nTable[i] = (uint)(remainder & uint.MaxValue);
        }

        return (n0inv, nTable, rrTable, e);
    }

    /// <summary>
    /// Writes key text to <paramref name="path"/> with restrictive permissions
    /// fixed at the moment of creation.
    /// <para>将密钥文本写入 <paramref name="path"/>，受限权限在创建之时即已固定。</para>
    /// </summary>
    /// <param name="path">Destination file path. <para>目标文件路径。</para></param>
    /// <param name="contents">The text contents to write. <para>待写入的文本内容。</para></param>
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
    /// <param name="path">Destination file path. <para>目标文件路径。</para></param>
    /// <param name="bytes">The key bytes to write. <para>待写入的密钥字节。</para></param>
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
    /// Resolves the user home directory the way upstream <c>adb_get_homedir_path()</c> does: the
    /// Windows profile folder (<c>SHGetFolderPathW(CSIDL_PROFILE)</c>) or, on Linux and macOS,
    /// <c>$HOME</c> with the passwd entry as fallback.
    /// <para>按上游 <c>adb_get_homedir_path()</c> 的方式解析用户主目录：Windows 上取配置文件目录
    /// （<c>SHGetFolderPathW(CSIDL_PROFILE)</c>），Linux 与 macOS 上取 <c>$HOME</c> 并以 passwd 条目兜底。</para>
    /// </summary>
    /// <remarks>
    /// The managed profile lookup can fail for a service account without a profile or in a stripped
    /// container; upstream then reads the environment, so the fallback is platform-correct:
    /// <c>USERPROFILE</c> is the Windows profile variable and <c>HOME</c> the POSIX one.
    /// <para>托管的配置文件查询在无配置文件的服务账户或精简容器里可能失败；上游随后读取环境变量，因此兜底顺序
    /// 按平台区分：<c>USERPROFILE</c> 是 Windows 的配置文件变量，<c>HOME</c> 是 POSIX 的。</para>
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
    /// Builds the <c>loginname@hostname</c> label upstream adb appends to the public key, reading the
    /// same environment first: <c>USERNAME</c> and <c>COMPUTERNAME</c> on Windows, <c>LOGNAME</c> and
    /// <c>HOSTNAME</c> on Linux and macOS, then the OS identity calls.
    /// <para>构建上游 adb 追加到公钥后的 <c>loginname@hostname</c> 标注，并按相同顺序先读环境变量：
    /// Windows 上用 <c>USERNAME</c> 与 <c>COMPUTERNAME</c>，Linux 与 macOS 上用 <c>LOGNAME</c> 与
    /// <c>HOSTNAME</c>，随后退回操作系统身份调用。</para>
    /// </summary>
    /// <remarks>
    /// On POSIX upstream falls back to <c>getlogin()</c>, which reports the session leader recorded in
    /// utmp; the managed runtime instead reports the account name of the current uid
    /// (<c>getpwuid_r(getuid())-&gt;pw_name</c>). The label is informational: adbd compares only the base64
    /// key material before the first space, never the label.
    /// <para>在 POSIX 上上游回退到 <c>getlogin()</c>，该调用返回 utmp 中记录的会话用户；托管运行时则报告当前
    /// uid 的账户名（<c>getpwuid_r(getuid())-&gt;pw_name</c>）。该标注仅供参考：adbd 只比对第一个空格之前的
    /// base64 密钥材料，从不比对标注。</para>
    /// </remarks>
    /// <returns>The label appended after the base64 public key. <para>追加在 base64 公钥之后的标注。</para></returns>
    private static string GetIdentityComment()
    {
        var windows = OperatingSystem.IsWindows();
        var user = Environment.GetEnvironmentVariable(windows ? "USERNAME" : "LOGNAME");
        var host = Environment.GetEnvironmentVariable(windows ? "COMPUTERNAME" : "HOSTNAME");
        return $"{(string.IsNullOrEmpty(user) ? Environment.UserName : user)}@"
            + $"{(string.IsNullOrEmpty(host) ? Environment.MachineName : host)}";
    }

    /// <summary>
    /// Interprets a big-endian unsigned byte array as a positive <see cref="BigInteger"/>.
    /// <para>将大端无符号字节数组解释为正的 <see cref="BigInteger"/>。</para>
    /// </summary>
    /// <param name="bytes">The big-endian bytes. <para>大端字节。</para></param>
    /// <returns>The parsed value. <para>解析后的值。</para></returns>
    private static BigInteger FromBigEndianUnsigned(byte[] bytes)
    {
        var littleEndian = new byte[bytes.Length];
        for (var i = 0; i < bytes.Length; i++)
        {
            littleEndian[i] = bytes[bytes.Length - 1 - i];
        }

        if ((littleEndian[^1] & 0x80) != 0)
        {
            Array.Resize(ref littleEndian, littleEndian.Length + 1);
            littleEndian[^1] = 0;
        }

        return new BigInteger(littleEndian);
    }

    /// <summary>
    /// Computes the modular inverse via the extended Euclidean algorithm.
    /// <para>通过扩展欧几里得算法计算模逆元。</para>
    /// </summary>
    /// <param name="value">The value to invert. <para>待求逆的值。</para></param>
    /// <param name="modulo">The modulus. <para>模数。</para></param>
    /// <returns>The modular inverse. <para>模逆元。</para></returns>
    private static BigInteger ModInverse(BigInteger value, BigInteger modulo)
    {
        var egcd = ExtendedGcd(value, modulo, out var x, out _);
        if (egcd != BigInteger.One)
        {
            throw new InvalidOperationException("Invalid modulus for ADB key conversion.");
        }

        if (x < 0)
        {
            x += modulo;
        }

        return x % modulo;
    }

    /// <summary>
    /// Computes the extended GCD of two values, exposing the Bezout coefficients.
    /// <para>计算两个值的扩展最大公约数，并给出裴蜀系数。</para>
    /// </summary>
    /// <param name="left">The left operand. <para>左操作数。</para></param>
    /// <param name="right">The right operand. <para>右操作数。</para></param>
    /// <param name="leftFactor">The Bezout coefficient of <paramref name="left"/>. <para><paramref name="left"/> 的裴蜀系数。</para></param>
    /// <param name="rightFactor">The Bezout coefficient of <paramref name="right"/>. <para><paramref name="right"/> 的裴蜀系数。</para></param>
    /// <returns>The greatest common divisor. <para>最大公约数。</para></returns>
    private static BigInteger ExtendedGcd(BigInteger left, BigInteger right, out BigInteger leftFactor, out BigInteger rightFactor)
    {
        leftFactor = 0;
        rightFactor = 1;
        BigInteger u = 1;
        BigInteger v = 0;
        BigInteger gcd = 0;

        while (left != 0)
        {
            var q = right / left;
            var r = right % left;

            var m = leftFactor - (u * q);
            var n = rightFactor - (v * q);

            right = left;
            left = r;
            leftFactor = u;
            rightFactor = v;
            u = m;
            v = n;

            gcd = right;
        }

        return gcd;
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