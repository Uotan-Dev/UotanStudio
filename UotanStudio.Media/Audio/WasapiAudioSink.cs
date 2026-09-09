using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace UotanStudio.Media.Audio;

/// <summary>
/// Windows WASAPI shared-mode render sink. Accepts 16-bit interleaved PCM and
/// renders through the device mix format (float32 or int16), event-driven.
/// <para>Windows WASAPI 共享模式渲染输出端：接受 16 位交错 PCM，按设备混音格式
/// （float32 或 int16）以事件驱动方式渲染。</para>
/// </summary>
/// <remarks>
/// <para>
/// Calls the WASAPI unmanaged ABI directly (vtable slots per audioclient.h /
/// mmdeviceapi.h) so it stays NativeAOT compatible without COM marshalling.
/// The mix format returned by GetMixFormat is passed verbatim to Initialize.
/// </para>
/// <para>
/// 直接调用 WASAPI 非托管 ABI（vtable 槽位对照 audioclient.h / mmdeviceapi.h），
/// 无需 COM 封送，保持 NativeAOT 兼容。GetMixFormat 返回的混音格式原样传给
/// Initialize。
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed unsafe partial class WasapiAudioSink : IAudioSink
{
    private const string Combase = "combase.dll";
    private const uint ClsCtxAll = 23;                 // CLSCTX_ALL
    private const uint AudClntSharemodeShared = 0;     // AUDCLNT_SHAREMODE_SHARED
    private const uint EventCallbackFlag = 0x1;        // AUDCLNT_STREAMFLAGS_EVENTCALLBACK
    private const uint WaitTimeout = 2000;

    private static readonly Guid IidIUnknown = new("00000000-0000-0000-C000-000000000046");
    private static readonly Guid IidIAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    private static readonly Guid IidIAudioRenderClient = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
    private static readonly Guid IidISimpleAudioVolume = new("87CE5498-68D6-44E5-9215-6DA47EF883D8");
    private static readonly Guid ClsidDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");

    private nint _enumerator, _device, _client, _renderClient, _volume, _event;
    private int _channels, _inputChannels, _sampleRate, _frameBytes, _bufferFrames;
    private bool _mixIsFloat;
    private int _thread = Environment.CurrentManagedThreadId;
    private float _volumeValue = 1f;
    private bool _disposed;

    /// <inheritdoc />
    public bool IsOpen => _client != 0 && _renderClient != 0;

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
            _volumeValue = Math.Clamp(value, 0f, 1f);
            // ISimpleAudioVolume::SetMasterVolume (slot 3); failures are non-fatal.
            // ISimpleAudioVolume::SetMasterVolume（槽 3）；失败不视为致命。
            if (_volume != 0)
                _ = ((delegate* unmanaged[Stdcall]<nint, float, nint, int>)Slot(_volume, 3))(_volume, _volumeValue, 0);
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
        _ = CoInitializeEx(0, 0); // RPC_E_CHANGED_MODE tolerated; the process owns another apartment.

        try
        {
            // Field addresses of a movable object require pinning; use locals instead.
            // 可移动对象的字段取址需要固定；改用局部变量。
            nint enumerator = 0, device = 0, client = 0, renderClient = 0, volume = 0;
            fixed (Guid* c = &ClsidDeviceEnumerator, u = &IidIUnknown)
            {
                Check(CoCreateInstance(c, 0, ClsCtxAll, u, &enumerator), "create device enumerator");
            }
            // IMMDeviceEnumerator::GetDefaultAudioEndpoint (slot 4): eRender=0, eConsole=1.
            // IMMDeviceEnumerator::GetDefaultAudioEndpoint（槽 4）：eRender=0, eConsole=1。
            Check(((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, int>)Slot(enumerator, 4))
                (enumerator, 0, 1, &device), "GetDefaultAudioEndpoint");
            // IMMDevice::Activate (slot 3).
            // IMMDevice::Activate（槽 3）。
            fixed (Guid* ac = &IidIAudioClient)
            {
                Check(((delegate* unmanaged[Stdcall]<nint, Guid*, uint, nint, nint*, int>)Slot(device, 3))
                    (device, ac, ClsCtxAll, 0, &client), "activate audio client");
            }
            // IAudioClient::GetMixFormat (slot 8); pass it verbatim to Initialize so
            // WAVEFORMATEXTENSIBLE layouts keep their SubFormat.
            // IAudioClient::GetMixFormat（槽 8）；原样传给 Initialize，保留
            // WAVEFORMATEXTENSIBLE 的 SubFormat。
            nint mixPtr = 0;
            try
            {
                Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(client, 8))(client, &mixPtr), "GetMixFormat");
                var mix = (WaveFormatEx*)mixPtr;
                _sampleRate = checked((int)mix->SamplesPerSecond);
                int mixChannels = mix->Channels;
                if (_sampleRate is < 8000 or > 192000) throw new NotSupportedException($"Unsupported device mix rate {_sampleRate}.");
                if (mixChannels != channels)
                    throw new NotSupportedException($"Device mix layout ({mixChannels}ch) does not match the decoded layout ({channels}ch).");
                _inputChannels = channels;
                _channels = mixChannels;
                _mixIsFloat = mix->BitsPerSample == 32;
                _frameBytes = checked(mixChannels * (mix->BitsPerSample / 8));

                uint bufferFrames;
                // IAudioClient::Initialize (slot 3): shared + event callback, default periods.
                // IAudioClient::Initialize（槽 3）：共享 + 事件回调，默认周期。
                Check(((delegate* unmanaged[Stdcall]<nint, WaveFormatEx*, uint, uint, long, long, nint, nint, uint*, int>)Slot(client, 3))
                    (client, mix, AudClntSharemodeShared, EventCallbackFlag, 0, 0, 0, 0, &bufferFrames), "Initialize");
                _bufferFrames = checked((int)bufferFrames);
            }
            finally { if (mixPtr != 0) VtableRelease(mixPtr); }

            _event = CreateEventW(0, 0, 0, 0);
            if (_event == 0) throw new InvalidOperationException("Cannot create the WASAPI event.");
            // IAudioClient::SetEventHandle (slot 13).
            // IAudioClient::SetEventHandle（槽 13）。
            Check(((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(client, 13))(client, _event), "SetEventHandle");
            // IAudioClient::GetService (slot 14).
            // IAudioClient::GetService（槽 14）。
            fixed (Guid* rc = &IidIAudioRenderClient, sv = &IidISimpleAudioVolume)
            {
                Check(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(client, 14))
                    (client, rc, &renderClient), "get render client");
                _ = ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(client, 14))(client, sv, &volume);
            }
            // IAudioClient::Start (slot 10).
            // IAudioClient::Start（槽 10）。
            Check(((delegate* unmanaged[Stdcall]<nint, int>)Slot(client, 10))(client), "Start");
            _enumerator = enumerator;
            _device = device;
            _client = client;
            _renderClient = renderClient;
            _volume = volume;
        }
        catch { Close(); throw; }
    }

    /// <inheritdoc />
    public void Write(ReadOnlySpan<byte> pcm)
    {
        CheckOpen();
        int blockAlign = _inputChannels * 2; // 16-bit input block. 16 位输入块。
        if (pcm.Length % blockAlign != 0) throw new ArgumentException("PCM length is not frame aligned.", nameof(pcm));

        int offset = 0;
        while (offset < pcm.Length)
        {
            _ = WaitForSingleObject(_event, WaitTimeout);
            // IAudioClient::GetCurrentPadding (slot 6).
            // IAudioClient::GetCurrentPadding（槽 6）。
            uint padding;
            Check(((delegate* unmanaged[Stdcall]<nint, uint*, int>)Slot(_client, 6))(_client, &padding), "GetCurrentPadding");
            int available = _bufferFrames - checked((int)padding);
            if (available <= 0) continue;
            int frames = Math.Min(available, (pcm.Length - offset) / blockAlign);
            if (frames <= 0) return;
            // IAudioRenderClient::GetBuffer (slot 3).
            // IAudioRenderClient::GetBuffer（槽 3）。
            nint data;
            Check(((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(_renderClient, 3))
                (_renderClient, (uint)frames, &data), "GetBuffer");
            try
            {
                int bytes = frames * _frameBytes;
                WriteConverted(data, pcm.Slice(offset, frames * blockAlign), bytes);
                // IAudioRenderClient::ReleaseBuffer (slot 4).
                // IAudioRenderClient::ReleaseBuffer（槽 4）。
                Check(((delegate* unmanaged[Stdcall]<nint, uint, uint, int>)Slot(_renderClient, 4))
                    (_renderClient, (uint)frames, 0), "ReleaseBuffer");
                offset += frames * blockAlign;
            }
            catch
            {
                // Release an empty (silent) buffer so the device never stalls.
                // 释放空（静音）缓冲区，避免设备停顿。
                _ = ((delegate* unmanaged[Stdcall]<nint, uint, uint, int>)Slot(_renderClient, 4))(_renderClient, 0, 2);
                throw;
            }
        }
    }

    private void WriteConverted(nint target, ReadOnlySpan<byte> input, int outputBytes)
    {
        // The mix format was fixed at Open: int16 or float32 with the same channel
        // count; mono input duplicates channel 0 into both output channels.
        // 混音格式在 Open 时固定：int16 或 float32、通道数一致；单声道输入把
        // 通道 0 复制到全部输出通道。
        Span<byte> output = new((void*)target, outputBytes);
        int inputFrames = input.Length / (_inputChannels * 2);
        for (int frame = 0; frame < inputFrames; frame++)
        {
            for (int channel = 0; channel < _channels; channel++)
            {
                int sourceChannel = _inputChannels == 1 ? 0 : channel;
                short sample = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(
                    input.Slice((frame * _inputChannels + sourceChannel) * 2));
                int outOffset = frame * _frameBytes + channel * (_mixIsFloat ? 4 : 2);
                if (_mixIsFloat)
                {
                    System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(
                        output.Slice(outOffset), sample / 32768f);
                }
                else
                {
                    System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(
                        output.Slice(outOffset), sample);
                }
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Close();
    }

    private void CheckOpen()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsOpen) throw new InvalidOperationException("Sink is not open.");
        if (_thread != Environment.CurrentManagedThreadId) throw new InvalidOperationException("The sink must remain on its owning thread.");
    }

    private void Close()
    {
        if (_client != 0)
        {
            _ = ((delegate* unmanaged[Stdcall]<nint, int>)Slot(_client, 11))(_client); // Stop
        }
        if (_volume != 0) { VtableRelease(_volume); _volume = 0; }
        if (_renderClient != 0) { VtableRelease(_renderClient); _renderClient = 0; }
        if (_client != 0) { VtableRelease(_client); _client = 0; }
        if (_device != 0) { VtableRelease(_device); _device = 0; }
        if (_enumerator != 0) { VtableRelease(_enumerator); _enumerator = 0; }
        if (_event != 0) { _ = CloseHandle(_event); _event = 0; }
    }

    // Reads slot N of the COM vtable: *(void***)target)[N] as a nint.
    // 读取 COM vtable 的第 N 槽：(*(void***)target)[N]，以 nint 返回。
    private static nint Slot(nint target, int slot) => (*(nint**)target)[slot];

    private static void VtableRelease(nint target) => _ = ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(target, 2))(target);

    private static void Check(int hr, string operation)
    {
        if (hr < 0) throw new InvalidOperationException($"WASAPI {operation} failed: 0x{hr:X8}.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveFormatEx
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSecond;
        public uint AverageBytesPerSecond;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort ExtraSize;
    }

    [LibraryImport(Combase)]
    private static partial int CoInitializeEx(nint reserved, uint model);

    [LibraryImport(Combase)]
    private static partial int CoCreateInstance(Guid* clsid, nint outer, uint ctx, Guid* iid, nint* instance);

    [LibraryImport("kernel32.dll")]
    private static partial nint CreateEventW(nint attributes, uint manualReset, uint initialState, nint name);

    [LibraryImport("kernel32.dll")]
    private static partial int CloseHandle(nint handle);

    [LibraryImport("kernel32.dll")]
    private static partial uint WaitForSingleObject(nint handle, uint milliseconds);
}
