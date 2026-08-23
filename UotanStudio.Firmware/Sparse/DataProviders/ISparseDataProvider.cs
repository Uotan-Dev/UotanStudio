namespace UotanStudio.Firmware.Sparse;

/// <summary>
/// Represents a sparse data provider that supplies payload data for sparse chunks.
/// <para>表示稀疏数据提供程序，为稀疏数据块提供载荷数据。</para>
/// </summary>
public interface ISparseDataProvider : IDisposable
{
    /// <summary>
    /// Gets the total length of the data in bytes.
    /// <para>获取数据的总字节长度。</para>
    /// </summary>
    long Length { get; }

    /// <summary>
    /// Writes the entire data to the specified stream.
    /// <para>将全部数据写入指定流。</para>
    /// </summary>
    /// <param name="stream">The target stream to write data to. <para>要写入数据的目标流。</para></param>
    void WriteTo(Stream stream);

    /// <summary>
    /// Asynchronously writes the entire data to the specified stream.
    /// <para>异步将全部数据写入指定流。</para>
    /// </summary>
    /// <param name="stream">The target stream to write data to. <para>要写入数据的目标流。</para></param>
    /// <param name="cancellationToken">The cancellation token. <para>取消令牌。</para></param>
    /// <returns>A task representing the asynchronous write operation. <para>表示异步写入操作的任务。</para></returns>
    Task WriteToAsync(Stream stream, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads data from the specified offset into a byte array buffer.
    /// <para>从指定偏移读取数据到字节数组缓冲区。</para>
    /// </summary>
    /// <param name="offset">The starting offset to read from. <para>起始读取偏移。</para></param>
    /// <param name="buffer">The target buffer to receive bytes. <para>接收字节的目标缓冲区。</para></param>
    /// <param name="bufferOffset">The starting index in the buffer. <para>缓冲区中的起始索引。</para></param>
    /// <param name="count">The number of bytes to read. <para>要读取的字节数。</para></param>
    /// <returns>The number of bytes actually read. <para>实际读取的字节数。</para></returns>
    int Read(long offset, byte[] buffer, int bufferOffset, int count);

    /// <summary>
    /// Reads data from the specified offset into a buffer span.
    /// <para>从指定偏移读取数据到缓冲区 Span。</para>
    /// </summary>
    /// <param name="offset">The starting offset to read from. <para>起始读取偏移。</para></param>
    /// <param name="buffer">The target buffer span. <para>接收数据的目标 Span。</para></param>
    /// <returns>The number of bytes actually read. <para>实际读取的字节数。</para></returns>
    int Read(long offset, Span<byte> buffer);

    /// <summary>
    /// Gets a sub-data provider for the specified range.
    /// <para>获取指定范围的子数据提供程序。</para>
    /// </summary>
    /// <param name="offset">The starting offset of the sub-data. <para>子数据的起始偏移。</para></param>
    /// <param name="length">The length of the sub-data. <para>子数据的长度。</para></param>
    /// <returns>A new <see cref="ISparseDataProvider"/> instance for the sub-range. <para>表示子范围的新 ISparseDataProvider 实例。</para></returns>
    ISparseDataProvider GetSubProvider(long offset, long length);
}