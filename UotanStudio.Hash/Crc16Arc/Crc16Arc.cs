namespace UotanStudio.Hash.Crc16Arc;

/// <summary>
/// Reflected CRC-16 (low-bit-first polynomial <c>0xA001</c>, the reflection of <c>0x8005</c>) with a caller-supplied
/// register seed and no final XOR, which is the checksum PAC header and data fields store.
/// <para>反射式 CRC-16（低位优先多项式 <c>0xA001</c>，即 <c>0x8005</c> 的反射），寄存器初值由调用方给出且无最终异或，
/// 正是 PAC 头部与数据字段所存储的校验和。</para>
/// </summary>
/// <remarks>
/// The name is deliberately parameter-shaped rather than a catalog alias: sources disagree on what "CRC-16" means,
/// so this type pins only the polynomial, the seed convention and the absence of a final XOR.
/// <para>类型名刻意按参数描述而非目录别名：不同资料对 “CRC-16” 的含义并不一致，因此本类型只固定多项式、seed 约定
/// 以及“不做末异或”这一点。</para>
/// </remarks>
public static class Crc16Arc
{
    private const ushort Polynomial = 0xA001;

    /// <summary>
    /// Computes the CRC-16 register value of the data starting from <paramref name="seed"/>.
    /// <para>以 <paramref name="seed"/> 为寄存器初值计算数据的 CRC-16 寄存器值。</para>
    /// </summary>
    /// <param name="data">
    /// Bytes to fold into the checksum; an empty span returns <paramref name="seed"/> unchanged.
    /// <para>要参与校验和的字节；空区间原样返回 <paramref name="seed"/>。</para>
    /// </param>
    /// <param name="seed">
    /// Starting register value; passing the result of a previous call continues the same checksum over further bytes.
    /// PAC stores a fresh checksum, so those callers pass <c>0</c>.
    /// <para>起始寄存器值；把上一次调用的结果传入即可对后续字节续算同一校验和。PAC 存放的是全新校验和，
    /// 因此那些调用点传 <c>0</c>。</para>
    /// </param>
    /// <returns>
    /// The 16-bit register after processing every byte with the low-bit-first shift.
    /// <para>以低位优先移位处理完所有字节后的 16 位寄存器值。</para>
    /// </returns>
    public static ushort Compute(ReadOnlySpan<byte> data, ushort seed)
    {
        var crc = seed;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
            {
                crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ Polynomial) : (ushort)(crc >> 1);
            }
        }

        return crc;
    }
}