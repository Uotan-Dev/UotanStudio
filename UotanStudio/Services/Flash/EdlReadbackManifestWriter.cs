using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace UotanStudio.Services.Flash;

/// <summary>
/// <para>回读清单描述：一个已落盘分区镜像及其在目标存储上的几何信息。</para>
/// Describes one read-back dump: a partition image on disk plus its geometry on the target storage.
/// </summary>
public sealed record EdlReadbackManifestEntry
{
    /// <summary>
    /// <para>分区标签名，与 GPT 分区名一致。</para>
    /// The partition label, matching the GPT partition name.
    /// </summary>
    public required string PartitionName { get; init; }

    /// <summary>
    /// <para>起始扇区（分区在该 LUN 上的绝对扇区号）。</para>
    /// The start sector (absolute sector number on the LUN).
    /// </summary>
    public required long StartSector { get; init; }

    /// <summary>
    /// <para>分区扇区数，与落盘镜像长度一致。</para>
    /// The partition sector count, matching the dumped image length.
    /// </summary>
    public required long SectorCount { get; init; }

    /// <summary>
    /// <para>物理分区号（UFS 上即 LUN 编号）。</para>
    /// The physical partition number (the LUN on UFS).
    /// </summary>
    public required int PhysicalPartitionNumber { get; init; }

    /// <summary>
    /// <para>落盘镜像的文件名（不含路径）。</para>
    /// The dumped image file name (without path).
    /// </summary>
    public required string FileName { get; init; }
}

/// <summary>
/// <para>EDL 回读清单生成器：把回读结果写成 rawprogram*.xml，供其他工具或再次刷写使用。
/// 标准模式记录本机存储的精确扇区几何；通用化模式在与磁盘末尾相邻处改写为
/// <c>NUM_DISK_SECTORS-</c> 相对起始扇区，使清单可适配不同容量的同代存储。</para>
/// EDL read-back manifest writer: turns read-back results into a rawprogram*.xml for other
/// tools or re-flashing. Standard mode records the exact sector geometry of this device;
/// generalized mode rewrites start sectors that abut the end of the disk as relative
/// <c>NUM_DISK_SECTORS-</c> expressions so the manifest adapts to same-generation storage
/// of different capacities.
/// </summary>
public static class EdlReadbackManifestWriter
{
    /// <summary>
    /// <para>生成 rawprogram XML 文本。输出条目顺序与 <paramref name="entries"/> 一致；
    /// 通用化模式只重写确实与磁盘末尾相邻的条目，其余起始扇区保持原值。</para>
    /// Generates the rawprogram XML text. Output entries keep the order of
    /// <paramref name="entries"/>; generalized mode rewrites only entries that truly abut the
    /// end of the disk, leaving all other start sectors untouched.
    /// </summary>
    /// <param name="entries"><para>回读条目，每条对应一个已落盘镜像。</para>
    /// The read-back entries, one per dumped image.</param>
    /// <param name="sectorSizeInBytes"><para>目标存储扇区大小（字节），必须为正。</para>
    /// The target storage sector size in bytes; must be positive.</param>
    /// <param name="numDiskSectors"><para>所在 LUN 的总扇区数，通用化模式必需；标准模式可为 0。</para>
    /// The total sector count of the LUN, required by generalized mode; 0 is allowed in standard mode.</param>
    /// <param name="generalized"><para>是否生成通用化清单。</para>Whether to generate a generalized manifest.</param>
    /// <returns><para>rawprogram XML 文本。</para>The rawprogram XML text.</returns>
    /// <exception cref="ArgumentException"><para>扇区大小非正，或条目几何非法时抛出。</para>
    /// Thrown when the sector size is not positive or an entry has invalid geometry.</exception>
    public static string Generate(
        IReadOnlyList<EdlReadbackManifestEntry> entries, long sectorSizeInBytes,
        long numDiskSectors, bool generalized)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (sectorSizeInBytes <= 0)
        {
            throw new ArgumentException("Sector size must be positive.", nameof(sectorSizeInBytes));
        }

        if (entries.Count == 0)
        {
            throw new ArgumentException("At least one read-back entry is required.", nameof(entries));
        }

        var document = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement("data"));
        foreach (var entry in entries)
        {
            ValidateEntry(entry);
            var startSector = FormatStartSector(entry, numDiskSectors, generalized);
            document.Root!.Add(new XElement("program",
                new XAttribute("SECTOR_SIZE_IN_BYTES", sectorSizeInBytes.ToString(CultureInfo.InvariantCulture)),
                new XAttribute("file", entry.FileName),
                new XAttribute("label", entry.PartitionName),
                new XAttribute("start_sector", startSector),
                new XAttribute("num_partition_sectors", entry.SectorCount.ToString(CultureInfo.InvariantCulture)),
                new XAttribute("physical_partition_number",
                    entry.PhysicalPartitionNumber.ToString(CultureInfo.InvariantCulture))));
        }

        var builder = new StringBuilder();
        builder.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        builder.Append(document.Root);
        return builder.ToString();
    }

    /// <summary>
    /// <para>把清单写入目标文件；写入先落临时文件，完整成功后原子替换，失败时保留原文件。</para>
    /// Writes the manifest to the target file; the write goes to a temporary file first and is
    /// atomically replaced after the full content is flushed, preserving the original on failure.
    /// </summary>
    /// <param name="destinationPath"><para>目标 rawprogram XML 完整路径。</para>
    /// The full destination path of the rawprogram XML.</param>
    /// <param name="entries"><para>回读条目。</para>The read-back entries.</param>
    /// <param name="sectorSizeInBytes"><para>目标存储扇区大小（字节）。</para>
    /// The target storage sector size in bytes.</param>
    /// <param name="numDiskSectors"><para>所在 LUN 的总扇区数。</para>
    /// The total sector count of the LUN.</param>
    /// <param name="generalized"><para>是否生成通用化清单。</para>Whether to generate a generalized manifest.</param>
    /// <param name="cancellationToken"><para>取消令牌。</para>The cancellation token.</param>
    /// <returns><para>完成写入的任务。</para>A task that completes when the file is written.</returns>
    public static async Task WriteAsync(string destinationPath,
        IReadOnlyList<EdlReadbackManifestEntry> entries, long sectorSizeInBytes,
        long numDiskSectors, bool generalized, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            throw new ArgumentException("Destination path must not be empty.", nameof(destinationPath));
        }

        var content = Generate(entries, sectorSizeInBytes, numDiskSectors, generalized);
        var temporaryPath = destinationPath + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporaryPath, content, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    /// <summary>
    /// <para>格式化起始扇区：通用化模式下与磁盘末尾相邻的条目改写为相对表达式。</para>
    /// Formats the start sector; generalized mode rewrites entries adjacent to the disk end
    /// into a relative expression.
    /// </summary>
    private static string FormatStartSector(EdlReadbackManifestEntry entry, long numDiskSectors, bool generalized)
        => generalized && numDiskSectors > 0 && entry.StartSector + entry.SectorCount == numDiskSectors
            ? string.Create(CultureInfo.InvariantCulture, $"NUM_DISK_SECTORS-{entry.SectorCount}.")
            : entry.StartSector.ToString(CultureInfo.InvariantCulture);

    private static void ValidateEntry(EdlReadbackManifestEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.PartitionName))
        {
            throw new ArgumentException("A read-back entry has an empty partition name.");
        }

        if (string.IsNullOrWhiteSpace(entry.FileName))
        {
            throw new ArgumentException($"Read-back entry '{entry.PartitionName}' has an empty file name.");
        }

        if (entry.StartSector < 0)
        {
            throw new ArgumentException($"Read-back entry '{entry.PartitionName}' has a negative start sector.");
        }

        if (entry.SectorCount <= 0)
        {
            throw new ArgumentException($"Read-back entry '{entry.PartitionName}' has a non-positive sector count.");
        }

        if (entry.PhysicalPartitionNumber < 0)
        {
            throw new ArgumentException($"Read-back entry '{entry.PartitionName}' has a negative LUN number.");
        }
    }
}