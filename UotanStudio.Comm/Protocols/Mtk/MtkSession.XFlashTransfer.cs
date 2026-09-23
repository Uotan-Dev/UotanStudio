using System.Buffers.Binary;

namespace UotanStudio.Comm.Protocols.Mtk;

public sealed partial class MtkSession
{
    /// <summary>
    /// Reads an aligned physical storage range to a caller-owned stream. The destination may
    /// contain partial data on failure; callers publishing files must use temporary files.
    /// </summary>
    /// <param name="partitionId">The discovered XFlash physical partition identifier, not a GPT index.</param>
    /// <param name="offset">The block-aligned byte offset within the physical partition.</param>
    /// <param name="length">The positive block-aligned byte count.</param>
    /// <param name="destination">A writable stream which remains open.</param>
    /// <param name="progress">Optional cumulative bytes stored and acknowledged.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    public async Task ReadXFlashStorageAsync(uint partitionId, long offset, long length, Stream destination,
        IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
        {
            throw new ArgumentException("The storage destination is not writable.", nameof(destination));
        }
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ReadXFlashStorageOwnedAsync(partitionId, offset, length, destination, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operation.Release();
        }
    }

    private async Task ReadXFlashStorageOwnedAsync(uint partitionId, long offset, long length, Stream destination,
        IProgress<long>? progress, CancellationToken cancellationToken)
    {
        RequireState(MtkSessionState.XFlashStorageReady);
        cancellationToken.ThrowIfCancellationRequested();
        var storage = XFlashStorage ?? throw new InvalidOperationException("Storage has not been discovered.");
        storage.ValidateRange(partitionId, offset, length);
        var channel = _xflash ?? throw new InvalidOperationException("XFlash was not initialized.");
        try
        {
            await BeginXFlashTransferAsync(channel, storage, false, partitionId, offset, length, cancellationToken).ConfigureAwait(false);
            long transferred = 0;
            while (transferred < length)
            {
                var frame = await channel.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                ThrowIfDisposed();
                if (frame.DataType != 1)
                {
                    throw new MtkProtocolException("Storage data arrived in a non-protocol frame.");
                }
                if (frame.Payload.Length is 2 or 4)
                {
                    var status = frame.Payload.Length == 2
                        ? BinaryPrimitives.ReadUInt16LittleEndian(frame.Payload)
                        : BinaryPrimitives.ReadUInt32LittleEndian(frame.Payload);
                    throw new MtkProtocolException("Storage read ended before the requested data was received.", status);
                }
                if (frame.Payload.Length == 0 || frame.Payload.Length > storage.ReadPacketLength
                    || frame.Payload.Length > length - transferred || frame.Payload.Length % storage.BlockSize != 0)
                {
                    throw new MtkProtocolException("The storage data frame violates the requested range or negotiated packet size.");
                }
                await destination.WriteAsync(frame.Payload, cancellationToken).ConfigureAwait(false);
                await SendXFlashWordAsync(channel, 0, cancellationToken).ConfigureAwait(false);
                transferred += frame.Payload.Length;
                progress?.Report(transferred);
            }
            await ReadXFlashStatusAsync(channel, "storage read completion", cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfDisposed();
        }
        catch
        {
            FaultXFlashStorage();
            throw;
        }
    }

    /// <summary>
    /// Writes exactly an aligned byte range from a caller-owned stream. No padding, retry,
    /// rollback, vendor optional-download action or readback verification is implied.
    /// </summary>
    /// <param name="partitionId">The discovered XFlash physical partition identifier, excluding RPMB.</param>
    /// <param name="offset">The block-aligned byte offset within the physical partition.</param>
    /// <param name="length">The positive block-aligned byte count.</param>
    /// <param name="source">A readable stream positioned at the first source byte; remains open.</param>
    /// <param name="progress">Optional cumulative bytes acknowledged by the agent.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    public async Task WriteXFlashStorageAsync(uint partitionId, long offset, long length, Stream source,
        IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        if (!source.CanRead || (source.CanSeek && source.Length - source.Position < length))
        {
            throw new ArgumentException("The storage source is unreadable or shorter than the requested range.", nameof(source));
        }
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteXFlashStorageOwnedAsync(partitionId, offset, length, source, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operation.Release();
        }
    }

    private async Task WriteXFlashStorageOwnedAsync(uint partitionId, long offset, long length, Stream source,
        IProgress<long>? progress, CancellationToken cancellationToken)
    {
        RequireState(MtkSessionState.XFlashStorageReady);
        cancellationToken.ThrowIfCancellationRequested();
        var storage = XFlashStorage ?? throw new InvalidOperationException("Storage has not been discovered.");
        storage.ValidateRange(partitionId, offset, length);
        var channel = _xflash ?? throw new InvalidOperationException("XFlash was not initialized.");
        try
        {
            await BeginXFlashTransferAsync(channel, storage, true, partitionId, offset, length, cancellationToken).ConfigureAwait(false);
            var buffer = new byte[storage.WritePacketLength];
            long transferred = 0;
            while (transferred < length)
            {
                var count = (int)Math.Min(buffer.Length, length - transferred);
                await source.ReadExactlyAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                uint checksum = 0;
                for (var index = 0; index < count; index++)
                {
                    checksum += buffer[index];
                }
                await SendXFlashWordAsync(channel, 0, cancellationToken).ConfigureAwait(false);
                await SendXFlashWordAsync(channel, checksum & 0xFFFF, cancellationToken).ConfigureAwait(false);
                using var payload = new MemoryStream(buffer, 0, count, writable: false);
                await channel.SendStreamAsync(1, payload, count, null, cancellationToken).ConfigureAwait(false);
                await ReadXFlashStatusAsync(channel, "storage write packet", cancellationToken).ConfigureAwait(false);
                transferred += count;
                progress?.Report(transferred);
            }
            await ReadXFlashStatusAsync(channel, "storage write completion", cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfDisposed();
        }
        catch
        {
            FaultXFlashStorage();
            throw;
        }
    }

    private async Task BeginXFlashTransferAsync(MtkXFlashChannel channel, MtkXFlashStorageInfo storage, bool write,
        uint partitionId, long offset, long length, CancellationToken cancellationToken)
    {
        await SendXFlashWordAsync(channel, write ? 0x010004u : 0x010005u, cancellationToken).ConfigureAwait(false);
        await ReadXFlashStatusAsync(channel, "storage request", cancellationToken).ConfigureAwait(false);
        var parameters = CreateXFlashStorageParameters(storage, partitionId, offset, length);
        await channel.SendAsync(1, parameters, cancellationToken).ConfigureAwait(false);
        await ReadXFlashStatusAsync(channel, "storage parameters", cancellationToken).ConfigureAwait(false);
        if (!write)
        {
            await ReadXFlashStatusAsync(channel, "storage read start", cancellationToken).ConfigureAwait(false);
        }
    }

    private static byte[] CreateXFlashStorageParameters(MtkXFlashStorageInfo storage, uint partitionId, long offset, long length)
    {
        var parameters = new byte[56];
        BinaryPrimitives.WriteUInt32LittleEndian(parameters, storage.Kind == MtkXFlashStorageKind.Emmc ? 1u : 0x30u);
        BinaryPrimitives.WriteUInt32LittleEndian(parameters.AsSpan(4), partitionId);
        BinaryPrimitives.WriteUInt64LittleEndian(parameters.AsSpan(8), (ulong)offset);
        BinaryPrimitives.WriteUInt64LittleEndian(parameters.AsSpan(16), (ulong)length);
        // Eight NAND extension fields are unused and zero for the supported eMMC/UFS families.
        return parameters;
    }

    private void FaultXFlashStorage()
    {
        _state = MtkSessionState.Faulted;
        _xflash = null;
        XFlashStorage = null;
    }
}