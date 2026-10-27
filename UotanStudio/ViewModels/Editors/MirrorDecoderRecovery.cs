using UotanStudio.Media.Acceleration.Linux;
using UotanStudio.Media.Decoding;
using UotanStudio.Media.Decoding.GStreamer;
using UotanStudio.Media.Decoding.MediaFoundation;
using UotanStudio.Media.Decoding.VideoToolbox;

namespace UotanStudio.ViewModels.Editors;

/// <summary>
/// Decides whether a mid-stream decoder failure can be recovered by rebuilding the decoder at the next
/// key-frame boundary, and which backend a rebuilt session should use.
/// <para>决定流内解码失败能否在下一个关键帧边界重建解码器来恢复，以及重建后的会话应当改用哪个后端。</para>
/// </summary>
/// <remarks>
/// A rebuilt decoder cannot join a running GOP: feeding it a delta frame produces artifacts or an immediate
/// second failure, so recovery waits for a codec configuration packet plus a key frame and asks the server
/// for one with <c>TYPE_RESET_VIDEO</c>. Only one rebuild is attempted per session; a decoder that fails again
/// after losing hardware decoding ends the session rather than oscillating between backends.
/// <para>重建后的解码器无法加入正在进行的 GOP：喂入差分帧会产生伪影或立刻二次失败，因此恢复过程等待一个编码器配置包
/// 加上一个关键帧，并用 <c>TYPE_RESET_VIDEO</c> 向服务端请求。每次会话只尝试重建一次；在失去硬件解码后仍失败的
/// 解码器照旧终止会话，而不是在后端之间来回振荡。</para>
/// </remarks>
internal static class MirrorDecoderRecovery
{
    internal const int ConsecutiveFailuresBeforeRebuild = 3;
    internal const int RebuildAttemptsPerSession = 1;
    internal const long KeyFrameWaitMilliseconds = 3000;

    /// <summary>
    /// Reports whether an exception came from the decoder rather than from teardown or cancellation.
    /// <para>判断异常是否来自解码器本身，而不是拆除或取消。</para>
    /// </summary>
    /// <param name="error">
    /// The exception thrown by <see cref="IVideoDecoder.Decode(ReadOnlySpan{byte}, long)"/>,
    /// <see cref="IVideoDecoder.Poll"/> or <see cref="IVideoDecoder.Drain"/>.
    /// <para>由 <see cref="IVideoDecoder.Decode(ReadOnlySpan{byte}, long)"/>、<see cref="IVideoDecoder.Poll"/>
    /// 或 <see cref="IVideoDecoder.Drain"/> 抛出的异常。</para>
    /// </param>
    /// <returns>
    /// <see langword="true"/> when a rebuild is worth attempting. Teardown faults are excluded even though
    /// <see cref="ObjectDisposedException"/> derives from <see cref="InvalidOperationException"/>.
    /// <para>值得尝试重建时为 <see langword="true"/>。拆除期的故障被排除，尽管
    /// <see cref="ObjectDisposedException"/> 派生自 <see cref="InvalidOperationException"/>。</para>
    /// </returns>
    internal static bool IsRecoverable(Exception error) => error is InvalidDataException
        or (InvalidOperationException and not ObjectDisposedException);

    /// <summary>
    /// Identifies the backend a decoder instance was created for.
    /// <para>识别解码器实例所属的后端。</para>
    /// </summary>
    /// <param name="decoder">
    /// The active decoder, or <see langword="null"/> before initialization.
    /// <para>当前激活的解码器；未初始化时为 <see langword="null"/>。</para>
    /// </param>
    /// <returns>
    /// The backend to degrade from, or <see cref="VideoDecoderBackend.Auto"/> when the instance is
    /// unrecognized and therefore not worth rebuilding.
    /// <para>需要降级的后端；实例无法识别（因而不值得重建）时返回 <see cref="VideoDecoderBackend.Auto"/>。</para>
    /// </returns>
    internal static VideoDecoderBackend BackendOf(IVideoDecoder? decoder) => decoder switch
    {
        null => VideoDecoderBackend.Auto,
        MediaFoundationGpuVideoDecoder => VideoDecoderBackend.MediaFoundationGpu,
        MediaFoundationVideoDecoder => VideoDecoderBackend.MediaFoundationCpu,
        VideoToolboxVideoDecoder => VideoDecoderBackend.VideoToolbox,
        VaApiVideoDecoder => VideoDecoderBackend.VaApi,
        GStreamerVideoDecoder gst => gst.UsesHardware ? VideoDecoderBackend.GStreamerHardware : VideoDecoderBackend.GStreamerSoftware,
        _ => VideoDecoderBackend.Auto,
    };

    /// <summary>
    /// Builds the next-simpler decoder choices, dropping GPU export first and hardware decoding second.
    /// <para>构造下一级更简化的解码器选项：先去掉 GPU 导出，再去掉硬件解码。</para>
    /// </summary>
    /// <param name="options">
    /// The options the failed decoder was created with.
    /// <para>失败的解码器创建时使用的选项。</para>
    /// </param>
    /// <param name="active">
    /// The backend reported by <see cref="BackendOf"/> for the failed decoder.
    /// <para>由 <see cref="BackendOf"/> 报告的失败解码器后端。</para>
    /// </param>
    /// <returns>
    /// Options for a rebuilt decoder, or <see langword="null"/> when the backend is already the simplest
    /// one available on this platform, in which case the session ends.
    /// <para>重建解码器应使用的选项；当该后端已是本平台最简选择（此时会话终止）时返回 <see langword="null"/>。</para>
    /// </returns>
    internal static VideoDecoderOptions? Degrade(VideoDecoderOptions options, VideoDecoderBackend active)
    {
        if (OperatingSystem.IsWindows()) return DegradeWindows(options, active);
        if (OperatingSystem.IsLinux()) return DegradeLinux(options, active);
        if (OperatingSystem.IsMacOS()) return DegradeMacOS(options, active);
        return null;
    }

    /// <summary>
    /// Windows recovery choice: a hardware decoder that failed mid-stream gives way to the Media Foundation
    /// software decoder, and nothing else is worth rebuilding.
    /// <para>Windows 恢复选择：流内失败的硬件解码器让位给 Media Foundation 软件解码器，其他后端不值得重建。</para>
    /// </summary>
    /// <param name="options">Choices of the failed decoder. <para>失败解码器的选项。</para></param>
    /// <param name="active">Backend of the failed decoder. <para>失败解码器的后端。</para></param>
    /// <returns>
    /// Rebuilt choices, or <see langword="null"/> when no simpler backend remains.
    /// <para>重建用的选项；已无更简后端时返回 <see langword="null"/>。</para>
    /// </returns>
    internal static VideoDecoderOptions? DegradeWindows(VideoDecoderOptions options, VideoDecoderBackend active)
    {
        if (active != VideoDecoderBackend.MediaFoundationGpu) return null;
        return options with
        {
            Backend = VideoDecoderBackend.MediaFoundationCpu,
            MediaFoundationPreferGpuExport = false,
            MediaFoundationAdapterLuid = null,
            PreferNv12Output = true,
        };
    }

    /// <summary>
    /// Linux recovery choice: VA-API or a hardware GStreamer plugin gives way to a software GStreamer decode.
    /// <para>Linux 恢复选择：VA-API 或硬件 GStreamer 插件让位给软件 GStreamer 解码。</para>
    /// </summary>
    /// <param name="options">Choices of the failed decoder. <para>失败解码器的选项。</para></param>
    /// <param name="active">Backend of the failed decoder. <para>失败解码器的后端。</para></param>
    /// <returns>
    /// Rebuilt choices, or <see langword="null"/> when no simpler backend remains.
    /// <para>重建用的选项；已无更简后端时返回 <see langword="null"/>。</para>
    /// </returns>
    internal static VideoDecoderOptions? DegradeLinux(VideoDecoderOptions options, VideoDecoderBackend active)
    {
        if (active is not (VideoDecoderBackend.VaApi or VideoDecoderBackend.GStreamerHardware)) return null;
        return options with
        {
            Backend = VideoDecoderBackend.GStreamerSoftware,
            VaApiPreferGpuExport = false,
            MediaFoundationPreferGpuExport = false,
        };
    }

    /// <summary>
    /// macOS recovery choice: VideoToolbox first loses its IOSurface export, then gives way to software
    /// GStreamer decoding.
    /// <para>macOS 恢复选择：VideoToolbox 先放弃 IOSurface 导出，再让位给软件 GStreamer 解码。</para>
    /// </summary>
    /// <param name="options">Choices of the failed decoder. <para>失败解码器的选项。</para></param>
    /// <param name="active">Backend of the failed decoder. <para>失败解码器的后端。</para></param>
    /// <returns>
    /// Rebuilt choices, or <see langword="null"/> when no simpler backend remains.
    /// <para>重建用的选项；已无更简后端时返回 <see langword="null"/>。</para>
    /// </returns>
    internal static VideoDecoderOptions? DegradeMacOS(VideoDecoderOptions options, VideoDecoderBackend active) => active switch
    {
        VideoDecoderBackend.VideoToolbox => options with { VideoToolboxPreferGpuExport = false },
        VideoDecoderBackend.GStreamerHardware => options with { Backend = VideoDecoderBackend.GStreamerSoftware, VideoToolboxPreferGpuExport = false },
        _ => null,
    };
}