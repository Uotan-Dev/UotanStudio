namespace UotanStudio.Services.Images;

/// <summary>
/// Display-ready attributes read from one image file, combining codec header fields, file size and
/// compiled file(1) magic recognition.
/// <para>从单个图片文件读取的展示就绪属性，汇总编解码头字段、文件大小与编译型 file(1) 魔数识别结果。</para>
/// </summary>
public sealed record ImageFileProperties
{
    /// <summary>Absolute file path.<para>绝对文件路径。</para></summary>
    public string FilePath { get; init; } = string.Empty;

    /// <summary>On-disk length in bytes.<para>磁盘长度（字节）。</para></summary>
    public long FileSize { get; init; }

    /// <summary>Human-readable codec format name.<para>可读的编解码格式名。</para></summary>
    public string CodecFormat { get; init; } = string.Empty;

    /// <summary>Description produced by the compiled magic engine.<para>编译型魔数引擎给出的类型描述。</para></summary>
    public string MagicDescription { get; init; } = string.Empty;

    /// <summary>MIME type declared by the matched magic rule.<para>命中魔数规则声明的 MIME 类型。</para></summary>
    public string MimeType { get; init; } = string.Empty;

    /// <summary>Extensions declared by the matched magic rule.<para>命中魔数规则声明的扩展名。</para></summary>
    public string MagicExtensions { get; init; } = string.Empty;

    /// <summary>Stored pixel dimensions.<para>存储的像素尺寸。</para></summary>
    public string Dimensions { get; init; } = string.Empty;

    /// <summary>Samples per pixel of the decoded raster.<para>解码栅格每像素的采样数。</para></summary>
    public string Channels { get; init; } = string.Empty;

    /// <summary>Stored bit depth.<para>存储的位深。</para></summary>
    public string BitDepth { get; init; } = string.Empty;

    /// <summary>Per-channel sample type.<para>每通道采样类型。</para></summary>
    public string SampleFormat { get; init; } = string.Empty;

    /// <summary>Frame or page count reported by the header.<para>文件头报告的帧数或页数。</para></summary>
    public string FrameCount { get; init; } = string.Empty;

    /// <summary>EXIF orientation reported by the header; the viewer never silently rotates the raster.<para>文件头报告的 EXIF 方向；查看器不隐式旋转栅格。</para></summary>
    public string Orientation { get; init; } = string.Empty;

    /// <summary>Whether the facade can decode the detected format.<para>门面能否解码所识别的格式。</para></summary>
    public bool Decodable { get; init; }
}

/// <summary>
/// Result of one inspection: the attributes, an optional PNG preview encoded from the decoded raster,
/// and an optional localized error key.
/// <para>单次探测结果：属性、由解码栅格编码的可选 PNG 预览，以及可选的本地化错误键。</para>
/// </summary>
/// <param name="Properties">Attributes read from the file.<para>从文件读取的属性。</para></param>
/// <param name="PreviewPng">PNG bytes for display, or null when decoding was not possible.<para>用于显示的 PNG 字节；无法解码时为 null。</para></param>
/// <param name="ErrorKey">Localized status key, or null on success.<para>本地化状态键；成功时为 null。</para></param>
internal sealed record ImageFileSnapshot(
    ImageFileProperties Properties,
    byte[]? PreviewPng,
    string? ErrorKey = null);