using UotanStudio.Comm.Protocols.Common;
using UotanStudio.Firmware.BoundedContexts.BlockImage.Gpt;

namespace UotanStudio.Comm.Protocols.Mtk;

public sealed partial class MtkSession
{
    /// <summary>Reads both GPT copies under one operation lease, without repair or primary-only fallback.</summary>
    /// <param name="partitionId">Explicit discovered physical region, not a GPT entry index.</param>
    /// <param name="cancellationToken">Cancellation propagated to every storage exchange.</param>
    /// <returns>A validated snapshot bound to the selected storage geometry and physical region.</returns>
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