namespace UotanStudio.Media.Decoding;

/// <summary>Creates AOT-safe platform decoders and probes native codec availability.</summary>
public static class VideoDecoderFactory
{
    /// <summary>
    /// Checks whether a backend accepts a small stream configuration (Windows) or advertises
    /// hardware codec support (macOS). Actual stream profile, dimensions and decoding can still fail.
    /// Linux VA/EGL primitives do not yet provide an Annex-B parser/DPB and are not advertised here.
    /// </summary>
    public static bool IsSupported(VideoCodecId codec)
    {
        if (codec is not (VideoCodecId.H264 or VideoCodecId.H265)) return false;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var decoder = Create();
                decoder.Initialize(codec, 64, 64, static _ => { });
                return true;
            }
            if (OperatingSystem.IsMacOS())
                return VideoToolbox.VtNative.VTIsHardwareDecodeSupported(VideoToolbox.VtNative.Codec(codec)) != 0;
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
    /// On Windows the GPU path (asynchronous hardware MFT + D3D11 video processor) is tried
    /// first; the synchronous CPU decoder is the fallback when no suitable hardware MFT exists.
    /// </para>
    /// <para>
    /// Windows 上优先尝试 GPU 路径（异步硬件 MFT + D3D11 视频处理器）；无合适硬件 MFT 时
    /// 回退到同步 CPU 解码器。
    /// </para>
    /// </remarks>
    public static IVideoDecoder Create()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                return new MediaFoundation.MediaFoundationGpuVideoDecoder();
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                // D3D11 / MF plumbing missing entirely — go straight to the CPU path.
                return new MediaFoundation.MediaFoundationVideoDecoder();
            }
        }
        if (OperatingSystem.IsMacOS()) return new VideoToolbox.VideoToolboxVideoDecoder();
        throw new NotSupportedException("An Annex-B video decoder is not available on this platform. Linux VA-API/EGL primitives require a codec parser and reference-picture manager.");
    }

    /// <summary>
    /// Reports which backend <see cref="Create"/> would return on Windows: the asynchronous
    /// hardware path when an async hardware MFT exists for the codec, otherwise the CPU decoder.
    /// </summary>
    /// <summary>
    /// 报告 Windows 上 <see cref="Create"/> 会返回的后端：存在该编码格式的异步硬件 MFT 时为
    /// 硬件路径，否则为 CPU 解码器。
    /// </summary>
    public static bool HasGpuDecoder(VideoCodecId codec)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var decoder = new MediaFoundation.MediaFoundationGpuVideoDecoder();
            decoder.Initialize(codec, 64, 64, static _ => { });
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or NotSupportedException or InvalidOperationException)
        {
            return false;
        }
    }
}
