namespace UotanStudio.Firmware.BlockImage.Gpt;

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
    /// Reads and validates a GPT pair at the entry-array LBAs its own headers report.
    /// <para>按两个头部自身所报的条目区 LBA 读取并校验 GPT 主备对。</para>
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
    /// Explicit capacity in blocks. <para>显式的块容量。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancellation propagated through every read. <para>贯穿每次读取传递的取消令牌。</para>
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
    public static Task<GptTable> ReadAsync(GptBlockReader readBlocks, int blockSize, ulong blockCount,
        CancellationToken cancellationToken = default) =>
        ReadWithPartitionEntryOverridesAsync(readBlocks, blockSize, blockCount, null, null, null, cancellationToken);

    /// <summary>
    /// Reads and validates a GPT pair with operator-supplied partition-entry array dimensions.
    /// <para>按操作者提供的分区条目数组维度读取并校验 GPT 主备对。</para>
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
    /// <param name="partitionEntryLba">
    /// Override of the primary partition-entry array LBA for a header whose own value is damaged, or
    /// <see langword="null"/> to use the header's value; the backup array keeps its header-reported location.
    /// Only the location is replaced: every other primary header field must still validate, and the array read
    /// from the overridden location is still required to match the declared entry-array CRC byte-for-byte
    /// against the backup copy, so an override that points inside the reported capacity but at the wrong place
    /// ends in <see cref="InvalidDataException"/>, while an override that cannot describe the array at all ends in
    /// <see cref="ArgumentOutOfRangeException"/>. Neither path yields a partially trusted table. The reference
    /// downloader exposes the same knob as
    /// <c>--gpt-part-entry-start-lba</c> (bkerler/edl <c>edlclient/Library/firehose_client.py:483-485</c>,
    /// consumed at <c>edlclient/Library/gpt.py:360-362</c>).
    /// <para>主分区条目数组 LBA 的覆盖值，用于自身取值已损坏的头部；为 <see langword="null"/> 时采用头部自身的
    /// 取值，备份数组仍使用其头部所报位置。覆盖仅替换位置：主头部的其余字段仍须全部通过校验，且从覆盖位置读出的
    /// 条目数组仍必须与备份副本逐字节一致并匹配头部声明的条目数组 CRC。因此落在上报容量之内却指错位置的覆盖值以
    /// <see cref="InvalidDataException"/> 结束，而根本无法描述该数组的覆盖值以
    /// <see cref="ArgumentOutOfRangeException"/> 被拒绝，两条路径都不会产出部分可信的分区表。参考下载器以
    /// <c>--gpt-part-entry-start-lba</c> 暴露同一开关（bkerler/edl
    /// <c>edlclient/Library/firehose_client.py:483-485</c>，消费点 <c>edlclient/Library/gpt.py:360-362</c>）。</para>
    /// </param>
    /// <param name="partitionEntryCount">
    /// Override of the partition-entry count for a header that reports zero or a damaged count, or
    /// <see langword="null"/> to use the header's value. Unlike the LBA, the dimensions are array-wide and
    /// therefore apply to both copies, so the declared entry-array CRC still gates the result. The reference
    /// downloader exposes the same knob as <c>--gpt-num-part-entries</c>.
    /// <para>分区条目数量的覆盖值，用于上报零或条目数量已损坏的头部；为 <see langword="null"/> 时采用头部自身的取值。
    /// 与 LBA 不同，数组维度作用于整条数组，因此同时应用于主备两份，所声明的条目数组 CRC 仍是放行条件。参考下载器以
    /// <c>--gpt-num-part-entries</c> 暴露同一开关。</para>
    /// </param>
    /// <param name="partitionEntrySize">
    /// Override of the partition-entry size, a power of two from 128 through 4096, or <see langword="null"/> to
    /// use the header's value; the same array-wide rule as <paramref name="partitionEntryCount"/> applies. The
    /// reference downloader exposes the same knob as <c>--gpt-part-entry-size</c>.
    /// <para>分区条目大小的覆盖值，须为 128 至 4096 之间 2 的幂；为 <see langword="null"/> 时采用头部自身的取值，
    /// 且与 <paramref name="partitionEntryCount"/> 一样作用于整条数组。参考下载器以 <c>--gpt-part-entry-size</c>
    /// 暴露同一开关。</para>
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
    /// The block size or block count is outside the supported range; or <paramref name="partitionEntryLba"/>
    /// places the entry array outside the reported capacity; or <paramref name="partitionEntryCount"/> is not
    /// between 1 and 65536; or <paramref name="partitionEntrySize"/> is not a power of two from 128 through 4096;
    /// or the requested dimensions exceed the supported entry-array limit.
    /// <para>块大小或块数量超出支持范围；或 <paramref name="partitionEntryLba"/> 把条目数组放到上报容量之外；或
    /// <paramref name="partitionEntryCount"/> 不在 1 至 65536 之间；或 <paramref name="partitionEntrySize"/>
    /// 不是 128 至 4096 之间 2 的幂；或所请求的维度超出支持的条目数组上限。</para>
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// The signature, CRC, geometry, identities or entry arrays fail validation.
    /// <para>签名、CRC、几何、标识或条目数组未通过校验。</para>
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// The token was cancelled, including cancellation surfaced by <paramref name="readBlocks"/>.
    /// <para>令牌已被取消，含由 <paramref name="readBlocks"/> 抛出的取消。</para>
    /// </exception>
    public static async Task<GptTable> ReadWithPartitionEntryOverridesAsync(GptBlockReader readBlocks, int blockSize,
        ulong blockCount, ulong? partitionEntryLba, uint? partitionEntryCount, uint? partitionEntrySize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(readBlocks);
        ValidateGeometry(blockSize, blockCount);
        cancellationToken.ThrowIfCancellationRequested();
        var header = new byte[blockSize];
        await readBlocks(blockSize, header, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var primary = ParseHeader(header, blockSize, blockCount, 1, partitionEntryLba, partitionEntryCount,
            partitionEntrySize);
        await readBlocks(checked((long)((blockCount - 1) * (uint)blockSize)), header, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var backup = ParseHeader(header, blockSize, blockCount, blockCount - 1, null, partitionEntryCount,
            partitionEntrySize);
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