using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.Intrinsics.Arm;

namespace UotanStudio.Hash.Crc32Register;

/// <summary>
/// Reflected CRC-32 (polynomial <c>0xEDB88320</c>) with register seed semantics and no final XOR, so non-standard
/// initial values such as the F2FS superblock magic seed stay expressible.
/// <see cref="Crc32Ieee.Crc32Ieee"/> covers the IEEE 802.3 form instead; the two differ by exactly
/// one complement: <c>Crc32Ieee.Compute(data) == ~Compute(data, 0xFFFF_FFFF)</c>.
/// <para>反射式 CRC-32（多项式 <c>0xEDB88320</c>），寄存器 seed 语义且无最终异或，因此可以表达 F2FS 超级块魔数种子
/// 这类非标准初值。IEEE 802.3 标准式由 <see cref="Crc32Ieee.Crc32Ieee"/> 承担，两者只差一次取反：
/// <c>Crc32Ieee.Compute(data) == ~Compute(data, 0xFFFF_FFFF)</c>。</para>
/// </summary>
/// <remarks>
/// <para>On ARM64 the whole span, including every non-standard seed, runs through the hardware <c>crc32</c> extension,
/// whose reflected-IEEE instruction follows exactly this register convention. On other processors a fresh
/// <c>0xFFFF_FFFF</c> seed of at least 512 bytes is complemented and forwarded to the hardware-accelerated
/// <c>System.IO.Hashing.Crc32</c>, and a continued register (any other seed) of at least 128 KiB reaches the same
/// hardware path through the linearity of the CRC update: <c>C(seed, data) == ~Crc32(data) ^ C(~seed, zeros^len)</c>,
/// where the zero-run term advances the complemented seed through a 32×32 GF(2) matrix power cached by length.
/// Everything else keeps the eight-table loop that consumes eight bytes per iteration (slice-by-8) and falls back to
/// the byte-at-a-time table for the trailing remainder, so the register semantics stay identical for every length
/// and seed.</para>
/// <para>ARM64 上整段数据（含任意非标准 seed）都走硬件 <c>crc32</c> 扩展，该反射 IEEE 指令恰好遵循本寄存器约定。
/// 其他处理器上，初值 <c>0xFFFF_FFFF</c> 且长度不少于 512 字节的区间先取反再转发给硬件加速的
/// <c>System.IO.Hashing.Crc32</c>；其余 seed 的续算只要达到 128 KiB 也经 CRC 更新的线性性到达同一硬件路径：
/// <c>C(seed, data) == ~Crc32(data) ^ C(~seed, zeros^len)</c>，其中零串项把取反后的 seed 经 32×32 GF(2)
/// 矩阵幂推进（按长度缓存）。其余情况仍走 8 张 256 项表的热循环（每轮消费 8 字节，slice-by-8），
/// 尾部余数字节回退到逐字节查表，因此任意长度与初值下的寄存器语义完全一致。</para>
/// </remarks>
public static class Crc32Register
{
    private const uint Polynomial = 0xEDB88320;
    private const uint AllOnesSeed = 0xFFFF_FFFF;
    private const int HardwareFastPathMinimumLength = 512;
    private const int HardwareContinuationMinimumLength = 128 * 1024;
    private const int SliceSize = 8;
    private const int TableEntries = 256;
    private const int SliceTableEntries = TableEntries * SliceSize;
    private const int MatrixRows = 32;

    private static readonly uint[] Table = InitTable();
    private static readonly uint[] SliceTable = InitSliceTable();

    private static uint[]? _cachedZeroMatrix;
    private static long _cachedZeroMatrixLength;

    /// <summary>Computes the CRC-32 of the data starting from <paramref name="seed"/> (register semantics, no final XOR).
    /// <para>以 <paramref name="seed"/> 为寄存器初值计算数据的 CRC-32（寄存器语义，无最终异或）。</para></summary>
    /// <param name="data">Bytes to hash; an empty span returns <paramref name="seed"/> unchanged.
    /// <para>参与计算的字节；空区间原样返回 <paramref name="seed"/>。</para></param>
    /// <param name="seed">Register initial value, such as <c>0xFFFF_FFFF</c> or a format-specific constant.
    /// <para>寄存器初值，例如 <c>0xFFFF_FFFF</c> 或格式专属常量。</para></param>
    /// <returns>The CRC-32 register value after consuming the data.
    /// <para>消费完数据后的 CRC-32 寄存器值。</para></returns>
    public static uint Compute(ReadOnlySpan<byte> data, uint seed)
    {
        if (Crc32.IsSupported)
        {
            return ComputeArm(data, seed);
        }

        if (seed == AllOnesSeed && data.Length >= HardwareFastPathMinimumLength)
        {
            return ~System.IO.Hashing.Crc32.HashToUInt32(data);
        }

        if (data.Length >= HardwareContinuationMinimumLength)
        {
            return ~System.IO.Hashing.Crc32.HashToUInt32(data) ^ AdvanceOverZeros(~seed, data.Length);
        }

        return ComputeSoftware(data, seed);
    }

    /// <summary>
    /// Consumes the data through the ARM64 CRC extension, whose reflected-IEEE <c>crc32</c> instruction implements
    /// exactly this register semantics: the seed is the register value and no final XOR is applied.
    /// <para>经 ARM64 CRC 扩展消费数据，其反射 IEEE <c>crc32</c> 指令恰好实现本寄存器语义：seed 即寄存器值，
    /// 且无最终异或。</para>
    /// </summary>
    /// <param name="data">Bytes to consume. <para>要消费的字节。</para></param>
    /// <param name="seed">Register initial value carried through unchanged. <para>原样带入的寄存器初值。</para></param>
    /// <returns>The updated register value. <para>更新后的寄存器值。</para></returns>
    private static uint ComputeArm(ReadOnlySpan<byte> data, uint seed)
    {
        var crc = seed;
        while (data.Length >= sizeof(ulong))
        {
            var low = BinaryPrimitives.ReadUInt32LittleEndian(data);
            var high = BinaryPrimitives.ReadUInt32LittleEndian(data[sizeof(uint)..]);
            crc = Crc32.ComputeCrc32(crc, low);
            crc = Crc32.ComputeCrc32(crc, high);
            data = data[sizeof(ulong)..];
        }

        while (data.Length >= sizeof(uint))
        {
            crc = Crc32.ComputeCrc32(crc, BinaryPrimitives.ReadUInt32LittleEndian(data));
            data = data[sizeof(uint)..];
        }

        foreach (var b in data)
        {
            crc = Crc32.ComputeCrc32(crc, b);
        }

        return crc;
    }

    /// <summary>
    /// The portable slice-by-8 table walk used whenever no hardware path applies; it produces the same register
    /// values as the hardware paths for every length and seed.
    /// <para>无可用硬件路径时使用的可移植 slice-by-8 查表实现；对任意长度与 seed 都产出与硬件路径相同的寄存器值。</para>
    /// </summary>
    /// <param name="data">Bytes to consume. <para>要消费的字节。</para></param>
    /// <param name="seed">Register initial value. <para>寄存器初值。</para></param>
    /// <returns>The updated register value. <para>更新后的寄存器值。</para></returns>
    internal static uint ComputeSoftware(ReadOnlySpan<byte> data, uint seed)
    {
        var crc = seed;
        var slice = SliceTable;
        while (data.Length >= SliceSize)
        {
            var head = BinaryPrimitives.ReadUInt32LittleEndian(data);
            var tail = BinaryPrimitives.ReadUInt32LittleEndian(data[sizeof(uint)..]);
            var mixed = crc ^ head;
            var m0 = (byte)mixed;
            var m1 = (byte)(mixed >> 8);
            var m2 = (byte)(mixed >> 16);
            var m3 = (byte)(mixed >> 24);
            var n0 = (byte)tail;
            var n1 = (byte)(tail >> 8);
            var n2 = (byte)(tail >> 16);
            var n3 = (byte)(tail >> 24);
            crc = slice[0x700 + m0]
                ^ slice[0x600 + m1]
                ^ slice[0x500 + m2]
                ^ slice[0x400 + m3]
                ^ slice[0x300 + n0]
                ^ slice[0x200 + n1]
                ^ slice[0x100 + n2]
                ^ slice[n3];
            data = data[SliceSize..];
        }

        foreach (var b in data)
        {
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    /// <summary>
    /// Advances a register over a run of zero bytes by applying the length-cached GF(2) matrix power, which is the
    /// linear half of the CRC update — the term that lets a continued seed reuse the hardware path.
    /// <para>通过按长度缓存的 GF(2) 矩阵幂把寄存器推进一段零字节，即 CRC 更新的线性部分——正是让续算 seed 复用
    /// 硬件路径的那一项。</para>
    /// </summary>
    /// <param name="value">Register value to advance. <para>待推进的寄存器值。</para></param>
    /// <param name="length">Number of zero bytes. <para>零字节的个数。</para></param>
    /// <returns>The register after consuming <paramref name="length"/> zero bytes.
    /// <para>消费 <paramref name="length"/> 个零字节后的寄存器值。</para></returns>
    private static uint AdvanceOverZeros(uint value, long length)
    {
        if (length == 0 || value == 0)
        {
            return value;
        }

        var matrix = ObtainZeroMatrix(length);
        var result = 0u;
        var bits = value;
        while (bits != 0)
        {
            result ^= matrix[BitOperations.TrailingZeroCount(bits)];
            bits &= bits - 1;
        }

        return result;
    }

    /// <summary>
    /// Returns the zero-run advance matrix for the given length, memoizing the most recent power because callers
    /// continue block-shaped data whose lengths repeat.
    /// <para>返回给定长度的零串推进矩阵并记忆最近一次结果，因为调用方按固定块长续算，长度高度重复。</para>
    /// </summary>
    /// <param name="length">Number of zero bytes; must be positive. <para>零字节的个数；必须为正。</para></param>
    /// <returns>The matrix whose row <c>i</c> is the register <c>1 &lt;&lt; i</c> after that many zero bytes.
    /// <para>行 <c>i</c> 为寄存器 <c>1 &lt;&lt; i</c> 推进相应零字节后的矩阵。</para></returns>
    private static uint[] ObtainZeroMatrix(long length)
    {
        var cached = _cachedZeroMatrix;
        if (cached is not null && Volatile.Read(ref _cachedZeroMatrixLength) == length)
        {
            return cached;
        }

        var matrix = BuildZeroMatrix(length);
        Volatile.Write(ref _cachedZeroMatrixLength, length);
        Volatile.Write(ref _cachedZeroMatrix, matrix);
        return matrix;
    }

    /// <summary>
    /// Raises the single-byte zero advance operator to <paramref name="length"/> by binary exponentiation of its
    /// 32×32 GF(2) matrix, so any run length costs only a logarithmic number of matrix products.
    /// <para>以 32×32 GF(2) 矩阵的二进制快速幂把单字节零推进算子提升到 <paramref name="length"/> 次方，任意长度只需
    /// 对数级的矩阵乘法次数。</para>
    /// </summary>
    /// <param name="length">Number of zero bytes; must be positive. <para>零字节的个数；必须为正。</para></param>
    /// <returns>The composed advance matrix. <para>复合后的推进矩阵。</para></returns>
    private static uint[] BuildZeroMatrix(long length)
    {
        var result = IdentityMatrix();
        var factor = OneByteZeroAdvanceMatrix();
        while (length > 0)
        {
            if ((length & 1) != 0)
            {
                result = MatrixCompose(result, factor);
            }

            length >>= 1;
            if (length > 0)
            {
                factor = MatrixCompose(factor, factor);
            }
        }

        return result;
    }

    /// <summary>
    /// Builds the linear operator of one zero byte: row <c>i</c> is the table step applied to the register
    /// <c>1 &lt;&lt; i</c>.
    /// <para>构造推进一个零字节的线性算子：行 <c>i</c> 是寄存器 <c>1 &lt;&lt; i</c> 经查表步进后的结果。</para>
    /// </summary>
    /// <returns>The 32-row advance matrix. <para>32 行推进矩阵。</para></returns>
    private static uint[] OneByteZeroAdvanceMatrix()
    {
        var matrix = new uint[MatrixRows];
        for (var bit = 0; bit < MatrixRows; bit++)
        {
            var state = 1u << bit;
            matrix[bit] = Table[state & 0xFF] ^ (state >> 8);
        }

        return matrix;
    }

    /// <summary>
    /// Composes two advance operators represented as rows of basis images.
    /// <para>复合两个以基向量为行的推进算子。</para>
    /// </summary>
    /// <param name="first">Operator applied last. <para>后施加的算子。</para></param>
    /// <param name="second">Operator applied first. <para>先施加的算子。</para></param>
    /// <returns>The matrix of applying <paramref name="second"/> then <paramref name="first"/>.
    /// <para>先施加 <paramref name="second"/> 再施加 <paramref name="first"/> 的复合矩阵。</para></returns>
    private static uint[] MatrixCompose(uint[] first, uint[] second)
    {
        var composed = new uint[MatrixRows];
        for (var row = 0; row < MatrixRows; row++)
        {
            var acc = 0u;
            var bits = second[row];
            while (bits != 0)
            {
                acc ^= first[BitOperations.TrailingZeroCount(bits)];
                bits &= bits - 1;
            }

            composed[row] = acc;
        }

        return composed;
    }

    /// <summary>
    /// The identity advance operator. <para>单位推进算子。</para>
    /// </summary>
    /// <returns>The identity matrix. <para>单位矩阵。</para></returns>
    private static uint[] IdentityMatrix()
    {
        var matrix = new uint[MatrixRows];
        for (var bit = 0; bit < MatrixRows; bit++)
        {
            matrix[bit] = 1u << bit;
        }

        return matrix;
    }

    private static uint[] InitTable()
    {
        var table = new uint[TableEntries];
        for (uint i = 0; i < TableEntries; i++)
        {
            var crc = i;
            for (var j = 0; j < 8; j++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ Polynomial : crc >> 1;
            }

            table[i] = crc;
        }

        return table;
    }

    private static uint[] InitSliceTable()
    {
        var slice = new uint[SliceTableEntries];
        Table.CopyTo(slice, 0);
        for (var step = 1; step < SliceSize; step++)
        {
            var previous = (step - 1) * TableEntries;
            var current = step * TableEntries;
            for (var i = 0; i < TableEntries; i++)
            {
                var carry = slice[previous + i];
                slice[current + i] = (carry >> 8) ^ Table[carry & 0xFF];
            }
        }

        return slice;
    }
}