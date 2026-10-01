namespace UotanStudio.Media.Audio;

/// <summary>
/// A PCM audio output sink. Open once with the decoded format, then stream data.
/// <para>PCM 音频输出端：以解码格式打开一次，然后持续写入。</para>
/// </summary>
/// <remarks>
/// <para>Instances are not thread-safe; a single audio loop should own the instance.</para>
/// <para>实例非线程安全，应由单一音频循环持有。</para>
/// </remarks>
public interface IAudioSink : IDisposable
{
    /// <summary>
    /// Gets a value indicating whether the sink is open.
    /// <para>获取一个值，指示输出端是否已打开。</para>
    /// </summary>
    /// <value>
    /// <see langword="true"/> after a successful <see cref="Open"/> while the device stream is still active.
    /// <para><see cref="Open"/> 成功后、设备流仍处于活动状态时为 <see langword="true"/>。</para>
    /// </value>
    bool IsOpen { get; }

    /// <summary>
    /// Gets the channel count of the open format.
    /// <para>获取已打开格式的通道数。</para>
    /// </summary>
    /// <value>
    /// The channel count given to the last successful <see cref="Open"/>.
    /// <para>最近一次成功 <see cref="Open"/> 所使用的通道数。</para>
    /// </value>
    int Channels { get; }

    /// <summary>
    /// Gets the sample rate of the open format.
    /// <para>获取已打开格式的采样率。</para>
    /// </summary>
    /// <value>
    /// The sample rate in hertz given to the last successful <see cref="Open"/>.
    /// <para>最近一次成功 <see cref="Open"/> 所使用的采样率（赫兹）。</para>
    /// </value>
    int SampleRate { get; }

    /// <summary>
    /// Gets or sets the output volume in [0, 1].
    /// <para>获取或设置输出音量 [0, 1]。</para>
    /// </summary>
    /// <value>
    /// The current output gain; values outside [0, 1] are clamped by implementations.
    /// <para>当前输出增益；超出 [0, 1] 的值会由实现裁剪。</para>
    /// </value>
    float Volume { get; set; }

    /// <summary>
    /// Opens the sink for the given interleaved PCM format.
    /// <para>以给定的交错 PCM 格式打开输出端。</para>
    /// </summary>
    /// <param name="channels">Channel count. <para>通道数。</para></param>
    /// <param name="sampleRate">Sample rate in hertz. <para>采样率（赫兹）。</para></param>
    /// <param name="bitsPerSample">Bits per sample of the interleaved PCM data. <para>交错 PCM 数据的每样本位数。</para></param>
    void Open(int channels, int sampleRate, int bitsPerSample);

    /// <summary>
    /// Writes interleaved PCM samples, blocking while the device buffer is full.
    /// <para>写入交错 PCM；设备缓冲区满时阻塞。</para>
    /// </summary>
    /// <param name="pcm">Interleaved PCM bytes in the open format. <para>按已打开格式排列的交错 PCM 字节。</para></param>
    void Write(ReadOnlySpan<byte> pcm);
}

/// <summary>
/// Creates the platform audio sink (WASAPI on Windows, AudioUnit on macOS).
/// <para>创建平台音频输出端（Windows 为 WASAPI，macOS 为 AudioUnit）。</para>
/// </summary>
public static class AudioSinkFactory
{
    /// <summary>
    /// Creates an uninitialized platform sink; throws when none exists.
    /// <para>创建未初始化的平台输出端；平台不支持时抛出。</para>
    /// </summary>
    /// <returns>
    /// A closed sink for the current platform; call <see cref="IAudioSink.Open"/> before writing.
    /// <para>当前平台上尚未打开的输出端；写入前需先调用 <see cref="IAudioSink.Open"/>。</para>
    /// </returns>
    /// <exception cref="PlatformNotSupportedException">No audio sink exists on this platform. <para>该平台没有可用的音频输出端时抛出。</para></exception>
    public static IAudioSink Create()
    {
        if (OperatingSystem.IsWindows()) return new WasapiAudioSink();
        if (OperatingSystem.IsMacOS()) return new AudioUnitSink();
        if (OperatingSystem.IsLinux()) return new GStreamerAudioSink();
        throw new PlatformNotSupportedException("No audio sink is available on this platform.");
    }
}