namespace UotanStudio.Comm.Protocols.Edl;

/// <summary>
/// One device-independent physical storage range bound to the geometry vocabulary used across the
/// whole flash-plan lifecycle.
/// <para>跨刷写计划生命周期使用统一几何词汇描述的一个与设备无关的物理存储范围。</para>
/// Preflight produces these ranges from manifests and every consuming command path validates them
/// against the geometry the target actually reports, so a plan built on one storage snapshot is
/// rejected when the geometry changed (sector size, capacity, or partition identity).
/// <para>预检从清单产出这些范围，各消费命令路径再用目标实际上报的几何信息校验它们，因此基于
/// 某一份存储快照的计划会在几何信息变化后（扇区大小、容量或分区身份）被拒绝。</para>
/// </summary>
/// <remarks>
/// Creates a validated range.
/// <para>创建经校验的范围。</para>
/// </remarks>
/// <param name="StartLba">
/// The start block value. When <paramref name="RelativeToEnd"/> is set this is the positive offset
/// subtracted from the reported disk size, mirroring the <c>NUM_DISK_SECTORS-</c> grammar.
/// <para>起始块值。当 <paramref name="RelativeToEnd"/> 置位时，它是从上报磁盘大小中减去的正偏移，
/// 对应 <c>NUM_DISK_SECTORS-</c> 文法。</para>
/// </param>
/// <param name="BlockCount">The number of blocks in the range; must be positive. <para>范围内的块数；必须为正。</para></param>
/// <param name="PhysicalPartitionNumber">The physical partition or LUN; must be non-negative. <para>物理分区或 LUN；必须非负。</para></param>
/// <param name="SectorSize">The sector size in bytes the range was planned with; between 1 and 65536. <para>规划本范围所用的扇区大小（字节）；介于 1 与 65536。</para></param>
/// <param name="RelativeToEnd">Whether the start is disk-relative. <para>起始地址是否为磁盘相对地址。</para></param>
/// <param name="StorageSlot">The A/B storage slot the range belongs to, when the plan carries one. <para>计划携带槽位时本范围所属的 A/B 存储槽位。</para></param>
/// <param name="StartSectorExpression">The original manifest text for the start sector, retained for
/// byte-exact command re-emission. <para>起始扇区在清单中的原始文本，为逐字节重发命令而保留。</para></param>
/// <exception cref="ArgumentOutOfRangeException">
/// A field violates its bound or the absolute range overflows. <para>某字段越界，或绝对范围溢出。</para>
/// </exception>
public sealed record EdlPhysicalRange(long StartLba, long BlockCount, int PhysicalPartitionNumber,
    long SectorSize, bool RelativeToEnd = false, int? StorageSlot = null, string? StartSectorExpression = null)
{
    /// <summary>
    /// Gets the start block value.
    /// <para>获取起始块值。</para>
    /// </summary>
    /// <value>For absolute ranges the first addressed block; for disk-relative ranges the positive
    /// offset subtracted from the reported disk size. <para>绝对范围为首个被寻址块；磁盘相对范围为从
    /// 上报磁盘大小减去的正偏移。</para></value>
    public long StartLba { get; init; } = ValidateStart(StartLba, RelativeToEnd);

    /// <summary>
    /// Gets the number of addressed blocks.
    /// <para>获取被寻址块的数量。</para>
    /// </summary>
    /// <value>A strictly positive block count. <para>严格为正的块数。</para></value>
    public long BlockCount { get; init; } = ValidateBlocks(BlockCount);

    /// <summary>
    /// Gets the physical partition or LUN the range addresses.
    /// <para>获取本范围寻址的物理分区或 LUN。</para>
    /// </summary>
    /// <value>A non-negative partition number. <para>非负分区编号。</para></value>
    public int PhysicalPartitionNumber { get; init; } = ValidatePartition(PhysicalPartitionNumber);

    /// <summary>
    /// Gets the sector size the range was planned with.
    /// <para>获取规划本范围所用的扇区大小。</para>
    /// </summary>
    /// <value>A sector size between 1 and 65536 bytes. <para>介于 1 与 65536 字节的扇区大小。</para></value>
    public long SectorSize { get; init; } = ValidateSectorSize(SectorSize);

    /// <summary>
    /// Gets whether the start address is evaluated against the end of the disk.
    /// <para>获取起始地址是否相对磁盘末尾求值。</para>
    /// </summary>
    /// <value><see langword="true"/> for <c>NUM_DISK_SECTORS-</c> style starts.
    /// <para><c>NUM_DISK_SECTORS-</c> 风格的起始地址为 <see langword="true"/>。</para></value>
    public bool RelativeToEnd { get; init; } = RelativeToEnd;

    /// <summary>
    /// Gets the A/B storage slot the range belongs to.
    /// <para>获取本范围所属的 A/B 存储槽位。</para>
    /// </summary>
    /// <value>The slot number, or <see langword="null"/> when the plan carries no slot binding.
    /// <para>槽位编号；计划未携带槽位绑定时为 <see langword="null"/>。</para></value>
    public int? StorageSlot { get; init; } = StorageSlot;

    /// <summary>
    /// Gets the original manifest expression for the start sector.
    /// <para>获取起始扇区在清单中的原始表达式。</para>
    /// </summary>
    /// <value>The unmodified attribute text, or <see langword="null"/> for programmatically built
    /// ranges. <para>未经修改的属性文本；程序构造的范围为 <see langword="null"/>。</para></value>
    public string? StartSectorExpression { get; init; } = StartSectorExpression;

    /// <summary>
    /// Parses a manifest start-sector expression into a range without touching the device.
    /// <para>在不接触设备的情况下，把清单起始扇区表达式解析为范围。</para>
    /// </summary>
    /// <param name="startSector">The constant or <c>NUM_DISK_SECTORS-</c> expression.
    /// <para>常量或 <c>NUM_DISK_SECTORS-</c> 表达式。</para></param>
    /// <param name="blockCount">The number of blocks. <para>块数量。</para></param>
    /// <param name="physicalPartitionNumber">The physical partition or LUN. <para>物理分区或 LUN。</para></param>
    /// <param name="sectorSize">The planned sector size in bytes. <para>规划扇区大小（字节）。</para></param>
    /// <param name="storageSlot">The slot binding, when known. <para>已知的槽位绑定。</para></param>
    /// <returns>The validated plan-time range. <para>经校验的计划期范围。</para></returns>
    /// <exception cref="ArgumentException">The expression violates the bounded address grammar.
    /// <para>表达式违反有界地址文法。</para></exception>
    public static EdlPhysicalRange Parse(string startSector, long blockCount, int physicalPartitionNumber,
        long sectorSize, int? storageSlot = null)
    {
        var address = FirehoseSectorAddress.Parse(startSector);
        return new EdlPhysicalRange(address.Value, blockCount, physicalPartitionNumber, sectorSize,
            address.RelativeToEnd, storageSlot, startSector);
    }

    /// <summary>
    /// Resolves the absolute starting block against the target-reported geometry.
    /// <para>依据目标上报的几何信息解析绝对起始块。</para>
    /// </summary>
    /// <param name="geometry">The validated geometry of the same physical partition.
    /// <para>同一物理分区经校验的几何信息。</para></param>
    /// <returns>The absolute start and the unchanged block count. <para>绝对起始值与不变的块数。</para></returns>
    /// <exception cref="InvalidDataException">The geometry sector size contradicts the plan.
    /// <para>几何扇区大小与计划矛盾。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">The resolved range extends beyond the reported capacity.
    /// <para>解析后的范围超出上报容量。</para></exception>
    public (long Start, long Blocks) Resolve(FirehoseStorageGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        RequireGeometryBinding(geometry);
        if (RelativeToEnd && BlockCount > StartLba)
        {
            throw new ArgumentOutOfRangeException(nameof(BlockCount),
                "The disk-relative range extends beyond the end of storage.");
        }

        var start = RelativeToEnd ? checked(geometry.SectorCount - StartLba) : StartLba;
        if (start < 0 || start > geometry.SectorCount || BlockCount > geometry.SectorCount - start)
        {
            throw new ArgumentOutOfRangeException(nameof(geometry),
                "The range exceeds the reported physical storage capacity.");
        }

        return (start, BlockCount);
    }

    /// <summary>
    /// Validates this plan range against the geometry the target currently reports, rejecting plans
    /// built on a stale geometry snapshot.
    /// <para>用目标当前上报的几何信息校验本计划范围，拒绝基于过期几何快照构建的计划。</para>
    /// </summary>
    /// <param name="geometry">The validated geometry of the same physical partition.
    /// <para>同一物理分区经校验的几何信息。</para></param>
    /// <exception cref="InvalidDataException">The geometry sector size contradicts the plan.
    /// <para>几何扇区大小与计划矛盾。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">The resolved range extends beyond the reported capacity.
    /// <para>解析后的范围超出上报容量。</para></exception>
    public void ValidateAgainst(FirehoseStorageGeometry geometry) => _ = Resolve(geometry);

    /// <summary>
    /// Returns a debug representation with the resolved or relative start marker.
    /// <para>返回带解析后或相对起始标记的调试表示。</para>
    /// </summary>
    /// <returns>The range description. <para>范围描述。</para></returns>
    public override string ToString() =>
        $"EdlPhysicalRange[{(RelativeToEnd ? "NUM_DISK_SECTORS-" : string.Empty)}{StartLba
            .ToString(System.Globalization.CultureInfo.InvariantCulture)}+{BlockCount
                .ToString(System.Globalization.CultureInfo.InvariantCulture)} blocks, pp={PhysicalPartitionNumber}, sector={SectorSize}, slot={StorageSlot?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"}]";

    private void RequireGeometryBinding(FirehoseStorageGeometry geometry)
    {
        if (geometry.PhysicalPartitionNumber != PhysicalPartitionNumber)
        {
            throw new InvalidDataException("The storage geometry belongs to a different physical partition than the plan.");
        }

        if (geometry.SectorSize != SectorSize)
        {
            throw new InvalidDataException("The storage geometry sector size changed since the plan was preflighted.");
        }
    }

    private static long ValidateStart(long value, bool relative)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        if (relative && value == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value),
                "A disk-relative start must subtract a positive offset.");
        }

        return value;
    }

    private static long ValidateBlocks(long value)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
        return value;
    }

    private static int ValidatePartition(int value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        return value;
    }

    private static long ValidateSectorSize(long value)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 65536);
        return value;
    }
}