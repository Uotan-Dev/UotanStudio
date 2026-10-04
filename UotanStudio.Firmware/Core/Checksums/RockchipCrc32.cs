namespace UotanStudio.Firmware.Core.Checksums;

/// <summary>
/// Non-reflected CRC-32 (polynomial <c>0x04C10DB7</c>, MSB-first, init 0, no final XOR) matching the
/// <c>gTable_Crc32</c> semantics of Rockchip's rkaf/rkdeveloptool <c>crc.cpp</c>, used by RKAF/RKFW
/// container and loader image checksums.
/// <para>非反射式 CRC-32（多项式 <c>0x04C10DB7</c>，高位优先，初值 0，无最终异或），对应 Rockchip
/// rkaf/rkdeveloptool 的 <c>crc.cpp</c> 中 <c>gTable_Crc32</c> 的语义，用于 RKAF/RKFW 容器与 loader
/// 镜像校验。</para>
/// </summary>
public static class RockchipCrc32
{
    /// <summary>
    /// Computes the Rockchip CRC-32 of the data starting from <paramref name="seed"/>. To checksum a
    /// stream in chunks, pass the previous return value as the next <paramref name="seed"/>.
    /// <para>以 <paramref name="seed"/> 为初值计算数据的 Rockchip CRC-32。分块计算流数据时，把上一次的
    /// 返回值作为下一次的 <paramref name="seed"/> 传入即可续算。</para>
    /// </summary>
    /// <param name="data">
    /// Bytes to hash; an empty span returns <paramref name="seed"/> unchanged.
    /// <para>参与计算的字节；空区间原样返回 <paramref name="seed"/>。</para>
    /// </param>
    /// <param name="seed">
    /// Register initial value; the convention for full images is 0, and the previous return value for
    /// chunked continuation.
    /// <para>寄存器初值；整图计算按约定取 0，分块续算时取上一次的返回值。</para>
    /// </param>
    /// <returns>
    /// The Rockchip CRC-32 register value after consuming the data.
    /// <para>消费完数据后的 Rockchip CRC-32 寄存器值。</para>
    /// </returns>
    public static uint Compute(ReadOnlySpan<byte> data, uint seed = 0)
    {
        var crc = seed;
        foreach (var value in data)
        {
            crc ^= (uint)value << 24;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ 0x04C10DB7u : crc << 1;
            }
        }

        return crc;
    }
}