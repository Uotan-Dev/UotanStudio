namespace UotanStudio.Firmware.BoundedContexts.Archive.Writing;

/// <summary>
/// Options that adjust entry metadata and framing when saving an archive; a null argument selects
/// the defaults. <para>保存归档时调整条目元数据与成帧的选项；传入 <see langword="null"/> 即采用默认值。</para>
/// </summary>
public sealed class WriteOptions
{
    /// <summary>
    /// Appends the CPIO end-of-archive trailer entry named <c>TRAILER!!!</c> after the last entry.
    /// <para>在最后一个条目之后追加名为 <c>TRAILER!!!</c> 的 CPIO 归档结束条目。</para>
    /// </summary>
    /// <value>
    /// <see langword="true"/> by default; readers stop at the trailer, so omitting it truncates
    /// consumers. Ignored by the TAR writer, which frames its own end blocks.
    /// <para>默认为 <see langword="true"/>；读取器在结束条目处停止，省略它会使消费方提前截断。 TAR 写入器忽略此项，其结束块由自身成帧。</para>
    /// </value>
    public bool IncludeTrailer { get; init; } = true;

    /// <summary>
    /// Serializes a symbolic link by writing the UTF-8 bytes of its link target as the payload.
    /// <para>序列化符号链接时，把链接目标的 UTF-8 字节作为负载写出。</para>
    /// </summary>
    /// <value>
    /// <see langword="true"/> by default; when <see langword="false"/> the entry's own data or stream
    /// is written instead of the target text.
    /// <para>默认为 <see langword="true"/>；为 <see langword="false"/> 时改写条目自身的数据或流，而非目标文本。</para>
    /// </value>
    public bool CpioSymlinkAsDataPayload { get; init; } = true;

    /// <summary>
    /// Reassigns inode numbers while preserving hardlink grouping during metadata normalization.
    /// <para>在元数据归一化期间重新分配 inode 编号，同时保持硬链接分组关系。</para>
    /// </summary>
    /// <value>
    /// When <see langword="true"/>, each distinct non-zero (inode, device) pair maps to a fresh
    /// sequential number starting at 1 and group members share it; <see langword="false"/> keeps
    /// original values, and inode 0 is never renumbered.
    /// <para>为 <see langword="true"/> 时，每个不同的非零 (inode, 设备) 对映射为从 1 开始的顺序新号，同组成员共享该号；
    /// 为 <see langword="false"/> 时保留原值，inode 0 永不重编号。</para>
    /// </value>
    public bool RenumberInodes { get; init; }

    /// <summary>
    /// Zeroes the hosting device numbers of every entry before writing.
    /// <para>写出前将所有条目的宿主机设备号清零。</para>
    /// </summary>
    /// <value>
    /// When <see langword="true"/>, DeviceMajor and DeviceMinor become 0 before writing and before
    /// any inode renumbering; rdev fields are unaffected.
    /// <para>为 <see langword="true"/> 时，写出与任何 inode 重编号之前 DeviceMajor 与 DeviceMinor 均变为 0；
    /// rdev 字段不受影响。</para>
    /// </value>
    public bool IgnoreDeviceNumbers { get; init; }

    /// <summary>
    /// Forces a single owning user identifier on every written entry.
    /// <para>为每个写出的条目强制指定统一的属主 uid。</para>
    /// </summary>
    /// <value>
    /// The replacement uid; <see langword="null"/> keeps each entry's own UserId.
    /// <para>替换用的 uid；为 <see langword="null"/> 时保留各条目自身的 UserId。</para>
    /// </value>
    public uint? OwnerUserId { get; init; }

    /// <summary>
    /// Forces a single owning group identifier on every written entry.
    /// <para>为每个写出的条目强制指定统一的属主 gid。</para>
    /// </summary>
    /// <value>
    /// The replacement gid; <see langword="null"/> keeps each entry's own GroupId.
    /// <para>替换用的 gid；为 <see langword="null"/> 时保留各条目自身的 GroupId。</para>
    /// </value>
    public uint? OwnerGroupId { get; init; }
}