namespace UotanStudio.Firmware.FileSystem.Erofs.Models.Inodes;

/// <summary>
/// One fragment payload packed into a packed inode: the owner that references it, its offset inside the
/// packed inode and its length.
/// <para>打包进 packed inode 的一段 fragment 负载：引用它的所有者、它在 packed inode 内的偏移与长度。</para>
/// </summary>
internal sealed class ErofsPackedFragment
{
    /// <summary>Node that references this fragment. <para>引用该 fragment 的节点。</para></summary>
    /// <value>Never <see langword="null"/> once the planner has registered the fragment.
    /// <para>规划器登记该 fragment 后绝不为 <see langword="null"/>。</para></value>
    internal required ErofsInode Owner { get; init; }

    /// <summary>Offset of the fragment inside the packed inode.
    /// <para>该 fragment 在 packed inode 内的偏移。</para></summary>
    internal long Offset { get; set; }

    /// <summary>Offset of the fragment's bytes inside the owner's own file.
    /// <para>该 fragment 的字节在所有者文件内的偏移。</para></summary>
    /// <value>Zero for a whole-file fragment, whose payload starts at the file's first byte, and the
    /// final pcluster's logical start for a tail fragment.
    /// <para>整文件 fragment 为零，其负载自文件首字节开始；尾部 fragment 为末尾 pcluster 的逻辑起点。</para></value>
    internal long SourceOffset { get; set; }

    /// <summary>Length of the fragment in bytes. <para>该 fragment 的字节长度。</para></summary>
    internal long Length { get; set; }
}