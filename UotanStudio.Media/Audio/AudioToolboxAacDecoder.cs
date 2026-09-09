using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace UotanStudio.Media.Audio;

/// <summary>
/// macOS AudioToolbox AAC decoder producing 16-bit interleaved PCM via AudioConverter.
/// <para>macOS AudioToolbox AAC 解码器：经 AudioConverter 输出 16 位交错 PCM。</para>
/// </summary>
/// <remarks>
/// <para>
/// The AAC AudioSpecificConfig from scrcpy's codec-config packet becomes the
/// converter magic cookie; packets are fed one at a time through
/// AudioConverterFillComplexBuffer. All calls use the creating thread.
/// </para>
/// <para>
/// scrcpy 配置包中的 AAC AudioSpecificConfig 作为转换器 magic cookie；压缩包
/// 逐个经 AudioConverterFillComplexBuffer 输入。所有调用使用创建线程。
/// </para>
/// </remarks>
[SupportedOSPlatform("macos")]
public sealed unsafe partial class AudioToolboxAacDecoder : IAudioDecoder
{
    private const uint FormatAac = 0x61616320;   // kAudioFormatMPEG4AAC ('aac ')
    private const uint FormatPcm = 0x6C70636D;   // kAudioFormatLinearPCM ('lpcm')
    private const uint FlagsPackedSignedInt = 0x8 | 0x4; // packed | signed integer
    private const uint PropertyMagicCookie = 0x6D616769; // kAudioConverterDecompressionMagicCookie ('magi')

    // One-packet input callback error; keeps the fill call failing loudly.
    // 单包输入回调的错误码；让填充调用明确失败。
    private const int InputCallbackError = 1;

    private nint _converter;
    private GCHandle _self;
    private Action<DecodedAudioFrame>? _callback;
    private nint _packetData, _packetDescription;
    private int _packetBytes;
    private bool _packetConsumed;
    private int _sampleRate, _channels;
    private int _thread = Environment.CurrentManagedThreadId;
    private bool _disposed;
    private bool _inCallback;

    /// <inheritdoc />
    public bool IsInitialized => _converter != 0;

    /// <inheritdoc />
    public int SampleRate => _sampleRate;

    /// <inheritdoc />
    public int Channels => _channels;

    /// <inheritdoc />
    public int BitsPerSample => 16;

    /// <inheritdoc />
    public void Initialize(ReadOnlySpan<byte> codecConfig, Action<DecodedAudioFrame> frameCallback)
    {
        if (_inCallback) throw new InvalidOperationException("Decoder callbacks cannot reenter the decoder.");
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(frameCallback);
        if (codecConfig.Length is 0 or > 64) throw new InvalidDataException("AAC AudioSpecificConfig is required (2-64 bytes).");
        if (_thread != Environment.CurrentManagedThreadId) throw new InvalidOperationException("The decoder must remain on its owning thread.");
        Release();

        // Parse the AudioSpecificConfig: object type, sampling frequency, channels.
        // 解析 AudioSpecificConfig：对象类型、采样频率、通道配置。
        (int rate, int channels) = AudioFormatLimits.ParseAacConfig(codecConfig);
        var source = new AudioStreamBasicDescription
        {
            SampleRate = rate,
            FormatId = FormatAac,
            FormatFlags = 0,
            BytesPerPacket = 0, // Variable-rate packetized AAC. 变码率封包 AAC。
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
            // Locals live on the stack; take their addresses directly (no fixed needed).
            // 局部结构体位于栈上，直接取地址（无需 fixed）。
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
    }

    /// <inheritdoc />
    public void Decode(ReadOnlySpan<byte> packet, long timestampTicks)
    {
        CheckReady();
        if (packet.IsEmpty || packet.Length > 1024 * 1024) throw new InvalidDataException("Invalid AAC packet.");
        int bufferBytes = 1024 * 2 * _channels;
        byte[]? buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(bufferBytes);
        try
        {
            fixed (byte* input = packet, outputData = buffer)
            {
                var description = new AudioStreamPacketDescription { DataByteSize = (uint)packet.Length, Frames = 1024 };
                _packetData = (nint)input;
                _packetDescription = (nint)(&description);
                _packetBytes = packet.Length;
                _packetConsumed = false;
                uint packets = 1024; // PCM packets contain one sample frame, not one AAC access unit.
                var output = new AudioBufferList
                {
                    NumberBuffers = 1,
                    Buffer0 = new AudioBuffer { NumberChannels = (uint)_channels, DataByteSize = (uint)bufferBytes, Data = (nint)outputData },
                };
                int status = AudioConverterFillComplexBuffer(_converter, &InputProc, GCHandle.ToIntPtr(_self), ref packets, &output, null);
                if (status != 0 && status != InputCallbackError) Check(status, "decode packet");
                if (packets == 0) return;
                int bytes = checked((int)output.Buffer0.DataByteSize);
                if (bytes > bufferBytes || bytes != packets * _channels * 2) throw new InvalidDataException("Invalid AudioConverter PCM output.");
                using var frame = new DecodedAudioFrame(buffer, bytes, (int)packets, _channels, _sampleRate, 16, timestampTicks);
                buffer = null; // Ownership passed to frame; never return this array twice.
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
    /// <inheritdoc />
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
        if (_thread != Environment.CurrentManagedThreadId) throw new InvalidOperationException("The decoder must remain on its owning thread.");
    }

    private void Release()
    {
        if (_converter != 0) { AudioConverterDispose(_converter); _converter = 0; }
        if (_self.IsAllocated) { _self.Free(); }
        _callback = null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        if (_inCallback || _thread != Environment.CurrentManagedThreadId) throw new InvalidOperationException("Dispose must run on the owner thread outside callbacks.");
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