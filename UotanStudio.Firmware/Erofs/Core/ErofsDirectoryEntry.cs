using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Firmware.Erofs;

/// <summary>
/// erofs 目录条目（compact_dirent，12 字节）及基于 nameoff 名称表的目录块解析。
/// <para>EROFS directory entry (compact_dirent, 12 bytes) and directory-block parsing based on the
/// nameoff name table.</para>
/// </summary>
internal sealed class ErofsDirectoryEntry
{
    /// <summary>dirent 固定大小。Fixed size of a dirent.</summary>
    public const int DirentSize = 12;

    /// <summary>dense 目录块末尾为长名称头部预留的 dirent 槽位数。Head slots reserved at the end of a dense directory block for long names.</summary>
    public const int DenseHeadSlots = 4;

    /// <summary>dense 目录中表示名称头部存储在预留槽位的 nameoff 高位标记。High nameoff bit marking a head stored in a reserved slot (dense).</summary>
    public const ushort NameOffHeadFlag = 0x8000;

    /// <summary>目录项 nid。Entry nid.</summary>
    public ulong Nid { get; set; }

    /// <summary>名称起始偏移（块内）。Start offset of the name within the block.</summary>
    public ushort NameOff { get; set; }

    /// <summary>文件类型。File type.</summary>
    public byte FileType { get; set; }

    /// <summary>保留字节。Reserved byte.</summary>
    public byte Reserved { get; set; }

    /// <summary>条目名称。Entry name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 从 12 字节解析单个 dirent（不含名称）。
    /// <para>Parses a single dirent (without the name) from 12 bytes.</para>
    /// </summary>
    public static ErofsDirectoryEntry FromBytes(ReadOnlySpan<byte> data)
    {
        if (data.Length < DirentSize)
        {
            throw new FormatException("Dirent region too short.");
        }

        return new ErofsDirectoryEntry
        {
            Nid = BinaryPrimitives.ReadUInt64LittleEndian(data),
            NameOff = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(8)),
            FileType = data[10],
            Reserved = data[11],
        };
    }

    /// <summary>
    /// 解析一个目录块（通常为 4K）。名称按 nameoff 表自块末尾倒排存储：
    /// 第 i 项名称占据 [nameoff[i], nameoff[i-1])，首个条目以块尾为界；dirent 自块首顺序排列。
    /// <para>Parses one directory block (usually 4K). Names are stored backwards from the block end
    /// via the nameoff table: entry i's name occupies [nameoff[i], nameoff[i-1]), the first entry
    /// being bounded by the block end; dirents are laid out from the block start in order.</para>
    /// </summary>
    public static List<ErofsDirectoryEntry> ParseBlock(ReadOnlySpan<byte> block)
    {
        var result = new List<ErofsDirectoryEntry>();
        var position = 0;
        var previousNameOff = block.Length;
        while (position + DirentSize <= block.Length)
        {
            var nameOff = BinaryPrimitives.ReadUInt16LittleEndian(block.Slice(position + 8, 2));
            if (nameOff == 0 || nameOff < position + DirentSize || nameOff >= previousNameOff)
            {
                break;
            }

            var name = Encoding.UTF8.GetString(block.Slice(nameOff, previousNameOff - nameOff));
            result.Add(new ErofsDirectoryEntry
            {
                Nid = BinaryPrimitives.ReadUInt64LittleEndian(block.Slice(position)),
                NameOff = nameOff,
                FileType = block[position + 10],
                Reserved = block[position + 11],
                Name = name,
            });

            previousNameOff = nameOff;
            position += DirentSize;
        }

        return result;
    }

    /// <summary>
    /// 解析一个 dense 目录块（尽力而为）。dense 与 compact 共用 nameoff 名称表结构，
    /// 本实现的构建器只生成 compact 目录块，因此 dense 块复用同一套解析逻辑；
    /// 长文件名头部（末尾预留槽位）的重建未实现。
    /// <para>Parses a dense directory block (best effort). Dense and compact blocks share the same
    /// nameoff name-table structure; this builder only emits compact blocks, so dense blocks reuse
    /// the same logic. Long-name head reconstruction from the trailing reserved slots is not
    /// implemented.</para>
    /// </summary>
    public static List<ErofsDirectoryEntry> ParseDenseBlock(ReadOnlySpan<byte> block) => ParseBlock(block);

    /// <summary>
    /// 按块大小迭代解析整个目录数据区（i_size 可能跨多个目录块）。
    /// <para>Parses a whole directory data area block by block (i_size may span several directory
    /// blocks).</para>
    /// </summary>
    public static List<ErofsDirectoryEntry> ParseDirectory(ReadOnlySpan<byte> data, int blockSize)
    {
        var result = new List<ErofsDirectoryEntry>();
        for (var offset = 0; offset + DirentSize <= data.Length; offset += blockSize)
        {
            var length = Math.Min(blockSize, data.Length - offset);
            result.AddRange(ParseBlock(data.Slice(offset, length)));
        }

        return result;
    }
}