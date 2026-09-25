using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace UotanStudio.Media.Audio;

/// <summary>
/// macOS AudioUnit output sink (kAudioUnitType_Output / kAudioUnitSubType_DefaultOutputUnit).
/// <para>macOS AudioUnit 输出端（kAudioUnitType_Output / kAudioUnitSubType_DefaultOutputUnit）。</para>
/// </summary>
/// <remarks>
/// <para>
/// Accepts 16-bit interleaved PCM; converts to the device's float32 stereo mix
/// format in <see cref="Write"/> and pushes through the AudioUnit render callback.
/// All calls use the creating thread; the callback only reads pre-staged PCM.
/// </para>
/// <para>
/// 接受 16 位交错 PCM；在 <see cref="Write"/> 中转换为设备 float32 立体声混音格式，
/// 经 AudioUnit 渲染回调推送。所有调用使用创建线程；回调仅读取预存的 PCM。
/// </para>
/// </remarks>
[SupportedOSPlatform("macos")]
public sealed unsafe partial class AudioUnitSink : IAudioSink
{
    private const string Au = "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";
    private const uint ComponentTypeOutput = 0x61756F75;
    private const uint ComponentSubTypeDefault = 0x64656661;
    private const uint ComponentManufacturer = 0x61706C63;
    private const uint ScopeOutput = 0;
    private const uint ScopeInput = 1;
    private const uint ElementOutput = 0;
    private const uint PropertyStreamFormat = 8;
    private const uint PropertyRenderCallback = 11;
    private const uint FormatIdPcm = 0x6C70636D;
    private nint _unit;
    private AudioPcmRingBuffer? _staging;
    private int _channels = 2, _sampleRate;
    private int _thread = Environment.CurrentManagedThreadId;
    private float _volumeValue = 1f;
    private GCHandle _self;
    private bool _disposed;

    /// <summary>
    /// Whether the output AudioUnit is instantiated and rendering.
    /// <para>输出 AudioUnit 是否已实例化并正在渲染。</para>
    /// </summary>
    /// <value><see langword="true"/> while the unit handle is held; otherwise <see langword="false"/>. <para>持有单元句柄期间为 <see langword="true"/>，否则为 <see langword="false"/>。</para></value>
    public bool IsOpen => _unit != 0;

    /// <summary>
    /// The channel count the sink was opened with.
    /// <para>打开该输出端时指定的通道数。</para>
    /// </summary>
    /// <value>Channel count (1 or 2); valid once open. <para>通道数（1 或 2）；打开后有效。</para></value>
    public int Channels => _channels;

    /// <summary>
    /// The sample rate the sink was opened with.
    /// <para>打开该输出端时指定的采样率。</para>
    /// </summary>
    /// <value>Sample rate in hertz; valid once open. <para>以赫兹计的采样率；打开后有效。</para></value>
    public int SampleRate => _sampleRate;

    /// <summary>
    /// The playback gain applied to staged PCM, in the range <c>0.0</c> to <c>1.0</c>.
    /// <para>应用于暂存 PCM 的播放增益，取值范围为 <c>0.0</c> 到 <c>1.0</c>。</para>
    /// </summary>
    /// <value>The current gain; values below <c>0.0</c> clamp to <c>0.0</c> and values above <c>1.0</c> clamp to <c>1.0</c>. <para>当前增益；小于 <c>0.0</c> 的值被钳制为 <c>0.0</c>，大于 <c>1.0</c> 的值被钳制为 <c>1.0</c>。</para></value>
    /// <exception cref="ArgumentOutOfRangeException">The assigned value is not a finite number. <para>所赋的值不是有限数。</para></exception>
    public float Volume
    {
        get => _volumeValue;
        set
        {
            if (!float.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
            _volumeValue = Math.Clamp(value, 0f, 1f);

        }
    }

    /// <summary>
    /// Opens the default output AudioUnit, closes any previous instance, and starts rendering; the unit is converted to the device float32 format internally.
    /// <para>打开默认输出 AudioUnit，关闭此前的实例并开始渲染；单元在内部转换为设备 float32 格式。</para>
    /// </summary>
    /// <param name="channels">
    /// Input channel count; must be 1 or 2.
    /// <para>输入通道数；必须为 1 或 2。</para>
    /// </param>
    /// <param name="sampleRate">
    /// Input sample rate in hertz; must be within 8000 to 192000.
    /// <para>以赫兹计的输入采样率；必须处于 8000 至 192000 之间。</para>
    /// </param>
    /// <param name="bitsPerSample">
    /// Input sample width; only 16-bit PCM is accepted.
    /// <para>输入位宽；仅接受 16 位 PCM。</para>
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The channel count, sample rate, or bit depth is outside the supported range.
    /// <para>通道数、采样率或位深超出支持范围。</para>
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The call runs on a thread other than the owning thread, the default output component is unavailable, or an AudioUnit call fails.
    /// <para>该调用在非属主线程上运行、默认输出组件不可用，或某个 AudioUnit 调用失败。</para>
    /// </exception>
    /// <exception cref="ObjectDisposedException">
    /// The sink has been disposed.
    /// <para>输出端已被释放。</para>
    /// </exception>
    public void Open(int channels, int sampleRate, int bitsPerSample)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (channels is < 1 or > 2 || sampleRate is < 8000 or > 192000 || bitsPerSample != 16)
            throw new ArgumentOutOfRangeException(nameof(bitsPerSample), "Only 16-bit mono/stereo PCM input is accepted.");
        if (_thread != Environment.CurrentManagedThreadId) throw new InvalidOperationException("The sink must remain on its owning thread.");
        Close();

        var description = new AudioComponentDescription
        {
            ComponentType = ComponentTypeOutput,
            ComponentSubType = ComponentSubTypeDefault,
            ComponentManufacturer = ComponentManufacturer,
        };
        nint component = AudioComponentFindNext(0, &description);
        if (component == 0) throw new InvalidOperationException("AudioUnit default output component is unavailable.");
        Check(AudioComponentInstanceNew(component, out _unit), "AudioComponentInstanceNew");
        _self = GCHandle.Alloc(this);
        try
        {
            var format = new AudioStreamBasicDescription
            {
                SampleRate = sampleRate,
                FormatId = FormatIdPcm,
                FormatFlags = 0x9,
                BytesPerPacket = 4 * (uint)channels,
                FramesPerPacket = 1,
                BytesPerFrame = 4 * (uint)channels,
                ChannelsPerFrame = (uint)channels,
                BitsPerChannel = 32,
            };
            Check(AudioUnitSetProperty(_unit, PropertyStreamFormat, ScopeInput, ElementOutput,
                &format, (uint)sizeof(AudioStreamBasicDescription)), "set stream format");
            var callback = new AURenderCallbackStruct { Procedure = &RenderProc, UserData = GCHandle.ToIntPtr(_self) };
            Check(AudioUnitSetProperty(_unit, PropertyRenderCallback, ScopeInput, ElementOutput,
                &callback, (uint)sizeof(AURenderCallbackStruct)), "set render callback");
            _channels = channels;
            _sampleRate = sampleRate;
            _staging = new AudioPcmRingBuffer(channels);
            Check(AudioUnitInitialize(_unit), "AudioUnitInitialize");
            Check(AudioOutputUnitStart(_unit), "AudioOutputUnitStart");
        }
        catch { Close(); throw; }
        _channels = channels;
        _sampleRate = sampleRate;
    }

    /// <summary>
    /// Stages 16-bit interleaved PCM for the render callback, scaled by the current <see cref="Volume"/>; an empty span is ignored.
    /// <para>将 16 位交错 PCM 暂存以供渲染回调读取，并按当前 <see cref="Volume"/> 缩放；空区间会被忽略。</para>
    /// </summary>
    /// <param name="pcm">
    /// Interleaved 16-bit PCM whose length is a multiple of the frame size; read only for the duration of the call.
    /// <para>长度须为帧大小整数倍的交错 16 位 PCM；仅在调用期间被读取。</para>
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="pcm"/> is not frame aligned or exceeds the maximum packet size.
    /// <para><paramref name="pcm"/> 未按帧对齐，或超过最大数据包大小。</para>
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The sink is not open or the call runs on a thread other than the owning thread.
    /// <para>输出端未打开，或该调用在非属主线程上运行。</para>
    /// </exception>
    /// <exception cref="ObjectDisposedException">
    /// The sink has been disposed.
    /// <para>输出端已被释放。</para>
    /// </exception>
    public void Write(ReadOnlySpan<byte> pcm)
    {
        CheckOpen();
        int blockAlign = _channels * 2;
        if (pcm.Length % blockAlign != 0) throw new ArgumentException("PCM length is not frame aligned.", nameof(pcm));
        if (pcm.IsEmpty) return;

        if (pcm.Length > AudioFormatLimits.MaximumPacketBytes) throw new ArgumentException("Excessive PCM packet.", nameof(pcm));
        _staging!.Write(pcm, _volumeValue);
    }

    [UnmanagedCallersOnly]
    private static int RenderProc(nint refCon, nint actionFlags, nint timestamp, uint bus, uint frames, nint data)
    {
        try
        {
            var sink = GCHandle.FromIntPtr(refCon).Target as AudioUnitSink;
            if (sink is null || data == 0) return -50;
            var buffer = (AudioBufferList*)data;
            if (buffer->NumberBuffers != 1 || buffer->Buffer0.Data == 0) return -50;
            uint required = checked(frames * (uint)sink._channels * 4);
            if (required > buffer->Buffer0.DataByteSize || required > AudioFormatLimits.MaximumPacketBytes) return -50;
            var destination = new Span<byte>((void*)buffer->Buffer0.Data, checked((int)required));
            var staging = sink._staging;
            if (staging is null) destination.Clear();
            else staging.Read(destination);
            return 0;
        }
        catch { return -50; }
    }
    private void CheckOpen()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsOpen) throw new InvalidOperationException("Sink is not open.");
        if (_thread != Environment.CurrentManagedThreadId) throw new InvalidOperationException("The sink must remain on its owning thread.");
    }

    /// <summary>
    /// Stops and disposes the output AudioUnit and releases the staging buffer and self handle; repeated calls are no-ops once disposed.
    /// <para>停止并释放输出 AudioUnit，同时解除暂存缓冲区与自引用句柄；已释放后再次调用不产生效果。</para>
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Close();
    }

    private void Close()
    {
        if (_unit != 0)
        {
            _ = AudioOutputUnitStop(_unit);
            AudioUnitUninitialize(_unit);
            AudioComponentInstanceDispose(_unit);
            _unit = 0;
        }
        if (_self.IsAllocated) _self.Free();
        _staging = null;
    }

    private static void Check(int status, string operation)
    {
        if (status != 0) throw new InvalidOperationException($"AudioUnit {operation} failed: {status}.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioComponentDescription
    {
        public uint ComponentType;
        public uint ComponentSubType;
        public uint ComponentManufacturer;
        public uint ComponentFlags;
        public uint ComponentFlagsMask;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioStreamBasicDescription
    {
        public double SampleRate;
        public uint FormatId;
        public uint FormatFlags;
        public uint BytesPerPacket;
        public uint FramesPerPacket;
        public uint BytesPerFrame;
        public uint ChannelsPerFrame;
        public uint BitsPerChannel;
        public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AURenderCallbackStruct
    {
        public delegate* unmanaged<nint, nint, nint, uint, uint, nint, int> Procedure;
        public nint UserData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioBufferList
    {
        public uint NumberBuffers;
        public AudioBuffer Buffer0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioBuffer
    {
        public uint NumberChannels;
        public uint DataByteSize;
        public nint Data;
    }

    [LibraryImport(Au)] private static partial nint AudioComponentFindNext(nint previous, AudioComponentDescription* description);
    [LibraryImport(Au)] private static partial int AudioComponentInstanceNew(nint component, out nint instance);
    [LibraryImport(Au)] private static partial int AudioComponentInstanceDispose(nint instance);
    [LibraryImport(Au)] private static partial int AudioUnitInitialize(nint unit);
    [LibraryImport(Au)] private static partial int AudioUnitUninitialize(nint unit);
    [LibraryImport(Au)] private static partial int AudioOutputUnitStart(nint unit);
    [LibraryImport(Au)] private static partial int AudioOutputUnitStop(nint unit);
    [LibraryImport(Au)] private static partial int AudioUnitSetProperty(nint unit, uint propertyId, uint scope, uint element, void* data, uint size);
    [LibraryImport(Au)] private static partial int AudioUnitSetParameter(nint unit, uint parameterId, uint scope, uint element, float value, uint bufferOffsetInFrames);

}