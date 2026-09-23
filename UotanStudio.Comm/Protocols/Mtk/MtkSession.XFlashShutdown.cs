using System.Buffers.Binary;

namespace UotanStudio.Comm.Protocols.Mtk;

public sealed partial class MtkSession
{
    /// <summary>
    /// Explicitly requests shutdown or a boot-mode transition from a storage-ready XFlash
    /// agent. Both statuses must confirm success before the session becomes terminal.
    /// No reboot is performed automatically by flashing or disposal.
    /// </summary>
    /// <remarks>
    /// Requires caller authorization. Uses synchronous target completion with watchdog,
    /// download-bit, RTC-preservation and USB-retention flags cleared. These flags and the
    /// requested boot mode are target-dependent; ACK does not prove boot or durable storage.
    /// The owned transport is disconnected, not reset, after acknowledgement. A disconnect
    /// failure leaves the acknowledged terminal state observable; Dispose still releases it.
    /// In-flight cancellation or loss of the final response faults the session without retry.
    /// </remarks>
    /// <param name="mode">The explicitly authorized target power/boot request.</param>
    /// <param name="cancellationToken">Cancellation for waiting and the command exchange.</param>
    public async Task ShutdownXFlashAsync(MtkXFlashShutdownMode mode,
        CancellationToken cancellationToken = default)
    {
        if (mode is not (MtkXFlashShutdownMode.PowerOff or MtkXFlashShutdownMode.NormalBoot or MtkXFlashShutdownMode.Fastboot))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireState(MtkSessionState.XFlashStorageReady);
            cancellationToken.ThrowIfCancellationRequested();
            var channel = _xflash ?? throw new InvalidOperationException("XFlash was not initialized.");
            var parameters = new byte[32];
            BinaryPrimitives.WriteUInt32LittleEndian(parameters, mode == MtkXFlashShutdownMode.PowerOff ? 0u : 1u);
            BinaryPrimitives.WriteUInt32LittleEndian(parameters.AsSpan(12), (uint)mode);
            try
            {
                await SendXFlashWordAsync(channel, 0x010007, cancellationToken).ConfigureAwait(false);
                await ReadXFlashStatusAsync(channel, "shutdown request", cancellationToken).ConfigureAwait(false);
                await channel.SendAsync(1, parameters, cancellationToken).ConfigureAwait(false);
                await ReadXFlashStatusAsync(channel, "shutdown completion", cancellationToken).ConfigureAwait(false);
                ThrowIfDisposed();
            }
            catch
            {
                FaultXFlashStorage();
                throw;
            }
            _state = MtkSessionState.XFlashShutdownAcknowledged;
            _xflash = null;
            XFlashStorage = null;
            Transport.Disconnect();
        }
        finally
        {
            _operation.Release();
        }
    }
}