namespace UotanStudio.Firmware.BoundedContexts.FileSystem.Sparse;

public interface ISparseDataProvider : IDisposable
{
    long Length { get; }

    /// <summary>
    /// Writes the entire data to the specified stream.
    /// <para>将全部数据写入指定流。</para>
    /// </summary>
    void WriteTo(Stream stream);

    /// <summary>
    /// Asynchronously writes the entire data to the specified stream.
    /// <para>异步将全部数据写入指定流。</para>
    /// </summary>
    Task WriteToAsync(Stream stream, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads data from the specified offset into a byte array buffer.
    /// <para>从指定偏移读取数据到字节数组缓冲区。</para>
    /// </summary>
    int Read(long offset, byte[] buffer, int bufferOffset, int count);

    /// <summary>
    /// Reads data from the specified offset into a buffer span.
    /// <para>从指定偏移读取数据到缓冲区 Span。</para>
    /// </summary>
    int Read(long offset, Span<byte> buffer);

    /// <summary>
    /// Gets a sub-data provider for the specified range.
    /// <para>获取指定范围的子数据提供程序。</para>
    /// </summary>
    ISparseDataProvider GetSubProvider(long offset, long length);
}
