namespace UotanStudio.Media.Decoding;

/// <summary>Explicit video backend selection. 视频解码后端选择。</summary>
public enum VideoDecoderBackend
{
    /// <summary>Prefer native GPU, allow initialization fallback. 优先 GPU，可在初始化时回退。</summary>
    Auto,
    /// <summary>Windows D3D11, without CPU fallback. Windows D3D11，不自动回退。</summary>
    MediaFoundationGpu,
    /// <summary>Windows CPU-readable Media Foundation. Windows CPU 后端。</summary>
    MediaFoundationCpu,
    /// <summary>macOS native VideoToolbox hardware. macOS 原生硬件后端。</summary>
    VideoToolbox,
    /// <summary>GStreamer hardware plugins (VA/NVDEC/VideoToolbox/D3D11). GStreamer 硬件插件。</summary>
    GStreamerHardware,
    /// <summary>Explicit GStreamer software decoding. GStreamer 软件解码。</summary>
    GStreamerSoftware,
    /// <summary>Linux VA-API hardware via the native libavcodec bridge; no GStreamer or software fallback.</summary>
    VaApi,
}

/// <summary>YUV matrix override. YUV 色彩矩阵覆盖。</summary>
public enum VideoColorMatrix
{
    /// <summary>Use stream metadata. 采用码流元数据。</summary>
    Auto,
    /// <summary>BT.601 SDR. BT.601 标清矩阵。</summary>
    Bt601,
    /// <summary>BT.709 SDR. BT.709 高清矩阵。</summary>
    Bt709,
}

/// <summary>YUV nominal range override. YUV 数值范围覆盖。</summary>
public enum VideoColorRange
{
    /// <summary>Use stream metadata. 采用码流元数据。</summary>
    Auto,
    /// <summary>Limited/video range. 有限范围。</summary>
    Limited,
    /// <summary>Full range. 全范围。</summary>
    Full,
}

/// <summary>Immutable per-session decoding choices. 每次会话的不可变解码选项。</summary>
public sealed record VideoDecoderOptions
{
    /// <summary>Prefer NV12 readback from the Windows GPU decoder, avoiding BGRA video-processor surfaces. The consumer converts pixels for display.</summary>
    public bool PreferNv12Output { get; init; }
    /// <summary>Selected backend. 后端。</summary>
    public VideoDecoderBackend Backend { get; init; }
    /// <summary>Source YUV matrix override. 源 YUV 矩阵覆盖。</summary>
    public VideoColorMatrix ColorMatrix { get; init; }
    /// <summary>Source YUV range override. 源 YUV 范围覆盖。</summary>
    public VideoColorRange ColorRange { get; init; }
    /// <summary>Optional absolute Linux DRM render-node path. When omitted, probe available render nodes in order.</summary>
    public string? VaApiRenderNode { get; init; }
}
