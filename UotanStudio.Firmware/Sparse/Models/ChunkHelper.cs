namespace UotanStudio.Firmware.Sparse;

/// <summary>
/// Provides helper methods for calculating sparse chunk sizes and validating chunk headers.
/// <para>提供计算稀疏数据块大小和验证数据块头部的辅助方法。</para>
/// </summary>
public static class ChunkHelper
{
    /// <summary>
    /// Calculates the on-disk size (in bytes) of a sparse chunk, including its header.
    /// <para>计算稀疏数据块的磁盘占用大小（字节数），包括数据块头部。</para>
    /// </summary>
    /// <param name="chunkType">Chunk type code (ushort). <para>数据块类型代码。</para></param>
    /// <param name="chunkSize">Number of blocks in the chunk (uint). <para>数据块中的块数量。</para></param>
    /// <param name="chunkHeaderSize">Size of the chunk header in bytes (ushort). <para>数据块头部的字节大小。</para></param>
    /// <param name="blockSize">Size of a single block in bytes (uint). <para>单个块的字节大小。</para></param>
    /// <returns>The total on-disk size in bytes. <para>磁盘占用总字节数。</para></returns>
    public static long GetDiskSize(ushort chunkType, uint chunkSize, ushort chunkHeaderSize, uint blockSize)
    {
        return chunkType switch
        {
            (ushort)ChunkType.Raw => chunkHeaderSize + ((long)chunkSize * blockSize),
            (ushort)ChunkType.Fill => chunkHeaderSize + 4,
            _ => chunkHeaderSize
        };
    }

    /// <summary>
    /// Calculates the on-disk size of a chunk using its header and the sparse file header.
    /// <para>使用数据块头部和稀疏文件头部计算数据块的磁盘占用大小。</para>
    /// </summary>
    /// <param name="header">The chunk header. <para>数据块头部。</para></param>
    /// <param name="chunkHeaderSize">Size of the chunk header in bytes (ushort). <para>数据块头部的字节大小。</para></param>
    /// <param name="blockSize">Size of a single block in bytes (uint). <para>单个块的字节大小。</para></param>
    /// <returns>The total on-disk size in bytes. <para>磁盘占用总字节数。</para></returns>
    public static long GetDiskSize(ChunkHeader header, ushort chunkHeaderSize, uint blockSize) => GetDiskSize(header.ChunkType, header.ChunkSize, chunkHeaderSize, blockSize);

    /// <summary>
    /// Calculates the on-disk size of a sparse chunk using the sparse file header for parameters.
    /// <para>使用稀疏文件头部的参数计算稀疏数据块的磁盘占用大小。</para>
    /// </summary>
    /// <param name="chunk">The sparse chunk. <para>稀疏数据块。</para></param>
    /// <param name="sparseHeader">The parent sparse file header. <para>所属稀疏文件的头部。</para></param>
    /// <returns>The total on-disk size in bytes. <para>磁盘占用总字节数。</para></returns>
    public static long GetDiskSize(SparseChunk chunk, SparseHeader sparseHeader)
    {
        return GetDiskSize(chunk.Header.ChunkType, chunk.Header.ChunkSize,
            sparseHeader.ChunkHeaderSize, sparseHeader.BlockSize);
    }

    /// <summary>
    /// Calculates the expected TotalSize field value for a chunk header.
    /// <para>计算数据块头部的预期 TotalSize 字段值。</para>
    /// </summary>
    /// <param name="chunkType">Chunk type code (ushort). <para>数据块类型代码。</para></param>
    /// <param name="chunkSize">Number of blocks in the chunk (uint). <para>数据块中的块数量。</para></param>
    /// <param name="chunkHeaderSize">Size of the chunk header in bytes (ushort). <para>数据块头部的字节大小。</para></param>
    /// <param name="blockSize">Size of a single block in bytes (uint). <para>单个块的字节大小。</para></param>
    /// <returns>The expected TotalSize value as a uint. <para>预期的 TotalSize 值。</para></returns>
    public static uint GetExpectedTotalSize(ushort chunkType, uint chunkSize, ushort chunkHeaderSize, uint blockSize)
        => checked((uint)GetDiskSize(chunkType, chunkSize, chunkHeaderSize, blockSize));

    /// <summary>
    /// Determines whether the specified chunk type contains splittable data (Raw or Fill).
    /// <para>判断指定的数据块类型是否包含可拆分的数据（Raw 或 Fill）。</para>
    /// </summary>
    /// <param name="chunkType">Chunk type code (ushort). <para>数据块类型代码。</para></param>
    /// <returns>True if the chunk type is Raw or Fill. <para>如果数据块类型为 Raw 或 Fill 则返回 true。</para></returns>
    public static bool IsSplittableChunkType(ushort chunkType) => chunkType is (ushort)ChunkType.Raw or (ushort)ChunkType.Fill;
}