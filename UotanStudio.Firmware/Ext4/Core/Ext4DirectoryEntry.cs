using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Firmware.Ext4.Core;

/// <summary>
/// ext4_dir_entry_2 directory entry used for directory traversal.
/// <para>用于目录遍历的 ext4_dir_entry_2 目录项。</para>
/// </summary>
internal readonly record struct Ext4DirectoryEntry
{
    /// <summary>
    /// file_type value for a regular file.
    /// <para>普通文件的 file_type 值。</para>
    /// </summary>
    public const byte TypeRegular = 1;

    /// <summary>
    /// file_type value for a directory.
    /// <para>目录的 file_type 值。</para>
    /// </summary>
    public const byte TypeDirectory = 2;

    /// <summary>
    /// file_type value for a symbolic link.
    /// <para>符号链接的 file_type 值。</para>
    /// </summary>
    public const byte TypeSymlink = 7;

    /// <summary>
    /// Inode number referenced by this entry.
    /// <para>该目录项引用的 inode 号。</para>
    /// </summary>
    public uint Inode { get; init; }

    /// <summary>
    /// Entry record length in bytes (distance to the next entry).
    /// <para>目录项记录长度（到下一目录项的字节距离）。</para>
    /// </summary>
    public ushort RecordLength { get; init; }

    /// <summary>
    /// Name length in bytes (no NUL terminator stored).
    /// <para>名称字节长度（不存储 NUL 终止符）。</para>
    /// </summary>
    public byte NameLength { get; init; }

    /// <summary>
    /// File type hint (0 = read from the inode instead).
    /// <para>文件类型提示（0 表示改用 inode 判断）。</para>
    /// </summary>
    public byte FileType { get; init; }

    /// <summary>
    /// Entry name (UTF-8 decoded).
    /// <para>目录项名称（UTF-8 解码）。</para>
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Explicit parameterless constructor: required by CS8983 (a struct with field initializers
    /// must declare an explicit constructor) and provides the default empty-entry values.
    /// <para>显式无参构造函数：满足 CS8983（带字段初始值设定项的结构必须含显式构造函数），
    /// 同时提供空目录项的默认值。</para>
    /// </summary>
    public Ext4DirectoryEntry()
    {
        Inode = 0;
        RecordLength = 0;
        NameLength = 0;
        FileType = 0;
        Name = string.Empty;
    }

    /// <summary>
    /// Enumerates the directory entries inside one directory block.
    /// Stops at an empty tail entry (inode 0 and name length 0) or malformed record lengths.
    /// <para>枚举一个目录块内的所有目录项；遇到空尾部项（inode 为 0 且名称为 0）
    /// 或异常记录长度时停止。</para>
    /// </summary>
    public static IReadOnlyList<Ext4DirectoryEntry> Enumerate(ReadOnlySpan<byte> block)
    {
        var result = new List<Ext4DirectoryEntry>();
        var offset = 0;
        while (offset + 8 <= block.Length)
        {
            var inode = BinaryPrimitives.ReadUInt32LittleEndian(block[offset..(offset + 4)]);
            var recordLength = BinaryPrimitives.ReadUInt16LittleEndian(block[(offset + 4)..(offset + 6)]);
            var nameLength = block[offset + 6];
            var fileType = block[offset + 7];
            if (recordLength < 8)
            {
                // Malformed or zero-length padding; cannot advance reliably.
                // 异常或零长度填充，无法可靠前进。
                break;
            }

            var availableNameBytes = block.Length - offset - 8;
            var actualNameLength = Math.Min(nameLength, Math.Max(availableNameBytes, 0));
            var name = Encoding.UTF8.GetString(block.Slice(offset + 8, actualNameLength));
            if (nameLength == 0)
            {
                // Empty entry consuming the rest of the block.
                // 空目录项，占据块内剩余空间。
                break;
            }

            if (inode != 0)
            {
                result.Add(new Ext4DirectoryEntry
                {
                    Inode = inode,
                    RecordLength = recordLength,
                    NameLength = nameLength,
                    FileType = fileType,
                    Name = name,
                });
            }

            offset += recordLength;
        }

        return result;
    }
}