using System.Buffers.Binary;

namespace UotanStudio.Firmware.F2fs;

/// <summary>
/// f2fs 节点（struct f2fs_node）：4096 字节节点页，尾部为 24 字节 struct node_footer。
/// 提供 inode/direct/indirect 三种节点共享的布局常量与页脚解析/序列化。
/// <para>F2FS node (struct f2fs_node): a 4096-byte node page ending with the 24-byte
/// struct node_footer. Provides the shared layout constants and footer parsing/serialization for
/// inode, direct and indirect nodes.</para>
/// </summary>
internal static class F2fsNode
{
    /// <summary>节点页大小（= F2FS_BLKSIZE）。Node page size (= F2FS_BLKSIZE).</summary>
    public const int NodeSize = 4096;

    /// <summary>页脚在节点页内的字节偏移（F2FS_BLKSIZE - sizeof(node_footer)）。Byte offset of the footer within a node page.</summary>
    public const int FooterOffset = NodeSize - F2fsNodeFooter.FooterSize;

    /// <summary>inode 节点内 inode 结构结束（页脚起始）的偏移，即 i_addr 区结束。Offset where the inode structure ends in an inode node.</summary>
    public const int InodeEndOffset = 4072;

    /// <summary>inode 内 i_addr 数组起始偏移（OFFSET_OF_END_OF_I_EXT）。Start offset of the i_addr array (OFFSET_OF_END_OF_I_EXT).</summary>
    public const int AddrArrayOffset = 360;

    /// <summary>inode 内 i_nid 节点指针数组起始偏移（紧接 i_addr 之后，结束于页脚起始处）。
    /// Start offset of the i_nid node-pointer array (right after i_addr, ending at the footer).</summary>
    public const int NidArrayOffset = AddrArrayOffset + DefAddrsPerInode * 4;

    /// <summary>inode 内 i_addr 直接指针数（DEF_ADDRS_PER_INODE = (4096-360-20-24)/4）。Direct address pointers inside an inode.</summary>
    public const int DefAddrsPerInode = 923;

    /// <summary>inode 内 i_nid 节点指针数（DEF_NIDS_PER_INODE）。Node ids inside an inode.</summary>
    public const int DefNidsPerInode = 5;

    /// <summary>direct/indirect 节点页内指针数（ADDRS_PER_BLOCK = NIDS_PER_BLOCK = 1018）。Pointers per direct/indirect node page.</summary>
    public const int AddrsPerBlock = 1018;

    /// <summary>inline 数据保留的 LE32 槽数（DEF_INLINE_RESERVED_SIZE）。Reserved LE32 slots before inline data.</summary>
    public const int DefInlineReservedSize = 1;

    /// <summary>direct 节点 1 覆盖的文件逻辑块起始号。First logical block covered by direct node 1.</summary>
    public const long Direct1Block = DefAddrsPerInode;

    /// <summary>direct 节点 2 覆盖的文件逻辑块起始号。First logical block covered by direct node 2.</summary>
    public const long Direct2Block = Direct1Block + AddrsPerBlock;

    /// <summary>indirect 节点 1 覆盖的文件逻辑块起始号。First logical block covered by indirect node 1.</summary>
    public const long Indirect1Block = Direct2Block + AddrsPerBlock;

    /// <summary>保留 nid 数（0/1/2 分别表示 null/node_ino/meta_ino），第一个可用 inode 号为 3。
    /// Reserved nid count (0/1/2 are null/node_ino/meta_ino); the first usable inode number is 3.</summary>
    public const uint ReservedNodeCount = 3;

    /// <summary>每 NAT 块条目数（F2FS_BLKSIZE / sizeof(struct f2fs_nat_entry) = 455）。
    /// NAT entries per block.</summary>
    public const int NatEntryPerBlock = 455;

    /// <summary>NAT 条目大小（version 1 + ino 4 + block_addr 4 = 9 字节）。Size of a NAT entry.</summary>
    public const int NatEntrySize = 9;

    /// <summary>NAT 条目内 ino 偏移。Offset of ino inside a NAT entry.</summary>
    public const int NatEntryInoOffset = 1;

    /// <summary>NAT 条目内 block_addr 偏移。Offset of block_addr inside a NAT entry.</summary>
    public const int NatEntryBlockOffset = 5;

    /// <summary>每 SIT 块条目数（F2FS_BLKSIZE / sizeof(struct f2fs_sit_entry) = 55）。
    /// SIT entries per block.</summary>
    public const int SitEntryPerBlock = 55;

    /// <summary>SIT valid_map 大小（字节，= 512 位）。Size of the SIT valid bitmap in bytes.</summary>
    public const int SitValidMapSize = 64;
}

/// <summary>
/// f2fs 节点页脚（struct node_footer，24 字节，位于节点页末尾）。
/// <para>F2FS node footer (struct node_footer, 24 bytes, at the end of a node page).</para>
/// </summary>
internal sealed record F2fsNodeFooter
{
    /// <summary>页脚大小（字节）。Footer size in bytes.</summary>
    public const int FooterSize = 24;

    /// <summary>OFFSET_BIT_SHIFT：flag 高位存放文件内数据块偏移的移位。Offset bit shift inside the flag field.</summary>
    public const int OffsetBitShift = 8;

    // 页脚内字段偏移。Field offsets within the footer.
    private const int OffsetNid = 0;
    private const int OffsetIno = 4;
    private const int OffsetFlag = 8;
    private const int OffsetCpVersion = 12;
    private const int OffsetNextBlkaddr = 20;

    /// <summary>节点 id（nid）。Node id.</summary>
    public uint Nid { get; init; }

    /// <summary>所属 inode 号（ino）。Owning inode number.</summary>
    public uint Ino { get; init; }

    /// <summary>标志位（含 cold/fsync/dentry 位与文件内偏移）。Flag field (cold/fsync/dentry bits plus in-file offset).</summary>
    public uint Flag { get; init; }

    /// <summary>checkpoint 版本。Checkpoint version.</summary>
    public ulong CpVersion { get; init; }

    /// <summary>下一个节点页块号。Next node page block address.</summary>
    public uint NextBlkaddr { get; init; }

    /// <summary>该节点是否为 inode 节点（nid == ino，RAW_IS_INODE）。Whether this is an inode node (nid == ino).</summary>
    public bool IsInode => Nid == Ino;

    /// <summary>从节点页末尾解析页脚。Parses the footer from the tail of a node page.</summary>
    public static F2fsNodeFooter FromBytes(ReadOnlySpan<byte> node)
    {
        if (node.Length < F2fsNode.NodeSize)
        {
            throw new FormatException($"F2FS node page too short: {node.Length} bytes.");
        }

        var data = node.Slice(F2fsNode.FooterOffset, FooterSize);
        return new F2fsNodeFooter
        {
            Nid = BinaryPrimitives.ReadUInt32LittleEndian(data[OffsetNid..]),
            Ino = BinaryPrimitives.ReadUInt32LittleEndian(data[OffsetIno..]),
            Flag = BinaryPrimitives.ReadUInt32LittleEndian(data[OffsetFlag..]),
            CpVersion = BinaryPrimitives.ReadUInt64LittleEndian(data[OffsetCpVersion..]),
            NextBlkaddr = BinaryPrimitives.ReadUInt32LittleEndian(data[OffsetNextBlkaddr..]),
        };
    }

    /// <summary>将页脚写入节点页末尾。Writes the footer into the tail of a node page.</summary>
    public static void WriteTo(Span<byte> node, uint nid, uint ino, uint flag, ulong cpVersion, uint nextBlkaddr)
    {
        if (node.Length < F2fsNode.NodeSize)
        {
            throw new ArgumentException("Node destination buffer is too small.", nameof(node));
        }

        var data = node.Slice(F2fsNode.FooterOffset, FooterSize);
        data.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(data[OffsetNid..], nid);
        BinaryPrimitives.WriteUInt32LittleEndian(data[OffsetIno..], ino);
        BinaryPrimitives.WriteUInt32LittleEndian(data[OffsetFlag..], flag);
        BinaryPrimitives.WriteUInt64LittleEndian(data[OffsetCpVersion..], cpVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(data[OffsetNextBlkaddr..], nextBlkaddr);
    }
}