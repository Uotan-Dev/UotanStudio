namespace UotanStudio.Firmware.Sparse;

/// <summary>
/// CRC32 (IEEE 802.3) helper backed by the Crc32.NET package.
/// <para>基于 Crc32.NET 包的 CRC32(IEEE 802.3)辅助工具。</para>
/// </summary>
public static class Crc32
{
    /// <summary>
    /// Computes the CRC32 checksum of the given data.
    /// <para>计算给定数据的 CRC32 校验和。</para>
    /// </summary>
    public static uint Calculate(ReadOnlySpan<byte> data) => data.IsEmpty ? 0u : Force.Crc32.Crc32Algorithm.Compute(data.ToArray());

    /// <summary>
    /// Begins an incremental CRC32 calculation.
    /// <para>开始增量式 CRC32 计算。</para>
    /// </summary>
    public static uint Begin() => 0u;

    /// <summary>
    /// Updates an incremental CRC32 calculation with more data.
    /// <para>用更多数据更新增量式 CRC32 计算。</para>
    /// </summary>
    public static uint Update(uint crc, ReadOnlySpan<byte> data) => data.IsEmpty ? crc : Force.Crc32.Crc32Algorithm.Append(crc, data.ToArray());

    /// <summary>
    /// Updates the CRC32 with a 4-byte fill value repeated for a given length.
    /// <para>用重复给定长度的 4 字节填充值更新 CRC32。</para>
    /// </summary>
    public static uint UpdateRepeated(uint crc, uint fillValue, long totalBytes)
    {
        Span<byte> buf = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(buf, fillValue);
        var full = (int)(totalBytes / 4);
        for (var i = 0; i < full; i++)
        {
            crc = Update(crc, buf);
        }
        var rem = (int)(totalBytes % 4);
        if (rem > 0)
        {
            crc = Update(crc, buf[..rem]);
        }
        return crc;
    }

    /// <summary>
    /// Updates the CRC32 with zero bytes of the given length.
    /// <para>用给定长度的零字节更新 CRC32。</para>
    /// </summary>
    public static uint UpdateZero(uint crc, long totalBytes)
    {
        Span<byte> buf = stackalloc byte[4096];
        var remaining = totalBytes;
        while (remaining > 0)
        {
            var n = (int)Math.Min(buf.Length, remaining);
            crc = Update(crc, buf[..n]);
            remaining -= n;
        }
        return crc;
    }

    /// <summary>
    /// Finalizes an incremental CRC32 calculation.
    /// <para>结束增量式 CRC32 计算。</para>
    /// </summary>
    public static uint Finish(uint crc) => crc;
}