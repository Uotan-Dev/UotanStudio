using UotanStudio.Comm.Protocols.Mtk.Storage;

namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>One ordinary region of a region-agnostic flash area with its absolute base offset.
/// <para>区域无关闪存范围内的一个普通区域，附带其绝对基准偏移。</para></summary>
/// <param name="RegionName">The canonical region token the owning family addresses.
/// <para>所属家族用于寻址的规范区域标记。</para></param>
/// <param name="PhysicalIdentifier">The numeric region identifier, or null for a named-only region.
/// <para>数值区域标识符；仅按名称寻址的区域为 null。</para></param>
/// <param name="AbsoluteOffset">The first absolute byte of this region in the walked area.
/// <para>本区域在被遍历区域中的首个绝对字节。</para></param>
/// <param name="CapacityBytes">The reported capacity contributed to the walk, always positive.
/// <para>计入遍历的上报容量，始终为正数。</para></param>
public sealed record MtkFlashAreaRegion(string RegionName, uint? PhysicalIdentifier, long AbsoluteOffset,
    long CapacityBytes);

/// <summary>One contiguous slice of a requested range that lies wholly inside a single region.
/// <para>请求范围中完全落在单个区域内的一段连续切片。</para></summary>
/// <param name="RegionName">The canonical region token carrying this slice.
/// <para>承载该片段的规范区域标记。</para></param>
/// <param name="PhysicalIdentifier">The numeric region identifier, or null for a named-only region.
/// <para>数值区域标识符；仅按名称寻址的区域为 null。</para></param>
/// <param name="AbsoluteOffset">The first absolute byte of the slice. <para>片段的首个绝对字节。</para></param>
/// <param name="RegionOffset">The region-relative offset of the slice. <para>片段相对于区域的偏移。</para></param>
/// <param name="Length">The slice byte count, aligned and positive. <para>片段字节数，已对齐且为正数。</para></param>
public sealed record MtkFlashAreaSegment(string RegionName, uint? PhysicalIdentifier, long AbsoluteOffset,
    long RegionOffset, long Length);

/// <summary>
/// A flat, LUN- and region-agnostic address space built from the ordinary regions a download agent family reported, which is how whole-flash and raw-sector reads are addressed. Regions reserved for a separate authenticated protocol, regions reporting no capacity, and page-oriented NAND geometries never enter this space.
/// <para>由下载代理家族上报的普通区域构成的扁平地址空间，与 LUN 和区域无关，用于整片读取与原始扇区读取的寻址。为独立认证协议预留的区域、未上报容量的区域与页式 NAND 几何一律不纳入。</para>
/// </summary>
public sealed class MtkFlashAreaMap
{
    /// <summary>
    /// The host ceiling for one device transfer inside a walk, mirroring the whole-user-area dump bound.
    /// <para>遍历中单次设备传输的宿主机上限，与整用户区采集的上限一致。</para>
    /// </summary>
    public const int MaximumWalkChunkBytes = 1024 * 1024;

    private const int MaximumRegionCount = 64;

    private MtkFlashAreaMap(MtkStorageDiscoverySource source, MtkXFlashStorageKind kind, int alignmentBytes,
        int chunkBytes, MtkStorageCapabilitySnapshot capability, MtkFlashAreaRegion[] regions, long totalBytes,
        MtkXFlashStorageInfo? xflash, MtkLegacyStorageInfo? legacy, MtkXmlStorageInfo? xml)
    {
        Source = source;
        Kind = kind;
        AlignmentBytes = alignmentBytes;
        ChunkBytes = chunkBytes;
        Capability = capability;
        Regions = Array.AsReadOnly(regions);
        TotalBytes = totalBytes;
        XFlashGeometry = xflash;
        LegacyGeometry = legacy;
        XmlGeometry = xml;
    }

    /// <summary>Gets the discovery path that produced this address space.
    /// <para>获取产生本地址空间的发现路径。</para></summary>
    /// <value>The family whose session methods can consume this map; the others refuse it.
    /// <para>可消费本映射的家族会话方法；其他家族会拒绝它。</para></value>
    public MtkStorageDiscoverySource Source { get; }

    /// <summary>Gets the storage family the reported regions belong to.
    /// <para>获取这些已上报区域所属的存储家族。</para></summary>
    /// <value>eMMC, UFS or NOR; NAND never reaches a walk.
    /// <para>eMMC、UFS 或 NOR；NAND 永远不会进入遍历。</para></value>
    public MtkXFlashStorageKind Kind { get; }

    /// <summary>Gets the byte alignment every walked offset and length must respect.
    /// <para>获取被遍历的每个偏移与长度都必须遵守的字节对齐。</para></summary>
    /// <value>The transfer alignment the owning family enforces, in bytes.
    /// <para>所属家族强制的传输对齐（字节）。</para></value>
    public int AlignmentBytes { get; }

    /// <summary>Gets the chunk bound a walk applies to every transfer except a shorter tail.
    /// <para>获取遍历在除较短尾段外的每次传输上应用的分块上限。</para></summary>
    /// <value>A positive, alignment-respecting byte count no larger than
    /// <see cref="MaximumWalkChunkBytes"/>.
    /// <para>正的、满足对齐且不超过 <see cref="MaximumWalkChunkBytes"/> 的字节数。</para></value>
    public int ChunkBytes { get; }

    /// <summary>Gets the concatenated size of every ordinary region in the walk.
    /// <para>获取遍历中所有普通区域拼接后的大小。</para></summary>
    /// <value>A positive byte count; the walk never spans a region it does not list.
    /// <para>正数字节数；遍历绝不会跨越未列出的区域。</para></value>
    public long TotalBytes { get; }

    /// <summary>Gets the ordinary regions in absolute-address order.
    /// <para>获取按绝对地址顺序排列的普通区域。</para></summary>
    /// <value>At most <c>64</c> regions, each with a positive capacity and no gaps or overlaps.
    /// <para>最多 <c>64</c> 个区域，每个容量为正且无空洞或重叠。</para></value>
    public IReadOnlyList<MtkFlashAreaRegion> Regions { get; }

    /// <summary>Gets the capability projection the walk was derived from.
    /// <para>获取本遍历所依据的能力投影。</para></summary>
    /// <value>The immutable snapshot a consuming session compares against its published geometry.
    /// <para>消费会话会与之比对已上报几何信息的不可变快照。</para></value>
    public MtkStorageCapabilitySnapshot Capability { get; }

    internal MtkXFlashStorageInfo? XFlashGeometry { get; }
    internal MtkLegacyStorageInfo? LegacyGeometry { get; }
    internal MtkXmlStorageInfo? XmlGeometry { get; }

    /// <summary>Builds a walk space from a discovered XFlash geometry.
    /// <para>由已发现的 XFlash 几何信息构建遍历空间。</para></summary>
    /// <param name="storage">The geometry the XFlash agent reported for one storage family.
    /// <para>XFlash 代理就某一存储家族上报的几何信息。</para></param>
    /// <returns>A space over every ordinary region with a positive reported capacity.
    /// <para>覆盖所有具有正数上报容量的普通区域的空间。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="storage"/> is null.
    /// <para><paramref name="storage"/> 为 null。</para></exception>
    /// <exception cref="NotSupportedException">The family is NAND, whose byte addressing needs page,
    /// spare-area and bad-block handling rather than a flat walk.
    /// <para>该家族为 NAND，其字节寻址需要页、备用区与坏块处理，而非扁平遍历。</para></exception>
    /// <exception cref="InvalidOperationException">No ordinary region reported a positive capacity.
    /// <para>没有任何普通区域上报正数容量。</para></exception>
    public static MtkFlashAreaMap FromXFlash(MtkXFlashStorageInfo storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        if (storage.Kind == MtkXFlashStorageKind.Nand)
        {
            throw new NotSupportedException(
                "The XFlash download agent addresses NAND through dedicated page, spare-area and bad-block " +
                "methods, so a region-agnostic byte walk is not available for it.");
        }

        return Build(MtkStorageDiscoverySource.XFlash, storage.Kind, storage.BlockSize,
            BoundChunk(storage.ReadPacketLength, storage.BlockSize),
            MtkStorageCapabilitySnapshot.FromXFlash(storage), requireIdentifier: true, storage, null, null);
    }

    /// <summary>Builds a walk space from a discovered Legacy eMMC inventory.
    /// <para>由已发现的 Legacy eMMC 清单构建遍历空间。</para></summary>
    /// <param name="storage">The inventory the Legacy agent reported.
    /// <para>Legacy 代理上报的清单。</para></param>
    /// <returns>A space over every ordinary eMMC region with a positive reported capacity.
    /// <para>覆盖所有具有正数上报容量的普通 eMMC 区域的空间。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="storage"/> is null.
    /// <para><paramref name="storage"/> 为 null。</para></exception>
    /// <exception cref="NotSupportedException">The Legacy agent was initialized for NOR or NAND, which its dedicated geometry methods own instead of the region-walked eMMC inventory.
    /// <para>Legacy 代理以 NOR 或 NAND 初始化；这些由各自的专用几何方法负责，而非可遍历的 eMMC 清单。</para></exception>
    /// <exception cref="InvalidOperationException">No ordinary region reported a positive capacity.
    /// <para>没有任何普通区域上报正数容量。</para></exception>
    public static MtkFlashAreaMap FromLegacy(MtkLegacyStorageInfo storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        if (storage.Kind != MtkXFlashStorageKind.Emmc)
        {
            throw new NotSupportedException(
                $"The Legacy download agent reports {storage.Kind} storage, which is addressed through its " +
                "dedicated geometry methods and carries no ordinary region inventory for a region-agnostic walk.");
        }

        return Build(MtkStorageDiscoverySource.Legacy, storage.Kind, 512,
            BoundChunk(MaximumWalkChunkBytes, 512),
            MtkStorageCapabilitySnapshot.FromLegacy(storage), requireIdentifier: true, null, storage, null);
    }

    /// <summary>Builds a walk space from a discovered XML geometry.
    /// <para>由已发现的 XML 几何信息构建遍历空间。</para></summary>
    /// <param name="storage">The region document the XML agent reported.
    /// <para>XML 代理上报的区域文档。</para></param>
    /// <returns>A space over every ordinary named region with a positive reported capacity.
    /// <para>覆盖所有具有正数上报容量的命名普通区域的空间。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="storage"/> is null.
    /// <para><paramref name="storage"/> 为 null。</para></exception>
    /// <exception cref="InvalidOperationException">No ordinary region reported a positive capacity.
    /// <para>没有任何普通区域上报正数容量。</para></exception>
    public static MtkFlashAreaMap FromXml(MtkXmlStorageInfo storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        return Build(MtkStorageDiscoverySource.Xml,
            storage.Kind == MtkXmlStorageKind.Emmc ? MtkXFlashStorageKind.Emmc : MtkXFlashStorageKind.Ufs,
            storage.BlockSize, BoundChunk(MaximumWalkChunkBytes, storage.BlockSize),
            MtkStorageCapabilitySnapshot.FromXml(storage), requireIdentifier: false, null, null, storage);
    }

    /// <summary>Converts a sector number and count into the absolute walk range the raw-sector read uses.
    /// <para>把扇区号与扇区数转换为原始扇区读取所用的绝对遍历范围。</para></summary>
    /// <remarks>
    /// <para>The reference handler multiplies both operands by the transfer size it calls
    /// <c>config.pagesize</c> (Library/DA/mtk_da_handler.py:304-307). This map substitutes the block size
    /// the agent itself reported, because the scatter's declared page size is not a device fact and the
    /// family's read path enforces that reported alignment on every transfer.</para>
    /// <para>参考处理程序把两个操作数都乘以它称为 <c>config.pagesize</c> 的传输大小
    /// （Library/DA/mtk_da_handler.py:304-307）。本映射改用代理自报的块大小，因为 scatter 声明的页大小并非
    /// 设备事实，而该家族的读取路径在每次传输上强制的就是这个自报对齐。</para>
    /// </remarks>
    /// <param name="startSector">The zero-based first sector of the walk. <para>遍历的零基起始扇区。</para></param>
    /// <param name="sectorCount">The positive number of sectors to read. <para>要读取的正数扇区数。</para></param>
    /// <returns>The absolute byte offset and byte length, both aligned to <see cref="AlignmentBytes"/>.
    /// <para>绝对字节偏移与字节长度，均按 <see cref="AlignmentBytes"/> 对齐。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="startSector"/> is negative or
    /// <paramref name="sectorCount"/> is not positive.
    /// <para><paramref name="startSector"/> 为负，或 <paramref name="sectorCount"/> 不是正数。</para></exception>
    /// <exception cref="OverflowException">The multiplied offset or length exceeds the supported range.
    /// <para>相乘得到的偏移或长度超出支持范围。</para></exception>
    public (long Offset, long Length) ResolveSectorRange(long startSector, long sectorCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startSector);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sectorCount);
        return (checked(startSector * AlignmentBytes), checked(sectorCount * AlignmentBytes));
    }

    /// <summary>Splits an absolute walk range into per-region segments without touching the device.
    /// <para>把绝对遍历范围按区域切分，不接触设备。</para></summary>
    /// <remarks>
    /// <para>Every bound is checked before any segment is allocated, so a fabricated offset or length can never produce a large list or an overflowed sum. Segments follow the reported region order and never span a region the map does not list.</para>
    /// <para>在分配任何片段之前先完成全部边界检查，因此伪造的偏移或长度既不会产生庞大的列表，也不会造成累加 溢出。片段按上报区域顺序排列，绝不跨越映射未列出的区域。</para></remarks>
    /// <param name="offset">The nonnegative absolute byte offset, aligned to <see cref="AlignmentBytes"/>.
    /// <para>非负且按 <see cref="AlignmentBytes"/> 对齐的绝对字节偏移。</para></param>
    /// <param name="length">The positive absolute byte count, aligned to <see cref="AlignmentBytes"/>.
    /// <para>正数且按 <see cref="AlignmentBytes"/> 对齐的绝对字节长度。</para></param>
    /// <returns>One to <c>64</c> contiguous, ordered segments covering exactly the requested bytes.
    /// <para>1 到 <c>64</c> 个连续、有序且恰好覆盖请求字节的片段。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException">The offset is negative, the length is not positive, either operand is unaligned, or the range extends past <see cref="TotalBytes"/>.
    /// <para>偏移为负、长度不是正数、任一操作数未对齐，或该范围超出 <see cref="TotalBytes"/>。</para></exception>
    public IReadOnlyList<MtkFlashAreaSegment> Plan(long offset, long length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        if (offset % AlignmentBytes != 0 || length % AlignmentBytes != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length),
                $"The region-agnostic walk is addressed in {AlignmentBytes}-byte units; the requested " +
                "offset or length is unaligned.");
        }

        var requestedEnd = checked(offset + length);
        if (offset >= TotalBytes || requestedEnd > TotalBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(length),
                $"The requested walk range ends at byte {requestedEnd}, past the {TotalBytes} bytes the " +
                $"{Source} download agent reported as ordinary storage.");
        }

        var segments = new List<MtkFlashAreaSegment>(Regions.Count);
        foreach (var region in Regions)
        {
            var regionEnd = checked(region.AbsoluteOffset + region.CapacityBytes);
            var start = Math.Max(offset, region.AbsoluteOffset);
            var end = Math.Min(requestedEnd, regionEnd);
            if (start >= end)
            {
                continue;
            }

            segments.Add(new MtkFlashAreaSegment(region.RegionName, region.PhysicalIdentifier, start,
                checked(start - region.AbsoluteOffset), checked(end - start)));
        }

        if (segments.Count == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset),
                "The requested walk range covers no reported region in this address space.");
        }

        return segments;
    }

    private static MtkFlashAreaMap Build(MtkStorageDiscoverySource source, MtkXFlashStorageKind kind,
        int alignmentBytes, int chunkBytes, MtkStorageCapabilitySnapshot capability, bool requireIdentifier,
        MtkXFlashStorageInfo? xflash, MtkLegacyStorageInfo? legacy, MtkXmlStorageInfo? xml)
    {
        var regions = new List<MtkFlashAreaRegion>(MaximumRegionCount);
        var absolute = 0L;
        foreach (var region in capability.OrdinaryRegions)
        {
            var capacity = region.CapacityBytes ?? 0L;
            if (capacity <= 0)
            {
                continue;
            }

            if (region.PhysicalIdentifier is null && requireIdentifier)
            {
                throw new NotSupportedException(
                    $"The {source} download agent reported the region {region.CanonicalName} without a " +
                    "physical identifier, so it cannot take part in a region-agnostic walk.");
            }

            if (regions.Count >= MaximumRegionCount)
            {
                throw new NotSupportedException(
                    $"The {source} download agent reported more than {MaximumRegionCount} ordinary regions.");
            }

            absolute = checked(absolute + capacity);
            regions.Add(new MtkFlashAreaRegion(region.CanonicalName, region.PhysicalIdentifier,
                checked(absolute - capacity), capacity));
        }

        if (regions.Count == 0)
        {
            throw new InvalidOperationException(
                $"The {source} download agent reports no ordinary storage region with a positive capacity, so " +
                "there is nothing for a region-agnostic walk to read.");
        }

        return new MtkFlashAreaMap(source, kind, alignmentBytes, chunkBytes, capability, [.. regions], absolute,
            xflash, legacy, xml);
    }

    private static int BoundChunk(int reportedPacketBytes, int alignmentBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(reportedPacketBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(alignmentBytes);
        var candidate = Math.Min(reportedPacketBytes, MaximumWalkChunkBytes);
        var aligned = candidate - candidate % alignmentBytes;
        return aligned > 0 ? aligned : alignmentBytes;
    }
}