using System.Buffers;

namespace UotanStudio.Media.Audio;

/// <summary>
/// Decoded interleaved PCM audio produced by an audio decoder, owning a pooled buffer.
/// <para>音频解码器输出的交错 PCM：持有池化缓冲区。</para>
/// </summary>
/// <remarks>
/// Instances own the pooled buffer and must be disposed exactly once by the
/// consumer, promptly after consuming the samples.
/// 实例持有池化缓冲区，必须由消费方恰好释放一次；消费采样后应立即释放。
/// </remarks>
public sealed class DecodedAudioFrame : IDisposable
{
    private byte[]? _buffer;

    /// <summary>Creates a frame that takes ownership of an existing pooled buffer.</summary>
    /// <summary>创建一个接管现有池化缓冲区所有权的帧。</summary>
    /// <param name="buffer">The pooled buffer holding interleaved PCM. 持有交错 PCM 的池化缓冲区。</param>
    /// <param name="length">The number of valid bytes in <paramref name="buffer"/>. <paramref name="buffer"/> 中的有效字节数。</param>
    /// <param name="sampleCount">Per-channel sample frames. 每通道采样帧数。</param>
    /// <param name="channels">Channel count. 通道数。</param>
    /// <param name="sampleRate">Samples per second. 每秒采样数。</param>
    /// <param name="bitsPerSample">Bits per sample (16 or 32). 每采样位深（16 或 32）。</param>
    /// <param name="timestampTicks">Presentation timestamp in ticks. 显示时间戳（tick）。</param>
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

    /// <summary>Gets the number of valid bytes in <see cref="Buffer"/>.</summary>
    /// <summary>获取 <see cref="Buffer"/> 中的有效字节数。</summary>
    public int Length { get; }

    /// <summary>Gets the per-channel sample frames.</summary>
    /// <summary>获取每通道采样帧数。</summary>
    public int SampleCount { get; }

    /// <summary>Gets the channel count.</summary>
    /// <summary>获取通道数。</summary>
    public int Channels { get; }

    /// <summary>Gets the samples per second.</summary>
    /// <summary>获取每秒采样数。</summary>
    public int SampleRate { get; }

    /// <summary>Gets the bits per sample (16 = int16, 32 = float32).</summary>
    /// <summary>获取每采样位深（16 = int16，32 = float32）。</summary>
    public int BitsPerSample { get; }

    /// <summary>Gets the presentation timestamp in ticks.</summary>
    /// <summary>获取显示时间戳（tick）。</summary>
    public long TimestampTicks { get; }

    /// <summary>Gets the buffer holding interleaved PCM (valid range <c>[0, Length)</c>).</summary>
    /// <summary>获取持有交错 PCM 的缓冲区（有效范围 <c>[0, Length)</c>）。</summary>
    public byte[] Buffer => _buffer ?? throw new ObjectDisposedException(nameof(DecodedAudioFrame));

    /// <summary>Returns the valid portion of the PCM buffer. Throws if disposed.</summary>
    /// <summary>返回 PCM 缓冲区的有效部分；已释放时抛出异常。</summary>
    public ReadOnlySpan<byte> Span => Buffer.AsSpan(0, Length);

    /// <inheritdoc />
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
/// 实例非线程安全，应由单一音频循环持有。
/// </remarks>
public interface IAudioDecoder : IDisposable
{
    /// <summary>Gets a value indicating whether the decoder is initialized.</summary>
    /// <summary>获取一个值，该值指示解码器是否已初始化。</summary>
    bool IsInitialized { get; }

    /// <summary>Gets the output sample rate; valid once initialized.</summary>
    /// <summary>获取输出采样率；初始化后有效。</summary>
    int SampleRate { get; }

    /// <summary>Gets the output channel count; valid once initialized.</summary>
    /// <summary>获取输出通道数；初始化后有效。</summary>
    int Channels { get; }

    /// <summary>Gets the output bits per sample (16 = int16, 32 = float32).</summary>
    /// <summary>获取输出位深（16 = int16，32 = float32）。</summary>
    int BitsPerSample { get; }

    /// <summary>
    /// Initializes the decoder. <paramref name="codecConfig"/> is the codec private
    /// data (e.g. AAC AudioSpecificConfig); it may be empty when the first
    /// compressed packet carries it.
    /// </summary>
    /// <summary>
    /// 初始化解码器。<paramref name="codecConfig"/> 为编码器私有数据
    /// （如 AAC AudioSpecificConfig）；首个压缩包自带配置时可为空。
    /// </summary>
    void Initialize(ReadOnlySpan<byte> codecConfig, Action<DecodedAudioFrame> frameCallback);

    /// <summary>Feeds one compressed audio packet; PCM is delivered via the callback.</summary>
    /// <summary>输入一个压缩音频包；PCM 经回调返回。</summary>
    void Decode(ReadOnlySpan<byte> packet, long timestampTicks);

    /// <summary>Resets decoder state for a new stream; the decoder stays initialized.</summary>
    /// <summary>重置解码器状态以开始新码流；解码器保持已初始化。</summary>
    void Reset();
    /// <summary>Delivers pending asynchronous PCM output. 提取待处理的异步 PCM 输出。</summary>
    void Poll() { }
}