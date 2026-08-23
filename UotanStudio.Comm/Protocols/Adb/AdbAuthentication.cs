using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace UotanStudio.Comm.Protocols.Adb;

/// <summary>
/// Converts between .NET RSA keys and the ADB wire format, and performs
/// ADB RSA-SHA1 token signing. Aligned with AOSP (adb_auth_host.cpp).
/// <para>在 .NET RSA 密钥与 ADB 线上格式之间转换，并执行 ADB RSA-SHA1 令牌签名。
/// 与 AOSP（adb_auth_host.cpp）对齐。</para>
/// </summary>
public sealed class AdbAuthentication : IDisposable
{
    private const int KeyLengthInDwords = 64; // 2048-bit key = 256 bytes

    private readonly RSA _rsa;

    /// <summary>
    /// Gets the default key directory (%USERPROFILE%/.android on Windows,
    /// ~/.android elsewhere), matching the AOSP adb key location.
    /// <para>获取默认密钥目录（Windows 为 %USERPROFILE%/.android，其他平台为
    /// ~/.android），与 AOSP adb 密钥位置一致。</para>
    /// </summary>
    public static string DefaultKeyDirectory => Path.Combine(GetHomeDirectory(), ".android");

    /// <summary>
    /// Gets the default private key file path (%USERPROFILE%/.android/adbkey),
    /// matching the AOSP adb key location.
    /// <para>获取默认私钥文件路径（%USERPROFILE%/.android/adbkey），与 AOSP adb
    /// 密钥位置一致。</para>
    /// </summary>
    public static string DefaultKeyFilePath => Path.Combine(DefaultKeyDirectory, "adbkey");

    /// <summary>
    /// Initializes authentication with the supplied 2048-bit RSA key.
    /// <para>使用提供的 2048 位 RSA 密钥初始化认证。</para>
    /// </summary>
    /// <param name="rsa">The RSA key to use. 待使用的 RSA 密钥。</param>
    /// <exception cref="ArgumentNullException"><paramref name="rsa"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">The key is not 2048 bits. 密钥不是 2048 位。</exception>
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
    /// <returns>A new <see cref="AdbAuthentication"/> instance. 新的 <see cref="AdbAuthentication"/> 实例。</returns>
    public static AdbAuthentication CreateNew()
    {
        var rsa = RSA.Create();
        rsa.KeySize = 2048;
        return new AdbAuthentication(rsa);
    }

    /// <summary>
    /// Loads the authentication key from <paramref name="keyPath"/> (PEM, PKCS#1 or
    /// PKCS#8), or generates and persists a new 2048-bit key when the file does not
    /// exist. The public key payload is written next to it with a ".pub" extension.
    /// <para>从 <paramref name="keyPath"/> 加载认证密钥（PEM，PKCS#1 或 PKCS#8）；
    /// 文件不存在时生成并持久化新的 2048 位密钥。公钥负载以 ".pub" 扩展名
    /// 写入同名文件。</para>
    /// </summary>
    /// <param name="keyPath">
    /// The private key path; defaults to <see cref="DefaultKeyFilePath"/>.
    /// 私钥路径；默认为 <see cref="DefaultKeyFilePath"/>。
    /// </param>
    /// <returns>The loaded or newly created authentication. 加载或新建的认证实例。</returns>
    public static AdbAuthentication LoadOrCreate(string? keyPath = null)
    {
        var path = keyPath ?? DefaultKeyFilePath;

        if (File.Exists(path))
        {
            var pem = File.ReadAllText(path);
            var rsa = RSA.Create();
            rsa.ImportFromPem(pem);
            if (rsa.KeySize != 2048)
            {
                var keySize = rsa.KeySize;
                rsa.Dispose();
                throw new InvalidOperationException($"The ADB key at '{path}' is {keySize} bits; 2048 bits are required.");
            }

            return new AdbAuthentication(rsa);
        }

        var created = CreateNew();
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, created._rsa.ExportRSAPrivateKeyPem());
        File.WriteAllText(path + ".pub", Encoding.UTF8.GetString(created.BuildPublicKeyPayload()));
        return created;
    }

    /// <summary>
    /// Signs the token with RSA-PKCS1 + SHA1 (ADB signature).
    /// <para>用私钥以 RSA-PKCS1 + SHA1 对令牌签名。</para>
    /// </summary>
    /// <param name="token">The token to sign. 待签名的令牌。</param>
    /// <returns>The RSA-SHA1 signature. RSA-SHA1 签名。</returns>
    /// <exception cref="ArgumentException"><paramref name="token"/> is empty. 令牌为空。</exception>
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
    /// 追加在密钥后的可选注释；默认为 "user@host"。
    /// </param>
    /// <returns>The public key payload bytes. 公钥负载字节。</returns>
    public byte[] BuildPublicKeyPayload(string? comment = null)
    {
        var publicKey = _rsa.ExportParameters(false);
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
    /// <param name="n0inv">The -1/n[0] inverse used by the ADB crypto routines. ADB 加密例程使用的 -1/n[0] 逆元。</param>
    /// <param name="n">The modulus split into 32-bit words. 按 32 位字拆分的模数。</param>
    /// <param name="rr">The r^2 mod n split into 32-bit words. 按 32 位字拆分的 r^2 mod n。</param>
    /// <param name="exponent">The public exponent. 公钥指数。</param>
    /// <returns>The serialized RSAPublicKey buffer. 序列化后的 RSAPublicKey 缓冲区。</returns>
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
    /// <param name="parameters">The .NET RSA parameters. .NET RSA 参数。</param>
    /// <returns>The ADB key fields. ADB 密钥字段。</returns>
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

        home = Environment.GetEnvironmentVariable("USERPROFILE") ?? Environment.GetEnvironmentVariable("HOME") ?? string.Empty;
        return string.IsNullOrEmpty(home) ? "." : home;
    }

    /// <summary>
    /// Interprets a big-endian unsigned byte array as a positive <see cref="BigInteger"/>.
    /// <para>将大端无符号字节数组解释为正的 <see cref="BigInteger"/>。</para>
    /// </summary>
    /// <param name="bytes">The big-endian bytes. 大端字节。</param>
    /// <returns>The parsed value. 解析后的值。</returns>
    private static BigInteger FromBigEndianUnsigned(byte[] bytes)
    {
        // BigInteger expects little-endian input; reverse and keep the value positive.
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
    /// <param name="value">The value to invert. 待求逆的值。</param>
    /// <param name="modulo">The modulus. 模数。</param>
    /// <returns>The modular inverse. 模逆元。</returns>
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
    /// <param name="left">The left operand. 左操作数。</param>
    /// <param name="right">The right operand. 右操作数。</param>
    /// <param name="leftFactor">The Bezout coefficient of <paramref name="left"/>. <paramref name="left"/> 的裴蜀系数。</param>
    /// <param name="rightFactor">The Bezout coefficient of <paramref name="right"/>. <paramref name="right"/> 的裴蜀系数。</param>
    /// <returns>The greatest common divisor. 最大公约数。</returns>
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