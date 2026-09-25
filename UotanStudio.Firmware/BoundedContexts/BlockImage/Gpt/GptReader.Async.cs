namespace UotanStudio.Firmware.BoundedContexts.BlockImage.Gpt;

/// <summary>
/// Fills an entire block-aligned buffer from the given absolute byte offset, or throws.
/// <para>从指定绝对字节偏移填满整个块对齐的缓冲区，否则抛出异常。</para>
/// </summary>
/// <param name="offset">
/// Absolute byte offset, always a multiple of the negotiated block size.
/// <para>绝对字节偏移，始终为协商块大小的整数倍。</para>
/// </param>
/// <param name="destination">
/// Block-aligned buffer to fill exactly; the reader must not keep the data and the caller must not retain the buffer after completion.
/// <para>需精确填满的块对齐缓冲区；读取方不得保留数据，调用方在完成后的缓冲区也不得继续持有。</para>
/// </param>
/// <param name="cancellationToken">
/// Cancellation token the implementation must honor.
/// <para>实现必须遵守的取消令牌。</para>
/// </param>
/// <remarks>The buffer must not be retained after completion. Reads are sequential, never concurrent.</remarks>
public delegate ValueTask GptBlockReader(long offset, Memory<byte> destination, CancellationToken cancellationToken);

/// <summary>
/// Read-only GPT pair validation; this part performs bounded asynchronous reads.
/// <para>只读的 GPT 主备校验；本部分实现有界的异步读取。</para>
/// </summary>
public static partial class GptReader
{
    /// <summary>
    /// Reads and validates a GPT pair using bounded asynchronous block reads.
    /// <para>使用有界异步块读取读取并校验 GPT 主备对。</para>
    /// </summary>
    /// <param name="readBlocks">
    /// Exact reader of the selected device address space; no ownership is transferred.
    /// <para>所选设备地址空间的精确读取委托；不转移任何所有权。</para>
    /// </param>
    /// <param name="blockSize">
    /// Explicit logical block size, a power of two from 512 through 65536.
    /// <para>显式逻辑块大小，须为 512 至 65536 之间 2 的幂。</para>
    /// </param>
    /// <param name="blockCount">
    /// Explicit capacity in blocks.
    /// <para>显式的块容量。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancellation propagated through every read.
    /// <para>贯穿每次读取传递的取消令牌。</para>
    /// </param>
    /// <returns>
    /// A metadata snapshot, not an atomic snapshot of externally changing storage.
    /// <para>元数据快照，而非对外部可变存储的原子快照。</para>
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The block size or block count is outside the supported range.
    /// <para>块大小或块数量超出支持范围。</para>
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// The signature, CRC, geometry, identities or entry arrays fail validation.
    /// <para>签名、CRC、几何、标识或条目数组未通过校验。</para>
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// The token was cancelled, including cancellation surfaced by <paramref name="readBlocks"/>.
    /// <para>令牌已被取消，含由 <paramref name="readBlocks"/> 抛出的取消。</para>
    /// </exception>
    public static async Task<GptTable> ReadAsync(GptBlockReader readBlocks, int blockSize, ulong blockCount,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(readBlocks);
        ValidateGeometry(blockSize, blockCount);
        cancellationToken.ThrowIfCancellationRequested();
        var header = new byte[blockSize];
        await readBlocks(blockSize, header, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var primary = ParseHeader(header, blockSize, blockCount, 1);
        await readBlocks(checked((long)((blockCount - 1) * (uint)blockSize)), header, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var backup = ParseHeader(header, blockSize, blockCount, blockCount - 1);
        ValidatePair(primary, backup);

        using var builder = new TableBuilder(primary, backup, blockSize, blockCount);
        var left = new byte[65536];
        var right = new byte[left.Length];
        var total = (long)primary.Count * primary.Size;
        for (long offset = 0; offset < total;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var used = (int)Math.Min(left.Length, total - offset);
            var transfer = checked((used + blockSize - 1) / blockSize * blockSize);
            await readBlocks(checked((long)(primary.ArrayLba * (uint)blockSize) + offset),
                left.AsMemory(0, transfer), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await readBlocks(checked((long)(backup.ArrayLba * (uint)blockSize) + offset),
                right.AsMemory(0, transfer), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            for (var position = 0; position < used; position += (int)primary.Size)
            {
                builder.Append(checked((int)((offset + position) / primary.Size)),
                    left.AsSpan(position, (int)primary.Size), right.AsSpan(position, (int)primary.Size));
            }
            offset += used;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return builder.Finish();
    }

    private static void ValidateGeometry(int blockSize, ulong blockCount)
    {
        if (blockSize is < 512 or > 65536 || (blockSize & (blockSize - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(blockSize));
        }
        if (blockCount < 6 || blockCount > (ulong)long.MaxValue / (uint)blockSize)
        {
            throw new ArgumentOutOfRangeException(nameof(blockCount));
        }
    }

    private static void ValidatePair(Header primary, Header backup)
    {
        if (primary.DiskId != backup.DiskId || primary.First != backup.First || primary.Last != backup.Last
            || primary.Count != backup.Count || primary.Size != backup.Size || primary.ArrayCrc != backup.ArrayCrc)
        {
            throw new InvalidDataException("The primary and backup GPT headers disagree.");
        }
    }
}