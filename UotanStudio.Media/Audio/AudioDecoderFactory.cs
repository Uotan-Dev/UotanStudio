using System.Buffers;
using UotanStudio.Media.GStreamer;

namespace UotanStudio.Media.Audio;

/// <summary>Wire audio codecs supported by screen mirroring. <para>投屏支持的音频编码格式。</para></summary>
public enum AudioCodecId
{
    /// <summary>AAC-LC. <para>AAC-LC 音频编码。</para></summary>
    Aac,
    /// <summary>Opus. <para>Opus 音频编码。</para></summary>
    Opus,
    /// <summary>FLAC. <para>FLAC 无损音频编码。</para></summary>
    Flac,
    /// <summary>48 kHz stereo signed 16-bit PCM. <para>48 kHz 立体声 16 位 PCM。</para></summary>
    Pcm,
}

/// <summary>Audio decoder selection. <para>音频解码器选择。</para></summary>
public enum AudioDecoderBackend
{
    /// <summary>Prefer platform native AAC; other codecs use GStreamer. <para>优先平台原生 AAC；其他编码使用 GStreamer。</para></summary>
    Auto,
    /// <summary>Platform native AAC. <para>平台原生 AAC。</para></summary>
    Native,
    /// <summary>GStreamer codec plugins. <para>GStreamer 编解码插件。</para></summary>
    GStreamer,
}

/// <summary>Unified packet audio decoder factory. <para>统一的分包音频解码工厂。</para></summary>
public static class AudioDecoderFactory
{
    /// <summary>
    /// Creates an uninitialized decoder for the requested codec and backend.
    /// <para>为指定的音频编码与后端创建未初始化的解码器。</para>
    /// </summary>
    /// <param name="codec">Wire codec of the incoming packets; <see cref="AudioCodecId.Pcm"/> ignores the backend.
    /// <para>待解码数据包的音频编码；<see cref="AudioCodecId.Pcm"/> 会忽略后端选择。</para></param>
    /// <param name="backend">Decoder selection; <see cref="AudioDecoderBackend.Native"/> never falls back.
    /// <para>解码器选择；<see cref="AudioDecoderBackend.Native"/> 不会回退到其他后端。</para></param>
    /// <returns>An uninitialized decoder owned by the caller, which must call
    /// <see cref="IAudioDecoder.Initialize"/> before decoding and dispose it when done.
    /// <para>调用方持有的未初始化解码器；解码前须调用 <see cref="IAudioDecoder.Initialize"/>，用毕须释放。</para></returns>
    /// <exception cref="NotSupportedException">
    /// <see cref="AudioDecoderBackend.Native"/> was requested but no native decoder exists here for the codec.
    /// <para>选择了 <see cref="AudioDecoderBackend.Native"/>，但当前平台没有该编码的原生解码器。</para>
    /// </exception>
    /// <example>
    /// <code>
    /// using IAudioDecoder decoder = AudioDecoderFactory.Create(AudioCodecId.Flac);
    /// decoder.Initialize([], frame => { /* copy or queue the PCM inside this callback */ });
    /// decoder.Decode(packet, timestampTicks);
    /// </code>
    /// </example>
    public static IAudioDecoder Create(AudioCodecId codec = AudioCodecId.Aac, AudioDecoderBackend backend = AudioDecoderBackend.Auto)
    {
        if (codec == AudioCodecId.Pcm) return new PcmAudioDecoder();
        if (backend != AudioDecoderBackend.GStreamer && codec == AudioCodecId.Aac)
        {
            if (OperatingSystem.IsWindows()) return new MediaFoundationAacDecoder();
            if (OperatingSystem.IsMacOS()) return new AudioToolboxAacDecoder();
        }
        if (backend == AudioDecoderBackend.Native) throw new NotSupportedException("The selected native audio codec is unavailable on this platform.");
        return new GStreamerAudioDecoder(codec);
    }
}

internal sealed class PcmAudioDecoder : IAudioDecoder
{
    private Action<DecodedAudioFrame>? _callback;
    private bool _disposed;
    public bool IsInitialized => _callback is not null;
    public int SampleRate => 48000;
    public int Channels => 2;
    public int BitsPerSample => 16;
    public void Initialize(ReadOnlySpan<byte> codecConfig, Action<DecodedAudioFrame> frameCallback)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(frameCallback);
        _callback = frameCallback;
    }
    public void Decode(ReadOnlySpan<byte> packet, long timestampTicks)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_callback is null) throw new InvalidOperationException("PCM decoder is not initialized.");
        if (packet.IsEmpty || packet.Length % 4 != 0 || packet.Length > AudioFormatLimits.MaximumPacketBytes) throw new InvalidDataException("Invalid stereo PCM packet.");
        var data = ArrayPool<byte>.Shared.Rent(packet.Length);
        packet.CopyTo(data);
        using var frame = new DecodedAudioFrame(data, packet.Length, packet.Length / 4, 2, 48000, 16, timestampTicks);
        _callback(frame);
    }
    public void Reset() { ObjectDisposedException.ThrowIf(_disposed, this); }
    public void Dispose() { _callback = null; _disposed = true; }
}

/// <summary>
/// GStreamer packet audio decoding into bounded stereo PCM: compressed packets are pushed through a private
/// appsrc/decoder pipeline and re-emitted as interleaved S16LE 48 kHz stereo frames via the frame callback.
/// <para>基于 GStreamer 的分包音频解码，输出有界的立体声 PCM：压缩数据包经私有 appsrc/解码器管线推送，
/// 并以交织存放的 S16LE、48 kHz 立体声帧经帧回调重新输出。</para>
/// </summary>
/// <param name="codec">Wire codec the pipeline decodes; this backend does not serve raw PCM.
/// <para>管线所解码的音频编码；此后端不处理原始 PCM。</para></param>
public sealed unsafe class GStreamerAudioDecoder(AudioCodecId codec) : IAudioDecoder
{
    private GstPipeline? _pipeline;
    private byte[] _config = [];
    private Action<DecodedAudioFrame>? _callback;
    private bool _disposed;
    private bool _inCallback;
    /// <summary>
    /// Whether the GStreamer pipeline has been built by a successful <see cref="Initialize"/>.
    /// <para>是否已由成功的 <see cref="Initialize"/> 构建了 GStreamer 管线。</para>
    /// </summary>
    /// <value>
    /// <see langword="true"/> after <see cref="Initialize"/> or <see cref="Reset"/> builds the pipeline;
    /// <see langword="false"/> before initialization and after <see cref="Dispose"/>.
    /// <para>在 <see cref="Initialize"/> 或 <see cref="Reset"/> 构建管线后为 <see langword="true"/>；
    /// 初始化之前与 <see cref="Dispose"/> 之后为 <see langword="false"/>。</para>
    /// </value>
    public bool IsInitialized => _pipeline is not null;
    /// <summary>Output sample rate in hertz; the pipeline resamples every stream to this fixed rate.
    /// <para>输出采样率（赫兹）；管线会将所有码流重采样到该固定速率。</para></summary>
    /// <value>Always 48 000, even before initialization. <para>恒为 48 000，初始化之前同样如此。</para></value>
    public int SampleRate => 48000;
    /// <summary>Output channel count; the pipeline downmixes or upmixes every stream to stereo.
    /// <para>输出声道数；管线会将所有码流混为立体声。</para></summary>
    /// <value>Always 2. <para>恒为 2。</para></value>
    public int Channels => 2;
    /// <summary>Bit depth of each decoded PCM sample; output is forced to signed 16-bit little-endian.
    /// <para>每个解码 PCM 样本的位深；输出固定为有符号 16 位小端。</para></summary>
    /// <value>Always 16. <para>恒为 16。</para></value>
    public int BitsPerSample => 16;
    /// <summary>
    /// (Re)builds the pipeline for the selected codec and registers the frame callback.
    /// <para>为所选编码重建解码管线并登记帧回调。</para>
    /// </summary>
    /// <param name="codecConfig">
    /// Codec initialization bytes, at most 65 536 bytes; for FLAC a non-empty header is pushed into the pipeline
    /// immediately.
    /// <para>解码器初始化字节，最多 65 536 字节；FLAC 的非空头配置会在构建后立即推入管线。</para>
    /// </param>
    /// <param name="frameCallback">
    /// Receives every decoded PCM frame; the frame is disposed once the callback returns, so consumers must copy
    /// the samples inside it.
    /// <para>接收每个解码后的 PCM 帧；回调返回后帧即被释放，消费方必须在其内部复制样本。</para>
    /// </param>
    /// <exception cref="InvalidOperationException">Called from inside a frame callback. <para>在帧回调内部被调用。</para></exception>
    /// <exception cref="ObjectDisposedException">The decoder was disposed. <para>解码器已被释放。</para></exception>
    /// <exception cref="ArgumentNullException"><paramref name="frameCallback"/> is <see langword="null"/>.
    /// <para><paramref name="frameCallback"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="InvalidDataException">
    /// The configuration exceeds 65 536 bytes or is a malformed AAC AudioSpecificConfig.
    /// <para>配置超过 65 536 字节，或 AAC AudioSpecificConfig 格式无效。</para>
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// Opus was requested without a valid mono/stereo mapping-family-zero OpusHead.
    /// <para>Opus 缺少有效的单声道/立体声、映射族为零的 OpusHead。</para>
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">The codec selected by this instance is not decodable here.
    /// <para>当前实例选定的编码无法由此后端解码。</para></exception>
    public void Initialize(ReadOnlySpan<byte> codecConfig, Action<DecodedAudioFrame> frameCallback)
    {
        if (_inCallback) throw new InvalidOperationException("Decoder callbacks cannot reenter the decoder.");
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(frameCallback);
        if (codecConfig.Length > 65536) throw new InvalidDataException("Excessive audio codec configuration.");
        _pipeline?.Dispose(); _pipeline = null;
        _config = codecConfig.ToArray(); _callback = frameCallback;
        string caps, decoder;
        switch (codec)
        {
            case AudioCodecId.Aac:
                var (rate, channels) = AudioFormatLimits.ParseAacConfig(codecConfig);
                caps = $"audio/mpeg,mpegversion=4,stream-format=raw,rate={rate},channels={channels},codec_data=(buffer){Convert.ToHexString(codecConfig)}";
                decoder = "aacparse ! avdec_aac";
                break;
            case AudioCodecId.Opus:
                if (codecConfig.Length < 19 || !codecConfig[..8].SequenceEqual("OpusHead"u8) || codecConfig[9] is < 1 or > 2 || codecConfig[18] != 0)
                    throw new NotSupportedException("Expected mono/stereo OpusHead with mapping family zero.");
                caps = $"audio/x-opus,rate=48000,channels={codecConfig[9]},channel-mapping-family=0,streamheader=(buffer)<{Convert.ToHexString(codecConfig)}>";
                decoder = "opusdec";
                break;
            case AudioCodecId.Flac:
                caps = "audio/x-flac";
                decoder = "flacparse ! flacdec";
                break;
            default: throw new ArgumentOutOfRangeException(nameof(codec));
        }
        _pipeline = new GstPipeline($"appsrc name=input is-live=true format=time block=false max-bytes={GstPipeline.MaximumQueuedBytes} caps=\"{caps}\" ! {decoder} ! audioconvert ! audioresample ! audio/x-raw,format=S16LE,layout=interleaved,channels=2,rate=48000 ! appsink name=output sync=false max-buffers=32 drop=false enable-last-sample=false");
        if (codec == AudioCodecId.Flac && !codecConfig.IsEmpty) _pipeline.Push(codecConfig, 0);
    }
    /// <summary>
    /// Pushes one compressed packet into the pipeline with the given timestamp, then immediately drains decoded
    /// PCM through the frame callback.
    /// <para>将一帧压缩数据包连同时间戳推入管线，随后立即抽取解码出的 PCM 并经帧回调交付。</para>
    /// </summary>
    /// <param name="packet">
    /// One compressed audio packet, non-empty and at most 16 MiB.
    /// <para>单个压缩音频数据包，非空且不超过 16 MiB。</para>
    /// </param>
    /// <param name="timestampTicks">
    /// Packet presentation time in 100-nanosecond ticks, forwarded to the pipeline source.
    /// <para>数据包呈现时间，以 100 纳刻（tick）为单位，会转发给管线源。</para>
    /// </param>
    /// <exception cref="InvalidOperationException">Called from inside a frame callback, or the decoder is not initialized.
    /// <para>在帧回调内部被调用，或解码器尚未初始化。</para></exception>
    /// <exception cref="ObjectDisposedException">The decoder was disposed. <para>解码器已被释放。</para></exception>
    /// <exception cref="InvalidDataException">The packet is empty or exceeds the size limit.
    /// <para>数据包为空或超出大小上限。</para></exception>
    public void Decode(ReadOnlySpan<byte> packet, long timestampTicks)
    {
        if (_inCallback) throw new InvalidOperationException("Decoder callbacks cannot reenter the decoder.");
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pipeline is null) throw new InvalidOperationException("Audio decoder is not initialized.");
        if (packet.IsEmpty || packet.Length > AudioFormatLimits.MaximumPacketBytes) throw new InvalidDataException("Invalid compressed audio packet.");
        _pipeline.Push(packet, timestampTicks);
        Poll();
    }
    /// <summary>
    /// Drains decoded PCM samples from the pipeline without feeding another packet.
    /// <para>在不输入新数据包的情况下抽取管线中已解码的 PCM 样本。</para>
    /// </summary>
    /// <exception cref="InvalidOperationException">Called from inside a frame callback. <para>在帧回调内部被调用。</para></exception>
    /// <exception cref="InvalidDataException">
    /// Pipeline output is malformed, truncated, or exceeds the per-call frame limit.
    /// <para>管线输出格式无效、被截断，或超出单次调用帧数上限。</para>
    /// </exception>
    public void Poll()
    {
        if (_inCallback) throw new InvalidOperationException("Decoder callbacks cannot reenter the decoder.");
        if (_pipeline is null) return;
        for (var i = 0; i < 4096; i++)
        {
            var sample = _pipeline.Pull(i == 0 ? 10_000_000UL : 0);
            if (sample == 0) return;
            try
            {
                var source = GstNative.gst_sample_get_buffer(sample);
                var length = source == 0 ? 0 : GstNative.gst_buffer_get_size(source);
                if (length == 0 || length > AudioFormatLimits.MaximumPacketBytes || length % 4 != 0) throw new InvalidDataException("Invalid GStreamer PCM output.");
                var data = ArrayPool<byte>.Shared.Rent((int)length);
                fixed (byte* target = data)
                {
                    if (GstNative.gst_buffer_extract(source, 0, target, length) != length) { ArrayPool<byte>.Shared.Return(data); throw new InvalidDataException("Truncated PCM buffer."); }
                }
                using var frame = new DecodedAudioFrame(data, (int)length, (int)length / 4, 2, 48000, 16, GstNative.GetTimestamp(source));
                _inCallback = true;
                try { _callback!(frame); } finally { _inCallback = false; }
            }
            finally { GstNative.gst_mini_object_unref(sample); }
        }
        throw new InvalidDataException("Excessive audio decoder output.");
    }
    /// <summary>
    /// Rebuilds the pipeline with the stored codec configuration and callback, discarding all queued decoder
    /// state. The decoder stays initialized.
    /// <para>使用先前保存的编码配置与回调重建管线，丢弃全部解码器排队状态；解码器保持已初始化。</para>
    /// </summary>
    /// <exception cref="InvalidOperationException">The decoder is not initialized, or called from inside a frame callback.
    /// <para>解码器尚未初始化，或在帧回调内部被调用。</para></exception>
    /// <exception cref="ObjectDisposedException">The decoder was disposed. <para>解码器已被释放。</para></exception>
    public void Reset() => Initialize(_config, _callback ?? throw new InvalidOperationException("Audio decoder is not initialized."));
    /// <summary>
    /// Releases the GStreamer pipeline and callback. Repeat calls are no-ops.
    /// <para>释放 GStreamer 管线与回调；重复调用无效果。</para>
    /// </summary>
    /// <exception cref="InvalidOperationException">Called from inside a frame callback. <para>在帧回调内部被调用。</para></exception>
    public void Dispose() { if (_inCallback) throw new InvalidOperationException("Decoder callbacks cannot dispose the decoder."); if (_disposed) return; _pipeline?.Dispose(); _pipeline = null; _callback = null; _disposed = true; }
}