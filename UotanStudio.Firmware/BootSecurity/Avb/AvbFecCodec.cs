namespace UotanStudio.Firmware.BootSecurity.Avb;

/// <summary>Generates and compares Android AVB Reed-Solomon FEC parity over a raw partition prefix.
/// <para>对原始分区前缀生成并比较 Android AVB Reed-Solomon FEC 校验码。</para></summary>
/// <remarks>Uses 4096-byte interleaving and RS(255, 255 - roots) over GF(256) with polynomial 0x11d.
/// The output contains only parity, not the standalone fec tool's trailing footer block.
/// <para>使用 4096 字节交织与基于多项式 0x11d 的 GF(256) RS(255, 255 - roots)。
/// 输出只包含校验码，不包含独立 fec 工具附加的末尾 footer 块。</para></remarks>
internal static class AvbFecCodec
{
    private const int BlockSize = 4096;
    private const int CodewordSize = 255;
    private static readonly byte[] Exponents = CreateExponents();
    private static readonly byte[] Logarithms = CreateLogarithms();

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
    /// <remarks>Applies Reed-Solomon erasure decoding: the block list is the set of erasure positions and the parity is trusted.
    /// A round can repair at most <c>roots</c> erased blocks; damaged parity or too many erasures in one round aborts the rebuild
    /// and leaves every image byte untouched. The standalone <c>fec</c> tool's trailing footer block is not produced or consumed.
    /// <para>采用 Reed-Solomon 删除译码：坏块列表即删除位置集合，校验码视为可信。单轮最多重建 <c>roots</c> 个删除块；
    /// 校验码本身损坏或某轮删除数超出上限时中止重建且不改动任何镜像字节。不生成也不消费独立 <c>fec</c> 工具的末尾 footer 块。</para></remarks>
    /// <param name="image">Readable, writable, seekable image holding the data prefix and its trailing FEC parity; its position is restored.
    /// <para>可读、可写、可定位的镜像，包含数据前缀及其尾部 FEC 校验码；结束后恢复位置。</para></param>
    /// <param name="fecOffset">Start of FEC, also the protected source length. <para>FEC 起点，亦为受保护源长度。</para></param>
    /// <param name="fecSize">Declared parity length without a standalone fec footer. <para>不含独立 fec footer 的校验码声明长度。</para></param>
    /// <param name="roots">Number of RS parity roots. <para>RS 校验根数。</para></param>
    /// <param name="badBlocks">Zero-based 4096-byte block numbers inside the protected prefix to rebuild; duplicates are ignored.
    /// <para>受保护前缀内待重建的从零起算 4096 字节块编号；重复项忽略。</para></param>
    /// <param name="cancellationToken">Cancels reads, solves and writes. <para>取消读取、求解与写入。</para></param>
    /// <returns>True when every requested block was reconstructed and written; false when the erasures exceed the code's capacity.
    /// <para>全部请求块均重建并写入时为真；删除数超出码可纠正能力时为假。</para></returns>
    /// <exception cref="ArgumentException">The stream cannot read, write or seek, or a block number is out of range.
    /// <para>流不可读、不可写或不可定位，或块编号越界。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">The geometry is invalid or the parity size mismatches the declaration.
    /// <para>布局无效或校验码长度与声明不符。</para></exception>
    /// <exception cref="EndOfStreamException">Declared bytes cannot be read completely. <para>无法完整读取声明的字节。</para></exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested. <para>请求了取消。</para></exception>
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
                var outputs = new byte[erased.Length][];
                for (var slot = 0; slot < erased.Length; slot++)
                {
                    outputs[slot] = new byte[BlockSize];
                }
                var residual = new byte[roots];
                for (var column = 0; column < BlockSize; column++)
                {
                    for (var root = 0; root < roots; root++)
                    {
                        residual[root] = parity[column * (int)roots + root];
                    }
                    for (var symbol = 0; symbol < dataSymbols; symbol++)
                    {
                        if (Array.IndexOf(erased, symbol) >= 0)
                        {
                            continue;
                        }
                        var value = data[symbol * BlockSize + column];
                        if (value != 0)
                        {
                            for (var root = 0; root < roots; root++)
                            {
                                residual[root] ^= Multiply(value, basis[symbol][root]);
                            }
                        }
                    }
                    for (var slot = 0; slot < erased.Length; slot++)
                    {
                        byte sum = 0;
                        for (var root = 0; root < roots; root++)
                        {
                            sum ^= Multiply(transforms[slot][root], residual[root]);
                        }
                        outputs[slot][column] = sum;
                    }
                    for (var root = erased.Length; root < roots; root++)
                    {
                        byte check = 0;
                        for (var source = 0; source < roots; source++)
                        {
                            check ^= Multiply(transforms[root][source], residual[source]);
                        }
                        if (check != 0)
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
        _ = CalculateParitySize(coveredBytes, roots);
        cancellationToken.ThrowIfCancellationRequested();
        var originalPosition = image.Position;
        try
        {
            var dataSymbols = CodewordSize - (int)roots;
            var rounds = (coveredBytes / BlockSize + dataSymbols - 1) / dataSymbols;
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
        finally
        {
            image.Position = originalPosition;
        }
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