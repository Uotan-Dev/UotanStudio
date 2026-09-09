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
    private byte[]? _pendingPacket;
    private int _sampleRate, _channels;
    private int _thread = Environment.CurrentManagedThreadId;
    private bool _disposed;

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
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(frameCallback);
        if (codecConfig.Length is 0 or > 64) throw new InvalidDataException("AAC AudioSpecificConfig is required (2-4 bytes).");
        if (_thread != Environment.CurrentManagedThreadId) throw new InvalidOperationException("The decoder must remain on its owning thread.");
        Release();

        // Parse the AudioSpecificConfig: object type, sampling frequency, channels.
        // 解析 AudioSpecificConfig：对象类型、采样频率、通道配置。
        (int rate, int channels) = ParseConfig(codecConfig);
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
        _pendingPacket = packet.ToArray();
        try
        {
            // One AAC packet carries 1024 frames per channel; request exactly one packet.
            // 一个 AAC 包承载每通道 1024 帧；每次请求一个包。
            uint packets = 1;
            int bufferBytes = 1024 * 4 * _channels;
            byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(bufferBytes);
            try
            {
                var output = new AudioBufferList
                {
                    NumberBuffers = 1,
                    Buffer0 = new AudioBuffer { NumberChannels = (uint)_channels, DataByteSize = (uint)bufferBytes, Data = 0 },
                };
                AudioBufferList* list = &output;
                fixed (byte* data = buffer)
                {
                    output.Buffer0.Data = (nint)data;
                    Check(AudioConverterFillComplexBuffer(_converter, &InputProc, GCHandle.ToIntPtr(_self), ref packets, list, null), "decode packet");
                    int bytes = checked((int)output.Buffer0.DataByteSize);
                    int blockAlign = _channels * 2;
                    if (bytes == 0 || bytes % blockAlign != 0) throw new InvalidDataException("Truncated PCM output from AudioConverter.");
                    using var frame = new DecodedAudioFrame(buffer, bytes, bytes / blockAlign, _channels, _sampleRate, 16, timestampTicks);
                    _callback!(frame);
                }
            }
            finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer); }
        }
        finally { _pendingPacket = null; }
    }

    /// <inheritdoc />
    public void Reset()
    {
        CheckReady();
        // Stateless per-packet conversion; nothing to flush between packets.
        // 逐包无状态转换；包间无需冲刷。
    }

    [UnmanagedCallersOnly]
    private static int InputProc(nint converter, nint packetCount, nint bufferList, nint packetDescriptions, nint userData)
    {
        // The instance context rides through the GCHandle supplied as userData.
        // 实例上下文经作为 userData 传入的 GCHandle 携带。
        var decoder = GCHandle.FromIntPtr(userData).Target as AudioToolboxAacDecoder;
        if (decoder?._pendingPacket is not { } packet) return InputCallbackError;
        var list = (AudioBufferList*)bufferList;
        uint bytes = (uint)packet.Length;
        if (list->Buffer0.DataByteSize < (int)bytes) return InputCallbackError;
        fixed (byte* source = packet)
        {
            Buffer.MemoryCopy(source, (void*)list->Buffer0.Data, bytes, bytes);
        }
        list->Buffer0.DataByteSize = (uint)bytes;
        if (packetDescriptions != 0)
        {
            var description = (AudioStreamPacketDescription*)packetDescriptions;
            *description = new AudioStreamPacketDescription { StartOffset = 0, DataByteSize = (int)bytes, Frames = 1024 };
        }
        return 0;
    }

    private static (int Rate, int Channels) ParseConfig(ReadOnlySpan<byte> config)
    {
        // Frequency table from ISO 14496-3 (samplingFrequencyIndex).
        // ISO 14496-3 采样频率表（samplingFrequencyIndex）。
        int[] rates = [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350];
        int objectType = (config[0] >> 3) & 0x1F;
        int index = ((config[0] & 0x07) << 1) | (config[1] >> 7);
        int rate = index == 15
            ? ((config[1] & 0x7F) << 17) | (config[2] << 9) | (config[3] << 1) | (config[4] >> 7)
            : rates[index];
        int channels = (config[1] >> 3) & 0x0F;
        if (objectType is < 1 or > 4 || channels is < 1 or > 8 || rate < 8000 || rate > 192000)
            throw new InvalidDataException($"Unsupported AAC config: object {objectType}, {channels}ch, {rate}Hz.");
        return (rate, channels);
    }

    private void CheckReady()
    {
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
        public int DataByteSize;
        public uint Frames;
    }

    private const string At = "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";

    [LibraryImport(At)] private static partial int AudioConverterNew(AudioStreamBasicDescription* source, AudioStreamBasicDescription* target, out nint converter);
    [LibraryImport(At)] private static partial int AudioConverterDispose(nint converter);
    [LibraryImport(At)] private static partial int AudioConverterSetProperty(nint converter, uint propertyId, uint size, void* data);
    [LibraryImport(At)] private static partial int AudioConverterFillComplexBuffer(nint converter, delegate* unmanaged<nint, nint, nint, nint, nint, int> inputProc, nint userData, ref uint packetCount, AudioBufferList* data, AudioStreamPacketDescription* packetDescriptions);
}
