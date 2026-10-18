namespace UotanStudio.Comm.Protocols.Mtk;

public sealed partial class MtkSession
{
    /// <summary>Requests device-side formatting of an aligned physical range and waits for its explicit completion status. Cancellation or timeout cannot stop an erase already accepted by the device; such sessions are faulted and must not be retried blindly.
    /// <para>请求设备端格式化一段对齐的物理范围，并等待其显式的完成状态。取消或超时 无法阻止设备已接受的擦除；此类会话会进入 fault 状态，且不得盲目重试。</para></summary>
    /// <param name="partitionId">The discovered XFlash physical partition identifier, excluding RPMB.
    /// <para>已发现的 XFlash 物理分区标识符，不含 RPMB。</para></param>
    /// <param name="offset">The block-aligned byte offset within the physical partition.
    /// <para>物理分区内按块对齐的字节偏移。</para></param>
    /// <param name="length">The positive block-aligned byte count.<para>正数、按块对齐的字节数。</para></param>
    /// <param name="timeoutMs">The entire exchange budget after acquiring the operation gate, from 1 ms to 24 hours.
    /// <para>获取操作门之后整次交换的时间预算，范围从 1 毫秒到 24 小时。</para></param>
    /// <param name="cancellationToken">The operation cancellation token.<para>操作取消令牌。</para></param>
    /// <returns>A task that completes when the device confirms erase completion.
    /// <para>在设备确认擦除完成后返回的异步任务。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeoutMs"/> is outside 1 ms to 24 hours.
    /// <para><paramref name="timeoutMs"/> 超出 1 毫秒到 24 小时的范围。</para></exception>
    /// <exception cref="InvalidOperationException">Storage is not ready or has not been discovered.
    /// <para>存储未就绪或尚未发现。</para></exception>
    /// <exception cref="NotSupportedException">NOR requires the explicit erase geometry accepted by <see cref="EraseXFlashNorStorageAsync"/>.
    /// <para>NOR 必须使用 <see cref="EraseXFlashNorStorageAsync"/> 接受的显式擦除几何信息。</para></exception>
    /// <exception cref="MtkProtocolException">The device returned an unexpected status, exceeded the bounded continuation count, or requested an unsupported wait interval.
    /// <para>设备返回意外状态、超出有界的续轮次数，或请求了不支持的等待间隔。</para></exception>
    /// <exception cref="TimeoutException">The deadline expired; the device may still be erasing and must not be retried blindly.
    /// <para>截止时间到期；设备可能仍在擦除，不得盲目重试。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled waiting or polling.
    /// <para>调用方取消了等待或轮询。</para></exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.
    /// <para>会话已被释放。</para></exception>
    public async Task EraseXFlashStorageAsync(uint partitionId, long offset, long length,
        int timeoutMs = 120000, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timeoutMs);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(timeoutMs, 86400000);
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EraseXFlashStorageOwnedAsync(partitionId, offset, length, timeoutMs, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operation.Release();
        }
    }

    private async Task EraseXFlashStorageOwnedAsync(uint partitionId, long offset, long length, int timeoutMs,
        CancellationToken cancellationToken, int? norEraseBlockSize = null)
    {
        RequireState(MtkSessionState.XFlashStorageReady);
        cancellationToken.ThrowIfCancellationRequested();
        var storage = XFlashStorage ?? throw new InvalidOperationException("Storage has not been discovered.");
        storage.ValidateRange(partitionId, offset, length);
        if (storage.Kind == MtkXFlashStorageKind.Nor)
        {
            if (norEraseBlockSize is not { } eraseBlockSize)
            {
                throw new NotSupportedException("NOR erase requires an explicitly selected erase-block size.");
            }
            if (eraseBlockSize < storage.BlockSize || offset % eraseBlockSize != 0 || length % eraseBlockSize != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(length), "The NOR erase range is not aligned to the selected erase block.");
            }
        }
        else if (norEraseBlockSize is not null)
        {
            throw new InvalidOperationException("The selected storage is not NOR.");
        }
        await EraseXFlashRangeAsync(CreateXFlashStorageParameters(storage, partitionId, offset, length),
            timeoutMs, cancellationToken).ConfigureAwait(false);
    }

    private async Task EraseXFlashRangeAsync(byte[] parameters, int timeoutMs, CancellationToken cancellationToken)
    {
        var channel = _xflash ?? throw new InvalidOperationException("XFlash was not initialized.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeoutMs);
        var token = deadline.Token;
        try
        {
            await SendXFlashWordAsync(channel, 0x010003, token).ConfigureAwait(false);
            await ReadXFlashStatusAsync(channel, "erase request", token).ConfigureAwait(false);
            await channel.SendAsync(1, parameters, token).ConfigureAwait(false);
            await ReadXFlashStatusAsync(channel, "erase parameters", token).ConfigureAwait(false);
            var polls = 0;
            while (true)
            {
                var status = await ReadXFlashValueAsync(channel, "erase progress", token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                ThrowIfDisposed();
                if (status == 0x40040005)
                {
                    return;
                }
                if (status != 0x40040004)
                {
                    throw new MtkProtocolException($"XFlash did not confirm erase completion (0x{status:X8}).", status);
                }
                if (++polls > 4096)
                {
                    throw new MtkProtocolException("XFlash erase exceeded the bounded continuation count.");
                }
                var delayMs = await ReadXFlashValueAsync(channel, "erase wait interval", token).ConfigureAwait(false);
                if (delayMs > 60000)
                {
                    throw new MtkProtocolException("XFlash requested an unsupported erase wait interval.");
                }
                await Task.Delay((int)delayMs, token).ConfigureAwait(false);
                await SendXFlashWordAsync(channel, 0, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException error) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            FaultXFlashStorage();
            throw new TimeoutException("XFlash erase completion was not confirmed before the deadline; the device may still be erasing.", error);
        }
        catch
        {
            FaultXFlashStorage();
            throw;
        }
    }
}