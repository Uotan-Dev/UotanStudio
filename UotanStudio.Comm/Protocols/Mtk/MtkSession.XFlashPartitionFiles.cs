using UotanStudio.Comm.Protocols.Common;

namespace UotanStudio.Comm.Protocols.Mtk;

public sealed partial class MtkSession
{
    /// <summary>
    /// Backs up an exactly named GPT partition after revalidating the snapshot under the
    /// same operation lease as the data read. No repair, slot inference or retry is performed.
    /// <para>在与数据读取同一操作租约下重新校验快照后，备份名称完全匹配的 GPT 分区。
    /// 不执行修复、槽位推断或重试。</para>
    /// </summary>
    /// <remarks>
    /// The destination is replaced only after successful transfer and staging-file flush.
    /// This does not freeze device contents or promise a point-in-time storage snapshot.
    /// Progress excludes metadata reads; callbacks must not wait on this session.
    /// </remarks>
    /// <param name="snapshot">A GPT observation from this session's current storage geometry.
    /// <para>来自本会话当前存储几何信息的 GPT 观测结果。</para></param>
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
    /// <exception cref="ArgumentException"><paramref name="destinationPath"/> is blank.
    /// <para><paramref name="destinationPath"/> 为空白。</para></exception>
    /// <exception cref="InvalidOperationException">Storage is not ready or the snapshot belongs to a different storage observation.
    /// <para>存储未就绪，或快照属于另一份存储观测。</para></exception>
    /// <exception cref="InvalidDataException">The revalidated GPT metadata changed after preparation.
    /// <para>重新校验的 GPT 元数据在准备之后发生了变化。</para></exception>
    /// <exception cref="MtkProtocolException">A metadata or data exchange failed; the session faults.
    /// <para>元数据或数据交换失败；会话进入 fault 状态。</para></exception>
    /// <exception cref="IOException">The staging file cannot be created or publication failed.
    /// <para>无法创建暂存文件或发布失败。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled at any cooperative point.
    /// <para>调用方在任一协作点取消。</para></exception>
    public async Task ReadXFlashPartitionToFileAsync(MtkGptSnapshot snapshot, string partitionName,
        string destinationPath, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        cancellationToken.ThrowIfCancellationRequested();
        var partition = snapshot.Table.FindPartition(partitionName);
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireState(MtkSessionState.XFlashStorageReady);
            if (!ReferenceEquals(snapshot.Storage, XFlashStorage))
            {
                throw new InvalidOperationException("The GPT snapshot belongs to a different storage observation.");
            }
            await StorageFilePublisher.PublishAsync(destinationPath, partition.ByteLength, async output =>
            {
                await RevalidateXFlashGptOwnedAsync(snapshot, cancellationToken).ConfigureAwait(false);
                await ReadXFlashStorageOwnedAsync(snapshot.PartitionId, partition.ByteOffset, partition.ByteLength,
                    output, progress, cancellationToken).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operation.Release();
        }
    }
}