using System.Buffers.Binary;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Metadata;

namespace UotanStudio.Firmware.BoundedContexts.FileSystem.F2fs.Models;

internal sealed record F2fsInode
{
    private const ushort ModeFormatMask = 0xF000;
    private const ushort ModeRegularFile = 0x8000;
    private const ushort ModeDirectory = 0x4000;
    private const ushort ModeSymbolicLink = 0xA000;

    public const byte InlineXattrFlag = 0x01;

    public const byte InlineDataFlag = 0x02;

    public const byte InlineDentryFlag = 0x04;

    public const byte DataExistFlag = 0x08;

    public const byte ExtraAttrFlag = 0x20;

    public const uint ComprFlag = 0x0000_0004;


    private const int OffsetMode = 0;
    private const int OffsetAdvise = 2;
    private const int OffsetInline = 3;
    private const int OffsetUid = 4;
    private const int OffsetGid = 8;
    private const int OffsetLinks = 12;
    private const int OffsetSize = 16;
    private const int OffsetBlocks = 24;
    private const int OffsetAtime = 32;
    private const int OffsetCtime = 40;
    private const int OffsetMtime = 48;
    private const int OffsetAtimeNsec = 56;
    private const int OffsetCtimeNsec = 60;
    private const int OffsetMtimeNsec = 64;
    private const int OffsetGeneration = 68;
    private const int OffsetCurrentDepth = 72;
    private const int OffsetXattrNid = 76;
    private const int OffsetFlags = 80;
    private const int OffsetPino = 84;
    private const int OffsetNamelen = 88;
    private const int OffsetName = 92;
    private const int OffsetExtraIsize = 360;
    private const int OffsetInlineXattrSize = 362;

    public ushort Mode { get; init; }

    public byte Advise { get; init; }

    public byte Inline { get; init; }

    public uint Uid { get; init; }

    public uint Gid { get; init; }

    public uint Links { get; init; }

    public ulong Size { get; init; }

    public ulong Blocks { get; init; }

    public uint XattrNid { get; init; }

    public uint Flags { get; init; }

    public uint CurrentDepth { get; init; }

    public uint Pino { get; init; }

    public uint Namelen { get; init; }

    public byte[] Name { get; init; } = [];

    public F2fsExtent Extent { get; init; } = new F2fsExtent();

    public uint[] Addr { get; init; } = [];

    public uint[] Nid { get; init; } = [];

    public ulong Mtime { get; init; }

    public uint MtimeNsec { get; init; }

    public ulong Ctime { get; init; }

    public uint CtimeNsec { get; init; }

    public ulong Atime { get; init; }
    public uint AtimeNsec { get; init; }
    public uint Generation { get; init; }
    public uint? ProjectId { get; init; }
    public FileSystemTimestamp? BirthTime { get; init; }

    public FileSystemInodeMetadata Metadata => new()
    {
        AccessTime = new(unchecked((long)Atime), AtimeNsec),
        ChangeTime = new(unchecked((long)Ctime), CtimeNsec),
        ModificationTime = new(unchecked((long)Mtime), MtimeNsec),
        BirthTime = BirthTime,
        Generation = Generation,
        ProjectId = ProjectId,
        LinkCount = Links,
        F2fsAdvise = Advise,
        F2fsCompression = IsCompressed ? new(CompressAlgorithm, LogClusterSize, CompressFlags) : null
    };

    public ushort ExtraIsize { get; init; }

    public ushort InlineXattrSize { get; init; }

    public byte CompressAlgorithm { get; init; }

    public byte LogClusterSize { get; init; }

    public ushort CompressFlags { get; init; }

    public ulong CompressedBlocks { get; init; }

    public bool IsCompressed => (Flags & ComprFlag) != 0;

    public bool IsRegularFile => (Mode & ModeFormatMask) == ModeRegularFile;

    public bool IsDirectory => (Mode & ModeFormatMask) == ModeDirectory;

    public bool IsSymlink => (Mode & ModeFormatMask) == ModeSymbolicLink;

    public bool HasInlineData => (Inline & InlineDataFlag) != 0;

    public bool HasInlineDentry => (Inline & InlineDentryFlag) != 0;

    public int ExtraIsizeWords => (Inline & ExtraAttrFlag) != 0 ? ExtraIsize / 4 : 0;

    public int AddressCount => AlignAddressCount(F2fsNode.DefAddrsPerInode - ExtraIsizeWords - InlineXattrAddrs);

    public int DataAddressesPerNode => AlignAddressCount(F2fsNode.AddrsPerBlock);

    private int AlignAddressCount(int count) => IsCompressed ? count & ~((1 << LogClusterSize) - 1) : count;

    public int InlineXattrAddrs => (Inline & InlineXattrFlag) != 0 ? InlineXattrSize : 0;

    public int MaxInlineData => (F2fsNode.DefAddrsPerInode - ExtraIsizeWords - InlineXattrAddrs - F2fsNode.DefInlineReservedSize) * 4;

    public int InlineDataOffset => F2fsNode.AddrArrayOffset + (ExtraIsizeWords + F2fsNode.DefInlineReservedSize) * 4;

    /// <summary>
    /// Parses the inode from a node page (the footer must be validated separately: nid == ino marks an inode node).
    /// <para>从节点页解析 inode（页脚需同时校验：nid == ino 表示 inode 节点）。</para>
    /// </summary>
    public static F2fsInode FromBytes(ReadOnlySpan<byte> node, bool flexibleInlineXattr = true)
    {
        if (node.Length < F2fsNode.NodeSize)
        {
            throw new FormatException($"F2FS node page too short: {node.Length} bytes.");
        }

        var addrs = new uint[F2fsNode.DefAddrsPerInode];
        for (var i = 0; i < addrs.Length; i++)
        {
            addrs[i] = BinaryPrimitives.ReadUInt32LittleEndian(node.Slice(F2fsNode.AddrArrayOffset + i * 4, 4));
        }

        var nids = new uint[F2fsNode.DefNidsPerInode];
        var nidOffset = F2fsNode.NidArrayOffset;
        for (var i = 0; i < nids.Length; i++)
        {
            nids[i] = BinaryPrimitives.ReadUInt32LittleEndian(node.Slice(nidOffset + i * 4, 4));
        }

        var namelen = BinaryPrimitives.ReadUInt32LittleEndian(node[OffsetNamelen..]);
        var name = new byte[F2fsNode.NodeSize - OffsetName];
        node.Slice(OffsetName).CopyTo(name);

        var advise = node[OffsetAdvise];
        var extraSize = (node[OffsetInline] & ExtraAttrFlag) != 0 ? BinaryPrimitives.ReadUInt16LittleEndian(node[OffsetExtraIsize..]) : 0;
        if (extraSize % 4 != 0 || extraSize > F2fsNode.DefAddrsPerInode * 4)
            throw new InvalidDataException("Invalid F2FS extra inode attribute size.");
        var compressed = (BinaryPrimitives.ReadUInt32LittleEndian(node[OffsetFlags..]) & ComprFlag) != 0;
        if (compressed && extraSize < 36)
            throw new InvalidDataException("F2FS compressed inode lacks compression extra attributes.");
        if (compressed && node[393] is < 2 or > 8)
            throw new InvalidDataException("Invalid F2FS compression cluster size.");

        return new F2fsInode
        {
            Mode = BinaryPrimitives.ReadUInt16LittleEndian(node[OffsetMode..]),
            Advise = advise,
            Inline = node[OffsetInline],
            Uid = BinaryPrimitives.ReadUInt32LittleEndian(node[OffsetUid..]),
            Gid = BinaryPrimitives.ReadUInt32LittleEndian(node[OffsetGid..]),
            Links = BinaryPrimitives.ReadUInt32LittleEndian(node[OffsetLinks..]),
            Size = BinaryPrimitives.ReadUInt64LittleEndian(node[OffsetSize..]),
            Blocks = BinaryPrimitives.ReadUInt64LittleEndian(node[OffsetBlocks..]),
            Atime = BinaryPrimitives.ReadUInt64LittleEndian(node[OffsetAtime..]),
            AtimeNsec = BinaryPrimitives.ReadUInt32LittleEndian(node[OffsetAtimeNsec..]),
            Generation = BinaryPrimitives.ReadUInt32LittleEndian(node[OffsetGeneration..]),
            ProjectId = extraSize >= 8 ? BinaryPrimitives.ReadUInt32LittleEndian(node[364..]) : null,
            BirthTime = extraSize >= 24 ? new(BinaryPrimitives.ReadInt64LittleEndian(node[372..]), BinaryPrimitives.ReadUInt32LittleEndian(node[380..])) : null,
            Mtime = BinaryPrimitives.ReadUInt64LittleEndian(node[OffsetMtime..]),
            MtimeNsec = BinaryPrimitives.ReadUInt32LittleEndian(node[OffsetMtimeNsec..]),
            Ctime = BinaryPrimitives.ReadUInt64LittleEndian(node[OffsetCtime..]),
            CtimeNsec = BinaryPrimitives.ReadUInt32LittleEndian(node[OffsetCtimeNsec..]),
            XattrNid = BinaryPrimitives.ReadUInt32LittleEndian(node[OffsetXattrNid..]),
            Flags = BinaryPrimitives.ReadUInt32LittleEndian(node[OffsetFlags..]),
            Pino = BinaryPrimitives.ReadUInt32LittleEndian(node[OffsetPino..]),
            CurrentDepth = BinaryPrimitives.ReadUInt32LittleEndian(node[OffsetCurrentDepth..]),
            Namelen = namelen,
            Name = TrimName(name, namelen),
            Extent = F2fsExtent.FromBytes(node.Slice(F2fsExtent.InodeOffset, F2fsExtent.Size)),
            Addr = addrs,
            Nid = nids,
            ExtraIsize = BinaryPrimitives.ReadUInt16LittleEndian(node[OffsetExtraIsize..]),
            InlineXattrSize = !flexibleInlineXattr && (node[OffsetInline] & InlineXattrFlag) != 0 ? (ushort)50 : BinaryPrimitives.ReadUInt16LittleEndian(node[OffsetInlineXattrSize..]),
            CompressAlgorithm = extraSize >= 36 ? node[392] : (byte)0,
            LogClusterSize = extraSize >= 36 ? node[393] : (byte)0,
            CompressFlags = extraSize >= 36 ? BinaryPrimitives.ReadUInt16LittleEndian(node[394..]) : (ushort)0,
            CompressedBlocks = extraSize >= 36 ? BinaryPrimitives.ReadUInt64LittleEndian(node[384..]) : 0,
        };
    }

    /// <summary>
    /// Writes the inode fields into a node page (the footer is written separately by the caller; untouched fields keep their previous values).
    /// <para>将 inode 字段写入节点页（页脚由调用方另行写入；未覆盖字段保持原样）。</para>
    /// </summary>
    public void WriteTo(Span<byte> node)
    {
        if (node.Length < F2fsNode.NodeSize)
        {
            throw new ArgumentException("Inode destination buffer is too small.", nameof(node));
        }

        BinaryPrimitives.WriteUInt16LittleEndian(node[OffsetMode..], Mode);
        node[OffsetAdvise] = Advise;
        node[OffsetInline] = Inline;
        BinaryPrimitives.WriteUInt32LittleEndian(node[OffsetUid..], Uid);
        BinaryPrimitives.WriteUInt32LittleEndian(node[OffsetGid..], Gid);
        BinaryPrimitives.WriteUInt32LittleEndian(node[OffsetLinks..], Links);
        BinaryPrimitives.WriteUInt64LittleEndian(node[OffsetSize..], Size);
        BinaryPrimitives.WriteUInt64LittleEndian(node[OffsetBlocks..], Blocks);


        Metadata.Validate();
        BinaryPrimitives.WriteUInt64LittleEndian(node[OffsetAtime..], Atime);
        BinaryPrimitives.WriteUInt64LittleEndian(node[OffsetCtime..], Ctime);
        BinaryPrimitives.WriteUInt64LittleEndian(node[OffsetMtime..], Mtime);
        BinaryPrimitives.WriteUInt32LittleEndian(node[OffsetAtimeNsec..], AtimeNsec);
        BinaryPrimitives.WriteUInt32LittleEndian(node[OffsetCtimeNsec..], CtimeNsec);
        BinaryPrimitives.WriteUInt32LittleEndian(node[OffsetMtimeNsec..], MtimeNsec);
        BinaryPrimitives.WriteUInt32LittleEndian(node[OffsetGeneration..], Generation);
        BinaryPrimitives.WriteUInt32LittleEndian(node[OffsetCurrentDepth..], CurrentDepth);
        BinaryPrimitives.WriteUInt32LittleEndian(node[OffsetXattrNid..], XattrNid);
        BinaryPrimitives.WriteUInt32LittleEndian(node[OffsetFlags..], Flags);
        BinaryPrimitives.WriteUInt32LittleEndian(node[OffsetPino..], Pino);
        BinaryPrimitives.WriteUInt32LittleEndian(node[OffsetNamelen..], Namelen);
        node.Slice(OffsetName, Math.Min(F2fsNode.NodeSize - OffsetName, Name.Length)).Clear();
        Name.AsSpan(0, Math.Min(F2fsNode.NodeSize - OffsetName, Name.Length)).CopyTo(node[OffsetName..]);
        Extent.WriteTo(node.Slice(F2fsExtent.InodeOffset, F2fsExtent.Size));
        for (var i = 0; i < Addr.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(node.Slice(F2fsNode.AddrArrayOffset + i * 4, 4), Addr[i]);
        }

        if ((Inline & ExtraAttrFlag) != 0)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(node[OffsetExtraIsize..], ExtraIsize);
            BinaryPrimitives.WriteUInt16LittleEndian(node[OffsetInlineXattrSize..], InlineXattrSize);
            if (ExtraIsize >= 8)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(node[364..], ProjectId ?? 0);
            }
            if (ExtraIsize >= 24)
            {
                BinaryPrimitives.WriteInt64LittleEndian(node[372..], BirthTime?.Seconds ?? 0);
                BinaryPrimitives.WriteUInt32LittleEndian(node[380..], BirthTime?.Nanoseconds ?? 0);
            }
            if (ExtraIsize >= 36)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(node[384..], CompressedBlocks);
                node[392] = CompressAlgorithm;
                node[393] = LogClusterSize;
                BinaryPrimitives.WriteUInt16LittleEndian(node[394..], CompressFlags);
            }
        }

        for (var i = 0; i < Nid.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(node.Slice(F2fsNode.NidArrayOffset + i * 4, 4), Nid[i]);
        }
    }

    /// <summary>
    /// Reads the file content bytes from the inline area (for inline data and fast symlinks).
    /// <para>从 inline 区域读取文件内容字节（供 inline data 与快速符号链接使用）。</para>
    /// </summary>
    public byte[] ReadInlineBytes(ReadOnlySpan<byte> node, ulong size)
    {
        var offset = InlineDataOffset;
        var length = Math.Min((ulong)MaxInlineData, size);
        if (offset < 0 || offset > F2fsNode.InodeEndOffset || (long)length > F2fsNode.InodeEndOffset - offset)
        {
            throw new InvalidDataException("F2FS inline data exceeds the inode bounds.");
        }

        return node.Slice(offset, (int)length).ToArray();
    }

    private static byte[] TrimName(byte[] raw, uint namelen)
    {
        var length = (int)Math.Min(namelen, (uint)raw.Length);
        var result = new byte[length];
        raw.AsSpan(0, length).CopyTo(result);
        return result;
    }
}