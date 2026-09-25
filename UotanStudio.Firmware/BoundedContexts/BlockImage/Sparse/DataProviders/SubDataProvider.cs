namespace UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse.DataProviders;

/// <summary>
/// Read-only view over a byte range of another data provider: every offset is translated by the
/// view's own start before reaching the parent. The parent is never disposed through this view, and
/// the caller must keep it alive for as long as the view is used.
/// <para>另一个数据提供程序上某个字节范围的只读视图：所有偏移在到达父提供程序前都会加上视图自身的起始位置。
/// 通过此视图不会释放父提供程序，调用方必须在使用视图期间保持其存活。</para>
/// </summary>
/// <param name="parent">
/// Underlying provider addressed with absolute offsets; it must outlive this view.
/// <para>以绝对偏移寻址的底层提供程序；必须比本视图存活更久。</para>
/// </param>
/// <param name="offset">
/// Absolute offset in <paramref name="parent"/> where this view starts.
/// <para>本视图在 <paramref name="parent"/> 中的绝对起始偏移。</para>
/// </param>
/// <param name="length">
/// Byte length of the view.
/// <para>视图的字节长度。</para>
/// </param>
public class SubDataProvider(ISparseDataProvider parent, long offset, long length) : ISparseDataProvider
{
    private readonly ISparseDataProvider _parent = parent;
    private readonly long _offset = offset;

    /// <summary>
    /// Byte length of the visible window.
    /// <para>可见窗口的字节长度。</para>
    /// </summary>
    /// <value>
    /// The length supplied at construction; it is not checked against the parent's remaining bytes.
    /// <para>构造时传入的长度；不会与父提供程序的剩余字节做校验。</para>
    /// </value>
    public long Length { get; } = length;

    /// <summary>
    /// Read bytes into a byte array from the sub-range.
    /// <para>从子范围读取字节到字节数组。</para>
    /// </summary>
    /// <param name="inOffset">
    /// Offset inside this view; <c>0</c> is the view's first byte.
    /// <para>本视图内的偏移；<c>0</c> 表示视图的第一个字节。</para>
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
    /// Requested byte count, capped at the bytes left in this view.
    /// <para>请求的字节数，会被限制在本视图的剩余字节内。</para>
    /// </param>
    /// <returns>
    /// Bytes copied by the parent, possibly fewer than requested when the source ends early.
    /// <para>由父提供程序复制的字节数；源提前结束时可能少于请求值。</para>
    /// </returns>
    public int Read(long inOffset, byte[] buffer, int bufferOffset, int count) => _parent.Read(_offset + inOffset, buffer, bufferOffset, (int)Math.Min(count, Length - inOffset));

    /// <summary>
    /// Read bytes into a Span{Byte} from the sub-range.
    /// <para>从子范围读取字节到 Span{Byte}。</para>
    /// </summary>
    /// <param name="inOffset">
    /// Offset inside this view; <c>0</c> is the view's first byte.
    /// <para>本视图内的偏移；<c>0</c> 表示视图的第一个字节。</para>
    /// </param>
    /// <param name="buffer">
    /// Destination span whose length is the maximum wanted.
    /// <para>长度即所需最大字节数的目标 Span。</para>
    /// </param>
    /// <returns>
    /// Bytes copied by the parent, never more than the bytes left in this view.
    /// <para>由父提供程序复制的字节数，不会超过本视图的剩余字节。</para>
    /// </returns>
    public int Read(long inOffset, Span<byte> buffer) => _parent.Read(_offset + inOffset, buffer[..(int)Math.Min(buffer.Length, Length - inOffset)]);

    /// <summary>
    /// Writing is not supported for this sub-provider.
    /// <para>此子提供程序不支持写入操作。</para>
    /// </summary>
    /// <param name="stream">
    /// Destination stream, never written because the view is read-only.
    /// <para>目标流；由于视图只读而不会被写入。</para>
    /// </param>
    /// <exception cref="NotSupportedException">
    /// Always thrown: the view exposes reads only.
    /// <para>始终抛出：视图仅支持读取。</para>
    /// </exception>
    public void WriteTo(Stream stream) => throw new NotSupportedException();

    /// <summary>
    /// Asynchronous writing is not supported for this sub-provider.
    /// <para>此子提供程序不支持异步写入操作。</para>
    /// </summary>
    /// <param name="stream">
    /// Destination stream, never written because the view is read-only.
    /// <para>目标流；由于视图只读而不会被写入。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Ignored; the call fails before performing I/O.
    /// <para>被忽略；调用在执行 I/O 之前即失败。</para>
    /// </param>
    /// <returns>
    /// No task is ever produced, because the call throws before it can return one.
    /// <para>不会返回任何 Task，因为调用在产生结果之前就已抛出。</para>
    /// </returns>
    /// <exception cref="NotSupportedException">
    /// Always thrown, synchronously, before a task is created.
    /// <para>始终在创建 Task 之前同步抛出：视图仅支持读取。</para>
    /// </exception>
    public Task WriteToAsync(Stream stream, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    /// <summary>
    /// Creates a nested sub-provider relative to this sub-range.
    /// <para>创建相对于此子范围的嵌套子提供程序。</para>
    /// </summary>
    /// <param name="subOffset">
    /// Offset inside this view where the nested view starts.
    /// <para>嵌套视图在本视图内的起始偏移。</para>
    /// </param>
    /// <param name="subLength">
    /// Byte length of the nested view.
    /// <para>嵌套视图的字节长度。</para>
    /// </param>
    /// <returns>
    /// New view over the same parent, starting at this view's offset plus <paramref name="subOffset"/>;
    /// the nesting is flattened onto the parent rather than chained.
    /// <para>基于同一父提供程序的新视图，起始于本视图偏移加上 <paramref name="subOffset"/>；
    /// 嵌套会被展平到父提供程序上，而不是形成链式结构。</para>
    /// </returns>
    public ISparseDataProvider GetSubProvider(long subOffset, long subLength) => new SubDataProvider(_parent, _offset + subOffset, subLength);

    /// <summary>
    /// Dispose is a no-op for the sub-provider; the parent's lifetime is managed externally.
    /// <para>子提供程序的 Dispose 为空操作；父提供程序的生命周期由外部管理。</para>
    /// </summary>
    public void Dispose() { }
}