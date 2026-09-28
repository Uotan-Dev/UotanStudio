namespace UotanStudio.Firmware.Archive.Models;

/// <summary>
/// Payload-free listing view of one archive entry, as produced by <c>CpioArchive.ListEntries</c>.
/// <para>单个归档条目不含负载的清单视图，由 <c>CpioArchive.ListEntries</c> 产出。</para>
/// </summary>
public sealed class ArchiveEntrySummary
{
    /// <summary>
    /// Entry path exactly as stored in the archive, using forward slashes and no leading root.
    /// <para>归档中原样存储的路径，使用正斜杠且不带根前缀。</para>
    /// </summary>
    /// <value>
    /// The UTF-8 decoded entry name; the CPIO trailer marker is never surfaced as a summary.
    /// <para>UTF-8 解码后的条目名；CPIO 结束标记不会作为清单条目出现。</para>
    /// </value>
    public required string Name { get; init; }

    /// <summary>
    /// Link target: the decoded symlink destination or, after hardlink consolidation, the name of
    /// the data-carrying entry sharing the inode.
    /// <para>链接目标：解码后的符号链接指向，或硬链接归组后与该 inode 共享数据的条目名。</para>
    /// </summary>
    /// <value>
    /// The target string when <c>Metadata.FileType</c> is <see cref="CpioFileType.SymbolicLink"/> or
    /// <see cref="CpioFileType.HardLink"/>; <see langword="null"/> otherwise.
    /// <para>当 <c>Metadata.FileType</c> 为 <see cref="CpioFileType.SymbolicLink"/> 或
    /// <see cref="CpioFileType.HardLink"/> 时返回目标字符串；其他情况为 <see langword="null"/>。</para>
    /// </value>
    public string? LinkName { get; init; }

    /// <summary>
    /// Ownership, permission, device and timestamp metadata of the entry.
    /// <para>条目的归属、权限、设备号与时间戳元数据。</para>
    /// </summary>
    /// <value>
    /// The same <see cref="ArchiveEntryMetadata"/> the full entry carries; reading it never touches payload bytes.
    /// <para>与完整条目一致的 <see cref="ArchiveEntryMetadata"/>；读取它不会触及负载字节。</para>
    /// </value>
    public required ArchiveEntryMetadata Metadata { get; init; }

    /// <summary>
    /// Uncompressed payload length in bytes as declared by the archive header; CPIO and this TAR
    /// reader store payloads uncompressed, so no decompressed size differs from it.
    /// <para>归档头部声明的未压缩负载字节数；CPIO 与本 TAR 读取器的负载均以未压缩形式存储，因此不存在与解压后大小不一致的情况。</para>
    /// </summary>
    /// <value>
    /// The declared length; for a symlink it is the byte count of the stored target, directories and
    /// special devices are typically 0, and a hardlinked name reports the data carrier's length.
    /// <para>头部声明的长度；符号链接为所存目标的字节数，目录与特殊设备通常为 0，硬链接名上报数据承载条目的长度。</para>
    /// </value>
    public long DataLength { get; init; }
}