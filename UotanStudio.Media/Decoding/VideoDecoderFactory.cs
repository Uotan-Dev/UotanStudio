using UotanStudio.Media.Acceleration.Linux;

namespace UotanStudio.Media.Decoding;

/// <summary>Creates AOT-safe platform decoders and probes native codec availability.</summary>
public static class VideoDecoderFactory
{
    /// <summary>
    /// Checks whether a backend accepts a 1920x1080 stream configuration (Windows) or advertises
    /// hardware codec support (macOS). Actual stream profile, dimensions and decoding can still fail.
    /// Linux probes the VA bridge/driver and installed GStreamer plugins; a real stream may still fail.
    /// </summary>
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

    /// <summary>Creates an uninitialized platform decoder.</summary>
    /// <summary>创建未初始化的平台解码器。</summary>
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

    /// <summary>Creates the requested backend; explicit choices never silently switch. 按用户选择创建后端，显式选择不静默切换。</summary>
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
                () => new VaApiVideoDecoder(options),
                static () => new FallbackVideoDecoder(
                    static () => new GStreamer.GStreamerVideoDecoder(true),
                    static () => new GStreamer.GStreamerVideoDecoder(false))),
            VideoDecoderBackend.VaApi when OperatingSystem.IsLinux() => new VaApiVideoDecoder(options),
            VideoDecoderBackend.MediaFoundationGpu when OperatingSystem.IsWindows() => new MediaFoundation.MediaFoundationGpuVideoDecoder(options),
            VideoDecoderBackend.MediaFoundationCpu when OperatingSystem.IsWindows() => new MediaFoundation.MediaFoundationVideoDecoder(),
            VideoDecoderBackend.VideoToolbox when OperatingSystem.IsMacOS() => new VideoToolbox.VideoToolboxVideoDecoder(),
            VideoDecoderBackend.GStreamerHardware => new GStreamer.GStreamerVideoDecoder(true),
            VideoDecoderBackend.GStreamerSoftware => new GStreamer.GStreamerVideoDecoder(false),
            _ => throw new NotSupportedException("Selected video backend is unavailable on this platform."),
        };
    }

    /// <summary>Creates a codec-aware platform backend, including AV1 on macOS via GStreamer VideoToolbox. 按编码格式创建平台后端。</summary>
    public static IVideoDecoder Create(VideoCodecId codec, VideoDecoderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (codec is not (VideoCodecId.H264 or VideoCodecId.H265 or VideoCodecId.Av1)) throw new ArgumentOutOfRangeException(nameof(codec));
        if (OperatingSystem.IsMacOS() && codec == VideoCodecId.Av1 && options.Backend is VideoDecoderBackend.Auto or VideoDecoderBackend.VideoToolbox)
            return new GStreamer.GStreamerVideoDecoder(true);

        return Create(options);
    }

    /// <summary>Gets the initialized backend name, including the actual choice after fallback.</summary>
    public static string GetBackendName(IVideoDecoder decoder)
    {
        if (decoder is FallbackVideoDecoder selection)
            return selection.ActiveDecoder is { } active ? GetBackendName(active) : "Uninitialized";
        return decoder switch
        {
            MediaFoundation.MediaFoundationGpuVideoDecoder => "Media Foundation (GPU)",
            MediaFoundation.MediaFoundationVideoDecoder => "Media Foundation (CPU)",
            VideoToolbox.VideoToolboxVideoDecoder => "VideoToolbox (hardware)",
            GStreamer.GStreamerVideoDecoder gst => $"GStreamer ({(gst.UsesHardware ? "GPU" : "CPU")}: {gst.DecoderElement})",
            VaApiVideoDecoder => "VA-API (libavcodec, hardware)",
            _ => "Unknown",
        };
    }

    /// <summary>Gets why the preferred backend failed during the most recent initialization.</summary>
    public static string? GetFallbackReason(IVideoDecoder decoder)
    {
        if (decoder is not FallbackVideoDecoder selection) return null;
        string? nested = selection.ActiveDecoder is { } active ? GetFallbackReason(active) : null;
        return nested is null ? selection.FallbackReason : $"{selection.FallbackReason}; {nested}";
    }

    /// <summary>Probes D3D11 initialization at 1920x1080. Actual stream decoding can still fail.</summary>
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
