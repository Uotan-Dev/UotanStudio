using UotanStudio.Comm.Protocols.Mtk.Storage;

namespace UotanStudio.Comm.Protocols.Mtk;

public sealed partial class MtkSession
{
    /// <summary>Gets validated storage geometry, or null before discovery completes.
    /// <para>获取已校验的存储几何信息；发现完成之前为 null。</para></summary>
    /// <value>The geometry from the latest successful discovery, cleared when the session faults.
    /// <para>最近一次成功发现得到的几何信息；会话 fault 时被清空。</para></value>
    public MtkXFlashStorageInfo? XFlashStorage { get; private set; }

    /// <summary>
    /// Queries an explicitly selected eMMC/UFS/NOR/NAND family and its packet sizes after DA2 startup.
    /// A zero device type returns null without guessing another storage family.
    /// <para>在 DA2 启动后查询显式选定的 eMMC/UFS/NOR/NAND 家族及其包大小。设备类型为 0 时返回
    /// null，不会猜测其他存储家族。</para>
    /// </summary>
    /// <param name="kind">The storage family to query.
    /// <para>要查询的存储家族。</para></param>
    /// <param name="cancellationToken">The operation cancellation token.
    /// <para>操作取消令牌。</para></param>
    /// <returns>Validated geometry, or null when the agent explicitly reports no device of this family.
    /// <para>已校验的几何信息；当 agent 明确报告该家族无设备时为 null。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is not a defined storage family.
    /// <para><paramref name="kind"/> 不是已定义的存储家族。</para></exception>
    /// <exception cref="InvalidOperationException">DA2 has not started or the XFlash channel is gone.
    /// <para>DA2 未启动或 XFlash 通道已失效。</para></exception>
    /// <exception cref="MtkProtocolException">A control reply is malformed or rejected; the session faults.
    /// <para>控制回复格式错误或被拒绝；会话进入 fault 状态。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled at any cooperative point.
    /// <para>调用方在任一协作点取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.
    /// <para>会话已被释放。</para></exception>
    public async Task<MtkXFlashStorageInfo?> DiscoverXFlashStorageAsync(MtkXFlashStorageKind kind,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireState(MtkSessionState.XFlashStageTwoStarted);
            cancellationToken.ThrowIfCancellationRequested();
            var channel = _xflash ?? throw new InvalidOperationException("XFlash was not initialized.");
            try
            {
                var storage = await QueryXFlashStorageOwnedAsync(channel, kind, cancellationToken).ConfigureAwait(false);
                if (storage is null)
                {
                    return null;
                }
                cancellationToken.ThrowIfCancellationRequested();
                ThrowIfDisposed();
                XFlashStorage = storage;
                _state = MtkSessionState.XFlashStorageReady;
                PublishStorageCapability(MtkStorageDiscoverySource.XFlash,
                    (generation, evidence) => MtkStorageCapabilitySnapshot.FromXFlash(storage, generation, evidence));
                return storage;
            }
            catch
            {
                XFlashStorage = null;
                _xflash = null;
                _state = MtkSessionState.Faulted;
                ClearStorageCapability(MtkStorageDiscoverySource.XFlash);
                throw;
            }
        }
        finally
        {
            _operation.Release();
        }
    }

    private async Task<MtkXFlashStorageInfo?> QueryXFlashStorageOwnedAsync(MtkXFlashChannel channel,
        MtkXFlashStorageKind kind, CancellationToken cancellationToken)
    {
        var command = kind switch
        {
            MtkXFlashStorageKind.Emmc => 0x040001u,
            MtkXFlashStorageKind.Ufs => 0x040004u,
            MtkXFlashStorageKind.Nor => 0x040003u,
            MtkXFlashStorageKind.Nand => 0x040002u,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        var geometry = await QueryXFlashControlAsync(channel, command, cancellationToken).ConfigureAwait(false);
        if (MtkXFlashStorageInfo.IsAbsent(geometry))
        {
            return null;
        }
        var packetLengths = await QueryXFlashControlAsync(channel, 0x040007, cancellationToken).ConfigureAwait(false);
        return MtkXFlashStorageInfo.Parse(kind, geometry, packetLengths);
    }

    private async Task<byte[]> QueryXFlashControlAsync(MtkXFlashChannel channel, uint command,
        CancellationToken cancellationToken, int maximumPayload = 1024 * 1024)
    {
        await SendXFlashWordAsync(channel, 0x010009, cancellationToken).ConfigureAwait(false);
        await ReadXFlashStatusAsync(channel, "device control", cancellationToken).ConfigureAwait(false);
        await SendXFlashWordAsync(channel, command, cancellationToken).ConfigureAwait(false);
        await ReadXFlashStatusAsync(channel, "device-control query", cancellationToken).ConfigureAwait(false);
        var reply = await channel.ReceiveAsync(maximumPayload, cancellationToken).ConfigureAwait(false);
        await ReadXFlashStatusAsync(channel, "device-control completion", cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (reply.DataType != 1)
        {
            throw new MtkProtocolException("The device-control query returned a non-protocol frame.");
        }
        return reply.Payload;
    }
}