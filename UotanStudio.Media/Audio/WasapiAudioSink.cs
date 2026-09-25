using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using UotanStudio.Media.Decoding.MediaFoundation;

namespace UotanStudio.Media.Audio;

/// <summary>
/// Windows shared-mode PCM playback with system channel/rate conversion.
/// <para>Windows 共享模式 PCM 播放，由系统进行声道与采样率转换。</para>
/// </summary>
/// <remarks>
/// <para>
/// The instance binds to the thread that constructed it; every public member must
/// be called from that thread and throws <see cref="InvalidOperationException"/>
/// otherwise. The sink owns its WASAPI COM interfaces until <see cref="Dispose"/>.
/// </para>
/// <para>
/// 实例绑定到构造它的线程；所有公共成员必须在该楼层调用，否则抛出
/// <see cref="InvalidOperationException"/>。在 <see cref="Dispose"/> 之前，sink 持有其 WASAPI COM 接口。
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed unsafe partial class WasapiAudioSink : IAudioSink
{
    private static readonly Guid EnumeratorClass = new("bcde0395-e52f-467c-8e3d-c4579291692e");
    private static readonly Guid EnumeratorInterface = new("a95664d2-9614-4f35-a746-de8db63617e6");
    private static readonly Guid AudioClientInterface = new("1cb9ad4c-dbfa-4c32-b178-c2f568a703b2");
    private static readonly Guid RenderInterface = new("f294acfc-3146-4483-a7bf-addca7c260e2");
    private static readonly Guid VolumeInterface = new("87ce5498-68d6-44e5-9215-6da47ef883d8");
    private nint _enumerator, _device, _client, _render, _volume;
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private bool _disposed, _comOwned;
    private int _blockAlign;
    private uint _capacity;
    private float _gain = 1;
    /// <summary>
    /// Gets a value indicating whether the sink is open.
    /// <para>获取一个值，指示输出端是否已打开。</para>
    /// </summary>
    /// <value>
    /// <see langword="true"/> while the WASAPI render client is started, from a successful
    /// <see cref="Open"/> until the next close or <see cref="Dispose"/>.
    /// <para>WASAPI 渲染客户端处于运行状态时为 <see langword="true"/>，即从成功 <see cref="Open"/> 到下次关闭或 <see cref="Dispose"/> 之间。</para>
    /// </value>
    public bool IsOpen => _render != 0;
    /// <summary>
    /// Gets the channel count of the open format.
    /// <para>获取已打开格式的通道数。</para>
    /// </summary>
    /// <value>
    /// The channels given to the last successful <see cref="Open"/>, or 0 before opening.
    /// <para>最近一次成功 <see cref="Open"/> 使用的通道数；打开之前为 0。</para>
    /// </value>
    public int Channels { get; private set; }
    /// <summary>
    /// Gets the sample rate of the open format.
    /// <para>获取已打开格式的采样率。</para>
    /// </summary>
    /// <value>
    /// The sample rate in hertz given to the last successful <see cref="Open"/>, or 0 before opening.
    /// <para>最近一次成功 <see cref="Open"/> 使用的采样率（赫兹）；打开之前为 0。</para>
    /// </value>
    public int SampleRate { get; private set; }
    /// <summary>
    /// Gets or sets the output volume applied through the endpoint volume interface.
    /// <para>获取或设置经端点音量接口施加的输出音量。</para>
    /// </summary>
    /// <value>
    /// The current gain in [0, 1]; assigned values are clamped to that range.
    /// <para>当前增益，范围 [0, 1]；赋出的值会被裁剪到该范围。</para>
    /// </value>
    /// <exception cref="ArgumentOutOfRangeException">The assigned value is not finite. <para>赋入的值非有限数时抛出。</para></exception>
    /// <exception cref="InvalidOperationException">Called from another thread, or the COM volume call fails. <para>在其他线程调用，或 COM 音量调用失败时抛出。</para></exception>
    /// <exception cref="ObjectDisposedException">The sink has been disposed. <para>输出端已释放时抛出。</para></exception>
    public float Volume
    {
        get => _gain;
        set
        {
            CheckThread();
            if (!float.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
            _gain = Math.Clamp(value, 0, 1);
            if (_volume != 0) Check(((delegate* unmanaged[Stdcall]<nint, float, nint, int>)Slot(_volume, 3))(_volume, _gain, 0), "volume");
        }
    }
    /// <summary>
    /// Opens the default WASAPI render endpoint in shared mode, starts playback, and applies the
    /// stored volume; any previous session is closed first.
    /// <para>以共享模式打开默认 WASAPI 渲染端点，启动播放并应用已存储的音量；先关闭此前的会话。</para>
    /// </summary>
    /// <param name="channels">Channel count, 1 to 8. <para>通道数，1 到 8。</para></param>
    /// <param name="sampleRate">Sample rate in hertz, 7350 to 192000. <para>采样率（赫兹），7350 到 192000。</para></param>
    /// <param name="bitsPerSample">Bits per sample, 16 or 32. <para>每样本位数，16 或 32。</para></param>
    /// <exception cref="ArgumentOutOfRangeException">The format is outside the supported bounds. <para>格式超出支持范围时抛出。</para></exception>
    /// <exception cref="InvalidOperationException">Called from another thread, or a WASAPI/COM call fails. <para>在其他线程调用，或 WASAPI/COM 调用失败时抛出。</para></exception>
    /// <exception cref="InvalidDataException">The endpoint reports an invalid buffer capacity. <para>端点报告的缓冲区容量非法时抛出。</para></exception>
    /// <exception cref="ObjectDisposedException">The sink has been disposed. <para>输出端已释放时抛出。</para></exception>
    public void Open(int channels, int sampleRate, int bitsPerSample)
    {
        CheckThread();
        AudioFormatLimits.Validate(channels, sampleRate, bitsPerSample);
        Close();
        int hr = MfInterop.CoInitializeEx(0, 0);
        if (hr != unchecked((int)0x80010106)) Check(hr, "COM initialization");
        _comOwned = hr >= 0;
        try
        {
            Check(CoCreateInstance(EnumeratorClass, 0, 23, EnumeratorInterface, out _enumerator), "device enumerator");
            nint device;
            Check(((delegate* unmanaged[Stdcall]<nint, int, int, nint*, int>)Slot(_enumerator, 4))(_enumerator, 0, 0, &device), "default endpoint");
            _device = device;
            nint client;
            fixed (Guid* iid = &AudioClientInterface)
                Check(((delegate* unmanaged[Stdcall]<nint, Guid*, uint, nint, nint*, int>)Slot(_device, 3))(_device, iid, 23, 0, &client), "audio client");
            _client = client;
            _blockAlign = channels * (bitsPerSample / 8);
            var format = new WaveFormatEx
            {
                FormatTag = (ushort)(bitsPerSample == 32 ? 3 : 1),
                Channels = (ushort)channels,
                SamplesPerSecond = (uint)sampleRate,
                AverageBytesPerSecond = (uint)(sampleRate * _blockAlign),
                BlockAlign = (ushort)_blockAlign,
                BitsPerSample = (ushort)bitsPerSample,
            };
            Check(((delegate* unmanaged[Stdcall]<nint, int, uint, long, long, WaveFormatEx*, nint, int>)Slot(_client, 3))
                (_client, 0, 0x88000000, 1_000_000, 0, &format, 0), "Initialize");
            uint capacity;
            Check(((delegate* unmanaged[Stdcall]<nint, uint*, int>)Slot(_client, 4))(_client, &capacity), "buffer size");
            if (capacity == 0 || capacity > sampleRate * 10) throw new InvalidDataException("Invalid WASAPI buffer capacity.");
            _capacity = capacity;
            nint render, volume;
            fixed (Guid* iid = &RenderInterface)
                Check(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(_client, 14))(_client, iid, &render), "render service");
            _render = render;
            fixed (Guid* iid = &VolumeInterface)
                Check(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(_client, 14))(_client, iid, &volume), "volume service");
            _volume = volume;
            Volume = _gain;
            Check(((delegate* unmanaged[Stdcall]<nint, int>)Slot(_client, 10))(_client), "start");
            Channels = channels;
            SampleRate = sampleRate;
        }
        catch { Close(); throw; }
    }
    /// <summary>
    /// Writes interleaved PCM bytes to the endpoint in block-aligned chunks, waiting up to two
    /// seconds of silence for the device to consume data before each successful hand-off.
    /// <para>按块对齐的分片向端点写入交错 PCM 字节；每次成功交付前最多等待两秒让设备消费数据。</para>
    /// </summary>
    /// <param name="pcm">
    /// PCM bytes whose length is a multiple of the open format's block alignment and within the
    /// bounded packet size; an empty span returns immediately.
    /// <para>长度必须是已打开格式块对齐的整数倍且不超过有界的包大小；空 span 立即返回。</para>
    /// </param>
    /// <exception cref="InvalidOperationException">The sink is not open, it was called from another thread, or a WASAPI call fails. <para>输出端未打开、在其他线程调用，或 WASAPI 调用失败时抛出。</para></exception>
    /// <exception cref="ArgumentException">The length exceeds the packet limit or is not block-aligned. <para>长度超过包上限或未块对齐时抛出。</para></exception>
    /// <exception cref="TimeoutException">The endpoint stops consuming PCM for two seconds. <para>端点连续两秒不消费 PCM 时抛出。</para></exception>
    /// <exception cref="InvalidDataException">The endpoint reports padding beyond its capacity. <para>端点报告的填充超出其容量时抛出。</para></exception>
    /// <exception cref="ObjectDisposedException">The sink has been disposed. <para>输出端已释放时抛出。</para></exception>
    public void Write(ReadOnlySpan<byte> pcm)
    {
        CheckThread();
        if (!IsOpen) throw new InvalidOperationException("Audio sink is not open.");
        if (pcm.Length > AudioFormatLimits.MaximumPacketBytes || pcm.Length % _blockAlign != 0) throw new ArgumentException("Invalid PCM packet.", nameof(pcm));
        long deadline = Environment.TickCount64 + 2000;
        while (!pcm.IsEmpty)
        {
            uint padding;
            Check(((delegate* unmanaged[Stdcall]<nint, uint*, int>)Slot(_client, 6))(_client, &padding), "padding");
            if (padding > _capacity) throw new InvalidDataException("Invalid WASAPI padding.");
            uint frames = Math.Min(_capacity - padding, (uint)(pcm.Length / _blockAlign));
            if (frames == 0)
            {
                if (Environment.TickCount64 >= deadline) throw new TimeoutException("Audio endpoint stopped consuming PCM.");
                Thread.Sleep(1);
                continue;
            }
            nint data;
            Check(((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(_render, 3))(_render, frames, &data), "GetBuffer");
            int bytes = checked((int)frames * _blockAlign);
            pcm[..bytes].CopyTo(new Span<byte>((void*)data, bytes));
            Check(((delegate* unmanaged[Stdcall]<nint, uint, uint, int>)Slot(_render, 4))(_render, frames, 0), "ReleaseBuffer");
            pcm = pcm[bytes..];
            deadline = Environment.TickCount64 + 2000;
        }
    }
    private void CheckThread()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_thread != Environment.CurrentManagedThreadId) throw new InvalidOperationException("Audio sink belongs to another thread.");
    }
    private void Close()
    {
        if (_client != 0) _ = ((delegate* unmanaged[Stdcall]<nint, int>)Slot(_client, 11))(_client);
        MfCom.Release(_volume); _volume = 0;
        MfCom.Release(_render); _render = 0;
        MfCom.Release(_client); _client = 0;
        MfCom.Release(_device); _device = 0;
        MfCom.Release(_enumerator); _enumerator = 0;
        if (_comOwned) { MfInterop.CoUninitialize(); _comOwned = false; }
    }
    /// <summary>
    /// Stops playback, releases every WASAPI COM interface owned by the sink and uninitializes COM
    /// if this instance initialized it; calling it again has no effect.
    /// <para>停止播放、释放该实例持有的全部 WASAPI COM 接口，并在由本实例初始化时反初始化 COM；重复调用无效果。</para>
    /// </summary>
    /// <exception cref="InvalidOperationException">Called from another thread. <para>在其他线程调用时抛出。</para></exception>
    public void Dispose() { if (_disposed) return; CheckThread(); Close(); _disposed = true; }
    private static nint Slot(nint instance, int slot) => (nint)MfCom.PublicSlot(instance, slot);
    private static void Check(int hr, string operation) { if (hr < 0) throw new InvalidOperationException($"WASAPI {operation}: 0x{hr:X8}."); }
    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    internal struct WaveFormatEx
    {
        public ushort FormatTag, Channels;
        public uint SamplesPerSecond, AverageBytesPerSecond;
        public ushort BlockAlign, BitsPerSample, ExtraSize;
    }
    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint instance);
}