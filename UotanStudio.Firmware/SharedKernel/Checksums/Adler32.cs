namespace UotanStudio.Firmware.SharedKernel.Checksums;

public static class Adler32
{
    private const uint Mod = 65521;
    private const int Nmax = 5552;

    /// <summary>
    /// Computes the Adler-32 checksum of the given data.
    /// <para>计算数据的 Adler-32 校验和。</para>
    /// </summary>
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
    public static uint Compute(byte[] data, int offset, int length)
        => Compute(data.AsSpan(offset, length));
}
