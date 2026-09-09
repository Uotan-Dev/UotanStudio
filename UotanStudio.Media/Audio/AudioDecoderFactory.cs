using System.Buffers;
using UotanStudio.Media.GStreamer;

namespace UotanStudio.Media.Audio;

/// <summary>Wire audio codecs supported by screen mirroring. 投屏音频编码格式。</summary>
public enum AudioCodecId
{
    /// <summary>AAC-LC.</summary>
    Aac,
    /// <summary>Opus.</summary>
    Opus,
    /// <summary>FLAC.</summary>
    Flac,
    /// <summary>48 kHz stereo signed 16-bit PCM. 48 kHz 立体声 16 位 PCM。</summary>
    Pcm,
}

/// <summary>Audio decoder selection. 音频解码器选择。</summary>
public enum AudioDecoderBackend
{
    /// <summary>Prefer platform native AAC; other codecs use GStreamer. 优先平台原生 AAC。</summary>
    Auto,
    /// <summary>Platform native AAC. 平台原生 AAC。</summary>
    Native,
    /// <summary>GStreamer codec plugins. GStreamer 编解码插件。</summary>
    GStreamer,
}

/// <summary>Unified packet audio decoder factory. 统一分包音频解码工厂。</summary>
public static class AudioDecoderFactory
{
    /// <summary>Creates an uninitialized decoder. 创建未初始化的解码器。</summary>
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
        byte[] data = ArrayPool<byte>.Shared.Rent(packet.Length);
        packet.CopyTo(data);
        using var frame = new DecodedAudioFrame(data, packet.Length, packet.Length / 4, 2, 48000, 16, timestampTicks);
        _callback(frame);
    }
    public void Reset() { ObjectDisposedException.ThrowIf(_disposed, this); }
    public void Dispose() { _callback = null; _disposed = true; }
}

/// <summary>GStreamer packet audio decoding into bounded stereo PCM. GStreamer 分包音频解码。</summary>
public sealed unsafe class GStreamerAudioDecoder(AudioCodecId codec) : IAudioDecoder
{
    private GstPipeline? _pipeline;
    private byte[] _config = [];
    private Action<DecodedAudioFrame>? _callback;
    private bool _disposed;
    private bool _inCallback;
    /// <inheritdoc />
    public bool IsInitialized => _pipeline is not null;
    /// <inheritdoc />
    public int SampleRate => 48000;
    /// <inheritdoc />
    public int Channels => 2;
    /// <inheritdoc />
    public int BitsPerSample => 16;
    /// <inheritdoc />
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
    /// <inheritdoc />
    public void Decode(ReadOnlySpan<byte> packet, long timestampTicks)
    {
        if (_inCallback) throw new InvalidOperationException("Decoder callbacks cannot reenter the decoder.");
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pipeline is null) throw new InvalidOperationException("Audio decoder is not initialized.");
        if (packet.IsEmpty || packet.Length > AudioFormatLimits.MaximumPacketBytes) throw new InvalidDataException("Invalid compressed audio packet.");
        _pipeline.Push(packet, timestampTicks);
        Poll();
    }
    /// <inheritdoc />
    public void Poll()
    {
        if (_inCallback) throw new InvalidOperationException("Decoder callbacks cannot reenter the decoder.");
        if (_pipeline is null) return;
        for (int i = 0; i < 4096; i++)
        {
            nint sample = _pipeline.Pull(i == 0 ? 10_000_000UL : 0);
            if (sample == 0) return;
            try
            {
                nint source = GstNative.gst_sample_get_buffer(sample);
                nuint length = source == 0 ? 0 : GstNative.gst_buffer_get_size(source);
                if (length == 0 || length > AudioFormatLimits.MaximumPacketBytes || length % 4 != 0) throw new InvalidDataException("Invalid GStreamer PCM output.");
                byte[] data = ArrayPool<byte>.Shared.Rent((int)length);
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
    /// <inheritdoc />
    public void Reset() => Initialize(_config, _callback ?? throw new InvalidOperationException("Audio decoder is not initialized."));
    /// <inheritdoc />
    public void Dispose() { if (_inCallback) throw new InvalidOperationException("Decoder callbacks cannot dispose the decoder."); if (_disposed) return; _pipeline?.Dispose(); _pipeline = null; _callback = null; _disposed = true; }
}