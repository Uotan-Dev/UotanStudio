using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;

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
/// <para>On little-endian processors the doublewords are summed <see cref="Vector{T}"/> lanes at a time, which is the same
/// modular addition the byte-at-a-time path performs; other byte orders keep the portable path because a native
/// doubleword read there would not be a little-endian read.</para>
/// <para>小端处理器上双字按 <see cref="Vector{T}"/> 通道并行累加，与逐字节路径做的是同一个模加；其他字节序保留可移植
/// 路径，因为在那些平台上按本机字节序读双字不等于按小端读。</para>
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
        => BitConverter.IsLittleEndian ? ComputeLittleEndian(data) : ComputePortable(data);

    /// <summary>
    /// The portable form, which reads every doubleword explicitly as little-endian and is therefore correct on any
    /// processor byte order.
    /// <para>可移植形态：显式按小端读取每个双字，因此在任意处理器字节序上都正确。</para>
    /// </summary>
    /// <param name="data">Bytes to sum. <para>待累加的字节。</para></param>
    /// <returns>The checksum modulo 2^32. <para>mod 2^32 的校验和。</para></returns>
    internal static uint ComputePortable(ReadOnlySpan<byte> data)
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

    /// <summary>
    /// The little-endian form, which reinterprets the bytes as native doublewords and accumulates whole
    /// <see cref="Vector{T}"/> lanes before finishing the remaining doublewords and trailing bytes scalarly.
    /// <para>小端形态：把字节重组为本机双字，先按整条 <see cref="Vector{T}"/> 通道累加，再以标量收尾剩余双字与尾部字节。</para>
    /// </summary>
    /// <param name="data">Bytes to sum. <para>待累加的字节。</para></param>
    /// <returns>The checksum modulo 2^32. <para>mod 2^32 的校验和。</para></returns>
    internal static uint ComputeLittleEndian(ReadOnlySpan<byte> data)
    {
        var words = MemoryMarshal.Cast<byte, uint>(data);
        uint sum = 0;
        var index = 0;
        if (Vector.IsHardwareAccelerated && words.Length >= Vector<uint>.Count)
        {
            var accumulator = Vector<uint>.Zero;
            var limit = words.Length - Vector<uint>.Count;
            while (index <= limit)
            {
                accumulator += new Vector<uint>(words.Slice(index, Vector<uint>.Count));
                index += Vector<uint>.Count;
            }

            for (var lane = 0; lane < Vector<uint>.Count; lane++)
            {
                sum += accumulator[lane];
            }
        }

        for (; index < words.Length; index++)
        {
            sum += words[index];
        }

        for (var tail = words.Length * sizeof(uint); tail < data.Length; tail++)
        {
            sum += data[tail];
        }

        return sum;
    }
}