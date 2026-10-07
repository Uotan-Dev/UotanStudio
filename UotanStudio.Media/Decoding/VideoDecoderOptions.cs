namespace UotanStudio.Media.Decoding;

/// <summary>
/// Explicit video backend selection.
/// <para>显式的视频解码后端选择。</para>
/// </summary>
public enum VideoDecoderBackend
{
    /// <summary>
    /// Prefer native GPU, allow initialization fallback.
    /// <para>优先使用原生 GPU，允许在初始化时回退。</para>
    /// </summary>
    Auto,
    /// <summary>
    /// Windows D3D11, without CPU fallback.
    /// <para>Windows D3D11 路径，不回退到 CPU。</para>
    /// </summary>
    MediaFoundationGpu,
    /// <summary>
    /// Windows CPU-readable Media Foundation.
    /// <para>Windows 上可被 CPU 读取的 Media Foundation 后端。</para>
    /// </summary>
    MediaFoundationCpu,
    /// <summary>
    /// macOS native VideoToolbox hardware.
    /// <para>macOS 原生 VideoToolbox 硬件后端。</para>
    /// </summary>
    VideoToolbox,
    /// <summary>
    /// GStreamer hardware plugins (VA/NVDEC/VideoToolbox/D3D11).
    /// <para>GStreamer 硬件插件（VA/NVDEC/VideoToolbox/D3D11）。</para>
    /// </summary>
    GStreamerHardware,
    /// <summary>
    /// Explicit GStreamer software decoding.
    /// <para>显式使用 GStreamer 软件解码。</para>
    /// </summary>
    GStreamerSoftware,
    /// <summary>
    /// Linux VA-API hardware via the native libavcodec bridge; no GStreamer or software fallback.
    /// <para>经原生 libavcodec 桥接的 Linux VA-API 硬件解码；不回退到 GStreamer 或软件解码。</para>
    /// </summary>
    VaApi,
}

/// <summary>
/// YUV matrix override.
/// <para>YUV 色彩矩阵覆盖。</para>
/// </summary>
public enum VideoColorMatrix
{
    /// <summary>
    /// Use stream metadata.
    /// <para>采用码流元数据。</para>
    /// </summary>
    Auto,
    /// <summary>
    /// BT.601 SDR.
    /// <para>BT.601 标清矩阵。</para>
    /// </summary>
    Bt601,
    /// <summary>
    /// BT.709 SDR.
    /// <para>BT.709 高清矩阵。</para>
    /// </summary>
    Bt709,
}

/// <summary>
/// YUV nominal range override.
/// <para>YUV 数值范围覆盖。</para>
/// </summary>
public enum VideoColorRange
{
    /// <summary>
    /// Use stream metadata.
    /// <para>采用码流元数据。</para>
    /// </summary>
    Auto,
    /// <summary>
    /// Limited/video range.
    /// <para>有限（视频）范围。</para>
    /// </summary>
    Limited,
    /// <summary>
    /// Full range.
    /// <para>全范围。</para>
    /// </summary>
    Full,
}

/// <summary>
/// Immutable per-session decoding choices.
/// <para>每次会话的不可变解码选项。</para>
/// </summary>
public sealed record VideoDecoderOptions
{
    /// <summary>
    /// Prefer NV12 readback from the Windows GPU decoder, avoiding BGRA video-processor surfaces.
    /// The consumer converts pixels for display.
    /// <para>优先从 Windows GPU 解码器回读 NV12，避开 BGRA 视频处理器表面；由消费者自行转换像素用于显示。</para>
    /// </summary>
    /// <value>
    /// <see langword="true"/> when the Windows GPU backend should produce NV12 frames; other backends ignore it.
    /// <para>为 <see langword="true"/> 时 Windows GPU 后端应产出 NV12 帧；其他后端忽略该值。</para>
    /// </value>
    public bool PreferNv12Output { get; init; }
    /// <summary>
    /// Selected backend.
    /// <para>所选解码后端。</para>
    /// </summary>
    /// <value>
    /// The requested backend; the default <c>0</c> value is <see cref="VideoDecoderBackend.Auto"/>.
    /// <para>请求的后端；默认值 <c>0</c> 对应 <see cref="VideoDecoderBackend.Auto"/>。</para>
    /// </value>
    public VideoDecoderBackend Backend { get; init; }
    /// <summary>
    /// Source YUV matrix override.
    /// <para>源 YUV 矩阵覆盖。</para>
    /// </summary>
    /// <value>
    /// The matrix forced onto conversion, or <see cref="VideoColorMatrix.Auto"/> to follow stream metadata.
    /// <para>转换时强制使用的矩阵；<see cref="VideoColorMatrix.Auto"/> 表示遵循码流元数据。</para>
    /// </value>
    public VideoColorMatrix ColorMatrix { get; init; }
    /// <summary>
    /// Source YUV range override.
    /// <para>源 YUV 数值范围覆盖。</para>
    /// </summary>
    /// <value>
    /// The range forced onto conversion, or <see cref="VideoColorRange.Auto"/> to follow stream metadata.
    /// <para>转换时强制使用的范围；<see cref="VideoColorRange.Auto"/> 表示遵循码流元数据。</para>
    /// </value>
    public VideoColorRange ColorRange { get; init; }
    /// <summary>
    /// Optional absolute Linux DRM render-node path. When omitted, probe available render nodes in order.
    /// <para>可选的 Linux DRM 渲染节点绝对路径；缺省时按顺序探测可用的渲染节点。</para>
    /// </summary>
    /// <value>
    /// The render node for <see cref="VideoDecoderBackend.VaApi"/>, or <see langword="null"/> for automatic probing.
    /// <para><see cref="VideoDecoderBackend.VaApi"/> 使用的渲染节点；<see langword="null"/> 表示自动探测。</para>
    /// </value>
    public string? VaApiRenderNode { get; init; }
    /// <summary>
    /// Prefer exporting decoded Linux VA surfaces as DRM PRIME layers for zero-copy presentation
    /// instead of downloading NV12. Presentation must consume <see cref="Acceleration.Linux.VaDmaBufVideoFrame"/>
    /// through <see cref="Acceleration.Linux.VaApiVideoDecoder.DmabufFrameCallback"/>, and the decoder
    /// falls back to the download path when an export fails.
    /// <para>优先把解码后的 Linux VA 表面导出为 DRM PRIME 层以供零拷贝呈现，而非下载 NV12。呈现必须经
    /// <see cref="Acceleration.Linux.VaApiVideoDecoder.DmabufFrameCallback"/> 消费
    /// <see cref="Acceleration.Linux.VaDmaBufVideoFrame"/>；导出失败时解码器回退到下载路径。</para>
    /// </summary>
    /// <value>
    /// <see langword="true"/> to request GPU export on the VA-API backend; other backends ignore it.
    /// <para>为 <see langword="true"/> 时 VA-API 后端请求 GPU 导出；其他后端忽略该值。</para>
    /// </value>
    public bool VaApiPreferGpuExport { get; init; }
    /// <summary>
    /// Prefer exporting decoded Windows frames as shared D3D11 textures with a keyed mutex for zero-copy
    /// presentation instead of reading back NV12. Requires <see cref="MediaFoundationAdapterLuid"/> and a
    /// presenter that consumes <see cref="MediaFoundation.D3D11SharedVideoFrame"/> through
    /// <see cref="MediaFoundation.MediaFoundationGpuVideoDecoder.GpuExportFrameCallback"/>; when the
    /// compositor stops consuming a texture the decoder drops frames instead of blocking.
    /// <para>优先把 Windows 解码输出导出为带键互锁的 D3D11 共享纹理以供零拷贝呈现，而非回读 NV12。
    /// 需要 <see cref="MediaFoundationAdapterLuid"/>，且呈现器须通过
    /// <see cref="MediaFoundation.MediaFoundationGpuVideoDecoder.GpuExportFrameCallback"/> 消费
    /// <see cref="MediaFoundation.D3D11SharedVideoFrame"/>；合成器停止消费纹理时解码器丢帧而不阻塞。</para>
    /// </summary>
    /// <value>
    /// <see langword="true"/> to request shared-texture export on the Windows GPU backend; other backends ignore it.
    /// <para>为 <see langword="true"/> 时 Windows GPU 后端请求共享纹理导出；其他后端忽略该值。</para>
    /// </value>
    public bool MediaFoundationPreferGpuExport { get; init; }
    /// <summary>
    /// The 8-byte DXGI adapter LUID of the compositor. The Windows GPU decoder creates its D3D11 device
    /// on this adapter so the shared textures can be opened by the render device; cross-adapter shared
    /// resources are not addressable.
    /// <para>合成器的 8 字节 DXGI 适配器 LUID。Windows GPU 解码器在该适配器上创建 D3D11 设备，使共享纹理可被
    /// 渲染设备打开；跨适配器的共享资源不可寻址。</para>
    /// </summary>
    /// <value>
    /// The LUID bytes from <c>ICompositionGpuInterop.DeviceLuid</c>, or <see langword="null"/> to use the
    /// default adapter (disables export).
    /// <para>来自 <c>ICompositionGpuInterop.DeviceLuid</c> 的 LUID 字节；<see langword="null"/> 表示使用默认适配器（禁用导出）。</para>
    /// </value>
    public byte[]? MediaFoundationAdapterLuid { get; init; }
    /// <summary>
    /// Prefer exporting decoded macOS frames as IOSurface-backed pixel buffers for zero-copy presentation
    /// instead of copying NV12 planes. The decompression session then requests BGRA output and falls back to
    /// the NV12 session when the hardware path refuses it; presentation must consume
    /// <see cref="VideoToolbox.IosurfaceVideoFrame"/> through
    /// <see cref="VideoToolbox.VideoToolboxVideoDecoder.IosurfaceFrameCallback"/>.
    /// <para>优先把 macOS 解码输出导出为 IOSurface 支撑的像素缓冲以供零拷贝呈现，而非拷贝 NV12 平面。此时解压会话
    /// 请求 BGRA 输出，硬件路径拒绝时回落 NV12 会话；呈现器须通过
    /// <see cref="VideoToolbox.VideoToolboxVideoDecoder.IosurfaceFrameCallback"/> 消费
    /// <see cref="VideoToolbox.IosurfaceVideoFrame"/>。</para>
    /// </summary>
    /// <value>
    /// <see langword="true"/> to request IOSurface export on the VideoToolbox backend; other backends ignore it.
    /// <para>为 <see langword="true"/> 时 VideoToolbox 后端请求 IOSurface 导出；其他后端忽略该值。</para>
    /// </value>
    public bool VideoToolboxPreferGpuExport { get; init; }
}