using System.Buffers.Binary;

namespace UotanStudio.Firmware.FileSystem.Ext4.Models;

internal readonly record struct Ext4DirectoryEntry
{
    public const byte TypeRegular = 1;

    public const byte TypeDirectory = 2;

    public const byte TypeSymlink = 7;

    public uint Inode { get; init; }

    public ushort RecordLength { get; init; }

    public byte NameLength { get; init; }

    public byte FileType { get; init; }

    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Explicit parameterless constructor: required by CS8983 (a struct with field initializers must declare an explicit constructor) and provides the default empty-entry values.
    /// <para>显式无参构造函数：满足 CS8983（带字段初始值设定项的结构必须含显式构造函数）， 同时提供空目录项的默认值。</para>
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
    /// Enumerates the directory entries inside one directory block, stopping at the first record that declares more name bytes than <c>rec_len - 8</c> allows rather than folding later records into one name.
    /// <para>枚举一个目录块内的所有目录项；遇到声明的名称字节超过 <c>rec_len - 8</c> 上限的记录时停止扫描，而不是把后续记录折进一个名称。</para>
    /// </summary>
    /// <param name="block">Bytes of one directory block; a record needs 8 header bytes plus its name, and the name is read only within the smaller of its own record and the space left in the block, so no read leaves the array.
    /// <para>单个目录块的字节；每条记录至少需要 8 字节头加上名称，名称只在“自身记录容量”与“块剩余空间”两者的较小者之内读取，任何读取都不会越出数组。</para></param>
    /// <returns>Records with a non-zero inode in block order, ending at the first record that declares a record length below 8, declares a name its record or the block cannot hold, or carries an empty name such as the dir_entry tail and the dx_node fake entry; returns empty when the block has no full header.
    /// <para>按块内顺序返回 inode 非零的记录；遇到声明记录长度小于 8、声明的名称超出自身记录或块剩余空间、或名称为空（例如 dir_entry 尾部与 dx_node 伪目录项）的记录时结束；块内不足一个完整头时返回空列表。</para></returns>
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
                break;
            }

            var nameCapacity = Math.Min(recordLength - 8, block.Length - offset - 8);
            if (nameLength > nameCapacity)
            {
                break;
            }

            var name = Extraction.PortableExtractionPaths.NameEncoding.GetString(block.Slice(offset + 8, nameLength));
            if (nameLength == 0)
            {
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