using UotanStudio.Firmware.VendorFirmware.Allwinner.CardImage;

namespace UotanStudio.Services.Flash;

/// <summary>
/// Recognizes Allwinner (Sunxi) card material and assembles it into one flashable SD-card image. Two input shapes
/// are accepted, both carrying a vendor <c>sunxi_mbr.fex</c> partition table: a directory of vendor <c>.fex</c>
/// files, or an <c>IMAGEWTY</c> firmware container.
/// <para>识别全志（Sunxi）卡材料并组装为一张可烧录的 SD 卡镜像。接受两种输入形态，二者都携带厂商
/// <c>sunxi_mbr.fex</c> 分区表：厂商 <c>.fex</c> 文件目录，或 <c>IMAGEWTY</c> 固件容器。</para>
/// </summary>
public sealed class SunxiCardPackage
{
    private SunxiCardPackage(string sourcePath, SunxiCardMaterialKind kind, int partitionCount,
        long estimatedImageSizeBytes)
    {
        SourcePath = sourcePath;
        Kind = kind;
        PartitionCount = partitionCount;
        EstimatedImageSizeBytes = estimatedImageSizeBytes;
    }

    /// <summary>
    /// Gets the absolute path of the directory or container the material was read from.
    /// <para>获取读取材料所用的目录或容器的绝对路径。</para>
    /// </summary>
    public string SourcePath { get; }

    /// <summary>
    /// Gets which input shape the material came from. <para>获取材料来自哪种输入形态。</para>
    /// </summary>
    public SunxiCardMaterialKind Kind { get; }

    /// <summary>
    /// Gets the number of partitions the vendor table declares. <para>获取厂商表声明的分区数量。</para>
    /// </summary>
    public int PartitionCount { get; }

    /// <summary>
    /// Gets an estimate, in bytes, of the assembled image, used for the selection summary.
    /// <para>获取组装镜像的字节估算值，用于选择阶段的摘要显示。</para>
    /// </summary>
    public long EstimatedImageSizeBytes { get; }

    /// <summary>
    /// Recognizes Allwinner card material at <paramref name="sourcePath"/>.
    /// <para>识别 <paramref name="sourcePath"/> 处的全志卡材料。</para>
    /// </summary>
    /// <param name="sourcePath">Candidate directory of <c>.fex</c> files, or an <c>IMAGEWTY</c> container.
    /// <para>候选的 <c>.fex</c> 文件目录，或 <c>IMAGEWTY</c> 容器。</para></param>
    /// <param name="cancellationToken">Cancels the recognition. <para>取消识别。</para></param>
    /// <returns>The recognized package, or <see langword="null"/> when the path carries no Sunxi MBR table.
    /// <para>识别出的包；路径不含 Sunxi MBR 表时为 <see langword="null"/>。</para></returns>
    /// <exception cref="InvalidDataException">The material carries a table but its partitions or payloads are
    /// inconsistent. <para>材料带有分区表，但其分区或负载不一致。</para></exception>
    public static SunxiCardPackage? TryOpen(string sourcePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var full = Path.GetFullPath(sourcePath);
        if (!File.Exists(full) && !Directory.Exists(full))
        {
            return null;
        }

        var material = Directory.Exists(full)
            ? SunxiCardMaterialReader.IsFexDirectory(full)
                ? SunxiCardMaterialReader.ReadFexDirectory(full, cancellationToken)
                : null
            : SunxiCardMaterialReader.IsImageWtyCardSource(full, cancellationToken)
                ? SunxiCardMaterialReader.ReadImageWty(full, cancellationToken)
                : null;
        if (material is null)
        {
            return null;
        }

        var estimate = material.DeclaredBytes ?? EstimateFromTable(material);
        return new SunxiCardPackage(full, material.Kind, material.Partitions.Count, estimate);
    }

    /// <summary>
    /// Assembles the flashable card image at <paramref name="destinationPath"/>.
    /// <para>在 <paramref name="destinationPath"/> 组装可烧录的卡镜像。</para>
    /// </summary>
    /// <param name="destinationPath">Destination image path. <para>目标镜像路径。</para></param>
    /// <param name="progress">Optional assembly progress sink. <para>可选的组装进度接收器。</para></param>
    /// <param name="cancellationToken">Cancels the assembly. <para>取消组装。</para></param>
    /// <returns>What the assembly produced. <para>组装产出。</para></returns>
    public SunxiCardAssemblyResult Synthesize(string destinationPath,
        IProgress<SunxiCardAssemblyProgress>? progress = null, CancellationToken cancellationToken = default) =>
        SunxiCardAssembler.Assemble(SourcePath, destinationPath, capacityBytes: null, progress, cancellationToken);

    /// <summary>
    /// Estimates the assembled capacity from the material's own partition ranges plus the backup GPT, so the
    /// drive-compatibility check runs against the real size before anything is written.
    /// <para>由材料自身的分区区间加后备 GPT 估算组装容量，使驱动器兼容性检查在写入之前就按真实大小判断。</para>
    /// </summary>
    private static long EstimateFromTable(SunxiCardMaterialSet material)
    {
        var highestEnd = 0UL;
        foreach (var partition in material.Table)
        {
            var end = partition.FirstSector + partition.SectorCount;
            if (end > highestEnd)
            {
                highestEnd = end;
            }
        }

        var required = checked((long)(highestEnd + 34) * 512);
        return Math.Max(material.DeclaredBytes ?? 0, required);
    }
}