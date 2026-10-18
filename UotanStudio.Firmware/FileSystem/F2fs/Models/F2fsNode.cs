using System.Buffers.Binary;

namespace UotanStudio.Firmware.FileSystem.F2fs.Models;

internal static class F2fsNode
{
    public const int NodeSize = 4096;

    public const int FooterOffset = NodeSize - F2fsNodeFooter.FooterSize;

    public const int InodeEndOffset = 4072;

    public const int AddrArrayOffset = 360;

    public const int NidArrayOffset = AddrArrayOffset + DefAddrsPerInode * 4;

    public const int DefAddrsPerInode = 923;

    public const int DefNidsPerInode = 5;

    public const int AddrsPerBlock = 1018;

    public const int DefInlineReservedSize = 1;

    public const long Direct1Block = DefAddrsPerInode;

    public const long Direct2Block = Direct1Block + AddrsPerBlock;

    public const long Indirect1Block = Direct2Block + AddrsPerBlock;

    public const uint ReservedNodeCount = 3;

    public const int NatEntryPerBlock = 455;

    public const int NatEntrySize = 9;

    public const int NatEntryInoOffset = 1;

    public const int NatEntryBlockOffset = 5;

    public const int SitEntryPerBlock = 55;

    public const int SitValidMapSize = 64;
}

internal sealed record F2fsNodeFooter
{
    public const int FooterSize = 24;

    public const int OffsetBitShift = 8;


    private const int OffsetNid = 0;
    private const int OffsetIno = 4;
    private const int OffsetFlag = 8;
    private const int OffsetCpVersion = 12;
    private const int OffsetNextBlkaddr = 20;

    public uint Nid { get; init; }

    public uint Ino { get; init; }

    public uint Flag { get; init; }

    public ulong CpVersion { get; init; }

    public uint NextBlkaddr { get; init; }

    public bool IsInode => Nid == Ino;

    /// <summary>
    /// Parses the footer from the tail of a node page.
    /// <para>从节点页末尾解析页脚。</para>
    /// </summary>
    /// <param name="node">Full node page of at least <see cref="F2fsNode.NodeSize"/> bytes; the footer is read at <see cref="F2fsNode.FooterOffset"/>, the tail position of a node-sized page.
    /// <para>完整的节点页，长度至少为 <see cref="F2fsNode.NodeSize"/> 字节；页脚按 <see cref="F2fsNode.FooterOffset"/> 读取，即节点大小页面的尾部位置。</para></param>
    /// <returns>Footer carrying the little-endian nid, ino, flag, checkpoint version and next block address; <see cref="IsInode"/> reports whether nid and ino match.
    /// <para>返回的页脚含小端的 nid、ino、flag、checkpoint 版本与下一块地址；<see cref="IsInode"/> 反映 nid 与 ino 是否相同。</para></returns>
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

    /// <summary>Writes the footer into the tail of a node page.<para>将页脚写入节点页末尾。</para></summary>
    /// <param name="node">Destination node page with at least <see cref="F2fsNode.NodeSize"/> writable bytes; a shorter buffer is rejected before anything is written.
    /// <para>目标节点页，可写字节至少为 <see cref="F2fsNode.NodeSize"/>；缓冲区过短时在写入任何内容前就被拒绝。</para></param>
    /// <param name="nid">Node id written at the start of the footer, equal to the inode number for an inode node.
    /// <para>写入页脚起始处的节点号；对 inode 节点而言它等于 inode 号。</para></param>
    /// <param name="ino">Owning inode number written four bytes after the node id.
    /// <para>所属 inode 号，写在节点号之后 4 字节处。</para></param>
    /// <param name="flag">Footer flag word written at offset 8, holding the node type and inline-xattr bits.
    /// <para>写在偏移 8 的页脚标志字，含节点类型与内联 xattr 位。</para></param>
    /// <param name="cpVersion">64-bit checkpoint version written at offset 12.
    /// <para>写在偏移 12 的 64 位 checkpoint 版本。</para></param>
    /// <param name="nextBlkaddr">Block address of the next node page written at offset 20, or 0 when the chain ends.
    /// <para>写在偏移 20 的下一个节点页块地址，链结束时为 0。</para></param>
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