using UotanStudio.Media.Acceleration.Linux;

namespace UotanStudio.Media.Decoding;

/// <summary>
/// Creates AOT-safe platform decoders and probes native codec availability.
/// <para>创建 AOT 安全的平台解码器，并探测原生编解码器可用性。</para>
/// </summary>
public static class VideoDecoderFactory
{
    /// <summary>
    /// Checks whether a backend accepts the codec with a 1920x1080 stream configuration.
    /// <para>检查后端是否以 1920x1080 流配置接受该编码格式。</para>
    /// </summary>
    /// <param name="codec">
    /// Codec to probe; only H.264, H.265 and AV1 are considered, everything else reports unsupported.
    /// <para>要探测的编码格式；仅考虑 H.264、H.265 与 AV1，其余一律视为不支持。</para>
    /// </param>
    /// <returns>
    /// <see langword="true"/> when a platform backend answers the probe; missing native libraries and
    /// initialization failures are swallowed and reported as <see langword="false"/>.
    /// <para>当某个平台后端通过探测时返回 <see langword="true"/>；缺失原生库与初始化失败会被吞掉并按 <see langword="false"/> 报告。</para>
    /// </returns>
    public static bool IsSupported(VideoCodecId codec)
    {
        if (codec is not (VideoCodecId.H264 or VideoCodecId.H265 or VideoCodecId.Av1)) return false;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var decoder = Create(codec, new VideoDecoderOptions());
                decoder.Initialize(codec, 1920, 1080, static _ => { });
                return true;
            }
            if (OperatingSystem.IsLinux()) return VaApiVideoDecoder.IsSupported(codec) || GStreamer.GStreamerVideoDecoder.IsAvailable(codec, true) || GStreamer.GStreamerVideoDecoder.IsAvailable(codec, false);
            if (OperatingSystem.IsMacOS())
                return codec == VideoCodecId.Av1 ? GStreamer.GStreamerVideoDecoder.IsAvailable(codec, true) : VideoToolbox.VtNative.VTIsHardwareDecodeSupported(VideoToolbox.VtNative.Codec(codec)) != 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or NotSupportedException or InvalidOperationException)
        {
            return false;
        }
        return false;
    }

    /// <summary>
    /// Creates an uninitialized platform decoder.
    /// <para>创建未初始化的平台解码器。</para>
    /// </summary>
    /// <returns>
    /// A decoder for the platform default backend, ready for <c>Initialize</c>.
    /// <para>面向平台默认后端的解码器，可直接调用 <c>Initialize</c>。</para>
    /// </returns>
    /// <remarks>
    /// <para>
    /// On Windows the GPU path (D3D11-aware MFT + D3D11 video processor) is tried
    /// first; the synchronous CPU decoder is the fallback when no suitable hardware MFT exists.
    /// </para>
    /// <para>
    /// Windows 上优先尝试 GPU 路径（D3D11 感知 MFT + D3D11 视频处理器）；无合适硬件 MFT 时
    /// 回退到同步 CPU 解码器。
    /// </para>
    /// </remarks>
    public static IVideoDecoder Create() => Create(new VideoDecoderOptions());

    /// <summary>
    /// Creates the requested backend; explicit choices never silently switch.
    /// <para>按用户选择创建后端；显式选择绝不静默切换。</para>
    /// </summary>
    /// <param name="options">
    /// Session choices; only <see cref="VideoDecoderBackend.Auto"/> permits platform-driven fallback (GPU to CPU on Windows, VA-API to GStreamer hardware to GStreamer software on Linux).
    /// <para>会话选项；只有 <see cref="VideoDecoderBackend.Auto"/> 允许按平台回退（Windows 上 GPU 回退 CPU，Linux 上 VA-API 回退 GStreamer 硬件再回退 GStreamer 软件）。</para>
    /// </param>
    /// <returns>
    /// An uninitialized decoder for the selected or composed backend.
    /// <para>所选（或组合出的）后端的未初始化解码器。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="options"/> is null. <para><paramref name="options"/> 为 null。</para>
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The selected backend is unavailable on the current platform. <para>所选后端在当前平台不可用。</para>
    /// </exception>
    public static IVideoDecoder Create(VideoDecoderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.Backend switch
        {
            VideoDecoderBackend.Auto when OperatingSystem.IsWindows() => new FallbackVideoDecoder(
                () => Create(options with { Backend = VideoDecoderBackend.MediaFoundationGpu }),
                () => Create(options with { Backend = VideoDecoderBackend.MediaFoundationCpu })),
            VideoDecoderBackend.Auto when OperatingSystem.IsMacOS() => new VideoToolbox.VideoToolboxVideoDecoder(),
            VideoDecoderBackend.Auto when OperatingSystem.IsLinux() => new FallbackVideoDecoder(
                () => OperatingSystem.IsLinux()
                    ? new VaApiVideoDecoder(options)
                    : throw new NotSupportedException("VA-API requires Linux."),
                () => OperatingSystem.IsLinux()
                    ? new FallbackVideoDecoder(
                        () => new GStreamer.GStreamerVideoDecoder(true, options),
                        () => new GStreamer.GStreamerVideoDecoder(false, options))
                    : throw new NotSupportedException("GStreamer fallback requires Linux.")),
            VideoDecoderBackend.VaApi when OperatingSystem.IsLinux() => new VaApiVideoDecoder(options),
            VideoDecoderBackend.MediaFoundationGpu when OperatingSystem.IsWindows() => new MediaFoundation.MediaFoundationGpuVideoDecoder(options),
            VideoDecoderBackend.MediaFoundationCpu when OperatingSystem.IsWindows() => new MediaFoundation.MediaFoundationVideoDecoder(options),
            VideoDecoderBackend.VideoToolbox when OperatingSystem.IsMacOS() => new VideoToolbox.VideoToolboxVideoDecoder(),
            VideoDecoderBackend.GStreamerHardware => new GStreamer.GStreamerVideoDecoder(true, options),
            VideoDecoderBackend.GStreamerSoftware => new GStreamer.GStreamerVideoDecoder(false, options),
            _ => throw new NotSupportedException("Selected video backend is unavailable on this platform."),
        };
    }

    /// <summary>
    /// Creates a codec-aware platform backend, including AV1 on macOS via GStreamer VideoToolbox.
    /// <para>按编码格式创建平台后端，包括 macOS 上经 GStreamer VideoToolbox 的 AV1。</para>
    /// </summary>
    /// <param name="codec">
    /// Codec the decoder must accept; only H.264, H.265 and AV1 are allowed.
    /// <para>解码器必须支持的编码格式；仅允许 H.264、H.265 与 AV1。</para>
    /// </param>
    /// <param name="options">
    /// Session choices forwarded to <see cref="Create(VideoDecoderOptions)"/>; on macOS an AV1 stream with <c>Auto</c> or <c>VideoToolbox</c> is rerouted to GStreamer hardware.
    /// <para>传递给 <see cref="Create(VideoDecoderOptions)"/> 的会话选项；在 macOS 上，<c>Auto</c> 或 <c>VideoToolbox</c> 后端的 AV1 码流会改由 GStreamer 硬件承载。</para>
    /// </param>
    /// <returns>
    /// An uninitialized decoder able to target the given codec.
    /// <para>可面向给定编码格式的未初始化解码器。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="options"/> is null. <para><paramref name="options"/> 为 null。</para>
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="codec"/> is not H.264, H.265 or AV1. <para><paramref name="codec"/> 不是 H.264、H.265 或 AV1。</para>
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The composed backend is unavailable on the current platform. <para>组合出的后端在当前平台不可用。</para>
    /// </exception>
    public static IVideoDecoder Create(VideoCodecId codec, VideoDecoderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (codec is not (VideoCodecId.H264 or VideoCodecId.H265 or VideoCodecId.Av1)) throw new ArgumentOutOfRangeException(nameof(codec));
        if (OperatingSystem.IsMacOS() && codec == VideoCodecId.Av1 && options.Backend is VideoDecoderBackend.Auto or VideoDecoderBackend.VideoToolbox)
            return new GStreamer.GStreamerVideoDecoder(true, options);

        return Create(options);
    }

    /// <summary>
    /// Resolves the decoder actually in use after initialization: fallback wrappers return their active
    /// inner decoder, everything else returns the argument unchanged.
    /// <para>解析初始化后实际在用的解码器：回退包装器返回其激活的内部解码器，其余原样返回。</para>
    /// </summary>
    /// <param name="decoder">Initialized decoder to inspect. <para>要检查的已初始化解码器。</para></param>
    /// <returns>
    /// The active decoder, or <see langword="null"/> when a fallback wrapper has activated nothing.
    /// <para>激活的解码器；回退包装器尚未激活任何实现时返回 <see langword="null"/>。</para>
    /// </returns>
    public static IVideoDecoder? GetActiveDecoder(IVideoDecoder decoder) =>
        decoder is FallbackVideoDecoder selection ? selection.ActiveDecoder : decoder;

    /// <summary>
    /// Gets the pixel layout the initialized backend delivers on its decode callback.
    /// <para>获取已初始化后端在解码回调上交付的像素布局。</para>
    /// </summary>
    /// <param name="decoder">
    /// Decoder to inspect; fallback wrappers resolve to their active inner decoder.
    /// <para>要检查的解码器；回退包装器会解析到其当前激活的内部解码器。</para>
    /// </param>
    /// <returns>
    /// <see cref="VideoPixelFormat.Bgra32"/> only for a backend whose GPU export is currently active;
    /// <see cref="VideoPixelFormat.Nv12"/> for every readback or download path and before initialization.
    /// Consumers use this instead of matching the display name from <see cref="GetBackendName"/>.
    /// <para>仅当某后端的 GPU 导出当前开启时返回 <see cref="VideoPixelFormat.Bgra32"/>；所有回读或下载路径
    /// 以及未初始化状态返回 <see cref="VideoPixelFormat.Nv12"/>。消费方使用本值，而不是去匹配
    /// <see cref="GetBackendName"/> 的显示名称。</para>
    /// </returns>
    public static VideoPixelFormat GetFramePixelFormat(IVideoDecoder decoder) => GetActiveDecoder(decoder) switch
    {
        MediaFoundation.MediaFoundationGpuVideoDecoder gpu when OperatingSystem.IsWindows() => gpu.FramePixelFormat,
        VideoToolbox.VideoToolboxVideoDecoder toolbox when OperatingSystem.IsMacOS() => toolbox.FramePixelFormat,
        _ => VideoPixelFormat.Nv12,
    };

    /// <summary>
    /// Gets the initialized backend name, including the actual choice after fallback.
    /// <para>获取已初始化后端的名称，包含回退后实际选中的实现。</para>
    /// </summary>
    /// <param name="decoder">
    /// Decoder to describe; fallback wrappers resolve to their active inner decoder.
    /// <para>要描述的解码器；回退包装器会解析到其当前激活的内部解码器。</para>
    /// </param>
    /// <returns>
    /// A display name such as <c>Media Foundation (GPU)</c>; <c>Uninitialized</c> while a fallback wrapper has activated nothing, and <c>Unknown</c> for unrecognised implementations.
    /// <para>用于展示的英文名称，例如 <c>Media Foundation (GPU)</c>；回退包装器尚未激活任何实现时返回 <c>Uninitialized</c>，无法识别的实现返回 <c>Unknown</c>。</para>
    /// </returns>
    public static string GetBackendName(IVideoDecoder decoder)
    {
        if (decoder is FallbackVideoDecoder selection)
            return selection.ActiveDecoder is { } active ? GetBackendName(active) : "Uninitialized";
        return decoder switch
        {
            MediaFoundation.MediaFoundationGpuVideoDecoder => "Media Foundation (GPU)",
            MediaFoundation.MediaFoundationVideoDecoder => "Media Foundation (CPU)",
            VideoToolbox.VideoToolboxVideoDecoder => "VideoToolbox (hardware)",
            GStreamer.GStreamerVideoDecoder gst => $"GStreamer ({gst.PlaneDescription}: {gst.DecoderElement})",
            VaApiVideoDecoder => "VA-API (libavcodec, hardware)",
            _ => "Unknown",
        };
    }

    /// <summary>
    /// Gets why the preferred backend failed during the most recent initialization.
    /// <para>获取首选后端在最近一次初始化中失败的原因。</para>
    /// </summary>
    /// <param name="decoder">
    /// Decoder to inspect; nested fallback wrappers are flattened.
    /// <para>要检查的解码器；嵌套的回退包装器会被展平。</para>
    /// </param>
    /// <returns>
    /// The recorded reason, with nested wrapper reasons joined by <c>"; "</c>, or <see langword="null"/> when the decoder is not a fallback wrapper.
    /// <para>已记录的原因；嵌套包装器的原因以 <c>"; "</c> 连接；解码器不是回退包装器时返回 <see langword="null"/>。</para>
    /// </returns>
    public static string? GetFallbackReason(IVideoDecoder decoder)
    {
        if (decoder is not FallbackVideoDecoder selection) return null;
        var nested = selection.ActiveDecoder is { } active ? GetFallbackReason(active) : null;
        return nested is null ? selection.FallbackReason : $"{selection.FallbackReason}; {nested}";
    }

    /// <summary>
    /// Probes whether a hardware decode path exists for the codec.
    /// <para>探测该编码格式是否存在硬件解码路径。</para>
    /// </summary>
    /// <param name="codec">
    /// Codec to probe; anything other than H.264, H.265 or AV1 returns false without touching native code.
    /// <para>要探测的编码格式；非 H.264、H.265、AV1 时直接返回 false，不会进入原生代码。</para>
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the hardware path initializes; missing native libraries and initialization failures are reported as false.
    /// <para>硬件路径成功初始化时返回 <see langword="true"/>；缺失原生库与初始化失败均按 false 报告。</para>
    /// </returns>
    public static bool HasGpuDecoder(VideoCodecId codec)
    {
        if (codec is not (VideoCodecId.H264 or VideoCodecId.H265 or VideoCodecId.Av1)) return false;
        if (OperatingSystem.IsLinux()) return VaApiVideoDecoder.IsSupported(codec) || GStreamer.GStreamerVideoDecoder.IsAvailable(codec, true);
        if (OperatingSystem.IsMacOS() && codec == VideoCodecId.Av1) return GStreamer.GStreamerVideoDecoder.IsAvailable(codec, true);
        if (OperatingSystem.IsMacOS()) return VideoToolbox.VtNative.VTIsHardwareDecodeSupported(VideoToolbox.VtNative.Codec(codec)) != 0;
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var decoder = new MediaFoundation.MediaFoundationGpuVideoDecoder();
            decoder.Initialize(codec, 1920, 1080, static _ => { });
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or NotSupportedException or InvalidOperationException)
        {
            return false;
        }
    }
}