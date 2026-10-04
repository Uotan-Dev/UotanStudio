namespace UotanStudio.Firmware.CompoundFile;

/// <summary>
/// Object type of a directory entry inside a compound file (OLE2/CFB) container.
/// <para>复合文档（OLE2/CFB）容器内目录项的对象类型。</para>
/// </summary>
public enum CompoundFileEntryType
{
    /// <summary>
    /// A stream holding contiguous user data.
    /// <para>承载连续用户数据的流。</para>
    /// </summary>
    Stream,

    /// <summary>
    /// A storage (virtual folder) that can contain other entries.
    /// <para>可包含其他条目的存储（虚拟文件夹）。</para>
    /// </summary>
    Storage,

    /// <summary>
    /// The root entry; its stream region is the mini-stream container.
    /// <para>根条目；其流区域即 mini stream 容器。</para>
    /// </summary>
    Root
}

/// <summary>
/// One directory entry of a compound file (OLE2/CFB) container, addressed by its slash-separated
/// path from the root entry (<see cref="FullPath"/> is empty for the root itself).
/// <para>复合文档（OLE2/CFB）容器的一个目录项，以从根条目开始的斜杠分隔路径定位（根条目自身的
/// <see cref="FullPath"/> 为空字符串）。</para>
/// </summary>
public sealed class CompoundFileEntry
{
    /// <summary>
    /// Directory entry index inside the directory chain.
    /// <para>目录链内的目录项索引。</para>
    /// </summary>
    /// <value>
    /// Zero-based directory entry index.
    /// <para>从零开始的目录项索引。</para>
    /// </value>
    public int Id { get; }

    /// <summary>
    /// Entry name without any path components.
    /// <para>不含路径成分的条目名。</para>
    /// </summary>
    /// <value>
    /// The decoded UTF-16 name of this entry.
    /// <para>该条目解码后的 UTF-16 名称。</para>
    /// </value>
    public string Name { get; }

    /// <summary>
    /// Slash-separated path from the root entry; empty for the root itself.
    /// <para>从根条目开始的斜杠分隔路径；根条目自身为空字符串。</para>
    /// </summary>
    /// <value>
    /// The full path of this entry, e.g. <c>00000000/default/NV_ITEM_ARRAY</c>.
    /// <para>该条目的完整路径，例如 <c>00000000/default/NV_ITEM_ARRAY</c>。</para>
    /// </value>
    public string FullPath { get; }

    /// <summary>
    /// Object type of this entry.
    /// <para>该条目的对象类型。</para>
    /// </summary>
    /// <value>
    /// One of <see cref="CompoundFileEntryType.Stream"/>, <see cref="CompoundFileEntryType.Storage"/>
    /// or <see cref="CompoundFileEntryType.Root"/>.
    /// <para>为 <see cref="CompoundFileEntryType.Stream"/>、<see cref="CompoundFileEntryType.Storage"/>
    /// 或 <see cref="CompoundFileEntryType.Root"/> 之一。</para>
    /// </value>
    public CompoundFileEntryType Type { get; }

    /// <summary>
    /// Declared stream size in bytes; storages and the root report their mini-stream container size.
    /// <para>声明的流大小（字节）；存储与根条目报告其 mini stream 容器大小。</para>
    /// </summary>
    /// <value>
    /// Stream size in bytes.
    /// <para>流大小（字节）。</para>
    /// </value>
    public long StreamSize { get; }

    /// <summary>
    /// First sector of the stream chain (regular FAT chain for streams at or above the mini-stream
    /// cutoff and for the root; mini FAT chain index otherwise).
    /// <para>流链的首扇区（达到 mini stream 阈值的流与根条目为常规 FAT 链，其余为 mini FAT 链索引）。</para>
    /// </summary>
    /// <value>
    /// Raw start-sector field of the directory entry.
    /// <para>目录项的起始扇区原始字段值。</para>
    /// </value>
    public uint StartSector { get; }

    internal CompoundFileEntry(int id, string name, string fullPath, CompoundFileEntryType type, long streamSize, uint startSector)
    {
        Id = id;
        Name = name;
        FullPath = fullPath;
        Type = type;
        StreamSize = streamSize;
        StartSector = startSector;
    }
}