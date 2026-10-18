using UotanStudio.Comm.Protocols.Common;

namespace UotanStudio.Comm.Protocols.Edl;

public sealed partial class FirehoseProtocol
{
    /// <summary>Revalidates GPT and backs up an exactly named partition using one operation lease.
    /// <para>使用单个操作租约重新校验 GPT，并备份精确命名的分区。</para></summary>
    /// <remarks>The destination is replaced only after final ACK, complete staging output and flush. No slot inference, repair or retry occurs. This does not freeze target contents or guarantee filesystem consistency. Progress callbacks must not wait on this protocol.
    /// <para>只有在收到最终 ACK、暂存文件写入完整并刷新之后才会替换目标文件。不会进行槽位推断、 修复或重试。这不冻结目标内容，也不保证文件系统一致性。进度回调不得等待本协议。</para></remarks>
    /// <param name="snapshot">GPT observation from this protocol instance and current slot/sector configuration.
    /// <para>来自本协议实例、且与当前槽位/扇区配置匹配的 GPT 观测。</para></param>
    /// <param name="partitionName">Exact, case-sensitive GPT partition name.
    /// <para>精确、区分大小写的 GPT 分区名。</para></param>
    /// <param name="destinationPath">Output file in an existing directory.
    /// <para>写入已存在目录中的输出文件。</para></param>
    /// <param name="progress">Cumulative partition bytes, excluding metadata; full progress is not publication success.
    /// <para>累计已读分区字节数（不含元数据）；进度报满不代表发布成功。</para></param>
    /// <param name="cancellationToken">Cancellation for waiting, metadata, data and publication preparation.
    /// <para>取消覆盖租约等待、元数据、数据传输与发布准备阶段。</para></param>
    /// <returns>A task that completes after the staged file has been atomically published.
    /// <para>在暂存文件被原子发布后完成的任务。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is null.
    /// <para><paramref name="snapshot"/> 为 null。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="destinationPath"/> is null, empty, or whitespace, or <paramref name="partitionName"/> is empty.
    /// <para><paramref name="destinationPath"/> 为 null、空或仅空白，或 <paramref name="partitionName"/> 为空。</para></exception>
    /// <exception cref="KeyNotFoundException">No partition carries the exact <paramref name="partitionName"/>.
    /// <para>没有任何分区使用精确匹配的 <paramref name="partitionName"/>。</para></exception>
    /// <exception cref="InvalidDataException">The partition name is ambiguous, the GPT observation changed, or the configuration differs from the snapshot.
    /// <para>分区名有歧义、GPT 观测已变化，或配置与快照不一致。</para></exception>
    /// <exception cref="InvalidOperationException">The snapshot belongs to a different protocol instance or its slot/sector configuration differs from this one.
    /// <para>快照属于其他协议实例，或其槽位/扇区配置与当前实例不一致。</para></exception>
    /// <exception cref="FirehoseException">The partition read returned NAK or did not complete successfully.
    /// <para>分区读取返回 NAK 或未能成功完成。</para></exception>
    /// <exception cref="OperationCanceledException">The token is cancelled before or during the transfer.
    /// <para>令牌在传输之前或期间被取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The protocol has been disposed.
    /// <para>协议已被释放。</para></exception>
    /// <exception cref="IOException">The staging file cannot be created in, or moved into, the destination directory.
    /// <para>无法在目标目录中创建暂存文件或将其移动到目标位置。</para></exception>
    public async Task ReadPartitionToFileAsync(FirehoseGptSnapshot snapshot, string partitionName, string destinationPath,
        IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        cancellationToken.ThrowIfCancellationRequested();
        var partition = snapshot.Table.FindPartition(partitionName);
        using var operation = await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        ValidateGptBinding(snapshot);
        await StorageFilePublisher.PublishAsync(destinationPath, partition.ByteLength, async output =>
        {
            await RevalidateGptOwnedAsync(snapshot, cancellationToken).ConfigureAwait(false);
            var response = await ReadToStreamCoreAsync(output, checked((long)partition.FirstLba),
                partition.ByteLength / snapshot.Geometry.SectorSize, "partition.bin",
                snapshot.Geometry.PhysicalPartitionNumber, cancellationToken, progress).ConfigureAwait(false);
            if (!response.IsAck)
            {
                throw new FirehoseException("The partition backup did not complete successfully.");
            }
            ThrowIfDisposed();
            ValidateGptBinding(snapshot);
        }, cancellationToken).ConfigureAwait(false);
    }
}