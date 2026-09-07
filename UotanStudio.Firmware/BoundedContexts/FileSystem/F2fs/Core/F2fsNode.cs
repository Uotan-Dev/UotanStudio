using System.Buffers.Binary;

namespace UotanStudio.Firmware.BoundedContexts.FileSystem.F2fs;

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

    /// <summary>
    /// Writes the footer into the tail of a node page.
    /// <para>将页脚写入节点页末尾。</para>
    /// </summary>
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
