using UotanStudio.Comm.Protocols.Common;

namespace UotanStudio.Comm.Protocols.Edl;

public sealed partial class FirehoseProtocol
{
    /// <summary>Revalidates GPT and backs up an exactly named partition using one operation lease.</summary>
    /// <remarks>
    /// The destination is replaced only after final ACK, complete staging output and flush.
    /// No slot inference, repair or retry occurs. This does not freeze target contents or
    /// guarantee filesystem consistency. Progress callbacks must not wait on this protocol.
    /// </remarks>
    /// <param name="snapshot">GPT observation from this protocol instance and current slot/sector configuration.</param>
    /// <param name="partitionName">Exact, case-sensitive GPT partition name.</param>
    /// <param name="destinationPath">Output file in an existing directory.</param>
    /// <param name="progress">Cumulative partition bytes, excluding metadata; full progress is not publication success.</param>
    /// <param name="cancellationToken">Cancellation for waiting, metadata, data and publication preparation.</param>
    public async Task ReadPartitionToFileAsync(FirehoseGptSnapshot snapshot, string partitionName, string destinationPath,
        IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        cancellationToken.ThrowIfCancellationRequested();
        var partition = snapshot.Table.FindPartition(partitionName);
        using var operation = await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        ValidateGptBinding(snapshot);
        await StorageFilePublisher.PublishAsync(destinationPath, partition.ByteLength, async output =>
        {
            await RevalidateGptOwnedAsync(snapshot, cancellationToken).ConfigureAwait(false);
            var response = await ReadToStreamCoreAsync(output, checked((long)partition.FirstLba),
                partition.ByteLength / snapshot.Geometry.SectorSize, "partition.bin",
                snapshot.Geometry.PhysicalPartitionNumber, cancellationToken, progress).ConfigureAwait(false);
            if (!response.IsAck)
            {
                throw new FirehoseException("The partition backup did not complete successfully.");
            }
            ThrowIfDisposed();
            ValidateGptBinding(snapshot);
        }, cancellationToken).ConfigureAwait(false);
    }
}