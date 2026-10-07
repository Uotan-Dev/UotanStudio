using System.Buffers.Binary;
using UotanStudio.Firmware.FileSystem.Metadata;

namespace UotanStudio.Firmware.FileSystem.Ext4.Models;

internal sealed record Ext4Inode
{
    private const uint FlagExtents = 0x0008_0000;

    private const uint FlagInlineData = 0x1000_0000;

    public ushort Mode { get; init; }

    public ushort UidLo { get; init; }

    public ushort GidLo { get; init; }
    public ushort UidHigh { get; init; }
    public ushort GidHigh { get; init; }
    public uint Uid => UidLo | ((uint)UidHigh << 16);
    public uint Gid => GidLo | ((uint)GidHigh << 16);

    public uint SizeLo { get; init; }

    public uint SizeHigh { get; init; }

    public uint BlocksLo { get; init; }

    public uint Flags { get; init; }

    public byte[] BlockData { get; init; } = [];

    public uint[] BlockPointers { get; init; } = [];

    public ushort LinksCount { get; init; }

    public uint Mtime { get; init; }

    public uint Ctime { get; init; }

    public FileSystemInodeMetadata Metadata { get; init; } = new();

    public ulong FileAcl { get; init; }

    public long Size => ((long)SizeHigh << 32) | SizeLo;

    public bool HasExtents => (Flags & FlagExtents) != 0;

    public bool HasInlineData => (Flags & FlagInlineData) != 0;

    /// <summary>
    /// Returns the inline payload capacity including the system.data value.
    /// <para>返回包含 system.data 属性值的内联载荷容量。</para>
    /// </summary>
    /// <param name="inodeSize">Filesystem inode size in bytes; the result is taken from the parsed <see cref="InlineData"/> payload, so this value does not alter it.
    /// <para>文件系统 inode 大小（字节）；结果取自解析得到的 <see cref="InlineData"/> 载荷，因此该值不会改变结果。</para></param>
    /// <returns>Bytes of inline payload: the 60-byte i_block area plus the <c>system.data</c> xattr value, and 0 for an inode parsed without the inline-data flag.
    /// <para>内联载荷字节数：60 字节 i_block 区加上 <c>system.data</c> 扩展属性值；解析时未设置内联数据标志的 inode 为 0。</para></returns>
    public int InlineDataCapacity(int inodeSize) => InlineData.Length;

    public byte[] InlineData { get; init; } = [];

    public bool IsRegularFile => (Mode & InodeTypeBits.FormatMask) == InodeTypeBits.RegularFile;

    public bool IsDirectory => (Mode & InodeTypeBits.FormatMask) == InodeTypeBits.Directory;

    public bool IsSymlink => (Mode & InodeTypeBits.FormatMask) == InodeTypeBits.SymbolicLink;

    /// <summary>
    /// Parses an inode from raw bytes. inodeSize must match the filesystem inode size; i_size_high is read unconditionally (offset 108 lies within every inode size of at least 128 bytes, matching the kernel's ext4_isize()).
    /// <para>从原始字节解析 inode。</para>
    /// </summary>
    /// <param name="data">Inode record bytes read little-endian, holding the mode at 0, size at 4, flags at 32, the fifteen block pointers at 40 to 100 and the extra fields from 128 onward.
    /// <para>按小端读取的 inode 记录字节：0 处是模式，4 处是大小，32 处是标志，40 至 100 是十五个块指针，128 之后是扩展字段。</para></param>
    /// <param name="inodeSize">Filesystem inode size taken from the superblock, at least 128; a smaller value, or fewer bytes than it in <paramref name="data"/>, is reported as a truncated inode, and it also bounds the extra-field window.
    /// <para>取自超级块的文件系统 inode 大小，至少 128；该值过小、<paramref name="data"/> 字节不足都会被视为 inode 截断，同时它也限定扩展字段的可读范围。</para></param>
    /// <returns>Inode carrying the combined 64-bit size and uid/gid, the pointer array, the raw i_block bytes, inline payload when the inline flag is set, the decoded timestamps and the file ACL.
    /// <para>返回的 inode 含合并后的 64 位大小与 uid/gid、块指针数组、原始 i_block 字节、设置了内联标志时的内联载荷、解码出的时间戳以及文件 ACL。</para></returns>
    public static Ext4Inode FromBytes(ReadOnlySpan<byte> data, int inodeSize)
    {
        if (inodeSize < 128 || data.Length < inodeSize)
        {
            throw new FileSystemCorruptedException($"Inode is truncated (need {inodeSize} bytes, got {data.Length}).");
        }

        var pointers = new uint[15];
        for (var i = 0; i < 15; i++)
        {
            pointers[i] = BinaryPrimitives.ReadUInt32LittleEndian(data[(40 + i * 4)..(44 + i * 4)]);
        }

        var hasInline = (BinaryPrimitives.ReadUInt32LittleEndian(data[32..36]) & FlagInlineData) != 0;
        byte[] inlineData = [];
        if (hasInline)
        {

            var value = FindInlineDataValue(data[..inodeSize]);
            inlineData = new byte[60 + value.Length];
            data[40..100].CopyTo(inlineData);
            value.CopyTo(inlineData.AsSpan(60));
            var mode = BinaryPrimitives.ReadUInt16LittleEndian(data);
            var fileSize = ((ulong)BinaryPrimitives.ReadUInt32LittleEndian(data[108..]) << 32) | BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
            if ((mode & InodeTypeBits.FormatMask) != InodeTypeBits.Directory && fileSize > (ulong)inlineData.Length)
                throw new FileSystemCorruptedException("EXT4 inline file size exceeds i_block and system.data.");
        }

        return new Ext4Inode
        {
            Mode = BinaryPrimitives.ReadUInt16LittleEndian(data[0..2]),
            UidLo = BinaryPrimitives.ReadUInt16LittleEndian(data[2..4]),
            GidLo = BinaryPrimitives.ReadUInt16LittleEndian(data[24..26]),
            UidHigh = BinaryPrimitives.ReadUInt16LittleEndian(data[120..122]),
            GidHigh = BinaryPrimitives.ReadUInt16LittleEndian(data[122..124]),
            SizeLo = BinaryPrimitives.ReadUInt32LittleEndian(data[4..8]),
            SizeHigh = BinaryPrimitives.ReadUInt32LittleEndian(data[108..112]),
            BlocksLo = BinaryPrimitives.ReadUInt32LittleEndian(data[28..32]),
            Flags = BinaryPrimitives.ReadUInt32LittleEndian(data[32..36]),
            BlockData = data[40..100].ToArray(),
            BlockPointers = pointers,
            LinksCount = BinaryPrimitives.ReadUInt16LittleEndian(data[26..28]),
            Mtime = BinaryPrimitives.ReadUInt32LittleEndian(data[16..20]),
            Ctime = BinaryPrimitives.ReadUInt32LittleEndian(data[12..16]),
            Metadata = ReadMetadata(data[..inodeSize]),
            InlineData = inlineData,
            FileAcl = ReadFileAcl(data, inodeSize),
        };
    }

    private static FileSystemInodeMetadata ReadMetadata(ReadOnlySpan<byte> data)
    {
        var extra = data.Length > 128 ? BinaryPrimitives.ReadUInt16LittleEndian(data[128..]) : 0;
        if (extra > data.Length - 128)
        {
            throw new FileSystemCorruptedException("EXT4 extra inode fields exceed the inode size.");
        }
        var end = 128 + extra;
        var metadata = new FileSystemInodeMetadata
        {
            AccessTime = ReadTimestamp(data, 8, 140, end),
            ChangeTime = ReadTimestamp(data, 12, 132, end),
            ModificationTime = ReadTimestamp(data, 16, 136, end),
            BirthTime = end >= 148 ? ReadTimestamp(data, 144, 148, end) : null,
            Generation = BinaryPrimitives.ReadUInt32LittleEndian(data[100..]),
            ProjectId = end >= 160 ? BinaryPrimitives.ReadUInt32LittleEndian(data[156..]) : null,
            Version = BinaryPrimitives.ReadUInt32LittleEndian(data[36..])
                | (end >= 156 ? (ulong)BinaryPrimitives.ReadUInt32LittleEndian(data[152..]) << 32 : 0),
            LinkCount = BinaryPrimitives.ReadUInt16LittleEndian(data[26..])
        };
        metadata.Validate();
        return metadata;
    }

    private static FileSystemTimestamp ReadTimestamp(ReadOnlySpan<byte> data, int offset, int extraOffset, int end)
    {
        var extra = end >= extraOffset + 4 ? BinaryPrimitives.ReadUInt32LittleEndian(data[extraOffset..]) : 0u;
        return new FileSystemTimestamp(BinaryPrimitives.ReadInt32LittleEndian(data[offset..]) + ((long)(extra & 3) << 32), extra >> 2);
    }

    private static ReadOnlySpan<byte> FindInlineDataValue(ReadOnlySpan<byte> inode)
    {
        if (inode.Length < 132) throw new FileSystemCorruptedException("EXT4 inline data requires inode xattrs.");
        var extra = BinaryPrimitives.ReadUInt16LittleEndian(inode[128..]);
        var header = 128 + extra;
        if (header > inode.Length - 4 || BinaryPrimitives.ReadUInt32LittleEndian(inode[header..]) != Ext4Xattr.Magic)
            throw new FileSystemCorruptedException("EXT4 inline data has no inode xattr header.");
        var first = header + 4;
        for (var pos = first; pos <= inode.Length - 16;)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(inode[pos..]) == 0) break;
            var nameLength = inode[pos];
            var entrySize = (16 + nameLength + 3) & ~3;
            if (entrySize > inode.Length - pos) throw new FileSystemCorruptedException("Truncated EXT4 inline xattr entry.");
            if (inode[pos + 1] == 7 && inode.Slice(pos + 16, nameLength).SequenceEqual("data"u8))
            {
                var valueOffset = first + BinaryPrimitives.ReadUInt16LittleEndian(inode[(pos + 2)..]);
                var valueInode = BinaryPrimitives.ReadUInt32LittleEndian(inode[(pos + 4)..]);
                var valueSize = BinaryPrimitives.ReadUInt32LittleEndian(inode[(pos + 8)..]);
                if (valueInode != 0 || valueOffset > inode.Length || valueSize > inode.Length - valueOffset)
                    throw new FileSystemCorruptedException("EXT4 system.data exceeds the inode bounds.");
                return inode.Slice(valueOffset, (int)valueSize);
            }
            pos += entrySize;
        }
        throw new FileSystemCorruptedException("EXT4 inline data has no system.data xattr.");
    }

    /// <summary>
    /// Reads the extended attribute block number per the upstream layout: low 32 bits at offset 104 (i_file_acl_lo) plus the 16-bit high word at offset 118 (l_i_file_acl_high), both inside the base 128-byte inode.
    /// <para>按上游布局读取扩展属性块号：偏移 104 处低 32 位（i_file_acl_lo）加偏移 118 处 16 位高字（l_i_file_acl_high），两者都在基础 128 字节 inode 内。</para>
    /// </summary>
    private static ulong ReadFileAcl(ReadOnlySpan<byte> data, int inodeSize)
    {


        if (inodeSize < 128 || data.Length < 120)
        {
            return 0;
        }

        var low = BinaryPrimitives.ReadUInt32LittleEndian(data[104..108]);
        var high = BinaryPrimitives.ReadUInt16LittleEndian(data[118..120]);
        return ((ulong)high << 32) | low;
    }
}