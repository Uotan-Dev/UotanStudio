using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace UotanStudio.Media.Audio;

/// <summary>
/// macOS AudioToolbox AAC decoder producing 16-bit interleaved PCM via AudioConverter.
/// <para>macOS AudioToolbox AAC 解码器：经 AudioConverter 输出 16 位交错 PCM。</para>
/// </summary>
/// <remarks>
/// The AAC AudioSpecificConfig from scrcpy's codec-config packet becomes the
/// converter magic cookie; packets are fed one at a time through
/// AudioConverterFillComplexBuffer. All calls use the creating thread.
/// <para>scrcpy 配置包中的 AAC AudioSpecificConfig 作为转换器 magic cookie；压缩包
/// 逐个经 AudioConverterFillComplexBuffer 输入。所有调用使用创建线程。</para>
/// </remarks>
[SupportedOSPlatform("macos")]
public sealed unsafe partial class AudioToolboxAacDecoder : IAudioDecoder
{
    private const uint FormatAac = 0x61616320;
    private const uint FormatPcm = 0x6C70636D;
    private const uint FlagsPackedSignedInt = 0x8 | 0x4;
    private const uint PropertyMagicCookie = 0x61626364;

    private const int InputCallbackError = 1;

    private nint _converter;
    private GCHandle _self;
    private Action<DecodedAudioFrame>? _callback;
    private nint _packetData, _packetDescription;
    private int _packetBytes;
    private bool _packetConsumed;
    private int _sampleRate, _channels;
    private int _thread;
    private bool _disposed;
    private bool _inCallback;

    /// <summary>
    /// Whether the native AudioConverter has been created.
    /// <para>原生 AudioConverter 是否已创建。</para>
    /// </summary>
    /// <value><see langword="true"/> while the converter handle is held; otherwise <see langword="false"/>. <para>持有转换器句柄期间为 <see langword="true"/>，否则为 <see langword="false"/>。</para></value>
    public bool IsInitialized => _converter != 0;

    /// <summary>
    /// The output sample rate parsed from the AudioSpecificConfig.
    /// <para>由 AudioSpecificConfig 解析出的输出采样率。</para>
    /// </summary>
    /// <value>Sample rate in hertz; valid once initialized. <para>以赫兹计的采样率；初始化后有效。</para></value>
    public int SampleRate => _sampleRate;

    /// <summary>
    /// The output channel count parsed from the AudioSpecificConfig.
    /// <para>由 AudioSpecificConfig 解析出的输出通道数。</para>
    /// </summary>
    /// <value>Number of interleaved channels; valid once initialized. <para>交错通道数量；初始化后有效。</para></value>
    public int Channels => _channels;

    /// <summary>
    /// The fixed output width of the 16-bit signed PCM this decoder emits.
    /// <para>本解码器输出的 16 位有符号 PCM 的固定位宽。</para>
    /// </summary>
    /// <value>Always <c>16</c>. <para>始终为 <c>16</c>。</para></value>
    public int BitsPerSample => 16;

    /// <summary>Creates the AudioConverter from an AAC AudioSpecificConfig and installs the per-frame callback.
    /// <para>依据 AAC AudioSpecificConfig 创建 AudioConverter，并安装逐帧回调。</para></summary>
    /// <param name="codecConfig">The AudioSpecificConfig bytes; must be 2 to 64 bytes long and is used as the converter magic cookie.
    /// <para>AudioSpecificConfig 字节；长度须为 2 至 64 字节，用作转换器的 magic cookie。</para></param>
    /// <param name="frameCallback">Invoked on the calling thread for each decoded frame; the callback owns and must dispose the frame.
    /// <para>对每一解码帧在调用线程上触发；回调接管该帧并必须将其释放。</para></param>
    /// <exception cref="InvalidDataException"><paramref name="codecConfig"/> is empty or longer than 64 bytes, or the converter call fails.
    /// <para><paramref name="codecConfig"/> 为空或超过 64 字节，或转换器调用失败。</para></exception>
    /// <exception cref="ArgumentNullException"><paramref name="frameCallback"/> is <see langword="null"/>.
    /// <para><paramref name="frameCallback"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ObjectDisposedException">The decoder has been disposed.
    /// <para>解码器已被释放。</para></exception>
    /// <exception cref="InvalidOperationException">The call reenters from a callback or runs on a thread other than the owning thread.
    /// <para>该调用从回调中重入，或在非属主线程上运行。</para></exception>
    public void Initialize(ReadOnlySpan<byte> codecConfig, Action<DecodedAudioFrame> frameCallback)
    {
        if (_inCallback) throw new InvalidOperationException("Decoder callbacks cannot reenter the decoder.");
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(frameCallback);
        if (codecConfig.Length is 0 or > 64) throw new InvalidDataException("AAC AudioSpecificConfig is required (2-64 bytes).");
        if (_thread != 0 && _thread != Environment.CurrentManagedThreadId) throw new InvalidOperationException("The decoder must remain on its owning thread.");
        Release();
        (var rate, var channels) = AudioFormatLimits.ParseAacConfig(codecConfig);
        var source = new AudioStreamBasicDescription
        {
            SampleRate = rate,
            FormatId = FormatAac,
            FormatFlags = 0,
            BytesPerPacket = 0,
            FramesPerPacket = 1024,
            BytesPerFrame = 0,
            ChannelsPerFrame = (uint)channels,
            BitsPerChannel = 0,
        };
        var target = new AudioStreamBasicDescription
        {
            SampleRate = rate,
            FormatId = FormatPcm,
            FormatFlags = FlagsPackedSignedInt,
            BytesPerPacket = 2 * (uint)channels,
            FramesPerPacket = 1,
            BytesPerFrame = 2 * (uint)channels,
            ChannelsPerFrame = (uint)channels,
            BitsPerChannel = 16,
        };
        _self = GCHandle.Alloc(this);
        try
        {
            AudioStreamBasicDescription* s = &source;
            AudioStreamBasicDescription* t = &target;
            Check(AudioConverterNew(s, t, out _converter), "create converter");
            fixed (byte* cookie = codecConfig)
            {
                Check(AudioConverterSetProperty(_converter, PropertyMagicCookie,
                    (uint)codecConfig.Length, cookie), "set magic cookie");
            }
        }
        catch { Release(); throw; }
        _sampleRate = rate;
        _channels = channels;
        _callback = frameCallback;
        _thread = Environment.CurrentManagedThreadId;
    }

    /// <summary>Decodes one AAC packet through the converter and emits interleaved 16-bit PCM via the callback on the owning thread.
    /// <para>经转换器解码一个 AAC 包，并在属主线程上通过回调输出交错 16 位 PCM。</para></summary>
    /// <param name="packet">A single AAC access unit; must be non-empty and no larger than 1 MiB. The bytes are read only for the duration of the call.
    /// <para>单个 AAC 访问单元；必须非空且不超过 1 MiB。这些字节仅在调用期间被读取。</para></param>
    /// <param name="timestampTicks">Presentation timestamp stamped onto every frame produced from this packet.
    /// <para>盖在该包产生的每一帧上的显示时间戳。</para></param>
    /// <exception cref="InvalidDataException">The packet is empty or exceeds 1 MiB, or the converter returns a PCM size inconsistent with the sample layout.
    /// <para>包为空或超过 1 MiB，或转换器返回的 PCM 大小与采样布局不一致。</para></exception>
    /// <exception cref="InvalidOperationException">The decoder is not initialized, the call reenters from a callback, or it runs on a thread other than the owning thread.
    /// <para>解码器未初始化、该调用从回调中重入，或在非属主线程上运行。</para></exception>
    /// <exception cref="ObjectDisposedException">The decoder has been disposed.
    /// <para>解码器已被释放。</para></exception>
    public void Decode(ReadOnlySpan<byte> packet, long timestampTicks)
    {
        CheckReady();
        if (packet.IsEmpty || packet.Length > 1024 * 1024) throw new InvalidDataException("Invalid AAC packet.");
        var bufferBytes = 1024 * 2 * _channels;
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(bufferBytes);
        try
        {
            fixed (byte* input = packet, outputData = buffer)
            {
                var description = new AudioStreamPacketDescription { DataByteSize = (uint)packet.Length, Frames = 1024 };
                _packetData = (nint)input;
                _packetDescription = (nint)(&description);
                _packetBytes = packet.Length;
                _packetConsumed = false;
                uint packets = 1024;
                var output = new AudioBufferList
                {
                    NumberBuffers = 1,
                    Buffer0 = new AudioBuffer { NumberChannels = (uint)_channels, DataByteSize = (uint)bufferBytes, Data = (nint)outputData },
                };
                var status = AudioConverterFillComplexBuffer(_converter, &InputProc, GCHandle.ToIntPtr(_self), ref packets, &output, null);
                if (status != 0 && status != InputCallbackError) Check(status, "decode packet");
                if (packets == 0) return;
                var bytes = checked((int)output.Buffer0.DataByteSize);
                if (bytes > bufferBytes || bytes != packets * _channels * 2) throw new InvalidDataException("Invalid AudioConverter PCM output.");
                using var frame = new DecodedAudioFrame(buffer, bytes, (int)packets, _channels, _sampleRate, 16, timestampTicks);
                buffer = null;
                _inCallback = true;
                try { _callback!(frame); } finally { _inCallback = false; }
            }
        }
        finally
        {
            _packetData = _packetDescription = 0;
            if (buffer is not null) System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }
    /// <summary>
    /// Resets the converter for a new stream, keeping the decoder initialized and its owning thread unchanged.
    /// <para>重置转换器以开始新码流，解码器保持已初始化且属主线程不变。</para>
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The decoder is not initialized, the call reenters from a callback, it runs on a thread other than the owning thread, or the reset call fails.
    /// <para>解码器未初始化、该调用从回调中重入、在非属主线程上运行，或重置调用失败。</para>
    /// </exception>
    /// <exception cref="ObjectDisposedException">
    /// The decoder has been disposed.
    /// <para>解码器已被释放。</para>
    /// </exception>
    public void Reset()
    {
        CheckReady();
        Check(AudioConverterReset(_converter), "reset converter");
    }

    [UnmanagedCallersOnly]
    private static int InputProc(nint converter, nint packetCount, nint bufferList, nint packetDescriptions, nint userData)
    {
        var decoder = (AudioToolboxAacDecoder?)GCHandle.FromIntPtr(userData).Target;
        if (decoder is null || decoder._packetConsumed || decoder._packetData == 0)
        {
            *(uint*)packetCount = 0;
            return InputCallbackError;
        }
        decoder._packetConsumed = true;
        *(uint*)packetCount = 1;
        var list = (AudioBufferList*)bufferList;
        list->NumberBuffers = 1;
        list->Buffer0 = new AudioBuffer
        {
            NumberChannels = (uint)decoder._channels,
            DataByteSize = (uint)decoder._packetBytes,
            Data = decoder._packetData,
        };
        if (packetDescriptions != 0) *(nint*)packetDescriptions = decoder._packetDescription;
        return 0;
    }
    private void CheckReady()
    {
        if (_inCallback) throw new InvalidOperationException("Decoder callbacks cannot reenter the decoder.");
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_converter == 0 || _callback is null) throw new InvalidOperationException("Audio decoder is not initialized.");
        if (_thread != 0 && _thread != Environment.CurrentManagedThreadId) throw new InvalidOperationException("The decoder must remain on its owning thread.");
    }

    private void Release()
    {
        if (_converter != 0) { AudioConverterDispose(_converter); _converter = 0; }
        if (_self.IsAllocated) { _self.Free(); }
        _callback = null;
    }

    /// <summary>
    /// Disposes the native AudioConverter and releases the self handle; repeated calls are no-ops once disposed.
    /// <para>释放原生 AudioConverter 并解除自引用句柄；已释放后再次调用不产生效果。</para>
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The call runs from within a callback or on a thread other than the owning thread.
    /// <para>该调用在回调内部触发，或在非属主线程上运行。</para>
    /// </exception>
    public void Dispose()
    {
        if (_disposed) return;
        if (_inCallback || _thread != 0 && _thread != Environment.CurrentManagedThreadId) throw new InvalidOperationException("Dispose must run on the owner thread outside callbacks.");
        _disposed = true;
        Release();
    }

    private static void Check(int status, string operation)
    {
        if (status != 0) throw new InvalidOperationException($"AudioToolbox {operation} failed: {status}.");
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
    private struct AudioBuffer
    {
        public uint NumberChannels;
        public uint DataByteSize;
        public nint Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioBufferList
    {
        public uint NumberBuffers;
        public AudioBuffer Buffer0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioStreamPacketDescription
    {
        public long StartOffset;
        public uint Frames;
        public uint DataByteSize;
    }

    private const string At = "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";

    [LibraryImport(At)] private static partial int AudioConverterNew(AudioStreamBasicDescription* source, AudioStreamBasicDescription* target, out nint converter);
    [LibraryImport(At)] private static partial int AudioConverterReset(nint converter);
    [LibraryImport(At)] private static partial int AudioConverterDispose(nint converter);
    [LibraryImport(At)] private static partial int AudioConverterSetProperty(nint converter, uint propertyId, uint size, void* data);
    [LibraryImport(At)] private static partial int AudioConverterFillComplexBuffer(nint converter, delegate* unmanaged<nint, nint, nint, nint, nint, int> inputProc, nint userData, ref uint packetCount, AudioBufferList* data, AudioStreamPacketDescription* packetDescriptions);
}