namespace UotanStudio.Firmware.BlockImage.Sparse.DataProviders;

/// <summary>
/// Data provider over a slice of an in-memory byte array. The array is borrowed, not copied, and the
/// caller must keep it unchanged and alive while the provider is in use; a negative
/// <c>length</c> argument means "up to the end of the array".
/// <para>基于内存字节数组某个切片的提供程序。数组按引用借用而非复制，调用方必须在提供程序使用期间保持数组不变且存活；
/// <c>length</c> 为负数表示“直到数组末尾”。</para>
/// </summary>
/// <param name="data">
/// Array holding the bytes; it is referenced, never copied, and must stay unchanged while in use.
/// <para>承载字节的数组；只被引用而不会复制，使用期间必须保持不变。</para>
/// </param>
/// <param name="offset">
/// Index in <paramref name="data"/> where the visible slice starts.
/// <para>可见切片在 <paramref name="data"/> 中的起始索引。</para>
/// </param>
/// <param name="length">
/// Byte length of the slice; a negative value selects the rest of the array.
/// <para>切片字节长度；负数表示取到数组末尾。</para>
/// </param>
internal class MemoryDataProvider(byte[] data, int offset = 0, int length = -1) : ISparseDataProvider
{
    private readonly byte[] _data = data;
    private readonly int _offset = offset;
    private readonly int _length = length < 0 ? data.Length - offset : length;

    /// <summary>
    /// Byte length of the visible slice.
    /// <para>可见切片的字节长度。</para>
    /// </summary>
    /// <value>
    /// The requested length, or <c>data.Length - offset</c> when the constructor was given a negative
    /// length. Never re-checked against the array, so an oversized request surfaces later as an
    /// <see cref="ArgumentOutOfRangeException"/> from the copy.
    /// <para>请求的长度；构造时传入负长度则为 <c>data.Length - offset</c>。不会与数组再次校验，
    /// 过大的请求会在复制阶段以 <see cref="ArgumentOutOfRangeException"/> 形式暴露。</para>
    /// </value>
    public long Length => _length;

    /// <summary>
    /// Synchronously writes the provider's data to the destination stream.
    /// <para>同步将提供程序的数据写入目标流。</para>
    /// </summary>
    /// <param name="stream">
    /// Writable destination stream receiving the whole slice from its current position; the provider
    /// keeps no ownership of it.
    /// <para>可写目标流，从其当前位置接收整个切片；流的所有权仍属于调用方。</para>
    /// </param>
    public void WriteTo(Stream stream) => stream.Write(_data, _offset, _length);

    /// <summary>
    /// Asynchronously writes the provider's data to the destination stream.
    /// <para>异步将提供程序的数据写入目标流。</para>
    /// </summary>
    /// <param name="stream">
    /// Writable destination stream receiving the whole slice.
    /// <para>接收整个切片的可写目标流。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels the underlying write; the memory copy itself never blocks.
    /// <para>取消底层写入；内存复制本身不会阻塞。</para>
    /// </param>
    /// <returns>
    /// Task representing the pending write of the slice.
    /// <para>表示切片写入待完成状态的 Task。</para>
    /// </returns>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled before the write completed.
    /// <para><paramref name="cancellationToken"/> 在写入完成前被取消。</para>
    /// </exception>
    public Task WriteToAsync(Stream stream, CancellationToken cancellationToken = default) => stream.WriteAsync(_data, _offset, _length, cancellationToken);

    /// <summary>
    /// Reads bytes from this provider into a byte array buffer.
    /// <para>从此提供程序读取字节到字节数组缓冲区。</para>
    /// </summary>
    /// <param name="offset">
    /// Offset inside the slice; <c>0</c> is the slice's first byte.
    /// <para>切片内的偏移；<c>0</c> 表示切片第一个字节。</para>
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
    /// Bytes copied, truncated at the slice end.
    /// <para>实际复制的字节数，到达切片末尾时会被截断。</para>
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="bufferOffset"/> or <paramref name="count"/> falls outside <paramref name="buffer"/>.
    /// <para><paramref name="bufferOffset"/> 或 <paramref name="count"/> 超出 <paramref name="buffer"/> 范围。</para>
    /// </exception>
    public int Read(long offset, byte[] buffer, int bufferOffset, int count) => Read(offset, buffer.AsSpan(bufferOffset, count));

    /// <summary>
    /// Reads bytes from this provider into a Span{Byte}.
    /// <para>从此提供程序读取字节到 Span{Byte}。</para>
    /// </summary>
    /// <param name="offset">
    /// Offset inside the slice; a value at or beyond <see cref="Length"/> yields no bytes.
    /// <para>切片内的偏移；等于或超过 <see cref="Length"/> 时不返回任何字节。</para>
    /// </param>
    /// <param name="buffer">
    /// Destination span whose length is the maximum wanted.
    /// <para>长度即所需最大字节数的目标 Span。</para>
    /// </param>
    /// <returns>
    /// Bytes copied, or <c>0</c> when the slice is already exhausted; never exceeds the span length.
    /// <para>实际复制的字节数，切片已读完时为 <c>0</c>，且不会超过 Span 长度。</para>
    /// </returns>
    public int Read(long offset, Span<byte> buffer)
    {
        var available = (int)Math.Max(0, _length - offset);
        var toCopy = Math.Min(buffer.Length, available);
        if (toCopy <= 0)
        {
            return 0;
        }

        _data.AsSpan(_offset + (int)offset, toCopy).CopyTo(buffer);
        return toCopy;
    }

    /// <summary>
    /// Creates a sub-provider representing a slice of the current in-memory data.
    /// <para>创建表示当前内存数据切片的子提供程序。</para>
    /// </summary>
    /// <param name="offset">
    /// Offset inside this slice where the child starts, narrowed to <see cref="int"/>.
    /// <para>子提供程序在本切片内的起始偏移，会收窄为 <see cref="int"/>。</para>
    /// </param>
    /// <param name="length">
    /// Byte length of the child slice, narrowed to <see cref="int"/>.
    /// <para>子切片的字节长度，会收窄为 <see cref="int"/>。</para>
    /// </param>
    /// <returns>
    /// Provider sharing the same array with a shifted window; a negative <paramref name="length"/>
    /// means "up to the end of the array", while <c>0</c> produces an empty child.
    /// <para>共享同一数组并移动窗口的新提供程序；<paramref name="length"/> 为负数表示“直到数组末尾”，
    /// 为 <c>0</c> 时得到空子切片。</para>
    /// </returns>
    public ISparseDataProvider GetSubProvider(long offset, long length) => new MemoryDataProvider(_data, _offset + (int)offset, (int)length);

    /// <summary>
    /// Releases any resources held by the provider.
    /// <para>释放提供程序持有的所有资源。</para>
    /// </summary>
    public void Dispose() { }
}