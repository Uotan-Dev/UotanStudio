using UotanStudio.Firmware.Common.Streams;

namespace UotanStudio.Firmware.Rockchip;

/// <summary>
/// <para>提供 Rockchip RKAF/RKFW 固件镜像的条目列表与只读提取。</para>
/// Provides entry listing and read-only extraction for Rockchip RKAF/RKFW firmware images.
/// </summary>
/// <remarks>
/// <para>镜像结构依据 Rockchip 官方 afptool / rkdeveloptool（rkfw.c / afptool.c）公开源码：
/// 头部字段全部为小端序 u32，FDT（DTB 资源）区与镜像区由头部偏移/大小字段定位。
/// 镜像区（image section）内部为 afptool 打包的分区目录，本只读解析器暂将其整体作为
/// image.bin 暴露（不枚举内部分区）。无原生工具（afptool / imgRePackerRK）交叉验证，
/// 格式依据上游公开源码并经测试内自洽回环镜像验证（同 Zucchini 先例）。</para>
/// The image layout follows the Rockchip upstream afptool / rkdeveloptool (rkfw.c / afptool.c)
/// public source: header fields are little-endian u32, and the FDT (DTB resource) region and the
/// image section are located by header offset/size fields. The image section embeds an afptool-packed
/// partition directory; this read-only parser currently exposes it as a single image.bin blob
/// (internal partitions are not enumerated). There is no native-tool cross-validation; the format
/// rests on the upstream public source and is validated by self-consistent synthetic round-trip images.
/// </remarks>
public static class RockchipExtractor
{
    private const int ChunkSize = 0x1000;

    /// <summary>
    /// <para>列出镜像中的条目（FDT 与镜像区）。</para>
    /// Lists the entries (FDT and image section) within the image.
    /// </summary>
    /// <returns>条目列表（名称、偏移、大小、类型）。<para>Entry list (name, offset, size, type).</para></returns>
    /// <exception cref="InvalidDataException">魔数/头部/区间校验失败时抛出。<para>Thrown when magic, header or range validation fails.</para></exception>
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
    /// <para>读取镜像元数据（与 List 等价，供处理器元数据路径调用）。</para>
    /// Reads image metadata (equivalent to <see cref="List"/>; used by the handler metadata path).
    /// </summary>
    public static List<RkfwEntry> ReadMetadata(string imagePath) => List(imagePath);

    /// <summary>
    /// <para>将镜像中的条目提取到输出目录（FDT 写为 fdt.dtb，镜像区写为 image.bin）。</para>
    /// Extracts the entries to the output directory (FDT as fdt.dtb, image section as image.bin).
    /// </summary>
    /// <param name="imagePath">镜像文件路径。<para>The image file path.</para></param>
    /// <param name="outputDirectory">输出目录。<para>The output directory.</para></param>
    /// <param name="progress">可选进度回调（0..1）。<para>Optional progress callback (0..1).</para></param>
    /// <returns>提取的条目数量。<para>The number of extracted entries.</para></returns>
    /// <exception cref="InvalidDataException">魔数/头部/区间校验失败时抛出。<para>Thrown when magic, header or range validation fails.</para></exception>
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