namespace UotanStudio.DD.Images;

/// <summary>
/// Container layer that must be decoded before writing an image.
/// <para>写入镜像前必须解码的容器层。</para>
/// </summary>
public enum ImageContainerKind
{
    /// <summary>
    /// The file is a raw disk image and is streamed as-is.
    /// <para>文件是裸磁盘镜像，按原样流式读取。</para>
    /// </summary>
    Raw,

    /// <summary>
    /// The file is a compression stream wrapping a raw disk image.
    /// <para>文件是包裹裸磁盘镜像的压缩流。</para>
    /// </summary>
    Compressed,

    /// <summary>
    /// The file is a ZIP archive containing exactly one raw disk image entry.
    /// <para>文件是仅包含一个裸磁盘镜像条目的 ZIP 归档。</para>
    /// </summary>
    Zip,
}