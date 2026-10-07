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
    /// Enumerates the directory entries inside one directory block.
    /// The name lives inside its own record: upstream <c>struct ext4_dir_entry_2</c> is inode, rec_len, name_len, file_type and then the name bytes, so a record can hold at most <c>rec_len - 8</c> name bytes. A record declaring more than that is corrupt, and enumerating it would fold the following records into one name and then skip them, presenting a name the image never stored; therefore the walk stops at such a record and neither it nor any later record in the block is listed. Stopping is safe for untrusted input because the records kept are byte-verified and a damaged directory cannot be repaired from the block alone.
    /// <para>枚举一个目录块内的所有目录项。名称位于自身记录之内：上游 <c>struct ext4_dir_entry_2</c> 依次为 inode、rec_len、name_len、file_type 再跟名称字节，因此一条记录最多容纳 <c>rec_len - 8</c> 字节的名称。声明更多名称字节即为损坏记录，继续解析会把后续记录折进一个名称再整体跳过，呈现出镜像里并不存在的名称；因此扫描到此停止，本条与其后的记录都不列出。对不可信输入而言停止而非猜测是安全的：已保留的记录都经过字节核对，而损坏目录无法只靠本块修复。</para>
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

            var name = UotanStudio.Firmware.FileSystem.Extraction.PortableExtractionPaths.NameEncoding.GetString(block.Slice(offset + 8, nameLength));
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