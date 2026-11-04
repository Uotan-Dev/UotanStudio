using System.IO.Compression;
using UotanStudio.Compress.Zip;

namespace UotanStudio.DD.Images;

/// <summary>
/// Reads image metadata without decoding the whole payload.
/// <para>在不解码全部内容的情况下读取镜像元数据。</para>
/// </summary>
public static class ImageProbe
{
    /// <summary>
    /// Probes an image file and resolves its container, size and ZIP entry.
    /// <para>探测镜像文件并解析其容器、大小与 ZIP 条目。</para>
    /// </summary>
    /// <param name="path">Existing image file.<para>已存在的镜像文件。</para></param>
    /// <returns>The probed image.<para>已探测的镜像。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty or whitespace.
    /// <para><paramref name="path"/> 为空或空白。</para></exception>
    /// <exception cref="FlashException">The file does not exist, its format is unsupported, or a ZIP
    /// archive does not contain exactly one file entry.
    /// <para>文件不存在、格式不受支持，或 ZIP 归档未恰好包含一个文件条目。</para></exception>
    public static FlashImage Probe(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FlashException($"The image file \"{fullPath}\" does not exist.");
        }

        var fileInfo = new FileInfo(fullPath);
        var format = ImageFormatResolver.Resolve(fullPath);
        long? size;
        string? zipEntryName = null;
        switch (format.Container)
        {
            case ImageContainerKind.Raw:
                size = fileInfo.Length;
                break;
            case ImageContainerKind.Zip:
                using (var archive = ZipContainer.OpenRead(fullPath))
                {
                    var entry = SelectSingleEntry(archive);
                    size = entry.Length;
                    zipEntryName = entry.FullName;
                }

                break;
            default:
                size = null;
                break;
        }

        return new FlashImage
        {
            Path = fullPath,
            DisplayName = Path.GetFileName(fullPath),
            Format = format,
            CompressedSize = fileInfo.Length,
            Size = size,
            ZipEntryName = zipEntryName,
        };
    }

    /// <summary>
    /// Selects the only file entry in a ZIP archive, rejecting multi-file archives and directories.
    /// <para>选择 ZIP 归档中唯一的文件条目，拒绝多文件归档与目录。</para>
    /// </summary>
    /// <param name="archive">Open archive.<para>已打开的归档。</para></param>
    /// <returns>The single file entry.<para>唯一的文件条目。</para></returns>
    /// <exception cref="FlashException">Zero or multiple file entries were found.
    /// <para>未找到或找到多个文件条目。</para></exception>
    internal static ZipArchiveEntry SelectSingleEntry(ZipArchive archive)
    {
        var entries = archive.Entries.Where(entry => !entry.FullName.EndsWith('/')).ToArray();
        if (entries.Length != 1)
        {
            var names = entries.Length == 0
                ? "none"
                : string.Join(", ", entries.Select(entry => entry.FullName));
            throw new FlashException(
                $"A ZIP image must contain exactly one file entry, but the archive contains {entries.Length} ({names}).");
        }

        return entries[0];
    }
}