using UotanStudio.Comm.Protocols.Common;

namespace UotanStudio.Comm.Protocols.Mtk;

public sealed partial class MtkSession
{
    /// <summary>
    /// Backs up an exactly named GPT partition after revalidating the snapshot under the
    /// same operation lease as the data read. No repair, slot inference or retry is performed.
    /// </summary>
    /// <remarks>
    /// The destination is replaced only after successful transfer and staging-file flush.
    /// This does not freeze device contents or promise a point-in-time storage snapshot.
    /// Progress excludes metadata reads; callbacks must not wait on this session.
    /// </remarks>
    /// <param name="snapshot">A GPT observation from this session's current storage geometry.</param>
    /// <param name="partitionName">Exact case-sensitive name, rejecting absent or ambiguous entries.</param>
    /// <param name="destinationPath">Output file in an existing directory.</param>
    /// <param name="progress">Cumulative partition bytes; full progress is not publication success.</param>
    /// <param name="cancellationToken">Cancellation for waiting, metadata, data and publication preparation.</param>
    public async Task ReadXFlashPartitionToFileAsync(MtkGptSnapshot snapshot, string partitionName,
        string destinationPath, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        cancellationToken.ThrowIfCancellationRequested();
        var partition = snapshot.Table.FindPartition(partitionName);
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireState(MtkSessionState.XFlashStorageReady);
            if (!ReferenceEquals(snapshot.Storage, XFlashStorage))
            {
                throw new InvalidOperationException("The GPT snapshot belongs to a different storage observation.");
            }
            await StorageFilePublisher.PublishAsync(destinationPath, partition.ByteLength, async output =>
            {
                await RevalidateXFlashGptOwnedAsync(snapshot, cancellationToken).ConfigureAwait(false);
                await ReadXFlashStorageOwnedAsync(snapshot.PartitionId, partition.ByteOffset, partition.ByteLength,
                    output, progress, cancellationToken).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operation.Release();
        }
    }
}