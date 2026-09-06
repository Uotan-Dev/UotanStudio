using UotanStudio.Firmware.Common.Streams;

namespace UotanStudio.Firmware.Rockchip;

public static class RockchipExtractor
{
    private const int ChunkSize = 0x1000;

    /// <summary>
    /// Lists the entries (FDT and image section) within the image.
    /// <para>列出镜像中的条目（FDT 与镜像区）。</para>
    /// </summary>
    public static List<RkfwEntry> List(string imagePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(imagePath);

        using var fs = new FileStream(imagePath, FileMode.Open, FileAccess.Read);
        using var reader = new BinaryReader(fs);
        var head = RkFwHeader.Read(reader);
        ValidateHeader(head, fs.Length);
        return EnumerateEntries(head);
    }

    /// <summary>
    /// Reads image metadata (equivalent to List; used by the handler metadata path).
    /// <para>读取镜像元数据（与 List 等价，供处理器元数据路径调用）。</para>
    /// </summary>
    public static List<RkfwEntry> ReadMetadata(string imagePath) => List(imagePath);

    /// <summary>
    /// Extracts the entries to the output directory (FDT as fdt.dtb, image section as image.bin).
    /// <para>将镜像中的条目提取到输出目录（FDT 写为 fdt.dtb，镜像区写为 image.bin）。</para>
    /// </summary>
    public static int Extract(string imagePath, string outputDirectory, IProgress<double>? progress = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(imagePath);
        ArgumentException.ThrowIfNullOrEmpty(outputDirectory);

        Directory.CreateDirectory(outputDirectory);

        using var fs = new FileStream(imagePath, FileMode.Open, FileAccess.Read);
        using var reader = new BinaryReader(fs);
        var head = RkFwHeader.Read(reader);
        ValidateHeader(head, fs.Length);

        var entries = EnumerateEntries(head);
        var totalBytes = entries.Sum(static e => e.Size);
        long copied = 0;
        foreach (var entry in entries)
        {
            ExtractEntry(fs, entry, outputDirectory);
            copied += entry.Size;
            progress?.Report(totalBytes == 0 ? 1.0 : (double)copied / totalBytes);
        }
        return entries.Count;
    }

    private static List<RkfwEntry> EnumerateEntries(RkFwHeader head)
    {
        var entries = new List<RkfwEntry>(2);
        if (head.FdtSize > 0)
        {
            entries.Add(new RkfwEntry("fdt.dtb", head.FdtOffset, head.FdtSize, RkfwEntryType.Fdt));
        }
        if (head.ImageSize > 0)
        {
            entries.Add(new RkfwEntry("image.bin", head.ImageOffset, head.ImageSize, RkfwEntryType.Image));
        }
        return entries;
    }

    private static void ExtractEntry(FileStream source, RkfwEntry entry, string outputDirectory)
    {
        source.Seek(entry.Offset, SeekOrigin.Begin);
        var targetPath = Path.Combine(outputDirectory, entry.Name);
        var tempPath = targetPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var fo = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                StreamCopy.CopyExactly(source, fo, entry.Size, ChunkSize);
            }
            File.Move(tempPath, targetPath, true);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    private static void ValidateHeader(RkFwHeader head, long fileLength)
    {
        if (head.Tag is not (RkFwHeader.MagicRkfw or RkFwHeader.MagicRkaf))
        {
            throw new InvalidDataException("Not a Rockchip RKAF/RKFW firmware image: bad magic.");
        }
        if (head.HeaderSize < RkFwHeader.StructSize)
        {
            throw new InvalidDataException("Rockchip RKAF/RKFW firmware header_size is too small.");
        }
        if (head.HeaderSize > fileLength)
        {
            throw new InvalidDataException("Rockchip RKAF/RKFW firmware header extends beyond the file.");
        }
        if (head.FdtSize > 0)
        {
            CheckRegion("fdt", head.FdtOffset, head.FdtSize, head.HeaderSize, fileLength);
        }
        if (head.ImageSize > 0)
        {
            CheckRegion("image", head.ImageOffset, head.ImageSize, head.HeaderSize, fileLength);
        }
    }

    private static void CheckRegion(string name, uint offset, uint size, uint headerSize, long fileLength)
    {
        if (offset < headerSize)
        {
            throw new InvalidDataException($"Rockchip RKAF/RKFW firmware {name} region overlaps the header.");
        }
        if (offset > fileLength || size > fileLength - offset)
        {
            throw new InvalidDataException($"Rockchip RKAF/RKFW firmware {name} region is outside the image.");
        }
    }
}
