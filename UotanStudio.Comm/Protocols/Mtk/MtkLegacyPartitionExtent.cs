namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>One addressable partition extent resolved from a Legacy partition table, named so a caller can ask for
/// it by name instead of by raw byte range.
/// <para>由 Legacy 分区表解析出的一个可寻址分区区间，带名称，使调用方可以按名称而不是按原始字节范围请求它。</para>
/// </summary>
/// <remarks>
/// A UEFI GPT stores partition names, so <see cref="Name"/> is the entry's own name. A classic MBR has no name
/// field at all, so its extents are named from the only data the table carries: the partition type byte and a
/// one-based ordinal within that type, in ascending first-LBA order. The two sources therefore never collide
/// within one table, but an MBR name is a synthetic label and not a vendor name.
/// <para>UEFI GPT 存储分区名称，因此 <see cref="Name"/> 就是条目自带的名称。经典 MBR 完全没有名称字段，所以其区间按表内
/// 仅有的数据命名：分区类型字节，以及该类型内按起始 LBA 升序计数的一次性序号。两个来源因此在同一张表内不会冲突，
/// 但 MBR 名称是合成标签，不是厂商名称。</para>
/// </remarks>
/// <param name="Name">The table name, or the synthetic <c>mbr-TT-N</c> label for an MBR extent.
/// <para>表内名称；MBR 区间为合成的 <c>mbr-TT-N</c> 标签。</para></param>
/// <param name="ByteOffset">Absolute byte offset inside the physical region, table prefix already included.
/// <para>物理区域内的绝对字节偏移，已计入分区表前缀。</para></param>
/// <param name="ByteLength">The extent length in bytes, always a whole number of 512-byte sectors.
/// <para>区间字节长度，始终为 512 字节扇区的整数倍。</para></param>
/// <param name="MbrPartitionType">The MBR type byte, or <see langword="null"/> for a GPT entry that has no such field.
/// <para>MBR 类型字节；没有该字段的 GPT 条目为 <see langword="null"/>。</para></param>
/// <param name="IsLogical"><see langword="true"/> for an MBR logical partition reached through the extended chain.
/// <para>对于经扩展链到达的 MBR 逻辑分区为 <see langword="true"/>。</para></param>
public sealed record MtkLegacyPartitionExtent(string Name, long ByteOffset, long ByteLength,
    byte? MbrPartitionType, bool IsLogical);