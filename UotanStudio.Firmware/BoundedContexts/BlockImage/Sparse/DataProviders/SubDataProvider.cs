namespace UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse.DataProviders;

public class SubDataProvider(ISparseDataProvider parent, long offset, long length) : ISparseDataProvider
{
    private readonly ISparseDataProvider _parent = parent;
    private readonly long _offset = offset;

    public long Length { get; } = length;

    /// <summary>
    /// Read bytes into a byte array from the sub-range.
    /// <para>从子范围读取字节到字节数组。</para>
    /// </summary>
    public int Read(long inOffset, byte[] buffer, int bufferOffset, int count) => _parent.Read(_offset + inOffset, buffer, bufferOffset, (int)Math.Min(count, Length - inOffset));

    /// <summary>
    /// Read bytes into a Span{Byte} from the sub-range.
    /// <para>从子范围读取字节到 Span{Byte}。</para>
    /// </summary>
    public int Read(long inOffset, Span<byte> buffer) => _parent.Read(_offset + inOffset, buffer[..(int)Math.Min(buffer.Length, Length - inOffset)]);

    /// <summary>
    /// Writing is not supported for this sub-provider.
    /// <para>此子提供程序不支持写入操作。</para>
    /// </summary>
    public void WriteTo(Stream stream) => throw new NotSupportedException();

    /// <summary>
    /// Asynchronous writing is not supported for this sub-provider.
    /// <para>此子提供程序不支持异步写入操作。</para>
    /// </summary>
    public Task WriteToAsync(Stream stream, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    /// <summary>
    /// Creates a nested sub-provider relative to this sub-range.
    /// <para>创建相对于此子范围的嵌套子提供程序。</para>
    /// </summary>
    public ISparseDataProvider GetSubProvider(long subOffset, long subLength) => new SubDataProvider(_parent, _offset + subOffset, subLength);

    /// <summary>
    /// Dispose is a no-op for the sub-provider; the parent's lifetime is managed externally.
    /// <para>子提供程序的 Dispose 为空操作；父提供程序的生命周期由外部管理。</para>
    /// </summary>
    public void Dispose() { }
}