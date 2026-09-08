namespace UotanStudio.Media.Decoding;

/// <summary>
/// Abstract decoder that turns compressed H.264/H.265 NAL unit data into NV12 frames.
/// <para>将压缩的 H.264/H.265 NAL 单元数据解码为 NV12 帧的抽象解码器。</para>
/// </summary>
/// <remarks>
/// <para>
/// Implementations are push-style: the caller feeds access units through
/// <see cref="Decode"/> and receives decoded frames via the callback supplied to
/// <see cref="Initialize"/>. This mirrors how a screen-mirroring client consumes
/// a continuous bitstream from a socket.
/// </para>
/// <para>
/// 实现为推送式：调用方通过 <see cref="Decode"/> 输入访问单元，解码帧经
/// <see cref="Initialize"/> 提供的回调返回。这与投屏客户端从 socket 消费
/// 连续码流的方式一致。
/// </para>
/// <para>
/// Instances are not thread-safe; a single decode loop should own the instance.
/// The callback may be invoked on an arbitrary thread chosen by the native
/// decoder; consumers must synchronize if they touch shared state.
/// </para>
/// <para>
/// 实例非线程安全，应由单一解码循环持有。回调可能在原生解码器选择的任意线程
/// 上触发；访问共享状态时消费方须自行同步。
/// </para>
/// </remarks>
public interface IVideoDecoder : IDisposable
{
    /// <summary>Gets a value indicating whether the decoder is initialized and ready to decode.</summary>
    /// <summary>获取一个值，该值指示解码器是否已初始化并可开始解码。</summary>
    bool IsInitialized { get; }

    /// <summary>
    /// Initializes the decoder for the given codec and frame size.
    /// </summary>
    /// <summary>
    /// 以指定的编码格式与帧尺寸初始化解码器。
    /// </summary>
    /// <param name="codec">The compressed codec. 压缩编码格式。</param>
    /// <param name="width">Expected frame width in pixels. 预期帧宽度（像素）。</param>
    /// <param name="height">Expected frame height in pixels. 预期帧高度（像素）。</param>
    /// <param name="frameCallback">
    /// Invoked for every decoded frame; the frame is disposed by the decoder
    /// after the callback returns, so consumers must copy or convert within it.
    /// 每解出一帧时调用；回调返回后解码器会释放该帧，消费方须在回调内完成
    /// 复制或转换。
    /// </param>
    void Initialize(VideoCodecId codec, int width, int height, Action<DecodedVideoFrame> frameCallback);

    /// <summary>
    /// Feeds one compressed access unit into the decoder. Decoded frames are
    /// delivered through the callback registered in <see cref="Initialize"/>.
    /// </summary>
    /// <summary>
    /// 将一个压缩访问单元送入解码器。解码帧经 <see cref="Initialize"/>
    /// 注册的回调返回。
    /// </summary>
    /// <param name="accessUnit">
    /// One complete access unit (a single NAL unit or a full frame of NAL units).
    /// 一个完整的访问单元（单个 NAL 单元或一帧的全部 NAL 单元）。
    /// </param>
    /// <exception cref="InvalidOperationException">Thrown when not initialized. 未初始化时抛出。</exception>
    void Decode(ReadOnlySpan<byte> accessUnit);

    /// <summary>
    /// Resets decoder state (after a resolution change or stream discontinuity);
    /// the decoder remains initialized with the same codec and callback.
    /// </summary>
    /// <summary>
    /// 重置解码器状态（分辨率变更或码流不连续后调用）；解码器保持以相同的
    /// 编码格式与回调处于已初始化状态。
    /// </summary>
    void Reset();
}
