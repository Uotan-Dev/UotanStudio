namespace UotanStudio.Media.Decoding;

/// <summary>
/// Abstract decoder that turns compressed H.264/H.265 NAL unit data into NV12 frames.
/// <para>将压缩的 H.264/H.265 NAL 单元数据解码为 NV12 帧的抽象解码器。</para>
/// </summary>
/// <remarks>
/// <para>
/// Implementations are push-style: the caller feeds access units through
/// <see cref="Decode(ReadOnlySpan{byte})"/> and receives decoded frames via the callback supplied to
/// <see cref="Initialize"/>. This mirrors how a screen-mirroring client consumes
/// a continuous bitstream from a socket.
/// </para>
/// <para>
/// 实现为推送式：调用方通过 <see cref="Decode(ReadOnlySpan{byte})"/> 输入访问单元，解码帧经
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
    /// <summary>
    /// Gets a value indicating whether the decoder is initialized and ready to decode.
    /// <para>获取一个值，该值指示解码器是否已初始化并可开始解码。</para>
    /// </summary>
    /// <value>
    /// <see langword="true"/> once <see cref="Initialize"/> has completed and the decoder has not been disposed.
    /// <para><see cref="Initialize"/> 完成且解码器尚未释放时为 <see langword="true"/>。</para>
    /// </value>
    bool IsInitialized { get; }

    /// <summary>
    /// Initializes the decoder for the given codec and frame size.
    /// <para>以指定的编码格式与帧尺寸初始化解码器。</para>
    /// </summary>
    /// <param name="codec">The compressed codec. <para>压缩编码格式。</para></param>
    /// <param name="width">Expected frame width in pixels. <para>预期帧宽度（像素）。</para></param>
    /// <param name="height">Expected frame height in pixels. <para>预期帧高度（像素）。</para></param>
    /// <param name="frameCallback">
    /// Invoked for every decoded frame; the frame is disposed by the decoder
    /// after the callback returns, so consumers must copy or convert within it.
    /// <para>每解出一帧时调用；回调返回后解码器会释放该帧，消费方须在回调内完成
    /// 复制或转换。</para>
    /// </param>
    void Initialize(VideoCodecId codec, int width, int height, Action<DecodedVideoFrame> frameCallback);

    /// <summary>
    /// Feeds one compressed access unit into the decoder. Decoded frames are
    /// delivered through the callback registered in <see cref="Initialize"/>.
    /// <para>将一个压缩访问单元送入解码器。解码帧经 <see cref="Initialize"/>
    /// 注册的回调返回。</para>
    /// </summary>
    /// <param name="accessUnit">
    /// One complete access unit (a single NAL unit or a full frame of NAL units).
    /// <para>一个完整的访问单元（单个 NAL 单元或一帧的全部 NAL 单元）。</para>
    /// </param>
    /// <exception cref="InvalidOperationException">Thrown when not initialized. <para>未初始化时抛出。</para></exception>
    void Decode(ReadOnlySpan<byte> accessUnit);

    /// <summary>
    /// Feeds an Annex-B access unit with an explicit presentation timestamp in TimeSpan ticks.
    /// <para>送入带显式显示时间戳（以 TimeSpan tick 计）的 Annex-B 访问单元。</para>
    /// </summary>
    /// <param name="accessUnit">One complete Annex-B access unit. <para>一个完整的 Annex-B 访问单元。</para></param>
    /// <param name="timestampTicks">Presentation timestamp in TimeSpan ticks. <para>以 TimeSpan tick 计的显示时间戳。</para></param>
    void Decode(ReadOnlySpan<byte> accessUnit, long timestampTicks);

    /// <summary>
    /// Delivers delayed output at end of stream; reset before decoding a new stream.
    /// <para>在流结束时交付滞后的输出；解码新码流前须先重置。</para>
    /// </summary>
    void Drain();
    /// <summary>
    /// Delivers asynchronously completed output without consuming another input packet.
    /// <para>提取已完成的异步输出，不消耗新的输入包。</para>
    /// </summary>
    void Poll() { }

    /// <summary>
    /// Resets decoder state (after a resolution change or stream discontinuity);
    /// the decoder remains initialized with the same codec and callback.
    /// <para>重置解码器状态（分辨率变更或码流不连续后调用）；解码器保持以相同的
    /// 编码格式与回调处于已初始化状态。</para>
    /// </summary>
    void Reset();
}