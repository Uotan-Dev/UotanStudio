using UotanStudio.Comm.Protocols.Common;

namespace UotanStudio.Comm.Protocols.Mtk;

public sealed partial class MtkSession
{
    /// <summary>Reads an XML storage range to a sibling staging file and publishes only after full protocol success.</summary>
    /// <remarks>The existing destination is preserved on transfer failure. Its directory must already exist.
    /// Full byte progress is not final handshake or publication success.</remarks>
    /// <param name="regionName">The exact discovered XML physical-region token.</param>
    /// <param name="offset">The nonnegative block-aligned byte offset.</param>
    /// <param name="length">The positive block-aligned byte count.</param>
    /// <param name="destinationPath">The file to publish after a complete read.</param>
    /// <param name="timeoutMs">Cooperative protocol deadline, from one millisecond through 24 hours.</param>
    /// <param name="progress">Optional cumulative bytes delivered and acknowledged.</param>
    /// <param name="cancellationToken">Cancellation for the read and publication preparation.</param>
    public Task ReadXmlStorageToFileAsync(string regionName, long offset, long length, string destinationPath,
        int timeoutMs = 120000, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
        => StorageFilePublisher.PublishAsync(destinationPath, length,
            output => ReadXmlStorageAsync(regionName, offset, length, output, timeoutMs, progress, cancellationToken), cancellationToken);

    /// <summary>Compares a complete XML read with a caller-owned reference stream without stopping at the first mismatch.</summary>
    /// <remarks>A result requires final protocol success. This is a separate read, not an atomic write-and-verify transaction.
    /// The reference remains open, with its position advanced by the compared bytes.</remarks>
    /// <param name="regionName">The exact discovered XML physical-region token.</param>
    /// <param name="offset">The nonnegative block-aligned device byte offset.</param>
    /// <param name="length">The positive block-aligned byte count.</param>
    /// <param name="expected">Readable reference positioned at the first expected byte.</param>
    /// <param name="timeoutMs">Cooperative protocol deadline, from one millisecond through 24 hours.</param>
    /// <param name="progress">Optional cumulative compared and acknowledged bytes.</param>
    /// <param name="cancellationToken">Cancellation for the full read and comparison.</param>
    /// <returns>Comparison evidence including the first relative mismatch offset.</returns>
    public async Task<MtkStorageVerificationResult> VerifyXmlStorageAsync(string regionName, long offset, long length,
        Stream expected, int timeoutMs = 120000, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        if (!expected.CanRead || (expected.CanSeek && expected.Length - expected.Position < length))
        {
            throw new ArgumentException("The XML reference stream is unreadable or shorter than the selected range.", nameof(expected));
        }
        using var comparison = new StorageComparisonStream(expected);
        await ReadXmlStorageAsync(regionName, offset, length, comparison, timeoutMs, progress, cancellationToken).ConfigureAwait(false);
        return new MtkStorageVerificationResult(comparison.ComparedBytes, comparison.FirstMismatchOffset);
    }
}