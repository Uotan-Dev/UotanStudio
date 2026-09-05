namespace UotanStudio.Firmware.Common.Checksums;

/// <summary>
/// Adler-32 校验和（RFC 1950）公共工具类。
/// <para>Adler-32 checksum (RFC 1950) shared utility.</para>
/// </summary>
public static class Adler32
{
    private const uint Mod = 65521;
    private const int Nmax = 5552;

    /// <summary>
    /// 计算数据的 Adler-32 校验和。
    /// <para>Computes the Adler-32 checksum of the given data.</para>
    /// </summary>
    /// <param name="data">要计算校验和的数据。<para>The data to checksum.</para></param>
    /// <returns>Adler-32 校验和值。<para>The Adler-32 checksum value.</para></returns>
    public static uint Compute(ReadOnlySpan<byte> data)
    {
        // 每 NMAX 字节取模一次（zlib 标准做法）：5552 满足 255n(n+1)/2 + (n+1)(BASE-1) ≤ 2^32-1，
        // 期间 a/b 不会溢出 32 位，同时省去逐字节取模。
        // Modulo once per NMAX bytes (zlib's approach): 5552 satisfies
        // 255n(n+1)/2 + (n+1)(BASE-1) ≤ 2^32-1, so a/b never overflow 32 bits and per-byte mod is avoided.
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
    /// 计算字节数组切片的 Adler-32 校验和。
    /// <para>Computes the Adler-32 checksum of a byte array slice.</para>
    /// </summary>
    /// <param name="data">字节数组。<para>The byte array.</para></param>
    /// <param name="offset">起始偏移。<para>The starting offset.</para></param>
    /// <param name="length">字节数。<para>The number of bytes.</para></param>
    /// <returns>Adler-32 校验和值。<para>The Adler-32 checksum value.</para></returns>
    public static uint Compute(byte[] data, int offset, int length)
        => Compute(data.AsSpan(offset, length));
}
