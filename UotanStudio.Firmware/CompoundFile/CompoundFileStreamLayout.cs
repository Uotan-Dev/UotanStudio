namespace UotanStudio.Firmware.CompoundFile;

/// <summary>
/// Physical block layout of one compound file stream: the file byte offset of every logical block
/// plus the uniform block size (the sector size for regular streams, 64 bytes for mini streams).
/// <para>复合文档单个流的物理块布局：每个逻辑块的文件字节偏移与统一块大小（常规流为扇区大小，
/// mini 流为 64 字节）。</para>
/// </summary>
public sealed class CompoundFileStreamLayout
{
    internal CompoundFileStreamLayout(long[] blockOffsets, int blockSize)
    {
        BlockOffsets = blockOffsets;
        BlockSize = blockSize;
    }

    /// <summary>
    /// Gets the physical byte offsets ordered by logical block index.
    /// <para>获取按逻辑块索引排列的物理字节偏移。</para>
    /// </summary>
    /// <value>
    /// Physical byte offsets of the logical blocks.
    /// <para>各逻辑块的物理字节偏移。</para>
    /// </value>
    public long[] BlockOffsets { get; }

    /// <summary>
    /// Gets the uniform logical block size in bytes.
    /// <para>获取统一的逻辑块大小（字节）。</para>
    /// </summary>
    /// <value>
    /// The logical block size in bytes.
    /// <para>逻辑块大小（字节）。</para>
    /// </value>
    public int BlockSize { get; }
}