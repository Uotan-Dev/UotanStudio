namespace UotanStudio.Firmware.Compress.Internal.Xz;

/// <summary>
/// CRC-64（ECMA-182，多项式 0xC96C5795D7870F42 反射形式），用于 XZ 校验。
/// <para>CRC-64 (ECMA-182, reflected polynomial 0xC96C5795D7870F42), used for the XZ check.</para>
/// </summary>
internal static class Crc64
{
    private static readonly ulong[] Table = BuildTable();

    private static ulong[] BuildTable()
    {
        const ulong poly = 0xC96C5795D7870F42UL;
        var table = new ulong[256];
        for (ulong i = 0; i < 256; i++)
        {
            var crc = i;
            for (var j = 0; j < 8; j++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ poly : crc >> 1;
            }

            table[i] = crc;
        }
        return table;
    }

    /// <summary>计算 CRC-64（初始值 0xFFFFFFFFFFFFFFFF，最终异或 0xFFFFFFFFFFFFFFFF）。</summary>
    public static ulong Compute(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFFFFFFFFFUL;
        foreach (var b in data)
        {
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFFFFFFFFFUL;
    }

    /// <summary>
    /// 增量 CRC-64：<paramref name="state"/> 为前一段的中间值（首段用 0xFFFFFFFFFFFFFFFF），
    /// 返回更新后的中间值；全部追加后异或 0xFFFFFFFFFFFFFFFF 即得最终值。
    /// <para>Incremental CRC-64: <paramref name="state"/> is the intermediate value of the
    /// previous segment (start with 0xFFFFFFFFFFFFFFFF); returns the updated state; XOR
    /// with 0xFFFFFFFFFFFFFFFF after all segments for the final value.</para>
    /// </summary>
    public static ulong Append(ulong state, ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            state = Table[(state ^ b) & 0xFF] ^ (state >> 8);
        }

        return state;
    }
}