using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using UotanStudio.Media.Decoding.MediaFoundation;

namespace UotanStudio.Media.Audio;

/// <summary>Windows shared-mode PCM playback with system channel/rate conversion. Windows 共享模式 PCM 播放。</summary>
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
    /// <inheritdoc />
    public bool IsOpen => _render != 0;
    /// <inheritdoc />
    public int Channels { get; private set; }
    /// <inheritdoc />
    public int SampleRate { get; private set; }
    /// <inheritdoc />
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
    /// <inheritdoc />
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
            // AUTOCONVERTPCM | SRC_DEFAULT_QUALITY: WASAPI owns device-format conversion.
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
    /// <inheritdoc />
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
    /// <inheritdoc />
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