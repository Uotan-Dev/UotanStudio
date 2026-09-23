using UotanStudio.Comm.Protocols.Common;

namespace UotanStudio.Comm.Protocols.Mtk;

public sealed partial class MtkSession
{
    /// <summary>
    /// Reads to a unique sibling staging file and replaces the destination only after
    /// complete protocol success. The destination directory must already exist.
    /// </summary>
    /// <param name="partitionId">The discovered XFlash physical partition identifier.</param>
    /// <param name="offset">The block-aligned byte offset within the physical partition.</param>
    /// <param name="length">The positive block-aligned byte count.</param>
    /// <param name="destinationPath">The file to publish after successful completion.</param>
    /// <param name="progress">Optional cumulative transferred bytes; full progress is not publication success.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    public Task ReadXFlashStorageToFileAsync(uint partitionId, long offset, long length, string destinationPath,
        IProgress<long>? progress = null, CancellationToken cancellationToken = default)
        => StorageFilePublisher.PublishAsync(destinationPath, length,
            output => ReadXFlashStorageAsync(partitionId, offset, length, output, progress, cancellationToken), cancellationToken);

    /// <summary>
    /// Compares a device range with exactly the requested bytes at the source's current position.
    /// Mismatches do not interrupt reception; only a fully completed exchange produces a result.
    /// This is a separate read operation, not an atomic write-and-verify transaction.
    /// </summary>
    /// <param name="partitionId">The discovered XFlash physical partition identifier.</param>
    /// <param name="offset">The block-aligned byte offset within the physical partition.</param>
    /// <param name="length">The positive block-aligned byte count.</param>
    /// <param name="expected">A readable caller-owned reference stream which remains open.</param>
    /// <param name="progress">Optional cumulative compared and acknowledged bytes.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>Full comparison evidence, including the first relative mismatch offset.</returns>
    public async Task<MtkStorageVerificationResult> VerifyXFlashStorageAsync(uint partitionId, long offset, long length,
        Stream expected, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        if (!expected.CanRead || (expected.CanSeek && expected.Length - expected.Position < length))
        {
            throw new ArgumentException("The reference stream is unreadable or shorter than the requested range.", nameof(expected));
        }
        using var comparison = new StorageComparisonStream(expected);
        await ReadXFlashStorageAsync(partitionId, offset, length, comparison, progress, cancellationToken).ConfigureAwait(false);
        return new MtkStorageVerificationResult(comparison.ComparedBytes, comparison.FirstMismatchOffset);
    }

    private sealed class StorageComparisonStream(Stream expected) : Stream
    {
        private readonly byte[] _buffer = new byte[65536];
        internal long ComparedBytes { get; private set; }
        internal long? FirstMismatchOffset { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var position = 0;
            while (position < buffer.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = Math.Min(_buffer.Length, buffer.Length - position);
                await expected.ReadExactlyAsync(_buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                if (FirstMismatchOffset is null)
                {
                    for (var index = 0; index < count; index++)
                    {
                        if (_buffer[index] != buffer.Span[position + index])
                        {
                            FirstMismatchOffset = ComparedBytes + index;
                            break;
                        }
                    }
                }
                ComparedBytes += count;
                position += count;
            }
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}