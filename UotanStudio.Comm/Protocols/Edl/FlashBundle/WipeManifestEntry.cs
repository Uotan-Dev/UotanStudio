namespace UotanStudio.Comm.Protocols.Edl.FlashBundle;

/// <summary>
/// One destructive wipe request parsed from a wipe element of a rawprogram document. Firehose has no wipe verb, so a wipe is a program of zeros over a physical range. The entry keeps the literal attributes and the unresolved start_sector expression; the region only becomes a device address once the target reports its geometry.
/// <para>由 rawprogram 文档的 wipe 元素解析出的一条破坏性擦除请求。Firehose 没有擦除动词，擦除即在物理范围内写入零。条目保留字面属性值与未解析的 start_sector 表达式；只有目标上报几何信息后区域才成为设备地址。</para>
/// </summary>
public sealed class WipeManifestEntry
{
    /// <summary>
    /// The zeros payload filename used when a manifest names none.
    /// <para>清单未命名时使用的零字节负载文件名。</para>
    /// </summary>
    /// <value>The literal <c>Zeros16KB.bin</c>. <para>字面值 <c>Zeros16KB.bin</c>。</para></value>
    public const string DefaultZeroFilename = "Zeros16KB.bin";

    /// <summary>
    /// Gets or sets the sector size in bytes declared by the wipe element.
    /// <para>获取或设置 wipe 元素声明的扇区大小（字节）。</para>
    /// </summary>
    /// <value>Value of SECTOR_SIZE_IN_BYTES; 0 when the XML omits it, in which case the geometry the target
    /// reports supplies the size. <para>SECTOR_SIZE_IN_BYTES 的值；XML 未给出时为 0，此时由目标上报的几何
    /// 信息提供扇区大小。</para></value>
    public long SectorSizeInBytes { get; set; }

    /// <summary>
    /// Gets or sets the number of sectors to overwrite with zeros.
    /// <para>获取或设置要以零字节覆盖的扇区数。</para>
    /// </summary>
    /// <value>Value of num_partition_sectors; ignored when <see cref="EntireStorage"/> is set, and a
    /// non-positive count on a ranged entry is refused before any command is built.
    /// <para>num_partition_sectors 的值；设置 <see cref="EntireStorage"/> 时被忽略；带范围的条目若给出
    /// 非正的计数，会在构造任何命令之前被拒绝。</para></value>
    public long NumPartitionSectors { get; set; }

    /// <summary>
    /// Gets or sets the physical partition number (the LUN on UFS) the wipe addresses.
    /// <para>获取或设置擦写所针对的物理分区编号（UFS 上即 LUN）。</para>
    /// </summary>
    /// <value>Value of physical_partition_number, defaulting to 0; must be non-negative.
    /// <para>physical_partition_number 的值，默认为 0；必须非负。</para></value>
    public int PhysicalPartitionNumber { get; set; }

    /// <summary>
    /// Gets or sets the start sector address, which may be a constant or a disk-relative expression.
    /// <para>获取或设置起始扇区地址，可为常量或磁盘相对表达式。</para>
    /// </summary>
    /// <value>The raw start_sector string, default "0"; resolved through the same bounded grammar as a
    /// program entry, and refused when it does not resolve against the reported geometry.
    /// <para>原始 start_sector 字符串，默认 "0"；按与 program 条目相同的有界文法解析，无法依据上报几何
    /// 解析时会被拒绝。</para></value>
    public string StartSector { get; set; } = "0";

    /// <summary>
    /// Gets or sets the optional partition label carried by the wipe element.
    /// <para>获取或设置 wipe 元素携带的可选分区标签。</para>
    /// </summary>
    /// <value>Value of the label attribute or empty; used as the display name in previews and evidence.
    /// <para>label 属性的值或空字符串；在预览与证据中作为显示名称。</para></value>
    public string Label { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the filename identifier of the zeros payload written by the command.
    /// <para>获取或设置该命令写入的零字节负载文件名标识。</para>
    /// </summary>
    /// <value>Value of the zeros_filename attribute, or <see cref="DefaultZeroFilename"/> when absent.
    /// <para>zeros_filename 属性的值，缺失时为 <see cref="DefaultZeroFilename"/>。</para></value>
    public string ZeroFilename { get; set; } = DefaultZeroFilename;

    /// <summary>
    /// Gets or sets the slot number for dual-slot devices; <see langword="null"/> indicates no slot was given.
    /// <para>获取或设置双槽位设备的槽位编号；<see langword="null"/> 表示未指定槽位。</para>
    /// </summary>
    /// <value>Value of the slot attribute when present, otherwise <see langword="null"/>.
    /// <para>存在 slot 属性时为其值，否则为 <see langword="null"/>。</para></value>
    public int? Slot { get; set; }

    /// <summary>
    /// Gets or sets whether the element asks for the whole reported storage region of its partition.
    /// <para>获取或设置该元素是否要求擦除其分区上报的整段存储区域。</para>
    /// </summary>
    /// <value>True when the all attribute is <c>true</c> or <c>1</c>. Such a request is ambiguous by itself:
    /// it erases every partition block, including the partition table, and is executed only when the caller
    /// separately sets <see cref="FlashBundleOptions.AllowEntireStorageWipe"/>.
    /// <para>当 all 属性为 <c>true</c> 或 <c>1</c> 时为 true。这类请求本身含义并不明确：它会擦除分区的
    /// 每一个块，包含分区表，只有在调用方另行设置 <see cref="FlashBundleOptions.AllowEntireStorageWipe"/>
    /// 后才会执行。</para></value>
    public bool EntireStorage { get; set; }

    /// <summary>
    /// Builds the plan-time physical range for a ranged wipe, or <see langword="null"/> for a whole-storage
    /// request whose length only the reported geometry can supply.
    /// <para>为带范围的擦写构造计划期物理范围；整段存储请求的长度只能由上报几何提供，因此返回
    /// <see langword="null"/>。</para>
    /// </summary>
    /// <param name="sectorSize">The sector size the bundle was planned with.
    /// <para>本包规划所用的扇区大小。</para></param>
    /// <returns>The validated plan range, or <see langword="null"/> for a whole-storage request.
    /// <para>经校验的计划范围；整段存储请求返回 <see langword="null"/>。</para></returns>
    /// <exception cref="ArgumentException">The <see cref="StartSector"/> expression violates the bounded
    /// address grammar. <para><see cref="StartSector"/> 表达式违反有界地址文法。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">The sector count is not positive, the partition or
    /// sector size is out of range, or the absolute range overflows.
    /// <para>扇区数非正、分区或扇区大小越界，或绝对范围溢出。</para></exception>
    public EdlPhysicalRange? ToPlanRange(long sectorSize) => EntireStorage
        ? null
        : EdlPhysicalRange.Parse(StartSector, NumPartitionSectors, PhysicalPartitionNumber, sectorSize, Slot);

    /// <summary>
    /// Returns a string representation of the wipe manifest entry.
    /// <para>返回擦写清单条目的字符串表示。</para>
    /// </summary>
    /// <returns>A string describing the pending wipe. <para>描述待执行擦写的字符串。</para></returns>
    public override string ToString() =>
        $"Wipe[Label={Label}, Partition={PhysicalPartitionNumber}, StartSector={StartSector}, " +
        $"Sectors={(EntireStorage ? "all" : NumPartitionSectors.ToString(System.Globalization.CultureInfo.InvariantCulture))}]";
}