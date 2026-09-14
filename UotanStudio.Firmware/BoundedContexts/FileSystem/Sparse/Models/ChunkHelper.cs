namespace UotanStudio.Firmware.BoundedContexts.FileSystem.Sparse;

public static class ChunkHelper
{
    /// <summary>
    /// Calculates the on-disk size (in bytes) of a sparse chunk, including its header.
    /// <para>计算稀疏数据块的磁盘占用大小（字节数），包括数据块头部。</para>
    /// </summary>
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
    public static long GetDiskSize(ChunkHeader header, ushort chunkHeaderSize, uint blockSize) => GetDiskSize(header.ChunkType, header.ChunkSize, chunkHeaderSize, blockSize);

    /// <summary>
    /// Calculates the on-disk size of a sparse chunk using the sparse file header for parameters.
    /// <para>使用稀疏文件头部的参数计算稀疏数据块的磁盘占用大小。</para>
    /// </summary>
    public static long GetDiskSize(SparseChunk chunk, SparseHeader sparseHeader)
    {
        return GetDiskSize(chunk.Header.ChunkType, chunk.Header.ChunkSize,
            sparseHeader.ChunkHeaderSize, sparseHeader.BlockSize);
    }

    /// <summary>
    /// Calculates the expected TotalSize field value for a chunk header.
    /// <para>计算数据块头部的预期 TotalSize 字段值。</para>
    /// </summary>
    public static uint GetExpectedTotalSize(ushort chunkType, uint chunkSize, ushort chunkHeaderSize, uint blockSize)
        => checked((uint)GetDiskSize(chunkType, chunkSize, chunkHeaderSize, blockSize));

    /// <summary>
    /// Determines whether the specified chunk type contains splittable data (Raw or Fill).
    /// <para>判断指定的数据块类型是否包含可拆分的数据（Raw 或 Fill）。</para>
    /// </summary>
    public static bool IsSplittableChunkType(ushort chunkType) => chunkType is (ushort)ChunkType.Raw or (ushort)ChunkType.Fill;
}