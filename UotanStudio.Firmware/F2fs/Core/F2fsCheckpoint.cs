using System.Buffers.Binary;

namespace UotanStudio.Firmware.F2fs;

/// <summary>
/// f2fs checkpoint（struct f2fs_checkpoint，一个块大小），位于 cp_blkaddr；用于定位
/// NAT/SIT 区、当前段与版本位图等关键状态。
/// <para>F2FS checkpoint (struct f2fs_checkpoint, one block) located at cp_blkaddr; carries the
/// NAT/SIT addressing, current segments and version bitmaps needed to locate the metadata areas.</para>
/// </summary>
internal sealed record F2fsCheckpoint
{
    /// <summary>CP_UMOUNT_FLAG：上次干净卸载标志。Cleanly-unmounted flag.</summary>
    public const uint FlagUnmount = 0x0000_0001;

    /// <summary>CP_COMPACT_SUM_FLAG：紧凑摘要标志。Compact-summary flag.</summary>
    public const uint FlagCompactSum = 0x0000_0004;

    /// <summary>默认 checksum 偏移（F2FS_BLKSIZE - 4）。Default checksum offset.</summary>
    public const int DefaultChecksumOffset = F2fsNode.NodeSize - 4;

    // 字段偏移（相对 checkpoint 块起始；MAX_ACTIVE_NODE_LOGS = MAX_ACTIVE_DATA_LOGS = 8，
    // 对齐内核 fs/f2fs/f2fs_fs.h 的 struct f2fs_checkpoint）。Field offsets relative to the
    // checkpoint block (kernel struct f2fs_checkpoint).
    private const int OffsetCheckpointVer = 0;
    private const int OffsetUserBlockCount = 8;
    private const int OffsetValidBlockCount = 16;
    private const int OffsetRsvdSegmentCount = 24;
    private const int OffsetOverprovSegmentCount = 28;
    private const int OffsetFreeSegmentCount = 32;
    private const int OffsetCurNodeSegno = 36;
    private const int OffsetCurNodeBlkoff = 68;   // __le16[8] 紧随 cur_node_segno[8]
    private const int OffsetCurDataSegno = 84;
    private const int OffsetCurDataBlkoff = 116;  // __le16[8] 紧随 cur_data_segno[8]
    private const int OffsetCkptFlags = 132;
    private const int OffsetCpPackTotalBlockCount = 136;
    private const int OffsetCpPackStartSum = 140;
    private const int OffsetValidNodeCount = 144;
    private const int OffsetValidInodeCount = 148;
    private const int OffsetNextFreeNid = 152;
    private const int OffsetSitVerBitmapBytesize = 156;
    private const int OffsetNatVerBitmapBytesize = 160;
    private const int OffsetChecksumOffset = 164;
    private const int OffsetElapsedTime = 168;
    private const int OffsetAllocType = 176;
    private const int OffsetVersionBitmap = 192;

    /// <summary>checkpoint 版本号。Checkpoint version.</summary>
    public ulong CheckpointVer { get; init; }

    /// <summary>用户块总数。Total user block count.</summary>
    public ulong UserBlockCount { get; init; }

    /// <summary>主区有效块数。Valid block count in the main area.</summary>
    public ulong ValidBlockCount { get; init; }

    /// <summary>GC 保留段数。Reserved segment count for GC.</summary>
    public uint RsvdSegmentCount { get; init; }

    /// <summary>over-provision 段数。Overprovision segment count.</summary>
    public uint OverprovSegmentCount { get; init; }

    /// <summary>主区空闲段数。Free segment count in the main area.</summary>
    public uint FreeSegmentCount { get; init; }

    /// <summary>当前 node 段号（cur_node_segno[0..7]）。Current node segment numbers.</summary>
    public uint[] CurNodeSegno { get; init; } = [];

    /// <summary>当前 node 段内下一空闲块偏移（cur_node_blkoff[0..7]，__le16）。
    /// Next free block offset inside each current node segment (__le16).</summary>
    public ushort[] CurNodeBlkoff { get; init; } = [];

    /// <summary>当前 data 段号（cur_data_segno[0..7]）。Current data segment numbers.</summary>
    public uint[] CurDataSegno { get; init; } = [];

    /// <summary>当前 data 段内下一空闲块偏移（cur_data_blkoff[0..7]，__le16）。
    /// Next free block offset inside each current data segment (__le16).</summary>
    public ushort[] CurDataBlkoff { get; init; } = [];

    /// <summary>checkpoint 标志。Checkpoint flags.</summary>
    public uint CkptFlags { get; init; }

    /// <summary>cp pack 总块数。Total block count of one cp pack.</summary>
    public uint CpPackTotalBlockCount { get; init; }

    /// <summary>数据摘要起始块号（pack 内相对）。Start block of the data summary within the pack.</summary>
    public uint CpPackStartSum { get; init; }

    /// <summary>有效节点总数。Total valid node count.</summary>
    public uint ValidNodeCount { get; init; }

    /// <summary>有效 inode 总数。Total valid inode count.</summary>
    public uint ValidInodeCount { get; init; }

    /// <summary>下一个空闲 nid。Next free nid.</summary>
    public uint NextFreeNid { get; init; }

    /// <summary>SIT 版本位图字节数。SIT version bitmap size in bytes.</summary>
    public uint SitVerBitmapBytesize { get; init; }

    /// <summary>NAT 版本位图字节数。NAT version bitmap size in bytes.</summary>
    public uint NatVerBitmapBytesize { get; init; }

    /// <summary>checksum 偏移。Checksum offset inside the checkpoint block.</summary>
    public uint ChecksumOffset { get; init; }

    /// <summary>已挂载时间。Mounted time.</summary>
    public ulong ElapsedTime { get; init; }

    /// <summary>版本位图起始偏移（sit_ver_bitmap + nat_ver_bitmap）。Start offset of the version bitmaps.</summary>
    public int VersionBitmapOffset => OffsetVersionBitmap;

    /// <summary>从 checkpoint 块解析。Parses a checkpoint from its block.</summary>
    public static F2fsCheckpoint FromBytes(ReadOnlySpan<byte> block)
    {
        if (block.Length < F2fsNode.NodeSize)
        {
            throw new FormatException($"F2FS checkpoint block too short: {block.Length} bytes.");
        }

        return new F2fsCheckpoint
        {
            CheckpointVer = BinaryPrimitives.ReadUInt64LittleEndian(block[OffsetCheckpointVer..]),
            UserBlockCount = BinaryPrimitives.ReadUInt64LittleEndian(block[OffsetUserBlockCount..]),
            ValidBlockCount = BinaryPrimitives.ReadUInt64LittleEndian(block[OffsetValidBlockCount..]),
            RsvdSegmentCount = BinaryPrimitives.ReadUInt32LittleEndian(block[OffsetRsvdSegmentCount..]),
            OverprovSegmentCount = BinaryPrimitives.ReadUInt32LittleEndian(block[OffsetOverprovSegmentCount..]),
            FreeSegmentCount = BinaryPrimitives.ReadUInt32LittleEndian(block[OffsetFreeSegmentCount..]),
            CurNodeSegno = ReadUInt32Array(block, OffsetCurNodeSegno, 8),
            CurNodeBlkoff = ReadUInt16Array(block, OffsetCurNodeBlkoff, 8),
            CurDataSegno = ReadUInt32Array(block, OffsetCurDataSegno, 8),
            CurDataBlkoff = ReadUInt16Array(block, OffsetCurDataBlkoff, 8),
            CkptFlags = BinaryPrimitives.ReadUInt32LittleEndian(block[OffsetCkptFlags..]),
            CpPackTotalBlockCount = BinaryPrimitives.ReadUInt32LittleEndian(block[OffsetCpPackTotalBlockCount..]),
            CpPackStartSum = BinaryPrimitives.ReadUInt32LittleEndian(block[OffsetCpPackStartSum..]),
            ValidNodeCount = BinaryPrimitives.ReadUInt32LittleEndian(block[OffsetValidNodeCount..]),
            ValidInodeCount = BinaryPrimitives.ReadUInt32LittleEndian(block[OffsetValidInodeCount..]),
            NextFreeNid = BinaryPrimitives.ReadUInt32LittleEndian(block[OffsetNextFreeNid..]),
            SitVerBitmapBytesize = BinaryPrimitives.ReadUInt32LittleEndian(block[OffsetSitVerBitmapBytesize..]),
            NatVerBitmapBytesize = BinaryPrimitives.ReadUInt32LittleEndian(block[OffsetNatVerBitmapBytesize..]),
            ChecksumOffset = BinaryPrimitives.ReadUInt32LittleEndian(block[OffsetChecksumOffset..]),
            ElapsedTime = BinaryPrimitives.ReadUInt64LittleEndian(block[OffsetElapsedTime..]),
        };
    }

    /// <summary>
    /// 将 checkpoint 关键字段写入块（版本位图区域与 checksum 一并写入）。
    /// <para>Writes the checkpoint key fields into a block (the version bitmap area and the
    /// checksum are written as well).</para>
    /// </summary>
    public void WriteTo(Span<byte> block, ReadOnlySpan<byte> versionBitmap)
    {
        if (block.Length < F2fsNode.NodeSize)
        {
            throw new ArgumentException("Checkpoint destination buffer is too small.", nameof(block));
        }

        block.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(block[OffsetCheckpointVer..], CheckpointVer);
        BinaryPrimitives.WriteUInt64LittleEndian(block[OffsetUserBlockCount..], UserBlockCount);
        BinaryPrimitives.WriteUInt64LittleEndian(block[OffsetValidBlockCount..], ValidBlockCount);
        BinaryPrimitives.WriteUInt32LittleEndian(block[OffsetRsvdSegmentCount..], RsvdSegmentCount);
        BinaryPrimitives.WriteUInt32LittleEndian(block[OffsetOverprovSegmentCount..], OverprovSegmentCount);
        BinaryPrimitives.WriteUInt32LittleEndian(block[OffsetFreeSegmentCount..], FreeSegmentCount);
        for (var i = 0; i < 8; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(block.Slice(OffsetCurNodeSegno + i * 4, 4), i < CurNodeSegno.Length ? CurNodeSegno[i] : uint.MaxValue);
            BinaryPrimitives.WriteUInt16LittleEndian(block.Slice(OffsetCurNodeBlkoff + i * 2, 2), i < CurNodeBlkoff.Length ? CurNodeBlkoff[i] : (ushort)0);
            BinaryPrimitives.WriteUInt32LittleEndian(block.Slice(OffsetCurDataSegno + i * 4, 4), i < CurDataSegno.Length ? CurDataSegno[i] : uint.MaxValue);
            BinaryPrimitives.WriteUInt16LittleEndian(block.Slice(OffsetCurDataBlkoff + i * 2, 2), i < CurDataBlkoff.Length ? CurDataBlkoff[i] : (ushort)0);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(block[OffsetCkptFlags..], CkptFlags);
        BinaryPrimitives.WriteUInt32LittleEndian(block[OffsetCpPackTotalBlockCount..], CpPackTotalBlockCount);
        BinaryPrimitives.WriteUInt32LittleEndian(block[OffsetCpPackStartSum..], CpPackStartSum);
        BinaryPrimitives.WriteUInt32LittleEndian(block[OffsetValidNodeCount..], ValidNodeCount);
        BinaryPrimitives.WriteUInt32LittleEndian(block[OffsetValidInodeCount..], ValidInodeCount);
        BinaryPrimitives.WriteUInt32LittleEndian(block[OffsetNextFreeNid..], NextFreeNid);
        BinaryPrimitives.WriteUInt32LittleEndian(block[OffsetSitVerBitmapBytesize..], SitVerBitmapBytesize);
        BinaryPrimitives.WriteUInt32LittleEndian(block[OffsetNatVerBitmapBytesize..], NatVerBitmapBytesize);
        BinaryPrimitives.WriteUInt32LittleEndian(block[OffsetChecksumOffset..], ChecksumOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(block[OffsetElapsedTime..], ElapsedTime);
        var bitmapLength = Math.Min(versionBitmap.Length, F2fsNode.NodeSize - OffsetVersionBitmap);
        versionBitmap.Slice(0, bitmapLength).CopyTo(block.Slice(OffsetVersionBitmap, bitmapLength));
    }

    private static uint[] ReadUInt32Array(ReadOnlySpan<byte> block, int offset, int count)
    {
        var result = new uint[count];
        for (var i = 0; i < count; i++)
        {
            result[i] = BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(offset + i * 4, 4));
        }

        return result;
    }

    private static ushort[] ReadUInt16Array(ReadOnlySpan<byte> block, int offset, int count)
    {
        var result = new ushort[count];
        for (var i = 0; i < count; i++)
        {
            result[i] = BinaryPrimitives.ReadUInt16LittleEndian(block.Slice(offset + i * 2, 2));
        }

        return result;
    }
}