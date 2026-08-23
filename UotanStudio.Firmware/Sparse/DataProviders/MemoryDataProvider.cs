namespace UotanStudio.Firmware.Sparse;

/// <summary>
/// Provides an in-memory view over a byte array as an <see cref="ISparseDataProvider"/>.
/// Useful for small chunks of data already loaded into memory.
/// <para>提供对字节数组的内存视图，实现 ISparseDataProvider 接口。
/// 适用于已加载到内存中的小型数据块。</para>
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="MemoryDataProvider"/> class.
/// <para>初始化 MemoryDataProvider 的新实例。</para>
/// </remarks>
/// <param name="data">The source byte array. <para>源字节数组。</para></param>
/// <param name="offset">The starting offset in the array. <para>数组中的起始偏移。</para></param>
/// <param name="length">The data length; if -1, all data from offset to the end is used. <para>数据长度；如果为 -1，则使用从偏移到末尾的所有数据。</para></param>
public class MemoryDataProvider(byte[] data, int offset = 0, int length = -1) : ISparseDataProvider
{
    private readonly byte[] _data = data;
    private readonly int _offset = offset;
    private readonly int _length = length < 0 ? data.Length - offset : length;

    /// <summary>
    /// Gets the number of bytes available from this provider.
    /// <para>获取此提供程序可用的字节数。</para>
    /// </summary>
    public long Length => _length;

    /// <summary>
    /// Synchronously writes the provider's data to the destination stream.
    /// <para>同步将提供程序的数据写入目标流。</para>
    /// </summary>
    /// <param name="stream">Target stream to receive the bytes. <para>接收字节的目标流。</para></param>
    public void WriteTo(Stream stream) => stream.Write(_data, _offset, _length);

    /// <summary>
    /// Asynchronously writes the provider's data to the destination stream.
    /// <para>异步将提供程序的数据写入目标流。</para>
    /// </summary>
    /// <param name="stream">Target stream to receive the bytes. <para>接收字节的目标流。</para></param>
    /// <param name="cancellationToken">Cancellation token for the operation. <para>操作的取消令牌。</para></param>
    /// <returns>A task that completes when writing finishes. <para>写入完成时结束的任务。</para></returns>
    public Task WriteToAsync(Stream stream, CancellationToken cancellationToken = default) => stream.WriteAsync(_data, _offset, _length, cancellationToken);

    /// <summary>
    /// Reads bytes from this provider into a byte array buffer.
    /// <para>从此提供程序读取字节到字节数组缓冲区。</para>
    /// </summary>
    /// <param name="offset">Byte offset relative to this provider's start. <para>相对于此提供程序起始的字节偏移。</para></param>
    /// <param name="buffer">Destination buffer to receive bytes. <para>接收字节的目标缓冲区。</para></param>
    /// <param name="bufferOffset">Offset in the destination buffer to begin writing. <para>目标缓冲区中的起始写入偏移。</para></param>
    /// <param name="count">Maximum number of bytes to read. <para>最大读取字节数。</para></param>
    /// <returns>The number of bytes actually read. <para>实际读取的字节数。</para></returns>
    public int Read(long offset, byte[] buffer, int bufferOffset, int count) => Read(offset, buffer.AsSpan(bufferOffset, count));

    /// <summary>
    /// Reads bytes from this provider into a <see cref="Span{Byte}"/>.
    /// <para>从此提供程序读取字节到 Span{Byte}。</para>
    /// </summary>
    /// <param name="offset">Byte offset relative to this provider's start. <para>相对于此提供程序起始的字节偏移。</para></param>
    /// <param name="buffer">Span that will receive the data. <para>接收数据的 Span。</para></param>
    /// <returns>The number of bytes actually read. <para>实际读取的字节数。</para></returns>
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
    /// <param name="offset">Byte offset relative to this provider's start. <para>相对于此提供程序起始的字节偏移。</para></param>
    /// <param name="length">Length in bytes of the sub-range. <para>子范围的字节长度。</para></param>
    /// <returns>An <see cref="ISparseDataProvider"/> representing the requested sub-range. <para>表示请求子范围的 ISparseDataProvider。</para></returns>
    public ISparseDataProvider GetSubProvider(long offset, long length) => new MemoryDataProvider(_data, _offset + (int)offset, (int)length);

    /// <summary>
    /// Releases any resources held by the provider. No resources are held, so this is a no-op.
    /// <para>释放提供程序持有的所有资源。不持有任何资源，因此为空操作。</para>
    /// </summary>
    public void Dispose() { }
}