using UotanStudio.Comm.Protocols.Common;
using UotanStudio.Firmware.BoundedContexts.BlockImage.Gpt;

namespace UotanStudio.Comm.Protocols.Edl;

public sealed partial class FirehoseProtocol
{
    /// <summary>Queries physical geometry and validates both GPT copies under one operation lease.</summary>
    /// <remarks>
    /// The configured sector size must match the queried region. No geometry fallback,
    /// GPT repair or primary-only acceptance is performed. Configuration must not be mutated
    /// during operations. An observation does not freeze device contents or authorize writes.
    /// </remarks>
    /// <param name="physicalPartition">The explicitly selected physical region or LUN.</param>
    /// <param name="cancellationToken">Cancellation propagated to geometry and every block read.</param>
    /// <returns>A snapshot bound to this protocol instance and configured slot.</returns>
    public async Task<FirehoseGptSnapshot> ReadGptAsync(int physicalPartition = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(physicalPartition);
        using var operation = await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        return await ReadGptOwnedAsync(physicalPartition, cancellationToken).ConfigureAwait(false);
    }

    private void ValidateGptBinding(FirehoseGptSnapshot snapshot)
    {
        if (!ReferenceEquals(snapshot.Owner, this) || snapshot.Slot != Configuration.Slot
            || snapshot.Geometry.SectorSize != Configuration.SectorSize)
        {
            throw new InvalidOperationException("The GPT snapshot belongs to a different protocol or storage configuration.");
        }
    }

    private async Task RevalidateGptOwnedAsync(FirehoseGptSnapshot snapshot, CancellationToken cancellationToken)
    {
        ValidateGptBinding(snapshot);
        var current = await ReadGptOwnedAsync(snapshot.Geometry.PhysicalPartitionNumber, cancellationToken).ConfigureAwait(false);
        if (current.Geometry != snapshot.Geometry || current.Slot != snapshot.Slot
            || current.Table.MetadataSha256 != snapshot.Table.MetadataSha256)
        {
            throw new InvalidDataException("The GPT observation changed; no named partition operation was started.");
        }
        ValidateGptBinding(snapshot);
    }

    private async Task<FirehoseGptSnapshot> ReadGptOwnedAsync(int physicalPartition, CancellationToken cancellationToken)
    {
        var slot = Configuration.Slot;
        var geometry = await GetStorageGeometryOwnedAsync(physicalPartition, cancellationToken).ConfigureAwait(false);
        ValidateConfiguration();
        var table = await GptReader.ReadAsync(ReadBlocksAsync, checked((int)geometry.SectorSize),
            (ulong)geometry.SectorCount, cancellationToken).ConfigureAwait(false);
        ValidateConfiguration();
        return new FirehoseGptSnapshot(this, geometry, slot, table);

        void ValidateConfiguration()
        {
            if (Configuration.SectorSize != geometry.SectorSize || Configuration.Slot != slot)
            {
                throw new InvalidDataException("Firehose configuration does not match the GPT storage observation.");
            }
        }

        async ValueTask ReadBlocksAsync(long offset, Memory<byte> destination, CancellationToken token)
        {
            ValidateConfiguration();
            using var output = new FixedBufferWriteStream(destination);
            var response = await ReadToStreamCoreAsync(output, offset / geometry.SectorSize,
                destination.Length / geometry.SectorSize, "gpt.bin", physicalPartition, token).ConfigureAwait(false);
            if (!response.IsAck || output.Written != destination.Length)
            {
                throw new FirehoseException("The GPT block read did not complete successfully.");
            }
        }
    }
}