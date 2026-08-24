using System.Buffers.Binary;

namespace UotanStudio.Firmware.Sparse;

/// <summary>
/// Represents the structure of a sparse chunk header.
/// <para>表示稀疏数据块头部的结构。</para>
/// </summary>
public readonly struct ChunkHeader
{
    /// <summary>
    /// The type of the chunk (e.g., Raw, Fill, DontCare, Crc32).
    /// <para>数据块类型（如 Raw、Fill、DontCare、Crc32）。</para>
    /// </summary>
    public ushort ChunkType { get; init; }

    /// <summary>
    /// Reserved field, should be zero.
    /// <para>保留字段，应为零。</para>
    /// </summary>
    public ushort Reserved { get; init; }

    /// <summary>
    /// The number of blocks this chunk represents in the output image.
    /// <para>此数据块在输出镜像中表示的块数。</para>
    /// </summary>
    public uint ChunkSize { get; init; }

    /// <summary>
    /// The total size of the chunk including the header, in bytes.
    /// <para>数据块的总大小（包括头部），以字节为单位。</para>
    /// </summary>
    public uint TotalSize { get; init; }

    /// <summary>
    /// Parses a <see cref="ChunkHeader"/> from a little-endian byte sequence.
    /// <para>从小端字节序列解析 ChunkHeader。</para>
    /// </summary>
    /// <param name="data">A read-only byte span containing at least <see cref="SparseFormat.ChunkHeaderSize"/> bytes.
    /// <para>包含至少 ChunkHeaderSize 字节的只读字节跨度。</para></param>
    /// <returns>A parsed instance of <see cref="ChunkHeader"/>. <para>解析后的 ChunkHeader 实例。</para></returns>
    /// <exception cref="ArgumentException">Thrown when the data length is insufficient.
    /// <para>当数据长度不足时抛出。</para></exception>
    public static ChunkHeader FromBytes(ReadOnlySpan<byte> data)
    {
        return data.Length < SparseFormat.ChunkHeaderSize
            ? throw new ArgumentException("Data length is insufficient to build ChunkHeader")
            : new ChunkHeader
            {
                ChunkType = BinaryPrimitives.ReadUInt16LittleEndian(data),
                Reserved = BinaryPrimitives.ReadUInt16LittleEndian(data[2..]),
                ChunkSize = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]),
                TotalSize = BinaryPrimitives.ReadUInt32LittleEndian(data[8..])
            };
    }

    /// <summary>
    /// Writes the <see cref="ChunkHeader"/> to a little-endian byte sequence.
    /// <para>将 ChunkHeader 写入小端字节序列。</para>
    /// </summary>
    /// <param name="span">The target byte span, must be at least <see cref="SparseFormat.ChunkHeaderSize"/> bytes.
    /// <para>目标字节跨度，必须至少为 ChunkHeaderSize 字节。</para></param>
    /// <exception cref="ArgumentException">Thrown when the span length is insufficient.
    /// <para>当跨度长度不足时抛出。</para></exception>
    public void WriteTo(Span<byte> span)
    {
        if (span.Length < SparseFormat.ChunkHeaderSize)
        {
            throw new ArgumentException("Span length is insufficient to write ChunkHeader");
        }

        BinaryPrimitives.WriteUInt16LittleEndian(span, ChunkType);
        BinaryPrimitives.WriteUInt16LittleEndian(span[2..], Reserved);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], ChunkSize);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], TotalSize);
    }

    /// <summary>
    /// Converts the <see cref="ChunkHeader"/> to a new byte array.
    /// <para>将 ChunkHeader 转换为新的字节数组。</para>
    /// </summary>
    /// <returns>A byte array containing the serialized chunk header data. <para>包含序列化数据块头部数据的字节数组。</para></returns>
    public byte[] ToBytes()
    {
        var data = new byte[SparseFormat.ChunkHeaderSize];
        WriteTo(data);
        return data;
    }

    /// <summary>
    /// Validates whether the chunk header has a recognized type and consistent size fields.
    /// <para>验证数据块头部是否具有可识别的类型和一致的大小字段。</para>
    /// </summary>
    /// <param name="chunkHeaderSize">The expected chunk header size in bytes. <para>预期的数据块头部字节大小。</para></param>
    /// <param name="blockSize">The block size in bytes. <para>块的字节大小。</para></param>
    /// <returns>True if the chunk header appears valid; otherwise false. <para>如果数据块头部看起来有效则返回 true；否则返回 false。</para></returns>
    public bool IsValid(ushort chunkHeaderSize, uint blockSize)
    {
        return TotalSize >= chunkHeaderSize && (ChunkType switch
        {
            (ushort)Sparse.ChunkType.Raw => ChunkSize > 0 && TotalSize == chunkHeaderSize + ((long)ChunkSize * blockSize),
            (ushort)Sparse.ChunkType.Fill => ChunkSize > 0 && TotalSize == chunkHeaderSize + 4,
            (ushort)Sparse.ChunkType.DontCare => ChunkSize > 0 && TotalSize == chunkHeaderSize,
            (ushort)Sparse.ChunkType.Crc32 => TotalSize == chunkHeaderSize + 4,
            _ => (ChunkType & 0x8000) != 0,
        });
    }
}