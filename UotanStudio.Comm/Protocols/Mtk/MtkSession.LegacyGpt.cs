using UotanStudio.Comm.Protocols.Common;
using UotanStudio.Firmware.BlockImage.Gpt;

namespace UotanStudio.Comm.Protocols.Mtk;

public sealed partial class MtkSession
{
    private const int LegacyGptBlockSize = 512;
    private const int LegacyGptReadTimeoutMs = 120000;

    /// <summary>Reads both GPT copies of one Legacy region under a single operation lease, without repair or primary-only fallback.
    /// <para>在单个操作租约下读取 Legacy 区域的两份 GPT 副本，不进行修复，也不会只回退到主副本。</para></summary>
    /// <remarks>
    /// <para>The Legacy agent addresses a region with a byte offset that is already region-relative, and the agent itself fixes the block size at 512 bytes, so the table is parsed with that geometry regardless of what a scatter file declares. Older MediaTek targets may answer with a legacy PMT layout instead of a GPT; that case is reported as a validation failure rather than guessed.</para>
    /// <para>Legacy 代理使用已经相对于区域的字节偏移寻址，且代理把块大小固定为 512 字节，因此无论 scatter 声明为何，分区表都按该几何信息解析。较老的联发科目标可能返回旧式 PMT 布局而不是 GPT；这种情况按校验失败 上报，不做猜测。</para></remarks>
    /// <param name="partitionId">Explicit discovered Legacy physical region, not a GPT entry index.
    /// <para>显式指定的已发现 Legacy 物理区域，不是 GPT 条目索引。</para></param>
    /// <param name="cancellationToken">Cancellation propagated to every storage exchange.
    /// <para>取消信号会传播到每一次存储交换。</para></param>
    /// <returns>A validated snapshot bound to the Legacy inventory and the selected region.
    /// <para>与 Legacy 清单和所选区域绑定的已校验快照。</para></returns>
    /// <exception cref="InvalidOperationException">Storage is not ready, has not been discovered, or <paramref name="partitionId"/> is not a discovered region.
    /// <para>存储未就绪、尚未发现，或 <paramref name="partitionId"/> 不是已发现的区域。</para></exception>
    /// <exception cref="InvalidDataException">A GPT block read was incomplete or the table failed validation.
    /// <para>某个 GPT 块读取不完整，或表未通过校验。</para></exception>
    /// <exception cref="NotSupportedException">NOR/NAND page geometry does not define a GPT block-device layout.
    /// <para>NOR/NAND 页几何信息未定义 GPT 块设备布局。</para></exception>
    /// <exception cref="MtkProtocolException">A storage exchange faulted the session.
    /// <para>某次存储交换使会话进入 fault 状态。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled at any cooperative point.
    /// <para>调用方在任一协作点取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.
    /// <para>会话已被释放。</para></exception>
    public async Task<MtkLegacyGptSnapshot> ReadLegacyGptAsync(byte partitionId,
        CancellationToken cancellationToken = default)
    {
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadLegacyGptOwnedAsync(partitionId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operation.Release();
        }
    }

    private async Task<MtkLegacyGptSnapshot> ReadLegacyGptOwnedAsync(byte partitionId, CancellationToken cancellationToken)
    {
        RequireState(MtkSessionState.LegacyStorageReady);
        cancellationToken.ThrowIfCancellationRequested();
        var storage = LegacyStorage ?? throw new InvalidOperationException("Legacy storage has not been discovered.");
        if (storage.Kind is MtkXFlashStorageKind.Nor or MtkXFlashStorageKind.Nand)
        {
            throw new NotSupportedException("Raw flash page geometry does not define a GPT block-device layout.");
        }

        storage.ValidateRange(partitionId, 0, LegacyGptBlockSize);
        var region = storage.Regions.Single(candidate => candidate.PartitionId == partitionId);
        var table = await GptReader.ReadAsync(ReadBlocksAsync, LegacyGptBlockSize,
            (ulong)(region.CapacityBytes / LegacyGptBlockSize), cancellationToken).ConfigureAwait(false);
        ThrowIfDisposed();
        return new MtkLegacyGptSnapshot(storage, partitionId, table);

        async ValueTask ReadBlocksAsync(long offset, Memory<byte> destination, CancellationToken token)
        {
            using var output = new FixedBufferWriteStream(destination);
            await TransferLegacyStorageOwnedAsync(false, partitionId, offset, destination.Length, output,
                LegacyGptReadTimeoutMs, null, token, nor: false).ConfigureAwait(false);
            if (output.Written != destination.Length)
            {
                throw new InvalidDataException("The Legacy GPT block read was incomplete.");
            }
        }
    }
}