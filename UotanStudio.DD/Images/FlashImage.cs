namespace UotanStudio.DD.Images;

/// <summary>
/// Metadata for a probed image file, including the entry to read when the file is a ZIP archive.
/// <para>已探测镜像文件的元数据，包含文件为 ZIP 归档时要读取的条目。</para>
/// </summary>
public sealed record FlashImage
{
    /// <summary>
    /// Gets the absolute or caller-supplied path of the image file.
    /// <para>获取镜像文件的绝对路径或调用方提供的路径。</para>
    /// </summary>
    public required string Path { get; init; }

    /// <summary>
    /// Gets the file name shown to users.
    /// <para>获取展示给用户的文件名。</para>
    /// </summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// Gets the resolved container and codec.
    /// <para>获取已解析的容器与编解码器。</para>
    /// </summary>
    public required ImageFormat Format { get; init; }

    /// <summary>
    /// Gets the size of the image file on disk, which may be compressed.
    /// <para>获取镜像文件在磁盘上的大小，可能已压缩。</para>
    /// </summary>
    public required long CompressedSize { get; init; }

    /// <summary>
    /// Gets the decompressed image size, or <see langword="null"/> when the container does not expose it.
    /// <para>获取解压后的镜像大小；容器未暴露该信息时为 <see langword="null"/>。</para>
    /// </summary>
    /// <value>
    /// ZIP entries are known; gzip, xz, bzip2 and the other compression streams are treated as estimated.
    /// <para>ZIP 条目大小可知；gzip、xz、bzip2 等压缩流的该值视为估算。</para>
    /// </value>
    public long? Size { get; init; }

    /// <summary>
    /// Gets the single file entry read from a ZIP archive, or <see langword="null"/> for other containers.
    /// <para>获取从 ZIP 归档读取的唯一文件条目；其他容器为 <see langword="null"/>。</para>
    /// </summary>
    public string? ZipEntryName { get; init; }

    /// <summary>
    /// Gets whether <see cref="Size"/> is unknown and capacity checks must fall back to <see cref="CompressedSize"/>.
    /// <para>获取 <see cref="Size"/> 是否未知，容量检查是否必须回退到 <see cref="CompressedSize"/>。</para>
    /// </summary>
    public bool IsSizeEstimated => Size is null;
}