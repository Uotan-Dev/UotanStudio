using System.Buffers;
using UotanStudio.Media.Decoding;

namespace UotanStudio.Media.Acceleration.Linux;

/// <summary>
/// Linux VA-API hardware decoder using the versioned uotan_media_va bridge and libavcodec.
/// Supports 8-bit 4:2:0 H.264/HEVC/AV1 when the selected driver exposes the corresponding profile.
/// <para>基于带版本的 uotan_media_va 桥接与 libavcodec 的 Linux VA-API 硬件解码器。
/// 当所选驱动暴露对应 profile 时，支持 8 位 4:2:0 的 H.264/HEVC/AV1。</para>
/// </summary>
/// <remarks>
/// Requires libuotan_media_va.so, libavcodec/libavutil and a VA driver; does not require GStreamer.
/// Software decoding is forbidden. Complete access units are decoded in display order with their
/// original PTS. NV12 is downloaded through the driver and copied into callback-scoped pooled frames.
/// Calls must use one owning thread. Stream failures require Reset or reinitialization.
/// <para>
/// 需要 libuotan_media_va.so、libavcodec/libavutil 与一个 VA 驱动；不依赖 GStreamer。
/// 禁止软件解码。完整的访问单元按显示顺序以其原始 PTS 解码。NV12 经由驱动下载，
/// 并复制到仅在回调期间有效的池化帧中。所有调用必须在同一所属线程上执行。
/// 码流出错后必须先 Reset 或重新初始化。
/// </para>
/// </remarks>
public sealed class VaApiVideoDecoder : IVideoDecoder
{
    private readonly VideoDecoderOptions _options;
    private readonly Func<VideoCodecId, string?, IVaCodecSession> _open;
    private IVaCodecSession? _session;
    private Action<DecodedVideoFrame>? _callback;
    private VideoCodecId _codec;
    private int _thread;
    private bool _disposed, _inCallback, _faulted, _ended;
    private byte[]? _configuration;
    private long _sequence;

    /// <summary>
    /// Creates an uninitialized hardware-only VA-API decoder.
    /// <para>创建未初始化的纯硬件 VA-API 解码器。</para>
    /// </summary>
    public VaApiVideoDecoder() : this(new VideoDecoderOptions()) { }

    /// <summary>
    /// Creates a decoder with an optional DRM render node and color overrides.
    /// <para>创建解码器，可指定 DRM 渲染节点与色彩覆盖。</para>
    /// </summary>
    /// <param name="options">Render node selection and color overrides; must not be null.
    /// <para>渲染节点选择与色彩覆盖；不得为 null。</para></param>
    /// <exception cref="ArgumentNullException"><c>options</c> is null. <para><c>options</c> 为 null。</para></exception>
    public VaApiVideoDecoder(VideoDecoderOptions options) : this(options, VaCodecNative.Open) { }

    internal VaApiVideoDecoder(VideoDecoderOptions options, Func<VideoCodecId, string?, IVaCodecSession> open)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _open = open;
    }

    /// <summary>
    /// Gets a value indicating whether a native VA session is currently open.
    /// <para>获取一个值，该值指示当前是否已打开原生 VA 会话。</para>
    /// </summary>
    /// <value>
    /// <see langword="true"/> after a successful <see cref="Initialize"/> until <see cref="Dispose"/> or a re-initialization.
    /// <para>在 <see cref="Initialize"/> 成功后为 <see langword="true"/>，直到 <see cref="Dispose"/> 或重新初始化。</para>
    /// </value>
    public bool IsInitialized => _session is not null;

    /// <summary>
    /// Checks native codec and driver VLD capability; stream-specific profile support still needs decoding.
    /// <para>检查原生编解码器与驱动的 VLD 能力；具体码流的 profile 支持仍需实际解码验证。</para>
    /// </summary>
    /// <param name="codec">Codec to probe; only H.264, H.265 and AV1 are supported.
    /// <para>要探测的编解码器；仅支持 H.264、H.265 与 AV1。</para></param>
    /// <param name="renderNode">Absolute DRM render node path, or <see langword="null"/> for the driver default.
    /// <para>DRM 渲染节点绝对路径，或 <see langword="null"/> 表示使用驱动默认值。</para></param>
    /// <returns>
    /// <see langword="true"/> on Linux when a native session opens for the codec; otherwise <see langword="false"/>.
    /// <para>在 Linux 上且原生会话能对该编解码器打开时返回 <see langword="true"/>；否则返回 <see langword="false"/>。</para>
    /// </returns>
    public static bool IsSupported(VideoCodecId codec, string? renderNode = null)
    {
        if (!OperatingSystem.IsLinux() || codec is not (VideoCodecId.H264 or VideoCodecId.H265 or VideoCodecId.Av1)) return false;
        try { using var session = VaCodecNative.Open(codec, renderNode); return true; }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or NotSupportedException or IOException or InvalidOperationException)
        { return false; }
    }

    /// <summary>
    /// Opens the native VA session and decoder pipeline for the given codec and frame size, replacing any prior session.
    /// <para>为指定编解码器与帧尺寸打开原生 VA 会话与解码管线，并替换之前的任何会话。</para>
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
    /// <exception cref="ArgumentException">The configured render node is not an absolute device path.
    /// <para>配置的渲染节点不是绝对设备路径。</para></exception>
    /// <exception cref="InvalidOperationException">A native session cannot be opened or the call is on the wrong thread.
    /// <para>无法打开原生会话，或调用线程不正确。</para></exception>
    public void Initialize(VideoCodecId codec, int width, int height, Action<DecodedVideoFrame> frameCallback)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CheckThread();
        VideoFrameLimits.ValidateDimensions(width, height);
        ArgumentNullException.ThrowIfNull(frameCallback);
        if (codec is not (VideoCodecId.H264 or VideoCodecId.H265 or VideoCodecId.Av1)) throw new ArgumentOutOfRangeException(nameof(codec));
        if (_options.VaApiRenderNode is { } node && (string.IsNullOrWhiteSpace(node) || !Path.IsPathFullyQualified(node)))
            throw new ArgumentException("VA-API render node must be an absolute device path.", nameof(_options));
        _session?.Dispose();
        _session = null;
        _callback = null;
        _configuration = null;
        _thread = Environment.CurrentManagedThreadId;
        _codec = codec;
        _sequence = 0;
        _faulted = _ended = false;
        _session = _open(codec, _options.VaApiRenderNode);
        _callback = frameCallback;
    }

    /// <summary>
    /// Feeds one compressed access unit into the decoder with a synthesized 60 fps presentation timestamp.
    /// <para>将一个压缩访问单元送入解码器，时间戳为合成的 60 fps 递进值。</para>
    /// </summary>
    /// <param name="accessUnit">
    /// One complete access unit (H.264/H.265 in Annex-B, AV1 in OBU stream form).
    /// <para>一个完整的访问单元（H.264/H.265 为 Annex-B 形式，AV1 为 OBU 流形式）。</para>
    /// </param>
    /// <exception cref="ObjectDisposedException">The decoder was disposed. <para>解码器已被释放。</para></exception>
    /// <exception cref="InvalidOperationException">The decoder is not initialized, has faulted, was drained, or the call is on the wrong thread.
    /// <para>解码器未初始化、已出错、已排空，或调用线程不正确。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">The access unit is empty or exceeds 32 MiB.
    /// <para>访问单元为空或超过 32 MiB。</para></exception>
    /// <exception cref="InvalidDataException">The stream was rejected by the native decoder.
    /// <para>码流被原生解码器拒绝。</para></exception>
    public void Decode(ReadOnlySpan<byte> accessUnit) => Decode(accessUnit, checked(_sequence++ * 166667));

    /// <summary>
    /// Feeds one compressed access unit with an explicit presentation timestamp in TimeSpan ticks.
    /// Non-picture units are buffered and prepended to the next picture-bearing unit.
    /// <para>送入一个带显式呈现时间戳（TimeSpan 刻度）的压缩访问单元。不含图像的单元会被缓存，并拼接到下一个含图像的单元之前。</para>
    /// </summary>
    /// <param name="accessUnit">
    /// One complete access unit (H.264/H.265 in Annex-B, AV1 in OBU stream form).
    /// <para>一个完整的访问单元（H.264/H.265 为 Annex-B 形式，AV1 为 OBU 流形式）。</para>
    /// </param>
    /// <param name="timestampTicks">Presentation timestamp in TimeSpan ticks, carried through to the output frames.
    /// <para>以 TimeSpan 刻度表示的呈现时间戳，会传递到输出帧。</para></param>
    /// <exception cref="ObjectDisposedException">The decoder was disposed. <para>解码器已被释放。</para></exception>
    /// <exception cref="InvalidOperationException">The decoder is not initialized, has faulted, was drained, or the call is on the wrong thread.
    /// <para>解码器未初始化、已出错、已排空，或调用线程不正确。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">The access unit is empty or exceeds 32 MiB.
    /// <para>访问单元为空或超过 32 MiB。</para></exception>
    /// <exception cref="InvalidDataException">The stream was truncated, exceeds 32 MiB after buffering, or was rejected.
    /// <para>码流被截断、缓存后超过 32 MiB，或被拒绝。</para></exception>
    public void Decode(ReadOnlySpan<byte> accessUnit, long timestampTicks)
    {
        CheckReady();
        if (_ended) throw new InvalidOperationException("Reset the decoder after draining before sending new input.");
        VideoFrameLimits.ValidateAccessUnit(accessUnit);
        try
        {
            bool hasPicture = _codec == VideoCodecId.Av1 && Av1Obu.ContainsFrame(accessUnit);
            if (_codec != VideoCodecId.Av1)
            {
                foreach (Range range in AnnexB.Split(accessUnit))
                {
                    ReadOnlySpan<byte> nal = accessUnit[range];
                    if (_codec == VideoCodecId.H265 && nal.Length < 2) throw new InvalidDataException("Truncated HEVC NAL header.");
                    int type = _codec == VideoCodecId.H264 ? nal[0] & 31 : (nal[0] >> 1) & 63;
                    hasPicture |= _codec == VideoCodecId.H264 ? type is >= 1 and <= 5 : type <= 31;
                }
            }
            byte[]? combined = null;
            if (_configuration is not null)
            {
                int length = checked(_configuration.Length + accessUnit.Length);
                if (length > VideoFrameLimits.MaximumAccessUnitBytes) throw new InvalidDataException("Pending access unit exceeds 32 MiB.");
                combined = new byte[length];
                _configuration.CopyTo(combined, 0);
                accessUnit.CopyTo(combined.AsSpan(_configuration.Length));
                accessUnit = combined;
            }
            if (!hasPicture) { _configuration = combined ?? accessUnit.ToArray(); return; }
            _configuration = null;
            int status = _session!.Send(accessUnit, timestampTicks);
            if (status == 2)
            {
                ReadFrames(out int frames);
                if (frames == 0) throw new InvalidDataException("VA-API decoder made no progress before retry.");
                status = _session.Send(accessUnit, timestampTicks);
            }
            if (status != 0) throw new InvalidDataException("VA-API decoder did not accept the access unit.");
            ReadFrames(out _);
        }
        catch { _faulted = true; throw; }
    }

    /// <summary>
    /// Delivers frames that finished decoding asynchronously without submitting another access unit.
    /// <para>在不提交新的访问单元的情况下，交付已异步完成解码的帧。</para>
    /// </summary>
    /// <exception cref="ObjectDisposedException">The decoder was disposed. <para>解码器已被释放。</para></exception>
    /// <exception cref="InvalidOperationException">The decoder is not initialized, has faulted, or the call is on the wrong thread.
    /// <para>解码器未初始化、已出错，或调用线程不正确。</para></exception>
    /// <exception cref="InvalidDataException">A queued output frame had an invalid layout.
    /// <para>队列中的输出帧布局无效。</para></exception>
    public void Poll()
    {
        CheckReady();
        try { ReadFrames(out _); }
        catch { _faulted = true; throw; }
    }

    /// <summary>
    /// Signals end of stream and delivers all delayed output frames; further input requires <see cref="Reset"/>.
    /// Calling an already-drained decoder is a no-op.
    /// <para>发出流结束信号并交付所有延迟的输出帧；继续输入前必须先 <see cref="Reset"/>。对已排空的解码器重复调用不执行任何操作。</para>
    /// </summary>
    /// <exception cref="ObjectDisposedException">The decoder was disposed. <para>解码器已被释放。</para></exception>
    /// <exception cref="InvalidOperationException">The decoder is not initialized, has faulted, or the call is on the wrong thread.
    /// <para>解码器未初始化、已出错，或调用线程不正确。</para></exception>
    /// <exception cref="InvalidDataException">The native decoder rejected EOS or did not finish draining.
    /// <para>原生解码器拒绝了 EOS，或未能完成排空。</para></exception>
    public void Drain()
    {
        CheckReady();
        if (_ended) return;
        try
        {
            int status = _session!.Send([], 0);
            if (status == 2) { ReadFrames(out _); status = _session.Send([], 0); }
            if (status is not (0 or 3)) throw new InvalidDataException("VA-API decoder rejected EOS.");
            if (ReadFrames(out _) != 3) throw new InvalidDataException("VA-API decoder did not finish draining.");
            _configuration = null;
            _ended = true;
        }
        catch { _faulted = true; throw; }
    }

    private int ReadFrames(out int count)
    {
        for (count = 0; count < 4096; count++)
        {
            int status = _session!.Receive(out VaFrameInfo info);
            if (status is 0 or 3) return status;
            if (status != 1) throw new InvalidDataException("Unexpected VA-API receive status.");
            int length = VideoFrameLimits.Nv12Length(info.Width, info.Height, info.Stride);
            if (info.Length != length || info.Matrix is < 0 or > 2 || info.Range is < 0 or > 2)
                throw new InvalidDataException("Invalid VA-API output layout or color metadata.");
            byte[] buffer = ArrayPool<byte>.Shared.Rent(length);
            try { _session.Copy(buffer.AsSpan(0, length)); }
            catch { ArrayPool<byte>.Shared.Return(buffer); throw; }
            using var frame = new DecodedVideoFrame(buffer, length, info.Width, info.Height, info.Stride, info.Timestamp)
            {
                ColorMatrix = _options.ColorMatrix != VideoColorMatrix.Auto ? _options.ColorMatrix : (VideoColorMatrix)info.Matrix,
                ColorRange = _options.ColorRange != VideoColorRange.Auto ? _options.ColorRange : (VideoColorRange)info.Range,
            };
            _inCallback = true;
            try { _callback!(frame); } finally { _inCallback = false; }
        }
        throw new InvalidDataException("Excessive VA-API output frames.");
    }

    /// <summary>
    /// Clears native decoder state after a stream failure, resolution change or discontinuity;
    /// the decoder stays initialized with the same codec and callback, and this is allowed even when faulted.
    /// <para>在码流出错、分辨率变更或不连续后清除原生解码器状态；解码器以相同的编解码器与回调保持已初始化，
    /// 出错状态下调用同样被允许。</para>
    /// </summary>
    /// <exception cref="ObjectDisposedException">The decoder was disposed. <para>解码器已被释放。</para></exception>
    /// <exception cref="InvalidOperationException">The decoder is not initialized or the call is on the wrong thread.
    /// <para>解码器未初始化或调用线程不正确。</para></exception>
    public void Reset()
    {
        CheckReady(allowFault: true);
        _session!.Reset();
        _faulted = _ended = false;
        _configuration = null;
        _sequence = 0;
    }

    private void CheckReady(bool allowFault = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CheckThread();
        if (_session is null) throw new InvalidOperationException("Decoder is not initialized.");
        if (_faulted && !allowFault) throw new InvalidOperationException("Reset or reinitialize the decoder after a stream failure.");
    }

    private void CheckThread()
    {
        if (_inCallback) throw new InvalidOperationException("Decoder callbacks cannot reenter the decoder.");
        if (_thread != 0 && _thread != Environment.CurrentManagedThreadId) throw new InvalidOperationException("Decoder belongs to another thread.");
    }

    /// <summary>
    /// Closes the native session and drops the callback; repeat calls are no-ops and the decoder cannot be reused.
    /// <para>关闭原生会话并卸下回调；重复调用不执行任何操作，且解码器不可再复用。</para>
    /// </summary>
    /// <exception cref="InvalidOperationException">The call comes from a decoder callback or another thread.
    /// <para>调用来自解码器回调或其他线程。</para></exception>
    public void Dispose()
    {
        if (_disposed) return;
        CheckThread();
        _session?.Dispose();
        _session = null;
        _callback = null;
        _configuration = null;
        _disposed = true;
    }
}