namespace UotanStudio.Comm.Protocols.Mtk;

public sealed partial class MtkSession
{
    /// <summary>Gets validated storage geometry, or null before discovery completes.</summary>
    public MtkXFlashStorageInfo? XFlashStorage { get; private set; }

    /// <summary>
    /// Queries an explicitly selected eMMC/UFS family and its packet sizes after DA2 startup.
    /// A zero device type returns null without guessing another storage family.
    /// </summary>
    /// <param name="kind">The storage family to query.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>Validated geometry, or null when the agent explicitly reports no device of this family.</returns>
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
                var geometry = await QueryXFlashControlAsync(channel,
                    kind == MtkXFlashStorageKind.Emmc ? 0x040001u : 0x040004u, cancellationToken).ConfigureAwait(false);
                if (MtkXFlashStorageInfo.IsAbsent(geometry))
                {
                    return null;
                }
                var packetLengths = await QueryXFlashControlAsync(channel, 0x040007, cancellationToken).ConfigureAwait(false);
                var storage = MtkXFlashStorageInfo.Parse(kind, geometry, packetLengths);
                cancellationToken.ThrowIfCancellationRequested();
                ThrowIfDisposed();
                XFlashStorage = storage;
                _state = MtkSessionState.XFlashStorageReady;
                return storage;
            }
            catch
            {
                XFlashStorage = null;
                _xflash = null;
                _state = MtkSessionState.Faulted;
                throw;
            }
        }
        finally
        {
            _operation.Release();
        }
    }

    private async Task<byte[]> QueryXFlashControlAsync(MtkXFlashChannel channel, uint command,
        CancellationToken cancellationToken)
    {
        await SendXFlashWordAsync(channel, 0x010009, cancellationToken).ConfigureAwait(false);
        await ReadXFlashStatusAsync(channel, "device control", cancellationToken).ConfigureAwait(false);
        await SendXFlashWordAsync(channel, command, cancellationToken).ConfigureAwait(false);
        await ReadXFlashStatusAsync(channel, "device-control query", cancellationToken).ConfigureAwait(false);
        var reply = await channel.ReceiveAsync(cancellationToken).ConfigureAwait(false);
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