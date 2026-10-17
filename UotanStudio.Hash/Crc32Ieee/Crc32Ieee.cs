namespace UotanStudio.Hash.Crc32Ieee;

/// <summary>
/// Standard CRC-32 (IEEE 802.3: reflected polynomial <c>0xEDB88320</c>, initial register <c>0xFFFF_FFFF</c> and a
/// final complement), which is the form ZIP entries and central directories, GPT headers and entry arrays, sparse
/// image chunks, XZ block checks, LZOP frames and zucchini patches store. It forwards the hardware-accelerated
/// implementation so every format in the product reads the same value.
/// <para>标准 CRC-32（IEEE 802.3：反射多项式 <c>0xEDB88320</c>、寄存器初值 <c>0xFFFF_FFFF</c> 并做最终取反），
/// 即 ZIP 条目与目录、GPT 头与条目数组、sparse 镜像块、XZ 块校验、LZOP 帧、zucchini 补丁所存储的形式。
/// 本类型转发硬件加速实现，使产品中每种格式读到的数值一致。</para>
/// </summary>
/// <remarks>
/// Use <see cref="Crc32Register.Crc32Register"/> when a format needs a non-default register seed or
/// must skip the final complement; the two families differ by exactly one bitwise complement and are separate types
/// on purpose.
/// <para>格式需要非默认寄存器初值或要求跳过末异或时，改用
/// <see cref="Crc32Register.Crc32Register"/>；两族恰好差一次按位取反，因此刻意拆成两个类型。</para>
/// </remarks>
public static class Crc32Ieee
{
    /// <summary>
    /// Computes the standard CRC-32 of the data.
    /// <para>计算数据的标准 CRC-32。</para>
    /// </summary>
    /// <param name="data">
    /// Bytes to checksum; an empty span returns <c>0</c>.
    /// <para>参与校验的字节；空区间返回 <c>0</c>。</para>
    /// </param>
    /// <returns>
    /// The checksum as stored by the formats listed in the summary. <para>摘要值，即上述格式所存储的数值。</para>
    /// </returns>
    public static uint Compute(ReadOnlySpan<byte> data) => System.IO.Hashing.Crc32.HashToUInt32(data);
}