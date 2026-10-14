namespace UotanStudio.Firmware.FileSystem.Erofs.Models.Inodes;

/// <summary>
/// Compressed addressing: the compressed block count stored in <c>u.i_blocks</c> plus the pcluster
/// plan and the index-form flags decoded from the <c>z_erofs_map_header</c> that follows the inode.
/// <para>压缩寻址：存放在 <c>u.i_blocks</c> 的压缩块数量，加上 inode 之后
/// <c>z_erofs_map_header</c> 解码出的 pcluster 规划与索引形态标志。</para>
/// </summary>
internal sealed class ErofsCompressedIdata : IErofsIdata
{
    /// <summary>Number of blocks the compressed payload occupies.
    /// <para>压缩负载占用的块数量。</para></summary>
    /// <value>The on-disk <c>u.i_blocks</c> value, including index and tail blocks.
    /// <para>磁盘上的 <c>u.i_blocks</c> 值，包含索引块与尾部块。</para></value>
    internal uint Blocks { get; set; }

    /// <summary>Planned pclusters of this inode, or <see langword="null"/> for a decoded inode whose
    /// index rows have not been expanded into a plan.
    /// <para>本 inode 已规划的 pcluster；索引行尚未展开为规划的解码 inode 为 <see langword="null"/>。</para></summary>
    /// <value>The builder fills this during payload planning; the reader leaves it
    /// <see langword="null"/>. <para>构建器在负载规划阶段填充；读取方保持 <see langword="null"/>。</para></value>
    internal ErofsPclusterPlan[]? Pclusters { get; set; }

    /// <summary>Planned logical cluster count of the payload.
    /// <para>负载已规划的逻辑簇数量。</para></summary>
    /// <value>Zero for a decoded inode whose index rows were not expanded into a plan.
    /// <para>索引行未展开为规划的解码 inode 为零。</para></value>
    internal uint ClusterCount { get; set; }

    /// <summary>Logical cluster size in bytes the planner selected.
    /// <para>规划器选定的逻辑簇字节大小。</para></summary>
    /// <value>Zero for a decoded inode whose index rows were not expanded into a plan.
    /// <para>索引行未展开为规划的解码 inode 为零。</para></value>
    internal int LogicalClusterSize { get; set; }

    /// <summary>Whether the lcluster index uses the packed 4-byte compact form.
    /// <para>逻辑簇索引是否采用紧凑 4 字节打包形式。</para></summary>
    /// <value>Selected by <c>datalayout == EROFS_INODE_COMPRESSED_COMPACT</c>, never by a bare advise bit.
    /// <para>由 <c>datalayout == EROFS_INODE_COMPRESSED_COMPACT</c> 选定，绝不依据单独的 advise 位。</para></value>
    internal bool CompactedIndexes { get; set; }

    /// <summary>Whether the compact index carries the 2-byte packed tail rows
    /// (<c>Z_EROFS_ADVISE_COMPACTED_2B</c>, which shares value <c>0x0001</c> with
    /// <c>Z_EROFS_ADVISE_EXTENTS</c> and is therefore only meaningful under a compact datalayout).
    /// <para>紧凑索引是否携带 2 字节打包尾部行（<c>Z_EROFS_ADVISE_COMPACTED_2B</c>，与
    /// <c>Z_EROFS_ADVISE_EXTENTS</c> 共用值 <c>0x0001</c>，因此仅在紧凑布局下有义）。</para></summary>
    /// <value><see langword="true"/> only when <see cref="CompactedIndexes"/> is set and the shared
    /// low advise bit is raised. <para>仅在 <see cref="CompactedIndexes"/> 置位且共用的低位 advise 位被置起时为
    /// <see langword="true"/>。</para></value>
    internal bool Compiled2B { get; set; }

    /// <summary>Whether any lcluster index row advertises a big pcluster.
    /// <para>是否有任一逻辑簇索引行声明了 big pcluster。</para></summary>
    /// <value>Raised from <c>Z_EROFS_ADVISE_BIG_PCLUSTER_1</c> or <c>_2</c>.
    /// <para>由 <c>Z_EROFS_ADVISE_BIG_PCLUSTER_1</c> 或 <c>_2</c> 置起。</para></value>
    internal bool BigPcluster { get; set; }

    /// <summary>Whether the final partial pcluster is stored beside the inode record
    /// (<c>Z_EROFS_ADVISE_INLINE_PCLUSTER</c>).
    /// <para>最后不完整 pcluster 是否存放在 inode 记录旁
    /// （<c>Z_EROFS_ADVISE_INLINE_PCLUSTER</c>）。</para></summary>
    /// <value>Raised from the map header advise word.
    /// <para>由映射头 advise 字置起。</para></value>
    internal bool InlineCompressedTail { get; set; }

    /// <summary>Whether any plain index row is stored with the interlaced byte rotation
    /// (<c>Z_EROFS_ADVISE_INTERLACED_PCLUSTER</c>).
    /// <para>是否有任一原样索引行按交错字节旋转存放
    /// （<c>Z_EROFS_ADVISE_INTERLACED_PCLUSTER</c>）。</para></summary>
    /// <value>Raised from the map header advise word; a rebuild only honours it while
    /// <see cref="ErofsOptions.InterlacedPlainPclusters"/> holds.
    /// <para>由映射头 advise 字置起；重建仅在 <see cref="ErofsOptions.InterlacedPlainPclusters"/> 成立时才兑现它。</para></value>
    internal bool InterlacedPlainPclusters { get; set; }

    /// <summary>Whether the source inode stored its index as the extent form
    /// (<c>Z_EROFS_ADVISE_EXTENTS</c> under the compressed-full datalayout).
    /// <para>源 inode 是否以 extent 形态存储索引（compressed-full 布局下的
    /// <c>Z_EROFS_ADVISE_EXTENTS</c>）。</para></summary>
    /// <value><see langword="true"/> only when the shared low advise bit is raised under
    /// <see cref="ErofsDataLayout.Compressed"/>, so it never aliases the compacted-2B bit a compact
    /// layout uses. <para>仅在 <see cref="ErofsDataLayout.Compressed"/> 下共用的低位 advise 位被置起时为
    /// <see langword="true"/>，因此绝不会与紧凑布局使用的 2B 位混同。</para></value>
    internal bool HasExtents { get; set; }

    /// <summary>Record size of the extent index rows in bytes.
    /// <para>extent 索引行的记录字节大小。</para></summary>
    /// <value>One of <c>4</c>, <c>8</c>, <c>16</c> or <c>32</c>; <c>0</c> when the inode carries no
    /// extent index. <para><c>4</c>、<c>8</c>、<c>16</c> 或 <c>32</c> 之一；inode 不携带 extent 索引时为
    /// <c>0</c>。</para></value>
    internal int ExtentRecordSize { get; set; }

    /// <summary>Logical cluster size in bytes the extent rows are laid out over.
    /// <para>extent 行所依据的逻辑簇字节大小。</para></summary>
    /// <value><c>1 &lt;&lt; (blkszbits + stored cluster bits)</c>, or <c>0</c> when the inode carries no
    /// extent index. <para><c>1 &lt;&lt; (blkszbits + 存储的簇位)</c>；inode 不携带 extent 索引时为
    /// <c>0</c>。</para></value>
    internal int ExtentClusterSize { get; set; }

    /// <summary>Number of extent index rows carried by a compressed-full inode.
    /// <para>compressed-full inode 携带的 extent 索引行数量。</para></summary>
    /// <value>Equal to <c>h_extents_lo | h_extents_hi &lt;&lt; 32</c>; zero when no extents are present.
    /// <para>等于 <c>h_extents_lo | h_extents_hi &lt;&lt; 32</c>；无 extent 时为零。</para></value>
    internal ulong ExtentCount { get; set; }
}