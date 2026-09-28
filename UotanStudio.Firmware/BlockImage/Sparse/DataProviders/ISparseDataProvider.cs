namespace UotanStudio.Firmware.BlockImage.Sparse.DataProviders;

/// <summary>
/// Random-access byte source over one region of a sparse image, shared by chunk readers and raw writers.
/// <para>稀疏镜像某一区域上的随机字节访问源，供数据块读取器与原始镜像写入器共同使用。</para>
/// </summary>
public interface ISparseDataProvider : IDisposable
{
    /// <summary>
    /// Byte length of the region this provider exposes.
    /// <para>此提供程序暴露区域的字节长度。</para>
    /// </summary>
    /// <value>
    /// Non-negative length fixed when the provider is created; reads past it return <c>0</c>.
    /// <para>创建提供程序时确定的非负长度；越过该长度的读取返回 <c>0</c>。</para>
    /// </value>
    long Length { get; }

    /// <summary>
    /// Writes the entire data to the specified stream.
    /// <para>将全部数据写入指定流。</para>
    /// </summary>
    /// <param name="stream">
    /// Writable destination stream; its current position advances by the bytes written and the
    /// provider keeps ownership of the stream.
    /// <para>可写目标流；其当前位置按写入字节数前进，流的所有权仍属于调用方。</para>
    /// </param>
    /// <exception cref="NotSupportedException">
    /// The implementation is read-only.
    /// <para>实现为只读。</para>
    /// </exception>
    void WriteTo(Stream stream);

    /// <summary>
    /// Asynchronously writes the entire data to the specified stream.
    /// <para>异步将全部数据写入指定流。</para>
    /// </summary>
    /// <param name="stream">
    /// Writable destination stream; the provider keeps ownership of it.
    /// <para>可写目标流；流的所有权仍属于调用方。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels pending I/O; an already-cancelled token may stop the copy before any byte is written.
    /// <para>取消挂起的 I/O；已取消的令牌可能在写出任何字节之前就停止复制。</para>
    /// </param>
    /// <returns>
    /// Task that completes once every byte of the region has been written.
    /// <para>在区域全部字节写出后完成的 Task。</para>
    /// </returns>
    /// <exception cref="NotSupportedException">
    /// The implementation is read-only.
    /// <para>实现为只读。</para>
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled during the copy.
    /// <para><paramref name="cancellationToken"/> 在复制过程中被取消。</para>
    /// </exception>
    Task WriteToAsync(Stream stream, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads data from the specified offset into a byte array buffer.
    /// <para>从指定偏移读取数据到字节数组缓冲区。</para>
    /// </summary>
    /// <param name="offset">
    /// Zero-based offset inside this provider's region.
    /// <para>本提供程序区域内从零开始的偏移。</para>
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
    /// Bytes actually copied, which is <c>0</c> at or past <see cref="Length"/>.
    /// <para>实际复制的字节数；位于或越过 <see cref="Length"/> 时为 <c>0</c>。</para>
    /// </returns>
    int Read(long offset, byte[] buffer, int bufferOffset, int count);

    /// <summary>
    /// Reads data from the specified offset into a buffer span.
    /// <para>从指定偏移读取数据到缓冲区 Span。</para>
    /// </summary>
    /// <param name="offset">
    /// Zero-based offset inside this provider's region.
    /// <para>本提供程序区域内从零开始的偏移。</para>
    /// </param>
    /// <param name="buffer">
    /// Destination span sized to the maximum bytes wanted.
    /// <para>长度即所需最大字节数的目标 Span。</para>
    /// </param>
    /// <returns>
    /// Bytes actually copied, which is <c>0</c> at or past <see cref="Length"/>.
    /// <para>实际复制的字节数；位于或越过 <see cref="Length"/> 时为 <c>0</c>。</para>
    /// </returns>
    int Read(long offset, Span<byte> buffer);

    /// <summary>
    /// Gets a sub-data provider for the specified range.
    /// <para>获取指定范围的子数据提供程序。</para>
    /// </summary>
    /// <param name="offset">
    /// Offset of the sub-range relative to this provider's region.
    /// <para>子范围相对本提供程序区域的偏移。</para>
    /// </param>
    /// <param name="length">
    /// Byte length of the sub-range.
    /// <para>子范围的字节长度。</para>
    /// </param>
    /// <returns>
    /// Independent provider whose offsets start at <paramref name="offset"/>; disposing it never
    /// disposes this provider.
    /// <para>独立的提供程序，其偏移从 <paramref name="offset"/> 开始；释放它不会影响本提供程序。</para>
    /// </returns>
    ISparseDataProvider GetSubProvider(long offset, long length);
}