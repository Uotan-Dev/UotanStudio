namespace UotanStudio.Firmware.FileSystem.Erofs.Models.Inodes;

/// <summary>
/// Physical layout of an inode's payload as encoded in the <c>EROFS_I_DATALAYOUT</c> bits of
/// <c>i_format</c>.
/// <para>由 <c>i_format</c> 的 <c>EROFS_I_DATALAYOUT</c> 位编码的 inode 负载物理布局。</para>
/// </summary>
internal enum ErofsDataLayout : byte
{
    /// <summary>Data blocks addressed directly by <c>u.i_blkaddr</c>.
    /// <para>数据块由 <c>u.i_blkaddr</c> 直接寻址。</para></summary>
    Flat = 0,

    /// <summary>Compressed payload with the full 8-byte lcluster index form.
    /// <para>采用完整 8 字节逻辑簇索引形式的压缩负载。</para></summary>
    Compressed = 1,

    /// <summary>Flat blocks followed by the sub-block tail stored beside the inode record.
    /// <para>平坦块之后跟随存放在 inode 记录旁的不完整块尾部。</para></summary>
    Inline = 2,

    /// <summary>Compressed payload whose lcluster index uses the packed 4-byte form.
    /// <para>逻辑簇索引采用紧凑 4 字节形式的压缩负载。</para></summary>
    CompactIndexes = 3,

    /// <summary>Data addressed through a chunk index table (<c>u.chunkformat</c> plus <c>chunkbits</c>).
    /// <para>经 chunk 索引表寻址的数据（<c>u.chunkformat</c> 加上 <c>chunkbits</c>）。</para></summary>
    ChunkBased = 4,
}