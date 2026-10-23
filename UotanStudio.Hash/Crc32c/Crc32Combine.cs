using System.Runtime.CompilerServices;
using IeeeCrc32 = UotanStudio.Hash.Crc32Ieee.Crc32Ieee;

namespace UotanStudio.Hash.Crc32c;

/// <summary>
/// Combines standard IEEE 802.3 CRC-32 check values of adjacent byte ranges in logarithmic time by exponentiating
/// the GF(2) shift matrix of the reflected polynomial.
/// <para>通过反射多项式的 GF(2) 移位矩阵快速幂，以对数时间合并相邻字节区间的标准 IEEE 802.3 CRC-32 校验值。</para>
/// </summary>
/// <remarks>
/// Values follow <see cref="Crc32Ieee.Crc32Ieee"/> and <see cref="Crc32Ieee.Crc32IeeeAccumulator"/>: register
/// initial value <c>0xFFFF_FFFF</c> and a final complement, so a combined result is bit-identical to hashing the
/// concatenated bytes. Combination lets an expanded zero or repeated-fill run enter a streaming checksum without
/// materializing its bytes.
/// <para>数值遵循 <see cref="Crc32Ieee.Crc32Ieee"/> 与 <see cref="Crc32Ieee.Crc32IeeeAccumulator"/>：寄存器初值
/// <c>0xFFFF_FFFF</c> 并做最终取反，因此合并结果与逐字节哈希拼接后的数据完全一致。借助合并，展开后的零段或重复
/// 填充段无需真正生成字节即可进入流式校验和。</para>
/// </remarks>
public static class Crc32Combine
{
    private const uint IeeePolynomial = 0xEDB88320;
    private const int MatrixDimension = 32;

    /// <summary>
    /// Combines two IEEE CRC-32 check values covering adjacent byte ranges, such as two consecutive buffers of one
    /// streaming checksum.
    /// <para>合并覆盖相邻字节区间的两个 IEEE CRC-32 校验值，例如同一流式校验和的两个连续缓冲。</para>
    /// </summary>
    /// <param name="first">CRC-32 of the leading range; <c>0</c> represents an empty leading range.
    /// <para>前段区间的 CRC-32；<c>0</c> 表示前段为空。</para></param>
    /// <param name="second">CRC-32 of the following range; a single buffer can be hashed with <see cref="Crc32Ieee.Crc32Ieee"/>.
    /// <para>后段区间的 CRC-32；单个缓冲区可用 <see cref="Crc32Ieee.Crc32Ieee"/> 计算。</para></param>
    /// <param name="secondLength">Length in bytes of the following range; negative values are rejected.
    /// <para>后段区间的字节长度；负值会被拒绝。</para></param>
    /// <returns>The CRC-32 of both ranges concatenated in order, identical to hashing them in one pass.
    /// <para>两段数据按顺序拼接后的 CRC-32，与一次性哈希完全一致。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="secondLength"/> is negative.
    /// <para><paramref name="secondLength"/> 为负数。</para></exception>
    public static uint CombineIeee(uint first, uint second, long secondLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(secondLength);
        return AdvanceBytes(first, secondLength) ^ second;
    }

    /// <summary>
    /// Advances a running IEEE CRC-32 across a run of zero bytes in logarithmic time, as if every zero byte had been
    /// appended to the checksum.
    /// <para>以对数时间让运行中的 IEEE CRC-32 越过一段零字节，效果等同于把每个零字节都追加到校验和。</para>
    /// </summary>
    /// <param name="crc">CRC-32 of the bytes preceding the zero run; <c>0</c> represents an empty prefix.
    /// <para>零段之前字节的 CRC-32；<c>0</c> 表示前缀为空。</para></param>
    /// <param name="zeroBytes">Number of zero bytes to skip; negative values are rejected and zero returns
    /// <paramref name="crc"/> unchanged.
    /// <para>要越过的零字节数；负值会被拒绝，0 原样返回 <paramref name="crc"/>。</para></param>
    /// <returns>The CRC-32 of the prefix followed by <paramref name="zeroBytes"/> zero bytes.
    /// <para>前缀后接 <paramref name="zeroBytes"/> 个零字节的 CRC-32。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="zeroBytes"/> is negative.
    /// <para><paramref name="zeroBytes"/> 为负数。</para></exception>
    public static uint AdvanceIeee(uint crc, long zeroBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(zeroBytes);
        return CombineIeee(crc, ZeroBytesCrc(zeroBytes), zeroBytes);
    }

    /// <summary>
    /// Computes the IEEE CRC-32 of a run of zero bytes by doubling the affine zero-byte transformation, so the cost
    /// is logarithmic in the run length.
    /// <para>通过倍增零字节的仿射变换计算一段零字节的 IEEE CRC-32，成本随段长呈对数增长。</para>
    /// </summary>
    /// <param name="byteCount">The nonnegative run length in bytes. <para>非负的段长（字节）。</para></param>
    /// <returns>The CRC-32 of exactly <paramref name="byteCount"/> zero bytes, or <c>0</c> for an empty run.
    /// <para>恰好 <paramref name="byteCount"/> 个零字节的 CRC-32；空段为 <c>0</c>。</para></returns>
    private static uint ZeroBytesCrc(long byteCount)
    {
        if (byteCount <= 0)
        {
            return 0;
        }

        ReadOnlySpan<byte> oneZero = [0];
        var blockCrc = IeeeCrc32.Compute(oneZero);
        long blockLength = 1;
        var result = 0u;
        var remaining = byteCount;
        while (remaining > 0)
        {
            if ((remaining & 1) != 0)
            {
                result = CombineIeee(result, blockCrc, blockLength);
            }

            remaining >>= 1;
            if (remaining == 0)
            {
                break;
            }

            blockCrc = CombineIeee(blockCrc, blockCrc, blockLength);
            blockLength = checked(blockLength * 2);
        }

        return result;
    }

    /// <summary>
    /// Advances one finalized CRC-32 value by <paramref name="byteCount"/> zero bytes through binary exponentiation
    /// of the polynomial's shift matrix.
    /// <para>通过对多项式移位矩阵的快速幂，让一个定稿 CRC-32 值越过 <paramref name="byteCount"/> 个零字节。</para>
    /// </summary>
    /// <param name="crc">Finalized CRC-32 value before the zero run. <para>零段之前的定稿 CRC-32 值。</para></param>
    /// <param name="byteCount">Nonnegative number of zero bytes. <para>非负的零字节数。</para></param>
    /// <returns>The advanced CRC-32 value. <para>推进后的 CRC-32 值。</para></returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint AdvanceBytes(uint crc, long byteCount)
    {
        if (byteCount <= 0)
        {
            return crc;
        }

        Span<uint> odd = stackalloc uint[MatrixDimension];
        Span<uint> even = stackalloc uint[MatrixDimension];
        odd[0] = IeeePolynomial;
        var row = 1u;
        for (var index = 1; index < MatrixDimension; index++)
        {
            odd[index] = row;
            row <<= 1;
        }

        Square(even, odd);
        Square(odd, even);
        var remaining = (ulong)byteCount;
        do
        {
            Square(even, odd);
            if ((remaining & 1) != 0)
            {
                crc = Times(even, crc);
            }

            remaining >>= 1;
            if (remaining == 0)
            {
                break;
            }

            Square(odd, even);
            if ((remaining & 1) != 0)
            {
                crc = Times(odd, crc);
            }

            remaining >>= 1;
        }
        while (remaining != 0);

        return crc;
    }

    /// <summary>Squares one 32-bit linear operator. <para>对一个 32 位线性算子求平方。</para></summary>
    /// <param name="square">Destination matrix; may not alias <paramref name="matrix"/>.
    /// <para>目标矩阵；不得与 <paramref name="matrix"/> 重叠。</para></param>
    /// <param name="matrix">Source operator whose columns are basis images.
    /// <para>源算子，其各列是基向量的像。</para></param>
    private static void Square(Span<uint> square, ReadOnlySpan<uint> matrix)
    {
        for (var index = 0; index < MatrixDimension; index++)
        {
            square[index] = Times(matrix, matrix[index]);
        }
    }

    /// <summary>
    /// Multiplies a bit vector by one 32-bit GF(2) matrix stored column-wise.
    /// <para>把一个位向量与按列存储的 32 位 GF(2) 矩阵相乘。</para>
    /// </summary>
    /// <param name="matrix">Operator columns. <para>算子的各列。</para></param>
    /// <param name="vector">Input bit vector. <para>输入位向量。</para></param>
    /// <returns>The transformed bit vector. <para>变换后的位向量。</para></returns>
    private static uint Times(ReadOnlySpan<uint> matrix, uint vector)
    {
        var sum = 0u;
        var index = 0;
        while (vector != 0)
        {
            if ((vector & 1) != 0)
            {
                sum ^= matrix[index];
            }

            vector >>= 1;
            index++;
        }

        return sum;
    }
}