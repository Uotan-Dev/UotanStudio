using System.Buffers;
using System.Buffers.Binary;
using UotanStudio.Media.GStreamer;

namespace UotanStudio.Media.Audio;

/// <summary>
/// GStreamer PulseAudio/PipeWire or ALSA PCM playback.
/// <para>GStreamer Linux 音频输出。</para>
/// </summary>
/// <param name="alsa">
/// Selects the sink element: <see langword="true"/> uses the ALSA <c>alsasink</c>, <see langword="false"/> uses the PulseAudio <c>pulsesink</c>.
/// <para>选择输出元素：<see langword="true"/> 使用 ALSA 的 <c>alsasink</c>，<see langword="false"/> 使用 PulseAudio 的 <c>pulsesink</c>。</para>
/// </param>
public sealed class GStreamerAudioSink(bool alsa = false) : IAudioSink
{
    private GstPipeline? _pipeline;
    private int _bits;
    private long _samples;
    private bool _disposed;
    /// <summary>
    /// Whether a playback pipeline has been built.
    /// <para>是否已构建播放流水线。</para>
    /// </summary>
    /// <value><see langword="true"/> while the pipeline is alive; otherwise <see langword="false"/>. <para>流水线存活期间为 <see langword="true"/>，否则为 <see langword="false"/>。</para></value>
    public bool IsOpen => _pipeline is not null;
    /// <summary>
    /// The channel count the sink was opened with.
    /// <para>打开该输出端时指定的通道数。</para>
    /// </summary>
    /// <value>Channel count; valid once open. <para>通道数；打开后有效。</para></value>
    public int Channels { get; private set; }
    /// <summary>
    /// The sample rate the sink was opened with.
    /// <para>打开该输出端时指定的采样率。</para>
    /// </summary>
    /// <value>Sample rate in hertz; valid once open. <para>以赫兹计的采样率；打开后有效。</para></value>
    public int SampleRate { get; private set; }
    /// <summary>
    /// The playback gain applied when staging PCM, in the range <c>0.0</c> to <c>1.0</c>.
    /// <para>暂存 PCM 时应用的播放增益，取值范围为 <c>0.0</c> 到 <c>1.0</c>。</para>
    /// </summary>
    /// <value>The current gain; values below <c>0.0</c> clamp to <c>0.0</c> and values above <c>1.0</c> clamp to <c>1.0</c>. <para>当前增益；小于 <c>0.0</c> 的值被钳制为 <c>0.0</c>，大于 <c>1.0</c> 的值被钳制为 <c>1.0</c>。</para></value>
    /// <exception cref="ArgumentOutOfRangeException">The assigned value is not a finite number. <para>所赋的值不是有限数。</para></exception>
    public float Volume
    {
        get => field;
        set { if (!float.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value)); field = Math.Clamp(value, 0, 1); }
    } = 1;
    /// <summary>
    /// Builds a live appsrc playback pipeline for the given format, disposing any previous pipeline; 16-bit input maps to S16LE and 32-bit input to F32LE.
    /// <para>为指定格式构建实时 appsrc 播放流水线，并释放此前的流水线；16 位输入映射为 S16LE，32 位输入映射为 F32LE。</para>
    /// </summary>
    /// <param name="channels">
    /// Input channel count; must be 1 to 8.
    /// <para>输入通道数；必须为 1 至 8。</para>
    /// </param>
    /// <param name="sampleRate">
    /// Input sample rate in hertz; must be within 7350 to 192000.
    /// <para>以赫兹计的输入采样率；必须处于 7350 至 192000 之间。</para>
    /// </param>
    /// <param name="bitsPerSample">
    /// Input sample width; must be 16 or 32.
    /// <para>输入位宽；必须为 16 或 32。</para>
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The channel count, sample rate, or bit depth is outside the supported range.
    /// <para>通道数、采样率或位深超出支持范围。</para>
    /// </exception>
    /// <exception cref="ObjectDisposedException">
    /// The sink has been disposed.
    /// <para>输出端已被释放。</para>
    /// </exception>
    public void Open(int channels, int sampleRate, int bitsPerSample)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        AudioFormatLimits.Validate(channels, sampleRate, bitsPerSample);
        _pipeline?.Dispose(); _pipeline = null;
        _pipeline = new GstPipeline($"appsrc name=input is-live=true format=time block=false max-bytes={sampleRate * channels * bitsPerSample / 8} caps=audio/x-raw,format={(bitsPerSample == 16 ? "S16LE" : "F32LE")},layout=interleaved,channels={channels},rate={sampleRate} ! audioconvert ! audioresample ! {(alsa ? "alsasink" : "pulsesink")} sync=true", hasSink: false);
        Channels = channels; SampleRate = sampleRate; _bits = bitsPerSample; _samples = 0;
    }
    /// <summary>
    /// Pushes interleaved PCM into the pipeline, scaling samples to the current <see cref="Volume"/> when it differs from unity and applying little-endian 16-bit or 32-bit sample encoding; an empty span is ignored.
    /// <para>将交错 PCM 推入流水线：当 <see cref="Volume"/> 不为 1 时按其缩放采样，并采用小端 16 位或 32 位编码；空区间会被忽略。</para>
    /// </summary>
    /// <param name="pcm">
    /// Interleaved PCM whose length is a multiple of the frame size and no larger than the maximum packet size; read only for the duration of the call.
    /// <para>长度须为帧大小整数倍且不超过最大数据包大小的交错 PCM；仅在调用期间被读取。</para>
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// The sink has not been opened.
    /// <para>输出端尚未打开。</para>
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="pcm"/> is not frame aligned or exceeds the maximum packet size.
    /// <para><paramref name="pcm"/> 未按帧对齐，或超过最大数据包大小。</para>
    /// </exception>
    /// <exception cref="TimeoutException">
    /// The output stopped consuming PCM and the queue stayed above the half-second backlog for two seconds.
    /// <para>输出停止消费 PCM，且队列在半秒积压量以上持续两秒。</para>
    /// </exception>
    /// <exception cref="ObjectDisposedException">
    /// The sink has been disposed.
    /// <para>输出端已被释放。</para>
    /// </exception>
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
        if (Volume == 1)
        {
            _pipeline.Push(pcm, checked(_samples * 10_000_000 / SampleRate));
            _samples += pcm.Length / align;
            return;
        }
        byte[] bytes = ArrayPool<byte>.Shared.Rent(pcm.Length);
        try
        {
            pcm.CopyTo(bytes);
            Span<byte> samples = bytes.AsSpan(0, pcm.Length);
            for (int i = 0; i < samples.Length; i += _bits / 8)
            {
                if (_bits == 16) BinaryPrimitives.WriteInt16LittleEndian(samples[i..], (short)(BinaryPrimitives.ReadInt16LittleEndian(samples[i..]) * Volume));
                else BinaryPrimitives.WriteSingleLittleEndian(samples[i..], BinaryPrimitives.ReadSingleLittleEndian(samples[i..]) * Volume);
            }
            _pipeline.Push(bytes.AsSpan(0, pcm.Length), checked(_samples * 10_000_000 / SampleRate));
            _samples += pcm.Length / align;
        }
        finally { ArrayPool<byte>.Shared.Return(bytes); }
    }
    /// <summary>
    /// Disposes the playback pipeline and marks the sink closed; repeated calls are no-ops once disposed.
    /// <para>释放播放流水线并将输出端标记为已关闭；已释放后再次调用不产生效果。</para>
    /// </summary>
    public void Dispose() { if (_disposed) return; _pipeline?.Dispose(); _pipeline = null; _disposed = true; }
}