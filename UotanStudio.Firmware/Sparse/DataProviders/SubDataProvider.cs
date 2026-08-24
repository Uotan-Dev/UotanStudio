namespace UotanStudio.Firmware.Sparse;

/// <summary>
/// A generic sub-provider that exposes a sub-range of an existing <see cref="ISparseDataProvider"/>.
/// Useful for creating sliced views over any data provider without knowing its concrete type.
/// <para>通用子提供程序，用于暴露现有 ISparseDataProvider 的子范围。
/// 适用于在不知道具体类型的情况下对任何数据提供程序创建切片视图。</para>
/// </summary>
/// <remarks>
/// Creates a new <see cref="SubDataProvider"/> that represents a sub-range of <paramref name="parent"/>.
/// <para>创建一个新的 SubDataProvider，表示父提供程序的子范围。</para>
/// </remarks>
/// <param name="parent">Parent data provider to wrap. <para>要包装的父数据提供程序。</para></param>
/// <param name="offset">Byte offset within the parent where the sub-range begins. <para>子范围在父提供程序中的起始字节偏移。</para></param>
/// <param name="length">Length in bytes of the sub-range. <para>子范围的字节长度。</para></param>
public class SubDataProvider(ISparseDataProvider parent, long offset, long length) : ISparseDataProvider
{
    private readonly ISparseDataProvider _parent = parent;
    private readonly long _offset = offset;

    /// <summary>
    /// Gets the total length, in bytes, of the sub-range exposed by this provider.
    /// <para>获取此提供程序所暴露的子范围的总字节长度。</para>
    /// </summary>
    public long Length { get; } = length;

    /// <summary>
    /// Read bytes into a byte array from the sub-range.
    /// <para>从子范围读取字节到字节数组。</para>
    /// </summary>
    /// <param name="inOffset">Byte offset relative to the sub-range to begin reading. <para>相对于子范围的起始读取字节偏移。</para></param>
    /// <param name="buffer">Destination buffer to receive bytes. <para>接收字节数据的目标缓冲区。</para></param>
    /// <param name="bufferOffset">Offset in the destination buffer to start writing. <para>目标缓冲区中的起始写入偏移。</para></param>
    /// <param name="count">Maximum number of bytes to read. <para>最大读取字节数。</para></param>
    /// <returns>The number of bytes actually read. <para>实际读取的字节数。</para></returns>
    public int Read(long inOffset, byte[] buffer, int bufferOffset, int count) => _parent.Read(_offset + inOffset, buffer, bufferOffset, (int)Math.Min(count, Length - inOffset));

    /// <summary>
    /// Read bytes into a <see cref="Span{Byte}"/> from the sub-range.
    /// <para>从子范围读取字节到 Span{Byte}。</para>
    /// </summary>
    /// <param name="inOffset">Byte offset relative to the sub-range to begin reading. <para>相对于子范围的起始读取字节偏移。</para></param>
    /// <param name="buffer">Span that will receive the data. <para>接收数据的 Span。</para></param>
    /// <returns>The number of bytes actually read. <para>实际读取的字节数。</para></returns>
    public int Read(long inOffset, Span<byte> buffer) => _parent.Read(_offset + inOffset, buffer[..(int)Math.Min(buffer.Length, Length - inOffset)]);

    /// <summary>
    /// Writing is not supported for this sub-provider.
    /// <para>此子提供程序不支持写入操作。</para>
    /// </summary>
    /// <param name="stream">Not used. <para>未使用。</para></param>
    public void WriteTo(Stream stream) => throw new NotSupportedException();

    /// <summary>
    /// Asynchronous writing is not supported for this sub-provider.
    /// <para>此子提供程序不支持异步写入操作。</para>
    /// </summary>
    /// <param name="stream">Not used. <para>未使用。</para></param>
    /// <param name="cancellationToken">Not used. <para>未使用。</para></param>
    public Task WriteToAsync(Stream stream, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    /// <summary>
    /// Creates a nested sub-provider relative to this sub-range.
    /// <para>创建相对于此子范围的嵌套子提供程序。</para>
    /// </summary>
    /// <param name="subOffset">Offset relative to this sub-range. <para>相对于此子范围的偏移。</para></param>
    /// <param name="subLength">Length in bytes for the nested sub-range. <para>嵌套子范围的字节长度。</para></param>
    /// <returns>A new <see cref="SubDataProvider"/> representing the nested slice. <para>表示嵌套切片的新 SubDataProvider。</para></returns>
    public ISparseDataProvider GetSubProvider(long subOffset, long subLength) => new SubDataProvider(_parent, _offset + subOffset, subLength);

    /// <summary>
    /// Dispose is a no-op for the sub-provider; the parent's lifetime is managed externally.
    /// <para>子提供程序的 Dispose 为空操作；父提供程序的生命周期由外部管理。</para>
    /// </summary>
    public void Dispose() { }
}