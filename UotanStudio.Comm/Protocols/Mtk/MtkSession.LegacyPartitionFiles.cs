using UotanStudio.Comm.Protocols.Common;

namespace UotanStudio.Comm.Protocols.Mtk;

public sealed partial class MtkSession
{
    /// <summary>
    /// Backs up an exactly named Legacy-region GPT partition after revalidating the snapshot under the same
    /// operation lease as the data read. No repair, slot inference or retry is performed.
    /// <para>在与数据读取同一操作租约下重新校验快照后，备份名称完全匹配的 Legacy 区域 GPT 分区。
    /// 不执行修复、槽位推断或重试。</para>
    /// </summary>
    /// <remarks>
    /// <para>The destination is replaced only after a successful transfer and staging-file flush. This does not
    /// freeze device contents or promise a point-in-time storage snapshot. Progress excludes the metadata reads
    /// of the revalidation, and callbacks must not wait on this session.</para>
    /// <para>目标文件只在传输成功且暂存文件刷写完成后替换。这不会冻结设备内容，也不承诺时间点存储快照。
    /// 进度不含重新校验所用的元数据读取，且回调不得等待本会话。</para>
    /// </remarks>
    /// <param name="snapshot">A Legacy GPT observation from this session's current storage inventory.
    /// <para>来自本会话当前 Legacy 存储清单的 GPT 观测结果。</para></param>
    /// <param name="partitionName">Exact case-sensitive name, rejecting absent or ambiguous entries.
    /// <para>精确、区分大小写的名称；缺失或有歧义的条目会被拒绝。</para></param>
    /// <param name="destinationPath">Output file in an existing directory.
    /// <para>位于已存在目录中的输出文件。</para></param>
    /// <param name="progress">Cumulative partition bytes; full progress is not publication success.
    /// <para>累计的分区字节数；进度跑满不代表发布成功。</para></param>
    /// <param name="cancellationToken">Cancellation for waiting, metadata, data and publication preparation.
    /// <para>用于等待、元数据、数据与发布准备的取消令牌。</para></param>
    /// <returns>A task that completes once the staging file replaced the destination.
    /// <para>在暂存文件替换目标文件后完成的异步任务。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is null.
    /// <para><paramref name="snapshot"/> 为 null。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="partitionName"/> or
    /// <paramref name="destinationPath"/> is blank.
    /// <para><paramref name="partitionName"/> 或 <paramref name="destinationPath"/> 为空白。</para></exception>
    /// <exception cref="KeyNotFoundException">The partition name is absent from the GPT table.
    /// <para>GPT 表中不存在该分区名。</para></exception>
    /// <exception cref="InvalidOperationException">Legacy storage is not ready, or the snapshot belongs to a
    /// different storage inventory.
    /// <para>Legacy 存储未就绪，或快照属于另一份存储清单。</para></exception>
    /// <exception cref="InvalidDataException">The revalidated GPT metadata changed after preparation.
    /// <para>重新校验的 GPT 元数据在准备之后发生了变化。</para></exception>
    /// <exception cref="MtkProtocolException">A metadata or data exchange failed; the session faults.
    /// <para>元数据或数据交换失败；会话进入 fault 状态。</para></exception>
    /// <exception cref="IOException">The staging file cannot be created or publication failed.
    /// <para>无法创建暂存文件或发布失败。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled at any cooperative point.
    /// <para>调用方在任一协作点取消。</para></exception>
    public async Task ReadLegacyPartitionToFileAsync(MtkLegacyGptSnapshot snapshot, string partitionName,
        string destinationPath, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(partitionName);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        cancellationToken.ThrowIfCancellationRequested();
        var partition = snapshot.Table.FindPartition(partitionName);
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireState(MtkSessionState.LegacyStorageReady);
            if (!ReferenceEquals(snapshot.Storage, LegacyStorage))
            {
                throw new InvalidOperationException(
                    "The Legacy GPT snapshot belongs to a different storage inventory.");
            }

            await StorageFilePublisher.PublishAsync(destinationPath, partition.ByteLength, async output =>
            {
                await RevalidateLegacyGptOwnedAsync(snapshot, cancellationToken).ConfigureAwait(false);
                await TransferLegacyStorageOwnedAsync(false, snapshot.PartitionId, partition.ByteOffset,
                    partition.ByteLength, output, LegacyPlanTransferTimeoutMs, progress, cancellationToken,
                    nor: false).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operation.Release();
        }
    }
}