using UotanStudio.Compress;

namespace UotanStudio.DD.Images;

/// <summary>
/// Maps a file name extension to a supported image container.
/// <para>将文件名扩展名映射到受支持的镜像容器。</para>
/// </summary>
public static class ImageFormatResolver
{
    private static readonly HashSet<string> RawExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".img",
        ".iso",
        ".bin",
        ".raw",
        ".dsk",
        ".wic",
        ".hddimg",
        ".sdcard",
        ".rpi-sdimg",
    };

    /// <summary>
    /// Resolves the format of <paramref name="path"/>.
    /// <para>解析 <paramref name="path"/> 的格式。</para>
    /// </summary>
    /// <param name="path">Image file name or path.<para>镜像文件名或路径。</para></param>
    /// <returns>The resolved container and codec.<para>解析得到的容器与编解码器。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty or whitespace.
    /// <para><paramref name="path"/> 为空或空白。</para></exception>
    /// <exception cref="FlashException">The extension is unsupported, including the <c>.dmg</c>
    /// and <c>.etch</c> containers.
    /// <para>扩展名不受支持，包括 <c>.dmg</c> 与 <c>.etch</c> 容器。</para></exception>
    public static ImageFormat Resolve(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var extension = Path.GetExtension(path);
        if (extension.Length == 0)
        {
            throw new FlashException($"The image \"{path}\" has no file extension, so its format cannot be determined.");
        }

        if (extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return new ImageFormat(ImageContainerKind.Zip, CompressionFormat.None);
        }

        if (extension.Equals(".dmg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".etch", StringComparison.OrdinalIgnoreCase))
        {
            throw new FlashException($"The image format \"{extension}\" is not supported by this library.");
        }

        if (RawExtensions.Contains(extension))
        {
            return ImageFormat.Raw;
        }

        var compression = CompressionFormats.FromExtension(path);
        if (compression == CompressionFormat.None || !CompressionFormats.IsSupported(compression))
        {
            throw new FlashException($"The image format \"{extension}\" is not supported by this library.");
        }

        return new ImageFormat(ImageContainerKind.Compressed, compression);
    }
}