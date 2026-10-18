namespace UotanStudio.Comm.Protocols.Edl;

/// <summary>
/// Validated geometry reported for one explicitly queried physical storage region.
/// <para>为一个显式查询的物理存储区域所报告并经校验的几何信息。</para>
/// </summary>
public sealed record FirehoseStorageGeometry
{
    /// <summary>Creates a geometry with checked sector and byte capacities.
    /// <para>创建带有经校验扇区与字节容量的几何信息。</para></summary>
    /// <param name="physicalPartitionNumber">The queried physical region or LUN.
    /// <para>被查询的物理区域或 LUN。</para></param>
    /// <param name="sectorSize">The reported block size in bytes.<para>所报告的块大小（字节）。</para></param>
    /// <param name="sectorCount">The reported total block count.<para>所报告的总块数。</para></param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="physicalPartitionNumber"/> is negative, <paramref name="sectorSize"/> is zero, negative or greater than 65536, or <paramref name="sectorCount"/> is zero or negative.
    /// <para><paramref name="physicalPartitionNumber"/> 为负、<paramref name="sectorSize"/> 为零、负值或大于 65536，或 <paramref name="sectorCount"/> 为零或负值。</para></exception>
    public FirehoseStorageGeometry(int physicalPartitionNumber, long sectorSize, long sectorCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(physicalPartitionNumber);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sectorSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sectorSize, 65536);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sectorCount);
        PhysicalPartitionNumber = physicalPartitionNumber;
        SectorSize = sectorSize;
        SectorCount = sectorCount;
        CapacityBytes = checked(sectorSize * sectorCount);
    }

    /// <summary>
    /// Gets the physical region whose command produced this report.
    /// <para>产生本报告所查询的物理区域。</para>
    /// </summary>
    /// <value>The physical partition number passed to the query. <para>传入该查询的物理分区编号。</para></value>
    public int PhysicalPartitionNumber { get; }
    /// <summary>
    /// Gets the block size in bytes.
    /// <para>获取块大小（字节）。</para>
    /// </summary>
    /// <value>A block size between 1 and 65536 bytes. <para>介于 1 与 65536 字节之间的块大小。</para></value>
    public long SectorSize { get; }
    /// <summary>
    /// Gets the number of addressable blocks.
    /// <para>获取可寻址块的数量。</para>
    /// </summary>
    /// <value>A strictly positive block count. <para>严格为正的块数。</para></value>
    public long SectorCount { get; }
    /// <summary>
    /// Gets the checked byte capacity.
    /// <para>获取经校验的字节容量。</para>
    /// </summary>
    /// <value><see cref="SectorSize"/> multiplied by <see cref="SectorCount"/>, throwing on overflow.
    /// <para><see cref="SectorSize"/> 乘以 <see cref="SectorCount"/>，溢出时抛出异常。</para></value>
    public long CapacityBytes { get; }

    /// <summary>
    /// Resolves a constant or disk-relative range and rejects any address beyond storage.
    /// <para>解析常量或磁盘相对范围，并拒绝任何超出存储容量的地址。</para>
    /// </summary>
    /// <param name="startSector">
    /// A constant or NUM_DISK_SECTORS-minus-offset expression. <para>常量或 NUM_DISK_SECTORS 减去偏移的表达式。</para>
    /// </param>
    /// <param name="numSectors">
    /// The positive range length in blocks. <para>以块表示的正数范围长度。</para>
    /// </param>
    /// <returns>The resolved absolute starting sector.
    /// <para>解析后的绝对起始扇区。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The resolved range extends beyond <see cref="SectorCount"/>.
    /// <para>解析后的范围超出 <see cref="SectorCount"/>。</para>
    /// </exception>
    public long ResolveRange(string startSector, long numSectors)
    {
        var address = FirehoseSectorAddress.Parse(startSector);
        address.ValidateRange(numSectors);
        var start = address.RelativeToEnd ? checked(SectorCount - address.Value) : address.Value;
        if (start < 0 || start > SectorCount || numSectors > SectorCount - start)
        {
            throw new ArgumentOutOfRangeException(nameof(startSector), "The range exceeds the reported physical storage capacity.");
        }
        return start;
    }
}