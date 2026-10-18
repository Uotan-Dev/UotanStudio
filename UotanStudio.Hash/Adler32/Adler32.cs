namespace UotanStudio.Hash.Adler32;

/// <summary>
/// Adler-32 checksum computation used by zlib-style payloads, reduced modulo <c>65521</c> in blocks of <c>5552</c> bytes.
/// <para>用于 zlib 风格载荷的 Adler-32 校验实现，按 <c>5552</c> 字节分块并对模数 <c>65521</c> 取余。</para>
/// </summary>
public static class Adler32
{
    private const uint Mod = 65521;
    private const int Nmax = 5552;

    /// <summary>
    /// Computes the Adler-32 checksum of the given data.
    /// <para>计算数据的 Adler-32 校验和。</para>
    /// </summary>
    /// <param name="data">
    /// Bytes to hash; an empty span yields the initial value <c>1</c>.
    /// <para>参与计算的字节；空区间返回初始值 <c>1</c>。</para>
    /// </param>
    /// <returns>
    /// The 32-bit Adler-32 value with the <c>b</c> accumulation in the high half, reduced modulo <c>65521</c>.
    /// <para>32 位 Adler-32 值，高半部为 <c>b</c> 累加结果，均按模数 <c>65521</c> 取余。</para>
    /// </returns>
    public static uint Compute(ReadOnlySpan<byte> data)
    {


        var a = 1u;
        var b = 0u;
        var i = 0;
        while (i < data.Length)
        {
            var end = Math.Min(i + Nmax, data.Length);
            while (i < end)
            {
                a += data[i++];
                b += a;
            }
            a %= Mod;
            b %= Mod;
        }
        return (b << 16) | a;
    }

    /// <summary>Computes the Adler-32 checksum of a byte array slice.
    /// <para>计算字节数组切片的 Adler-32 校验和。</para></summary>
    /// <param name="data">Source array; the slice is read without copying.
    /// <para>源数组；读取切片时不进行复制。</para></param>
    /// <param name="offset">Index of the first byte to include.<para>参与计算的首字节索引。</para></param>
    /// <param name="length">Number of bytes to include.<para>参与计算的字节数。</para></param>
    /// <returns>The 32-bit Adler-32 value computed over the requested slice.
    /// <para>对指定切片计算得到的 32 位 Adler-32 值。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="offset"/> or <paramref name="length"/> is negative, or the range exceeds the array.
    /// <para><paramref name="offset"/> 或 <paramref name="length"/> 为负，或区间超出数组边界。</para></exception>
    public static uint Compute(byte[] data, int offset, int length)
        => Compute(data.AsSpan(offset, length));

    /// <summary>Computes the Adler-32 checksum of the data continuing from <paramref name="previousResult"/>. To checksum a stream in chunks, pass the previous return value as the next <paramref name="previousResult"/>.
    /// <para>在 <paramref name="previousResult"/> 的基础上继续计算数据的 Adler-32 校验和。分块计算流数据时，把上一次的 返回值作为下一次的 <paramref name="previousResult"/> 传入即可续算。</para></summary>
    /// <param name="data">Bytes to hash; an empty span returns <paramref name="previousResult"/> unchanged.
    /// <para>参与计算的字节；空区间原样返回 <paramref name="previousResult"/>。</para></param>
    /// <param name="previousResult">Packed Adler-32 value of every earlier byte, holding <c>a</c> in the low half and <c>b</c> in the high half; the value <c>1</c> starts a new checksum.
    /// <para>此前所有字节的打包 Adler-32 值，低半部为 <c>a</c>，高半部为 <c>b</c>；取值 <c>1</c> 表示重新开始计算。</para></param>
    /// <returns>The Adler-32 value of the bytes already covered by <paramref name="previousResult"/> followed by <paramref name="data"/>, identical to <see cref="Compute(ReadOnlySpan{byte})"/> over the concatenated bytes.
    /// <para>返回 <paramref name="previousResult"/> 已覆盖字节后接 <paramref name="data"/> 的 Adler-32 值，与对拼接后的 完整字节调用 <see cref="Compute(ReadOnlySpan{byte})"/> 的结果一致。</para></returns>
    public static uint Compute(ReadOnlySpan<byte> data, uint previousResult)
    {
        if (data.IsEmpty)
        {
            return previousResult;
        }

        var added = Compute(data);
        var carry = (long)(previousResult & 0xFFFF) - 1;
        var low = ((added & 0xFFFF) + carry) % Mod;
        var high = ((added >> 16) + (previousResult >> 16) + (long)data.Length * carry) % Mod;
        return (uint)((high << 16) | low);
    }
}