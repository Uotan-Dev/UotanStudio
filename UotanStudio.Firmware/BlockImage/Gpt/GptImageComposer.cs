namespace UotanStudio.Firmware.BlockImage.Gpt;

/// <summary>
/// One caller-specified partition for a freshly composed GPT disk image, addressed in logical blocks.
/// <para>用于合成全新 GPT 磁盘镜像的单个调用方分区，以逻辑块寻址。</para>
/// </summary>
/// <param name="Name">UTF-16 partition name written into the entry array; at most 36 units, no embedded NUL.
/// <para>写入条目数组的 UTF-16 分区名；最多 36 个单元且不含内嵌 NUL。</para></param>
/// <param name="TypeId">Partition type GUID identifying the intended content.
/// <para>标识预期内容类型的分区类型 GUID。</para></param>
/// <param name="UniqueId">Unique partition GUID; duplicates across the list are rejected.
/// <para>唯一分区 GUID；列表内重复即被拒绝。</para></param>
/// <param name="FirstLba">First logical block of the partition, inside the usable area the layout derives.
/// <para>分区首个逻辑块，须位于布局推导的可用区间内。</para></param>
/// <param name="LastLba">Last logical block, inclusive and not below <paramref name="FirstLba"/>.
/// <para>结束逻辑块（闭区间），且不小于 <paramref name="FirstLba"/>。</para></param>
public sealed record GptImagePartition(string Name, Guid TypeId, Guid UniqueId, ulong FirstLba, ulong LastLba);

/// <summary>
/// Composes a brand-new disk image that contains only GPT metadata: a protective MBR, both headers
/// and both entry arrays over a zero-filled capacity. Partition data is never written here; the
/// caller fills the data area afterwards at the byte offsets of the emitted entries.
/// <para>合成只含 GPT 元数据的全新磁盘镜像：保护性 MBR、两个头部与两个条目数组，其余容量补零。
/// 此处绝不写入分区数据；调用方随后按条目的字节偏移填充数据区。</para>
/// </summary>
public static class GptImageComposer
{
    /// <summary>
    /// Writes a new image file of exactly <c><paramref name="blockCount"/> ×
    /// <paramref name="blockSize"/></c> bytes holding only GPT metadata over a zero-filled data area.
    /// The published file is complete before it replaces the destination, so a failed or cancelled
    /// run never leaves a truncated image behind.
    /// <para>写出大小恰为 <c><paramref name="blockCount"/> × <paramref name="blockSize"/></c> 字节的
    /// 新镜像文件，只承载 GPT 元数据，数据区全部补零。文件完整写出后才会替换目标，因此失败或取消
    /// 都不会留下截断的镜像。</para>
    /// </summary>
    /// <param name="targetPath">Destination image path; an existing file is replaced only after the
    /// whole image has been published. <para>目标镜像路径；已存在的文件仅在整张镜像发布完成后被替换。</para></param>
    /// <param name="blockSize">Logical block size, a power of two from 512 through 65536.
    /// <para>逻辑块大小，须为 512 至 65536 之间 2 的幂。</para></param>
    /// <param name="blockCount">Device capacity in blocks; large enough to hold both GPT metadata areas.
    /// <para>以块计的设备容量；须足以容纳两块 GPT 元数据区。</para></param>
    /// <param name="partitions">Entries in emit order; slot indexes are reassigned from the position in
    /// this list and ranges must not overlap. <para>按输出顺序排列的条目；槽位索引按列表位置重新分配，
    /// 且范围不得重叠。</para></param>
    /// <param name="diskId">Disk identifier for both headers; <see langword="null"/> generates a fresh
    /// random identifier. <para>两个头部使用的磁盘标识符；<see langword="null"/> 时生成新的随机标识符。</para></param>
    /// <param name="cancellationToken">Cancellation checked before and during the writes; cancellation
    /// leaves the destination untouched. <para>在写入之前与期间检查的取消令牌；取消不会改动目标。</para></param>
    /// <exception cref="ArgumentException"><paramref name="targetPath"/> is empty, <paramref name="diskId"/>
    /// is <see cref="Guid.Empty"/>, or two entries share a unique GUID.
    /// <para><paramref name="targetPath"/> 为空、<paramref name="diskId"/> 为 <see cref="Guid.Empty"/>，
    /// 或两个条目共享相同的唯一 GUID。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">The geometry or entry dimensions are outside the
    /// supported range, or more partitions were supplied than there are entry slots.
    /// <para>几何或条目维度超出支持范围，或提供的分区数量超过条目槽位数。</para></exception>
    /// <exception cref="InvalidDataException">A partition range, attribute bit or name is not encodable,
    /// or ranges overlap. <para>分区范围、属性位或名称不可编码，或范围存在重叠。</para></exception>
    /// <exception cref="IOException">The destination or its temporary publication file cannot be written.
    /// <para>无法写入目标或其临时发布文件。</para></exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.
    /// <para>令牌已被取消。</para></exception>
    public static void WriteNewImage(string targetPath, int blockSize, ulong blockCount,
        IReadOnlyList<GptImagePartition> partitions, Guid? diskId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentNullException.ThrowIfNull(partitions);
        var effectiveDiskId = diskId ?? Guid.NewGuid();
        var mapped = new List<GptPartition>(partitions.Count);
        for (var index = 0; index < partitions.Count; index++)
        {
            var partition = partitions[index];
            ArgumentNullException.ThrowIfNull(partition);
            mapped.Add(new GptPartition(index, partition.TypeId, partition.UniqueId,
                partition.FirstLba, partition.LastLba, 0, partition.Name, 0, 0));
        }

        var table = GptTableWriter.Create(effectiveDiskId, blockSize, blockCount, mapped);
        GptTableWriter.WriteNewImage(targetPath, table, cancellationToken);
    }
}