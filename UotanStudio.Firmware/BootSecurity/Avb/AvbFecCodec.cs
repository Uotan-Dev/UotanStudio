using System.Buffers;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace UotanStudio.Firmware.BootSecurity.Avb;

/// <summary>Generates and compares Android AVB Reed-Solomon FEC parity over a raw partition prefix.
/// <para>对原始分区前缀生成并比较 Android AVB Reed-Solomon FEC 校验码。</para></summary>
/// <remarks>Uses 4096-byte interleaving and RS(255, 255 - roots) over GF(256) with polynomial 0x11d.
/// The output contains only parity, without any trailing footer block.
/// <para>使用 4096 字节交织与基于多项式 0x11d 的 GF(256) RS(255, 255 - roots)。
/// 输出只包含校验码，不含末尾 footer 块。</para></remarks>
internal static class AvbFecCodec
{
    private const int BlockSize = 4096;
    private const int ReadBufferSize = 64 * 1024;
    private const int CodewordSize = 255;
    private static readonly byte[] Exponents = CreateExponents();
    private static readonly byte[] Logarithms = CreateLogarithms();

    /// <summary>
    /// The lane width the nibble-split field multiply works on.
    /// <para>nibble-split 域乘法处理的通道宽度。</para>
    /// </summary>
    private const int VectorLane = 16;

    /// <summary>
    /// The mask that isolates one nibble of each lane.
    /// <para>隔离每个通道一个半字节的掩码。</para>
    /// </summary>
    private static readonly Vector128<byte> NibbleMask = Vector128.Create((byte)0x0F);

    /// <summary>
    /// Whether the processor lowers a 16-byte lane permute to a single instruction, which the nibble-split field multiply
    /// requires: <c>pshufb</c> under SSSE3 on x86 and <c>tbl</c> under AdvSIMD on ARM. Without it the portable permute
    /// degrades into software emulation that is slower than the scalar table walk, so the scalar path is kept.
    /// <para>处理器是否把 16 字节通道置换降为单条指令，nibble-split 域乘法依赖该能力：x86 上是 SSSE3 的 <c>pshufb</c>，
    /// ARM 上是 AdvSIMD 的 <c>tbl</c>。不具备时，可移植置换会退化为比标量查表更慢的软件模拟，因此保留标量路径。</para>
    /// </summary>
    private static bool CanVectorizeMultiply => Ssse3.IsSupported || AdvSimd.IsSupported;

    /// <summary>
    /// Builds the two 16-entry tables that multiply any byte by <paramref name="coefficient"/> through a nibble split.
    /// <para>构造把任意字节乘以 <paramref name="coefficient"/> 的两张 16 项 nibble 表。</para>
    /// </summary>
    /// <param name="coefficient">Field element the tables are built for; zero is rejected by the caller.
    /// <para>建表所用的域元素；零值由调用方排除。</para></param>
    /// <returns>The table for the low nibble and the table for the high nibble.
    /// <para>低半字节表与高半字节表。</para></returns>
    private static (Vector128<byte> Low, Vector128<byte> High) BuildMultiplyTables(byte coefficient)
    {
        Span<byte> low = stackalloc byte[VectorLane];
        Span<byte> high = stackalloc byte[VectorLane];
        for (var nibble = 0; nibble < VectorLane; nibble++)
        {
            low[nibble] = Multiply(coefficient, (byte)nibble);
            high[nibble] = Multiply(coefficient, (byte)(nibble << 4));
        }

        return (Vector128.Create(low), Vector128.Create(high));
    }

    /// <summary>
    /// Multiplies sixteen field elements by the constant folded into <paramref name="low"/> and <paramref name="high"/>.
    /// <para>把十六个域元素乘以折叠进 <paramref name="low"/> 与 <paramref name="high"/> 的常数。</para>
    /// </summary>
    /// <param name="value">Sixteen field elements. <para>十六个域元素。</para></param>
    /// <param name="low">Low-nibble product table. <para>低半字节积表。</para></param>
    /// <param name="high">High-nibble product table. <para>高半字节积表。</para></param>
    /// <returns>The per-lane products. <para>逐通道的积。</para></returns>
    private static Vector128<byte> MultiplyVector(Vector128<byte> value, Vector128<byte> low, Vector128<byte> high)
    {
        var lowNibble = value & NibbleMask;
        var highNibble = Vector128.ShiftRightLogical(value.AsUInt16(), 4).AsByte() & NibbleMask;
        return Vector128.Shuffle(low, lowNibble) ^ Vector128.Shuffle(high, highNibble);
    }

    /// <summary>
    /// XORs <paramref name="source"/> scaled by <paramref name="coefficient"/> into <paramref name="destination"/>,
    /// vectorized when the processor can permute a 16-byte lane in one instruction.
    /// <para>把按 <paramref name="coefficient"/> 缩放的 <paramref name="source"/> 异或进 <paramref name="destination"/>；
    /// 处理器能以单条指令置换 16 字节通道时向量化执行。</para>
    /// </summary>
    /// <remarks>
    /// A zero coefficient leaves the destination untouched, which reproduces the scalar loop's short circuit on a zero
    /// source byte because the field product is zero either way. Field addition is associative and commutative, so
    /// accumulating a row across several calls cannot change the result.
    /// <para>系数为零时不改动目标，这与标量循环对零源字节的短路等价，因为两种情况下的域乘积都是零。域加法满足结合律与
    /// 交换律，因此分多次调用累加同一行不会改变结果。</para>
    /// </remarks>
    /// <param name="destination">Row the scaled source is XORed into; it must be at least as long as the source.
    /// <para>被异或的目标行；长度不得小于源。</para></param>
    /// <param name="source">Row of field elements to scale. <para>要缩放的域元素行。</para></param>
    /// <param name="coefficient">Field element every source byte is multiplied by.
    /// <para>源字节逐字节相乘的域元素。</para></param>
    internal static void XorMultiplyConstant(Span<byte> destination, ReadOnlySpan<byte> source, byte coefficient)
    {
        if (coefficient == 0 || source.IsEmpty)
        {
            return;
        }

        if (!CanVectorizeMultiply || source.Length < VectorLane)
        {
            XorMultiplyConstantScalar(destination, source, coefficient);
            return;
        }

        var (low, high) = BuildMultiplyTables(coefficient);
        var offset = 0;
        for (; offset <= source.Length - VectorLane; offset += VectorLane)
        {
            var product = MultiplyVector(Vector128.Create(source.Slice(offset, VectorLane)), low, high);
            var lane = destination.Slice(offset, VectorLane);
            (product ^ Vector128.Create(lane)).CopyTo(lane);
        }

        for (; offset < source.Length; offset++)
        {
            destination[offset] ^= Multiply(coefficient, source[offset]);
        }
    }

    /// <summary>
    /// The byte-at-a-time reference form of <see cref="XorMultiplyConstant(Span{byte}, ReadOnlySpan{byte}, byte)"/>,
    /// which is also its fallback when the processor cannot permute a lane in one instruction.
    /// <para><see cref="XorMultiplyConstant(Span{byte}, ReadOnlySpan{byte}, byte)"/> 的逐字节标量实现，同时是处理器无法
    /// 用单条指令置换通道时的回退路径。</para>
    /// </summary>
    /// <remarks>
    /// Exposed to the test assembly so both forms can be compared directly on machines that only ever execute one of them.
    /// <para>开放给测试程序集，使两种形态在只可能执行其中一条路径的机器上也能直接互相对比。</para>
    /// </remarks>
    /// <param name="destination">Row the scaled source is XORed into. <para>被异或的目标行。</para></param>
    /// <param name="source">Row of field elements to scale. <para>要缩放的域元素行。</para></param>
    /// <param name="coefficient">Field element every source byte is multiplied by.
    /// <para>源字节逐字节相乘的域元素。</para></param>
    internal static void XorMultiplyConstantScalar(Span<byte> destination, ReadOnlySpan<byte> source, byte coefficient)
    {
        if (coefficient == 0 || source.IsEmpty)
        {
            return;
        }

        for (var index = 0; index < source.Length; index++)
        {
            destination[index] ^= Multiply(coefficient, source[index]);
        }
    }

    /// <summary>Calculates the parity byte count without the standalone fec footer.
    /// <para>计算不包含独立 fec footer 的校验码字节数。</para></summary>
    /// <param name="coveredBytes">Number of 4096-byte-aligned source bytes preceding FEC.
    /// <para>FEC 之前按 4096 字节对齐的源字节数。</para></param>
    /// <param name="roots">Number of RS parity roots from 1 through 254. <para>1 至 254 的 RS 校验根数。</para></param>
    /// <returns>The exact descriptor FEC size. <para>描述符中精确的 FEC 长度。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException">The geometry is invalid. <para>布局参数无效。</para></exception>
    /// <exception cref="OverflowException">The parity size exceeds the addressable range. <para>校验码长度超过可表示范围。</para></exception>
    public static long CalculateParitySize(long coveredBytes, uint roots)
    {
        ValidateGeometry(coveredBytes, roots);
        var dataSymbols = CodewordSize - (int)roots;
        var blocks = coveredBytes / BlockSize;
        var rounds = checked((blocks + dataSymbols - 1) / dataSymbols);
        return checked(rounds * roots * BlockSize);
    }

    /// <summary>Writes parity for an existing raw image prefix without closing either stream.
    /// <para>为现有原始镜像前缀写入校验码，且不关闭任一流。</para></summary>
    /// <param name="image">Readable, seekable source stream; its position is restored. <para>可读、可定位的源流；结束后恢复位置。</para></param>
    /// <param name="output">Writable parity destination, positioned by the caller. <para>调用方已定位的可写校验码目标。</para></param>
    /// <param name="coveredBytes">Source bytes protected by the parity. <para>受校验码保护的源字节数。</para></param>
    /// <param name="roots">Number of RS parity roots. <para>RS 校验根数。</para></param>
    /// <param name="cancellationToken">Cancels reads and encoding. <para>取消读取与编码。</para></param>
    /// <returns>A task that completes when all parity bytes have been written. <para>全部校验码写完时完成的任务。</para></returns>
    /// <exception cref="ArgumentException">A stream does not support the required operation. <para>流不支持所需操作。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">The geometry is invalid. <para>布局参数无效。</para></exception>
    /// <exception cref="EndOfStreamException">The source is shorter than declared. <para>源数据短于声明值。</para></exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested. <para>请求了取消。</para></exception>
    public static Task WriteParityAsync(Stream image, Stream output, long coveredBytes, uint roots,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanWrite)
        {
            throw new ArgumentException("The parity destination must be writable.", nameof(output));
        }
        if (ReferenceEquals(image, output))
        {
            throw new ArgumentException("Use separate source and parity destination streams.", nameof(output));
        }
        return ProcessAsync(image, coveredBytes, roots, async (parity, token) =>
        {
            await output.WriteAsync(parity, token).ConfigureAwait(false);
            return true;
        }, cancellationToken);
    }

    /// <summary>Compares every stored parity byte against independently recomputed parity.
    /// <para>逐字节比较存储的校验码与重新计算的校验码。</para></summary>
    /// <param name="image">Readable, seekable partition stream; its position is restored. <para>可读、可定位的分区流；结束后恢复位置。</para></param>
    /// <param name="fecOffset">Start of FEC, also the protected source length. <para>FEC 起点，亦为受保护源长度。</para></param>
    /// <param name="fecSize">Declared parity length without a standalone fec footer. <para>不含独立 fec footer 的校验码声明长度。</para></param>
    /// <param name="roots">Number of RS parity roots. <para>RS 校验根数。</para></param>
    /// <param name="cancellationToken">Cancels reads and comparison. <para>取消读取与比较。</para></param>
    /// <returns>True if all parity bytes match; false for a size or byte mismatch. <para>全部校验码匹配时为真，长度或字节不符时为假。</para></returns>
    /// <exception cref="ArgumentException">The source cannot read or seek. <para>源流不可读或不可定位。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">The geometry is invalid. <para>布局参数无效。</para></exception>
    /// <exception cref="EndOfStreamException">The declared parity cannot be read. <para>无法读完声明的校验码。</para></exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested. <para>请求了取消。</para></exception>
    public static async Task<bool> VerifyAsync(Stream image, long fecOffset, long fecSize, uint roots,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (!image.CanRead || !image.CanSeek)
        {
            throw new ArgumentException("The partition stream must be readable and seekable.", nameof(image));
        }
        var expectedSize = CalculateParitySize(fecOffset, roots);
        if (fecSize != expectedSize || fecOffset > image.Length || fecSize > image.Length - fecOffset)
        {
            return false;
        }
        var comparison = new byte[checked((int)Math.Min(expectedSize, BlockSize * (long)roots))];
        var position = fecOffset;
        return await ProcessAsync(image, fecOffset, roots, async (parity, token) =>
        {
            token.ThrowIfCancellationRequested();
            image.Position = position;
            await image.ReadExactlyAsync(comparison.AsMemory(0, parity.Length), token).ConfigureAwait(false);
            position += parity.Length;
            return parity.Span.SequenceEqual(comparison.AsSpan(0, parity.Length));
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Rebuilds whole 4096-byte blocks that are known to be damaged from intact FEC parity.
    /// <para>借助完好的 FEC 校验码重建已知损坏的整块 4096 字节数据。</para></summary>
    /// <remarks>Applies Reed-Solomon erasure decoding: the block list is the set of erasure positions and the parity is trusted. A round can repair at most <c>roots</c> erased blocks; damaged parity or too many erasures in one round aborts the rebuild and leaves every image byte untouched. No trailing footer block is produced or consumed.
    /// <para>采用 Reed-Solomon 删除译码：坏块列表即删除位置集合，校验码视为可信。单轮最多重建 <c>roots</c> 个删除块； 校验码本身损坏或某轮删除数超出上限时中止重建且不改动任何镜像字节。不生成也不消费末尾 footer 块。</para></remarks>
    /// <param name="image">Readable, writable, seekable image holding the data prefix and its trailing FEC parity; its position is restored.
    /// <para>可读、可写、可定位的镜像，包含数据前缀及其尾部 FEC 校验码；结束后恢复位置。</para></param>
    /// <param name="fecOffset">Start of FEC, also the protected source length.
    /// <para>FEC 起点，亦为受保护源长度。</para></param>
    /// <param name="fecSize">Declared parity length without a standalone fec footer.
    /// <para>不含独立 fec footer 的校验码声明长度。</para></param>
    /// <param name="roots">Number of RS parity roots.<para>RS 校验根数。</para></param>
    /// <param name="badBlocks">Zero-based 4096-byte block numbers inside the protected prefix to rebuild; duplicates are ignored.
    /// <para>受保护前缀内待重建的从零起算 4096 字节块编号；重复项忽略。</para></param>
    /// <param name="cancellationToken">Cancels reads, solves and writes.
    /// <para>取消读取、求解与写入。</para></param>
    /// <returns>True when every requested block was reconstructed and written; false when the erasures exceed the code's capacity.
    /// <para>全部请求块均重建并写入时为真；删除数超出码可纠正能力时为假。</para></returns>
    /// <exception cref="ArgumentException">The stream cannot read, write or seek, or a block number is out of range.
    /// <para>流不可读、不可写或不可定位，或块编号越界。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">The geometry is invalid or the parity size mismatches the declaration.
    /// <para>布局无效或校验码长度与声明不符。</para></exception>
    /// <exception cref="EndOfStreamException">Declared bytes cannot be read completely.
    /// <para>无法完整读取声明的字节。</para></exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.
    /// <para>请求了取消。</para></exception>
    public static async Task<bool> RepairAsync(Stream image, long fecOffset, long fecSize, uint roots,
        IReadOnlyCollection<long> badBlocks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(badBlocks);
        ArgumentNullException.ThrowIfNull(image);
        if (!image.CanRead || !image.CanWrite || !image.CanSeek)
        {
            throw new ArgumentException("The image stream must be readable, writable and seekable.", nameof(image));
        }
        if (CalculateParitySize(fecOffset, roots) != fecSize)
        {
            throw new ArgumentOutOfRangeException(nameof(fecSize), "The declared parity size does not match the geometry.");
        }
        var dataSymbols = CodewordSize - (int)roots;
        var blocks = fecOffset / BlockSize;
        if (badBlocks.Count == 0)
        {
            return false;
        }
        if (badBlocks.Any(block => block < 0 || block >= blocks))
        {
            throw new ArgumentOutOfRangeException(nameof(badBlocks), "Every repaired block must lie inside the protected prefix.");
        }
        var rounds = (blocks + dataSymbols - 1) / dataSymbols;
        var erasures = new HashSet<long>[rounds];
        foreach (var block in badBlocks)
        {
            var round = (int)(block % rounds);
            erasures[round] ??= [];
            _ = erasures[round].Add(block / rounds);
        }
        if (erasures.Any(set => set is not null && set.Count > roots))
        {
            return false;
        }
        var polynomial = CreateGenerator((int)roots);
        var basis = new byte[dataSymbols][];
        for (var symbol = 0; symbol < dataSymbols; symbol++)
        {
            var unit = new byte[dataSymbols];
            unit[symbol] = 1;
            var parity = new byte[roots];
            EncodeParity(unit, parity, polynomial, (int)roots);
            basis[symbol] = parity;
        }
        var badSet = badBlocks.ToHashSet();
        var originalPosition = image.Position;
        var repaired = new List<(long Offset, byte[] Data)>();
        try
        {
            var data = new byte[dataSymbols * BlockSize];
            var parity = new byte[(int)roots * BlockSize];
            for (var round = 0L; round < rounds; round++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var erasedSymbols = erasures[(int)round];
                if (erasedSymbols is null)
                {
                    continue;
                }
                var erased = new int[erasedSymbols.Count];
                var index = 0;
                foreach (var symbol in erasedSymbols)
                {
                    erased[index++] = (int)symbol;
                }
                Array.Sort(erased);
                for (var symbol = 0; symbol < dataSymbols; symbol++)
                {
                    var blockOffset = checked(((long)symbol * rounds + round) * BlockSize);
                    var target = data.AsMemory(symbol * BlockSize, BlockSize);
                    target.Span.Clear();
                    if (blockOffset < fecOffset && !badSet.Contains(blockOffset / BlockSize))
                    {
                        image.Position = blockOffset;
                        await image.ReadExactlyAsync(target, cancellationToken).ConfigureAwait(false);
                    }
                }
                var parityOffset = checked(fecOffset + round * (long)roots * BlockSize);
                image.Position = parityOffset;
                await image.ReadExactlyAsync(parity, cancellationToken).ConfigureAwait(false);
                var transforms = TryBuildErasureSolve(erased, basis, (int)roots, out var success);
                if (!success)
                {
                    return false;
                }
                var rootCount = (int)roots;
                var outputs = new byte[erased.Length][];
                for (var slot = 0; slot < erased.Length; slot++)
                {
                    outputs[slot] = new byte[BlockSize];
                }

                var residual = new byte[rootCount][];
                for (var root = 0; root < rootCount; root++)
                {
                    residual[root] = new byte[BlockSize];
                }

                for (var column = 0; column < BlockSize; column++)
                {
                    for (var root = 0; root < rootCount; root++)
                    {
                        residual[root][column] = parity[column * rootCount + root];
                    }
                }

                for (var symbol = 0; symbol < dataSymbols; symbol++)
                {
                    if (Array.IndexOf(erased, symbol) >= 0)
                    {
                        continue;
                    }

                    var source = data.AsSpan(symbol * BlockSize, BlockSize);
                    for (var root = 0; root < rootCount; root++)
                    {
                        XorMultiplyConstant(residual[root], source, basis[symbol][root]);
                    }
                }

                for (var slot = 0; slot < erased.Length; slot++)
                {
                    var destination = outputs[slot].AsSpan();
                    for (var root = 0; root < rootCount; root++)
                    {
                        XorMultiplyConstant(destination, residual[root], transforms[slot][root]);
                    }
                }

                if (erased.Length < rootCount)
                {
                    var check = new byte[BlockSize];
                    for (var root = erased.Length; root < rootCount; root++)
                    {
                        Array.Clear(check);
                        for (var source = 0; source < rootCount; source++)
                        {
                            XorMultiplyConstant(check, residual[source], transforms[root][source]);
                        }

                        if (check.AsSpan().IndexOfAnyExcept((byte)0) >= 0)
                        {
                            return false;
                        }
                    }
                }
                for (var slot = 0; slot < erased.Length; slot++)
                {
                    var blockOffset = checked(((long)erased[slot] * rounds + round) * BlockSize);
                    repaired.Add((blockOffset, outputs[slot]));
                }
            }
            foreach (var entry in repaired)
            {
                cancellationToken.ThrowIfCancellationRequested();
                image.Position = entry.Offset;
                await image.WriteAsync(entry.Data, cancellationToken).ConfigureAwait(false);
            }
            return true;
        }
        finally
        {
            image.Position = originalPosition;
        }
    }

    /// <summary>Reduces the parity-basis columns for the erased symbols into a row transform that yields their values.
    /// <para>将已删除符号的校验基列约简为可直接读出其取值的行变换。</para></summary>
    private static byte[][] TryBuildErasureSolve(int[] erased, byte[][] basis, int roots, out bool success)
    {
        var count = erased.Length;
        var matrix = new byte[roots][];
        var transforms = new byte[roots][];
        for (var row = 0; row < roots; row++)
        {
            matrix[row] = new byte[count];
            transforms[row] = new byte[roots];
            for (var slot = 0; slot < count; slot++)
            {
                matrix[row][slot] = basis[erased[slot]][row];
            }
            transforms[row][row] = 1;
        }
        for (var column = 0; column < count; column++)
        {
            var pivot = -1;
            for (var row = column; row < roots; row++)
            {
                if (matrix[row][column] != 0)
                {
                    pivot = row;
                    break;
                }
            }
            if (pivot < 0)
            {
                success = false;
                return [];
            }
            if (pivot != column)
            {
                (matrix[column], matrix[pivot]) = (matrix[pivot], matrix[column]);
                (transforms[column], transforms[pivot]) = (transforms[pivot], transforms[column]);
            }
            var inverse = Inverse(matrix[column][column]);
            for (var slot = 0; slot < count; slot++)
            {
                matrix[column][slot] = Multiply(matrix[column][slot], inverse);
            }
            for (var source = 0; source < roots; source++)
            {
                transforms[column][source] = Multiply(transforms[column][source], inverse);
            }
            for (var row = 0; row < roots; row++)
            {
                if (row == column)
                {
                    continue;
                }
                var factor = matrix[row][column];
                if (factor == 0)
                {
                    continue;
                }
                for (var slot = 0; slot < count; slot++)
                {
                    matrix[row][slot] ^= Multiply(factor, matrix[column][slot]);
                }
                for (var source = 0; source < roots; source++)
                {
                    transforms[row][source] ^= Multiply(factor, transforms[column][source]);
                }
            }
        }
        success = true;
        return transforms;
    }

    private static void EncodeParity(byte[] data, byte[] parity, byte[] polynomial, int roots)
    {
        var code = parity.AsSpan(0, roots);
        code.Clear();
        for (var symbol = 0; symbol < data.Length; symbol++)
        {
            var feedback = (byte)(data[symbol] ^ code[0]);
            for (var root = 0; root < code.Length - 1; root++)
            {
                code[root] = (byte)(code[root + 1] ^ Multiply(feedback, polynomial[root + 1]));
            }
            code[^1] = Multiply(feedback, polynomial[^1]);
        }
    }

    private static byte Inverse(byte value)
    {
        if (value == 0)
        {
            throw new ArgumentException("Only nonzero field elements have inverses.", nameof(value));
        }
        return Exponents[CodewordSize - Logarithms[value]];
    }

    private static async Task<bool> ProcessAsync(Stream image, long coveredBytes, uint roots,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task<bool>> consume, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (!image.CanRead || !image.CanSeek)
        {
            throw new ArgumentException("The source must be readable and seekable.", nameof(image));
        }
        var parityBytes = CalculateParitySize(coveredBytes, roots);
        cancellationToken.ThrowIfCancellationRequested();
        var dataSymbols = CodewordSize - (int)roots;
        var rounds = (coveredBytes / BlockSize + dataSymbols - 1) / dataSymbols;
        var originalPosition = image.Position;
        try
        {
            if (parityBytes > Core.FirmwareParallelism.WorkerMemoryBudgetBytes)
            {
                return await ProcessColumnMajorAsync(image, coveredBytes, roots, rounds, consume, cancellationToken).ConfigureAwait(false);
            }

            return await ProcessRowMajorAsync(image, coveredBytes, roots, rounds, parityBytes, consume, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            image.Position = originalPosition;
        }
    }

    /// <summary>
    /// Encodes parity in a single row-major pass: data blocks are read sequentially in bounded batches and accumulated
    /// into their round's parity rows with the vectorized field multiply, then each round is emitted in interleaved order.
    /// <para>按行优先单遍编码校验码：按有界批次顺序读取数据块，用向量化域乘法累加到所属轮次的校验行，再按交织顺序输出每轮校验码。</para>
    /// </summary>
    private static async Task<bool> ProcessRowMajorAsync(Stream image, long coveredBytes, uint roots, long rounds, long parityBytes,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task<bool>> consume, CancellationToken cancellationToken)
    {
        var rootCount = (int)roots;
        var roundCount = checked((int)rounds);
        var dataSymbols = CodewordSize - rootCount;
        var coefficients = CreateParityCoefficients(dataSymbols, CreateGenerator(rootCount), rootCount);
        var parity = ArrayPool<byte>.Shared.Rent(checked((int)parityBytes));
        var roundParity = ArrayPool<byte>.Shared.Rent(rootCount * BlockSize);
        var blocks = coveredBytes / BlockSize;
        var blocksPerRead = (int)Math.Min(blocks, ReadBufferSize / BlockSize);
        var readBuffer = ArrayPool<byte>.Shared.Rent(blocksPerRead * BlockSize);
        try
        {
            parity.AsSpan(0, (int)parityBytes).Clear();
            image.Position = 0;
            var processed = 0L;
            while (processed < blocks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = (int)Math.Min(blocksPerRead, blocks - processed);
                await image.ReadExactlyAsync(readBuffer.AsMemory(0, count * BlockSize), cancellationToken).ConfigureAwait(false);
                for (var slot = 0; slot < count; slot++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var blockIndex = processed + slot;
                    var symbol = (int)(blockIndex / rounds);
                    var round = (int)(blockIndex % rounds);
                    var coefficient = coefficients[symbol];
                    var rowOffset = (int)((long)round * rootCount * BlockSize);
                    var block = readBuffer.AsSpan(slot * BlockSize, BlockSize);
                    for (var root = 0; root < rootCount; root++)
                    {
                        XorMultiplyConstant(parity.AsSpan(rowOffset + (root * BlockSize), BlockSize), block, coefficient[root]);
                    }
                }

                processed += count;
            }

            var roundBytes = rootCount * BlockSize;
            for (var round = 0; round < roundCount; round++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var roundOffset = round * roundBytes;
                for (var column = 0; column < BlockSize; column++)
                {
                    for (var root = 0; root < rootCount; root++)
                    {
                        roundParity[(column * rootCount) + root] = parity[roundOffset + (root * BlockSize) + column];
                    }
                }

                if (!await consume(roundParity.AsMemory(0, roundBytes), cancellationToken).ConfigureAwait(false))
                {
                    return false;
                }
            }

            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(readBuffer);
            ArrayPool<byte>.Shared.Return(roundParity);
            ArrayPool<byte>.Shared.Return(parity);
        }
    }

    /// <summary>
    /// Encodes parity round by round for geometries whose complete parity state exceeds the worker memory budget.
    /// <para>当完整校验状态超出工作内存预算时，按轮次逐列编码校验码。</para>
    /// </summary>
    private static async Task<bool> ProcessColumnMajorAsync(Stream image, long coveredBytes, uint roots, long rounds,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task<bool>> consume, CancellationToken cancellationToken)
    {
        var dataSymbols = CodewordSize - (int)roots;
        var data = new byte[dataSymbols * BlockSize];
        var parity = new byte[(int)roots * BlockSize];
        var polynomial = CreateGenerator((int)roots);
        for (var round = 0L; round < rounds; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var symbol = 0; symbol < dataSymbols; symbol++)
            {
                var blockOffset = checked(((long)symbol * rounds + round) * BlockSize);
                var block = data.AsMemory(symbol * BlockSize, BlockSize);
                if (blockOffset >= coveredBytes)
                {
                    block.Span.Clear();
                    continue;
                }
                image.Position = blockOffset;
                await image.ReadExactlyAsync(block, cancellationToken).ConfigureAwait(false);
            }
            for (var index = 0; index < BlockSize; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var code = parity.AsSpan(index * (int)roots, (int)roots);
                code.Clear();
                for (var symbol = 0; symbol < dataSymbols; symbol++)
                {
                    var feedback = (byte)(data[symbol * BlockSize + index] ^ code[0]);
                    for (var root = 0; root < code.Length - 1; root++)
                    {
                        code[root] = (byte)(code[root + 1] ^ Multiply(feedback, polynomial[root + 1]));
                    }
                    code[^1] = Multiply(feedback, polynomial[^1]);
                }
            }
            if (!await consume(parity, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Computes one field coefficient per data symbol and parity root from the scalar generator, so a block can be
    /// accumulated into every parity row with the vectorized multiply.
    /// <para>用标量生成式算出每个数据符号与校验根对应的域系数，使数据块可通过向量化乘法累加到各校验行。</para>
    /// </summary>
    private static byte[][] CreateParityCoefficients(int dataSymbols, byte[] polynomial, int roots)
    {
        var coefficients = new byte[dataSymbols][];
        for (var symbol = 0; symbol < dataSymbols; symbol++)
        {
            var unit = new byte[dataSymbols];
            unit[symbol] = 1;
            var parity = new byte[roots];
            EncodeParity(unit, parity, polynomial, roots);
            coefficients[symbol] = parity;
        }

        return coefficients;
    }

    private static void ValidateGeometry(long coveredBytes, uint roots)
    {
        if (coveredBytes <= 0 || coveredBytes % BlockSize != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(coveredBytes), "FEC requires nonempty 4096-byte-aligned input.");
        }
        if (roots is 0 or >= CodewordSize)
        {
            throw new ArgumentOutOfRangeException(nameof(roots), "FEC roots must be between 1 and 254.");
        }
    }

    private static byte[] CreateGenerator(int roots)
    {
        byte[] polynomial = [1];
        for (var root = 0; root < roots; root++)
        {
            var next = new byte[polynomial.Length + 1];
            for (var index = 0; index < polynomial.Length; index++)
            {
                next[index] ^= polynomial[index];
                next[index + 1] ^= Multiply(polynomial[index], Exponents[root]);
            }
            polynomial = next;
        }
        return polynomial;
    }

    private static byte Multiply(byte left, byte right)
        => left == 0 || right == 0 ? (byte)0 : Exponents[Logarithms[left] + Logarithms[right]];

    private static byte[] CreateExponents()
    {
        var values = new byte[512];
        var value = 1;
        for (var index = 0; index < 255; index++)
        {
            values[index] = (byte)value;
            value <<= 1;
            if ((value & 256) != 0)
            {
                value ^= 0x11d;
            }
        }
        for (var index = 255; index < values.Length; index++)
        {
            values[index] = values[index - 255];
        }
        return values;
    }

    private static byte[] CreateLogarithms()
    {
        var values = new byte[256];
        for (var index = 0; index < 255; index++)
        {
            values[Exponents[index]] = (byte)index;
        }
        return values;
    }
}