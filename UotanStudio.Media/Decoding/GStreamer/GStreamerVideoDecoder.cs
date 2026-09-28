using System.Buffers;
using UotanStudio.Media.GStreamer;

namespace UotanStudio.Media.Decoding.GStreamer;

/// <summary>
/// Packetized GStreamer decoder using explicitly selected hardware or software plugins.
/// <para>GStreamer 分包视频解码器，使用显式选择的硬件或软件插件。</para>
/// </summary>
public sealed unsafe class GStreamerVideoDecoder : IVideoDecoder
{
    private readonly VideoDecoderOptions _options;
    private readonly bool _hardware;
    private GstPipeline? _pipeline;
    private Action<DecodedVideoFrame>? _callback;
    private VideoCodecId _codec;
    private int _width, _height;
    private long _sequence;
    private bool _disposed;
    private bool _inCallback;

    /// <summary>
    /// Creates a GStreamer decoder that follows stream color metadata.
    /// <para>创建遵循码流色彩元数据的 GStreamer 解码器。</para>
    /// </summary>
    /// <param name="hardware">
    /// When <see langword="true"/>, only hardware decoder candidate elements are accepted; software candidates otherwise.
    /// <para>为 <see langword="true"/> 时仅接受硬件解码器候选元素，否则接受软件候选元素。</para>
    /// </param>
    public GStreamerVideoDecoder(bool hardware) : this(hardware, new VideoDecoderOptions()) { }

    /// <summary>
    /// Creates a GStreamer decoder with per-session color overrides forced onto the NV12 output caps.
    /// <para>创建带会话色彩覆盖的 GStreamer 解码器，覆盖会强制写入 NV12 输出 caps。</para>
    /// </summary>
    /// <param name="hardware">
    /// When <see langword="true"/>, only hardware decoder candidate elements are accepted; software candidates otherwise.
    /// <para>为 <see langword="true"/> 时仅接受硬件解码器候选元素，否则接受软件候选元素。</para>
    /// </param>
    /// <param name="options">
    /// Session choices; must not be <see langword="null"/>. Color overrides are forced onto the output
    /// caps; other fields are ignored by this backend.
    /// <para>会话选择；不得为 <see langword="null"/>。色彩覆盖强制写入输出 caps；其他字段被此后端忽略。</para>
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.
    /// <para><paramref name="options"/> 为 <see langword="null"/> 时抛出。</para></exception>
    public GStreamerVideoDecoder(bool hardware, VideoDecoderOptions options)
    {
        _hardware = hardware;
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }
    /// <summary>
    /// Selected native decoder element.
    /// <para>实际选用的原生解码器元素。</para>
    /// </summary>
    /// <value>
    /// The GStreamer element name chosen by the last successful <see cref="Initialize"/>; empty before that.
    /// <para>最近一次成功 <see cref="Initialize"/> 选用的 GStreamer 元素名称；在此之前为空。</para>
    /// </value>
    public string DecoderElement { get; private set; } = string.Empty;
    /// <summary>
    /// Whether this instance requires a hardware plugin.
    /// <para>本实例是否要求硬件插件。</para>
    /// </summary>
    /// <value>
    /// <see langword="true"/> when only hardware decoder candidates are accepted; fixed at construction.
    /// <para>为 <see langword="true"/> 时仅接受硬件解码器候选；该值在构造时固定。</para>
    /// </value>
    public bool UsesHardware => _hardware;
    /// <summary>
    /// Gets a value indicating whether a GStreamer pipeline is currently built.
    /// <para>获取一个值，该值指示当前是否已构建 GStreamer 管线。</para>
    /// </summary>
    /// <value>
    /// <see langword="true"/> after a successful <see cref="Initialize"/> until <see cref="Dispose"/> or a re-initialization.
    /// <para>在 <see cref="Initialize"/> 成功后为 <see langword="true"/>，直到 <see cref="Dispose"/> 或重新初始化。</para>
    /// </value>
    public bool IsInitialized => _pipeline is not null;
    internal static string[] Candidates(VideoCodecId codec, bool hardware)
    {
        string name = codec switch { VideoCodecId.H264 => "h264", VideoCodecId.H265 => "h265", VideoCodecId.Av1 => "av1", _ => throw new ArgumentOutOfRangeException(nameof(codec)) };
        if (!hardware) return codec switch { VideoCodecId.H264 => ["avdec_h264", "openh264dec"], VideoCodecId.H265 => ["avdec_h265"], _ => ["dav1ddec", "av1dec", "avdec_av1"] };
        if (OperatingSystem.IsWindows()) return [$"d3d11{name}dec", $"d3d12{name}dec", $"nv{name}dec"];
        if (OperatingSystem.IsMacOS()) return ["vtdec_hw"];
        return [$"va{name}dec", $"vaapi{name}dec", $"nv{name}dec", $"v4l2sl{name}dec"];
    }

    /// <summary>
    /// Builds the numeric GStreamer colorimetry string ("range:matrix:transfer:primaries") for the
    /// session overrides. A zero component keeps the stream value untouched, matching
    /// <c>gst_video_colorimetry_from_string</c>; matrix values follow
    /// <see cref="VideoColorMatrix"/>/GstVideoColorMatrix (BT.709 = 3, BT.601 = 4).
    /// <para>按会话覆盖构建数值形式的 GStreamer colorimetry 串（"range:matrix:transfer:primaries"）。零分量保持码流值不变，
    /// 与 <c>gst_video_colorimetry_from_string</c> 语义一致；矩阵取值遵循 <see cref="VideoColorMatrix"/>/GstVideoColorMatrix（BT.709 = 3，BT.601 = 4）。</para>
    /// </summary>
    /// <param name="matrix">Session matrix override. <para>会话矩阵覆盖。</para></param>
    /// <param name="range">Session range override. <para>会话范围覆盖。</para></param>
    /// <returns>
    /// The colorimetry string for the output caps, or <see langword="null"/> when both overrides are Auto.
    /// <para>输出 caps 的 colorimetry 串；两个覆盖均为 Auto 时返回 <see langword="null"/>。</para>
    /// </returns>
    internal static string? ColorimetryFor(VideoColorMatrix matrix, VideoColorRange range)
    {
        int r = range switch { VideoColorRange.Full => 1, VideoColorRange.Limited => 2, _ => 0 };
        int m = matrix switch { VideoColorMatrix.Bt709 => 3, VideoColorMatrix.Bt601 => 4, _ => 0 };
        if (r == 0 && m == 0) return null;
        (int t, int p) = m switch { 3 => (5, 1), 4 => (4, 4), _ => (0, 0) };
        return $"{r}:{m}:{t}:{p}";
    }
    /// <summary>
    /// Checks installed plugin registration; stream and device initialization may still fail.
    /// <para>检查已安装插件的注册情况；码流与设备初始化仍可能失败。</para>
    /// </summary>
    /// <param name="codec">Codec whose candidate element names are probed. <para>要探测候选元素名称的编解码器。</para></param>
    /// <param name="hardware">Whether only hardware decoder elements qualify; <see langword="false"/> probes software elements.
    /// <para>是否仅硬件解码器元素符合条件；<see langword="false"/> 表示探测软件元素。</para></param>
    /// <returns>
    /// <see langword="true"/> when GStreamer initializes and at least one candidate element is registered; never throws for missing native libraries.
    /// <para>当 GStreamer 初始化成功且至少注册了一个候选元素时返回 <see langword="true"/>；缺少原生库时不会抛出异常。</para>
    /// </returns>
    public static bool IsAvailable(VideoCodecId codec, bool hardware)
    {
        try
        {
            GstNative.Initialize();
            return Candidates(codec, hardware).Any(GstNative.HasElement);
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or NotSupportedException or TypeInitializationException) { return false; }
    }
    /// <summary>
    /// Builds an appsrc-to-appsink pipeline for the codec, trying registered candidate decoders until one accepts the NV12 layout.
    /// <para>为该编解码器构建从 appsrc 到 appsink 的管线，依次尝试已注册的候选解码器，直到某个解码器接受 NV12 输出布局。</para>
    /// </summary>
    /// <param name="codec">H.264, H.265 or AV1. <para>H.264、H.265 或 AV1。</para></param>
    /// <param name="width">Expected frame width in pixels. <para>预期帧宽度（像素）。</para></param>
    /// <param name="height">Expected frame height in pixels. <para>预期帧高度（像素）。</para></param>
    /// <param name="frameCallback">
    /// Invoked for every decoded NV12 frame on the calling thread; the frame is disposed after the callback
    /// returns, so consumers must copy or convert within it. Reentering the decoder from the callback is forbidden.
    /// <para>每解出一帧 NV12 即在调用线程上触发；回调返回后该帧即被释放，消费方须在回调内完成复制或转换。
    /// 禁止在回调中重入解码器。</para>
    /// </param>
    /// <exception cref="ObjectDisposedException">The decoder was disposed. <para>解码器已被释放。</para></exception>
    /// <exception cref="ArgumentNullException"><c>frameCallback</c> is null.
    /// <para><c>frameCallback</c> 为 null。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><c>codec</c> is unsupported or dimensions exceed the limits.
    /// <para><c>codec</c> 不受支持，或尺寸超出上限。</para></exception>
    /// <exception cref="InvalidOperationException">The call reenters the decoder from a callback.
    /// <para>调用从回调中重入了解码器。</para></exception>
    /// <exception cref="NotSupportedException">No registered candidate decoder accepts the codec, or GStreamer is unavailable.
    /// <para>没有已注册的候选解码器接受该编解码器，或 GStreamer 不可用。</para></exception>
    public void Initialize(VideoCodecId codec, int width, int height, Action<DecodedVideoFrame> frameCallback)
    {
        if (_inCallback) throw new InvalidOperationException("Decoder callbacks cannot reenter the decoder.");
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(frameCallback);
        VideoFrameLimits.ValidateDimensions(width, height);
        _pipeline?.Dispose(); _pipeline = null;
        GstNative.Initialize();
        _codec = codec; _width = width; _height = height; _callback = frameCallback; _sequence = 0;
        string name = codec switch { VideoCodecId.H264 => "h264", VideoCodecId.H265 => "h265", VideoCodecId.Av1 => "av1", _ => throw new ArgumentOutOfRangeException(nameof(codec)) };
        string format = codec == VideoCodecId.Av1 ? "obu-stream,alignment=tu" : "byte-stream,alignment=au";
        var errors = new List<string>();
        string? colorimetry = ColorimetryFor(_options.ColorMatrix, _options.ColorRange);
        string outputCaps = $"video/x-raw,format=NV12{(colorimetry is null ? "" : $",colorimetry={colorimetry}")}";
        foreach (string candidate in Candidates(codec, _hardware))
        {
            if (!GstNative.HasElement(candidate)) continue;
            try
            {
                _pipeline = new GstPipeline($"appsrc name=input is-live=true format=time block=false max-bytes={GstPipeline.MaximumQueuedBytes} caps=video/x-{name},stream-format={format},width={width},height={height} ! {name}parse ! {candidate} ! video/x-raw ! videoconvert ! {outputCaps} ! appsink name=output sync=false max-buffers=8 drop=false enable-last-sample=false");
                DecoderElement = candidate;
                return;
            }
            catch (Exception error) when (error is NotSupportedException or InvalidOperationException) { errors.Add($"{candidate}: {error.Message}"); }
        }
        throw new NotSupportedException($"No GStreamer {(_hardware ? "hardware" : "software")} decoder accepts {codec}. {string.Join("; ", errors)}");
    }
    /// <summary>
    /// Feeds one compressed access unit into the pipeline with a synthesized 60 fps presentation timestamp.
    /// <para>将一个压缩访问单元送入管线，时间戳为合成的 60 fps 递进值。</para>
    /// </summary>
    /// <param name="accessUnit">
    /// One complete access unit (H.264/H.265 in Annex-B, AV1 in OBU stream form).
    /// <para>一个完整的访问单元（H.264/H.265 为 Annex-B 形式，AV1 为 OBU 流形式）。</para>
    /// </param>
    /// <exception cref="InvalidOperationException">The decoder is not initialized or the call reenters from a callback.
    /// <para>解码器未初始化，或调用从回调中重入。</para></exception>
    /// <exception cref="ObjectDisposedException">The decoder was disposed. <para>解码器已被释放。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">The access unit is empty or exceeds 32 MiB.
    /// <para>访问单元为空或超过 32 MiB。</para></exception>
    /// <exception cref="InvalidDataException">The pipeline output had an unexpected layout or produced excessive frames.
    /// <para>管线输出布局异常，或产出帧数过多。</para></exception>
    public void Decode(ReadOnlySpan<byte> accessUnit) => Decode(accessUnit, checked(_sequence++ * 166667));
    /// <summary>
    /// Feeds one compressed access unit with an explicit presentation timestamp in TimeSpan ticks.
    /// <para>送入一个带显式呈现时间戳（TimeSpan 刻度）的压缩访问单元。</para>
    /// </summary>
    /// <param name="accessUnit">
    /// One complete access unit (H.264/H.265 in Annex-B, AV1 in OBU stream form).
    /// <para>一个完整的访问单元（H.264/H.265 为 Annex-B 形式，AV1 为 OBU 流形式）。</para>
    /// </param>
    /// <param name="timestampTicks">Presentation timestamp in TimeSpan ticks, attached to the pushed buffer.
    /// <para>以 TimeSpan 刻度表示的呈现时间戳，将附加到推入的缓冲区上。</para></param>
    /// <exception cref="InvalidOperationException">The decoder is not initialized or the call reenters from a callback.
    /// <para>解码器未初始化，或调用从回调中重入。</para></exception>
    /// <exception cref="ObjectDisposedException">The decoder was disposed. <para>解码器已被释放。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">The access unit is empty or exceeds 32 MiB.
    /// <para>访问单元为空或超过 32 MiB。</para></exception>
    /// <exception cref="InvalidDataException">The pipeline output had an unexpected layout or produced excessive frames.
    /// <para>管线输出布局异常，或产出帧数过多。</para></exception>
    public void Decode(ReadOnlySpan<byte> accessUnit, long timestampTicks)
    {
        CheckReady();
        VideoFrameLimits.ValidateAccessUnit(accessUnit);
        _pipeline!.Push(accessUnit, timestampTicks);
        ReadFrames(20_000_000);
    }
    /// <summary>
    /// Delivers frames that finished decoding asynchronously without submitting another access unit.
    /// <para>在不提交新的访问单元的情况下，交付已异步完成解码的帧。</para>
    /// </summary>
    /// <exception cref="InvalidOperationException">The decoder is not initialized or the call reenters from a callback.
    /// <para>解码器未初始化，或调用从回调中重入。</para></exception>
    /// <exception cref="ObjectDisposedException">The decoder was disposed. <para>解码器已被释放。</para></exception>
    /// <exception cref="InvalidDataException">The pipeline output had an unexpected layout or produced excessive frames.
    /// <para>管线输出布局异常，或产出帧数过多。</para></exception>
    public void Poll() { CheckReady(); ReadFrames(0); }
    private void ReadFrames(ulong timeout)
    {
        for (int i = 0; i < 4096; i++)
        {
            nint sample = _pipeline!.Pull(i == 0 ? timeout : 0);
            if (sample == 0) return;
            try { Deliver(sample); }
            finally { GstNative.gst_mini_object_unref(sample); }
        }
        throw new InvalidDataException("Excessive GStreamer output frames.");
    }
    private void Deliver(nint sample)
    {
        nint caps = GstNative.gst_sample_get_caps(sample);
        if (caps == 0) throw new InvalidDataException("Missing video output caps.");
        nint structure = GstNative.gst_caps_get_structure(caps, 0);
        if (GstNative.gst_structure_get_int(structure, "width", out int width) == 0 || GstNative.gst_structure_get_int(structure, "height", out int height) == 0)
            throw new InvalidDataException("Missing video dimensions.");
        VideoFrameLimits.ValidateDimensions(width, height);
        int nativeStride = checked((width + 3) & ~3);
        int nativeYHeight = checked((height + 1) & ~1);
        int nativeSize = checked(nativeStride * (nativeYHeight + (height + 1) / 2));
        nint source = GstNative.gst_sample_get_buffer(sample);
        nint meta = source == 0 ? 0 : GstNative.gst_buffer_get_video_meta(source);
        if (source == 0 || (meta == 0 && GstNative.gst_buffer_get_size(source) != (nuint)nativeSize)) throw new InvalidDataException("Unexpected GStreamer NV12 memory layout.");
        int stride = (width + 1) & ~1;
        int size = VideoFrameLimits.Nv12Length(width, height, stride);
        byte[] pixels = ArrayPool<byte>.Shared.Rent(size);
        try
        {
            fixed (byte* target = pixels)
            {
                if (meta != 0)
                {
                    CopyPlane(meta, 0, target, stride, width, height);
                    CopyPlane(meta, 1, target + height * stride, stride, stride, (height + 1) / 2);
                }
                else
                {
                    for (int y = 0; y < height; y++) CopyRow(source, y * nativeStride, target + y * stride, stride);
                    for (int y = 0; y < (height + 1) / 2; y++) CopyRow(source, (nativeYHeight + y) * nativeStride, target + (height + y) * stride, stride);
                }
            }
        }
        catch { ArrayPool<byte>.Shared.Return(pixels); throw; }
        var color = new GstNative.Colorimetry();
        nint colorText = GstNative.gst_structure_get_string(structure, "colorimetry");
        if (colorText != 0) GstNative.gst_video_colorimetry_from_string(out color, colorText);
        using var frame = new DecodedVideoFrame(pixels, size, width, height, stride, GstNative.GetTimestamp(source))
        {
            ColorMatrix = color.Matrix switch { 3 => VideoColorMatrix.Bt709, 4 => VideoColorMatrix.Bt601, _ => VideoColorMatrix.Auto },
            ColorRange = color.Range switch { 1 => VideoColorRange.Full, 2 => VideoColorRange.Limited, _ => VideoColorRange.Auto },
        };
        _inCallback = true;
        try { _callback!(frame); } finally { _inCallback = false; }
    }
    private static void CopyPlane(nint meta, uint plane, byte* target, int targetStride, int rowBytes, int rows)
    {
        if (GstNative.gst_video_meta_map(meta, plane, out var map, out nint data, out int stride, 1) == 0)
            throw new InvalidDataException("Cannot map GStreamer video plane.");
        try
        {
            if (Math.Abs((long)stride) < rowBytes) throw new InvalidDataException("Invalid video plane stride.");
            for (int row = 0; row < rows; row++)
            {
                nint address = checked(data + checked(row * stride));
                long offset = checked((long)address - (long)map.Data);
                if (offset < 0 || (ulong)offset + (uint)rowBytes > (ulong)map.Size) throw new InvalidDataException("Video plane exceeds mapped memory.");
                new ReadOnlySpan<byte>((void*)address, rowBytes).CopyTo(new Span<byte>(target + row * targetStride, rowBytes));
                if (targetStride > rowBytes) new Span<byte>(target + row * targetStride + rowBytes, targetStride - rowBytes).Clear();
            }
        }
        finally { GstNative.gst_video_meta_unmap(meta, plane, ref map); }
    }
    private static void CopyRow(nint source, int offset, byte* target, int bytes)
    {
        if (GstNative.gst_buffer_extract(source, (nuint)offset, target, (nuint)bytes) != (nuint)bytes) throw new InvalidDataException("Truncated video plane.");
    }
    /// <summary>
    /// Signals end of stream and keeps pulling output until the pipeline reports EOS within five seconds.
    /// <para>发出流结束信号，并持续拉取输出，直到管线在 5 秒内报告 EOS。</para>
    /// </summary>
    /// <exception cref="InvalidOperationException">The decoder is not initialized or the call reenters from a callback.
    /// <para>解码器未初始化，或调用从回调中重入。</para></exception>
    /// <exception cref="ObjectDisposedException">The decoder was disposed. <para>解码器已被释放。</para></exception>
    /// <exception cref="TimeoutException">EOS was not reached within five seconds.
    /// <para>5 秒内未到达 EOS。</para></exception>
    /// <exception cref="InvalidDataException">The pipeline output had an unexpected layout or produced excessive frames.
    /// <para>管线输出布局异常，或产出帧数过多。</para></exception>
    public void Drain()
    {
        CheckReady();
        _pipeline!.End();
        long deadline = Environment.TickCount64 + 5000;
        do
        {
            ReadFrames(10_000_000);
            if (_pipeline.IsEos) return;
        } while (Environment.TickCount64 < deadline);
        throw new TimeoutException("GStreamer drain exceeded five seconds.");
    }
    /// <summary>
    /// Rebuilds the pipeline for the same codec, size and callback after a resolution change or stream discontinuity.
    /// <para>在分辨率变更或码流不连续后，以相同的编解码器、尺寸与回调重建管线。</para>
    /// </summary>
    /// <exception cref="InvalidOperationException">The decoder is not initialized or the call reenters from a callback.
    /// <para>解码器未初始化，或调用从回调中重入。</para></exception>
    /// <exception cref="ObjectDisposedException">The decoder was disposed. <para>解码器已被释放。</para></exception>
    /// <exception cref="NotSupportedException">No registered candidate decoder accepts the codec.
    /// <para>没有已注册的候选解码器接受该编解码器。</para></exception>
    public void Reset() { CheckReady(); Initialize(_codec, _width, _height, _callback!); }
    private void CheckReady()
    {
        if (_inCallback) throw new InvalidOperationException("Decoder callbacks cannot reenter the decoder.");
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pipeline is null) throw new InvalidOperationException("Decoder is not initialized.");
    }
    /// <summary>
    /// Tears down the GStreamer pipeline and drops the callback; repeat calls are no-ops and the decoder cannot be reused.
    /// <para>拆除 GStreamer 管线并卸下回调；重复调用不执行任何操作，且解码器不可再复用。</para>
    /// </summary>
    /// <exception cref="InvalidOperationException">The call comes from a decoder callback.
    /// <para>调用来自解码器回调。</para></exception>
    public void Dispose() { if (_inCallback) throw new InvalidOperationException("Decoder callbacks cannot dispose the decoder."); if (_disposed) return; _pipeline?.Dispose(); _pipeline = null; _callback = null; _disposed = true; }
}