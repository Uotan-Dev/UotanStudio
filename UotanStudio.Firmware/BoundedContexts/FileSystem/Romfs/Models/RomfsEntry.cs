namespace UotanStudio.Firmware.BoundedContexts.FileSystem.Romfs.Models;

/// <summary>
/// RomFS entry categories decoded from the three type bits of the next-field mode nibble.
/// <para>RomFS 条目类别，由 next 字段低半字节中的三位类型位解码得到。</para>
/// </summary>
internal enum RomfsEntryKind
{
    /// <summary>Hard link pointing at another file header. <para>指向另一文件头的硬链接。</para></summary>
    HardLink,

    /// <summary>Directory. <para>目录。</para></summary>
    Directory,

    /// <summary>Regular file. <para>常规文件。</para></summary>
    RegularFile,

    /// <summary>Symbolic link whose data bytes are the target path. <para>数据字节即目标路径的符号链接。</para></summary>
    SymbolicLink,

    /// <summary>Block device; the spec field carries major/minor numbers. <para>块设备；spec 字段承载主/次设备号。</para></summary>
    BlockDevice,

    /// <summary>Character device; the spec field carries major/minor numbers. <para>字符设备；spec 字段承载主/次设备号。</para></summary>
    CharDevice,

    /// <summary>Socket placeholder. <para>套接字占位条目。</para></summary>
    Socket,

    /// <summary>FIFO placeholder. <para>FIFO 占位条目。</para></summary>
    Fifo,
}

/// <summary>
/// A decoded RomFS tree node: name, kind, data location and child list for directories.
/// <para>已解码的 RomFS 树节点：名称、类别、数据位置以及目录的子项列表。</para>
/// </summary>
internal sealed class RomfsEntry
{
    /// <summary>Entry category. <para>条目类别。</para></summary>
    internal RomfsEntryKind Kind { get; init; }

    /// <summary>Base name as stored in the image (empty for the root directory). <para>镜像中存储的基名（根目录为空）。</para></summary>
    internal required string Name { get; init; }

    /// <summary>Slash-separated path from the volume root; the root itself uses the empty string. <para>自卷根起的斜杠分隔路径；根节点为空字符串。</para></summary>
    internal required string Path { get; init; }

    /// <summary>Byte size of the entry data (0 for directories and hard links). <para>条目数据字节大小（目录与硬链接为 0）。</para></summary>
    internal long Size { get; init; }

    /// <summary>Absolute offset of the entry file header inside the volume. <para>条目文件头在卷内的绝对偏移。</para></summary>
    internal long HeaderOffset { get; init; }

    /// <summary>Absolute offset where the entry data begins; -1 when the entry carries no data region. <para>条目数据起始绝对偏移；无数据区时为 -1。</para></summary>
    internal long DataOffset { get; init; } = -1;

    /// <summary>For hard links, the header offset of the linked target. <para>硬链接所指向目标的文件头偏移。</para></summary>
    internal long LinkTargetOffset { get; init; } = -1;

    /// <summary>Packed major/minor device numbers for device entries. <para>设备条目打包的主/次设备号。</para></summary>
    internal (uint Major, uint Minor) Device { get; init; }

    /// <summary>Child entries in image order for directories; empty otherwise. <para>目录按镜像顺序排列的子项；其他类别为空。</para></summary>
    internal List<RomfsEntry> Children { get; } = [];
}