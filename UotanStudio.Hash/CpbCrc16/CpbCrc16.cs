namespace UotanStudio.Hash.CpbCrc16;

/// <summary>
/// CRC-16/CCITT variant (high-bit-first polynomial <c>0x1021</c>, no final XOR) with a 32-bit accumulator that the
/// Coolpad CPB container checksum fields require: the fold step never truncates the register, so the full 32-bit
/// value participates in the stored checksum even though only its low 16 bits behave like a 16-bit CRC register.
/// <para>CRC-16/CCITT 变体（高位优先多项式 <c>0x1021</c>，无最终异或），采用酷派 CPB 容器校验和字段要求的 32 位
/// 累加器：折叠步骤从不截断寄存器，完整 32 位值都参与落盘校验和，尽管只有低 16 位表现得像 16 位 CRC 寄存器。</para>
/// </summary>
/// <remarks>
/// The table walk keeps the 32-bit accumulator untruncated, so the bits above 16 accumulate a deterministic residue
/// of the byte history and the container stores that whole value. The low 16 bits coincide with a 16-bit CCITT
/// register walk seeded from the accumulator's low half, which is what the bitwise reference loops in the tests
/// cross-check against.
/// <para>查表循环保持 32 位累加器不截断，因此第 16 位以上的部分会累积出字节历史的确定性残差，容器原样存储整个
/// 值。低 16 位与以累加器低半部为初值的 16 位 CCITT 寄存器步进一致，测试中的逐位参考循环正是对照这一恒等关系。</para>
/// </remarks>
public static class CpbCrc16
{
    private const int TableEntries = 256;

    private static readonly ushort[] Table = BuildTable();

    /// <summary>
    /// Accumulates the CPB container CRC-16 over the data, starting a fresh accumulation from zero.
    /// <para>从零开始对数据累加 CPB 容器 CRC-16。</para>
    /// </summary>
    /// <param name="data">
    /// Bytes to fold in; an empty span returns <paramref name="previousAccumulation"/> unchanged.
    /// <para>参与折叠的字节；空区间原样返回 <paramref name="previousAccumulation"/>。</para>
    /// </param>
    /// <param name="previousAccumulation">
    /// Running 32-bit accumulation; pass <c>0</c> to start a new accumulation and the previous return value to
    /// continue one, mirroring how the container folds its header bytes and image payloads into one checksum.
    /// <para>32 位累加值；从零开始累加时传 <c>0</c>，续算时传上一次的返回值，与容器把头部字节和镜像负载折叠进
    /// 同一校验和的方式一致。</para>
    /// </param>
    /// <returns>
    /// The updated 32-bit accumulation as stored by the container.
    /// <para>容器落盘的更新后 32 位累加值。</para>
    /// </returns>
    public static uint Compute(ReadOnlySpan<byte> data, uint previousAccumulation)
    {
        unchecked
        {
            var crc = previousAccumulation;
            var table = Table;
            foreach (var value in data)
            {
                crc = (crc << 8) ^ table[(byte)(crc >> 8) ^ value];
            }

            return crc;
        }
    }

    private static ushort[] BuildTable()
    {
        var table = new ushort[TableEntries];
        for (var value = 0; value < TableEntries; value++)
        {
            var entry = (ushort)(value << 8);
            for (var bit = 0; bit < 8; bit++)
            {
                entry = (entry & 0x8000) != 0 ? (ushort)((entry << 1) ^ 0x1021) : (ushort)(entry << 1);
            }

            table[value] = entry;
        }

        return table;
    }
}