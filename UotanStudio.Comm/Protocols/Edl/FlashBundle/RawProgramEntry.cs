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
    /// <value>Value of the SECTOR_SIZE_IN_BYTES attribute; 0 when the XML omits it, in which case the bundle uses the configured or overridden sector size.
    /// <para>SECTOR_SIZE_IN_BYTES 属性的值；XML 未给出时为 0，此时本包使用配置或覆盖的扇区大小。</para></value>
    public long SectorSizeInBytes { get; set; }

    /// <summary>
    /// Gets or sets the number of partition sectors to program.
    /// <para>获取或设置待编程的分区扇区数量。</para>
    /// </summary>
    /// <value>Value of num_partition_sectors; 0 derives the count from the source length rounded up to a sector, and a filename-less entry with 0 sectors is skipped instead of erased.
    /// <para>num_partition_sectors 的值；0 表示按源文件长度向上取整到扇区推导；无文件名且为 0 的条目会被跳过而不是擦除。</para></value>
    public long NumPartitionSectors { get; set; }

    /// <summary>
    /// Gets or sets the filename of the image to program.
    /// <para>获取或设置待编程镜像的文件名。</para>
    /// </summary>
    /// <value>Value of the filename attribute; empty marks the entry as an erase request. A non-empty name is resolved against the bundle image directory into <see cref="SourceFile"/>.
    /// <para>filename 属性的值；为空表示该条目是擦除请求。非空文件名会按本包镜像目录解析并写入 <see cref="SourceFile"/>。</para></value>
    public string Filename { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the physical partition number on the target storage.
    /// <para>获取或设置目标存储的物理分区编号。</para>
    /// </summary>
    /// <value>Value of physical_partition_number (the LUN on UFS), defaulting to 0; must be non-negative.
    /// <para>physical_partition_number 的值（UFS 上即 LUN），默认为 0；必须非负。</para></value>
    public int PhysicalPartitionNumber { get; set; }

    /// <summary>
    /// Gets or sets the start sector address, which can be numeric or a label string.
    /// <para>获取或设置起始扇区地址，可以是数字或标签字符串。</para>
    /// </summary>
    /// <value>The raw start_sector string, default "0"; it is later parsed as decimal, <c>0x</c>-prefixed hex, or a <c>NUM_DISK_SECTORS-</c> relative form with an optional trailing dot.
    /// <para>原始 start_sector 字符串，默认 "0"；后续按十进制、<c>0x</c> 前缀十六进制或可带结尾句点的 <c>NUM_DISK_SECTORS-</c> 相对形式解析。</para></value>
    public string StartSector { get; set; } = "0";

    /// <summary>
    /// Gets or sets the file sector offset within the image file.
    /// <para>获取或设置镜像文件内的文件扇区偏移。</para>
    /// </summary>
    /// <value>Value of file_sector_offset; programming starts at that sector inside the source file, and the offset must be 0 for sparse images.
    /// <para>file_sector_offset 的值；编程从源文件内该扇区开始，sparse 镜像必须为 0。</para></value>
    public long FileSectorOffset { get; set; }

    /// <summary>
    /// Gets or sets the partition label name.
    /// <para>获取或设置分区标签名称。</para>
    /// </summary>
    /// <value>Value of the label attribute or empty; drives the case-insensitive label inclusion and exclusion filters and the progress display name.
    /// <para>label 属性的值或空字符串；驱动不区分大小写的标签包含/排除过滤，并用于进度显示名称。</para></value>
    public string Label { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the image file is in sparse format.
    /// <para>获取或设置一个值，指示镜像文件是否为 sparse 格式。</para>
    /// </summary>
    /// <value>True when the sparse attribute is <c>true</c> or <c>1</c>; false entries are still sniffed for the sparse header magic before choosing the transfer path.
    /// <para>当 sparse 属性为 <c>true</c> 或 <c>1</c> 时为 true；即使为 false，选择传输路径前仍会嗅探 sparse 头魔数。</para></value>
    public bool IsSparse { get; set; }

    /// <summary>
    /// Gets or sets the uncompressed file size in bytes when the image is sparse.
    /// <para>获取或设置当镜像为 sparse 格式时的未压缩文件大小（字节）。</para>
    /// </summary>
    /// <value>Value of UNSPARSE_FILE_SIZE; 0 when the XML omits it, otherwise preflight requires it to equal the sparse image's logical length.
    /// <para>UNSPARSE_FILE_SIZE 的值；XML 未给出时为 0，否则预检要求其等于 sparse 镜像的逻辑长度。</para></value>
    public long UnsparseFileSize { get; set; }

    /// <summary>
    /// Gets or sets the number of pages per block (for NAND storage).
    /// <para>获取或设置每块的页数（用于 NAND 存储）。</para>
    /// </summary>
    /// <value>Value of the PAGES_PER_BLOCK attribute, 0 when the XML omits it; kept from the manifest for NAND geometry.
    /// <para>PAGES_PER_BLOCK 属性的值，XML 未给出时为 0；按清单保留以供 NAND 几何使用。</para></value>
    public int PagesPerBlock { get; set; }

    /// <summary>
    /// Gets or sets the last sector number (for NAND storage).
    /// <para>获取或设置最后扇区编号（用于 NAND 存储）。</para>
    /// </summary>
    /// <value>Value of the last_sector attribute, 0 when the XML omits it; kept from the manifest for NAND geometry.
    /// <para>last_sector 属性的值，XML 未给出时为 0；按清单保留以供 NAND 几何使用。</para></value>
    public long LastSector { get; set; }

    /// <summary>
    /// Gets or sets the slot number for dual-slot devices. Null indicates no slot specified.
    /// <para>获取或设置双槽位设备的槽位编号。Null 表示未指定槽位。</para>
    /// </summary>
    /// <value>Value of the slot attribute when present, otherwise <see langword="null"/>; the bundle's effective slot comes from <see cref="FlashBundleOptions.Slot"/> or the Firehose configuration.
    /// <para>存在 slot 属性时为其值，否则为 <see langword="null"/>；本包生效的槽位取自 <see cref="FlashBundleOptions.Slot"/> 或 Firehose 配置。</para></value>
    public int? Slot { get; set; }

    /// <summary>
    /// Gets or sets the full path to the source image file on disk (resolved against the
    /// firmware directory during parsing).
    /// <para>获取或设置磁盘上源镜像文件的完整路径（解析时按固件目录解析）。</para>
    /// </summary>
    /// <value>Resolved absolute path of <see cref="Filename"/> against the firmware or image directory; empty for erase-only entries, and programming fails when it stays empty.
    /// <para><see cref="Filename"/> 按固件/镜像目录解析出的绝对路径；纯擦除条目为空，仍为空时编程会失败。</para></value>
    public string SourceFile { get; set; } = string.Empty;

    /// <summary>
    /// Returns a string representation of the raw program entry.
    /// <para>返回 rawprogram 条目的字符串表示。</para>
    /// </summary>
    /// <returns>A string describing the program entry. 描述 program 条目的字符串。</returns>
    public override string ToString() =>
        $"RawProgram[Label={Label}, File={Filename}, StartSector={StartSector}, Sectors={NumPartitionSectors}]";
}