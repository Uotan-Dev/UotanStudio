namespace UotanStudio.Firmware.Common.Streams;

/// <summary>
/// 提供精确字节数的流复制工具。
/// <para>Provides stream-copy helpers for an exact byte count.</para>
/// </summary>
public static class StreamCopy
{
    /// <summary>
    /// 精确复制指定字节数，输入提前结束时抛出异常。
    /// <para>Copies exactly the specified byte count and throws if the input ends early.</para>
    /// </summary>
    /// <param name="source">源流。<para>The source stream.</para></param>
    /// <param name="destination">目标流。<para>The destination stream.</para></param>
    /// <param name="count">要复制的字节数。<para>The number of bytes to copy.</para></param>
    /// <param name="bufferSize">复制缓冲区大小。<para>The copy buffer size.</para></param>
    public static void CopyExactly(Stream source, Stream destination, long count, int bufferSize = 81920)
    {
        Validate(source, destination, count, bufferSize);
        var buffer = new byte[bufferSize];
        while (count > 0)
        {
            var read = source.Read(buffer, 0, (int)Math.Min(count, buffer.Length));
            if (read == 0) throw new EndOfStreamException("The source stream ended before the requested byte count was copied.");
            destination.Write(buffer, 0, read);
            count -= read;
        }
    }

    /// <summary>
    /// 异步精确复制指定字节数，支持取消，输入提前结束时抛出异常。
    /// <para>Asynchronously copies exactly the specified byte count with cancellation support and throws if the input ends early.</para>
    /// </summary>
    /// <param name="source">源流。<para>The source stream.</para></param>
    /// <param name="destination">目标流。<para>The destination stream.</para></param>
    /// <param name="count">要复制的字节数。<para>The number of bytes to copy.</para></param>
    /// <param name="cancellationToken">取消令牌。<para>The cancellation token.</para></param>
    /// <param name="bufferSize">复制缓冲区大小。<para>The copy buffer size.</para></param>
    public static async Task CopyExactlyAsync(Stream source, Stream destination, long count, CancellationToken cancellationToken = default, int bufferSize = 81920)
    {
        Validate(source, destination, count, bufferSize);
        var buffer = new byte[bufferSize];
        while (count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(count, buffer.Length)), cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("The source stream ended before the requested byte count was copied.");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            count -= read;
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