namespace UotanStudio.Comm.Protocols.Mtk;

public sealed partial class MtkSession
{
    /// <summary>
    /// Requests device-side formatting of an aligned physical range and waits for its
    /// explicit completion status. Cancellation or timeout cannot stop an erase already
    /// accepted by the device; such sessions are faulted and must not be retried blindly.
    /// </summary>
    /// <param name="partitionId">The discovered XFlash physical partition identifier, excluding RPMB.</param>
    /// <param name="offset">The block-aligned byte offset within the physical partition.</param>
    /// <param name="length">The positive block-aligned byte count.</param>
    /// <param name="timeoutMs">The entire exchange budget after acquiring the operation gate, from 1 ms to 24 hours.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
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
        CancellationToken cancellationToken)
    {
        RequireState(MtkSessionState.XFlashStorageReady);
        cancellationToken.ThrowIfCancellationRequested();
        var storage = XFlashStorage ?? throw new InvalidOperationException("Storage has not been discovered.");
        storage.ValidateRange(partitionId, offset, length);
        var channel = _xflash ?? throw new InvalidOperationException("XFlash was not initialized.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeoutMs);
        var token = deadline.Token;
        try
        {
            await SendXFlashWordAsync(channel, 0x010003, token).ConfigureAwait(false);
            await ReadXFlashStatusAsync(channel, "erase request", token).ConfigureAwait(false);
            await channel.SendAsync(1, CreateXFlashStorageParameters(storage, partitionId, offset, length), token).ConfigureAwait(false);
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