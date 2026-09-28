namespace UotanStudio.Firmware.BlockImage.Gpt;

/// <summary>
/// A partition from a validated pair of GPT arrays. End LBA is inclusive.
/// <para>来自已校验主备 GPT 数组对的分区。结束 LBA 为闭区间。</para>
/// </summary>
/// <param name="Index">
/// Zero-based slot position in the entry array. <para>条目数组中从零开始的槽位位置。</para>
/// </param>
/// <param name="TypeId">
/// Partition type GUID. <para>分区类型 GUID。</para>
/// </param>
/// <param name="Id">
/// Unique partition GUID; empty identifiers are rejected during validation. <para>唯一分区 GUID；空标识在校验阶段即被拒绝。</para>
/// </param>
/// <param name="FirstLba">
/// First logical block of the partition. <para>分区的起始逻辑块。</para>
/// </param>
/// <param name="LastLba">
/// Last logical block, inside the usable range declared by both headers. <para>结束逻辑块，位于两个头部共同声明的可用范围之内。</para>
/// </param>
/// <param name="Attributes">
/// Raw 64-bit attribute flags; only the low three bits may be set. <para>原始 64 位属性标志；仅允许置位最低三位。</para>
/// </param>
/// <param name="Name">
/// UTF-16 decoded partition name, without the NUL terminator. <para>由 UTF-16 解码得到的分区名称，不含 NUL 终止符。</para>
/// </param>
/// <param name="ByteOffset">
/// Absolute byte offset of <paramref name="FirstLba"/> in the device address space. <para><paramref name="FirstLba"/> 在设备地址空间中的绝对字节偏移。</para>
/// </param>
/// <param name="ByteLength">
/// Byte length covering <paramref name="FirstLba"/> through <paramref name="LastLba"/> inclusive. <para>覆盖 <paramref name="FirstLba"/> 至 <paramref name="LastLba"/>（含两端）的字节长度。</para>
/// </param>
public sealed record GptPartition(int Index, Guid TypeId, Guid Id, ulong FirstLba, ulong LastLba,
    ulong Attributes, string Name, long ByteOffset, long ByteLength);

/// <summary>
/// A matching primary/backup GPT snapshot, not a guarantee of immutable device contents.
/// <para>主备一致的 GPT 快照，但不保证设备内容不可变。</para>
/// </summary>
public sealed class GptTable
{
    internal GptTable(Guid diskId, int blockSize, ulong blockCount, List<GptPartition> partitions, string metadataSha256,
        uint headerSize, ulong firstUsableLba, ulong lastUsableLba, uint partitionEntryCount, uint partitionEntrySize,
        ulong primaryEntryArrayLba, ulong backupEntryArrayLba)
    {
        DiskId = diskId;
        BlockSize = blockSize;
        BlockCount = blockCount;
        Partitions = partitions.AsReadOnly();
        MetadataSha256 = metadataSha256;
        HeaderSize = headerSize;
        FirstUsableLba = firstUsableLba;
        LastUsableLba = lastUsableLba;
        PartitionEntryCount = partitionEntryCount;
        PartitionEntrySize = partitionEntrySize;
        PrimaryEntryArrayLba = primaryEntryArrayLba;
        BackupEntryArrayLba = backupEntryArrayLba;
    }

    /// <summary>
    /// Gets the disk identifier shared by both headers.
    /// <para>获取两个头部共同持有的磁盘标识符。</para>
    /// </summary>
    /// <value>
    /// The GUID read from the validated headers; never <see cref="Guid.Empty"/>, which validation rejects.
    /// <para>从已校验头部读取的 GUID；校验会拒绝 <see cref="Guid.Empty"/>。</para>
    /// </value>
    public Guid DiskId { get; }

    /// <summary>
    /// Gets the explicitly supplied logical block size.
    /// <para>获取显式提供的逻辑块大小。</para>
    /// </summary>
    /// <value>
    /// A power-of-two block size in bytes from 512 through 65536, as accepted by the reader.
    /// <para>读取器接受的 512 至 65536 字节之间、2 的幂的块大小。</para>
    /// </value>
    public int BlockSize { get; }

    /// <summary>
    /// Gets the explicitly supplied device block count.
    /// <para>获取显式提供的设备块数量。</para>
    /// </summary>
    /// <value>
    /// Total logical blocks of the addressed device; multiply by <see cref="BlockSize"/> for the byte size.
    /// <para>被寻址设备的总逻辑块数；乘以 <see cref="BlockSize"/> 即得字节大小。</para>
    /// </value>
    public ulong BlockCount { get; }

    /// <summary>
    /// Gets used entries in original array order.
    /// <para>按原始数组顺序获取已使用的条目。</para>
    /// </summary>
    /// <value>
    /// A read-only snapshot containing only non-empty entries; unused slots are omitted.
    /// <para>仅包含非空条目的只读快照；未使用的槽位不会出现在其中。</para>
    /// </value>
    public IReadOnlyList<GptPartition> Partitions { get; }

    /// <summary>
    /// Gets SHA-256 of the two header-block SHA-256 digests followed by the exact entry array, excluding sector padding.
    /// <para>获取如下数据的 SHA-256：两个头部块的 SHA-256 摘要后接精确的条目数组，不含扇区填充。</para>
    /// </summary>
    /// <value>
    /// Uppercase hexadecimal digest string identifying the validated metadata bytes.
    /// <para>用于标识已校验元数据字节的大写十六进制摘要字符串。</para>
    /// </value>
    public string MetadataSha256 { get; }

    /// <summary>
    /// Gets the declared header structure size recorded by the reader.
    /// <para>获取读取器记录的头部结构声明大小。</para>
    /// </summary>
    /// <value>
    /// Header size in bytes, from <c>92</c> through <see cref="BlockSize"/>; the writer emits the header
    /// with exactly this size and zero-fills the rest of the block.
    /// <para>头部字节大小，范围 <c>92</c> 至 <see cref="BlockSize"/>；写入器按该大小输出头部并将块内剩余部分补零。</para>
    /// </value>
    public uint HeaderSize { get; }

    /// <summary>
    /// Gets the first usable logical block declared by both headers.
    /// <para>获取两个头部共同声明的首个可用逻辑块。</para>
    /// </summary>
    /// <value>
    /// Logical block index of the first partition-eligible block; the writer round-trips this value.
    /// <para>允许承载分区的首个逻辑块索引；写入器原样往返该值。</para>
    /// </value>
    public ulong FirstUsableLba { get; }

    /// <summary>
    /// Gets the last usable logical block declared by both headers.
    /// <para>获取两个头部共同声明的最后一个可用逻辑块。</para>
    /// </summary>
    /// <value>
    /// Logical block index of the last partition-eligible block; the writer round-trips this value.
    /// <para>允许承载分区的最后逻辑块索引；写入器原样往返该值。</para>
    /// </value>
    public ulong LastUsableLba { get; }

    /// <summary>
    /// Gets the declared capacity of the partition entry array, including unused slots.
    /// <para>获取分区条目数组的声明容量（含未使用槽位）。</para>
    /// </summary>
    /// <value>
    /// Total entry slots from the header, from <c>1</c> through <c>65536</c>; <see cref="Partitions"/>
    /// only lists the non-empty slots within this count.
    /// <para>头部声明的条目槽位总数，范围 <c>1</c> 至 <c>65536</c>；<see cref="Partitions"/> 仅列出其中非空槽位。</para>
    /// </value>
    public uint PartitionEntryCount { get; }

    /// <summary>
    /// Gets the declared byte size of one partition entry slot.
    /// <para>获取单个分区条目槽位的声明字节大小。</para>
    /// </summary>
    /// <value>
    /// Entry size in bytes, a power of two from <c>128</c> through <c>4096</c>; bytes beyond the
    /// canonical <c>128</c>-entry layout are zero, as enforced by the reader.
    /// <para>条目字节大小，须为 <c>128</c> 至 <c>4096</c> 之间 2 的幂；规范 <c>128</c> 字节布局之外的字节为零（由读取器强制）。</para>
    /// </value>
    public uint PartitionEntrySize { get; }

    /// <summary>
    /// Gets the logical block address of the primary partition entry array.
    /// <para>获取主分区条目数组的逻辑块地址。</para>
    /// </summary>
    /// <value>
    /// First block of the primary array; the writer places the primary entry array here.
    /// <para>主条目数组的首块；写入器将主条目数组放在该位置。</para>
    /// </value>
    public ulong PrimaryEntryArrayLba { get; }

    /// <summary>
    /// Gets the logical block address of the backup partition entry array.
    /// <para>获取备份分区条目数组的逻辑块地址。</para>
    /// </summary>
    /// <value>
    /// First block of the backup array; the writer places the backup entry array here.
    /// <para>备份条目数组的首块；写入器将备份条目数组放在该位置。</para>
    /// </value>
    public ulong BackupEntryArrayLba { get; }

    /// <summary>
    /// Resolves an exact, case-sensitive name, rejecting missing or ambiguous names.
    /// <para>按精确、区分大小写的名称解析，缺失或有歧义的名称一律拒绝。</para>
    /// </summary>
    /// <param name="name">
    /// Partition name to match with ordinal semantics; blank names are rejected.
    /// <para>按序数语义匹配的分区名称；空白名称被拒绝。</para>
    /// </param>
    /// <returns>
    /// The single partition whose name equals <paramref name="name"/> exactly.
    /// <para>名称与 <paramref name="name"/> 完全相等的唯一分区。</para>
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="name"/> is empty or <see langword="null"/>. <para><paramref name="name"/> 为空或 <see langword="null"/>。</para>
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// More than one entry carries this name.
    /// <para>多条条目携带该名称。</para>
    /// </exception>
    /// <exception cref="KeyNotFoundException">
    /// No entry carries this name.
    /// <para>没有任何条目携带该名称。</para>
    /// </exception>
    public GptPartition FindPartition(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        GptPartition? match = null;
        foreach (var partition in Partitions)
        {
            if (!string.Equals(partition.Name, name, StringComparison.Ordinal))
            {
                continue;
            }
            if (match is not null)
            {
                throw new InvalidDataException("The GPT partition name is ambiguous.");
            }
            match = partition;
        }
        return match ?? throw new KeyNotFoundException("The GPT partition name was not found.");
    }
}