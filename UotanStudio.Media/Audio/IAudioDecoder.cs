using System.Buffers;

namespace UotanStudio.Media.Audio;

/// <summary>
/// Decoded interleaved PCM audio produced by an audio decoder, owning a pooled buffer.
/// <para>音频解码器输出的交错 PCM：持有池化缓冲区。</para>
/// </summary>
/// <remarks>
/// Instances own the pooled buffer and must be disposed exactly once by the
/// consumer, promptly after consuming the samples.
/// <para>实例持有池化缓冲区，必须由消费方恰好释放一次；消费采样后应立即释放。</para>
/// </remarks>
public sealed class DecodedAudioFrame : IDisposable
{
    private byte[]? _buffer;

    /// <summary>
    /// Creates a frame that takes ownership of an existing pooled buffer.
    /// <para>创建一个接管现有池化缓冲区所有权的帧。</para>
    /// </summary>
    /// <param name="buffer">The pooled buffer holding interleaved PCM. <para>持有交错 PCM 的池化缓冲区。</para></param>
    /// <param name="length">The number of valid bytes in <paramref name="buffer"/>. <para><paramref name="buffer"/> 中的有效字节数。</para></param>
    /// <param name="sampleCount">Per-channel sample frames. <para>每通道采样帧数。</para></param>
    /// <param name="channels">Channel count. <para>通道数。</para></param>
    /// <param name="sampleRate">Samples per second. <para>每秒采样数。</para></param>
    /// <param name="bitsPerSample">Bits per sample (16 or 32). <para>每采样位深（16 或 32）。</para></param>
    /// <param name="timestampTicks">Presentation timestamp in ticks. <para>显示时间戳（tick）。</para></param>
    public DecodedAudioFrame(byte[] buffer, int length, int sampleCount, int channels, int sampleRate, int bitsPerSample, long timestampTicks)
    {
        if (buffer is null) throw new ArgumentNullException(nameof(buffer));
        if (length < 0 || length > buffer.Length) throw new ArgumentOutOfRangeException(nameof(length));
        AudioFormatLimits.Validate(channels, sampleRate, bitsPerSample);
        if (sampleCount < 0 || length > AudioFormatLimits.MaximumPacketBytes
            || (long)sampleCount * channels * (bitsPerSample / 8) != length)
            throw new ArgumentException("PCM byte count does not match the sample layout.");
        _buffer = buffer;
        Length = length;
        SampleCount = sampleCount;
        Channels = channels;
        SampleRate = sampleRate;
        BitsPerSample = bitsPerSample;
        TimestampTicks = timestampTicks;
    }

    /// <summary>
    /// The number of valid bytes in <see cref="Buffer"/>.
    /// <para><see cref="Buffer"/> 中的有效字节数。</para>
    /// </summary>
    /// <value>Byte count within the pooled buffer, always in <c>[0, Buffer.Length]</c>. <para>池化缓冲区内的字节数，始终处于 <c>[0, Buffer.Length]</c> 范围内。</para></value>
    public int Length { get; }

    /// <summary>
    /// The per-channel sample frames.
    /// <para>每通道采样帧数。</para>
    /// </summary>
    /// <value>Sample-frame count shared by all channels. <para>各通道共有的采样帧数。</para></value>
    public int SampleCount { get; }

    /// <summary>
    /// The channel count.
    /// <para>通道数。</para>
    /// </summary>
    /// <value>Number of interleaved channels. <para>交错通道数量。</para></value>
    public int Channels { get; }

    /// <summary>
    /// The samples per second.
    /// <para>每秒采样数。</para>
    /// </summary>
    /// <value>Output sample rate in hertz. <para>以赫兹计的输出采样率。</para></value>
    public int SampleRate { get; }

    /// <summary>
    /// The bits per sample (16 = int16, 32 = float32).
    /// <para>每采样位深（16 = int16，32 = float32）。</para>
    /// </summary>
    /// <value>Sample width; 16 or 32. <para>采样位宽；为 16 或 32。</para></value>
    public int BitsPerSample { get; }

    /// <summary>
    /// The presentation timestamp in ticks.
    /// <para>显示时间戳（tick）。</para>
    /// </summary>
    /// <value>Tick timestamp carried through from the decoded packet. <para>由解码包透传的 tick 时间戳。</para></value>
    public long TimestampTicks { get; }

    /// <summary>
    /// The buffer holding interleaved PCM (valid range <c>[0, Length)</c>).
    /// <para>持有交错 PCM 的缓冲区（有效范围 <c>[0, Length)</c>）。</para>
    /// </summary>
    /// <value>The owned pooled array; valid bytes are the first <see cref="Length"/>. <para>所持有的池化数组；有效字节为前 <see cref="Length"/> 个。</para></value>
    /// <exception cref="ObjectDisposedException">The frame has already been disposed. <para>该帧已被释放。</para></exception>
    public byte[] Buffer => _buffer ?? throw new ObjectDisposedException(nameof(DecodedAudioFrame));

    /// <summary>
    /// The valid portion of the PCM buffer.
    /// <para>PCM 缓冲区的有效部分。</para>
    /// </summary>
    /// <value>Read-only span over <c>[0, Length)</c> of <see cref="Buffer"/>. <para>覆盖 <see cref="Buffer"/> 中 <c>[0, Length)</c> 的只读 span。</para></value>
    /// <exception cref="ObjectDisposedException">The frame has already been disposed. <para>该帧已被释放。</para></exception>
    public ReadOnlySpan<byte> Span => Buffer.AsSpan(0, Length);

    /// <summary>
    /// Returns the pooled buffer to <see cref="ArrayPool{T}"/>.<see cref="ArrayPool{T}.Shared"/>; safe to call once, later calls are no-ops.
    /// <para>将池化缓冲区归还到 <see cref="ArrayPool{T}"/>.<see cref="ArrayPool{T}.Shared"/>；调用一次即生效，后续调用不产生效果。</para>
    /// </summary>
    public void Dispose()
    {
        var buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is not null)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}

/// <summary>
/// Push-style audio decoder turning compressed packets (AAC) into interleaved PCM.
/// <para>推送式音频解码器：将压缩包（AAC）解码为交错 PCM。</para>
/// </summary>
/// <remarks>
/// Instances are not thread-safe; a single audio loop should own the instance.
/// <para>实例非线程安全，应由单一音频循环持有。</para>
/// </remarks>
public interface IAudioDecoder : IDisposable
{
    /// <summary>
    /// Whether the decoder is initialized.
    /// <para>解码器是否已初始化。</para>
    /// </summary>
    /// <value><see langword="true"/> once <see cref="Initialize"/> has succeeded; otherwise <see langword="false"/>. <para><see cref="Initialize"/> 成功后为 <see langword="true"/>，否则为 <see langword="false"/>。</para></value>
    bool IsInitialized { get; }

    /// <summary>
    /// The output sample rate; valid once initialized.
    /// <para>输出采样率；初始化后有效。</para>
    /// </summary>
    /// <value>Sample rate in hertz. <para>以赫兹计的采样率。</para></value>
    int SampleRate { get; }

    /// <summary>
    /// The output channel count; valid once initialized.
    /// <para>输出通道数；初始化后有效。</para>
    /// </summary>
    /// <value>Number of interleaved channels. <para>交错通道数量。</para></value>
    int Channels { get; }

    /// <summary>
    /// The output bits per sample (16 = int16, 32 = float32).
    /// <para>输出位深（16 = int16，32 = float32）。</para>
    /// </summary>
    /// <value>Sample width; 16 or 32. <para>采样位宽；为 16 或 32。</para></value>
    int BitsPerSample { get; }

    /// <summary>
    /// Initializes the decoder. <paramref name="codecConfig"/> is the codec private
    /// data (e.g. AAC AudioSpecificConfig); it may be empty when the first
    /// compressed packet carries it.
    /// <para>初始化解码器。<paramref name="codecConfig"/> 为编码器私有数据（如 AAC AudioSpecificConfig）；首个压缩包自带配置时可为空。</para>
    /// </summary>
    /// <param name="codecConfig">
    /// Codec private data, or empty when the first compressed packet carries it.
    /// <para>编码器私有数据；当首个压缩包自带配置时可为空。</para>
    /// </param>
    /// <param name="frameCallback">
    /// Invoked for each decoded frame; the callback owns and must dispose the frame.
    /// <para>对每一解码帧调用；回调接管该帧并必须将其释放。</para>
    /// </param>
    void Initialize(ReadOnlySpan<byte> codecConfig, Action<DecodedAudioFrame> frameCallback);

    /// <summary>
    /// Feeds one compressed audio packet; PCM is delivered via the callback.
    /// <para>输入一个压缩音频包；PCM 经回调返回。</para>
    /// </summary>
    /// <param name="packet">
    /// The compressed access unit to decode.
    /// <para>待解码的压缩访问单元。</para>
    /// </param>
    /// <param name="timestampTicks">
    /// Presentation timestamp stamped on the produced frames.
    /// <para>盖在生成帧上的显示时间戳。</para>
    /// </param>
    void Decode(ReadOnlySpan<byte> packet, long timestampTicks);

    /// <summary>
    /// Resets decoder state for a new stream; the decoder stays initialized.
    /// <para>重置解码器状态以开始新码流；解码器保持已初始化。</para>
    /// </summary>
    void Reset();
    /// <summary>
    /// Delivers pending asynchronous PCM output.
    /// <para>提取待处理的异步 PCM 输出。</para>
    /// </summary>
    void Poll() { }
}