using UotanStudio.Comm.Protocols.Common;
using UotanStudio.Firmware.BlockImage.Gpt;

namespace UotanStudio.Comm.Protocols.Mtk;

public sealed partial class MtkSession
{
    /// <summary>Reads both GPT copies under one operation lease, without repair or primary-only fallback.
    /// <para>在单个操作租约下读取两份 GPT 副本，不进行修复，也不会只回退到主副本。</para></summary>
    /// <param name="partitionId">Explicit discovered physical region, not a GPT entry index.
    /// <para>显式指定的已发现物理区域，不是 GPT 条目索引。</para></param>
    /// <param name="cancellationToken">Cancellation propagated to every storage exchange.
    /// <para>取消信号会传播到每一次存储交换。</para></param>
    /// <returns>A validated snapshot bound to the selected storage geometry and physical region.
    /// <para>与所选存储几何信息和物理区域绑定的已校验快照。</para></returns>
    /// <exception cref="InvalidOperationException">
    /// Storage is not ready, has not been discovered, or <paramref name="partitionId"/> is not a discovered region.
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
    public async Task<MtkGptSnapshot> ReadXFlashGptAsync(uint partitionId, CancellationToken cancellationToken = default)
    {
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadXFlashGptOwnedAsync(partitionId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operation.Release();
        }
    }

    private async Task RevalidateXFlashGptOwnedAsync(MtkGptSnapshot expected, CancellationToken cancellationToken)
    {
        var current = await ReadXFlashGptOwnedAsync(expected.PartitionId, cancellationToken).ConfigureAwait(false);
        if (current.Table.MetadataSha256 != expected.Table.MetadataSha256
            || current.Table.BlockSize != expected.Table.BlockSize || current.Table.BlockCount != expected.Table.BlockCount)
        {
            throw new InvalidDataException("The GPT metadata changed after preparation; no partition operation was started.");
        }
    }

    private async Task<MtkGptSnapshot> ReadXFlashGptOwnedAsync(uint partitionId, CancellationToken cancellationToken)
    {
        RequireState(MtkSessionState.XFlashStorageReady);
        cancellationToken.ThrowIfCancellationRequested();
        var storage = XFlashStorage ?? throw new InvalidOperationException("Storage has not been discovered.");
        if (storage.Kind is MtkXFlashStorageKind.Nor or MtkXFlashStorageKind.Nand)
        {
            throw new NotSupportedException("Raw flash page geometry does not define a GPT block-device layout.");
        }
        storage.ValidateRange(partitionId, 0, storage.BlockSize);
        var region = storage.Regions.Single(region => region.PartitionId == partitionId);
        var table = await GptReader.ReadAsync(ReadBlocksAsync, storage.BlockSize,
            (ulong)(region.CapacityBytes / storage.BlockSize), cancellationToken).ConfigureAwait(false);
        ThrowIfDisposed();
        return new MtkGptSnapshot(storage, partitionId, table);

        async ValueTask ReadBlocksAsync(long offset, Memory<byte> destination, CancellationToken token)
        {
            using var output = new FixedBufferWriteStream(destination);
            await ReadXFlashStorageOwnedAsync(partitionId, offset, destination.Length, output, null, token).ConfigureAwait(false);
            if (output.Written != destination.Length)
            {
                throw new InvalidDataException("The GPT block read was incomplete.");
            }
        }
    }
}