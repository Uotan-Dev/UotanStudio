namespace UotanStudio.Services.Projects;

/// <summary>
/// A stream-backed workflow value carrying an arbitrarily large payload without materializing it. Unlike a bare
/// <see cref="Stream"/>, the value can be opened any number of times and each opening starts at the beginning, so one
/// output may feed several downstream nodes, a preview and a re-run without interfering with each other.
/// <para>以流为载体的工作流值，承载任意大的载荷而不必整体物化。与裸 <see cref="Stream"/> 不同，该值可被打开任意多次，每次打开都从头开始，因此一个输出可以同时喂给多个下游节点、一次预览和一次重跑而互不干扰。</para>
/// </summary>
/// <remarks>
/// The owner of the value decides its lifetime: producers register it with the execution context through
/// <c>RegisterResource</c>, which releases it when the run ends or restarts. Readers never dispose the artifact and
/// never seek a stream they opened for someone else. The payload may live in a file or in memory; callers must not
/// infer which from the type.
/// <para>值的生命周期由所有者决定：产生方通过 <c>RegisterResource</c> 交给执行上下文，在运行结束或重跑时释放。
/// 读取方从不释放该工件，也不移动自己为他人打开的流。载荷可以落在文件中或内存中，调用方不得据类型推断其位置。</para>
/// </remarks>
public abstract class StreamArtifact : IAsyncDisposable
{
    /// <summary>
    /// Gets the payload length in bytes when it is known without reading the payload.
    /// <para>获取载荷长度（字节）；在无需读取载荷即可确定时给出该值。</para>
    /// </summary>
    /// <value>The length in bytes, or <see langword="null"/> when the payload size is unknown.
    /// <para>字节长度；载荷大小未知时为 <see langword="null"/>。</para></value>
    public abstract long? Length { get; }

    /// <summary>
    /// Gets a display name used by panels and logs, never the payload itself.
    /// <para>获取供面板与日志使用的显示名，绝不是载荷内容本身。</para>
    /// </summary>
    /// <value>A short caption such as a file name; <see langword="null"/> when no name applies.
    /// <para>形如文件名的短标题；不适用时为 <see langword="null"/>。</para></value>
    public abstract string? DisplayName { get; }

    /// <summary>
    /// Opens a new independent readable stream positioned at the first byte of the payload.
    /// <para>打开一条新的独立可读流，定位在载荷首字节。</para>
    /// </summary>
    /// <returns>A stream the caller owns and disposes; closing it never affects other readers.
    /// <para>由调用方持有并释放的流；关闭它不影响其他读取方。</para></returns>
    public abstract Stream OpenRead();

    /// <summary>
    /// Releases the payload. Implementations must tolerate being called after the payload has already gone.
    /// <para>释放载荷。实现必须容忍在载荷已被释放后再次调用。</para>
    /// </summary>
    /// <returns>A task that completes once the payload is released. <para>载荷释放后完成的任务。</para></returns>
    public abstract ValueTask DisposeAsync();
}