using System.Buffers.Binary;
using System.Numerics;

namespace UotanStudio.Firmware.VendorFirmware.Allwinner.ImageWty;

/// <summary>
/// RC6-32/20 block cipher used by encrypted IMAGEWTY containers: 128-bit blocks, 20 rounds and a
/// 32-bit word size, keyed with a 256-bit key and operating on little-endian words.
/// <para>加密 IMAGEWTY 容器使用的 RC6-32/20 分组密码：128 位分组、20 轮、32 位字，使用 256 位密钥并以小端字运算。</para>
/// </summary>
internal sealed class Rc6
{
    private const int Rounds = 20;
    private const int BlockBytes = 16;
    private const uint P32 = 0xB7E15163;
    private const uint Q32 = 0x9E3779B9;

    private readonly uint[] _schedule;

    private Rc6(uint[] schedule) => _schedule = schedule;

    /// <summary>Byte length of one RC6 block. <para>一个 RC6 分组的字节长度。</para></summary>
    public static int BlockSize => BlockBytes;

    /// <summary>
    /// Expands a 128-bit to 256-bit key into the round schedule. IMAGEWTY containers always use the
    /// 256-bit form; shorter keys exist only so the published RC6 vectors stay testable.
    /// <para>把 128 位至 256 位密钥扩展为轮密钥。IMAGEWTY 容器始终使用 256 位形式，较短密钥仅用于让公开的 RC6 测试向量可验证。</para>
    /// </summary>
    /// <param name="key">Key bytes, 16 to 32. <para>密钥字节，16 至 32 个。</para></param>
    /// <returns>Ready cipher instance. <para>可直接使用的密码实例。</para></returns>
    /// <exception cref="ArgumentException">The key is shorter than 16 bytes or longer than 32 bytes.
    /// <para>密钥短于 16 字节或长于 32 字节。</para></exception>
    public static Rc6 Create(ReadOnlySpan<byte> key)
    {
        if (key.Length is < 16 or > 32)
        {
            throw new ArgumentException("RC6 requires a 128-bit to 256-bit key.", nameof(key));
        }

        var keyWordCount = (key.Length + 3) / 4;
        Span<uint> keyWords = stackalloc uint[keyWordCount];
        for (var index = 0; index < key.Length; index++)
        {
            keyWords[index / 4] |= (uint)key[index] << (8 * (index % 4));
        }

        var schedule = new uint[(2 * Rounds) + 4];
        schedule[0] = P32;
        for (var index = 1; index < schedule.Length; index++)
        {
            schedule[index] = unchecked(schedule[index - 1] + Q32);
        }

        var mixA = 0u;
        var mixB = 0u;
        var scheduleIndex = 0;
        var keyIndex = 0;
        var iterations = 3 * Math.Max(keyWords.Length, schedule.Length);
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            mixA = schedule[scheduleIndex] = BitOperations.RotateLeft(unchecked(schedule[scheduleIndex] + mixA + mixB), 3);
            mixB = keyWords[keyIndex] = BitOperations.RotateLeft(unchecked(keyWords[keyIndex] + mixA + mixB),
                (int)((mixA + mixB) & 0x1F));
            scheduleIndex = (scheduleIndex + 1) % schedule.Length;
            keyIndex = (keyIndex + 1) % keyWords.Length;
        }

        return new Rc6(schedule);
    }

    /// <summary>
    /// Decrypts one 16-byte block in place.
    /// <para>就地解密一个 16 字节分组。</para>
    /// </summary>
    /// <param name="block">Block bytes, exactly 16. <para>分组字节，恰好 16 个。</para></param>
    /// <exception cref="ArgumentException">The block is not exactly 16 bytes. <para>分组不是恰好 16 字节。</para></exception>
    public void DecryptBlock(Span<byte> block)
    {
        if (block.Length != BlockBytes)
        {
            throw new ArgumentException("RC6 operates on 16-byte blocks.", nameof(block));
        }

        var roundA = BinaryPrimitives.ReadUInt32LittleEndian(block);
        var roundB = BinaryPrimitives.ReadUInt32LittleEndian(block[4..]);
        var roundC = BinaryPrimitives.ReadUInt32LittleEndian(block[8..]);
        var roundD = BinaryPrimitives.ReadUInt32LittleEndian(block[12..]);

        roundC = unchecked(roundC - _schedule[(2 * Rounds) + 3]);
        roundA = unchecked(roundA - _schedule[(2 * Rounds) + 2]);

        for (var round = Rounds; round > 0; round--)
        {
            var swap = roundA;
            roundA = roundD;
            roundD = roundC;
            roundC = roundB;
            roundB = swap;

            var u = BitOperations.RotateLeft(unchecked(roundD * ((2u * roundD) + 1u)), 5);
            var t = BitOperations.RotateLeft(unchecked(roundB * ((2u * roundB) + 1u)), 5);
            roundC = unchecked(BitOperations.RotateRight(unchecked(roundC - _schedule[(2 * round) + 1]), (int)t) ^ u);
            roundA = unchecked(BitOperations.RotateRight(unchecked(roundA - _schedule[2 * round]), (int)u) ^ t);
        }

        roundD = unchecked(roundD - _schedule[1]);
        roundB = unchecked(roundB - _schedule[0]);

        BinaryPrimitives.WriteUInt32LittleEndian(block, roundA);
        BinaryPrimitives.WriteUInt32LittleEndian(block[4..], roundB);
        BinaryPrimitives.WriteUInt32LittleEndian(block[8..], roundC);
        BinaryPrimitives.WriteUInt32LittleEndian(block[12..], roundD);
    }

    /// <summary>
    /// Encrypts one 16-byte block in place; the inverse of <see cref="DecryptBlock"/>.
    /// <para>就地加密一个 16 字节分组；与 <see cref="DecryptBlock"/> 互逆。</para>
    /// </summary>
    /// <param name="block">Block bytes, exactly 16. <para>分组字节，恰好 16 个。</para></param>
    /// <exception cref="ArgumentException">The block is not exactly 16 bytes. <para>分组不是恰好 16 字节。</para></exception>
    public void EncryptBlock(Span<byte> block)
    {
        if (block.Length != BlockBytes)
        {
            throw new ArgumentException("RC6 operates on 16-byte blocks.", nameof(block));
        }

        var roundA = BinaryPrimitives.ReadUInt32LittleEndian(block);
        var roundB = BinaryPrimitives.ReadUInt32LittleEndian(block[4..]);
        var roundC = BinaryPrimitives.ReadUInt32LittleEndian(block[8..]);
        var roundD = BinaryPrimitives.ReadUInt32LittleEndian(block[12..]);

        roundB = unchecked(roundB + _schedule[0]);
        roundD = unchecked(roundD + _schedule[1]);

        for (var round = 1; round <= Rounds; round++)
        {
            var t = BitOperations.RotateLeft(unchecked(roundB * ((2u * roundB) + 1u)), 5);
            var u = BitOperations.RotateLeft(unchecked(roundD * ((2u * roundD) + 1u)), 5);
            roundA = unchecked(BitOperations.RotateLeft(unchecked(roundA ^ t), (int)u) + _schedule[2 * round]);
            roundC = unchecked(BitOperations.RotateLeft(unchecked(roundC ^ u), (int)t) + _schedule[(2 * round) + 1]);

            var swap = roundA;
            roundA = roundB;
            roundB = roundC;
            roundC = roundD;
            roundD = swap;
        }

        roundA = unchecked(roundA + _schedule[(2 * Rounds) + 2]);
        roundC = unchecked(roundC + _schedule[(2 * Rounds) + 3]);

        BinaryPrimitives.WriteUInt32LittleEndian(block, roundA);
        BinaryPrimitives.WriteUInt32LittleEndian(block[4..], roundB);
        BinaryPrimitives.WriteUInt32LittleEndian(block[8..], roundC);
        BinaryPrimitives.WriteUInt32LittleEndian(block[12..], roundD);
    }
}