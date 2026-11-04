using UotanStudio.Compress;

namespace UotanStudio.DD.Images;

/// <summary>
/// Resolved on-disk representation of an image file.
/// <para>镜像文件在磁盘上的已解析表示形式。</para>
/// </summary>
/// <param name="Container">Decoder layer required before writing.<para>写入前需要的解码层。</para></param>
/// <param name="Compression">Compression codec when <paramref name="Container"/> is
/// <see cref="ImageContainerKind.Compressed"/>; otherwise <see cref="CompressionFormat.None"/>.
/// <para>当 <paramref name="Container"/> 为 <see cref="ImageContainerKind.Compressed"/> 时使用的压缩编解码器；否则为 <see cref="CompressionFormat.None"/>。</para></param>
public sealed record ImageFormat(ImageContainerKind Container, CompressionFormat Compression)
{
    /// <summary>
    /// Gets the format used for raw, uncompressed images.
    /// <para>获取裸、未压缩镜像使用的格式。</para>
    /// </summary>
    public static ImageFormat Raw { get; } = new(ImageContainerKind.Raw, CompressionFormat.None);

    /// <summary>
    /// Gets whether the source stream must be decoded before writing.
    /// <para>获取写入前是否必须解码源流。</para>
    /// </summary>
    public bool RequiresDecoding => Container != ImageContainerKind.Raw;
}