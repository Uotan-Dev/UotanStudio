using System.Buffers;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace UotanStudio.Media.Decoding.MediaFoundation;

/// <summary>
/// Synchronous Media Foundation decoder with CPU-readable NV12 output.
/// 同步 Media Foundation 解码器；不声明 D3D 硬件解码或 GPU 零拷贝支持。
/// </summary>
/// <remarks>Initialize, Decode, Reset and Dispose must run on the same thread.</remarks>
[SupportedOSPlatform("windows")]
public sealed unsafe class MediaFoundationVideoDecoder : IVideoDecoder
{
    private static readonly object LifetimeLock = new();
    private static int _leases;
    private nint _transform;
    private Action<DecodedVideoFrame>? _callback;
    private int _width, _height, _stride;
    private int _ownerThread;
    private bool _comOwned, _mfOwned, _disposed;
    private long _sequence;
    private bool _inCallback;

    /// <inheritdoc />
    public bool IsInitialized => _transform != 0;

    /// <inheritdoc />
    public void Initialize(VideoCodecId codec, int width, int height, Action<DecodedVideoFrame> frameCallback)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        VideoFrameLimits.ValidateDimensions(width, height);
        ArgumentNullException.ThrowIfNull(frameCallback);
        Guid format = CodecGuid(codec);
        if (_ownerThread != 0) CheckThread();
        ReleaseTransform();
        if (!_mfOwned)
        {
            int hr = MfInterop.CoInitializeEx(0, 0);
            // RPC_E_CHANGED_MODE means the caller already owns a different apartment.
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

        Check(MfInterop.MFCreateMediaType(out nint input), "MFCreateMediaType");
        try
        {
            Check(MfCom.SetGuid(input, MfInterop.MtMajorType, MfInterop.MediaTypeVideo), "major type");
            Check(MfCom.SetGuid(input, MfInterop.MtSubtype, format), "subtype");
            Check(MfCom.SetUInt64(input, MfInterop.MtFrameSize, ((ulong)(uint)width << 32) | (uint)height), "frame size");
            var registration = new MftRegisterTypeInfo(MfInterop.MediaTypeVideo, format);
            Check(MfInterop.MFTEnumEx(MfInterop.CategoryVideoDecoder, MfInterop.MftEnumFlags,
                registration, 0, out nint activates, out uint count), "MFTEnumEx");
            try
            {
                for (int i = 0; i < count; i++)
                {
                    nint activate = Marshal.ReadIntPtr(activates, checked(i * IntPtr.Size));
                    int hr = MfCom.Activate(activate, MfInterop.IidImfTransform, out nint transform);
                    if (hr < 0) { MfCom.Release(transform); continue; }
                    _transform = transform;
                    if (MfCom.SetType(transform, input, true) >= 0 && SelectOutputType())
                    {
                        ConfigureLowLatency(transform, codec);
                        break;
                    }
                    ReleaseTransform();
                }
            }
            finally
            {
                for (int i = 0; i < count; i++) MfCom.Release(Marshal.ReadIntPtr(activates, checked(i * IntPtr.Size)));
                Marshal.FreeCoTaskMem(activates);
            }
            if (_transform == 0) throw new NotSupportedException($"No synchronous Media Foundation NV12 decoder accepts {codec}.");
            _callback = frameCallback;
            _sequence = 0;
            Check(MfCom.Message(_transform, MfInterop.MftMessageBeginStreaming), "begin streaming");
            Check(MfCom.Message(_transform, MfInterop.MftMessageStartOfStream), "start of stream");
        }
        catch { ReleaseTransform(); throw; }
        finally { MfCom.Release(input); }
    }

    /// <inheritdoc />
    public void Decode(ReadOnlySpan<byte> accessUnit)
        => Decode(accessUnit, checked(_sequence++ * 166667));

    /// <inheritdoc />
    public void Decode(ReadOnlySpan<byte> accessUnit, long timestampTicks)
    {
        CheckReady();
        VideoFrameLimits.ValidateAccessUnit(accessUnit);
        nint sample = CreateSample(accessUnit, 0, 0);
        try
        {
            Check(MfCom.SetTime(sample, timestampTicks), "sample time");
            int hr = MfCom.Input(_transform, sample);
            if (hr == MfInterop.MfENotAccepting)
            {
                DrainOutput();
                hr = MfCom.Input(_transform, sample); // Retry the exact same sample; never drop it.
            }
            Check(hr, "ProcessInput");
            DrainOutput();
        }
        finally { MfCom.Release(sample); }
    }

    /// <inheritdoc />
    public void Reset()
    {
        CheckReady();
        Check(MfCom.Message(_transform, MfInterop.MftMessageFlush), "flush");
        Check(MfCom.Message(_transform, MfInterop.MftMessageStartOfStream), "restart stream");
        _sequence = 0;
    }

    /// <inheritdoc />
    public void Drain()
    {
        CheckReady();
        Check(MfCom.Message(_transform, 0x10000002), "end of stream");
        Check(MfCom.Message(_transform, 1), "drain command");
        DrainOutput();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        if (_ownerThread != 0) CheckThread();
        _disposed = true;
        ReleaseTransform();
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

    internal static void ConfigureLowLatency(nint transform, VideoCodecId codec)
    {
        if (MfCom.GetAttributes(transform, out nint attributes) >= 0 && attributes != 0)
        {
            try { Check(MfCom.SetUInt32(attributes, new Guid("9c27891a-ed7a-40e1-88e8-b22727a024ee"), 1), "low latency attribute"); }
            finally { MfCom.Release(attributes); }
        }
        int hr = MfCom.QueryInterface(transform, new Guid("901db4c7-31ce-41a2-85dc-8fa0bf41b8da"), out nint codecApi);
        if (hr < 0) return; // Third-party synchronous transforms may only expose attributes.
        try
        {
            Check(MfCom.SetCodecLowLatency(codecApi, new Guid("9c27891a-ed7a-40e1-88e8-b22727a024ee"), codec == VideoCodecId.H264), "enable low latency");
        }
        finally { MfCom.Release(codecApi); }
    }

    private VideoColorMatrix _colorMatrix;
    private VideoColorRange _colorRange;

    private bool SelectOutputType()
    {
        for (uint index = 0; index < 256; index++)
        {
            int hr = MfCom.OutputType(_transform, index, out nint type);
            if (hr < 0) { MfCom.Release(type); return false; }
            try
            {
                if (MfCom.GetGuid(type, MfInterop.MtSubtype, out Guid subtype) < 0 || subtype != MfInterop.VideoFormatNv12) continue;
                if (MfCom.SetType(_transform, type, false) < 0) continue;
                Check(MfCom.GetUInt64(type, MfInterop.MtFrameSize, out ulong size), "output dimensions");
                _ = MfCom.GetUInt32(type, new Guid("c21b8ee5-b956-4071-8daf-325edf5cab11"), out uint range);
                _ = MfCom.GetUInt32(type, new Guid("3e23d450-2c75-4d25-a00e-b91670d12327"), out uint matrix);
                _colorMatrix = matrix switch { 1 => VideoColorMatrix.Bt709, 2 => VideoColorMatrix.Bt601, _ => VideoColorMatrix.Auto };
                _colorRange = range switch { 1 => VideoColorRange.Full, 2 => VideoColorRange.Limited, _ => VideoColorRange.Auto };
                _width = checked((int)(size >> 32));
                _height = checked((int)(size & uint.MaxValue));
                VideoFrameLimits.ValidateDimensions(_width, _height);
                _stride = MfCom.GetUInt32(type, MfInterop.MtDefaultStride, out uint stride) >= 0
                    ? checked((int)stride) : checked((_width + 1) & ~1);
                _ = VideoFrameLimits.Nv12Length(_width, _height, _stride);
                return true;
            }
            finally { MfCom.Release(type); }
        }
        return false;
    }

    private void DrainOutput()
    {
        for (int attempts = 0; attempts < 256; attempts++)
        {
            Check(MfCom.OutputInfo(_transform, out var info), "GetOutputStreamInfo");
            nint supplied = (info.Flags & MfInterop.MftOutputStreamProvidesSamples) == 0
                ? CreateSample([], info.Size, info.Alignment) : 0;
            var output = new MftOutputDataBuffer { Sample = supplied };
            try
            {
                int hr = MfCom.Output(_transform, ref output);
                if (hr == MfInterop.MfETransformNeedMoreInput) return;
                if (hr == MfInterop.MfETransformStreamChange)
                {
                    if (!SelectOutputType()) throw new NotSupportedException("Decoder changed to a non-NV12 output format.");
                    continue;
                }
                Check(hr, "ProcessOutput");
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
        throw new InvalidOperationException("Decoder failed to make bounded output progress.");
    }

    private void EmitFrame(nint sample)
    {
        Check(MfCom.ContiguousBuffer(sample, out nint buffer), "ConvertToContiguousBuffer");
        try
        {
            Check(MfCom.Lock(buffer, out nint source, out uint length), "buffer lock");
            try
            {
                int required = VideoFrameLimits.Nv12Length(_width, _height, _stride);
                if (length < required) throw new InvalidDataException("Truncated NV12 output.");
                byte[] pooled = ArrayPool<byte>.Shared.Rent(required);
                _ = MfCom.GetTime(sample, out long timestamp);
                using var frame = new DecodedVideoFrame(pooled, required, _width, _height, _stride, timestamp) { ColorMatrix = _colorMatrix, ColorRange = _colorRange };
                new ReadOnlySpan<byte>((void*)source, required).CopyTo(pooled);
                _inCallback = true;
                try { _callback!(frame); }
                finally { _inCallback = false; }
            }
            finally { Check(MfCom.Unlock(buffer), "buffer unlock"); }
        }
        finally { MfCom.Release(buffer); }
    }

    private static nint CreateSample(ReadOnlySpan<byte> data, uint capacity, uint alignment)
    {
        uint size = Math.Max((uint)data.Length, capacity);
        if (size > VideoFrameLimits.MaximumFrameBytes) throw new InvalidDataException("MFT buffer exceeds the frame limit.");
        if (alignment > 4096 || alignment != 0 && (alignment & (alignment - 1)) != 0)
            throw new InvalidDataException("Invalid MFT buffer alignment.");
        Check(MfInterop.MFCreateAlignedMemoryBuffer(Math.Max(size, 1), alignment == 0 ? 0 : alignment - 1, out nint buffer), "MFCreateAlignedMemoryBuffer");
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
            try { Check(MfCom.AddBuffer(sample, buffer), "AddBuffer"); return sample; }
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
        _callback = null;
    }
    private void CheckThread()
    {
        if (_inCallback) throw new InvalidOperationException("Decoder callbacks must not reenter the decoder.");
        if (_ownerThread != Environment.CurrentManagedThreadId) throw new InvalidOperationException("The decoder must remain on its owning thread.");
    }
    private void CheckReady()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsInitialized) throw new InvalidOperationException("Decoder is not initialized.");
        CheckThread();
    }
    private static Guid CodecGuid(VideoCodecId codec) => codec switch
    {
        VideoCodecId.H264 => MfInterop.VideoFormatH264,
        VideoCodecId.H265 => MfInterop.VideoFormatHevc,
        VideoCodecId.Av1 => new Guid("31305641-0000-0010-8000-00aa00389b71"),
        _ => throw new ArgumentOutOfRangeException(nameof(codec)),
    };
    private static void Check(int hr, string operation)
    {
        if (hr < 0) throw new InvalidOperationException($"Media Foundation {operation} failed: 0x{hr:X8}.");
    }
}