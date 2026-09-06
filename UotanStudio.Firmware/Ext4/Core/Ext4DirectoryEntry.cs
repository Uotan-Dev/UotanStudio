using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Firmware.Ext4.Core;

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
    /// <para>枚举一个目录块内的所有目录项；遇到空尾部项（inode 为 0 且名称为 0） 或异常记录长度时停止。</para>
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


                break;
            }

            var availableNameBytes = block.Length - offset - 8;
            var actualNameLength = Math.Min(nameLength, Math.Max(availableNameBytes, 0));
            var name = Encoding.UTF8.GetString(block.Slice(offset + 8, actualNameLength));
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
