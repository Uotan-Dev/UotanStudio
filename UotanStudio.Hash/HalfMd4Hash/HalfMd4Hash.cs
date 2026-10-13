using System.Text;

namespace UotanStudio.Hash.HalfMd4Hash;

/// <summary>
/// Computes the ext4 half-MD4 directory hash (state word 1 with its least significant bit cleared), matching the
/// EXT2_HASH_HALF_MD4 branch of e2fsprogs ext2fs_dirhash: an all-zero seed selects the default MD4 init vector.
/// <para>计算 ext4 half-MD4 目录哈希（返回最低位清零的状态字 1），与 e2fsprogs ext2fs_dirhash 的 EXT2_HASH_HALF_MD4
/// 分支一致：全零种子改用默认 MD4 初始向量。</para>
/// </summary>
internal static class HalfMd4Hash
{
    private static readonly uint[] DefaultHashInit = [0x67452301, 0xEFCDAB89, 0x98BADCFE, 0x10325476];

    private const uint Md4K2 = 0x5A82_7999;
    private const uint Md4K3 = 0x6ED9_EBA1;

    /// <summary>
    /// Computes the ext4 Half-MD4 hash of a directory name (buf[1] &amp; ~1), matching the EXT2_HASH_HALF_MD4 branch of e2fsprogs ext2fs_dirhash: an all-zero seed selects the default MD4 init vector and the name is transformed in 32-byte chunks.
    /// <para>计算目录名的 ext4 Half-MD4 哈希（返回 buf[1] &amp; ~1）。</para>
    /// </summary>
    /// <param name="name">Directory entry name; it is UTF-8 encoded and consumed in 32-byte chunks.
    /// <para>目录项名称；先按 UTF-8 编码，再以 32 字节分块消费。</para></param>
    /// <param name="seed">At least four seed words from the superblock hash seed; an all-zero vector selects the default MD4 initial state.
    /// <para>超级块哈希种子中至少四个种子词；全零向量改用默认 MD4 初始状态。</para></param>
    /// <returns>The directory hash, which is state word 1 with its least significant bit cleared.
    /// <para>目录哈希值，即清除最低有效位后的状态字 1。</para></returns>
    internal static uint Compute(string name, uint[] seed)
    {
        ArgumentNullException.ThrowIfNull(seed);
        if (seed.Length < 4)
        {
            throw new ArgumentException("Hash seed must have at least 4 elements.", nameof(seed));
        }

        var buf = new uint[4];
        var seedIsZero = true;
        for (var i = 0; i < 4; i++)
        {
            buf[i] = seed[i];
            seedIsZero &= seed[i] == 0;
        }

        if (seedIsZero)
        {
            Array.Copy(DefaultHashInit, buf, 4);
        }

        var nameBytes = Encoding.UTF8.GetBytes(name);
        var offset = 0;
        var remaining = nameBytes.Length;
        while (remaining > 0)
        {
            var words = Str2HashBuf(nameBytes, offset, remaining);
            HalfMd4Transform(buf, words);
            offset += 32;
            remaining -= 32;
        }

        return buf[1] & 0xFFFF_FFFE;
    }

    /// <summary>
    /// Converts name bytes into up to 8 uint32 words (little-endian accumulation + length padding), matching e2fsprogs str2hashbuf(num=8).
    /// <para>将名字字节转为最多 8 个 uint32 词（小端累积 + 长度填充），与 e2fsprogs str2hashbuf(num=8) 一致。</para>
    /// </summary>
    private static uint[] Str2HashBuf(byte[] name, int offset, int len)
    {
        var pad = (uint)(len | (len << 8) | (len << 16) | (len << 24));
        var words = new uint[8];
        var val = pad;
        var count = 0;
        var limit = Math.Min(len, 32);
        for (var i = 0; i < limit; i++)
        {
            val = name[offset + i] + (val << 8);
            if ((i & 3) == 3)
            {
                words[count++] = val;
                val = pad;
            }
        }

        if ((limit & 3) != 0)
        {
            words[count++] = val;
        }

        while (count < 8)
        {
            words[count++] = pad;
        }

        return words;
    }

    /// <summary>
    /// Truncated MD4 transform, line-for-line equivalent to e2fsprogs halfMD4Transform.
    /// <para>截断 MD4 变换，与 e2fsprogs halfMD4Transform 逐行一致。</para>
    /// </summary>
    private static void HalfMd4Transform(uint[] buf, uint[] input)
    {
        var a = buf[0];
        var b = buf[1];
        var c = buf[2];
        var d = buf[3];

        a = RotateLeft(a + F(b, c, d) + input[0], 3);
        d = RotateLeft(d + F(a, b, c) + input[1], 7);
        c = RotateLeft(c + F(d, a, b) + input[2], 11);
        b = RotateLeft(b + F(c, d, a) + input[3], 19);
        a = RotateLeft(a + F(b, c, d) + input[4], 3);
        d = RotateLeft(d + F(a, b, c) + input[5], 7);
        c = RotateLeft(c + F(d, a, b) + input[6], 11);
        b = RotateLeft(b + F(c, d, a) + input[7], 19);

        a = RotateLeft(a + G(b, c, d) + input[1] + Md4K2, 3);
        d = RotateLeft(d + G(a, b, c) + input[3] + Md4K2, 5);
        c = RotateLeft(c + G(d, a, b) + input[5] + Md4K2, 9);
        b = RotateLeft(b + G(c, d, a) + input[7] + Md4K2, 13);
        a = RotateLeft(a + G(b, c, d) + input[0] + Md4K2, 3);
        d = RotateLeft(d + G(a, b, c) + input[2] + Md4K2, 5);
        c = RotateLeft(c + G(d, a, b) + input[4] + Md4K2, 9);
        b = RotateLeft(b + G(c, d, a) + input[6] + Md4K2, 13);

        a = RotateLeft(a + H(b, c, d) + input[3] + Md4K3, 3);
        d = RotateLeft(d + H(a, b, c) + input[7] + Md4K3, 9);
        c = RotateLeft(c + H(d, a, b) + input[2] + Md4K3, 11);
        b = RotateLeft(b + H(c, d, a) + input[6] + Md4K3, 15);
        a = RotateLeft(a + H(b, c, d) + input[1] + Md4K3, 3);
        d = RotateLeft(d + H(a, b, c) + input[5] + Md4K3, 9);
        c = RotateLeft(c + H(d, a, b) + input[0] + Md4K3, 11);
        b = RotateLeft(b + H(c, d, a) + input[4] + Md4K3, 15);

        buf[0] += a;
        buf[1] += b;
        buf[2] += c;
        buf[3] += d;
    }

    private static uint F(uint x, uint y, uint z) => z ^ (x & (y ^ z));

    private static uint G(uint x, uint y, uint z) => (x & y) + ((x ^ y) & z);

    private static uint H(uint x, uint y, uint z) => x ^ y ^ z;

    private static uint RotateLeft(uint value, int bits) => (value << bits) | (value >> (32 - bits));
}