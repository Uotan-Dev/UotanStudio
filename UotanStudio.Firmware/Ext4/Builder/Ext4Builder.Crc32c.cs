using System.Buffers.Binary;

namespace UotanStudio.Firmware.Ext4;

public sealed partial class Ext4Builder
{
    // CRC32c lookup table (reflected, poly 0x82F63B78)
    private static readonly uint[] Crc32cTable = InitCrc32cTable();

    /// <summary>
    /// 计算数据的 CRC32c 校验值（默认种子 0xFFFFFFFF）。
    /// Computes the CRC32c checksum of the given data with the default seed 0xFFFFFFFF.
    /// </summary>
    /// <param name="data">要计算校验值的数据 / The data to compute the checksum for.</param>
    /// <returns>CRC32c 校验值 / The CRC32c checksum.</returns>
    private static uint ComputeCrc32(ReadOnlySpan<byte> data) => ComputeCrc32(data, 0xFFFFFFFF);

    /// <summary>
    /// 使用指定种子计算数据的 CRC32c 校验值。
    /// Computes the CRC32c checksum of the given data with the specified seed.
    /// </summary>
    /// <param name="data">要计算校验值的数据 / The data to compute the checksum for.</param>
    /// <param name="seed">CRC 种子值 / The CRC seed value.</param>
    /// <returns>CRC32c 校验值 / The CRC32c checksum.</returns>
    private static uint ComputeCrc32(ReadOnlySpan<byte> data, uint seed)
    {
        uint crc = seed;
        foreach (byte b in data)
        {
            crc = Crc32cTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }
        return crc;
    }

    // Continue CRC32c computation from a previous result.
    private static uint ComputeCrc32Continue(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
        {
            crc = Crc32cTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }
        return crc;
    }

    // Build reflected CRC32c table with polynomial 0x82F63B78.
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

    // Check if value is a power of two.
    private static bool IsPowerOfTwo(int value) => (value & (value - 1)) == 0;

    // Compute floor(log2(value)) bit-by-bit.
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

    // Round up to the next power of two, starting from 8.
    private static long RoundUpPowerOfTwo(long value)
    {
        long power = 8;
        while (power < value)
        {
            power <<= 1;
        }
        return power;
    }

    // Write a ushort into a Span<byte> at the given offset (little-endian).
    private static void WriteU16(Span<byte> span, int offset, ushort value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(span[offset..], value);

    // Write a uint into a Span<byte> at the given offset (little-endian).
    private static void WriteU32(Span<byte> span, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(span[offset..], value);

    // Write a ushort into a byte[] at the given offset (little-endian).
    private static void WriteU16(byte[] bytes, int offset, ushort value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset, 2), value);

    // Write a uint into a byte[] at the given offset (little-endian).
    private static void WriteU32(byte[] bytes, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);
}