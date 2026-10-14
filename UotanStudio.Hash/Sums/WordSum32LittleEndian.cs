using System.Buffers.Binary;

namespace UotanStudio.Hash.Sums;

/// <summary>
/// Additive checksum that folds aligned little-endian doublewords first and then any remaining trailing bytes
/// individually, wrapping modulo 2^32 — the preloader XFlash command checksum, which the host writes big-endian onto
/// the wire after computing this value.
/// <para>先把对齐的小端双字累加、再把剩余尾部字节逐个累加的求和校验和，按 mod 2^32 回卷——即 preloader XFlash
/// 命令校验和；主机算出该值后以大端写到线上。</para>
/// </summary>
/// <remarks>
/// The tail rule is part of the protocol: a three-byte remainder contributes three byte values, not one padded
/// doubleword, so this is not a word sum over a zero-padded buffer.
/// <para>尾部规则属于协议本身：不足 4 字节的余数贡献的是逐字节数值，而不是补齐后的一个双字，
/// 因此这不是“对零填充缓冲区求双字和”。</para>
/// </remarks>
public static class WordSum32LittleEndian
{
    /// <summary>
    /// Sums the little-endian doublewords of the data plus any trailing bytes.
    /// <para>累加数据中的小端双字，并加上尾部剩余字节。</para>
    /// </summary>
    /// <param name="data">
    /// Bytes to sum; a span shorter than four bytes contributes only its byte values.
    /// <para>待累加的字节；少于 4 字节的区间只按字节值累加。</para>
    /// </param>
    /// <returns>
    /// The checksum modulo 2^32; an empty span returns <c>0</c>. <para>mod 2^32 的校验和；空区间返回 <c>0</c>。</para>
    /// </returns>
    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        var offset = 0;
        for (; offset + sizeof(uint) <= data.Length; offset += sizeof(uint))
        {
            sum += BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
        }

        for (; offset < data.Length; offset++)
        {
            sum += data[offset];
        }

        return sum;
    }
}