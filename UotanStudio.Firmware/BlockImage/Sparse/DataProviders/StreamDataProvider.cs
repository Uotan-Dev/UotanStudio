using System.Buffers;

namespace UotanStudio.Firmware.BlockImage.Sparse.DataProviders;

/// <summary>
/// Data provider over a byte range of an existing <see cref="Stream"/>. Offsets are absolute within
/// the stream when it supports seeking; on a non-seekable stream reads continue from the current
/// position, so such a source can only be consumed once and in order.
/// <para>基于已有 <see cref="Stream"/> 某个字节范围的提供程序。流支持寻址时偏移是流内的绝对位置；
/// 不可寻址的流则从当前位置继续读取，因此这种源只能顺序消费一次。</para>
/// </summary>
/// <param name="stream">
/// Source stream carrying the raw bytes; its lifetime is governed by <paramref name="leaveOpen"/>.
/// <para>承载原始字节的源流；其生命周期由 <paramref name="leaveOpen"/> 决定。</para>
/// </param>
/// <param name="offset">
/// Absolute stream position where the window starts, used only when the stream can seek.
/// <para>窗口开始的流内绝对位置，仅在流可寻址时使用。</para>
/// </param>
/// <param name="length">
/// Byte length of the window.
/// <para>窗口的字节长度。</para>
/// </param>
/// <param name="leaveOpen">
/// When <see langword="true"/>, disposing the provider leaves the source stream open for the caller.
/// <para>为 <see langword="true"/> 时，释放提供程序不会关闭源流，仍由调用方负责。</para>
/// </param>
internal class StreamDataProvider(Stream stream, long offset, long length, bool leaveOpen = true) : ISparseDataProvider
{
    private readonly Stream _stream = stream;
    private readonly long _offset = offset;
    private readonly bool _leaveOpen = leaveOpen;

    /// <summary>
    /// Byte length of the stream window this provider exposes.
    /// <para>此提供程序暴露的流窗口字节长度。</para>
    /// </summary>
    /// <value>
    /// The length given at construction; it is not validated against the stream's remaining bytes, so
    /// a window running past the end simply produces fewer bytes.
    /// <para>构造时传入的长度；不会与流剩余字节校验，窗口越过末尾时只会得到更少的字节。</para>
    /// </value>
    public long Length { get; } = length;

    /// <summary>
    /// Synchronously writes the provider's data to the specified output stream.
    /// <para>同步将提供程序的数据写入指定输出流。</para>
    /// </summary>
    /// <param name="outStream">
    /// Writable destination stream receiving the window in blocks of up to 1 MiB; it is not flushed
    /// or closed.
    /// <para>可写目标流，以不超过 1 MiB 的块接收窗口内容；不会刷新也不会关闭。</para>
    /// </param>
    /// <remarks>
    /// The source is seeked to the window start when it can seek, and the copy stops early when the
    /// source returns <c>0</c>.
    /// <para>源支持寻址时会定位到窗口起点；源返回 <c>0</c> 时复制提前结束。</para>
    /// </remarks>
    public void WriteTo(Stream outStream)
    {
        if (_stream.CanSeek)
        {
            _stream.Seek(_offset, SeekOrigin.Begin);
        }
        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            var remaining = Length;
            while (remaining > 0)
            {
                var toRead = (int)Math.Min(buffer.Length, remaining);
                var read = _stream.Read(buffer, 0, toRead);
                if (read == 0)
                {
                    break;
                }

                outStream.Write(buffer, 0, read);
                remaining -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Asynchronously writes the provider's data to the specified output stream.
    /// <para>异步将提供程序的数据写入指定输出流。</para>
    /// </summary>
    /// <param name="outStream">
    /// Writable destination stream receiving up to <see cref="Length"/> bytes.
    /// <para>可写目标流，接收最多 <see cref="Length"/> 字节。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Passed to every source read and destination write; partial output stays where it was written.
    /// <para>传递给每次源读取与目标写入；已写出的部分不会回滚。</para>
    /// </param>
    /// <returns>
    /// Task that completes once the window has been copied or the source reported its end.
    /// <para>在窗口复制完成或源报告结束时结束的 Task。</para>
    /// </returns>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled during the copy.
    /// <para><paramref name="cancellationToken"/> 在复制过程中被取消。</para>
    /// </exception>
    public async Task WriteToAsync(Stream outStream, CancellationToken cancellationToken = default)
    {
        if (_stream.CanSeek)
        {
            _stream.Seek(_offset, SeekOrigin.Begin);
        }
        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            var remaining = Length;
            while (remaining > 0)
            {
                var toRead = (int)Math.Min(buffer.Length, remaining);
                var read = await _stream.ReadAsync(buffer, 0, toRead, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                await outStream.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
                remaining -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Reads bytes from this provider into a byte array buffer.
    /// <para>从此提供程序读取字节到字节数组缓冲区。</para>
    /// </summary>
    /// <param name="inOffset">
    /// Offset inside the window; honoured only when the source stream supports seeking.
    /// <para>窗口内的偏移；仅在源流支持寻址时生效。</para>
    /// </param>
    /// <param name="buffer">
    /// Destination array receiving the bytes.
    /// <para>接收字节的目标数组。</para>
    /// </param>
    /// <param name="bufferOffset">
    /// Index in <paramref name="buffer"/> where writing starts.
    /// <para>开始写入的 <paramref name="buffer"/> 索引。</para>
    /// </param>
    /// <param name="count">
    /// Maximum number of bytes requested.
    /// <para>请求的最大字节数。</para>
    /// </param>
    /// <returns>
    /// Bytes read, truncated at the window end.
    /// <para>实际读取的字节数，到达窗口末尾时会被截断。</para>
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="bufferOffset"/> or <paramref name="count"/> falls outside <paramref name="buffer"/>.
    /// <para><paramref name="bufferOffset"/> 或 <paramref name="count"/> 超出 <paramref name="buffer"/> 范围。</para>
    /// </exception>
    public int Read(long inOffset, byte[] buffer, int bufferOffset, int count) => Read(inOffset, buffer.AsSpan(bufferOffset, count));

    /// <summary>
    /// Reads bytes from this provider into a Span{Byte}.
    /// <para>从此提供程序读取字节到 Span{Byte}。</para>
    /// </summary>
    /// <param name="inOffset">
    /// Offset inside the window; a value at or beyond <see cref="Length"/> reads nothing. On a
    /// non-seekable source this argument is ignored and reading continues from the current position.
    /// <para>窗口内的偏移；等于或超过 <see cref="Length"/> 时不读取。若源不可寻址，该参数会被忽略，
    /// 从流当前位置继续读取。</para>
    /// </param>
    /// <param name="buffer">
    /// Destination span whose length is the maximum wanted.
    /// <para>长度即所需最大字节数的目标 Span。</para>
    /// </param>
    /// <returns>
    /// Bytes returned by the source stream, or <c>0</c> at or past the window end and at the end of
    /// the underlying stream.
    /// <para>源流返回的字节数；位于或越过窗口末尾以及底层流末尾时为 <c>0</c>。</para>
    /// </returns>
    public int Read(long inOffset, Span<byte> buffer)
    {
        if (inOffset >= Length)
        {
            return 0;
        }

        var toRead = (int)Math.Min(buffer.Length, Length - inOffset);
        if (_stream.CanSeek)
        {
            _stream.Seek(_offset + inOffset, SeekOrigin.Begin);
        }
        return _stream.Read(buffer[..toRead]);
    }

    /// <summary>
    /// Creates a sub-provider that represents a sub-range of this provider.
    /// <para>创建表示此提供程序子范围的子提供程序。</para>
    /// </summary>
    /// <param name="subOffset">
    /// Offset inside this window where the child window starts.
    /// <para>子窗口在本窗口内的起始偏移。</para>
    /// </param>
    /// <param name="subLength">
    /// Byte length of the child window.
    /// <para>子窗口的字节长度。</para>
    /// </param>
    /// <returns>
    /// Provider sharing the same underlying stream configured to leave that stream open, so disposing
    /// the child never closes the source.
    /// <para>共享同一底层流的提供程序，并配置为保持该流打开，因此释放子提供程序不会关闭源流。</para>
    /// </returns>
    public ISparseDataProvider GetSubProvider(long subOffset, long subLength) => new StreamDataProvider(_stream, _offset + subOffset, subLength, true);

    /// <summary>
    /// Disposes the provider and optionally closes the underlying stream.
    /// <para>释放提供程序，并根据配置选择是否关闭底层流。</para>
    /// </summary>
    /// <remarks>
    /// The underlying stream is closed only when the provider was created with <c>leaveOpen</c> set to
    /// <see langword="false"/>; otherwise the caller keeps responsibility for it.
    /// <para>仅当创建时 <c>leaveOpen</c> 为 <see langword="false"/> 才会关闭底层流；否则仍由调用方负责。</para>
    /// </remarks>
    public void Dispose()
    {
        if (!_leaveOpen)
        {
            _stream.Dispose();
        }
    }
}