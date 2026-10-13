using System.Text;

namespace UotanStudio.Hash.F2fsDentryHash;

/// <summary>
/// Computes the F2FS dentry hash: TEA transform over 16-byte chunks started from the MD4-style init vector,
/// returning 0 for the <c>.</c> and <c>..</c> names as the kernel does.
/// <para>计算 F2FS 目录项哈希：从 MD4 风格初始向量出发对 16 字节分块做 TEA 变换，并为 <c>.</c> 与 <c>..</c> 返回 0，与内核行为一致。</para>
/// </summary>
internal static class F2fsDentryHash
{
    /// <summary>
    /// Computes the hash of one directory entry name; empty, <c>.</c> and <c>..</c> map to 0.
    /// <para>计算单个目录项名称的哈希；空名称、<c>.</c> 与 <c>..</c> 均映射为 0。</para>
    /// </summary>
    /// <param name="name">Directory entry name, UTF-8 encoded internally.
    /// <para>目录项名称，内部按 UTF-8 编码。</para></param>
    /// <returns>The 32-bit dentry hash.<para>32 位目录项哈希。</para></returns>
    internal static uint Compute(string name)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        if (nameBytes.Length <= 2 && nameBytes.Length > 0 && nameBytes[0] == (byte)'.'
            && (nameBytes.Length == 1 || nameBytes[1] == (byte)'.'))
        {
            return 0;
        }

        var buf = new uint[4] { 0x67452301u, 0xEFCDAB89u, 0x98BADCFEu, 0x10325476u };
        var offset = 0;
        var length = nameBytes.Length;
        while (true)
        {
            var input = Str2HashBuf(nameBytes.AsSpan(offset, Math.Min(length, 16)), Math.Min(length, 16));
            TeaTransform(buf, input);
            offset += 16;
            if (length <= 16)
            {
                break;
            }

            length -= 16;
        }

        return buf[0];
    }

    private static uint[] Str2HashBuf(ReadOnlySpan<byte> message, int length)
    {
        var result = new uint[4];
        var pad = (uint)(length | (length << 8));
        pad |= pad << 16;
        var val = pad;
        var len = Math.Min(length, 16);
        var bufIndex = 0;
        var num = 4;
        for (var i = 0; i < len; i++)
        {
            if (i % 4 == 0)
            {
                val = pad;
            }

            val = message[i] + (val << 8);
            if (i % 4 == 3)
            {
                result[bufIndex++] = val;
                val = pad;
                num--;
            }
        }

        if (--num >= 0)
        {
            result[bufIndex++] = val;
        }

        while (--num >= 0)
        {
            result[bufIndex++] = pad;
        }

        return result;
    }

    private static void TeaTransform(uint[] buf, uint[] input)
    {
        const uint delta = 0x9E3779B9u;
        var sum = 0u;
        var b0 = buf[0];
        var b1 = buf[1];
        var a = input[0];
        var b = input[1];
        var c = input[2];
        var d = input[3];
        var n = 16;
        do
        {
            sum += delta;
            b0 += ((b1 << 4) + a) ^ (b1 + sum) ^ ((b1 >> 5) + b);
            b1 += ((b0 << 4) + c) ^ (b0 + sum) ^ ((b0 >> 5) + d);
        }
        while (--n > 0);

        buf[0] += b0;
        buf[1] += b1;
    }
}