namespace UotanStudio.Comm.Protocols.Edl;

/// <summary>Evidence for a completed named raw write; ACK alone does not prove persistence.</summary>
/// <param name="AcknowledgedBytes">Bytes whose program operation received the final ACK.</param>
/// <param name="VerifiedBytes">Bytes subsequently read back and matched, or zero when verification was not requested.</param>
public sealed record FirehosePartitionWriteResult(long AcknowledgedBytes, long VerifiedBytes);

public sealed partial class FirehoseProtocol
{
    /// <summary>Writes a retained raw file into an exactly named GPT partition, optionally verifying by readback.</summary>
    /// <remarks>
    /// This is an explicit destructive command, not a preview. The caller must authorize it.
    /// GPT revalidation, write and optional readback share one lease. No erase, padding,
    /// sparse expansion, slot inference, retry or rollback occurs. Failure can follow a
    /// successful write and must not be interpreted as evidence that storage is unchanged.
    /// A retained file handle is not a cross-platform immutable content snapshot.
    /// </remarks>
    /// <param name="snapshot">Validated GPT observation from this protocol and storage configuration.</param>
    /// <param name="partitionName">Exact case-sensitive GPT name.</param>
    /// <param name="sourcePath">Explicit raw image file; its complete length must be positive and sector-aligned.</param>
    /// <param name="partitionOffset">Sector-aligned byte offset relative to the named partition.</param>
    /// <param name="verify">Whether to require full readback comparison before returning success.</param>
    /// <param name="cancellationToken">Cancellation for preflight, lease, metadata and transfers.</param>
    /// <returns>Final-ACK byte count and optional readback byte count.</returns>
    public async Task<FirehosePartitionWriteResult> ProgramRawPartitionFromFileAsync(FirehoseGptSnapshot snapshot,
        string partitionName, string sourcePath, long partitionOffset = 0, bool verify = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        cancellationToken.ThrowIfCancellationRequested();
        var partition = snapshot.Table.FindPartition(partitionName);
        ArgumentOutOfRangeException.ThrowIfNegative(partitionOffset);
        var sectorSize = snapshot.Geometry.SectorSize;
        if (partitionOffset % sectorSize != 0 || partitionOffset >= partition.ByteLength)
        {
            throw new ArgumentOutOfRangeException(nameof(partitionOffset));
        }
        using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var length = source.Length;
        if (length == 0 || length % sectorSize != 0 || length > partition.ByteLength - partitionOffset)
        {
            throw new InvalidDataException("The raw source must fit the selected GPT range without padding.");
        }
        using var operation = await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        await RevalidateGptOwnedAsync(snapshot, cancellationToken).ConfigureAwait(false);
        if (source.Length != length)
        {
            throw new InvalidDataException("The retained raw source length changed before programming.");
        }
        var start = checked((long)partition.FirstLba + partitionOffset / sectorSize);
        var sectors = length / sectorSize;
        var region = snapshot.Geometry.PhysicalPartitionNumber;
        await ProgramCoreAsync(source, new FirehoseSectorAddress(start, false), sectors, "partition.bin",
            region, cancellationToken).ConfigureAwait(false);
        ValidateGptBinding(snapshot);
        if (verify)
        {
            if (source.Length != length)
            {
                throw new InvalidDataException("The raw source length changed after the acknowledged write.");
            }
            using var comparison = new ReadBackComparisonStream(source, partitionName);
            await ReadToStreamCoreAsync(comparison, start, sectors, "partition.bin", region,
                cancellationToken).ConfigureAwait(false);
            await comparison.CompleteAsync(cancellationToken).ConfigureAwait(false);
        }
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        ValidateGptBinding(snapshot);
        return new FirehosePartitionWriteResult(length, verify ? length : 0);
    }
}