namespace UotanStudio.Firmware.FileSystem.Erofs.Models.Inodes;

/// <summary>
/// One planned pcluster of a compressed inode: its logical source window, the accepted encoded
/// length and the physical block it was assigned, kept beside the inode so the writer can emit
/// index rows and payload without re-deriving the plan.
/// <para>压缩 inode 的一个已规划 pcluster：其逻辑源窗口、已接受的编码长度与已分配的物理块，
/// 与 inode 并列保存，使写入方能直接发出索引行与负载而不必重新推导规划结果。</para>
/// </summary>
internal sealed class ErofsPclusterPlan
{
    /// <summary>Logical offset of the pcluster inside the file.
    /// <para>该 pcluster 在文件内的逻辑偏移。</para></summary>
    /// <value>A byte offset from the start of the file; negative values are not produced by the planner.
    /// <para>自文件起点计的字节偏移；规划器不会产生负值。</para></value>
    internal long SourceOffset { get; set; }

    /// <summary>Logical length this pcluster decodes to.
    /// <para>该 pcluster 解码出的逻辑长度。</para></summary>
    /// <value>Non-negative decoded byte count. <para>非负的解码字节数。</para></value>
    internal int SourceLength { get; set; }

    /// <summary>Encoded length written to the image.
    /// <para>写入镜像的编码长度。</para></summary>
    /// <value>Equals <see cref="SourceLength"/> for a plain pcluster.
    /// <para>原样 pcluster 时等于 <see cref="SourceLength"/>。</para></value>
    internal int CompressedLength { get; set; }

    /// <summary>First physical block assigned to this pcluster.
    /// <para>分配给该 pcluster 的首个物理块。</para></summary>
    /// <value>A block address in the target device's numbering.
    /// <para>目标设备编号体系中的块地址。</para></value>
    internal uint BlockAddress { get; set; }

    /// <summary>Whether the pcluster is stored verbatim instead of encoded.
    /// <para>该 pcluster 是否原样存储而非编码存储。</para></summary>
    /// <value><see langword="true"/> when the writer copies source bytes untouched.
    /// <para>写入方按源字节原样复制时为 <see langword="true"/>。</para></value>
    internal bool Plain { get; set; }

    /// <summary>Staging-file offset of this pcluster's accepted encoding.
    /// <para>该 pcluster 已接受编码在暂存文件中的偏移。</para></summary>
    /// <value><c>-1</c> for a plain pcluster, where the writer copies source bytes instead of reading
    /// the staging file. <para>原样 pcluster 为 <c>-1</c>，此时写入方复制源字节而不读暂存文件。</para></value>
    internal long StagedOffset { get; set; } = -1;

    /// <summary>Whether this entry is the node's tail fragment rather than a pcluster of its own data
    /// blocks. The entry still carries the fragment's logical source window so the index writers emit the
    /// head row and the trailing non-head rows exactly as they do for a pcluster, but no compressed frame
    /// belongs to it: those source bytes live in the shared packed inode and its encoded length stays zero.
    /// <para>该条目是否为节点的尾部 fragment，而不是其自身数据块的 pcluster。条目仍携带 fragment 的逻辑源窗口，
    /// 使索引写出方能像 pcluster 那样发出 head 行与随后的 non-head 行，但它不拥有任何压缩帧：这些源字节存放在
    /// 共享 packed inode 中，其编码长度保持为零。</para></summary>
    /// <value><see langword="true"/> only for the single synthetic entry a tail-fragment node ends with.
    /// <para>仅当尾部 fragment 节点末尾的那条合成条目为 <see langword="true"/>。</para></value>
    internal bool Fragment { get; set; }
}