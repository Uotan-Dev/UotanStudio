using System.Buffers;
using System.Buffers.Binary;
using UotanStudio.Media.GStreamer;

namespace UotanStudio.Media.Audio;

/// <summary>GStreamer PulseAudio/PipeWire or ALSA PCM playback. GStreamer Linux 音频输出。</summary>
public sealed class GStreamerAudioSink(bool alsa = false) : IAudioSink
{
    private GstPipeline? _pipeline;
    private int _bits;
    private long _samples;
    private bool _disposed;
    private float _volume = 1;
    /// <inheritdoc />
    public bool IsOpen => _pipeline is not null;
    /// <inheritdoc />
    public int Channels { get; private set; }
    /// <inheritdoc />
    public int SampleRate { get; private set; }
    /// <inheritdoc />
    public float Volume
    {
        get => _volume;
        set { if (!float.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value)); _volume = Math.Clamp(value, 0, 1); }
    }
    /// <inheritdoc />
    public void Open(int channels, int sampleRate, int bitsPerSample)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        AudioFormatLimits.Validate(channels, sampleRate, bitsPerSample);
        _pipeline?.Dispose(); _pipeline = null;
        _pipeline = new GstPipeline($"appsrc name=input is-live=true format=time block=false max-bytes={sampleRate * channels * bitsPerSample / 8} caps=audio/x-raw,format={(bitsPerSample == 16 ? "S16LE" : "F32LE")},layout=interleaved,channels={channels},rate={sampleRate} ! audioconvert ! audioresample ! {(alsa ? "alsasink" : "pulsesink")} sync=true", hasSink: false);
        Channels = channels; SampleRate = sampleRate; _bits = bitsPerSample; _samples = 0;
    }
    /// <inheritdoc />
    public void Write(ReadOnlySpan<byte> pcm)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pipeline is null) throw new InvalidOperationException("Audio sink is not open.");
        int align = Channels * (_bits / 8);
        if (pcm.Length > AudioFormatLimits.MaximumPacketBytes || pcm.Length % align != 0) throw new ArgumentException("Invalid PCM packet.", nameof(pcm));
        if (pcm.IsEmpty) return;
        long deadline = Environment.TickCount64 + 2000;
        while (_pipeline.QueuedBytes > (ulong)(SampleRate * align / 2))
        {
            _pipeline.CheckError();
            if (Environment.TickCount64 >= deadline) throw new TimeoutException("Audio output stopped consuming PCM.");
            Thread.Sleep(2);
        }
        if (_volume == 1)
        {
            _pipeline.Push(pcm, checked(_samples * 10_000_000 / SampleRate));
            _samples += pcm.Length / align;
            return;
        }
        byte[] bytes = ArrayPool<byte>.Shared.Rent(pcm.Length);
        try
        {
            pcm.CopyTo(bytes);
            if (_volume != 1)
            {
                Span<byte> samples = bytes.AsSpan(0, pcm.Length);
                for (int i = 0; i < samples.Length; i += _bits / 8)
                {
                    if (_bits == 16) BinaryPrimitives.WriteInt16LittleEndian(samples[i..], (short)(BinaryPrimitives.ReadInt16LittleEndian(samples[i..]) * _volume));
                    else BinaryPrimitives.WriteSingleLittleEndian(samples[i..], BinaryPrimitives.ReadSingleLittleEndian(samples[i..]) * _volume);
                }
            }
            _pipeline.Push(bytes.AsSpan(0, pcm.Length), checked(_samples * 10_000_000 / SampleRate));
            _samples += pcm.Length / align;
        }
        finally { ArrayPool<byte>.Shared.Return(bytes); }
    }
    /// <inheritdoc />
    public void Dispose() { if (_disposed) return; _pipeline?.Dispose(); _pipeline = null; _disposed = true; }
}