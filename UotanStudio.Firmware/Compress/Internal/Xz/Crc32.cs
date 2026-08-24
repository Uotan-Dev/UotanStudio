namespace UotanStudio.Firmware.Compress.Internal.Xz;

/// <summary>
/// 标准 CRC-32（IEEE 802.3，多项式 0xEDB88320 反射形式），用于 XZ 流头/块头/索引/校验。
/// <para>Standard CRC-32 (IEEE 802.3, reflected polynomial 0xEDB88320), used for XZ
/// stream/block headers, index and check.</para>
/// </summary>
internal static class Crc32
{
    /// <summary>
    /// 计算 CRC-32（初始值 0xFFFFFFFF，最终异或 0xFFFFFFFF）。
    /// <para>Computes CRC-32 (initial value 0xFFFFFFFF, final XOR 0xFFFFFFFF).</para>
    /// </summary>
    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = new System.IO.Hashing.Crc32();
        crc.Append(data);
        return crc.GetCurrentHashAsUInt32();
    }

    /// <summary>
    /// 写入小端序 CRC-32 到流中。
    /// <para>Writes the little-endian CRC-32 to a stream.</para>
    /// </summary>
    public static void WriteLittleEndian(Stream stream, uint crc)
    {
        stream.WriteByte((byte)(crc & 0xFF));
        stream.WriteByte((byte)((crc >> 8) & 0xFF));
        stream.WriteByte((byte)((crc >> 16) & 0xFF));
        stream.WriteByte((byte)((crc >> 24) & 0xFF));
    }
}