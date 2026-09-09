using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace UotanStudio.Media.Decoding.VideoToolbox;

/// <summary>
/// VideoToolbox H.264/H.265 Annex-B decoder with required hardware acceleration and NV12 readback.
/// macOS 硬件解码后端，接收独立 VPS/SPS/PPS 配置包并在参数变化时重建会话。
/// </summary>
/// <remarks>Calls belong to one thread; decoded frames are delivered synchronously from Decode.</remarks>
[SupportedOSPlatform("macos")]
public sealed unsafe class VideoToolboxVideoDecoder : IVideoDecoder
{
    private nint _session, _format;
    private GCHandle _self;
    private VideoCodecId _codec;
    private byte[]? _vps, _sps, _pps;
    private bool _dirty, _disposed;
    private Action<DecodedVideoFrame>? _callback;
    private readonly ConcurrentQueue<DecodedVideoFrame> _frames = new();
    private ExceptionDispatchInfo? _error;
    private int _thread;
    private long _sequence;
    private bool _inCallback;

    /// <inheritdoc />
    public bool IsInitialized => _callback is not null;

    /// <inheritdoc />
    public void Initialize(VideoCodecId codec, int width, int height, Action<DecodedVideoFrame> frameCallback)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        ObjectDisposedException.ThrowIf(_disposed, this);
        VideoFrameLimits.ValidateDimensions(width, height);
        ArgumentNullException.ThrowIfNull(frameCallback);
        if (_thread != 0) CheckThread();
        if (VtNative.VTIsHardwareDecodeSupported(VtNative.Codec(codec)) == 0)
            throw new NotSupportedException($"VideoToolbox hardware {codec} decoding is unavailable.");
        ReleaseSession();
        _thread = Environment.CurrentManagedThreadId;
        _codec = codec;
        _callback = frameCallback;
        _vps = _sps = _pps = null;
        _sequence = 0;
    }

    /// <inheritdoc />
    public void Decode(ReadOnlySpan<byte> accessUnit)
        => Decode(accessUnit, checked(_sequence++ * 166667));

    /// <inheritdoc />
    public void Decode(ReadOnlySpan<byte> accessUnit, long timestampTicks)
    {
        CheckReady();
        List<Range> ranges = AnnexB.Split(accessUnit);
        var payload = new List<Range>();
        bool hasPicture = false;
        foreach (Range range in ranges)
        {
            ReadOnlySpan<byte> nal = accessUnit[range];
            if (_codec == VideoCodecId.H265 && nal.Length < 2) throw new InvalidDataException("Truncated HEVC NAL header.");
            int type = _codec == VideoCodecId.H264 ? nal[0] & 31 : (nal[0] >> 1) & 63;
            if (_codec == VideoCodecId.H264 && type == 7 || _codec == VideoCodecId.H265 && type == 33) Update(ref _sps, nal);
            else if (_codec == VideoCodecId.H264 && type == 8 || _codec == VideoCodecId.H265 && type == 34) Update(ref _pps, nal);
            else if (_codec == VideoCodecId.H265 && type == 32) Update(ref _vps, nal);
            else
            {
                payload.Add(range);
                hasPicture |= _codec == VideoCodecId.H264 ? type is >= 1 and <= 5 : type <= 31;
            }
        }
        if (!hasPicture) return;
        if (_sps is null || _pps is null || _codec == VideoCodecId.H265 && _vps is null)
            throw new InvalidDataException("Video frame arrived before complete codec parameter sets.");
        if (_dirty || _session == 0) CreateSession();
        byte[] bytes = AnnexB.ToLengthPrefixed(accessUnit, payload);
        VtNative.Check(VtNative.CMBlockBufferCreateWithMemoryBlock(0, 0, (nuint)bytes.Length, 0, 0, 0,
            (nuint)bytes.Length, 0, out nint block), "create block buffer");
        try
        {
            fixed (byte* data = bytes) VtNative.Check(VtNative.CMBlockBufferReplaceDataBytes(data, block, 0, (nuint)bytes.Length), "copy access unit");
            var timing = new CmTiming { Presentation = CmTime.FromTicks(timestampTicks) };
            nuint size = (nuint)bytes.Length;
            VtNative.Check(VtNative.CMSampleBufferCreateReady(0, block, _format, 1, 1, &timing, 1, &size, out nint sample), "create sample");
            try
            {
                VtNative.Check(VtNative.VTDecompressionSessionDecodeFrame(_session, sample, 0, 0, out _), "decode frame");
                VtNative.Check(VtNative.VTDecompressionSessionWaitForAsynchronousFrames(_session), "wait for frames");
                Interlocked.Exchange(ref _error, null)?.Throw();
                while (_frames.TryDequeue(out DecodedVideoFrame? frame))
                {
                    using (frame) Deliver(frame);
                }
            }
            finally { VtNative.CFRelease(sample); }
        }
        finally { VtNative.CFRelease(block); ClearFrames(); }
    }

    /// <inheritdoc />
    public void Reset()
    {
        CheckReady();
        ReleaseSession(); // Preserve parameter sets; the next picture creates a fresh reference state.
        _sequence = 0;
    }

    /// <inheritdoc />
    public void Drain()
    {
        CheckReady();
        if (_session == 0) return;
        VtNative.Check(VtNative.VTDecompressionSessionFinishDelayedFrames(_session), "finish delayed frames");
        VtNative.Check(VtNative.VTDecompressionSessionWaitForAsynchronousFrames(_session), "wait for delayed frames");
        try
        {
            Interlocked.Exchange(ref _error, null)?.Throw();
            while (_frames.TryDequeue(out var frame))
                using (frame) Deliver(frame);
        }
        finally { ClearFrames(); }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        if (_thread != 0) CheckThread();
        _disposed = true;
        ReleaseSession();
        _callback = null;
        _vps = _sps = _pps = null;
    }

    private void Update(ref byte[]? target, ReadOnlySpan<byte> value)
    {
        if (value.Length > 1024 * 1024) throw new InvalidDataException("Excessive codec parameter set.");
        if (target is null || !value.SequenceEqual(target)) { target = value.ToArray(); _dirty = true; }
    }

    private void CreateSession()
    {
        ReleaseSession();
        fixed (byte* sps = _sps, pps = _pps, vps = _vps)
        {
            byte** sets = stackalloc byte*[3];
            nuint* sizes = stackalloc nuint[3];
            if (_codec == VideoCodecId.H264)
            {
                sets[0] = sps; sets[1] = pps;
                sizes[0] = (nuint)_sps!.Length; sizes[1] = (nuint)_pps!.Length;
                VtNative.Check(VtNative.CMVideoFormatDescriptionCreateFromH264ParameterSets(0, 2, sets, sizes, 4, out _format), "H.264 format");
            }
            else
            {
                sets[0] = vps; sets[1] = sps; sets[2] = pps;
                sizes[0] = (nuint)_vps!.Length; sizes[1] = (nuint)_sps!.Length; sizes[2] = (nuint)_pps!.Length;
                VtNative.Check(VtNative.CMVideoFormatDescriptionCreateFromHEVCParameterSets(0, 3, sets, sizes, 4, 0, out _format), "HEVC format");
            }
        }
        nint specification = 0, attributes = 0;
        try
        {
            specification = VtNative.Dictionary();
            VtNative.SetBoolean(specification, "kVTVideoDecoderSpecification_RequireHardwareAcceleratedVideoDecoder", true);
            attributes = VtNative.PixelAttributes();
            _self = GCHandle.Alloc(this);
            var callback = new VtCallback { Function = &OnFrame, RefCon = GCHandle.ToIntPtr(_self) };
            VtNative.Check(VtNative.VTDecompressionSessionCreate(0, _format, specification, attributes, &callback, out _session), "create hardware decoder");
            _dirty = false;
        }
        catch { ReleaseSession(); throw; }
        finally
        {
            if (attributes != 0) VtNative.CFRelease(attributes);
            if (specification != 0) VtNative.CFRelease(specification);
        }
    }

    [UnmanagedCallersOnly]
    private static void OnFrame(nint refCon, nint source, int status, uint flags, nint image, CmTime time, CmTime duration)
    {
        var decoder = (VideoToolboxVideoDecoder)GCHandle.FromIntPtr(refCon).Target!;
        try
        {
            VtNative.Check(status, "output callback");
            if (image == 0) return; // Dropped frames have no image buffer.
            if (decoder._frames.Count >= 32) throw new InvalidDataException("Too many pending VideoToolbox frames.");
            decoder._frames.Enqueue(CopyFrame(image, time.Ticks));
        }
        catch (Exception exception) { Interlocked.CompareExchange(ref decoder._error, ExceptionDispatchInfo.Capture(exception), null); }
    }

    private static DecodedVideoFrame CopyFrame(nint image, long timestamp)
    {
        if (VtNative.CVPixelBufferGetPixelFormatType(image) != VtNative.Nv12 || VtNative.CVPixelBufferGetPlaneCount(image) != 2)
            throw new NotSupportedException("VideoToolbox did not produce 8-bit video-range NV12.");
        int width = checked((int)VtNative.CVPixelBufferGetWidth(image));
        int height = checked((int)VtNative.CVPixelBufferGetHeight(image));
        int stride = checked((width + 1) & ~1);
        int length = VideoFrameLimits.Nv12Length(width, height, stride);
        VtNative.Check(VtNative.CVPixelBufferLockBaseAddress(image, 1), "lock decoded pixels");
        try
        {
            byte[] bytes = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                bytes.AsSpan(0, length).Clear();
                for (nuint plane = 0; plane < 2; plane++)
                {
                    int rows = plane == 0 ? height : (height + 1) / 2;
                    int rowBytes = plane == 0 ? width : stride;
                    nuint pitch = VtNative.CVPixelBufferGetBytesPerRowOfPlane(image, plane);
                    nint pointer = VtNative.CVPixelBufferGetBaseAddressOfPlane(image, plane);
                    if (pointer == 0 || pitch < (nuint)rowBytes || pitch > VideoFrameLimits.MaximumFrameBytes
                        || VtNative.CVPixelBufferGetHeightOfPlane(image, plane) < (nuint)rows) throw new InvalidDataException("Invalid CoreVideo plane layout.");
                    int start = plane == 0 ? 0 : stride * height;
                    for (int row = 0; row < rows; row++)
                        new ReadOnlySpan<byte>((byte*)pointer + checked((nuint)row * pitch), rowBytes).CopyTo(bytes.AsSpan(start + row * stride, rowBytes));
                }
                VideoColorMatrix matrix = VideoColorMatrix.Auto;
                nint attachment = VtNative.CVBufferCopyAttachment(image, VtNative.Symbol(VtNative.Cv, "kCVImageBufferYCbCrMatrixKey"), 0);
                if (attachment != 0)
                {
                    try
                    {
                        if (VtNative.CFEqual(attachment, VtNative.Symbol(VtNative.Cv, "kCVImageBufferYCbCrMatrix_ITU_R_709_2")) != 0) matrix = VideoColorMatrix.Bt709;
                        else if (VtNative.CFEqual(attachment, VtNative.Symbol(VtNative.Cv, "kCVImageBufferYCbCrMatrix_ITU_R_601_4")) != 0) matrix = VideoColorMatrix.Bt601;
                    }
                    finally { VtNative.CFRelease(attachment); }
                }
                return new(bytes, length, width, height, stride, timestamp) { ColorMatrix = matrix, ColorRange = VideoColorRange.Limited };
            }
            catch { ArrayPool<byte>.Shared.Return(bytes); throw; }
        }
        finally { VtNative.Check(VtNative.CVPixelBufferUnlockBaseAddress(image, 1), "unlock decoded pixels"); }
    }

    private void ReleaseSession()
    {
        if (_session != 0)
        {
            _ = VtNative.VTDecompressionSessionWaitForAsynchronousFrames(_session);
            VtNative.VTDecompressionSessionInvalidate(_session);
            VtNative.CFRelease(_session);
            _session = 0;
        }
        if (_self.IsAllocated) _self.Free();
        if (_format != 0) { VtNative.CFRelease(_format); _format = 0; }
        ClearFrames();
        _error = null;
    }
    private void ClearFrames() { while (_frames.TryDequeue(out var frame)) frame.Dispose(); }
    private void CheckReady()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsInitialized) throw new InvalidOperationException("Decoder is not initialized.");
        CheckThread();
    }
    private void Deliver(DecodedVideoFrame frame)
    {
        _inCallback = true;
        try { _callback!(frame); }
        finally { _inCallback = false; }
    }
    private void CheckThread()
    {
        if (_inCallback) throw new InvalidOperationException("Decoder callbacks must not reenter the decoder.");
        if (_thread != Environment.CurrentManagedThreadId) throw new InvalidOperationException("Decoder belongs to another thread.");
    }
}