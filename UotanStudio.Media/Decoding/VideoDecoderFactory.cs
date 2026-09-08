namespace UotanStudio.Media.Decoding;

/// <summary>
/// Creates the platform video decoder backed by OS built-in hardware decoders.
/// <para>创建以 OS 内置硬件解码器为后端的平台视频解码器。</para>
/// </summary>
/// <remarks>
/// <para>
/// Backend selection uses <see cref="OperatingSystem"/> runtime checks which are
/// AOT-safe. Currently only the Windows MediaFoundation backend is implemented;
/// macOS (VideoToolbox) and Linux (VA-API) backends are planned and throw
/// <see cref="NotSupportedException"/> until available.
/// </para>
/// <para>
/// 后端选择使用 AOT 安全的 <see cref="OperatingSystem"/> 运行时检查。当前仅实现
/// Windows MediaFoundation 后端；macOS（VideoToolbox）与 Linux（VA-API）后端
/// 为规划项，在可用前抛出 <see cref="NotSupportedException"/>。
/// </para>
/// </remarks>
public static class VideoDecoderFactory
{
    /// <summary>
    /// Determines whether the current platform supports decoding the given codec.
    /// </summary>
    /// <summary>
    /// 判断当前平台是否支持解码指定编码格式。
    /// </summary>
    /// <param name="codec">The codec to query. 要查询的编码格式。</param>
    /// <returns><see langword="true"/> when a decoder backend can handle the codec. 后端可处理该编码时为 <see langword="true"/>。</returns>
    public static bool IsSupported(VideoCodecId codec)
    {
        if (OperatingSystem.IsWindows())
        {
            // H.264 decoders ship with every supported Windows version. H.265
            // requires the HEVC Video Extensions, which is probed at decoder
            // creation rather than here.
            // H.264 解码器随所有受支持的 Windows 版本提供。H.265 需要 HEVC 视频
            // 扩展，在创建解码器时探测，此处不作判断。
            return codec == VideoCodecId.H264;
        }

        return false;
    }

    /// <summary>
    /// Creates the platform decoder for the current operating system.
    /// </summary>
    /// <summary>
    /// 为当前操作系统创建平台解码器。
    /// </summary>
    /// <returns>A new, uninitialized decoder instance. 新的未初始化解码器实例。</returns>
    /// <exception cref="NotSupportedException">Thrown when no backend exists for this platform. 当前平台无可用后端时抛出。</exception>
    public static IVideoDecoder Create()
    {
        if (OperatingSystem.IsWindows())
        {
            return new MediaFoundation.MediaFoundationVideoDecoder();
        }

        throw new NotSupportedException(
            "No video decoder backend is available on this platform. 当前平台没有可用的视频解码后端。");
    }
}
