using System.Buffers.Binary;

namespace UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4.Builder;

/// <summary>
/// EXT4 builder part carrying the CRC32c table used by <c>metadata_csum</c> together with the little-endian write and power-of-two rounding helpers.
/// <para>EXT4 构建器的组成部分，提供 <c>metadata_csum</c> 使用的 CRC32c 查表实现，以及小端写出与 2 的幂取整辅助方法。</para>
/// </summary>
public sealed partial class Ext4Builder
{

    private static readonly uint[] Crc32cTable = InitCrc32cTable();

    /// <summary>
    /// Computes the CRC32c checksum of the given data with the default seed 0xFFFFFFFF.
    /// <para>计算数据的 CRC32c 校验值（默认种子 0xFFFFFFFF）。</para>
    /// </summary>
    private static uint ComputeCrc32(ReadOnlySpan<byte> data) => ComputeCrc32(data, 0xFFFFFFFF);

    /// <summary>
    /// Computes the CRC32c checksum of the given data with the specified seed.
    /// <para>使用指定种子计算数据的 CRC32c 校验值。</para>
    /// </summary>
    private static uint ComputeCrc32(ReadOnlySpan<byte> data, uint seed)
    {
        uint crc = seed;
        foreach (byte b in data)
        {
            crc = Crc32cTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }
        return crc;
    }


    private static uint ComputeCrc32Continue(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
        {
            crc = Crc32cTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }
        return crc;
    }


    private static uint[] InitCrc32cTable()
    {
        var table = new uint[256];
        for (int i = 0; i < 256; i++)
        {
            uint crc = (uint)i;
            for (int j = 0; j < 8; j++)
            {
                crc = (crc & 1) != 0
                    ? (crc >> 1) ^ 0x82F63B78
                    : crc >> 1;
            }
            table[i] = crc;
        }
        return table;
    }


    private static bool IsPowerOfTwo(int value) => (value & (value - 1)) == 0;


    private static int Log2(int value)
    {
        int result = 0;
        while (value > 1)
        {
            value >>= 1;
            result++;
        }
        return result;
    }


    private static long RoundUpPowerOfTwo(long value)
    {
        long power = 8;
        while (power < value)
        {
            power <<= 1;
        }
        return power;
    }


    private static void WriteU16(Span<byte> span, int offset, ushort value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(span[offset..], value);


    private static void WriteU32(Span<byte> span, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(span[offset..], value);


    private static void WriteU16(byte[] bytes, int offset, ushort value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset, 2), value);


    private static void WriteU32(byte[] bytes, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);
}