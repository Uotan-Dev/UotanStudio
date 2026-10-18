using UotanStudio.Comm.Protocols.Common;

namespace UotanStudio.Comm.Protocols.Mtk;

public sealed partial class MtkSession
{
    /// <summary>Backs up an exactly named Legacy-region GPT partition after revalidating the snapshot under the same operation lease as the data read. No repair, slot inference or retry is performed.
    /// <para>在与数据读取同一操作租约下重新校验快照后，备份名称完全匹配的 Legacy 区域 GPT 分区。 不执行修复、槽位推断或重试。</para></summary>
    /// <remarks>
    /// <para>The destination is replaced only after a successful transfer and staging-file flush. This does not freeze device contents or promise a point-in-time storage snapshot. Progress excludes the metadata reads of the revalidation, and callbacks must not wait on this session.</para>
    /// <para>目标文件只在传输成功且暂存文件刷写完成后替换。这不会冻结设备内容，也不承诺时间点存储快照。 进度不含重新校验所用的元数据读取，且回调不得等待本会话。</para></remarks>
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
    /// <exception cref="ArgumentException"><paramref name="partitionName"/> or <paramref name="destinationPath"/> is blank.
    /// <para><paramref name="partitionName"/> 或 <paramref name="destinationPath"/> 为空白。</para></exception>
    /// <exception cref="KeyNotFoundException">The partition name is absent from the GPT table.
    /// <para>GPT 表中不存在该分区名。</para></exception>
    /// <exception cref="InvalidOperationException">Legacy storage is not ready, or the snapshot belongs to a different storage inventory.
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

    /// <summary>Reads an aligned Legacy eMMC range through a unique staging file and replaces the destination only after a complete exchange, which is how the reference handler publishes a raw-sector or whole-flash dump (Library/DA/mtk_da_handler.py:285-312) instead of returning bytes to the caller.
    /// <para>将对齐的 Legacy eMMC 范围经唯一暂存文件读取，仅在交换完整结束后替换目标文件。这正是参考处理程序 发布原始扇区或整片读取结果的方式（Library/DA/mtk_da_handler.py:285-312），而不是把字节返回给调用方。</para></summary>
    /// <param name="partitionId">The discovered physical region from 1 through 8, excluding RPMB.
    /// <para>已发现的 1 至 8 物理区域，不含 RPMB。</para></param>
    /// <param name="offset">The byte offset aligned to 512.<para>按 512 对齐的字节偏移。</para></param>
    /// <param name="length">The positive byte count aligned to 512.<para>按 512 对齐的正数字节数。</para></param>
    /// <param name="destinationPath">The file published after a successful completion.
    /// <para>成功完成后发布的文件路径。</para></param>
    /// <param name="timeoutMs">The cooperative whole-transfer deadline, from 1 ms through 24 hours.
    /// <para>完整传输的协作式截止时间，范围 1 毫秒至 24 小时。</para></param>
    /// <param name="progress">Optional cumulative transferred bytes; full progress is not publication success.
    /// <para>可选的累计传输字节数；进度跑满不代表发布成功。</para></param>
    /// <param name="cancellationToken">The operation cancellation token.<para>操作取消令牌。</para></param>
    /// <returns>A task that completes once the staging file replaced the destination.
    /// <para>在暂存文件替换目标文件后完成的异步任务。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="destinationPath"/> is blank.
    /// <para><paramref name="destinationPath"/> 为空白。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">The range is unaligned, empty or outside the region, or <paramref name="timeoutMs"/> is out of bounds.
    /// <para>范围未对齐、为空或超出区域，或 <paramref name="timeoutMs"/> 越界。</para></exception>
    /// <exception cref="InvalidOperationException">Legacy storage is not ready or has not been discovered.
    /// <para>Legacy 存储未就绪或尚未发现。</para></exception>
    /// <exception cref="NotSupportedException">The selected region is RPMB or the agent reports NOR or NAND.
    /// <para>所选区域为 RPMB，或代理上报 NOR 或 NAND。</para></exception>
    /// <exception cref="MtkProtocolException">The read failed; the Legacy family faults.
    /// <para>读取失败；Legacy 家族进入 fault 状态。</para></exception>
    /// <exception cref="TimeoutException">The transfer deadline expired; the session cannot be replayed.
    /// <para>传输截止时间到期；会话无法重放。</para></exception>
    /// <exception cref="IOException">The staging file cannot be created, the staged length is wrong, or publication failed.
    /// <para>无法创建暂存文件、暂存长度不符或发布失败。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled the read or publication preparation.
    /// <para>调用方取消了读取或发布准备。</para></exception>
    public Task ReadLegacyStorageToFileAsync(byte partitionId, long offset, long length, string destinationPath,
        int timeoutMs = 120000, IProgress<long>? progress = null, CancellationToken cancellationToken = default) =>
        StorageFilePublisher.PublishAsync(destinationPath, length,
            output => ReadLegacyStorageAsync(partitionId, offset, length, output, timeoutMs, progress,
                cancellationToken), cancellationToken);
}