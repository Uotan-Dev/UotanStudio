using UotanStudio.Comm.Protocols.Common;

namespace UotanStudio.Comm.Protocols.Mtk;

public sealed partial class MtkSession
{
    /// <summary>Reads to a unique sibling staging file and replaces the destination only after complete protocol success. The destination directory must already exist.
    /// <para>读取到同目录下一个唯一的暂存文件，仅在协议完全成功后替换目标文件。 目标目录必须已经存在。</para></summary>
    /// <param name="partitionId">The discovered XFlash physical partition identifier.
    /// <para>已发现的 XFlash 物理分区标识符。</para></param>
    /// <param name="offset">The block-aligned byte offset within the physical partition.
    /// <para>物理分区内按块对齐的字节偏移。</para></param>
    /// <param name="length">The positive block-aligned byte count.<para>正数、按块对齐的字节数。</para></param>
    /// <param name="destinationPath">The file to publish after successful completion.
    /// <para>成功完成后发布的文件路径。</para></param>
    /// <param name="progress">Optional cumulative transferred bytes; full progress is not publication success.
    /// <para>可选的累计传输字节数；进度跑满不代表发布成功。</para></param>
    /// <param name="cancellationToken">The operation cancellation token.<para>操作取消令牌。</para></param>
    /// <returns>A task that completes once the staging file replaced the destination.
    /// <para>在暂存文件替换目标文件后完成的异步任务。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="destinationPath"/> is blank.
    /// <para><paramref name="destinationPath"/> 为空白。</para></exception>
    /// <exception cref="InvalidOperationException">Storage is not ready or has not been discovered.
    /// <para>存储未就绪或尚未发现。</para></exception>
    /// <exception cref="MtkProtocolException">The storage read failed; the session faults.
    /// <para>存储读取失败；会话进入 fault 状态。</para></exception>
    /// <exception cref="IOException">The staging file cannot be created, the staged length is wrong, or publication failed.
    /// <para>无法创建暂存文件、暂存长度不符或发布失败。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled the read or publication preparation.
    /// <para>调用方取消了读取或发布准备。</para></exception>
    public Task ReadXFlashStorageToFileAsync(uint partitionId, long offset, long length, string destinationPath,
        IProgress<long>? progress = null, CancellationToken cancellationToken = default)
        => StorageFilePublisher.PublishAsync(destinationPath, length,
            output => ReadXFlashStorageAsync(partitionId, offset, length, output, progress, cancellationToken), cancellationToken);

    /// <summary>Compares a device range with exactly the requested bytes at the source's current position. Mismatches do not interrupt reception; only a fully completed exchange produces a result. This is a separate read operation, not an atomic write-and-verify transaction.
    /// <para>将设备范围与源流当前位置起恰好请求的字节数进行比较。不匹配不会中断接收； 只有完整结束的交换才产生结果。这是独立的读取操作，不是原子的写入并校验事务。</para></summary>
    /// <param name="partitionId">The discovered XFlash physical partition identifier.
    /// <para>已发现的 XFlash 物理分区标识符。</para></param>
    /// <param name="offset">The block-aligned byte offset within the physical partition.
    /// <para>物理分区内按块对齐的字节偏移。</para></param>
    /// <param name="length">The positive block-aligned byte count.<para>正数、按块对齐的字节数。</para></param>
    /// <param name="expected">A readable caller-owned reference stream which remains open.
    /// <para>调用方拥有、保持打开的可读参考流。</para></param>
    /// <param name="progress">Optional cumulative compared and acknowledged bytes.
    /// <para>可选的累计已比较并已确认字节数。</para></param>
    /// <param name="cancellationToken">The operation cancellation token.<para>操作取消令牌。</para></param>
    /// <returns>Full comparison evidence, including the first relative mismatch offset.
    /// <para>完整的比较证据，包含首个相对不匹配偏移。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="expected"/> is null.
    /// <para><paramref name="expected"/> 为 null。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is not positive.
    /// <para><paramref name="length"/> 不是正数。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="expected"/> is unreadable or shorter than the requested range.
    /// <para><paramref name="expected"/> 不可读或短于请求的范围。</para></exception>
    /// <exception cref="InvalidOperationException">Storage is not ready or has not been discovered.
    /// <para>存储未就绪或尚未发现。</para></exception>
    /// <exception cref="MtkProtocolException">The read failed, or the reference stream ended before the exchange did.
    /// <para>读取失败，或参考流在交换结束前提前结束。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled the read or comparison.
    /// <para>调用方取消了读取或比较。</para></exception>
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