using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace UotanStudio.Services.Flash;

/// <summary>
/// Describes one read-back dump: a partition image on disk plus its geometry on the target storage.
/// <para>回读清单描述：一个已落盘分区镜像及其在目标存储上的几何信息。</para>
/// </summary>
public sealed record EdlReadbackManifestEntry
{
    /// <summary>
    /// The partition label, matching the GPT partition name.
    /// <para>分区标签名，与 GPT 分区名一致。</para>
    /// </summary>
    /// <value>The GPT partition name carried as the label. <para>作为标签携带的 GPT 分区名。</para></value>
    public required string PartitionName { get; init; }

    /// <summary>
    /// The start sector (absolute sector number on the LUN).
    /// <para>起始扇区（分区在该 LUN 上的绝对扇区号）。</para>
    /// </summary>
    /// <value>The absolute start sector number on the LUN. <para>分区在该 LUN 上的绝对起始扇区号。</para></value>
    public required long StartSector { get; init; }

    /// <summary>
    /// The partition sector count, matching the dumped image length.
    /// <para>分区扇区数，与落盘镜像长度一致。</para>
    /// </summary>
    /// <value>The sector count matching the dumped image length. <para>与落盘镜像长度一致的扇区数。</para></value>
    public required long SectorCount { get; init; }

    /// <summary>
    /// The physical partition number (the LUN on UFS).
    /// <para>物理分区号（UFS 上即 LUN 编号）。</para>
    /// </summary>
    /// <value>The physical partition number, the LUN on UFS. <para>物理分区号，即 UFS 上的 LUN 编号。</para></value>
    public required int PhysicalPartitionNumber { get; init; }

    /// <summary>
    /// The dumped image file name (without path).
    /// <para>落盘镜像的文件名（不含路径）。</para>
    /// </summary>
    /// <value>The dumped image file name without path. <para>不含路径的落盘镜像文件名。</para></value>
    public required string FileName { get; init; }
}

/// <summary>
/// Writes EDL read-back results into a rawprogram XML manifest. Generalized mode rewrites
/// start sectors that abut the end of the disk as relative <c>NUM_DISK_SECTORS-</c>
/// expressions.
/// <para>把 EDL 回读结果写成 rawprogram XML 清单。通用化模式将与磁盘末尾相邻的起始扇区
/// 改写为 <c>NUM_DISK_SECTORS-</c> 相对表达式。</para>
/// </summary>
public static class EdlReadbackManifestWriter
{
    /// <summary>
    /// Generates the rawprogram XML text. Output entries keep the order of
    /// <paramref name="entries"/>; generalized mode rewrites only entries that truly abut the
    /// end of the disk, leaving all other start sectors untouched.
    /// <para>生成 rawprogram XML 文本。输出条目顺序与 <paramref name="entries"/> 一致；
    /// 通用化模式只重写确实与磁盘末尾相邻的条目，其余起始扇区保持原值。</para>
    /// </summary>
    /// <param name="entries">The read-back entries, one per dumped image. <para>回读条目，每条对应一个已落盘镜像。</para></param>
    /// <param name="sectorSizeInBytes">The target storage sector size in bytes; must be positive. <para>目标存储扇区大小（字节），必须为正。</para></param>
    /// <param name="numDiskSectors">Total sector count of the LUN; required by generalized mode, 0 allowed in standard mode. <para>所在 LUN 的总扇区数，通用化模式必需，标准模式可为 0。</para></param>
    /// <param name="generalized">Whether to generate a generalized manifest. <para>是否生成通用化清单。</para></param>
    /// <returns>The rawprogram XML text. <para>rawprogram XML 文本。</para></returns>
    /// <exception cref="ArgumentException">Thrown when the sector size is not positive or an entry has invalid geometry. <para>扇区大小非正，或条目几何非法时抛出。</para></exception>
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
                new XAttribute("filename", entry.FileName),
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
    /// Writes the manifest to the target file; the write goes to a temporary file first and is
    /// atomically replaced after the full content is flushed, preserving the original on failure.
    /// <para>把清单写入目标文件；写入先落临时文件，完整成功后原子替换，失败时保留原文件。</para>
    /// </summary>
    /// <param name="destinationPath">The full destination path of the rawprogram XML. <para>目标 rawprogram XML 完整路径。</para></param>
    /// <param name="entries">The read-back entries. <para>回读条目。</para></param>
    /// <param name="sectorSizeInBytes">The target storage sector size in bytes. <para>目标存储扇区大小（字节）。</para></param>
    /// <param name="numDiskSectors">The total sector count of the LUN. <para>所在 LUN 的总扇区数。</para></param>
    /// <param name="generalized">Whether to generate a generalized manifest. <para>是否生成通用化清单。</para></param>
    /// <param name="cancellationToken">The cancellation token. <para>取消令牌。</para></param>
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
    /// Formats the start sector; generalized mode rewrites entries adjacent to the disk end
    /// into a relative expression.
    /// <para>格式化起始扇区：通用化模式下与磁盘末尾相邻的条目改写为相对表达式。</para>
    /// </summary>
    /// <param name="entry">The read-back entry to format. <para>待格式化的回读条目。</para></param>
    /// <param name="numDiskSectors">The total sector count of the LUN. <para>所在 LUN 的总扇区数。</para></param>
    /// <param name="generalized">Whether generalized rewriting is active. <para>通用化改写是否启用。</para></param>
    /// <returns>The formatted start sector expression. <para>格式化后的起始扇区表达式。</para></returns>
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