namespace UotanStudio.Firmware.Core.Streams;

/// <summary>
/// Writes bulk zero runs and page-alignment padding to a stream in bounded chunks instead of byte-at-a-time loops.
/// <para>以有界分块方式向流批量写入零字节与页对齐填充，替代逐字节写入循环。</para>
/// </summary>
internal static class ZeroFill
{
    private const int ChunkSize = 65536;
    private static readonly byte[] Zeros = new byte[ChunkSize];

    /// <summary>
    /// Writes <paramref name="count"/> zero bytes to the stream synchronously.
    /// <para>同步向流写入 <paramref name="count"/> 个零字节。</para>
    /// </summary>
    /// <param name="stream">Writable target stream; it is not flushed.
    /// <para>可写目标流；本方法不会刷新它。</para></param>
    /// <param name="count">Number of zero bytes to write; zero writes nothing.
    /// <para>需要写入的零字节数量；为 0 时不写入任何内容。</para></param>
    /// <param name="cancellationToken">Token checked before each chunk transfer.
    /// <para>在每块传输前检查的取消令牌。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.
    /// <para><paramref name="stream"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative.
    /// <para><paramref name="count"/> 为负数。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled.
    /// <para><paramref name="cancellationToken"/> 已被触发。</para></exception>
    internal static void WriteZeros(Stream stream, long count, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        var remaining = count;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = (int)Math.Min(remaining, Zeros.Length);
            stream.Write(Zeros, 0, chunk);
            remaining -= chunk;
        }
    }

    /// <summary>
    /// Writes <paramref name="count"/> zero bytes to the stream asynchronously.
    /// <para>异步向流写入 <paramref name="count"/> 个零字节。</para>
    /// </summary>
    /// <param name="stream">Writable target stream; it is not flushed.
    /// <para>可写目标流；本方法不会刷新它。</para></param>
    /// <param name="count">Number of zero bytes to write; zero completes the task without touching the stream.
    /// <para>需要写入的零字节数量；为 0 时不访问流即完成。</para></param>
    /// <param name="cancellationToken">Token forwarded to each chunk transfer.
    /// <para>传递给每块传输的取消令牌。</para></param>
    /// <returns>A task that completes once all zero bytes have been written.
    /// <para>在全部零字节写入后完成的任务。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.
    /// <para><paramref name="stream"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative.
    /// <para><paramref name="count"/> 为负数。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled.
    /// <para><paramref name="cancellationToken"/> 已被触发。</para></exception>
    internal static async Task WriteZerosAsync(Stream stream, long count, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        var remaining = count;
        while (remaining > 0)
        {
            var chunk = (int)Math.Min(remaining, Zeros.Length);
            await stream.WriteAsync(Zeros.AsMemory(0, chunk), cancellationToken).ConfigureAwait(false);
            remaining -= chunk;
        }
    }

    /// <summary>
    /// Writes zero bytes until the stream position reaches <paramref name="targetPosition"/>; a position already at or
    /// beyond the target is left untouched.
    /// <para>写入零字节直至流位置达到 <paramref name="targetPosition"/>；位置已等于或超过目标时不做任何写入。</para>
    /// </summary>
    /// <param name="stream">Writable target stream positioned before the target.
    /// <para>定位在目标之前的可写目标流。</para></param>
    /// <param name="targetPosition">Absolute stream position to pad up to.
    /// <para>需要填充到的绝对流位置。</para></param>
    /// <param name="cancellationToken">Token checked before each chunk transfer.
    /// <para>在每块传输前检查的取消令牌。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.
    /// <para><paramref name="stream"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="NotSupportedException">The stream does not support seeking.
    /// <para>流不支持查找。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled.
    /// <para><paramref name="cancellationToken"/> 已被触发。</para></exception>
    internal static void FillTo(Stream stream, long targetPosition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        WriteZeros(stream, Math.Max(0, targetPosition - stream.Position), cancellationToken);
    }

    /// <summary>
    /// Asynchronously writes zero bytes until the stream position reaches <paramref name="targetPosition"/>; a position
    /// already at or beyond the target is left untouched.
    /// <para>异步写入零字节直至流位置达到 <paramref name="targetPosition"/>；位置已等于或超过目标时不做任何写入。</para>
    /// </summary>
    /// <param name="stream">Writable target stream positioned before the target.
    /// <para>定位在目标之前的可写目标流。</para></param>
    /// <param name="targetPosition">Absolute stream position to pad up to.
    /// <para>需要填充到的绝对流位置。</para></param>
    /// <param name="cancellationToken">Token forwarded to each chunk transfer.
    /// <para>传递给每块传输的取消令牌。</para></param>
    /// <returns>A task that completes once the padding has been written.
    /// <para>在填充字节写入后完成的任务。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.
    /// <para><paramref name="stream"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="NotSupportedException">The stream does not support seeking.
    /// <para>流不支持查找。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled.
    /// <para><paramref name="cancellationToken"/> 已被触发。</para></exception>
    internal static async Task FillToAsync(Stream stream, long targetPosition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        await WriteZerosAsync(stream, Math.Max(0, targetPosition - stream.Position), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes zero bytes until the stream position is a multiple of <paramref name="pageSize"/>.
    /// <para>写入零字节直至流位置为 <paramref name="pageSize"/> 的整数倍。</para>
    /// </summary>
    /// <param name="stream">Writable target stream positioned at the end of the last segment.
    /// <para>定位在上一区段末尾的可写目标流。</para></param>
    /// <param name="position">Absolute stream position to align from, usually <c>stream.Position</c>.
    /// <para>需要对齐的绝对流位置，通常为 <c>stream.Position</c>。</para></param>
    /// <param name="pageSize">Page size in bytes; must be non-zero.
    /// <para>页大小（字节），必须非零。</para></param>
    /// <param name="cancellationToken">Token checked before each chunk transfer.
    /// <para>在每块传输前检查的取消令牌。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.
    /// <para><paramref name="stream"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pageSize"/> is zero.
    /// <para><paramref name="pageSize"/> 为 0。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled.
    /// <para><paramref name="cancellationToken"/> 已被触发。</para></exception>
    internal static void AlignToPage(Stream stream, long position, uint pageSize, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfZero(pageSize);
        WriteZeros(stream, PaddingLength(position, pageSize), cancellationToken);
    }

    /// <summary>
    /// Asynchronously writes zero bytes until the stream position is a multiple of <paramref name="pageSize"/>.
    /// <para>异步写入零字节直至流位置为 <paramref name="pageSize"/> 的整数倍。</para>
    /// </summary>
    /// <param name="stream">Writable target stream positioned at the end of the last segment.
    /// <para>定位在上一区段末尾的可写目标流。</para></param>
    /// <param name="position">Absolute stream position to align from, usually <c>stream.Position</c>.
    /// <para>需要对齐的绝对流位置，通常为 <c>stream.Position</c>。</para></param>
    /// <param name="pageSize">Page size in bytes; must be non-zero.
    /// <para>页大小（字节），必须非零。</para></param>
    /// <param name="cancellationToken">Token forwarded to each chunk transfer.
    /// <para>传递给每块传输的取消令牌。</para></param>
    /// <returns>A task that completes once the alignment padding has been written.
    /// <para>在对齐填充字节写入后完成的任务。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.
    /// <para><paramref name="stream"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pageSize"/> is zero.
    /// <para><paramref name="pageSize"/> 为 0。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled.
    /// <para><paramref name="cancellationToken"/> 已被触发。</para></exception>
    internal static async Task AlignToPageAsync(Stream stream, long position, uint pageSize, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfZero(pageSize);
        await WriteZerosAsync(stream, PaddingLength(position, pageSize), cancellationToken).ConfigureAwait(false);
    }

    private static long PaddingLength(long position, uint pageSize)
    {
        var remainder = position % pageSize;
        return remainder == 0 ? 0 : pageSize - remainder;
    }
}