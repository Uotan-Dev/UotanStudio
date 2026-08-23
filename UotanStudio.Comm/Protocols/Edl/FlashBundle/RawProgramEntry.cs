namespace UotanStudio.Comm.Protocols.Edl.FlashBundle;

/// <summary>
/// Represents a single program entry parsed from a rawprogram XML file.
/// <para>表示从 rawprogram XML 文件解析的单个 program 条目。</para>
/// </summary>
public sealed class RawProgramEntry
{
    /// <summary>
    /// Gets or sets the sector size in bytes for this program entry.
    /// <para>获取或设置此 program 条目的扇区大小（字节）。</para>
    /// </summary>
    public long SectorSizeInBytes { get; set; }

    /// <summary>
    /// Gets or sets the number of partition sectors to program.
    /// <para>获取或设置待编程的分区扇区数量。</para>
    /// </summary>
    public long NumPartitionSectors { get; set; }

    /// <summary>
    /// Gets or sets the filename of the image to program.
    /// <para>获取或设置待编程镜像的文件名。</para>
    /// </summary>
    public string Filename { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the physical partition number on the target storage.
    /// <para>获取或设置目标存储的物理分区编号。</para>
    /// </summary>
    public int PhysicalPartitionNumber { get; set; }

    /// <summary>
    /// Gets or sets the start sector address, which can be numeric or a label string.
    /// <para>获取或设置起始扇区地址，可以是数字或标签字符串。</para>
    /// </summary>
    public string StartSector { get; set; } = "0";

    /// <summary>
    /// Gets or sets the file sector offset within the image file.
    /// <para>获取或设置镜像文件内的文件扇区偏移。</para>
    /// </summary>
    public long FileSectorOffset { get; set; }

    /// <summary>
    /// Gets or sets the partition label name.
    /// <para>获取或设置分区标签名称。</para>
    /// </summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the image file is in sparse format.
    /// <para>获取或设置一个值，指示镜像文件是否为 sparse 格式。</para>
    /// </summary>
    public bool IsSparse { get; set; }

    /// <summary>
    /// Gets or sets the uncompressed file size in bytes when the image is sparse.
    /// <para>获取或设置当镜像为 sparse 格式时的未压缩文件大小（字节）。</para>
    /// </summary>
    public long UnsparseFileSize { get; set; }

    /// <summary>
    /// Gets or sets the number of pages per block (for NAND storage).
    /// <para>获取或设置每块的页数（用于 NAND 存储）。</para>
    /// </summary>
    public int PagesPerBlock { get; set; }

    /// <summary>
    /// Gets or sets the last sector number (for NAND storage).
    /// <para>获取或设置最后扇区编号（用于 NAND 存储）。</para>
    /// </summary>
    public long LastSector { get; set; }

    /// <summary>
    /// Gets or sets the slot number for dual-slot devices. Null indicates no slot specified.
    /// <para>获取或设置双槽位设备的槽位编号。Null 表示未指定槽位。</para>
    /// </summary>
    public int? Slot { get; set; }

    /// <summary>
    /// Gets or sets the full path to the source image file on disk (resolved against the
    /// firmware directory during parsing).
    /// <para>获取或设置磁盘上源镜像文件的完整路径（解析时按固件目录解析）。</para>
    /// </summary>
    public string SourceFile { get; set; } = string.Empty;

    /// <summary>
    /// Returns a string representation of the raw program entry.
    /// <para>返回 rawprogram 条目的字符串表示。</para>
    /// </summary>
    /// <returns>A string describing the program entry. 描述 program 条目的字符串。</returns>
    public override string ToString() =>
        $"RawProgram[Label={Label}, File={Filename}, StartSector={StartSector}, Sectors={NumPartitionSectors}]";
}