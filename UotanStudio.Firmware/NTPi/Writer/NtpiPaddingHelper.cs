using System.Security.Cryptography;

namespace UotanStudio.Firmware.NTPi.Writer;

/// <summary>
/// <para>提供 NTPI 打包过程中的数据填充和随机字节生成工具方法。</para>
/// Provides utility methods for data padding and random byte generation during NTPI packing.
/// </summary>
internal static class NtpiPaddingHelper
{
    internal const int AesBlockSize = 16;

    /// <summary>
    /// <para>计算数据填充至 AES 块大小(16 字节)边界后的总大小。</para>
    /// Calculates the total size after padding data to the AES block size (16 bytes) boundary.
    /// </summary>
    public static int CalculatePaddedSize(int dataSize)
    {
        var remainder = dataSize % AesBlockSize;
        return remainder == 0 ? dataSize : dataSize + (AesBlockSize - remainder);
    }

    /// <summary>
    /// <para>以 long 计算数据填充至 AES 块大小(16 字节)边界后的总大小。</para>
    /// Calculates the total size after padding data to the AES block size (16 bytes) boundary using long arithmetic.
    /// </summary>
    public static long CalculatePaddedSize(long dataSize)
    {
        var remainder = dataSize % AesBlockSize;
        return remainder == 0 ? dataSize : dataSize + (AesBlockSize - remainder);
    }

    /// <summary>
    /// <para>使用零字节将数据填充至 AES 块大小(16 字节)边界,支持指定有效数据长度。</para>
    /// Pads data to the AES block size (16 bytes) boundary using zero bytes, supporting a specified valid data length.
    /// </summary>
    public static byte[] PadToBlockSize(byte[] data, int validLength)
    {
        var remainder = validLength % AesBlockSize;
        if (remainder == 0)
        {
            if (validLength == data.Length)
            {
                return data;
            }
            var result = new byte[validLength];
            Buffer.BlockCopy(data, 0, result, 0, validLength);
            return result;
        }

        var paddedLength = validLength + (AesBlockSize - remainder);
        if (data.Length >= paddedLength)
        {
            Array.Clear(data, validLength, paddedLength - validLength);
            if (paddedLength == data.Length)
            {
                return data;
            }
            var result = new byte[paddedLength];
            Buffer.BlockCopy(data, 0, result, 0, paddedLength);
            return result;
        }

        var padded = new byte[paddedLength];
        Buffer.BlockCopy(data, 0, padded, 0, validLength);
        return padded;
    }

    /// <summary>
    /// <para>生成指定长度的加密安全随机字节数组。</para>
    /// Generates a cryptographically secure random byte array of the specified length.
    /// </summary>
    public static byte[] GenerateRandomBytes(int count)
    {
        var bytes = new byte[count];
        RandomNumberGenerator.Fill(bytes);
        return bytes;
    }
}