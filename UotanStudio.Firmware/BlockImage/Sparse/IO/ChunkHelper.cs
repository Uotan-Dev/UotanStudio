using UotanStudio.Firmware.BlockImage.Sparse.Models;
namespace UotanStudio.Firmware.BlockImage.Sparse.IO;

/// <summary>
/// Derives on-disk and header byte sizes for Android sparse chunks from raw chunk fields, so every
/// chunk type is measured by one rule set.
/// <para>从原始数据块字段推导 Android 稀疏数据块的磁盘占用与头部字节大小，使所有数据块类型使用同一套规则。</para>
/// </summary>
internal static class ChunkHelper
{
    /// <summary>Calculates the on-disk size (in bytes) of a sparse chunk, including its header.
    /// <para>计算稀疏数据块的磁盘占用大小（字节数），包括数据块头部。</para></summary>
    /// <param name="chunkType">Chunk type value exactly as stored in the chunk header.
    /// <para>与数据块头部中存放值一致的类型值。</para></param>
    /// <param name="chunkSize">Block count declared by the chunk.<para>数据块声明的块数。</para></param>
    /// <param name="chunkHeaderSize">Byte size of a single chunk header, taken from the sparse file header.
    /// <para>单个数据块头部的字节大小，取自稀疏文件头部。</para></param>
    /// <param name="blockSize">Logical block size in bytes.<para>逻辑块大小（字节数）。</para></param>
    /// <returns>Header plus <c>chunkSize * blockSize</c> for a raw chunk, header plus <c>4</c> bytes for a fill chunk, and the header size alone for every other value, including types this reader does not know.
    /// <para>Raw 数据块返回头部加 <c>chunkSize * blockSize</c>，Fill 数据块返回头部加 <c>4</c> 字节， 其他值（含本读取器不认识的类型）仅返回头部大小。</para></returns>
    public static long GetDiskSize(ushort chunkType, uint chunkSize, ushort chunkHeaderSize, uint blockSize)
    {
        return chunkType switch
        {
            (ushort)ChunkType.Raw => chunkHeaderSize + ((long)chunkSize * blockSize),
            (ushort)ChunkType.Fill => chunkHeaderSize + 4,
            _ => chunkHeaderSize
        };
    }

    /// <summary>Calculates the on-disk size of a chunk using its header and the sparse file header.
    /// <para>使用数据块头部和稀疏文件头部计算数据块的磁盘占用大小。</para></summary>
    /// <param name="header">Chunk header supplying the type and block count.
    /// <para>提供类型与块数的数据块头部。</para></param>
    /// <param name="chunkHeaderSize">Byte size of a single chunk header, taken from the sparse file header.
    /// <para>单个数据块头部的字节大小，取自稀疏文件头部。</para></param>
    /// <param name="blockSize">Logical block size in bytes.<para>逻辑块大小（字节数）。</para></param>
    /// <returns>Same measurement as the field-based overload, expressed in bytes.
    /// <para>与基于字段的重载相同的字节度量结果。</para></returns>
    public static long GetDiskSize(ChunkHeader header, ushort chunkHeaderSize, uint blockSize) => GetDiskSize(header.ChunkType, header.ChunkSize, chunkHeaderSize, blockSize);

    /// <summary>Calculates the on-disk size of a sparse chunk using the sparse file header for parameters.
    /// <para>使用稀疏文件头部的参数计算稀疏数据块的磁盘占用大小。</para></summary>
    /// <param name="chunk">Parsed chunk whose header carries the type and block count.
    /// <para>已解析的数据块，其头部携带类型与块数。</para></param>
    /// <param name="sparseHeader">Sparse file header supplying the chunk-header size and logical block size.
    /// <para>提供数据块头部大小与逻辑块大小的稀疏文件头部。</para></param>
    /// <returns>Chunk byte size derived from the file header's chunk-header and block sizes.
    /// <para>由文件头部的数据块头部大小与块大小推导出的数据块字节大小。</para></returns>
    public static long GetDiskSize(SparseChunk chunk, SparseHeader sparseHeader)
    {
        return GetDiskSize(chunk.Header.ChunkType, chunk.Header.ChunkSize,
            sparseHeader.ChunkHeaderSize, sparseHeader.BlockSize);
    }

    /// <summary>Calculates the expected TotalSize field value for a chunk header.
    /// <para>计算数据块头部的预期 TotalSize 字段值。</para></summary>
    /// <param name="chunkType">Chunk type value exactly as stored in the chunk header.
    /// <para>与数据块头部中存放值一致的类型值。</para></param>
    /// <param name="chunkSize">Block count declared by the chunk.<para>数据块声明的块数。</para></param>
    /// <param name="chunkHeaderSize">Byte size of a single chunk header, taken from the sparse file header.
    /// <para>单个数据块头部的字节大小，取自稀疏文件头部。</para></param>
    /// <param name="blockSize">Logical block size in bytes.<para>逻辑块大小（字节数）。</para></param>
    /// <returns>The disk size in bytes narrowed to <see cref="uint"/>, matching the width of the stored field.
    /// <para>收窄为 <see cref="uint"/> 的字节磁盘大小，与存储字段的宽度一致。</para></returns>
    /// <exception cref="OverflowException">The computed byte size does not fit in a <see cref="uint"/>.
    /// <para>计算出的字节大小无法放入 <see cref="uint"/>。</para></exception>
    public static uint GetExpectedTotalSize(ushort chunkType, uint chunkSize, ushort chunkHeaderSize, uint blockSize)
        => checked((uint)GetDiskSize(chunkType, chunkSize, chunkHeaderSize, blockSize));

    /// <summary>Determines whether the specified chunk type contains splittable data (Raw or Fill).
    /// <para>判断指定的数据块类型是否包含可拆分的数据（Raw 或 Fill）。</para></summary>
    /// <param name="chunkType">Chunk type value exactly as stored in the chunk header.
    /// <para>与数据块头部中存放值一致的类型值。</para></param>
    /// <returns><see langword="true"/> for raw and fill chunks; <see langword="false"/> for every other value, so header-only types such as <c>DONT_CARE</c> and <c>CRC32</c> count as unsplittable.
    /// <para>Raw 与 Fill 数据块返回 <see langword="true"/>；其他值返回 <see langword="false"/>， 即仅含头部的 <c>DONT_CARE</c>、<c>CRC32</c> 类型视为不可拆分。</para></returns>
    public static bool IsSplittableChunkType(ushort chunkType) => chunkType is (ushort)ChunkType.Raw or (ushort)ChunkType.Fill;
}