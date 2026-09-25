namespace UotanStudio.Firmware.BoundedContexts.BlockImage.Gpt;

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
    internal GptTable(Guid diskId, int blockSize, ulong blockCount, List<GptPartition> partitions, string metadataSha256)
    {
        DiskId = diskId;
        BlockSize = blockSize;
        BlockCount = blockCount;
        Partitions = partitions.AsReadOnly();
        MetadataSha256 = metadataSha256;
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