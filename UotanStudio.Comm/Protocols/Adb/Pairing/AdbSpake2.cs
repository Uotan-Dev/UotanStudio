using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;

namespace UotanStudio.Comm.Protocols.Adb.Pairing;

/// <summary>
/// SPAKE2 over the Ed25519 group: the client is Alice (mask point M) and the pairing
/// server is Bob (mask point N). The shared key is a SHA-512 digest of the
/// length-prefixed transcript.
/// <para>Ed25519 群上的 SPAKE2：客户端为 Alice（掩码点 M），配对服务端为 Bob（掩码点
/// N）。共享密钥是对加长度前缀的转写内容求 SHA-512 的摘要。</para>
/// </summary>
internal sealed class AdbSpake2
{
    private const int RoleAlice = 0;
    private const int RoleBob = 1;
    private const int MessageSize = 32;
    private const int KeySize = 64;

    private static readonly BigInteger P = (BigInteger.One << 255) - 19;
    private static readonly BigInteger D = Mod(-121665 * Inverse(121666));
    private static readonly BigInteger TwoD = Mod(D << 1);
    private static readonly BigInteger L = (BigInteger.One << 252)
        + BigInteger.Parse("27742317777372353535851937790883648493", System.Globalization.CultureInfo.InvariantCulture);
    private static readonly BigInteger SqrtM1 = BigInteger.ModPow(2, (P - 1) >> 2, P);

    private static readonly BigInteger Mx = LeHex("c8a663c597f1ee40ab6242ee256f326c752ca7d3bd323b1e119cbd04a9786f45");
    private static readonly BigInteger My = LeHex("5ada7e4bf6ddd9adb6626d32131c6b5c51a1e347a3478f53cfcf441b88eed12e");
    private static readonly BigInteger Nx = LeHex("201bc5b343177110441e73b3ae3fbf9ff544c8138fd101c28a1a6dea4d005d6e");
    private static readonly BigInteger Ny = LeHex("10e3df0ae37d8e7a99b5fe74b44672103dbddcbd06af680d71329a11693bc778");

    private static readonly BigInteger[] BasePoint = Decode(BasePointEncoding());

    private readonly int _role;
    private readonly byte[] _myName;
    private readonly byte[] _theirName;

    private BigInteger _privateKey;
    private BigInteger _passwordScalar;
    private byte[] _passwordHash = [];
    private byte[] _myMsg = [];
    private bool _messageGenerated;

    /// <summary>
    /// Creates a client-side (Alice) context with the pairing role names.
    /// <para>创建使用配对角色名的客户端（Alice）上下文。</para>
    /// </summary>
    /// <returns>A new client context. <para>新的客户端上下文。</para></returns>
    public static AdbSpake2 CreateClient() =>
        new(RoleAlice, "adb pair client\0"u8.ToArray(), "adb pair server\0"u8.ToArray());

    /// <summary>
    /// Creates a server-side (Bob) context with the pairing role names.
    /// <para>创建使用配对角色名的服务端（Bob）上下文。</para>
    /// </summary>
    /// <returns>A new server context. <para>新的服务端上下文。</para></returns>
    public static AdbSpake2 CreateServer() =>
        new(RoleBob, "adb pair server\0"u8.ToArray(), "adb pair client\0"u8.ToArray());

    private AdbSpake2(int role, byte[] myName, byte[] theirName)
    {
        _role = role;
        _myName = myName;
        _theirName = theirName;
    }

    /// <summary>
    /// Generates our 32-byte SPAKE2 message from the shared password.
    /// <para>用共享密码生成本方 32 字节 SPAKE2 消息。</para>
    /// </summary>
    /// <param name="password">The shared password bytes (pairing code plus TLS exporter
    /// output). <para>共享密码字节（配对码加上 TLS exporter 输出）。</para></param>
    /// <returns>The 32-byte message. <para>32 字节消息。</para></returns>
    /// <exception cref="InvalidOperationException">The message was already generated.
    /// <para>消息已生成过。</para></exception>
    public byte[] GenerateMessage(byte[] password)
    {
        if (_messageGenerated)
        {
            throw new InvalidOperationException("The SPAKE2 message was already generated.");
        }

        var random = RandomNumberGenerator.GetBytes(64);
        return GenerateMessage(password, random);
    }

    /// <summary>
    /// Generates our SPAKE2 message from the shared password with injected randomness.
    /// <para>用共享密码与注入的随机字节生成本方 SPAKE2 消息。</para>
    /// </summary>
    /// <param name="password">The shared password bytes. <para>共享密码字节。</para></param>
    /// <param name="random64">64 bytes of randomness for the private scalar.
    /// <para>私钥标量使用的 64 字节随机数。</para></param>
    /// <returns>The 32-byte message. <para>32 字节消息。</para></returns>
    public byte[] GenerateMessage(byte[] password, byte[] random64)
    {
        if (_messageGenerated)
        {
            throw new InvalidOperationException("The SPAKE2 message was already generated.");
        }

        _privateKey = Reduce(random64) << 3;
        _passwordHash = SHA512.HashData(password);
        var scalar = Reduce(_passwordHash);
        if (scalar.TestBit(0))
        {
            scalar += L;
        }
        if (scalar.TestBit(1))
        {
            scalar += L << 1;
        }
        if (scalar.TestBit(2))
        {
            scalar += L << 2;
        }
        if (!(scalar & 7).IsZero)
        {
            throw new InvalidOperationException("The password scalar low bits could not be cleared.");
        }
        _passwordScalar = scalar;

        var basePointPart = ScalarMultiply(_privateKey, BasePoint);
        BigInteger[] mask = (_role == RoleAlice) ? [Mx, My] : [Nx, Ny];
        var message = Add(basePointPart, ScalarMultiply(scalar, mask));
        _myMsg = Encode(message);
        _messageGenerated = true;
        return _myMsg;
    }

    /// <summary>
    /// Processes the peer's 32-byte message and derives the 64-byte shared key.
    /// <para>处理对端 32 字节消息并派生 64 字节共享密钥。</para>
    /// </summary>
    /// <param name="peerMessage">The peer's SPAKE2 message. <para>对端的 SPAKE2 消息。</para></param>
    /// <returns>The 64-byte shared key. <para>64 字节共享密钥。</para></returns>
    /// <exception cref="InvalidOperationException">Our message was not generated yet.
    /// <para>本方消息尚未生成。</para></exception>
    /// <exception cref="ArgumentException">The peer message is not 32 bytes or does not
    /// decode to a curve point. <para>对端消息不是 32 字节或无法解码为曲线点。</para></exception>
    public byte[] ProcessMessage(byte[] peerMessage)
    {
        if (!_messageGenerated)
        {
            throw new InvalidOperationException("GenerateMessage must be called before ProcessMessage.");
        }
        if (peerMessage is not { Length: MessageSize })
        {
            throw new ArgumentException("The SPAKE2 peer message must be exactly 32 bytes.", nameof(peerMessage));
        }

        var peerPoint = Decode(peerMessage);
        BigInteger[] peerMaskPoint = (_role == RoleAlice) ? [Nx, Ny] : [Mx, My];
        var peerMask = ScalarMultiply(_passwordScalar, peerMaskPoint);
        var shared = ScalarMultiply(_privateKey, Sub(ToExtended(peerPoint), peerMask));
        var sharedEncoded = Encode(shared);

        using var transcript = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        if (_role == RoleAlice)
        {
            Update(transcript, _myName);
            Update(transcript, _theirName);
            Update(transcript, _myMsg);
            Update(transcript, peerMessage);
        }
        else
        {
            Update(transcript, _theirName);
            Update(transcript, _myName);
            Update(transcript, peerMessage);
            Update(transcript, _myMsg);
        }
        Update(transcript, sharedEncoded);
        Update(transcript, _passwordHash);
        return transcript.GetHashAndReset();
    }

    private static byte[] BasePointEncoding()
    {
        var encoding = new byte[32];
        encoding[0] = 0x58;
        Array.Fill(encoding, (byte)0x66, 1, 31);
        return encoding;
    }

    private static void Update(IncrementalHash hash, byte[] data)
    {
        Span<byte> length = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(length, data.Length);
        hash.AppendData(length);
        hash.AppendData(data);
    }

    /// <summary>
    /// Multiplies the Ed25519 base point by <paramref name="scalar"/> and encodes the
    /// result, so curve arithmetic can be checked against RFC 8032 vectors.
    /// <para>将 Ed25519 基点乘以 <paramref name="scalar"/> 并编码结果，便于按 RFC 8032
    /// 向量核对曲线算术。</para>
    /// </summary>
    /// <param name="scalar">The scalar multiplier. <para>标量乘数。</para></param>
    /// <returns>The encoded 32-byte point. <para>编码后的 32 字节点。</para></returns>
    internal static byte[] MultiplyBaseForTest(BigInteger scalar) => Encode(ScalarMultiply(scalar, BasePoint));

    private static BigInteger Reduce(byte[] littleEndian64) => LeToBigInteger(littleEndian64) % L;

    private static BigInteger LeToBigInteger(byte[] littleEndian)
    {
        var positive = new byte[littleEndian.Length + 1];
        Array.Copy(littleEndian, positive, littleEndian.Length);
        return new BigInteger(positive);
    }

    private static BigInteger LeHex(string hex) => LeToBigInteger(Convert.FromHexString(hex));

    private static BigInteger Mod(BigInteger value)
    {
        var remainder = value % P;
        return remainder.Sign < 0 ? remainder + P : remainder;
    }

    private static BigInteger Inverse(BigInteger value) => BigInteger.ModPow(value, P - 2, P);

    private static byte[] Encode(BigInteger[] point)
    {
        var (x, y) = Affine(point);
        var encoded = new byte[32];
        var bytes = y.ToByteArray();
        var count = Math.Min(bytes.Length, 32);
        Array.Copy(bytes, encoded, count);
        if (!x.IsEven)
        {
            encoded[31] |= 0x80;
        }
        return encoded;
    }

    private static BigInteger[] Decode(byte[] encoded)
    {
        var y = LeToBigInteger(encoded) & ((BigInteger.One << 255) - 1);
        var y2 = Mod(y * y);
        var u = Mod(y2 - 1);
        var v = Mod(D * y2 + 1);
        var vInverse = Inverse(v);
        var x = BigInteger.ModPow(Mod(u * vInverse), (P + 3) >> 3, P);
        if (Mod(x * x * v) != u)
        {
            x = Mod(x * SqrtM1);
            if (Mod(x * x * v) != u)
            {
                throw new ArgumentException("The SPAKE2 peer message is not a curve point.");
            }
        }
        var signBit = (encoded[31] & 0x80) != 0;
        if (x.IsEven == signBit)
        {
            x = P - x;
        }
        var x2 = Mod(x * x);
        if (Mod(y2 - x2) != Mod(1 + D * x2 * y2))
        {
            throw new ArgumentException("The SPAKE2 peer message is not a curve point.");
        }
        return [x, y];
    }

    private static (BigInteger X, BigInteger Y) Affine(BigInteger[] point)
    {
        var zInverse = BigInteger.ModPow(point[2], P - 2, P);
        return (Mod(point[0] * zInverse), Mod(point[1] * zInverse));
    }

    private static BigInteger[] Identity() => [BigInteger.Zero, BigInteger.One, BigInteger.One, BigInteger.Zero];

    private static BigInteger[] ToExtended(BigInteger[] affine) =>
        [affine[0], affine[1], BigInteger.One, Mod(affine[0] * affine[1])];

    private static BigInteger[] Double(BigInteger[] q)
    {
        var x1 = q[0];
        var y1 = q[1];
        var z1 = q[2];
        var a = Mod(x1 * x1);
        var b = Mod(y1 * y1);
        var c = Mod(Mod(z1 * z1) << 1);
        var d = P - a;
        var e = Mod(Mod((x1 + y1) * (x1 + y1)) - a - b);
        var g = Mod(d + b);
        var f = Mod(g - c);
        var h = Mod(d - b);
        return [Mod(e * f), Mod(g * h), Mod(f * g), Mod(e * h)];
    }

    private static BigInteger[] Add(BigInteger[] p, BigInteger[] q)
    {
        var x1 = p[0];
        var y1 = p[1];
        var z1 = p[2];
        var t1 = p[3];
        var x2 = q[0];
        var y2 = q[1];
        var z2 = q[2];
        var t2 = q[3];
        var a = Mod((y1 - x1) * (y2 - x2));
        var b = Mod((y1 + x1) * (y2 + x2));
        var c = Mod(t1 * TwoD * t2);
        var d = Mod((z1 << 1) * z2);
        var e = Mod(b - a);
        var f = Mod(d - c);
        var g = Mod(d + c);
        var h = Mod(b + a);
        return [Mod(e * f), Mod(g * h), Mod(f * g), Mod(e * h)];
    }

    private static BigInteger[] Negate(BigInteger[] q) =>
        [Mod(-q[0]), q[1], q[2], Mod(-q[3])];

    private static BigInteger[] Sub(BigInteger[] p, BigInteger[] q) => Add(p, Negate(q));

    private static BigInteger[] ScalarMultiply(BigInteger scalar, BigInteger[] point)
    {
        var extended = point.Length == 4 ? point : [point[0], point[1], BigInteger.One, Mod(point[0] * point[1])];
        var result = Identity();
        for (var i = (int)scalar.GetBitLength() - 1; i >= 0; i--)
        {
            result = Double(result);
            if (!scalar.TestBit(i))
            {
                continue;
            }
            result = Add(result, extended);
        }
        return result;
    }
}

/// <summary>
/// Bit-access extension members over big integer values.
/// <para>为大整数值提供按位访问的扩展成员。</para>
/// </summary>
internal static class AdbSpake2Extensions
{
    extension(BigInteger value)
    {
        /// <summary>
        /// Returns whether bit <paramref name="index"/> of the value is set.
        /// <para>返回该值的第 <paramref name="index"/> 位是否置位。</para>
        /// </summary>
        /// <param name="index">The zero-based bit index. <para>从零开始的位序号。</para></param>
        /// <returns><c>true</c> when that bit is set. <para>该位已置位时为 <c>true</c>。</para></returns>
        public bool TestBit(int index) => !((value >> index) & BigInteger.One).IsZero;
    }
}