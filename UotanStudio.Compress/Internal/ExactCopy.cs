using System.Buffers;

namespace UotanStudio.Compress.Internal;

/// <summary>
/// Copies byte counts that a container header already declared, so a short input is an error rather than a
/// silent truncation.
/// <para>按容器头部已声明的字节数进行复制，因此输入提前结束会被视为错误而不是静默截断。</para>
/// </summary>
/// <remarks>
/// The XZ writer needs exactly this behaviour while spooling its LZMA2 payload into the container. The firmware
/// shared kernel offers an equivalent helper, but this project sits below that kernel and must stay independent
/// of it, so the minimal bounded copy used here lives locally.
/// <para>XZ 写出器在把 LZMA2 载荷写入容器时需要这一行为。固件共享内核提供了等价辅助方法，但本项目位于该内核之下、
/// 必须保持独立，因此这里保留所需的最小有界复制实现。</para>
/// </remarks>
internal static class ExactCopy
{
    /// <summary>
    /// Copies exactly the specified byte count from <paramref name="source"/> to <paramref name="destination"/>.
    /// <para>从 <paramref name="source"/> 精确复制指定字节数到 <paramref name="destination"/>。</para>
    /// </summary>
    /// <param name="source">
    /// Readable stream positioned at the first byte of the region; it is not closed.
    /// <para>定位到待复制区域首字节、可读的源流；不会被关闭。</para>
    /// </param>
    /// <param name="destination">
    /// Writable stream receiving the copied bytes; it is not flushed and not closed.
    /// <para>接收复制字节的可写目标流；不会被刷新，也不会被关闭。</para>
    /// </param>
    /// <param name="count">
    /// Exact number of bytes to copy; zero writes nothing and completes immediately.
    /// <para>需要复制的精确字节数；为 0 时不写入任何内容并立即完成。</para>
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> or <paramref name="destination"/> is <see langword="null"/>.
    /// <para><paramref name="source"/> 或 <paramref name="destination"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative.
    /// <para><paramref name="count"/> 为负数。</para></exception>
    /// <exception cref="EndOfStreamException"><paramref name="source"/> ended before <paramref name="count"/> bytes were copied.
    /// <para><paramref name="source"/> 在复制满 <paramref name="count"/> 字节之前已经结束。</para></exception>
    public static void Exactly(Stream source, Stream destination, long count)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            while (count > 0)
            {
                var read = source.Read(buffer, 0, (int)Math.Min(count, buffer.Length));
                if (read == 0)
                {
                    throw new EndOfStreamException("The source stream ended before the requested byte count was copied.");
                }

                destination.Write(buffer, 0, read);
                count -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}