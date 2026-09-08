namespace UotanStudio.Firmware.SharedKernel.Streams;

using System.Buffers;

public static class StreamCopy
{
    /// <summary>
    /// Copies exactly the specified byte count and throws if the input ends early.
    /// <para>精确复制指定字节数，输入提前结束时抛出异常。</para>
    /// </summary>
    public static void CopyExactly(Stream source, Stream destination, long count, int bufferSize = 81920)
        => CopyExactly(source, destination, count, CancellationToken.None, bufferSize);

    /// <summary>
    /// Copies exactly the specified byte count with cancellation between buffer transfers.
    /// The transfer buffer is rented from <see cref="ArrayPool{Byte}.Shared"/> instead of being
    /// allocated per call; this method is called at high frequency by extractors.
    /// <para>精确复制指定字节数，并在缓冲传输之间检查取消。传输缓冲改由
    /// <see cref="ArrayPool{Byte}.Shared"/> 租借而非每次分配；提取器会高频调用本方法。</para>
    /// </summary>
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