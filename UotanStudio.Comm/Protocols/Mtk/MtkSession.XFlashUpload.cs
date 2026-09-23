using System.Buffers.Binary;

namespace UotanStudio.Comm.Protocols.Mtk;

public sealed partial class MtkSession
{
    /// <summary>Sends explicitly selected EMI bytes to DA1 and requires its DRAM initialization acknowledgement.</summary>
    /// <param name="parameters">The readable EMI parameter stream, not an entire preloader container.</param>
    /// <param name="length">The parameter byte count, limited to 1 MiB.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    public async Task InitializeXFlashExternalRamAsync(Stream parameters, int length,
        CancellationToken cancellationToken = default)
    {
        MtkXFlashChannel.ValidateSource(parameters, length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, 1024 * 1024);
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireState(MtkSessionState.XFlashStageOneReady);
            cancellationToken.ThrowIfCancellationRequested();
            var channel = _xflash ?? throw new InvalidOperationException("XFlash was not initialized.");
            try
            {
                await SendXFlashWordAsync(channel, 0x01000A, cancellationToken).ConfigureAwait(false);
                await ReadXFlashStatusAsync(channel, "external RAM request", cancellationToken).ConfigureAwait(false);
                await SendXFlashWordAsync(channel, (uint)length, cancellationToken).ConfigureAwait(false);
                await channel.SendStreamAsync(1, parameters, length, null, cancellationToken).ConfigureAwait(false);
                await ReadXFlashStatusAsync(channel, "external RAM initialization", cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                ThrowIfDisposed();
                _xflashExternalRamInitialized = true;
            }
            catch
            {
                _state = MtkSessionState.Faulted;
                _xflash = null;
                throw;
            }
        }
        finally
        {
            _operation.Release();
        }
    }

    /// <summary>
    /// Streams an explicitly selected DA2 image to its profile-provided address and waits
    /// for upload and execution acknowledgement. No signature stripping or patching is performed.
    /// </summary>
    /// <param name="image">The exact executable DA2 region, positioned at its first byte.</param>
    /// <param name="loadAddress">The word-aligned 64-bit load address from the selected device profile.</param>
    /// <param name="length">The exact byte count to send, limited to 64 MiB.</param>
    /// <param name="progress">Optional cumulative payload-byte progress.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    public async Task UploadXFlashStageTwoAsync(Stream image, ulong loadAddress, int length,
        IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        MtkXFlashChannel.ValidateSource(image, length);
        if ((loadAddress & 3) != 0 || loadAddress > ulong.MaxValue - (uint)length)
        {
            throw new ArgumentOutOfRangeException(nameof(loadAddress), "The DA2 address is unaligned or its range overflows.");
        }
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireState(MtkSessionState.XFlashStageOneReady);
            cancellationToken.ThrowIfCancellationRequested();
            if (!_xflashExternalRamInitialized && _xflashConnectionAgent != MtkXFlashConnectionAgent.Preloader)
            {
                throw new InvalidOperationException("DA2 requires acknowledged EMI setup or a queried preloader connection.");
            }
            var channel = _xflash ?? throw new InvalidOperationException("XFlash was not initialized.");
            try
            {
                await SendXFlashWordAsync(channel, 0x010008, cancellationToken).ConfigureAwait(false);
                await ReadXFlashStatusAsync(channel, "DA2 load request", cancellationToken).ConfigureAwait(false);
                var range = new byte[16];
                BinaryPrimitives.WriteUInt64LittleEndian(range, loadAddress);
                BinaryPrimitives.WriteUInt64LittleEndian(range.AsSpan(8), (uint)length);
                await channel.SendAsync(1, range, cancellationToken).ConfigureAwait(false);
                await channel.SendStreamAsync(1, image, length, progress, cancellationToken).ConfigureAwait(false);
                await ReadXFlashStatusAsync(channel, "DA2 upload", cancellationToken).ConfigureAwait(false);
                await ReadXFlashStatusAsync(channel, "DA2 execution", cancellationToken, allowSync: true).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                ThrowIfDisposed();
                _state = MtkSessionState.XFlashStageTwoStarted;
            }
            catch
            {
                _state = MtkSessionState.Faulted;
                _xflash = null;
                throw;
            }
        }
        finally
        {
            _operation.Release();
        }
    }
}