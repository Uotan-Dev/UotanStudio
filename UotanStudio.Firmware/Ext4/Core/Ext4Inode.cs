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
    /// High 32 bits of the file size (i_size_high); the kernel reads it unconditionally
    /// for regular files (the field occupies the historical i_dir_acl slot at offset 108).
    /// <para>文件大小高 32 位（i_size_high）；内核无条件将其用于普通文件
    /// （该字段占据偏移 108 处历史上的 i_dir_acl 槽位）。</para>
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
    /// Extended attribute block number (i_file_acl, absolute block address).
    /// Combines the low 32 bits (offset 140) and, for 256-byte inodes, the high 32 bits
    /// (i_file_acl_hi, offset 172) into a 64-bit value. When the 64BIT feature is not in
    /// use the high word is zero.
    /// <para>扩展属性块号（i_file_acl，绝对块地址）。合并低位 32 位（偏移 140）与
    /// 256 字节大 inode 的高 32 位（i_file_acl_hi，偏移 172）为 64 位值。
    /// 未启用 64BIT 特性时高位为 0。</para>
    /// </summary>
    public ulong FileAcl { get; init; }

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
    /// Whether the file content is inlined inside the inode.
    /// <para>文件内容是否内联于 inode 中。</para>
    /// </summary>
    public bool HasInlineData => (Flags & FlagInlineData) != 0;

    /// <summary>
    /// Returns the inline data capacity for this inode: i_block (60 bytes) + extra inode
    /// space after offset128 (only for regular files with EXT4_INLINE_DATA_FL).
    /// <para>返回此 inode 的内联数据容量：i_block（60字节）+ 偏移128之后的额外 inode 空间。</para>
    /// </summary>
    public int InlineDataCapacity(int inodeSize) => 60 + Math.Max(0, inodeSize - 128);

    /// <summary>
    /// Raw inline data bytes (i_block + extra inode space after offset 128).
    /// Only populated when <see cref="HasInlineData"/> is true.
    /// <para>内联数据原始字节（i_block + 偏移128后的额外 inode 空间），
    /// 仅在 <see cref="HasInlineData"/> 为 true 时填充。</para>
    /// </summary>
    public byte[] InlineData { get; init; } = [];

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
    /// filesystem inode size; i_size_high is read unconditionally (offset 108 lies within
    /// every inode size of at least 128 bytes, matching the kernel's ext4_isize()).
    /// <para>从原始字节解析 inode。<paramref name="inodeSize"/> 须与文件系统 inode 大小一致；
    /// i_size_high 无条件读取（偏移 108 位于任何不小于 128 字节的 inode 内，与内核 ext4_isize() 一致）。</para>
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

        var hasInline = (BinaryPrimitives.ReadUInt32LittleEndian(data[32..36]) & FlagInlineData) != 0;
        byte[] inlineData = [];
        if (hasInline)
        {
            // i_block 区域（60字节，偏移40）+ 额外 inode 空间（偏移128起）。
            var extraSize = Math.Max(0, inodeSize - 128);
            inlineData = new byte[60 + extraSize];
            data[40..100].CopyTo(inlineData);
            if (extraSize > 0)
            {
                data[128..inodeSize].CopyTo(inlineData.AsSpan(60));
            }
        }

        return new Ext4Inode
        {
            Mode = BinaryPrimitives.ReadUInt16LittleEndian(data[0..2]),
            UidLo = BinaryPrimitives.ReadUInt16LittleEndian(data[2..4]),
            SizeLo = BinaryPrimitives.ReadUInt32LittleEndian(data[4..8]),
            SizeHigh = BinaryPrimitives.ReadUInt32LittleEndian(data[108..112]),
            BlocksLo = BinaryPrimitives.ReadUInt32LittleEndian(data[28..32]),
            Flags = BinaryPrimitives.ReadUInt32LittleEndian(data[32..36]),
            BlockData = data[40..100].ToArray(),
            BlockPointers = pointers,
            LinksCount = BinaryPrimitives.ReadUInt16LittleEndian(data[26..28]),
            Mtime = BinaryPrimitives.ReadUInt32LittleEndian(data[16..20]),
            Ctime = BinaryPrimitives.ReadUInt32LittleEndian(data[12..16]),
            InlineData = inlineData,
            FileAcl = ReadFileAcl(data, inodeSize),
        };
    }

    /// <summary>
    /// Reads the extended attribute block number: low 32 bits at offset 140 and, when the
    /// inode is large enough (256-byte inode), the high 32 bits at offset 172.
    /// <para>读取扩展属性块号：偏移 140 处低位 32 位，当 inode 足够大（256 字节大 inode）
    /// 时还读偏移 172 处高位 32 位。</para>
    /// </summary>
    private static ulong ReadFileAcl(ReadOnlySpan<byte> data, int inodeSize)
    {
        // i_file_acl_lo @ offset 140 (u32 LE) only exists when the inode is larger than
        // 128 bytes (the legacy inode size). For a 128-byte inode there is no i_file_acl
        // field and external xattr blocks are not used.
        // i_file_acl_lo @ offset 140（u32 LE）仅当 inode 大于 128 字节时存在
        // （传统 inode 大小）。128 字节 inode 无 i_file_acl 字段，也不使用外部 xattr 块。
        if (inodeSize < 144 || data.Length < 144)
        {
            return 0;
        }

        var low = BinaryPrimitives.ReadUInt32LittleEndian(data[140..144]);
        ulong value = low;

        // i_file_acl_hi @ offset 172 (u32 LE) — only present in 256-byte (or larger) inodes.
        // Per the spec this field only holds meaningful data when the 64BIT feature is on;
        // without it the high word is zero, so reading it unconditionally is safe.
        // i_file_acl_hi @ offset 172（u32 LE）——仅在 256 字节或更大 inode 中存在。
        // 按规范该字段仅在启用 64BIT 特性时有意义；未启用时高位为 0，无条件读取是安全的。
        if (inodeSize >= 176 && data.Length >= 176)
        {
            var high = BinaryPrimitives.ReadUInt32LittleEndian(data[172..176]);
            value = ((ulong)high << 32) | low;
        }

        return value;
    }
}