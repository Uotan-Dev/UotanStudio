using System.Buffers;
using Microsoft.Win32.SafeHandles;

namespace UotanStudio.Firmware.BootSecurity.Avb.Verification;

/// <summary>
/// Reads declared byte ranges of a caller-owned seekable stream through one bounded pooled window per cursor.
/// <para>通过每个游标各自持有的有界池化窗口读取调用方持有的可定位流中的声明字节区间。</para>
/// </summary>
/// <remarks>
/// Every reader owns its window offset, so a data window and a tree-block window can alternate over the same
/// stream without invalidating each other's prefetch, and plain files are read with positional I/O instead of
/// treating the shared stream position as a cursor. Each fill requests at most <see cref="MaximumWindowBytes"/>,
/// so memory stays bounded no matter how large the declared region is. The memory returned by
/// <see cref="ReadBlockAsync"/> aliases that window and is only valid until the next call on the same reader.
/// <para>每个读取器持有自己的窗口偏移，因此数据窗口与树块窗口可以在同一条流上交替使用而不会互相使对方的预取失效；
/// 普通文件使用定位 I/O，不把共享的流位置当作游标。每次填充最多请求 <see cref="MaximumWindowBytes"/>，
/// 无论声明的区域多大内存都有界。<see cref="ReadBlockAsync"/> 返回的内存直接指向该窗口，
/// 只在下一次调用同一读取器之前有效。</para>
/// </remarks>
internal sealed class AvbWindowedReader : IDisposable
{
    /// <summary>Largest single fill; keeps one reader bounded by one mebibyte.
    /// <para>单次填充上限，使单个读取器的占用不超过 1 MiB。</para></summary>
    internal const int MaximumWindowBytes = 1024 * 1024;

    private readonly Stream _stream;
    private readonly SafeFileHandle? _file;
    private readonly long _limit;
    private readonly int _windowSize;
    private readonly byte[] _window;
    private long _windowStart;
    private int _windowLength;
    private bool _disposed;

    /// <summary>Creates a reader over the stream range starting at zero and ending before limit.
    /// <para>创建覆盖从零开始、在 limit 之前结束的流区间的读取器。</para>
    /// </summary>
    /// <param name="stream">Readable, seekable stream shared with other readers; never disposed here.
    /// <para>与其他读取器共享的可读可定位流；此处不释放它。</para></param>
    /// <param name="limit">Exclusive end offset the caller may read; clamped to the stream length.
    /// <para>调用方允许读取的排他结束偏移；会被限制在流长度内。</para></param>
    /// <param name="windowSize">Bytes fetched per fill, from 1 through <see cref="MaximumWindowBytes"/>.
    /// <para>每次填充读取的字节数，范围为 1 至 <see cref="MaximumWindowBytes"/>。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null. <para><paramref name="stream"/> 为 null。</para></exception>
    /// <exception cref="ArgumentException">The stream cannot read or seek. <para>流不可读或不可定位。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">The window size is outside its range. <para>窗口大小超出范围。</para></exception>
    internal AvbWindowedReader(Stream stream, long limit, int windowSize)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new ArgumentException("The stream must be readable and seekable.", nameof(stream));
        }
        ArgumentOutOfRangeException.ThrowIfLessThan(windowSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(windowSize, MaximumWindowBytes);
        _stream = stream;
        _limit = Math.Min(limit, stream.Length);
        _windowSize = windowSize;
        _window = ArrayPool<byte>.Shared.Rent(windowSize);
        if (stream.GetType() == typeof(FileStream))
        {
            _file = ((FileStream)stream).SafeFileHandle;
        }
    }

    /// <summary>Returns exactly the requested bytes, reading them from the stream when the window does not hold them.
    /// <para>返回恰好请求的字节；窗口未持有时从流中读取。</para>
    /// </summary>
    /// <param name="offset">Absolute offset of the first byte. <para>首字节的绝对偏移。</para></param>
    /// <param name="length">Byte count, from 1 through the configured window size. <para>字节数，范围为 1 至所配置的窗口大小。</para></param>
    /// <param name="cancellationToken">Cancels the stream read. <para>取消流读取。</para></param>
    /// <returns>A view of the window that the next call on this reader may overwrite.
    /// <para>该窗口的视图；本读取器的下一次调用可能覆盖它。</para></returns>
    /// <exception cref="EndOfStreamException">The range is outside the declared limit or the stream ended early.
    /// <para>区间超出声明上限或流提前结束。</para></exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested. <para>请求了取消。</para></exception>
    internal async ValueTask<ReadOnlyMemory<byte>> ReadBlockAsync(long offset, int length, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, _windowSize);
        await EnsureWindowAsync(offset, length, cancellationToken).ConfigureAwait(false);
        return _window.AsMemory(checked((int)(offset - _windowStart)), length);
    }

    /// <summary>Copies exactly the requested bytes across as many window fills as needed.
    /// <para>按需跨多次窗口填充复制恰好请求的字节。</para>
    /// </summary>
    /// <param name="offset">Absolute offset of the first byte. <para>首字节的绝对偏移。</para></param>
    /// <param name="destination">Buffer receiving the bytes; an empty buffer reads nothing.
    /// <para>接收字节的缓冲区；空缓冲区不读取任何内容。</para></param>
    /// <param name="cancellationToken">Cancels the stream reads. <para>取消流读取。</para></param>
    /// <returns>A task that completes once the buffer is filled. <para>缓冲区填满时完成的任务。</para></returns>
    /// <exception cref="EndOfStreamException">The range is outside the declared limit or the stream ended early.
    /// <para>区间超出声明上限或流提前结束。</para></exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested. <para>请求了取消。</para></exception>
    internal async ValueTask ReadExactlyAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken)
    {
        var position = offset;
        var remaining = destination;
        while (remaining.Length > 0)
        {
            await EnsureWindowAsync(position, 1, cancellationToken).ConfigureAwait(false);
            var start = (int)(position - _windowStart);
            var count = Math.Min(_windowLength - start, remaining.Length);
            _window.AsSpan(start, count).CopyTo(remaining.Span);
            remaining = remaining[count..];
            position += count;
        }
    }

    /// <summary>Returns the pooled window to the shared pool; calling this twice has no further effect.
    /// <para>把池化窗口归还共享池；重复调用不会产生额外影响。</para>
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        ArrayPool<byte>.Shared.Return(_window);
    }

    private async ValueTask EnsureWindowAsync(long offset, int length, CancellationToken cancellationToken)
    {
        if (offset >= _windowStart && offset - _windowStart <= _windowLength - length)
        {
            return;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (_limit - offset < length)
        {
            throw new EndOfStreamException("The partition stream ended before the declared bytes could be read.");
        }
        await FillAsync(offset, cancellationToken).ConfigureAwait(false);
        if (_windowLength < length)
        {
            throw new EndOfStreamException("The partition stream ended before the declared bytes could be read.");
        }
    }

    private async ValueTask FillAsync(long offset, CancellationToken cancellationToken)
    {
        var count = (int)Math.Min(_windowSize, _limit - offset);
        var destination = _window.AsMemory(0, count);
        if (_file is null)
        {
            _stream.Position = offset;
            await _stream.ReadExactlyAsync(destination, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var read = 0;
            while (read < count)
            {
                var received = await RandomAccess.ReadAsync(_file, destination[read..], offset + read, cancellationToken).ConfigureAwait(false);
                if (received == 0)
                {
                    throw new EndOfStreamException("The partition stream ended before the declared bytes could be read.");
                }
                read += received;
            }
        }
        _windowStart = offset;
        _windowLength = count;
    }
}