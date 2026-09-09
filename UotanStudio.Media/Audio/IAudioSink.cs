using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace UotanStudio.Media.Audio;

/// <summary>
/// A PCM audio output sink. Open once with the decoded format, then stream data.
/// <para>PCM 音频输出端：以解码格式打开一次，然后持续写入。</para>
/// </summary>
/// <remarks>
/// Instances are not thread-safe; a single audio loop should own the instance.
/// 实例非线程安全，应由单一音频循环持有。
/// </remarks>
public interface IAudioSink : IDisposable
{
    /// <summary>Gets a value indicating whether the sink is open.</summary>
    /// <summary>获取一个值，指示输出端是否已打开。</summary>
    bool IsOpen { get; }

    /// <summary>Gets the channel count of the open format.</summary>
    /// <summary>获取已打开格式的通道数。</summary>
    int Channels { get; }

    /// <summary>Gets the sample rate of the open format.</summary>
    /// <summary>获取已打开格式的采样率。</summary>
    int SampleRate { get; }

    /// <summary>Gets or sets the output volume in [0, 1].</summary>
    /// <summary>获取或设置输出音量 [0, 1]。</summary>
    float Volume { get; set; }

    /// <summary>Opens the sink for the given interleaved PCM format.</summary>
    /// <summary>以给定的交错 PCM 格式打开输出端。</summary>
    void Open(int channels, int sampleRate, int bitsPerSample);

    /// <summary>Writes interleaved PCM samples, blocking while the device buffer is full.</summary>
    /// <summary>写入交错 PCM；设备缓冲区满时阻塞。</summary>
    void Write(ReadOnlySpan<byte> pcm);
}

/// <summary>Creates the platform audio sink (WASAPI on Windows, AudioUnit on macOS).</summary>
/// <summary>创建平台音频输出端（Windows 为 WASAPI，macOS 为 AudioUnit）。</summary>
public static class AudioSinkFactory
{
    /// <summary>Creates an uninitialized platform sink; throws when none exists.</summary>
    /// <summary>创建未初始化的平台输出端；平台不支持时抛出。</summary>
    public static IAudioSink Create()
    {
        if (OperatingSystem.IsWindows()) return new WasapiAudioSink();
        if (OperatingSystem.IsMacOS()) return new AudioUnitSink();
        throw new PlatformNotSupportedException("No audio sink is available on this platform.");
    }
}
