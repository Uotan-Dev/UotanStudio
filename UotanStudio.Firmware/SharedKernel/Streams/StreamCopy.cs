namespace UotanStudio.Firmware.SharedKernel.Streams;

using System.Buffers;

/// <summary>
/// Copies byte counts that a container header already declared, so a short input is an error rather than a truncation.
/// <para>按容器头部已声明的字节数进行复制，因此输入提前结束会被视为错误而不是正常截断。</para>
/// </summary>
public static class StreamCopy
{
    /// <summary>
    /// Copies exactly the specified byte count and throws if the input ends early.
    /// <para>精确复制指定字节数，输入提前结束时抛出异常。</para>
    /// </summary>
    /// <param name="source">Readable stream positioned at the first byte of the region.
    /// <para>定位到待复制区域首字节、可读的源流。</para></param>
    /// <param name="destination">Writable stream receiving the copied bytes; it is not flushed.
    /// <para>接收复制字节的可写目标流；本方法不会刷新它。</para></param>
    /// <param name="count">Exact number of bytes to copy; zero copies nothing.
    /// <para>需要复制的精确字节数；为 0 时不复制任何内容。</para></param>
    /// <param name="bufferSize">Size in bytes of the rented transfer buffer; must be at least 1.
    /// <para>租借的传输缓冲区大小（字节），必须不小于 1。</para></param>
    /// <exception cref="ArgumentNullException"><c>source</c> or <c>destination</c> is <see langword="null"/>.
    /// <para><c>source</c> 或 <c>destination</c> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><c>count</c> is negative or <c>bufferSize</c> is below 1.
    /// <para><c>count</c> 为负数，或 <c>bufferSize</c> 小于 1。</para></exception>
    /// <exception cref="ArgumentException">The source is not readable or the destination is not writable.
    /// <para>源流不可读或目标流不可写。</para></exception>
    /// <exception cref="EndOfStreamException">The source reached its end before <c>count</c> bytes were copied.
    /// <para>源流在复制满 <c>count</c> 字节之前已经结束。</para></exception>
    public static void CopyExactly(Stream source, Stream destination, long count, int bufferSize = 81920)
        => CopyExactly(source, destination, count, CancellationToken.None, bufferSize);

    /// <summary>
    /// Copies exactly the specified byte count with cancellation between buffer transfers.
    /// The transfer buffer is rented from <see cref="ArrayPool{Byte}.Shared"/> instead of being
    /// allocated per call; this method is called at high frequency by extractors.
    /// <para>精确复制指定字节数，并在缓冲传输之间检查取消。传输缓冲改由
    /// <see cref="ArrayPool{Byte}.Shared"/> 租借而非每次分配；提取器会高频调用本方法。</para>
    /// </summary>
    /// <param name="source">Readable stream positioned at the first byte of the region.
    /// <para>定位到待复制区域首字节、可读的源流。</para></param>
    /// <param name="destination">Writable stream receiving the copied bytes; it is not flushed.
    /// <para>接收复制字节的可写目标流；本方法不会刷新它。</para></param>
    /// <param name="count">Exact number of bytes to copy; zero only checks the token.
    /// <para>需要复制的精确字节数；为 0 时仅检查取消令牌。</para></param>
    /// <param name="cancellationToken">Token tested before the transfer, between buffer transfers, and after the last write.
    /// <para>在传输前、各缓冲传输之间以及最后一次写入之后检查的取消令牌。</para></param>
    /// <param name="bufferSize">Size in bytes of the rented transfer buffer; must be at least 1.
    /// <para>租借的传输缓冲区大小（字节），必须不小于 1。</para></param>
    /// <exception cref="ArgumentNullException"><c>source</c> or <c>destination</c> is <see langword="null"/>.
    /// <para><c>source</c> 或 <c>destination</c> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><c>count</c> is negative or <c>bufferSize</c> is below 1.
    /// <para><c>count</c> 为负数，或 <c>bufferSize</c> 小于 1。</para></exception>
    /// <exception cref="ArgumentException">The source is not readable or the destination is not writable.
    /// <para>源流不可读或目标流不可写。</para></exception>
    /// <exception cref="OperationCanceledException"><c>cancellationToken</c> was signaled.
    /// <para><c>cancellationToken</c> 已被触发。</para></exception>
    /// <exception cref="EndOfStreamException">The source reached its end before <c>count</c> bytes were copied.
    /// <para>源流在复制满 <c>count</c> 字节之前已经结束。</para></exception>
    public static void CopyExactly(Stream source, Stream destination, long count,
        CancellationToken cancellationToken, int bufferSize = 81920)
    {
        Validate(source, destination, count, bufferSize);
        cancellationToken.ThrowIfCancellationRequested();
        var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        try
        {
            while (count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = source.Read(buffer, 0, (int)Math.Min(count, buffer.Length));
                if (read == 0) throw new EndOfStreamException("The source stream ended before the requested byte count was copied.");
                destination.Write(buffer, 0, read);
                count -= read;
            }
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Asynchronously copies exactly the specified byte count with cancellation support and throws if the input ends early.
    /// The transfer buffer is rented from <see cref="ArrayPool{Byte}.Shared"/> instead of being allocated per call.
    /// <para>异步精确复制指定字节数，支持取消，输入提前结束时抛出异常。传输缓冲改由
    /// <see cref="ArrayPool{Byte}.Shared"/> 租借而非每次分配。</para>
    /// </summary>
    /// <param name="source">Readable stream positioned at the first byte of the region.
    /// <para>定位到待复制区域首字节、可读的源流。</para></param>
    /// <param name="destination">Writable stream receiving the copied bytes; bytes already written stay written when the operation fails.
    /// <para>接收复制字节的可写目标流；操作失败时已写入的字节仍保留。</para></param>
    /// <param name="count">Exact number of bytes to copy; zero completes the task without touching either stream.
    /// <para>需要复制的精确字节数；为 0 时不访问任一流即完成。</para></param>
    /// <param name="cancellationToken">Token tested before each read and forwarded to the underlying read and write operations.
    /// <para>在每次读取前检查，并传递给底层读写操作的取消令牌。</para></param>
    /// <param name="bufferSize">Size in bytes of the rented transfer buffer; must be at least 1.
    /// <para>租借的传输缓冲区大小（字节），必须不小于 1。</para></param>
    /// <returns>A task that completes once all <c>count</c> bytes have been written.
    /// <para>在 <c>count</c> 字节全部写入后完成的任务。</para></returns>
    /// <exception cref="ArgumentNullException"><c>source</c> or <c>destination</c> is <see langword="null"/>.
    /// <para><c>source</c> 或 <c>destination</c> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><c>count</c> is negative or <c>bufferSize</c> is below 1.
    /// <para><c>count</c> 为负数，或 <c>bufferSize</c> 小于 1。</para></exception>
    /// <exception cref="ArgumentException">The source is not readable or the destination is not writable.
    /// <para>源流不可读或目标流不可写。</para></exception>
    /// <exception cref="OperationCanceledException"><c>cancellationToken</c> was signaled.
    /// <para><c>cancellationToken</c> 已被触发。</para></exception>
    /// <exception cref="EndOfStreamException">The source reached its end before <c>count</c> bytes were copied.
    /// <para>源流在复制满 <c>count</c> 字节之前已经结束。</para></exception>
    public static async Task CopyExactlyAsync(Stream source, Stream destination, long count, CancellationToken cancellationToken = default, int bufferSize = 81920)
    {
        Validate(source, destination, count, bufferSize);
        var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        try
        {
            while (count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(count, buffer.Length)), cancellationToken).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException("The source stream ended before the requested byte count was copied.");
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                count -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void Validate(Stream source, Stream destination, long count, int bufferSize)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfLessThan(bufferSize, 1);
        if (!source.CanRead) throw new ArgumentException("The source stream must be readable.", nameof(source));
        if (!destination.CanWrite) throw new ArgumentException("The destination stream must be writable.", nameof(destination));
    }
}