using UotanStudio.Comm.Protocols.SpacemiT;

namespace UotanStudio.Services.Flash;

/// <summary>
/// One partition row of a SpacemiT flash package's primary partition table.
/// <para>SpacemiT 烧录包主分区表中的一个分区行。</para>
/// </summary>
/// <param name="Partition">Partition name from the table. <para>分区表中的分区名。</para></param>
/// <param name="FilePath">Resolved image file path, or <see langword="null"/> when the package carries no such
/// file. <para>解析出的镜像文件路径；包内不存在该文件时为 <see langword="null"/>。</para></param>
/// <param name="ImageReference">Raw image reference text from the table, empty when the entry declares none.
/// <para>分区表中的原始镜像引用文本；条目未声明时为空。</para></param>
/// <param name="FileLength">Resolved image length in bytes; 0 when unresolved.
/// <para>解析出的镜像字节数；未解析时为 0。</para></param>
/// <param name="OffsetBytes">Partition offset in bytes; negative when the table omits it.
/// <para>分区偏移字节数；分区表未给出时为负。</para></param>
/// <param name="SizeBytes">Partition size in bytes; negative when the table leaves it open-ended.
/// <para>分区大小字节数；分区表声明"直到存储末尾"时为负。</para></param>
/// <param name="TableFormat">Format of the partition table the row came from ("gpt" or "mtd").
/// <para>该行所属分区表的格式（"gpt" 或 "mtd"）。</para></param>
public sealed record SpacemiTPackagePartition(
    string Partition,
    string? FilePath,
    string ImageReference,
    long FileLength,
    long OffsetBytes,
    long SizeBytes,
    string TableFormat);

/// <summary>
/// Contents of a SpacemiT flash package folder (the titan flasher layout with <c>fastboot.yaml</c> at its
/// root): the loaded package plus the rows of its primary partition table.
/// <para>SpacemiT 烧录包目录（根目录含 <c>fastboot.yaml</c> 的 titan 烧录器布局）的内容：已加载的包与
/// 主分区表的各行。</para>
/// </summary>
/// <param name="Package">The validated flash package. <para>校验通过的烧录包。</para></param>
/// <param name="PartitionTablePath">Path of the partition table the rows came from, empty when the package
/// carries none. <para>各行所属分区表的路径；包内没有分区表时为空。</para></param>
/// <param name="Partitions">Partition rows in table order. <para>按分区表顺序排列的分区行。</para></param>
public sealed record SpacemiTPackageContents(
    SpacemiTFlashPackage Package,
    string PartitionTablePath,
    IReadOnlyList<SpacemiTPackagePartition> Partitions);

/// <summary>
/// Reads a SpacemiT flash package folder: the <c>fastboot.yaml</c> marker validates the layout, and the
/// primary <c>partition_*.json</c> table (<c>partition_universal.json</c> preferred) provides one row per
/// partition whose image reference resolves inside the package. Multi-image entries display their first
/// reference; vendor regex markers stay unresolved rows.
/// <para>读取 SpacemiT 烧录包目录：<c>fastboot.yaml</c> 标记校验布局，主 <c>partition_*.json</c> 表
/// （优先 <c>partition_universal.json</c>）为每个镜像引用能在包内解析的分区提供一行。多镜像条目显示其
/// 第一个引用；厂商正则标记保留为未解析行。</para>
/// </summary>
public static class FlashSpacemiTPackage
{
    /// <summary>Partition table file preferred when several tables ship in one package.
    /// <para>包内存在多张分区表时优先使用的分区表文件。</para></summary>
    private const string UniversalTable = "partition_universal.json";

    /// <summary>
    /// Whether a folder has the SpacemiT flash package layout.
    /// <para>判断目录是否符合 SpacemiT 烧录包布局。</para>
    /// </summary>
    /// <param name="folder">Directory to inspect. <para>待检查目录。</para></param>
    /// <returns><see langword="true"/> when the folder carries <c>fastboot.yaml</c> at its root.
    /// <para>目录根下带有 <c>fastboot.yaml</c> 时为 <see langword="true"/>。</para></returns>
    public static bool HasStructure(string folder)
        => Directory.Exists(folder) && File.Exists(Path.Combine(folder, SpacemiTFlashPackage.ScriptFileName));

    /// <summary>
    /// Reads a SpacemiT flash package folder.
    /// <para>读取 SpacemiT 烧录包目录。</para>
    /// </summary>
    /// <param name="folder">Package root directory. <para>包根目录。</para></param>
    /// <returns>The package contents, or <see langword="null"/> when the folder lacks <c>fastboot.yaml</c>.
    /// <para>包内容；目录缺少 <c>fastboot.yaml</c> 时为 <see langword="null"/>。</para></returns>
    /// <exception cref="DirectoryNotFoundException">The directory does not exist or the script file is missing.
    /// <para>目录不存在或脚本文件缺失。</para></exception>
    /// <exception cref="InvalidDataException">The script or the primary partition table is not valid.
    /// <para>脚本或主分区表无效。</para></exception>
    /// <exception cref="IOException">The script or the primary partition table cannot be read.
    /// <para>脚本或主分区表无法读取。</para></exception>
    public static SpacemiTPackageContents? Read(string folder)
    {
        if (!HasStructure(folder))
        {
            return null;
        }

        var package = SpacemiTFlashPackage.Load(folder);
        var tablePath = SelectPartitionTable(folder);
        var partitions = new List<SpacemiTPackagePartition>();
        if (tablePath is not null)
        {
            var table = SpacemiTPartitionTable.Load(tablePath);
            foreach (var entry in table.Partitions)
            {
                var reference = entry.Images.Count > 0 ? entry.Images[0] : string.Empty;
                var resolved = reference.Length > 0 ? package.ResolveFile(reference) : null;
                partitions.Add(new SpacemiTPackagePartition(entry.Name, resolved, reference,
                    resolved is { } path ? new FileInfo(path).Length : 0,
                    entry.Offset, entry.Size, table.Format));
            }
        }

        return new SpacemiTPackageContents(package, tablePath ?? string.Empty, partitions);
    }

    /// <summary>
    /// Picks the primary <c>partition_*.json</c> table: <c>partition_universal.json</c> when present,
    /// otherwise the alphabetically first one.
    /// <para>挑选主 <c>partition_*.json</c> 分区表：存在 <c>partition_universal.json</c> 时用它，否则取
    /// 名称排序最靠前的一个。</para>
    /// </summary>
    /// <param name="folder">Package root directory. <para>包根目录。</para></param>
    /// <returns>The table path, or <see langword="null"/> when the package has none.
    /// <para>分区表路径；包内没有时为 <see langword="null"/>。</para></returns>
    private static string? SelectPartitionTable(string folder)
    {
        string? first = null;
        string? universal = null;
        foreach (var path in Directory.EnumerateFiles(folder, "partition_*.json", SearchOption.TopDirectoryOnly)
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            first ??= path;
            if (universal is null &&
                string.Equals(Path.GetFileName(path), UniversalTable, StringComparison.OrdinalIgnoreCase))
            {
                universal = path;
            }
        }

        return universal ?? first;
    }
}