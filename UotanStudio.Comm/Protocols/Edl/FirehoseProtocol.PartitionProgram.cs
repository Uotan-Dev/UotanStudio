namespace UotanStudio.Comm.Protocols.Edl;

/// <summary>
/// Evidence for a completed named raw write; ACK alone does not prove persistence.
/// <para>已完成的具名原始写入的证据；仅有 ACK 并不能证明数据已持久化。</para>
/// </summary>
/// <param name="AcknowledgedBytes">
/// Bytes whose program operation received the final ACK.
/// <para>其编程操作收到最终 ACK 的字节数。</para>
/// </param>
/// <param name="VerifiedBytes">
/// Bytes subsequently read back and matched, or zero when verification was not requested.
/// <para>随后被回读并逐字节匹配的字节数；未请求校验时为零。</para>
/// </param>
public sealed record FirehosePartitionWriteResult(long AcknowledgedBytes, long VerifiedBytes);

public sealed partial class FirehoseProtocol
{
    /// <summary>
    /// Writes a retained raw file into an exactly named GPT partition, optionally verifying by readback.
    /// <para>将保持打开的原始文件写入精确命名的 GPT 分区，可选地通过回读进行校验。</para>
    /// </summary>
    /// <remarks>
    /// This is an explicit destructive command, not a preview. The caller must authorize it.
    /// GPT revalidation, write and optional readback share one lease. No erase, padding,
    /// sparse expansion, slot inference, retry or rollback occurs. Failure can follow a
    /// successful write and must not be interpreted as evidence that storage is unchanged.
    /// A retained file handle is not a cross-platform immutable content snapshot.
    /// <para>这是显式的破坏性命令，不是预览，调用方必须先获得授权。GPT 重校验、写入与可选回读
    /// 共享同一个租约。不会进行擦除、填充、稀疏展开、槽位推断、重试或回滚。失败可能发生在成功
    /// 写入之后，不能据此认为存储未变化。保持打开的文件句柄在跨平台意义上并非不可变的内容快照。</para>
    /// </remarks>
    /// <param name="snapshot">
    /// Validated GPT observation from this protocol and storage configuration.
    /// <para>来自本协议与存储配置的已校验 GPT 观测。</para>
    /// </param>
    /// <param name="partitionName">Exact case-sensitive GPT name.
    /// <para>精确、区分大小写的 GPT 分区名。</para></param>
    /// <param name="sourcePath">
    /// Explicit raw image file; its complete length must be positive and sector-aligned.
    /// <para>显式指定的原始镜像文件；其完整长度必须为正且按扇区对齐。</para>
    /// </param>
    /// <param name="partitionOffset">
    /// Sector-aligned byte offset relative to the named partition.
    /// <para>相对于该命名分区、按扇区对齐的字节偏移。</para>
    /// </param>
    /// <param name="verify">
    /// Whether to require full readback comparison before returning success.
    /// <para>是否要求在返回成功之前完成完整的回读比对。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancellation for preflight, lease, metadata and transfers.
    /// <para>取消覆盖预检、租约等待、元数据与数据传输。</para>
    /// </param>
    /// <returns>
    /// Final-ACK byte count and optional readback byte count.
    /// <para>最终 ACK 的字节数以及可选的回读字节数。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is null.
    /// <para><paramref name="snapshot"/> 为 null。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="sourcePath"/> is null, empty, or whitespace,
    /// or <paramref name="partitionName"/> is empty.
    /// <para><paramref name="sourcePath"/> 为 null、空或仅空白，或 <paramref name="partitionName"/> 为空。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="partitionOffset"/> is negative, not
    /// sector-aligned, or at or beyond the partition end.
    /// <para><paramref name="partitionOffset"/> 为负、未按扇区对齐，或已达到/超过分区末尾。</para></exception>
    /// <exception cref="KeyNotFoundException">No partition carries the exact <paramref name="partitionName"/>.
    /// <para>没有任何分区使用精确匹配的 <paramref name="partitionName"/>。</para></exception>
    /// <exception cref="InvalidDataException">The partition name is ambiguous, the source length is not sector-aligned
    /// or exceeds the remaining partition range, the source length changed around the write, or the GPT observation
    /// fails revalidation.
    /// <para>分区名有歧义、源文件长度未按扇区对齐或超出分区剩余范围、写入前后源文件长度发生变化、GPT 观测无法重新校验。</para></exception>
    /// <exception cref="InvalidOperationException">The snapshot belongs to a different protocol instance or its
    /// slot/sector configuration differs from this one.
    /// <para>快照属于其他协议实例，或其槽位/扇区配置与当前实例不一致。</para></exception>
    /// <exception cref="FirehoseException">A program read-back or the target response indicates the transfer did not
    /// complete outside raw mode.
    /// <para>编程回读或目标响应表明传输未在原始模式之外正常完成。</para></exception>
    /// <exception cref="IOException">The source file cannot be opened for retained sequential reading.
    /// <para>无法以保持顺序读取的方式打开源文件。</para></exception>
    /// <exception cref="OperationCanceledException">The token is cancelled before or during any phase.
    /// <para>令牌在任一阶段之前或期间被取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The protocol has been disposed.
    /// <para>协议已被释放。</para></exception>
    public async Task<FirehosePartitionWriteResult> ProgramRawPartitionFromFileAsync(FirehoseGptSnapshot snapshot,
        string partitionName, string sourcePath, long partitionOffset = 0, bool verify = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        cancellationToken.ThrowIfCancellationRequested();
        var partition = snapshot.Table.FindPartition(partitionName);
        ArgumentOutOfRangeException.ThrowIfNegative(partitionOffset);
        var sectorSize = snapshot.Geometry.SectorSize;
        if (partitionOffset % sectorSize != 0 || partitionOffset >= partition.ByteLength)
        {
            throw new ArgumentOutOfRangeException(nameof(partitionOffset));
        }
        using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var length = source.Length;
        if (length == 0 || length % sectorSize != 0 || length > partition.ByteLength - partitionOffset)
        {
            throw new InvalidDataException("The raw source must fit the selected GPT range without padding.");
        }
        using var operation = await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        await RevalidateGptOwnedAsync(snapshot, cancellationToken).ConfigureAwait(false);
        if (source.Length != length)
        {
            throw new InvalidDataException("The retained raw source length changed before programming.");
        }
        var start = checked((long)partition.FirstLba + partitionOffset / sectorSize);
        var sectors = length / sectorSize;
        var region = snapshot.Geometry.PhysicalPartitionNumber;
        await ProgramCoreAsync(source, new FirehoseSectorAddress(start, false), sectors, "partition.bin",
            region, cancellationToken).ConfigureAwait(false);
        ValidateGptBinding(snapshot);
        if (verify)
        {
            if (source.Length != length)
            {
                throw new InvalidDataException("The raw source length changed after the acknowledged write.");
            }
            if (await TryVerifyProgramWithTargetDigestOwnedAsync(source, start, sectors, region,
                    cancellationToken).ConfigureAwait(false))
            {
                Log("On-target digest verification passed for the named partition write.");
            }
            else
            {
                using var comparison = new ReadBackComparisonStream(source, partitionName);
                await ReadToStreamCoreAsync(comparison, start, sectors, "partition.bin", region,
                    cancellationToken).ConfigureAwait(false);
                await comparison.CompleteAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        ValidateGptBinding(snapshot);
        return new FirehosePartitionWriteResult(length, verify ? length : 0);
    }
}