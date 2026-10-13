using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using UotanStudio.Comm.Text;

namespace UotanStudio.Comm.Protocols.Adb;

/// <summary>
/// Converts between managed RSA keys and the ADB wire format, and performs
/// ADB RSA-SHA1 token signing.
/// <para>在托管 RSA 密钥与 ADB 线上格式之间转换，并执行 ADB RSA-SHA1 令牌签名。</para>
/// </summary>
public sealed class AdbAuthentication : IDisposable
{
    private const int KeyLengthInDwords = 64;

    private readonly RSA _rsa;

    /// <summary>
    /// Gets the default key directory under the user home directory.
    /// <para>获取用户主目录下的默认密钥目录。</para>
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
    /// Loads the authentication key from <paramref name="keyPath"/>, or generates
    /// and persists a new 2048-bit key when the file is absent.
    /// <para>从 <paramref name="keyPath"/> 加载认证密钥；文件不存在时生成并持久化新的 2048 位密钥。</para>
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
                EnsurePublicKey(path, loaded);
                return loaded;
            }
            catch { rsa.Dispose(); throw; }
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, ".adbkey-" + Guid.NewGuid().ToString("N"));
        using var created = CreateNew();
        try
        {
            WriteKeyFile(temporary, created._rsa.ExportRSAPrivateKeyPem());
            try { File.Move(temporary, path, overwrite: false); }
            catch (IOException) when (File.Exists(path)) { }
            return LoadOrCreate(path);
        }
        finally { File.Delete(temporary); }
    }

    private static void EnsurePublicKey(string path, AdbAuthentication authentication)
    {
        if (File.Exists(path + ".pub")) return;
        var temporary = path + ".pub-" + Guid.NewGuid().ToString("N");
        try
        {
            WriteKeyFile(temporary, Encoding.UTF8.GetStringTrimNul(authentication.BuildPublicKeyPayload()) + "\n");
            try { File.Move(temporary, path + ".pub", overwrite: false); }
            catch (IOException) when (File.Exists(path + ".pub")) { }
        }
        finally { File.Delete(temporary); }
    }

    /// <summary>
    /// Signs the token with RSA-PKCS1 + SHA1 (ADB signature).
    /// <para>用私钥以 RSA-PKCS1 + SHA1 对令牌签名。</para>
    /// </summary>
    /// <param name="token">The token to sign. <para>待签名的令牌。</para></param>
    /// <returns>The RSA-SHA1 signature. <para>RSA-SHA1 签名。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="token"/> is empty. <para>令牌为空。</para></exception>
    public byte[] SignToken(byte[] token)
    {
        if (token is null || token.Length == 0)
        {
            throw new ArgumentException("Token must not be empty.", nameof(token));
        }

        using var sha1 = SHA1.Create();
        var hash = sha1.ComputeHash(token);
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
            userComment = $"{Environment.UserName}@{Environment.MachineName}";
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
    {
        var modulus = parameters.Modulus ?? throw new ArgumentException("Modulus is missing.", nameof(parameters));
        var exponentBytes = parameters.Exponent ?? throw new ArgumentException("Exponent is missing.", nameof(parameters));

        var e = (int)FromBigEndianUnsigned(exponentBytes);

        var r32 = BigInteger.One << 32;
        var n = FromBigEndianUnsigned(modulus);
        var r = BigInteger.One << (KeyLengthInDwords * 32);
        var rr = BigInteger.ModPow(r, 2, n);

        var remainder = BigInteger.Remainder(n, r32);
        var tn0inv = ModInverse(remainder, r32);
        var n0inv = (uint)(BigInteger.Negate(tn0inv) & uint.MaxValue);

        var nTable = new uint[KeyLengthInDwords];
        var rrTable = new uint[KeyLengthInDwords];

        for (var i = 0; i < KeyLengthInDwords; i++)
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
    /// Resolves the user home directory across platforms.
    /// <para>跨平台解析用户主目录。</para>
    /// </summary>
    /// <returns>The user home directory path. <para>用户主目录路径。</para></returns>
    private static string GetHomeDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
        {
            return home;
        }

        home = Environment.GetEnvironmentVariable("USERPROFILE") ?? Environment.GetEnvironmentVariable("HOME") ?? string.Empty;
        return string.IsNullOrEmpty(home) ? "." : home;
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