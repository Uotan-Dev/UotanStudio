namespace UotanStudio.Firmware.Core.Checksums;

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

    /// <summary>
    /// Computes the Adler-32 checksum of a byte array slice.
    /// <para>计算字节数组切片的 Adler-32 校验和。</para>
    /// </summary>
    /// <param name="data">Source array; the slice is read without copying. <para>源数组；读取切片时不进行复制。</para></param>
    /// <param name="offset">Index of the first byte to include. <para>参与计算的首字节索引。</para></param>
    /// <param name="length">Number of bytes to include. <para>参与计算的字节数。</para></param>
    /// <returns>
    /// The 32-bit Adler-32 value computed over the requested slice.
    /// <para>对指定切片计算得到的 32 位 Adler-32 值。</para>
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="offset"/> or <paramref name="length"/> is negative, or the range exceeds the array.
    /// <para><paramref name="offset"/> 或 <paramref name="length"/> 为负，或区间超出数组边界。</para>
    /// </exception>
    public static uint Compute(byte[] data, int offset, int length)
        => Compute(data.AsSpan(offset, length));
}