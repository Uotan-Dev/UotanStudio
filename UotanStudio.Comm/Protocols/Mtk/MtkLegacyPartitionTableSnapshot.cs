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
    public int PartitionCount => Kind switch
    {
        PartitionTableKind.Gpt => Gpt?.Partitions.Count ?? 0,
        PartitionTableKind.Mbr => Mbr is null
            ? 0
            : Mbr.Primary.Count(static entry => entry.IsPopulated) + Mbr.Logical.Count,
        _ => 0
    };
}