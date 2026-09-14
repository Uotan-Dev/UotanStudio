namespace UotanStudio.Firmware.BoundedContexts.FileSystem.Sparse;

public class MemoryDataProvider(byte[] data, int offset = 0, int length = -1) : ISparseDataProvider
{
    private readonly byte[] _data = data;
    private readonly int _offset = offset;
    private readonly int _length = length < 0 ? data.Length - offset : length;

    public long Length => _length;

    /// <summary>
    /// Synchronously writes the provider's data to the destination stream.
    /// <para>同步将提供程序的数据写入目标流。</para>
    /// </summary>
    public void WriteTo(Stream stream) => stream.Write(_data, _offset, _length);

    /// <summary>
    /// Asynchronously writes the provider's data to the destination stream.
    /// <para>异步将提供程序的数据写入目标流。</para>
    /// </summary>
    public Task WriteToAsync(Stream stream, CancellationToken cancellationToken = default) => stream.WriteAsync(_data, _offset, _length, cancellationToken);

    /// <summary>
    /// Reads bytes from this provider into a byte array buffer.
    /// <para>从此提供程序读取字节到字节数组缓冲区。</para>
    /// </summary>
    public int Read(long offset, byte[] buffer, int bufferOffset, int count) => Read(offset, buffer.AsSpan(bufferOffset, count));

    /// <summary>
    /// Reads bytes from this provider into a Span{Byte}.
    /// <para>从此提供程序读取字节到 Span{Byte}。</para>
    /// </summary>
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
    public ISparseDataProvider GetSubProvider(long offset, long length) => new MemoryDataProvider(_data, _offset + (int)offset, (int)length);

    /// <summary>
    /// Releases any resources held by the provider.
    /// <para>释放提供程序持有的所有资源。</para>
    /// </summary>
    public void Dispose() { }
}