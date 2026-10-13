namespace UotanStudio.Firmware.FileSystem.Erofs;

/// <summary>Selects the inode record width the builder emits for a file or directory.
/// <para>选择打包器为文件或目录写出的 inode 记录宽度。</para></summary>
public enum ErofsInodeFormat
{
    /// <summary>Let the builder pick the width from the metadata that must be stored.
    /// <para>由打包器按必须存放的元数据选择宽度。</para></summary>
    Auto = 0,

    /// <summary>Always emit the 32-byte compact record, failing loudly when the metadata does not fit one
    /// (a wide uid or gid, an extended timestamp, or a large file size).
    /// <para>始终写出 32 字节紧凑记录；元数据装不下时显式报错（宽 uid/gid、扩展时间戳或过大的文件大小）。</para></summary>
    Compact = 1,

    /// <summary>Always emit the 64-byte extended record.
    /// <para>始终写出 64 字节扩展记录。</para></summary>
    Extended = 2,
}

/// <summary>Selects the chunk index layout the builder emits for a chunk-based file.
/// <para>选择打包器为 chunk-based 文件写出的 chunk 索引布局。</para></summary>
public enum ErofsChunkIndexKind
{
    /// <summary>Let the address range decide between the 32-bit block map and the 8-byte chunk index.
    /// <para>由地址范围在 32 位块映射与 8 字节 chunk 索引之间选择。</para></summary>
    Auto = 0,

    /// <summary>Always emit the bare 32-bit block map, failing loudly when an address needs the wide form.
    /// <para>始终写出裸 32 位块映射；地址需要宽形态时显式报错。</para></summary>
    BlockMap = 1,

    /// <summary>Always emit the 8-byte <c>erofs_inode_chunk_index</c> entries, which is what a device id or
    /// a 48-bit start block requires.
    /// <para>始终写出 8 字节 <c>erofs_inode_chunk_index</c> 表项，设备号或 48 位首块正需要该形态。</para></summary>
    Indexes = 2,
}

/// <summary>Selects what the dedupe pass compares, separating whole-file plaintext reuse from encoded
/// pcluster reuse.
/// <para>选择去重阶段比较的对象，把整文件明文复用与编码后 pcluster 复用区分开。</para></summary>
public enum ErofsDedupeMode
{
    /// <summary>Deduplicate whole files and whole-file fragment payloads by content hash, which is the
    /// repository's existing behavior when <see cref="ErofsOptions.Dedup"/> is set.
    /// <para>按内容哈希对整文件与整文件 fragment 负载去重，即仓库在 <see cref="ErofsOptions.Dedup"/> 置位时
    /// 既有行为。</para></summary>
    WholeFile = 0,

    /// <summary>Deduplicate encoded pclusters, so two files that encode to the same frame share one physical
    /// extent and their index rows point at the same block address.
    /// <para>对编码后的 pcluster 去重，使编码结果相同的两个文件共享同一物理区间，其索引行指向同一块地址。</para></summary>
    Pcluster = 1,
}