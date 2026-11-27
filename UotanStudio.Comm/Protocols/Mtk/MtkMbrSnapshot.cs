namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>
/// One entry of a Master Boot Record primary partition table, as read from a device.
/// <para>从设备读取的主引导记录主分区表中的一个条目。</para>
/// </summary>
/// <param name="Index">Zero-based table slot, 0 through 3.
/// <para>从零开始的表槽位，取值 0 至 3。</para></param>
/// <param name="Status">The boot indicator byte; <c>0x80</c> marks an active partition and <c>0x00</c> an inactive one.
/// <para>引导指示字节；<c>0x80</c> 表示活动分区，<c>0x00</c> 表示非活动分区。</para></param>
/// <param name="PartitionType">The partition type byte, which selects the on-disk layout inside the extent.
/// <para>分区类型字节，用于选择该区间内的磁盘布局。</para></param>
/// <param name="FirstLba">The first logical block address of the extent, in 512-byte sectors.
/// <para>该区间的首个逻辑块地址，以 512 字节扇区计。</para></param>
/// <param name="SectorCount">The extent length in 512-byte sectors.
/// <para>该区间长度，以 512 字节扇区计。</para></param>
/// <param name="IsExtended">Whether the type byte marks an extended container rather than a filesystem extent.
/// <para>该类型字节是否标记扩展容器而非文件系统区间。</para></param>
public sealed record MtkMbrPartition(
    int Index, byte Status, byte PartitionType, uint FirstLba, uint SectorCount, bool IsExtended)
{
    /// <summary>Gets whether the entry is populated; an all-zero slot is unused.
    /// <para>获取该条目是否已填充；全零槽位表示未使用。</para></summary>
    /// <value><see langword="true"/> when the type byte is nonzero.
    /// <para>类型字节非零时为 <see langword="true"/>。</para></value>
    public bool IsPopulated => PartitionType != 0;
}

/// <summary>One logical partition found by following an extended partition chain.
/// <para>沿扩展分区链找到的一个逻辑分区。</para>
/// </summary>
/// <param name="Index">Zero-based discovery order inside the chain.
/// <para>链内从零开始的发现顺序。</para></param>
/// <param name="Status">The boot indicator byte of the logical entry.
/// <para>逻辑条目的引导指示字节。</para></param>
/// <param name="PartitionType">The partition type byte of the logical entry.
/// <para>逻辑条目的分区类型字节。</para></param>
/// <param name="FirstLba">The absolute first logical block address, already rebased out of the EBR-relative value.
/// <para>绝对首个逻辑块地址，已从 EBR 相对值换算出来。</para></param>
/// <param name="SectorCount">The extent length in 512-byte sectors.
/// <para>该区间长度，以 512 字节扇区计。</para></param>
/// <param name="EbrLba">The absolute LBA of the extended boot record that declared this entry.
/// <para>声明本条目的扩展引导记录的绝对 LBA。</para></param>
public sealed record MtkMbrLogicalPartition(
    int Index, byte Status, byte PartitionType, uint FirstLba, uint SectorCount, uint EbrLba);

/// <summary>
/// A validated Master Boot Record snapshot: the four primary entries plus every logical partition reached through
/// the extended chain. Parsing is fail-closed — a missing <c>0x55AA</c> signature, an out-of-range extent, a
/// cycle in the chain, or an over-long chain is reported as a validation failure instead of being guessed around.
/// <para>已校验的主引导记录快照：四个主分区条目，以及经扩展链到达的每个逻辑分区。解析是 fail-closed 的——
/// 缺少 <c>0x55AA</c> 签名、区间越界、链中出现环，或链过长，都会按校验失败上报，而不是靠猜测绕过。</para>
/// </summary>
public sealed class MtkMbrSnapshot
{
    /// <summary>The size of one MBR/EBR sector and the unit every LBA field uses.
    /// <para>一个 MBR/EBR 扇区的大小，也是所有 LBA 字段使用的单位。</para>
    /// </summary>
    public const int SectorSize = 512;

    /// <summary>The boot signature that must occupy the last two bytes of every MBR and EBR sector.
    /// <para>每个 MBR 与 EBR 扇区最后两字节必须具备的引导签名。</para>
    /// </summary>
    public const ushort BootSignature = 0x55AA;

    /// <summary>The partition type byte that opens an extended container.
    /// <para>开启扩展容器的分区类型字节。</para>
    /// </summary>
    public const byte ExtendedType = 0x05;

    /// <summary>The second extended container type byte, interchangeable with <see cref="ExtendedType"/> on the wire.
    /// <para>第二种扩展容器类型字节，在线上与 <see cref="ExtendedType"/> 等价。</para>
    /// </summary>
    public const byte ExtendedLbaType = 0x0F;

    /// <summary>The upper bound on chain hops, so a corrupt chain cannot spin the reader forever.
    /// <para>链跳数上限，使损坏的链无法让读取器无限空转。</para>
    /// </summary>
    internal const int MaximumChainHops = 128;

    internal MtkMbrSnapshot(byte partitionId, uint totalSectors,
        IReadOnlyList<MtkMbrPartition> primary, IReadOnlyList<MtkMbrLogicalPartition> logical,
        IReadOnlyList<MtkMbrPartition> chainRecords)
    {
        PartitionId = partitionId;
        TotalSectors = totalSectors;
        Primary = primary;
        Logical = logical;
        ChainRecords = chainRecords;
    }

    /// <summary>Gets the storage region this snapshot was read from.
    /// <para>获取读取本快照的存储区域。</para>
    /// </summary>
    /// <value>The discovered Legacy region id.<para>已发现的 Legacy 区域标识。</para></value>
    public byte PartitionId { get; }

    /// <summary>Gets the region capacity expressed in 512-byte sectors, used to bound every extent check.
    /// <para>获取以 512 字节扇区表示的区域容量，用于界定每次区间校验。</para>
    /// </summary>
    /// <value>The number of addressable sectors.<para>可寻址的扇区数。</para></value>
    public uint TotalSectors { get; }

    /// <summary>Gets the four primary table entries in table order, including unused slots.
    /// <para>获取按表顺序排列的四个主分区条目，包含未使用的槽位。</para>
    /// </summary>
    /// <value>Exactly four entries.<para>恰好四个条目。</para></value>
    public IReadOnlyList<MtkMbrPartition> Primary { get; }

    /// <summary>Gets every logical partition reached through the extended chain, in discovery order.
    /// <para>获取经扩展链到达的每个逻辑分区，按发现顺序排列。</para>
    /// </summary>
    /// <value>Empty when no extended container exists.<para>不存在扩展容器时为空。</para></value>
    public IReadOnlyList<MtkMbrLogicalPartition> Logical { get; }

    /// <summary>Gets the raw extended boot records that declared the logical partitions, in chain order.
    /// <para>获取声明各逻辑分区的原始扩展引导记录，按链顺序排列。</para>
    /// </summary>
    /// <value>One record per chain hop, each holding its own four entries.
    /// <para>每跳一条记录，各含自己的四个条目。</para></value>
    public IReadOnlyList<MtkMbrPartition> ChainRecords { get; }

    /// <summary>Gets whether the primary table declares an extended container.
    /// <para>获取主分区表是否声明了扩展容器。</para>
    /// </summary>
    /// <value><see langword="true"/> for type <c>0x05</c> or <c>0x0F</c>.
    /// <para>类型为 <c>0x05</c> 或 <c>0x0F</c> 时为 <see langword="true"/>。</para></value>
    public bool HasExtendedContainer => Primary.Any(static entry => entry.IsExtended);

    /// <summary>Gets every populated extent, primary and logical, in LBA order.
    /// <para>获取每个已填充的区间（主分区与逻辑分区），按 LBA 排序。</para>
    /// </summary>
    /// <returns>The ordered extents.<para>已排序的区间。</para></returns>
    public IReadOnlyList<(uint FirstLba, uint SectorCount, byte PartitionType)> EnumerateExtents()
        => [.. Primary.Where(static entry => entry.IsPopulated && !entry.IsExtended)
                .Select(entry => (entry.FirstLba,
                    ResolveExtentSectorCount(entry.FirstLba, entry.SectorCount, entry.FirstLba, TotalSectors),
                    entry.PartitionType))
                .Concat(Logical.Select(static entry => (entry.FirstLba, entry.SectorCount, entry.PartitionType)))
                .OrderBy(static extent => extent.FirstLba)];

    /// <summary>Resolves a partition's declared sector count to a length bounded by the region.</summary>
    /// <remarks>
    /// An MBR may express "through the end of the device" either with a count of <c>0xFFFFFFFF</c> or with a
    /// start-plus-count whose sum reaches the 32-bit LBA limit. Both forms are clamped to the sectors that
    /// actually remain in the region, so a backup never tries to read past the discovered capacity.
    /// <para>MBR 可以用 <c>0xFFFFFFFF</c> 扇区数，或「起始 LBA 加扇区数恰好到达 32 位 LBA 上限」来表示「一直到设备末尾」；
    /// 两种形式都会收敛到区域中实际剩余的扇区数，备份因此不会越出已发现的容量。</para>
    /// </remarks>
    /// <param name="declaredFirstLba">The entry's own first LBA, in the same coordinate space as its count.
    /// <para>条目自身的起始 LBA，与其扇区数处于同一坐标系。</para></param>
    /// <param name="declaredCount">The raw sector-count field.<para>原始扇区数字段。</para></param>
    /// <param name="absoluteFirstLba">The entry's start LBA rebased into the region.
    /// <para>换算到区域内的条目起始 LBA。</para></param>
    /// <param name="totalSectors">The region capacity in sectors.<para>区域容量，以扇区计。</para></param>
    /// <returns>The bounded sector count, or zero when the entry is empty or starts outside the region.
    /// <para>收敛后的扇区数；条目为空或起点位于区域之外时为零。</para></returns>
    internal static uint ResolveExtentSectorCount(uint declaredFirstLba, uint declaredCount,
        uint absoluteFirstLba, uint totalSectors)
    {
        if (declaredCount == 0 || absoluteFirstLba >= totalSectors)
        {
            return 0;
        }

        var available = totalSectors - absoluteFirstLba;
        if (declaredCount == uint.MaxValue ||
            (declaredFirstLba <= uint.MaxValue - declaredCount &&
             declaredFirstLba + declaredCount == uint.MaxValue))
        {
            return available;
        }

        return declaredCount <= available ? declaredCount : 0;
    }

    /// <summary>Checks whether a sector carries the <c>0x55AA</c> boot signature in byte order.</summary>
    /// <para>按字节顺序检查扇区是否带有 <c>0x55AA</c> 引导签名。</para>
    /// <param name="sector">The MBR sector.<para>MBR 扇区。</para></param>
    /// <returns><see langword="true"/> when the last two bytes are <c>55 AA</c>.
    /// <para>最后两个字节为 <c>55 AA</c> 时为 <see langword="true"/>。</para></returns>
    internal static bool HasBootSignature(ReadOnlySpan<byte> sector)
        => sector.Length >= SectorSize &&
            sector[SectorSize - 2] == 0x55 && sector[SectorSize - 1] == 0xAA;

    /// <summary>Reads the four 16-byte entries starting at byte 446 and classifies each as extended or not.
    /// <para>读取从字节 446 开始的四个 16 字节条目，并判定各自是否为扩展容器。</para>
    /// </summary>
    /// <param name="sector">A full 512-byte MBR or EBR sector.<para>完整的 512 字节 MBR 或 EBR 扇区。</para></param>
    /// <returns>The four entries in table order.<para>按表顺序排列的四个条目。</para></returns>
    internal static MtkMbrPartition[] ReadEntries(ReadOnlySpan<byte> sector)
    {
        var entries = new MtkMbrPartition[4];
        for (var index = 0; index < 4; index++)
        {
            var entry = sector.Slice(446 + (index * 16), 16);
            var type = entry[4];
            entries[index] = new MtkMbrPartition(
                index,
                entry[0],
                type,
                System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]),
                System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]),
                type is ExtendedType or ExtendedLbaType);
        }

        return entries;
    }
}