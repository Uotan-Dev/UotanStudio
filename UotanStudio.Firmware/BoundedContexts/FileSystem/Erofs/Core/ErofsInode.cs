using System.Buffers.Binary;

namespace UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs;

internal sealed class ErofsInode
{
    public const int CompactSize = 32;

    public const int ExtendedSize = 64;


    public const ushort DataLayoutMask = 0x7 << 1;

    public const ushort DataLayoutFlat = 0;

    public const ushort DataLayoutCompressed = 1 << 1;

    public const ushort DataLayoutCompactIndexes = 3 << 1;

    public const ushort DataLayoutInline = 2 << 1;

    public const ushort DataLayoutChunkBased = 4 << 1;

    public const ushort FlagExtended = 1;

    private const ushort AdviseBitNlinkOneOrDotOmitted = 1 << 4;


    private const ushort ChunkFormatBlkBitsMask = 0x001F;

    private const ushort ChunkFormatIndexes = 0x0020;

    private const ushort ChunkFormat48Bit = 0x0040;

    public const ushort ModeTypeMask = 0xF000;

    public const ushort ModeDirectory = 0x4000;

    public const ushort ModeRegularFile = 0x8000;

    public const ushort ModeSymlink = 0xA000;

    public const ushort ModeCharacterDevice = 0x2000;

    public const ushort ModeBlockDevice = 0x6000;

    public const ushort ModeFifo = 0x1000;

    public const ushort ModeSocket = 0xC000;

    public const byte FileTypeUnknown = 0;

    public const byte FileTypeRegularFile = 1;

    public const byte FileTypeDirectory = 2;

    public const byte FileTypeCharacterDevice = 3;

    public const byte FileTypeBlockDevice = 4;

    public const byte FileTypeFifo = 5;

    public const byte FileTypeSocket = 6;

    public const byte FileTypeSymlink = 7;

    public ushort Advise { get; set; }

    public ushort XattrICount { get; set; }

    public ushort Mode { get; set; }

    public ulong Nlink { get; set; }

    public ulong Size { get; set; }

    public uint U { get; set; }

    public uint Ino { get; set; }

    public uint Uid { get; set; }

    public uint Gid { get; set; }

    public uint Generation { get; set; }

    public ulong CTime { get; set; }

    public uint CTimeNsec { get; set; }

    public bool IsExtended => (Advise & FlagExtended) != 0;

    public ushort DataLayout => (ushort)(Advise & DataLayoutMask);

    public bool IsDirectory => (Mode & ModeTypeMask) == ModeDirectory;

    public bool IsRegularFile => (Mode & ModeTypeMask) == ModeRegularFile;

    public bool IsSymlink => (Mode & ModeTypeMask) == ModeSymlink;

    public bool IsCharacterDevice => (Mode & ModeTypeMask) == ModeCharacterDevice;

    public bool IsBlockDevice => (Mode & ModeTypeMask) == ModeBlockDevice;

    public bool IsFifo => (Mode & ModeTypeMask) == ModeFifo;

    public bool IsSocket => (Mode & ModeTypeMask) == ModeSocket;

    public byte FileType
    {
        get
        {
            if (IsRegularFile) return FileTypeRegularFile;
            if (IsDirectory) return FileTypeDirectory;
            if (IsSymlink) return FileTypeSymlink;
            if (IsCharacterDevice) return FileTypeCharacterDevice;
            if (IsBlockDevice) return FileTypeBlockDevice;
            if (IsFifo) return FileTypeFifo;
            if (IsSocket) return FileTypeSocket;
            return FileTypeUnknown;
        }
    }

    public bool IsCompressed =>
        DataLayout == DataLayoutCompressed || DataLayout == DataLayoutCompactIndexes;

    public bool NlinkIsOne => (Advise & AdviseBitNlinkOneOrDotOmitted) != 0 && !IsDirectory;

    public bool DotOmitted => (Advise & AdviseBitNlinkOneOrDotOmitted) != 0 && IsDirectory;

    public ushort ChunkFormat => (ushort)U;

    public int ChunkFormatBlkBits => ChunkFormat & ChunkFormatBlkBitsMask;

    public bool UsesChunkIndexes => (ChunkFormat & ChunkFormatIndexes) != 0;

    public bool Chunk48Bit => (ChunkFormat & ChunkFormat48Bit) != 0;

    public int OnDiskSize => IsExtended ? ExtendedSize : CompactSize;

    public int XattrTotalSize => XattrICount == 0 ? 0 : 12 + 4 * (XattrICount - 1);

    public int InlineDataOffset => OnDiskSize + XattrTotalSize;

    public uint RawBlockAddress => U;

    public uint CompressedBlocks => U;

    /// <summary>
    /// Parses an inode from bytes; selects compact/extended layout from the extended flag in i_format.
    /// <para>从字节解析 inode；根据 i_format 的 extended 标志自动选择 compact/extended 布局。</para>
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

        var mode = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(4));
        var isDirectory = (mode & ModeTypeMask) == ModeDirectory;
        var nb = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(6));
        var nlinkIsOne = (advise & AdviseBitNlinkOneOrDotOmitted) != 0 && !isDirectory;

        return new ErofsInode
        {
            Advise = advise,
            XattrICount = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(2)),
            Mode = mode,


            Nlink = nlinkIsOne ? 1u : nb,
            Size = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(8)),
            Generation = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(12)),
            U = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(16)),
            Ino = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(20)),
            Uid = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(24)),
            Gid = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(26)),
        };
    }

    /// <summary>
    /// Serializes the inode into a buffer of at least OnDiskSize bytes.
    /// <para>将 inode 序列化到缓冲区（长度须至少为 OnDiskSize）。</para>
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
