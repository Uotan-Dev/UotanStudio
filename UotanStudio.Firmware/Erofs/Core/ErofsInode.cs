using System.Buffers.Binary;

namespace UotanStudio.Firmware.Erofs;

/// <summary>
/// erofs inode：compact（32 字节）与 extended（72 字节）两种布局，均为小端。
/// <para>EROFS inode: both compact (32 bytes) and extended (72 bytes) layouts, little-endian.</para>
/// </summary>
internal sealed class ErofsInode
{
    /// <summary>compact inode 大小。Size of a compact inode.</summary>
    public const int CompactSize = 32;

    /// <summary>
    /// extended inode 大小。Linux 内核 erofs_fs.h 的标准 extended inode 为 72 字节
    /// （任务描述中的 64B 与标准布局不符，此处遵循内核标准以保证自洽）。
    /// <para>Size of an extended inode. The standard extended inode in Linux erofs_fs.h is 72 bytes
    /// (the 64B mentioned in the task deviates from the standard; this implementation follows the
    /// kernel layout to stay self-consistent).</para>
    /// </summary>
    public const int ExtendedSize = 72;

    // i_format 位域（本实现使用的标准子集）。i_format bit fields (subset used by this implementation).
    /// <summary>数据布局位掩码。Data layout mask.</summary>
    public const ushort DataLayoutMask = 0x7;

    /// <summary>扁平布局（未压缩，i_u = raw_blkaddr）。Flat layout (uncompressed, i_u = raw_blkaddr).</summary>
    public const ushort DataLayoutFlat = 0;

    /// <summary>压缩布局（pcluster 按块对齐，i_u = compressed_blocks）。Compressed layout (block-aligned pclusters, i_u = compressed_blocks).</summary>
    public const ushort DataLayoutCompressed = 1;

    /// <summary>紧凑索引压缩布局（保留）。Compacted-index compression layout (reserved).</summary>
    public const ushort DataLayoutCompactIndexes = 2;

    /// <summary>内联布局（保留）。Inline layout (reserved).</summary>
    public const ushort DataLayoutInline = 3;

    /// <summary>含 xattr 标志。Has-xattr flag.</summary>
    public const ushort FlagHasXattr = 1 << 3;

    /// <summary>块映射标志。Block-mapped flag.</summary>
    public const ushort FlagBlockMapped = 1 << 4;

    /// <summary>extended inode 标志（本实现约定的位 5）。Extended-inode flag (bit 5, as chosen by this implementation).</summary>
    public const ushort FlagExtended = 1 << 5;

    /// <summary>i_mode 文件类型掩码。i_mode file type mask.</summary>
    public const ushort ModeTypeMask = 0xF000;

    /// <summary>目录类型位。Directory type bits.</summary>
    public const ushort ModeDirectory = 0x4000;

    /// <summary>普通文件类型位。Regular-file type bits.</summary>
    public const ushort ModeRegularFile = 0x8000;

    /// <summary>dirent 文件类型：未知。Dirent file type: unknown.</summary>
    public const byte FileTypeUnknown = 0;

    /// <summary>dirent 文件类型：普通文件。Dirent file type: regular file.</summary>
    public const byte FileTypeRegularFile = 1;

    /// <summary>dirent 文件类型：目录。Dirent file type: directory.</summary>
    public const byte FileTypeDirectory = 2;

    /// <summary>dirent 文件类型：符号链接。Dirent file type: symbolic link.</summary>
    public const byte FileTypeSymlink = 7;

    /// <summary>i_format / i_advise。Format / advise field.</summary>
    public ushort Advise { get; set; }

    /// <summary>xattr 条目数量（以 4 字节计）。Xattr entry count (in 4-byte units).</summary>
    public ushort XattrICount { get; set; }

    /// <summary>i_mode（含文件类型位）。Mode including file type bits.</summary>
    public ushort Mode { get; set; }

    /// <summary>链接数。Link count.</summary>
    public ulong Nlink { get; set; }

    /// <summary>i_size（文件/目录字节大小）。Size in bytes.</summary>
    public ulong Size { get; set; }

    /// <summary>i_u：扁平布局为数据起始块号，压缩布局为压缩块总数。i_u: raw block address (flat) or compressed block count (compressed).</summary>
    public uint U { get; set; }

    /// <summary>i_ino（32 位 stat 兼容号）。Inode number for 32-bit stat compatibility.</summary>
    public uint Ino { get; set; }

    /// <summary>uid。User id.</summary>
    public uint Uid { get; set; }

    /// <summary>gid。Group id.</summary>
    public uint Gid { get; set; }

    /// <summary>generation（compact inode 的 i_reserved 字段）。Generation (compact inode i_reserved field).</summary>
    public uint Generation { get; set; }

    /// <summary>i_ctime（Unix 秒）。Creation/modification time in Unix seconds.</summary>
    public ulong CTime { get; set; }

    /// <summary>i_ctime 纳秒部分。Creation/modification time nanoseconds.</summary>
    public uint CTimeNsec { get; set; }

    /// <summary>是否为 extended inode。Whether this is an extended inode.</summary>
    public bool IsExtended => (Advise & FlagExtended) != 0;

    /// <summary>是否为目录。Whether this is a directory.</summary>
    public bool IsDirectory => (Mode & ModeTypeMask) == ModeDirectory;

    /// <summary>是否为普通文件。Whether this is a regular file.</summary>
    public bool IsRegularFile => (Mode & ModeTypeMask) == ModeRegularFile;

    /// <summary>是否为压缩布局。Whether the data is compressed.</summary>
    public bool IsCompressed => (Advise & DataLayoutMask) == DataLayoutCompressed;

    /// <summary>磁盘占用大小（compact 或 extended）。On-disk size (compact or extended).</summary>
    public int OnDiskSize => IsExtended ? ExtendedSize : CompactSize;

    /// <summary>数据起始块号（扁平布局）。Start block of file data (flat layout).</summary>
    public uint RawBlockAddress => U;

    /// <summary>压缩块总数（压缩布局）。Total compressed blocks (compressed layout).</summary>
    public uint CompressedBlocks => U;

    /// <summary>
    /// 从字节解析 inode；根据 i_format 的 extended 标志自动选择 compact/extended 布局。
    /// <para>Parses an inode from bytes; selects compact/extended layout from the extended flag in
    /// i_format.</para>
    /// </summary>
    public static ErofsInode FromBytes(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2)
        {
            throw new FormatException("Inode region too short.");
        }

        var advise = BinaryPrimitives.ReadUInt16LittleEndian(data);
        if ((advise & FlagExtended) != 0)
        {
            if (data.Length < ExtendedSize)
            {
                throw new FormatException($"Extended inode region too short: {data.Length} bytes.");
            }

            return new ErofsInode
            {
                Advise = advise,
                XattrICount = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(2)),
                Mode = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(4)),
                Size = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(8)),
                U = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(16)),
                Ino = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(20)),
                Uid = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(24)),
                Gid = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(28)),
                CTime = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(32)),
                CTimeNsec = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(40)),
                Nlink = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(44)),
            };
        }

        if (data.Length < CompactSize)
        {
            throw new FormatException($"Compact inode region too short: {data.Length} bytes.");
        }

        return new ErofsInode
        {
            Advise = advise,
            XattrICount = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(2)),
            Mode = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(4)),
            Nlink = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(6)),
            Size = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(8)),
            Generation = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(12)),
            U = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(16)),
            Ino = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(20)),
            Uid = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(24)),
            Gid = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(26)),
        };
    }

    /// <summary>
    /// 将 inode 序列化到缓冲区（长度须至少为 OnDiskSize）。
    /// <para>Serializes the inode into a buffer of at least OnDiskSize bytes.</para>
    /// </summary>
    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < OnDiskSize)
        {
            throw new ArgumentException("Inode destination buffer is too small.", nameof(destination));
        }

        destination.Clear();
        if (IsExtended)
        {
            WriteExtended(destination);
            return;
        }

        WriteCompact(destination);
    }

    private void WriteCompact(Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(destination, Advise);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[2..], XattrICount);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[4..], Mode);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[6..], (ushort)Math.Min(Nlink, ushort.MaxValue));
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], (uint)Math.Min(Size, uint.MaxValue));
        BinaryPrimitives.WriteUInt32LittleEndian(destination[12..], Generation);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[16..], U);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[20..], Ino);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[24..], (ushort)Math.Min(Uid, ushort.MaxValue));
        BinaryPrimitives.WriteUInt16LittleEndian(destination[26..], (ushort)Math.Min(Gid, ushort.MaxValue));
    }

    private void WriteExtended(Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(destination, Advise);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[2..], XattrICount);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[4..], Mode);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[8..], Size);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[16..], U);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[20..], Ino);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[24..], Uid);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[28..], Gid);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[32..], CTime);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[40..], CTimeNsec);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[44..], (uint)Math.Min(Nlink, uint.MaxValue));
    }
}