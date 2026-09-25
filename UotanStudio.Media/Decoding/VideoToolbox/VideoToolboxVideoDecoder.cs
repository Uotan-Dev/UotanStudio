using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace UotanStudio.Media.Decoding.VideoToolbox;

/// <summary>
/// VideoToolbox H.264/H.265 Annex-B decoder with required hardware acceleration and NV12 readback.
/// <para>VideoToolbox 的 H.264/H.265 Annex-B 解码器，强制使用硬件加速并回读 NV12。</para>
/// <para>macOS 硬件解码后端，接收独立 VPS/SPS/PPS 配置包并在参数变化时重建会话。</para>
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

    /// <summary>
    /// Whether a frame callback has been registered by a successful <see cref="Initialize"/>.
    /// <para>是否已由成功的 <see cref="Initialize"/> 登记帧回调。</para>
    /// </summary>
    /// <value>
    /// <see langword="true"/> between <see cref="Initialize"/> and <see cref="Dispose"/>; the hardware decode
    /// session itself is only created once complete parameter sets arrive with the first picture.
    /// <para>在 <see cref="Initialize"/> 与 <see cref="Dispose"/> 之间为 <see langword="true"/>；硬件解码会话
    /// 要等到完整参数集随首个图像到达后才真正创建。</para>
    /// </value>
    public bool IsInitialized => _callback is not null;

    /// <summary>
    /// Registers the frame callback and verifies that VideoToolbox hardware decoding is available for the codec;
    /// no decompression session exists yet. The calling thread becomes the owning thread for all later calls.
    /// <para>登记帧回调并校验 VideoToolbox 对该编码的硬件解码可用性；此时尚不解压会话。调用线程成为后续
    /// 所有调用的所属线程。</para>
    /// </summary>
    /// <param name="codec">H.264 or H.265; AV1 is rejected. <para>H.264 或 H.265；AV1 会被拒绝。</para></param>
    /// <param name="width">
    /// Expected frame width in pixels, from 1 to 16 384; actual output geometry follows the parameter sets.
    /// <para>预期帧宽度（像素），1 到 16 384；实际输出几何由参数集决定。</para>
    /// </param>
    /// <param name="height">
    /// Expected frame height in pixels, from 1 to 16 384.
    /// <para>预期帧高度（像素），1 到 16 384。</para>
    /// </param>
    /// <param name="frameCallback">
    /// Receives every decoded NV12 frame on the calling thread; the frame is disposed once the callback returns,
    /// so consumers must copy or convert within it.
    /// <para>在调用线程上接收每个解码出的 NV12 帧；回调返回后帧即被释放，消费方须在其内部完成复制或转换。</para>
    /// </param>
    /// <exception cref="PlatformNotSupportedException">The current OS is not macOS. <para>当前系统不是 macOS。</para></exception>
    /// <exception cref="ObjectDisposedException">The decoder was disposed. <para>解码器已被释放。</para></exception>
    /// <exception cref="ArgumentNullException"><paramref name="frameCallback"/> is <see langword="null"/>.
    /// <para><paramref name="frameCallback"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">The dimensions exceed the supported limits or the codec is not H.264/H.265.
    /// <para>尺寸超出支持上限，或编码不是 H.264/H.265。</para></exception>
    /// <exception cref="InvalidOperationException">
    /// Called from a callback or another thread, or a VideoToolbox or CoreVideo call failed.
    /// <para>在回调内或其他线程上调用，或 VideoToolbox、CoreVideo 调用失败。</para>
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// Hardware-accelerated decoding is unavailable for the codec.
    /// <para>该编码没有硬件加速解码能力。</para>
    /// </exception>
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

    /// <summary>
    /// Decodes one Annex-B access unit with a synthesized 60 fps timestamp derived from an internal frame counter.
    /// <para>解码一个 Annex-B 访问单元，时间戳为由内部帧计数器合成的 60 fps 值。</para>
    /// </summary>
    /// <param name="accessUnit">
    /// One Annex-B access unit; parameter sets may share it with picture data.
    /// <para>单个 Annex-B 访问单元；参数集可与图像数据同包。</para>
    /// </param>
    /// <exception cref="OverflowException">The internal timestamp sequence exceeded the tick range.
    /// <para>内部时间戳序列超出 tick 范围。</para></exception>
    public void Decode(ReadOnlySpan<byte> accessUnit)
        => Decode(accessUnit, checked(_sequence++ * 166667));

    /// <summary>
    /// Decodes one Annex-B access unit with an explicit timestamp: VPS/SPS/PPS NAL units are cached (a change
    /// rebuilds the hardware session), picture NAL units are converted to length-prefixed form, and every
    /// resulting NV12 frame is delivered through the callback on the calling thread before this call returns.
    /// Access units holding only parameter sets decode nothing.
    /// <para>解码一个带显式时间戳的 Annex-B 访问单元：缓存 VPS/SPS/PPS NAL 单元（内容变化会重建硬件会话），
    /// 将图像 NAL 单元转为长度前缀形式，并在本调用返回前于调用线程上经回调交付全部 NV12 帧。
    /// 仅含参数集的访问单元不产生解码。</para>
    /// </summary>
    /// <param name="accessUnit">
    /// One Annex-B access unit; parameter sets may share it with picture data.
    /// <para>单个 Annex-B 访问单元；参数集可与图像数据同包。</para>
    /// </param>
    /// <param name="timestampTicks">
    /// Presentation time in 100-nanosecond ticks, carried into the sample buffer timing.
    /// <para>呈现时间，以 100 纳刻（tick）为单位，会写入样本缓冲的时间信息。</para>
    /// </param>
    /// <exception cref="ObjectDisposedException">The decoder was disposed. <para>解码器已被释放。</para></exception>
    /// <exception cref="InvalidOperationException">
    /// The decoder is not initialized, called from a callback or another thread, or a VideoToolbox call failed.
    /// <para>解码器未初始化、在回调内或其他线程上调用，或 VideoToolbox 调用失败。</para>
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// A picture arrived before complete parameter sets, an NAL header or parameter set is truncated or excessive,
    /// or the decoded pixel buffer has an invalid plane layout.
    /// <para>图像先于完整参数集到达、NAL 头或参数集被截断或过大，或解码像素缓冲的平面布局无效。</para>
    /// </exception>
    /// <exception cref="NotSupportedException">The decoded pixel buffer is not 8-bit NV12 with two planes.
    /// <para>解码像素缓冲不是双平面 8 位 NV12。</para></exception>
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

    /// <summary>
    /// Invalidates the hardware session and resets the timestamp sequence; cached parameter sets and the callback
    /// are kept, so the next picture recreates the session. The decoder stays initialized.
    /// <para>使硬件会话失效并重置时间戳序列；保留缓存的参数集与回调，下一帧图像会重建会话。解码器保持已初始化。</para>
    /// </summary>
    /// <exception cref="ObjectDisposedException">The decoder was disposed. <para>解码器已被释放。</para></exception>
    /// <exception cref="InvalidOperationException">The decoder is not initialized, or called from a callback or another thread.
    /// <para>解码器未初始化，或在回调内或其他线程上调用。</para></exception>
    public void Reset()
    {
        CheckReady();
        ReleaseSession();
        _sequence = 0;
    }

    /// <summary>
    /// Flushes delayed frames from the hardware session, waits for asynchronous completion, and delivers every
    /// outstanding NV12 frame through the callback on the calling thread. Does nothing before a session exists;
    /// call <see cref="Initialize"/> or <see cref="Reset"/> before decoding a new stream.
    /// <para>导出硬件会话中滞留的帧并等待异步完成，在调用线程上经回调交付全部未交付的 NV12 帧。
    /// 会话建立前不做任何事；解码新码流前须调用 <see cref="Initialize"/> 或 <see cref="Reset"/>。</para>
    /// </summary>
    /// <exception cref="ObjectDisposedException">The decoder was disposed. <para>解码器已被释放。</para></exception>
    /// <exception cref="InvalidOperationException">
    /// The decoder is not initialized, called from a callback or another thread, too many frames are pending,
    /// a frame's plane layout is invalid, or a VideoToolbox call failed.
    /// <para>解码器未初始化、在回调内或其他线程上调用、滞留帧过多、帧的平面布局无效，或 VideoToolbox 调用失败。</para>
    /// </exception>
    /// <exception cref="NotSupportedException">A delayed frame was not delivered as 8-bit dual-plane NV12.
    /// <para>滞留帧未以双平面 8 位 NV12 交付。</para></exception>
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

    /// <summary>
    /// Waits for in-flight frames, invalidates the session, releases the CoreVideo format and callback handle,
    /// and drops the cached parameter sets. Repeat calls are no-ops; must run on the owning thread.
    /// <para>等待在途帧完成、使会话失效、释放 CoreVideo 格式与回调句柄，并清除缓存的参数集；
    /// 重复调用无效果，必须在所属线程上执行。</para>
    /// </summary>
    /// <exception cref="InvalidOperationException">Called from inside a frame callback or from another thread.
    /// <para>在帧回调内部或其他线程上调用。</para></exception>
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
            if (image == 0) return;
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