using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using UotanStudio.Media.Decoding.MediaFoundation;

namespace UotanStudio.Media.Audio;

/// <summary>
/// Windows Media Foundation AAC decoder MFT producing 16-bit interleaved PCM.
/// <para>Windows Media Foundation AAC 解码 MFT，输出 16 位交错 PCM。</para>
/// </summary>
/// <remarks>
/// <para>
/// Follows the same synchronous-MFT push model as the video decoder: feed one
/// AAC packet per <see cref="Decode"/> call and drain PCM output. The AAC
/// AudioSpecificConfig may arrive with the first packet (scrcpy sends it as a
/// codec-config packet); it is forwarded through MF_MT_USER_DATA when known.
/// </para>
/// <para>
/// 与视频解码器相同的同步 MFT 推送模型：每次 <see cref="Decode"/> 输入一个 AAC
/// 包并排空 PCM 输出。AAC AudioSpecificConfig 可随首个压缩包到达（scrcpy 以
/// 配置包发送）；已知时经 MF_MT_USER_DATA 转发。
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed unsafe class MediaFoundationAacDecoder : IAudioDecoder
{
    private static readonly object LifetimeLock = new();
    private static int _leases;

    private nint _transform;
    private Action<DecodedAudioFrame>? _callback;
    private int _sampleRate, _channels, _bitsPerSample;
    private int _ownerThread;
    private bool _comOwned, _mfOwned, _disposed;
    private byte[]? _codecConfig;
    private bool _inCallback;

    /// <inheritdoc />
    public bool IsInitialized => _callback is not null && !_disposed;

    /// <inheritdoc />
    public int SampleRate => _sampleRate;

    /// <inheritdoc />
    public int Channels => _channels;

    /// <inheritdoc />
    public int BitsPerSample => _bitsPerSample;

    /// <inheritdoc />
    public void Initialize(ReadOnlySpan<byte> codecConfig, Action<DecodedAudioFrame> frameCallback)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(frameCallback);
        if (codecConfig.Length > 64) throw new InvalidDataException("Excessive AAC codec configuration.");
        if (_ownerThread != 0) CheckThread();
        ReleaseTransform();
        if (!_mfOwned)
        {
            int hr = MfInterop.CoInitializeEx(0, 0);
            if (hr != unchecked((int)0x80010106)) Check(hr, "CoInitializeEx");
            _comOwned = hr >= 0;
            _ownerThread = Environment.CurrentManagedThreadId;
            try
            {
                lock (LifetimeLock)
                {
                    if (_leases == 0) Check(MfInterop.MFStartup(MfInterop.MfVersion, 0), "MFStartup");
                    _leases++;
                    _mfOwned = true;
                }
            }
            catch
            {
                if (_comOwned) MfInterop.CoUninitialize();
                _comOwned = false;
                _ownerThread = 0;
                throw;
            }
        }

        _codecConfig = codecConfig.IsEmpty ? null : codecConfig.ToArray();
        _callback = frameCallback;
    }

    /// <inheritdoc />
    public void Decode(ReadOnlySpan<byte> packet, long timestampTicks)
    {
        CheckReady();
        if (packet.IsEmpty || packet.Length > 16 * 1024 * 1024) throw new InvalidDataException("Invalid audio packet.");
        if (_transform == 0) InitializeTransform();
        nint sample = CreateSample(packet, timestampTicks);
        try
        {
            int hr = MfCom.Input(_transform, sample);
            if (hr == MfInterop.MfENotAccepting)
            {
                DrainOutput();
                hr = MfCom.Input(_transform, sample);
            }
            Check(hr, "audio ProcessInput");
            DrainOutput();
        }
        finally { MfCom.Release(sample); }
    }

    /// <inheritdoc />
    public void Reset()
    {
        CheckReady();
        if (_transform != 0)
        {
            Check(MfCom.Message(_transform, MfInterop.MftMessageFlush), "flush");
            Check(MfCom.Message(_transform, MfInterop.MftMessageStartOfStream), "restart stream");
        }
        ReleaseTransform(); // Re-negotiate on the next packet; AAC headers may change.
    }

    private void InitializeTransform()
    {
        Check(MfInterop.MFCreateMediaType(out nint input), "MFCreateMediaType");
        try
        {
            Check(MfCom.SetGuid(input, MfInterop.MtMajorType, MfInterop.MediaTypeAudio), "major type");
            Check(MfCom.SetGuid(input, MfInterop.MtSubtype, MfInterop.AudioFormatAac), "subtype");
            if (_codecConfig is { Length: > 0 } config)
            {
                var (rate, channels) = AudioFormatLimits.ParseAacConfig(config);
                Check(MfCom.SetUInt32(input, MfInterop.MtAudioSamplesPerSecond, (uint)rate), "AAC sample rate");
                Check(MfCom.SetUInt32(input, MfInterop.MtAudioNumChannels, (uint)channels), "AAC channels");
                Check(MfCom.SetUInt32(input, new Guid("bfbabe79-7434-4d1c-94f0-72a3b9e17188"), 0), "raw AAC payload");
                byte[] userData = new byte[12 + config.Length];
                userData[2] = 0xFE; // HEAACWAVEINFO profile-level indication: unspecified.
                config.CopyTo(userData, 12);
                Check(MfCom.SetBytes(input, MfInterop.MtUserData, userData), "codec private data");
            }
            var registration = new MftRegisterTypeInfo(MfInterop.MediaTypeAudio, MfInterop.AudioFormatAac);
            Check(MfInterop.MFTEnumEx(MfInterop.CategoryAudioDecoder, MfInterop.MftEnumFlags,
                registration, 0, out nint activates, out uint count), "MFTEnumEx");
            try
            {
                for (int i = 0; i < count; i++)
                {
                    nint activate = Marshal.ReadIntPtr(activates, checked(i * IntPtr.Size));
                    int hr = MfCom.Activate(activate, MfInterop.IidImfTransform, out nint transform);
                    if (hr < 0) { MfCom.Release(transform); continue; }
                    _transform = transform;
                    if (MfCom.SetType(_transform, input, true) >= 0) break;
                    ReleaseTransform();
                }
            }
            finally
            {
                for (int i = 0; i < count; i++) MfCom.Release(Marshal.ReadIntPtr(activates, checked(i * IntPtr.Size)));
                Marshal.FreeCoTaskMem(activates);
            }
            if (_transform == 0) throw new NotSupportedException("No synchronous Media Foundation AAC decoder is available.");
            if (!SelectOutputType()) throw new NotSupportedException("AAC decoder has no 16-bit PCM output.");
            Check(MfCom.Message(_transform, MfInterop.MftMessageBeginStreaming), "begin streaming");
            Check(MfCom.Message(_transform, MfInterop.MftMessageStartOfStream), "start of stream");
        }
        catch { ReleaseTransform(); throw; }
        finally { MfCom.Release(input); }
    }

    private bool SelectOutputType()
    {
        for (uint index = 0; index < 256; index++)
        {
            int hr = MfCom.OutputType(_transform, index, out nint type);
            if (hr < 0) { MfCom.Release(type); return false; }
            try
            {
                if (MfCom.GetGuid(type, MfInterop.MtSubtype, out Guid subtype) < 0 || subtype != MfInterop.AudioFormatPcm) continue;
                if (MfCom.GetUInt32(type, MfInterop.MtAudioBitsPerSample, out uint outputBits) < 0 || outputBits != 16) continue;
                if (MfCom.SetType(_transform, type, false) < 0) continue;
                _channels = MfCom.GetUInt32(type, MfInterop.MtAudioNumChannels, out uint channels) >= 0 ? checked((int)channels) : 2;
                _sampleRate = MfCom.GetUInt32(type, MfInterop.MtAudioSamplesPerSecond, out uint rate) >= 0 ? checked((int)rate) : 48000;
                _bitsPerSample = MfCom.GetUInt32(type, MfInterop.MtAudioBitsPerSample, out uint bits) >= 0 ? checked((int)bits) : 16;
                if (_channels is < 1 or > 8 || _sampleRate is < 8000 or > 192000 || _bitsPerSample is not (16 or 32))
                    throw new InvalidDataException($"Unsupported PCM output: {_channels}ch {_sampleRate}Hz {_bitsPerSample}bit.");
                return true;
            }
            finally { MfCom.Release(type); }
        }
        return false;
    }

    private void DrainOutput()
    {
        if (_transform == 0) return;
        for (int attempts = 0; attempts < 256; attempts++)
        {
            Check(MfCom.OutputInfo(_transform, out var info), "GetOutputStreamInfo");
            nint supplied = (info.Flags & MfInterop.MftOutputStreamProvidesSamples) == 0
                ? CreateOutputSample(info.Size, info.Alignment) : 0;
            var output = new MftOutputDataBuffer { Sample = supplied };
            try
            {
                int hr = MfCom.Output(_transform, ref output);
                if (hr == MfInterop.MfETransformNeedMoreInput) return;
                if (hr == MfInterop.MfETransformStreamChange)
                {
                    if (!SelectOutputType()) throw new NotSupportedException("Decoder changed to a non-PCM output format.");
                    continue;
                }
                Check(hr, "audio ProcessOutput");
                if (output.Sample == 0) return;
                EmitFrame(output.Sample);
            }
            finally
            {
                MfCom.Release(output.Events);
                if (output.Sample != supplied) MfCom.Release(output.Sample);
                MfCom.Release(supplied);
            }
        }
        throw new InvalidOperationException("Audio decoder failed to make bounded output progress.");
    }

    private nint CreateOutputSample(uint capacity, uint alignment)
    {
        if (capacity > AudioFormatLimits.MaximumPacketBytes) throw new InvalidDataException("Excessive MFT audio output capacity.");
        uint size = Math.Max(capacity, 1);
        if (alignment > 4096 || alignment != 0 && (alignment & (alignment - 1)) != 0)
            throw new InvalidDataException("Invalid audio MFT buffer alignment.");
        Check(MfInterop.MFCreateAlignedMemoryBuffer(size, alignment == 0 ? 0 : alignment - 1, out nint buffer), "MFCreateAlignedMemoryBuffer");
        try
        {
            Check(MfInterop.MFCreateSample(out nint sample), "MFCreateSample");
            try { Check(MfCom.AddBuffer(sample, buffer), "AddBuffer"); return sample; }
            catch { MfCom.Release(sample); throw; }
        }
        finally { MfCom.Release(buffer); }
    }

    private void EmitFrame(nint sample)
    {
        Check(MfCom.ContiguousBuffer(sample, out nint buffer), "ConvertToContiguousBuffer");
        try
        {
            Check(MfCom.Lock(buffer, out nint source, out uint length), "buffer lock");
            try
            {
                if (_bitsPerSample != 16 && _bitsPerSample != 32) throw new InvalidDataException("Unsupported PCM bit depth.");
                int blockAlign = _channels * (_bitsPerSample / 8);
                if (blockAlign == 0 || length < (uint)blockAlign || length > 16 * 1024 * 1024)
                    throw new InvalidDataException("Invalid audio output length.");
                int sampleCount = checked((int)(length / (uint)blockAlign));
                int required = checked(sampleCount * blockAlign);
                byte[] pooled = System.Buffers.ArrayPool<byte>.Shared.Rent(required);
                _ = MfCom.GetTime(sample, out long timestamp);
                new ReadOnlySpan<byte>((void*)source, required).CopyTo(pooled);
                using var frame = new DecodedAudioFrame(pooled, required, sampleCount, _channels, _sampleRate, _bitsPerSample, timestamp);
                _inCallback = true;
                try { _callback!(frame); }
                finally { _inCallback = false; }
            }
            finally { Check(MfCom.Unlock(buffer), "buffer unlock"); }
        }
        finally { MfCom.Release(buffer); }
    }

    private static nint CreateSample(ReadOnlySpan<byte> data, long timestampTicks)
    {
        uint size = Math.Max((uint)data.Length, 1);
        if (size > 16 * 1024 * 1024) throw new InvalidDataException("Audio MFT buffer exceeds the packet limit.");
        Check(MfInterop.MFCreateMemoryBuffer(size, out nint buffer), "MFCreateMemoryBuffer");
        try
        {
            if (!data.IsEmpty)
            {
                Check(MfCom.Lock(buffer, out nint target, out _), "buffer lock");
                try { data.CopyTo(new Span<byte>((void*)target, data.Length)); }
                finally { Check(MfCom.Unlock(buffer), "buffer unlock"); }
                Check(MfCom.SetLength(buffer, (uint)data.Length), "buffer length");
            }
            Check(MfInterop.MFCreateSample(out nint sample), "MFCreateSample");
            try
            {
                Check(MfCom.AddBuffer(sample, buffer), "AddBuffer");
                Check(MfCom.SetTime(sample, timestampTicks), "sample time");
                return sample;
            }
            catch { MfCom.Release(sample); throw; }
        }
        finally { MfCom.Release(buffer); }
    }

    private void ReleaseTransform()
    {
        if (_transform != 0)
        {
            _ = MfCom.Message(_transform, MfInterop.MftMessageFlush);
            MfCom.Release(_transform);
            _transform = 0;
        }
    }

    private void CheckThread()
    {
        if (_inCallback) throw new InvalidOperationException("Decoder callbacks must not reenter the decoder.");
        if (_ownerThread != Environment.CurrentManagedThreadId) throw new InvalidOperationException("The decoder must remain on its owning thread.");
    }

    private void CheckReady()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_callback is null) throw new InvalidOperationException("Audio decoder is not initialized.");
        CheckThread();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        if (_ownerThread != 0) CheckThread();
        _disposed = true;
        ReleaseTransform();
        _callback = null;
        if (_mfOwned)
        {
            lock (LifetimeLock)
            {
                if (--_leases == 0) _ = MfInterop.MFShutdown();
                _mfOwned = false;
            }
        }
        if (_comOwned) { MfInterop.CoUninitialize(); _comOwned = false; }
    }

    private static void Check(int hr, string operation)
    {
        if (hr < 0) throw new InvalidOperationException($"Media Foundation {operation} failed: 0x{hr:X8}.");
    }
}