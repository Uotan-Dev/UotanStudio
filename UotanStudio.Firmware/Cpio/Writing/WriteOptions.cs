namespace UotanStudio.Firmware.Cpio.Writing;

/// <summary>
/// Options for archive serialization.
/// <para>归档序列化选项。</para>
/// </summary>
public sealed class WriteOptions
{
    /// <summary>
    /// When true, auto-adds the TRAILER!!! end marker for cpio family formats.
    /// <para>为 true 时自动为 cpio 系格式追加 TRAILER!!! 结束标记。</para>
    /// </summary>
    public bool IncludeTrailer { get; init; } = true;

    /// <summary>
    /// When true, symbolic links are emitted as link payload in cpio formats.
    /// <para>为 true 时符号链接在 cpio 格式中以链接负载形式写出。</para>
    /// </summary>
    public bool CpioSymlinkAsDataPayload { get; init; } = true;

    /// <summary>
    /// Renumber non-zero inode values in output to stable sequential values.
    /// <para>在输出中将非零 inode 重新编号为稳定的顺序值。</para>
    /// </summary>
    public bool RenumberInodes { get; init; }

    /// <summary>
    /// Zero device numbers in output for reproducible archives.
    /// <para>将输出中的设备号清零以获得可复现归档。</para>
    /// </summary>
    public bool IgnoreDeviceNumbers { get; init; }

    /// <summary>
    /// Optional owner override written to archive entries.
    /// <para>可选：写入归档条目的属主覆盖值。</para>
    /// </summary>
    public uint? OwnerUserId { get; init; }

    /// <summary>
    /// Optional group override written to archive entries.
    /// <para>可选：写入归档条目的属组覆盖值。</para>
    /// </summary>
    public uint? OwnerGroupId { get; init; }
}