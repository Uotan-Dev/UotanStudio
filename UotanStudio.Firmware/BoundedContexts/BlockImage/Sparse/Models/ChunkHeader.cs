using System.Buffers.Binary;

namespace UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse.Models;

public readonly struct ChunkHeader
{
    public ushort ChunkType { get; init; }

    public ushort Reserved { get; init; }

    public uint ChunkSize { get; init; }

    public uint TotalSize { get; init; }

    /// <summary>
    /// Parses a ChunkHeader from a little-endian byte sequence.
    /// <para>从小端字节序列解析 ChunkHeader。</para>
    /// </summary>
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
    /// Writes the ChunkHeader to a little-endian byte sequence.
    /// <para>将 ChunkHeader 写入小端字节序列。</para>
    /// </summary>
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
    /// Converts the ChunkHeader to a new byte array.
    /// <para>将 ChunkHeader 转换为新的字节数组。</para>
    /// </summary>
    public byte[] ToBytes()
    {
        var data = new byte[SparseFormat.ChunkHeaderSize];
        WriteTo(data);
        return data;
    }

    /// <summary>
    /// Validates whether the chunk header has a recognized type and consistent size fields.
    /// Only the four AOSP standard types (RAW, FILL, DONT_CARE, CRC32) are accepted;
    /// any other type — including the 0x8000 vendor-range bits — is rejected here so
    /// the reader and the validator agree on what is a parseable sparse image.
    /// <para>验证数据块头部是否具有可识别的类型和一致的大小字段。仅接受 AOSP 四种标准类型
    /// （RAW/FILL/DONT_CARE/CRC32），任何其他类型——含 0x8000 厂商范围位——在此即被拒绝，
    /// 使读取端与验证端结论一致。</para>
    /// </summary>
    public bool IsValid(ushort chunkHeaderSize, uint blockSize)
    {
        if (TotalSize < chunkHeaderSize)
        {
            return false;
        }

        return ChunkType switch
        {
            (ushort)UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse.Models.ChunkType.Raw => ChunkSize > 0 && TotalSize == chunkHeaderSize + ((long)ChunkSize * blockSize),
            (ushort)UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse.Models.ChunkType.Fill => ChunkSize > 0 && TotalSize == chunkHeaderSize + 4,
            (ushort)UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse.Models.ChunkType.DontCare => ChunkSize > 0 && TotalSize == chunkHeaderSize,
            (ushort)UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse.Models.ChunkType.Crc32 => TotalSize == chunkHeaderSize + 4,
            _ => false,
        };
    }
}