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
    private const uint ComponentTypeOutput = 0x61756F75; // kAudioUnitType_Output ('auou')
    private const uint ComponentSubTypeDefault = 0x64656661; // kAudioUnitSubType_DefaultOutputUnit ('defa')
    private const uint ComponentManufacturer = 0x61706C63; // kAudioUnitManufacturer_Apple ('aplc')
    private const uint ScopeOutput = 0;
    private const uint ScopeInput = 1;                    // input side of the output unit = data we feed
    private const uint ElementOutput = 0;
    private const uint PropertyStreamFormat = 8;          // kAudioUnitProperty_StreamFormat
    private const uint PropertyRenderCallback = 11;       // kAudioUnitProperty_SetRenderCallback
    private const uint FormatIdPcm = 0x6C70636D;          // 'lpcm'
    private nint _unit;
    private readonly object _stageLock = new();
    private byte[] _staged = [];
    private int _stagedFrames;
    private int _stagedRead;
    private int _channels = 2, _sampleRate;
    private int _thread = Environment.CurrentManagedThreadId;
    private float _volumeValue = 1f;
    private GCHandle _self;
    private bool _disposed;

    /// <inheritdoc />
    public bool IsOpen => _unit != 0;

    /// <inheritdoc />
    public int Channels => _channels;

    /// <inheritdoc />
    public int SampleRate => _sampleRate;

    /// <inheritdoc />
    public float Volume
    {
        get => _volumeValue;
        set
        {
            if (!float.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
            _volumeValue = Math.Clamp(value, 0f, 1f);

        }
    }

    /// <inheritdoc />
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
        // AudioComponentFindNext returns the component handle (null = not found),
        // not a status code.
        // AudioComponentFindNext 返回组件句柄（null 表示未找到），而非状态码。
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
                FormatFlags = 0x9, // interleaved float32
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
            Check(AudioUnitInitialize(_unit), "AudioUnitInitialize");
            Check(AudioOutputUnitStart(_unit), "AudioOutputUnitStart");
        }
        catch { Close(); throw; }
        _channels = channels;
        _sampleRate = sampleRate;
    }

    /// <inheritdoc />
    public void Write(ReadOnlySpan<byte> pcm)
    {
        CheckOpen();
        int blockAlign = _channels * 2;
        if (pcm.Length % blockAlign != 0) throw new ArgumentException("PCM length is not frame aligned.", nameof(pcm));
        if (pcm.IsEmpty) return;

        // Convert int16 → interleaved float32 and stage it; the render callback
        // consumes on the HAL thread. Volume is applied by the unit itself.
        // int16 → 交错 float32 转换后暂存；渲染回调在 HAL 线程消费。音量由单元自身应用。
        if (pcm.Length > AudioFormatLimits.MaximumPacketBytes) throw new ArgumentException("Excessive PCM packet.", nameof(pcm));
        const int maxStagedFrames = 19200;
        int frames = pcm.Length / blockAlign;
        if (frames > maxStagedFrames)
        {
            pcm = pcm[^checked(maxStagedFrames * blockAlign)..];
            frames = maxStagedFrames;
        }
        byte[] staged = new byte[frames * _channels * 4];
        for (int frame = 0; frame < frames; frame++)
        {
            for (int channel = 0; channel < _channels; channel++)
            {
                short sample = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(
                    pcm.Slice((frame * _channels + channel) * 2));
                System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(
                    staged.AsSpan((frame * _channels + channel) * 4), sample / 32768f * _volumeValue);
            }
        }
        lock (_stageLock)
        {
            // Bounded staging: drop stale data if the producer runs far ahead.
            // 有界暂存：生产者超前过多时丢弃旧数据。

            int pendingFrames = _stagedFrames - _stagedRead;
            if (pendingFrames + frames > maxStagedFrames)
            {
                int drop = pendingFrames + frames - maxStagedFrames;
                _stagedRead += drop;
            }
            int keep = _stagedFrames - _stagedRead;
            byte[] merged = new byte[(keep + frames) * _channels * 4];
            if (keep > 0) _staged.AsSpan(_stagedRead * _channels * 4, keep * _channels * 4).CopyTo(merged);
            staged.CopyTo(merged, keep * _channels * 4);
            _staged = merged;
            _stagedFrames = keep + frames;
            _stagedRead = 0;
        }
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
            destination.Clear();
            lock (sink._stageLock)
            {
                int availableBytes = (sink._stagedFrames - sink._stagedRead) * sink._channels * 4;
                int copyBytes = Math.Min(destination.Length, availableBytes);
                sink._staged.AsSpan(sink._stagedRead * sink._channels * 4, copyBytes).CopyTo(destination);
                sink._stagedRead += copyBytes / (sink._channels * 4);
            }
            return 0;
        }
        catch { return -50; } // Never unwind managed exceptions across the native audio callback.
    }
    private void CheckOpen()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsOpen) throw new InvalidOperationException("Sink is not open.");
        if (_thread != Environment.CurrentManagedThreadId) throw new InvalidOperationException("The sink must remain on its owning thread.");
    }

    /// <inheritdoc />
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
        lock (_stageLock) { _staged = []; _stagedFrames = _stagedRead = 0; }
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