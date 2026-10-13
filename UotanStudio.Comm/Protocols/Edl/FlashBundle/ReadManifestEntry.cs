namespace UotanStudio.Comm.Protocols.Edl.FlashBundle;

/// <summary>
/// Represents a single read-back request parsed from a <c>&lt;read&gt;</c> element of a read
/// manifest. A read element names its destination file in <c>filename</c> and the flash range to dump in
/// <c>physical_partition_number</c>, <c>start_sector</c> and <c>num_partition_sectors</c>.
/// <para>表示从回读清单中单个 <c>&lt;read&gt;</c> 元素解析出的回读请求。read 元素以 <c>filename</c>
/// 指定目标文件，以 <c>physical_partition_number</c>、<c>start_sector</c> 与 <c>num_partition_sectors</c>
/// 指定要导出的闪存范围。</para>
/// </summary>
public sealed class ReadManifestEntry
{
    /// <summary>
    /// Gets or sets the sector size in bytes declared by the read element.
    /// <para>获取或设置 read 元素声明的扇区大小（字节）。</para>
    /// </summary>
    /// <value>Value of SECTOR_SIZE_IN_BYTES; 0 when the XML omits it, letting the host fall back to the
    /// negotiated sector size. <para>SECTOR_SIZE_IN_BYTES 的值；XML 未给出时为 0，由宿主回退到协商扇区大小。</para></value>
    public long SectorSizeInBytes { get; set; }

    /// <summary>
    /// Gets or sets the number of partition sectors to read.
    /// <para>获取或设置待读取的分区扇区数量。</para>
    /// </summary>
    /// <value>Value of num_partition_sectors; the host refuses a non-positive count before issuing a read.
    /// <para>num_partition_sectors 的值；宿主在发出读取前会拒绝非正的计数。</para></value>
    public long NumPartitionSectors { get; set; }

    /// <summary>
    /// Gets or sets the destination filename for the dumped range.
    /// <para>获取或设置导出范围的目标文件名。</para>
    /// </summary>
    /// <value>Value of the filename attribute; empty leaves destination naming to the host.
    /// <para>filename 属性的值；为空时由宿主决定目标命名。</para></value>
    public string Filename { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the physical partition number (the LUN on UFS) the range is read from.
    /// <para>获取或设置读取范围所在的物理分区编号（UFS 上即 LUN）。</para>
    /// </summary>
    /// <value>Value of physical_partition_number, defaulting to 0; must be non-negative.
    /// <para>physical_partition_number 的值，默认为 0；必须非负。</para></value>
    public int PhysicalPartitionNumber { get; set; }

    /// <summary>
    /// Gets or sets the start sector address, which may be a constant or a disk-relative expression.
    /// <para>获取或设置起始扇区地址，可为常量或磁盘相对表达式。</para>
    /// </summary>
    /// <value>The raw start_sector string, default "0"; resolved with the same rules as a program entry
    /// through <see cref="FirehoseSectorAddress.Parse"/>. <para>原始 start_sector 字符串，默认 "0"；通过
    /// <see cref="FirehoseSectorAddress.Parse"/> 以与 program 条目相同的规则解析。</para></value>
    public string StartSector { get; set; } = "0";

    /// <summary>
    /// Gets or sets the optional partition label carried by the read element.
    /// <para>获取或设置 read 元素携带的可选分区标签。</para>
    /// </summary>
    /// <value>Value of the label attribute or empty. <para>label 属性的值或空字符串。</para></value>
    public string Label { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the slot number for dual-slot devices; null indicates no slot was specified.
    /// <para>获取或设置双槽位设备的槽位编号；null 表示未指定槽位。</para>
    /// </summary>
    /// <value>Value of the slot attribute when present, otherwise <see langword="null"/>.
    /// <para>存在 slot 属性时为其值，否则为 <see langword="null"/>。</para></value>
    public int? Slot { get; set; }

    /// <summary>
    /// Resolves the read range against a storage geometry, rejecting any address beyond the reported capacity.
    /// <para>依据存储几何解析读取范围，并拒绝任何超出上报容量的地址。</para>
    /// </summary>
    /// <param name="geometry">The device-reported geometry for this physical partition.
    /// <para>该物理分区由设备上报的几何信息。</para></param>
    /// <returns>The absolute starting sector of the resolved range. <para>解析后范围的绝对起始扇区。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="geometry"/> is null, the sector count is
    /// non-positive, or the resolved range leaves the reported capacity.
    /// <para><paramref name="geometry"/> 为 null、扇区数非正，或解析后的范围超出上报容量。</para></exception>
    public long ResolveStartSector(FirehoseStorageGeometry? geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(NumPartitionSectors);
        return geometry.ResolveRange(StartSector, NumPartitionSectors);
    }

    /// <summary>
    /// Returns a string representation of the read manifest entry.
    /// <para>返回 read 清单条目的字符串表示。</para>
    /// </summary>
    /// <returns>A string describing the read entry. <para>描述该 read 条目的字符串。</para></returns>
    public override string ToString() =>
        $"Read[Label={Label}, File={Filename}, StartSector={StartSector}, Sectors={NumPartitionSectors}]";
}