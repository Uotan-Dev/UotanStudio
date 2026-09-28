namespace UotanStudio.Firmware.FileSystem.Squashfs;

/// <summary>
/// Logical SquashFS inode categories; the extended (4.1) variants collapse onto the same kind.
/// <para>SquashFS inode 的逻辑类别；扩展（4.1）变体归并到同一类别。</para>
/// </summary>
internal enum SquashfsEntryKind
{
    /// <summary>Directory. <para>目录。</para></summary>
    Directory,

    /// <summary>Regular file. <para>常规文件。</para></summary>
    RegularFile,

    /// <summary>Symbolic link. <para>符号链接。</para></summary>
    SymbolicLink,

    /// <summary>Block device. <para>块设备。</para></summary>
    BlockDevice,

    /// <summary>Character device. <para>字符设备。</para></summary>
    CharDevice,

    /// <summary>FIFO. <para>FIFO。</para></summary>
    Fifo,

    /// <summary>Socket. <para>套接字。</para></summary>
    Socket,
}

/// <summary>
/// Decoded SquashFS inode including the data block layout needed for extraction.
/// <para>已解码的 SquashFS inode，包含解包所需的数据块布局。</para>
/// </summary>
internal sealed class SquashfsInode
{
    internal SquashfsEntryKind Kind { get; init; }
    internal ushort RawType { get; init; }
    internal ushort Mode { get; init; }
    internal ushort UidIndex { get; init; }
    internal ushort GuidIndex { get; init; }
    internal uint ModifiedTime { get; init; }
    internal uint InodeNumber { get; init; }
    internal long XattrIndex { get; init; } = -1;

    /// <summary>Absolute image offset of the first data block; unused when every block is fragment-backed.
    /// <para>首个数据块的绝对镜像偏移；所有块由 fragment 承载时未使用。</para></summary>
    internal ulong DataStart { get; init; }

    /// <summary>Compressed block sizes with the version 4 non-compression bit retained.
    /// <para>保留版本 4 “未压缩”标志位的压缩块大小序列。</para></summary>
    internal uint[] BlockSizes { get; init; } = [];

    internal long Size { get; init; }
    internal int FragmentIndex { get; init; } = -1;
    internal int FragmentOffset { get; init; }
    internal bool Sparse { get; init; }

    internal long DirectoryBlock { get; init; }
    internal int DirectoryOffset { get; init; }
    internal long DirectorySize { get; init; }
    internal uint ParentInode { get; init; }

    internal byte[]? Target { get; init; }
    internal uint DeviceNumber { get; init; }
}

/// <summary>
/// A walked filesystem entry: image path, inode kind, reported size and the backing inode.
/// <para>已遍历出的文件系统条目：镜像路径、inode 类别、上报大小及所属 inode。</para>
/// </summary>
internal sealed class SquashfsEntry
{
    internal required string Path { get; init; }
    internal required SquashfsInode Inode { get; init; }
    internal SquashfsEntryKind Kind => Inode.Kind;
    internal long Size => Inode.Size;
}