using System.Security.Cryptography;

namespace UotanStudio.Firmware.VendorFirmware.NTPi.Writer;

internal static class NtpiPaddingHelper
{
    internal const int AesBlockSize = 16;

    /// <summary>
    /// Calculates the total size after padding data to the AES block size (16 bytes) boundary.
    /// <para>计算数据填充至 AES 块大小(16 字节)边界后的总大小。</para>
    /// </summary>
    /// <param name="dataSize">
    /// Unpadded byte count; a value already on the 16-byte boundary is returned unchanged.
    /// <para>未填充的字节数；已经位于 16 字节边界时原样返回。</para>
    /// </param>
    /// <returns>
    /// <paramref name="dataSize"/> rounded up to the next multiple of <see cref="AesBlockSize"/>.
    /// <para><paramref name="dataSize"/> 向上取整到 <see cref="AesBlockSize"/> 的下一个倍数。</para>
    /// </returns>
    public static int CalculatePaddedSize(int dataSize)
    {
        var remainder = dataSize % AesBlockSize;
        return remainder == 0 ? dataSize : dataSize + (AesBlockSize - remainder);
    }

    /// <summary>
    /// Calculates the total size after padding data to the AES block size (16 bytes) boundary using long arithmetic.
    /// <para>以 long 计算数据填充至 AES 块大小(16 字节)边界后的总大小。</para>
    /// </summary>
    /// <param name="dataSize">
    /// Unpadded byte count as a 64-bit value; a value already on the 16-byte boundary is returned unchanged.
    /// <para>以 64 位值给出的未填充字节数；已经位于 16 字节边界时原样返回。</para>
    /// </param>
    /// <returns>
    /// <paramref name="dataSize"/> rounded up to the next multiple of <see cref="AesBlockSize"/>, without
    /// the 32-bit range limit of the other overload.
    /// <para><paramref name="dataSize"/> 向上取整到 <see cref="AesBlockSize"/> 的下一个倍数，不受另一重载的 32 位范围限制。</para>
    /// </returns>
    public static long CalculatePaddedSize(long dataSize)
    {
        var remainder = dataSize % AesBlockSize;
        return remainder == 0 ? dataSize : dataSize + (AesBlockSize - remainder);
    }

    /// <summary>
    /// Pads data to the AES block size (16 bytes) boundary using zero bytes, supporting a specified valid data length.
    /// <para>使用零字节将数据填充至 AES 块大小(16 字节)边界,支持指定有效数据长度。</para>
    /// </summary>
    /// <param name="data">
    /// Source bytes whose leading <paramref name="validLength"/> bytes hold the content; the region between the valid
    /// length and the padded length inside this array is zeroed in place when it is reused.
    /// <para>源字节数组，其前 <paramref name="validLength"/> 个字节为有效内容；当该数组被直接复用时，有效长度与补齐长度之间的字节会被就地清零。</para>
    /// </param>
    /// <param name="validLength">
    /// Number of meaningful leading bytes to keep, which is the length rounded up to the block boundary; it must not
    /// exceed <paramref name="data"/>.Length.
    /// <para>需要保留的有效前导字节数，即向上取整到块边界的长度；不得超过 <paramref name="data"/>.Length。</para>
    /// </param>
    /// <returns>
    /// An array whose length is <paramref name="validLength"/> rounded up to a multiple of 16, with the added bytes zero.
    /// It can be the <paramref name="data"/> instance itself when no reshaping is needed, so the caller must not assume a
    /// fresh buffer.
    /// <para>返回长度向上取整为 16 的倍数的数组，新增字节为零。当无需重新整形时，返回的就是 <paramref name="data"/> 实例本身，因此调用方不能假定拿到的是新缓冲。</para>
    /// </returns>
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
    /// Generates a cryptographically secure random byte array of the specified length.
    /// <para>生成指定长度的加密安全随机字节数组。</para>
    /// </summary>
    /// <param name="count">
    /// Number of random bytes to produce; zero yields an empty array and a negative value fails to allocate.
    /// <para>需要生成的随机字节数；为 0 时返回空数组，负值因无法分配而失败。</para>
    /// </param>
    /// <returns>
    /// A newly allocated array of exactly <paramref name="count"/> unpredictable bytes.
    /// <para>新分配的数组，恰好包含 <paramref name="count"/> 个不可预测的字节。</para>
    /// </returns>
    public static byte[] GenerateRandomBytes(int count)
    {
        var bytes = new byte[count];
        RandomNumberGenerator.Fill(bytes);
        return bytes;
    }
}