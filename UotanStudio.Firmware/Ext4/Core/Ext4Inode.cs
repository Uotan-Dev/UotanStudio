using System.Buffers.Binary;

namespace UotanStudio.Firmware.Ext4.Core;

/// <summary>
/// ext4 inode structure, parsed in little-endian order. Provides the fields needed
/// for file/directory/symlink traversal and block mapping.
/// <para>ext4 inode 结构，按小端解析，提供文件/目录/符号链接遍历与块映射所需的字段。</para>
/// </summary>
internal sealed record Ext4Inode
{
    private const ushort ModeFormatMask = 0xF000;
    private const ushort ModeRegularFile = 0x8000;
    private const ushort ModeDirectory = 0x4000;
    private const ushort ModeSymbolicLink = 0xA000;

    /// <summary>
    /// EXT4_EXTENTS_FL flag: block pointers are an extent tree.
    /// <para>EXT4_EXTENTS_FL 标志：块指针为 extent 树。</para>
    /// </summary>
    private const uint FlagExtents = 0x0008_0000;

    /// <summary>
    /// EXT4_INLINE_DATA_FL flag: file content lives inside the inode.
    /// <para>EXT4_INLINE_DATA_FL 标志：文件内容内联于 inode 中。</para>
    /// </summary>
    private const uint FlagInlineData = 0x1000_0000;

    /// <summary>
    /// File mode (i_mode).
    /// <para>文件模式（i_mode）。</para>
    /// </summary>
    public ushort Mode { get; init; }

    /// <summary>
    /// Owner user id (i_uid_lo).
    /// <para>属主用户 id（i_uid_lo）。</para>
    /// </summary>
    public ushort UidLo { get; init; }

    /// <summary>
    /// Low 32 bits of the file size (i_size_lo).
    /// <para>文件大小低 32 位（i_size_lo）。</para>
    /// </summary>
    public uint SizeLo { get; init; }

    /// <summary>
    /// High 32 bits of the file size (i_size_high); only valid for large inodes.
    /// <para>文件大小高 32 位（i_size_high）；仅对大 inode 有效。</para>
    /// </summary>
    public uint SizeHigh { get; init; }

    /// <summary>
    /// Number of 512-byte sectors allocated (i_blocks_lo).
    /// <para>已分配的 512 字节扇区数（i_blocks_lo）。</para>
    /// </summary>
    public uint BlocksLo { get; init; }

    /// <summary>
    /// Inode flags (i_flags).
    /// <para>inode 标志（i_flags）。</para>
    /// </summary>
    public uint Flags { get; init; }

    /// <summary>
    /// Raw 60 bytes of i_block[15], holding the extent tree or fast symlink target.
    /// <para>i_block[15] 的原始 60 字节，承载 extent 树或快速符号链接目标。</para>
    /// </summary>
    public byte[] BlockData { get; init; } = [];

    /// <summary>
    /// i_block[15] interpreted as 32-bit block pointers (used by the indirect scheme).
    /// <para>将 i_block[15] 解释为 32 位块指针（用于间接映射方案）。</para>
    /// </summary>
    public uint[] BlockPointers { get; init; } = [];

    /// <summary>
    /// Link count (i_links_count).
    /// <para>链接计数（i_links_count）。</para>
    /// </summary>
    public ushort LinksCount { get; init; }

    /// <summary>
    /// Modification time in Unix seconds (i_mtime).
    /// <para>修改时间（Unix 秒，i_mtime）。</para>
    /// </summary>
    public uint Mtime { get; init; }

    /// <summary>
    /// Creation/inode-change time in Unix seconds (i_ctime).
    /// <para>创建/inode 变更时间（Unix 秒，i_ctime）。</para>
    /// </summary>
    public uint Ctime { get; init; }

    /// <summary>
    /// Computed file size combining the high and low words.
    /// <para>合并高低位后的文件大小。</para>
    /// </summary>
    public long Size => ((long)SizeHigh << 32) | SizeLo;

    /// <summary>
    /// Whether the inode uses the extent mapping scheme.
    /// <para>该 inode 是否使用 extent 映射方案。</para>
    /// </summary>
    public bool HasExtents => (Flags & FlagExtents) != 0;

    /// <summary>
    /// Whether the file content is inlined inside the inode (unsupported for reading).
    /// <para>文件内容是否内联于 inode 中（不支持读取）。</para>
    /// </summary>
    public bool HasInlineData => (Flags & FlagInlineData) != 0;

    /// <summary>
    /// Whether this is a regular file.
    /// <para>是否为普通文件。</para>
    /// </summary>
    public bool IsRegularFile => (Mode & ModeFormatMask) == ModeRegularFile;

    /// <summary>
    /// Whether this is a directory.
    /// <para>是否为目录。</para>
    /// </summary>
    public bool IsDirectory => (Mode & ModeFormatMask) == ModeDirectory;

    /// <summary>
    /// Whether this is a symbolic link.
    /// <para>是否为符号链接。</para>
    /// </summary>
    public bool IsSymlink => (Mode & ModeFormatMask) == ModeSymbolicLink;

    /// <summary>
    /// Parses an inode from raw bytes. <paramref name="inodeSize"/> must match the
    /// filesystem inode size; i_size_high is read only for inode sizes of 256 bytes or more.
    /// <para>从原始字节解析 inode。<paramref name="inodeSize"/> 须与文件系统 inode 大小一致；
    /// 仅当 inode 大小大于等于 256 字节时读取 i_size_high。</para>
    /// </summary>
    public static Ext4Inode FromBytes(ReadOnlySpan<byte> data, int inodeSize)
    {
        if (inodeSize < 128 || data.Length < inodeSize)
        {
            throw new InvalidDataException($"Inode is truncated (need {inodeSize} bytes, got {data.Length}).");
        }

        var pointers = new uint[15];
        for (var i = 0; i < 15; i++)
        {
            pointers[i] = BinaryPrimitives.ReadUInt32LittleEndian(data[(40 + i * 4)..(44 + i * 4)]);
        }

        return new Ext4Inode
        {
            Mode = BinaryPrimitives.ReadUInt16LittleEndian(data[0..2]),
            UidLo = BinaryPrimitives.ReadUInt16LittleEndian(data[2..4]),
            SizeLo = BinaryPrimitives.ReadUInt32LittleEndian(data[4..8]),
            SizeHigh = inodeSize >= 256 ? BinaryPrimitives.ReadUInt32LittleEndian(data[108..112]) : 0,
            BlocksLo = BinaryPrimitives.ReadUInt32LittleEndian(data[28..32]),
            Flags = BinaryPrimitives.ReadUInt32LittleEndian(data[32..36]),
            BlockData = data[40..100].ToArray(),
            BlockPointers = pointers,
            LinksCount = BinaryPrimitives.ReadUInt16LittleEndian(data[26..28]),
            Mtime = BinaryPrimitives.ReadUInt32LittleEndian(data[16..20]),
            Ctime = BinaryPrimitives.ReadUInt32LittleEndian(data[12..16]),
        };
    }
}