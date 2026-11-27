using UotanStudio.Firmware.BlockImage;
using UotanStudio.Firmware.BlockImage.Gpt;
using UotanStudio.Firmware.BlockImage.MediaTek;

namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>
/// One partition-table observation taken from a Legacy eMMC region, together with the layout that was actually
/// found there. A region may prefix its table with a MediaTek <c>brom_layout_header</c>, and the table itself may
/// be a UEFI GPT or a classic MBR, so the observation records the resolved table offset and kind instead of
/// assuming either.
/// <para>取自一个 Legacy eMMC 区域的一次分区表观测，连同在该处实际找到的布局。区域可能在分区表之前放置联发科
/// <c>brom_layout_header</c>，而表本身可能是 UEFI GPT 或经典 MBR，因此该观测记录解析出的表偏移与类型，
/// 而不是对二者中的任何一个做假定。</para>
/// </summary>
public sealed class MtkLegacyPartitionTableSnapshot
{
    internal MtkLegacyPartitionTableSnapshot(MtkLegacyStorageInfo storage, byte partitionId, long tableOffset,
        MtkBromLayoutHeader layoutHeader, PartitionTableKind kind, GptTable? gpt, MtkMbrSnapshot? mbr)
    {
        Storage = storage;
        PartitionId = partitionId;
        TableOffset = tableOffset;
        LayoutHeader = layoutHeader;
        Kind = kind;
        Gpt = gpt;
        Mbr = mbr;
    }

    /// <summary>Gets the exact discovered Legacy storage inventory the table was read under.
    /// <para>获取读取该表时所依据的、精确的已发现 Legacy 存储清单。</para></summary>
    /// <value>The immutable inventory observation. <para>不可变的存储清单观测。</para></value>
    public MtkLegacyStorageInfo Storage { get; }

    /// <summary>Gets the Legacy physical region that was read.
    /// <para>获取被读取的 Legacy 物理区域。</para></summary>
    /// <value>A discovered region identifier, normally <see cref="MtkLegacyEmmcRegion.User"/>.
    /// <para>已发现的区域标识符，通常为 <see cref="MtkLegacyEmmcRegion.User"/>。</para></value>
    public byte PartitionId { get; }

    /// <summary>Gets the resolved first byte of the table inside the region.
    /// <para>获取分区表在区域内的已解析起始字节。</para></summary>
    /// <value>Zero when the region starts with the table, or the header size when it was prefixed.
    /// <para>区域直接以表开始时为零；带前缀时为头部长度。</para></value>
    public long TableOffset { get; }

    /// <summary>Gets the MediaTek layout header that prefixed the table, when one was present.
    /// <para>获取位于分区表之前的联发科布局头（存在时）。</para></summary>
    /// <value>The parsed header, or <see cref="MtkBromLayoutHeader.None"/> when the region starts with the table.
    /// <para>解析出的头部；区域直接以表开始时为 <see cref="MtkBromLayoutHeader.None"/>。</para></value>
    public MtkBromLayoutHeader LayoutHeader { get; }

    /// <summary>Gets the resolved partition-table layout.
    /// <para>获取已解析的分区表布局。</para></summary>
    /// <value><see cref="PartitionTableKind.Gpt"/> or <see cref="PartitionTableKind.Mbr"/>.
    /// <para><see cref="PartitionTableKind.Gpt"/> 或 <see cref="PartitionTableKind.Mbr"/>。</para></value>
    public PartitionTableKind Kind { get; }

    /// <summary>Gets the validated GPT metadata when <see cref="Kind"/> is <see cref="PartitionTableKind.Gpt"/>.
    /// <para>当 <see cref="Kind"/> 为 <see cref="PartitionTableKind.Gpt"/> 时获取已校验的 GPT 元数据。</para></summary>
    /// <value>The parsed table, or <see langword="null"/> for an MBR region.
    /// <para>已解析的表；MBR 区域时为 <see langword="null"/>。</para></value>
    public GptTable? Gpt { get; }

    /// <summary>Gets the validated MBR metadata when <see cref="Kind"/> is <see cref="PartitionTableKind.Mbr"/>.
    /// <para>当 <see cref="Kind"/> 为 <see cref="PartitionTableKind.Mbr"/> 时获取已校验的 MBR 元数据。</para></summary>
    /// <value>The parsed snapshot, or <see langword="null"/> for a GPT region.
    /// <para>已解析的快照；GPT 区域时为 <see langword="null"/>。</para></value>
    public MtkMbrSnapshot? Mbr { get; }

    /// <summary>Gets the region byte rendered as the vendor's symbolic name.
    /// <para>获取以厂商符号名呈现的区域字节。</para></summary>
    /// <value>For example <c>USER</c> or <c>BOOT1</c>. <para>例如 <c>USER</c> 或 <c>BOOT1</c>。</para></value>
    public string RegionName => MtkLegacyEmmcRegion.Describe(PartitionId);

    /// <summary>Gets the number of populated partitions the resolved table declares.
    /// <para>获取已解析表所声明的已填充分区数量。</para></summary>
    /// <value>A nonnegative count; empty GPT entries and unused MBR slots are excluded.
    /// <para>非负计数；空的 GPT 条目与未使用的 MBR 槽位不计入。</para></value>
    public int PartitionCount => EnumerateExtents().Count;

    /// <summary>Resolves every addressable partition extent the table declares, in ascending offset order, with the
    /// byte ranges already translated into region-relative coordinates.
    /// <para>解析该表声明的每个可寻址分区区间，按偏移升序排列，字节范围已换算为区域内相对坐标。</para>
    /// </summary>
    /// <remarks>
    /// A GPT entry carries absolute device byte offsets, so they are used verbatim. A classic MBR stores LBAs relative
    /// to the 512-byte sector that holds the table, so each extent is recomputed against <see cref="TableOffset"/>:
    /// treating those LBAs as region-absolute is what makes an MBR read land on the wrong sectors. Extended
    /// containers are skipped because they only describe the chain and own no payload. Names come from the table when
    /// it has a name field and are otherwise synthesised as <c>mbr-TT-N</c>, never invented from filesystem content.
    /// <para>GPT 条目自带设备绝对字节偏移，因此原样采用。经典 MBR 存储的是相对于承载分区表的 512 字节扇区的 LBA，
    /// 因此每个区间都要对照 <see cref="TableOffset"/> 重新计算：把这些 LBA 当成区域绝对偏移，正是 MBR 读取落到错误扇区
    /// 的原因。扩展容器只描述链条、并不拥有负载，因此被跳过。表有名称字段时名称取自表内，否则合成为 <c>mbr-TT-N</c>，
    /// 绝不根据文件系统内容臆造。</para>
    /// </remarks>
    /// <returns>The extents ordered by ascending offset, empty when no table was resolved.
    /// <para>按偏移升序排列的区间；未解析出表时为空。</para></returns>
    public IReadOnlyList<MtkLegacyPartitionExtent> EnumerateExtents() => Kind switch
    {
        PartitionTableKind.Gpt => [.. (Gpt?.Partitions ?? [])
            .Select(static partition => new MtkLegacyPartitionExtent(partition.Name, partition.ByteOffset,
                partition.ByteLength, null, false))
            .OrderBy(static extent => extent.ByteOffset)],
        PartitionTableKind.Mbr => Mbr is null ? [] : DescribeMbrExtents(Mbr, TableOffset),
        _ => []
    };

    /// <summary>Finds exactly one extent by name, rejecting an absent or ambiguous match instead of guessing.
    /// <para>按名称查找恰好一个区间；缺失或有歧义时直接拒绝，而不是猜测。</para>
    /// </summary>
    /// <param name="name">The exact case-sensitive extent name.
    /// <para>精确、区分大小写的区间名称。</para></param>
    /// <returns>The single matching extent.<para>唯一匹配的区间。</para></returns>
    /// <exception cref="KeyNotFoundException">No extent carries this name.
    /// <para>没有任何区间携带该名称。</para></exception>
    /// <exception cref="InvalidDataException">More than one extent carries this name.
    /// <para>多个区间携带该名称。</para></exception>
    public MtkLegacyPartitionExtent FindExtent(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        MtkLegacyPartitionExtent? match = null;
        foreach (var extent in EnumerateExtents())
        {
            if (!string.Equals(extent.Name, name, StringComparison.Ordinal))
            {
                continue;
            }

            if (match is not null)
            {
                throw new InvalidDataException(
                    "The partition table declares more than one extent named '" + name + "'.");
            }

            match = extent;
        }

        return match ?? throw new KeyNotFoundException(
            "The partition table declares no extent named '" + name + "'.");
    }

    private static IReadOnlyList<MtkLegacyPartitionExtent> DescribeMbrExtents(MtkMbrSnapshot mbr, long tableOffset)
    {
        var extents = new List<MtkLegacyPartitionExtent>(mbr.Primary.Count + mbr.Logical.Count);
        var ordinals = new Dictionary<byte, int>();
        foreach (var (firstLba, sectorCount, partitionType) in mbr.EnumerateExtents())
        {
            ordinals.TryGetValue(partitionType, out var ordinal);
            ordinals[partitionType] = ++ordinal;
            var offset = checked(tableOffset + (firstLba * (long)MtkMbrSnapshot.SectorSize));
            var length = sectorCount * (long)MtkMbrSnapshot.SectorSize;
            var logical = mbr.Logical.Any(entry => entry.FirstLba == firstLba
                && entry.SectorCount == sectorCount && entry.PartitionType == partitionType);
            extents.Add(new MtkLegacyPartitionExtent("mbr-" + partitionType.ToString("X2") + "-" + ordinal,
                offset, length, partitionType, logical));
        }

        return extents;
    }
}