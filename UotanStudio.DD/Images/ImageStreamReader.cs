using UotanStudio.Compress;
using UotanStudio.Compress.Zip;

namespace UotanStudio.DD.Images;

/// <summary>
/// Opens the decoded payload stream for a probed image.
/// <para>为已探测镜像打开解码后的内容流。</para>
/// </summary>
public static class ImageStreamReader
{
    private const int FileBufferBytes = 64 * 1024;

    /// <summary>
    /// Opens a new sequential read stream over the raw image bytes.
    /// <para>在裸镜像字节上打开新的顺序读取流。</para>
    /// </summary>
    /// <param name="image">Probed image.<para>已探测的镜像。</para></param>
    /// <returns>
    /// A readable stream positioned at byte zero; disposing it also releases the underlying file or archive.
    /// <para>定位在字节零的可读流；释放它会同时释放底层文件或归档。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="image"/> is <see langword="null"/>.
    /// <para><paramref name="image"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="FlashException">The file disappeared or its container is inconsistent with the probe result.
    /// <para>文件已消失，或其容器与探测结果不一致。</para></exception>
    public static Stream OpenRead(FlashImage image)
    {
        ArgumentNullException.ThrowIfNull(image);

        if (!File.Exists(image.Path))
        {
            throw new FlashException($"The image file \"{image.Path}\" does not exist.");
        }

        return image.Format.Container switch
        {
            ImageContainerKind.Raw => OpenFile(image.Path),
            ImageContainerKind.Compressed => OpenCompressed(image),
            ImageContainerKind.Zip => OpenZip(image),
            _ => throw new FlashException($"The image container \"{image.Format.Container}\" is not supported."),
        };
    }

    private static FileStream OpenFile(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileBufferBytes, FileOptions.SequentialScan);

    private static Stream OpenCompressed(FlashImage image)
    {
        if (image.Format.Compression == CompressionFormat.None || !CompressionFormats.IsSupported(image.Format.Compression))
        {
            throw new FlashException($"The compression codec \"{image.Format.Compression}\" cannot be decoded.");
        }

        var file = OpenFile(image.Path);
        try
        {
            var decoded = CompressionService.CreateDecompressionStream(file, image.Format.Compression);
            return new CompositeReadStream(decoded, file);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    private static Stream OpenZip(FlashImage image)
    {
        var archive = ZipContainer.OpenRead(image.Path);
        try
        {
            var entry = image.ZipEntryName is { } entryName
                ? archive.GetEntry(entryName)
                : ImageProbe.SelectSingleEntry(archive);
            if (entry is null)
            {
                throw new FlashException($"The ZIP image entry \"{image.ZipEntryName}\" no longer exists.");
            }

            return new CompositeReadStream(entry.Open(), archive);
        }
        catch
        {
            archive.Dispose();
            throw;
        }
    }
}