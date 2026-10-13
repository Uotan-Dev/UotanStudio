using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace UotanStudio.Media.Decoding.VideoToolbox;

/// <summary>
/// VideoToolbox H.264/H.265 Annex-B decoder with required hardware acceleration and NV12 readback, or
/// IOSurface-backed BGRA export for zero-copy presentation.
/// <para>VideoToolbox 的 H.264/H.265 Annex-B 解码器：强制硬件加速，可回读 NV12，或导出 IOSurface 支撑的
/// BGRA 帧以供零拷贝呈现。</para>
/// <para>macOS 硬件解码后端，接收独立 VPS/SPS/PPS 配置包并在参数变化时重建会话。</para>
/// </summary>
/// <remarks>
/// Calls belong to one thread; decoded frames are delivered synchronously from Decode.
/// <see cref="VideoDecoderOptions.ColorMatrix"/> and <see cref="VideoDecoderOptions.ColorRange"/>
/// are ignored by this backend: VideoToolbox always converts to fixed video-range NV12 and exposes
/// no managed hook to force other colorimetry, so the reported metadata stays truthful. When export is
/// requested the session asks for IOSurface-compatible BGRA output and falls back to the NV12 session if
/// the hardware path refuses it; disabling export mid-session freezes the picture until the next
/// <see cref="Reset"/> recreates the session.
/// <para>所有调用属于单线程；解码帧在 Decode 内同步交付。本后端忽略
/// <see cref="VideoDecoderOptions.ColorMatrix"/> 与 <see cref="VideoDecoderOptions.ColorRange"/>：
/// VideoToolbox 总是转换为固定的 video-range NV12，且没有托管侧可强制其他色彩空间的入口，
/// 因此元数据保持如实报告。请求导出时会话申请 IOSurface 兼容的 BGRA 输出，硬件路径拒绝时回落 NV12 会话；
/// 播放中禁用导出会使画面停帧，直到下一次 <see cref="Reset"/> 重建会话。</para>
/// </remarks>
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
    private readonly ConcurrentQueue<IosurfaceVideoFrame> _exportFrames = new();
    private volatile bool _gpuExportEnabled;
    private Action<IosurfaceVideoFrame>? _iosurfaceFrameCallback;
    private long _exportDropped;
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
    /// Gets or sets whether decoded frames are handed to <see cref="IosurfaceFrameCallback"/> as
    /// IOSurface-backed BGRA frames instead of being copied as NV12. The flag is read on the owning thread
    /// and may be set from any thread; the session's pixel format is chosen when it is created, so
    /// disabling mid-session freezes the picture until the next <see cref="Reset"/>.
    /// <para>获取或设置解码帧是否以 IOSurface 支撑的 BGRA 帧交付给 <see cref="IosurfaceFrameCallback"/>，
    /// 而非拷贝 NV12。标志由所属线程读取、可在任意线程设置；会话像素格式在创建时决定，
    /// 播放中禁用会使画面停帧直到下一次 <see cref="Reset"/>。</para>
    /// </summary>
    /// <value>
    /// Effective only when initialization requested export via
    /// <see cref="VideoDecoderOptions.VideoToolboxPreferGpuExport"/>.
    /// <para>仅在初始化经 <see cref="VideoDecoderOptions.VideoToolboxPreferGpuExport"/> 请求导出时生效。</para>
    /// </value>
    public bool GpuExportEnabled { get => _gpuExportEnabled; set => _gpuExportEnabled = value; }

    /// <summary>
    /// Receives IOSurface-backed BGRA frames when <see cref="GpuExportEnabled"/> is set; the callback runs
    /// on the owning thread and must not reenter the decoder. Presentation must call
    /// <see cref="IosurfaceVideoFrame.MarkPresented"/> exactly once per frame, in a finally clause.
    /// <para>在 <see cref="GpuExportEnabled"/> 开启时接收 IOSurface 支撑的 BGRA 帧；回调在所属线程执行且不得
    /// 重入解码器。呈现方必须对每帧在 finally 中恰好调用一次 <see cref="IosurfaceVideoFrame.MarkPresented"/>。</para>
    /// </summary>
    public Action<IosurfaceVideoFrame>? IosurfaceFrameCallback { get => _iosurfaceFrameCallback; set => _iosurfaceFrameCallback = value; }

    /// <summary>
    /// Gets how many decoded frames were dropped because the presenter was busy or the buffer carried no
    /// IOSurface.
    /// <para>获取因呈现方忙碌或缓冲不含 IOSurface 而丢弃的解码帧数。</para>
    /// </summary>
    public long ExportDroppedFrames => Interlocked.Read(ref _exportDropped);

    private bool ExportActive => _gpuExportEnabled && _iosurfaceFrameCallback is not null;

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
    /// Decodes one Annex-B access unit with an explicit timestamp.
    /// <para>解码一个带显式时间戳的 Annex-B 访问单元。</para>
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
                DeliverQueued();
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
    /// Flushes delayed frames from the hardware session and delivers every outstanding NV12 frame.
    /// <para>导出硬件会话中滞留的帧并交付全部未交付的 NV12 帧。</para>
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
            DeliverQueued();
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
        if (ExportActive)
        {
            nint exportAttributes = VtNative.PixelAttributes(VtNative.Bgra, gpuCompatible: true);
            try
            {
                if (TryCreateSession(exportAttributes)) return;
            }
            finally { VtNative.CFRelease(exportAttributes); }
        }
        nint attributes = VtNative.PixelAttributes();
        try { TryCreateSession(attributes); }
        finally { VtNative.CFRelease(attributes); }
    }

    /// <summary>
    /// Creates the decompression session with the given pixel-buffer attributes. Returns
    /// <see langword="false"/> when VideoToolbox rejects the configuration, leaving no session behind so
    /// the caller can retry with different attributes.
    /// <para>以给定像素缓冲属性创建解压会话。VideoToolbox 拒绝该配置时返回 <see langword="false"/>，
    /// 不遗留任何会话，调用方可换属性重试。</para>
    /// </summary>
    private bool TryCreateSession(nint attributes)
    {
        nint specification = 0;
        try
        {
            specification = VtNative.Dictionary();
            VtNative.SetBoolean(specification, VtNative.Vt, "kVTVideoDecoderSpecification_RequireHardwareAcceleratedVideoDecoder", true);
            _self = GCHandle.Alloc(this);
            var callback = new VtCallback { Function = &OnFrame, RefCon = GCHandle.ToIntPtr(_self) };
            if (VtNative.VTDecompressionSessionCreate(0, _format, specification, attributes, &callback, out _session) != 0)
            {
                _session = 0;
                if (_self.IsAllocated) _self.Free();
                return false;
            }
            _dirty = false;
            return true;
        }
        catch { ReleaseSession(); throw; }
        finally { if (specification != 0) VtNative.CFRelease(specification); }
    }

    [UnmanagedCallersOnly]
    private static void OnFrame(nint refCon, nint source, int status, uint flags, nint image, CmTime time, CmTime duration)
    {
        var decoder = (VideoToolboxVideoDecoder)GCHandle.FromIntPtr(refCon).Target!;
        try
        {
            VtNative.Check(status, "output callback");
            if (image == 0) return;
            if (VtNative.CVPixelBufferGetPixelFormatType(image) == VtNative.Bgra)
            {
                decoder.EnqueueExportFrame(image, time.Ticks);
                return;
            }
            if (decoder._frames.Count >= 32) throw new InvalidDataException("Too many pending VideoToolbox frames.");
            decoder._frames.Enqueue(CopyFrame(image, time.Ticks));
        }
        catch (Exception exception) { Interlocked.CompareExchange(ref decoder._error, ExceptionDispatchInfo.Capture(exception), null); }
    }

    /// <summary>
    /// Retains an IOSurface-backed BGRA output buffer for presentation. The frame is dropped (without
    /// taking a reference) when export is off or the presenter queue is full, so VideoToolbox's decoder
    /// pool keeps cycling.
    /// <para>为呈现保留一个 IOSurface 支撑的 BGRA 输出缓冲。导出关闭或呈现队列已满时直接丢帧（不取引用），
    /// 让 VideoToolbox 的解码缓冲池继续轮转。</para>
    /// </summary>
    private void EnqueueExportFrame(nint image, long timestamp)
    {
        if (!ExportActive || _exportFrames.Count >= 8)
        {
            Interlocked.Increment(ref _exportDropped);
            return;
        }
        VtNative.CFRetain(image);
        try
        {
            nint iosurface = VtNative.CVPixelBufferGetIOSurface(image);
            if (iosurface == 0) throw new NotSupportedException("VideoToolbox produced a BGRA buffer without an IOSurface.");
            int width = checked((int)VtNative.CVPixelBufferGetWidth(image));
            int height = checked((int)VtNative.CVPixelBufferGetHeight(image));
            _exportFrames.Enqueue(new IosurfaceVideoFrame(image, iosurface, width, height, timestamp, () => VtNative.CFRelease(image)));
        }
        catch
        {
            VtNative.CFRelease(image);
            Interlocked.Increment(ref _exportDropped);
        }
    }

    private void DeliverQueued()
    {
        while (_exportFrames.TryDequeue(out IosurfaceVideoFrame? exported))
        {
            if (!ExportActive)
            {
                exported.MarkPresented();
                continue;
            }
            _inCallback = true;
            try { _iosurfaceFrameCallback!(exported); }
            catch { exported.MarkPresented(); throw; }
            finally { _inCallback = false; }
        }
        while (_frames.TryDequeue(out DecodedVideoFrame? frame))
        {
            using (frame) Deliver(frame);
        }
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
    private void ClearFrames()
    {
        while (_frames.TryDequeue(out var frame)) frame.Dispose();
        while (_exportFrames.TryDequeue(out var exported)) exported.MarkPresented();
    }
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