namespace UotanStudio.Services.Flash;

/// <summary>Reads an unpacked collection of explicitly named partition images.</summary>
public static class FlashPackageReader
{
    /// <summary>Returns top-level images from the folder or its images subfolder.</summary>
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