using UotanStudio.Firmware.Core;
using UotanStudio.Hash.Crc32c;

namespace UotanStudio.Firmware.FileSystem.Ext4.Builder;

/// <summary>
/// EXT4 builder part carrying the CRC32c entry points used by <c>metadata_csum</c> together with the little-endian write and power-of-two rounding helpers.
/// <para>EXT4 构建器的组成部分，提供 <c>metadata_csum</c> 使用的 CRC32c 计算入口，以及小端写出与 2 的幂取整辅助方法。</para>
/// </summary>
public sealed partial class Ext4Builder
{

    /// <summary>
    /// Computes the CRC32c checksum of the given data with the default seed 0xFFFFFFFF.
    /// <para>计算数据的 CRC32c 校验值（默认种子 0xFFFFFFFF）。</para>
    /// </summary>
    private static uint ComputeCrc32(ReadOnlySpan<byte> data) => Crc32c.Compute(data, Crc32c.InitialSeed);

    /// <summary>
    /// Computes the CRC32c checksum of the given data with the specified seed.
    /// <para>使用指定种子计算数据的 CRC32c 校验值。</para>
    /// </summary>
    private static uint ComputeCrc32(ReadOnlySpan<byte> data, uint seed) => Crc32c.Compute(data, seed);


    private static uint ComputeCrc32Continue(uint crc, ReadOnlySpan<byte> data) => Crc32c.Compute(data, crc);


    private static bool IsPowerOfTwo(int value) => (value & (value - 1)) == 0;


    private static int Log2(int value)
    {
        var result = 0;
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
        ByteWriter.WriteUInt16LE(span[offset..], value);


    private static void WriteU32(Span<byte> span, int offset, uint value) =>
        ByteWriter.WriteUInt32LE(span[offset..], value);


    private static void WriteU16(byte[] bytes, int offset, ushort value) =>
        ByteWriter.WriteUInt16LE(bytes, offset, value);


    private static void WriteU32(byte[] bytes, int offset, uint value) =>
        ByteWriter.WriteUInt32LE(bytes, offset, value);
}