namespace UotanStudio.Services.Flash;

/// <summary>Reads an unpacked collection of explicitly named partition images.<para>读取已解包且名称明确的分区镜像集合。</para></summary>
public static class FlashPackageReader
{
    /// <summary>Returns top-level images from the folder or its images subfolder.<para>返回该目录或其 images 子目录中的顶层镜像。</para></summary>
    /// <param name="folder">Directory holding the unpacked images.<para>存放解包镜像的目录。</para></param>
    /// <returns>The partition names, image paths and lengths.<para>分区名、镜像路径与长度。</para></returns>
    public static IReadOnlyList<(string Partition, string Path, long Length)> Read(string folder)
    {
        if (!Directory.Exists(folder))
        {
            throw new DirectoryNotFoundException(folder);
        }

        var images = Directory.Exists(Path.Combine(folder, "images"))
            ? Path.Combine(folder, "images")
            : folder;
        var entries = new List<(string Partition, string Path, long Length)>();
        foreach (var path in Directory.EnumerateFiles(images, "*", SearchOption.TopDirectoryOnly)
                     .Where(path => path.EndsWith(".img", StringComparison.OrdinalIgnoreCase) ||
                                    path.EndsWith(".img.zst", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var partition = Path.GetFileNameWithoutExtension(path);
            if (path.EndsWith(".img.zst", StringComparison.OrdinalIgnoreCase))
            {
                partition = Path.GetFileNameWithoutExtension(partition);
            }
            if (partition.Length == 0 || !partition.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            {
                continue;
            }
            if (partition.Equals("super_empty", StringComparison.OrdinalIgnoreCase))
            {
                if (!path.EndsWith(".img", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Compressed super_empty is not supported.");
                _ = FlashSuperEmptyService.Prepare(path);
            }

            var length = new FileInfo(path).Length;
            if (length > 0)
            {
                if (entries.Any(entry => entry.Partition.Equals(partition, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidDataException($"Duplicate image for partition {partition}.");
                }
                entries.Add((partition, path, length));
            }
        }

        return entries;
    }
}