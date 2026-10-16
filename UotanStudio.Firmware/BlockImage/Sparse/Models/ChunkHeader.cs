using System.Buffers.Binary;

namespace UotanStudio.Firmware.BlockImage.Sparse.Models;

/// <summary>
/// Immutable 12-byte base header of a single sparse chunk, parsed and written in little-endian form.
/// <para>单个稀疏数据块的不可变 12 字节基础头部，以小端形式解析和写入。</para>
/// </summary>
public readonly struct ChunkHeader
{
    /// <summary>Chunk type identifier as stored in the file.
    /// <para>文件中存储的数据块类型标识。</para></summary>
    /// <value>Compare against <see cref="Models.ChunkType"/> values; only the four standard identifiers pass <see cref="IsValid"/>.
    /// <para>与 <see cref="Models.ChunkType"/> 值比较；仅四种标准标识能通过 <see cref="IsValid"/>。</para></value>
    public ushort ChunkType { get; init; }

    /// <summary>Format-reserved 16-bit field between the type and the size fields.
    /// <para>类型与大小字段之间由格式保留的 16 位字段。</para></summary>
    /// <value>Round-tripped unchanged by parsing and writing; chunks this library creates leave it at the default 0.
    /// <para>解析与写入时原样往返；本库创建的数据块保持其默认为 0。</para></value>
    public ushort Reserved { get; init; }

    /// <summary>Number of logical blocks this chunk covers.
    /// <para>该数据块覆盖的逻辑块数量。</para></summary>
    /// <value>The block count; multiplying by <see cref="SparseHeader.BlockSize"/> gives the chunk's expanded byte length; CRC32 chunks carry 0.
    /// <para>块数量；乘以 <see cref="SparseHeader.BlockSize"/> 即得数据块展开后的字节长度；CRC32 块为 0。</para></value>
    public uint ChunkSize { get; init; }

    /// <summary>Total on-disk size of the chunk, header and payload included.
    /// <para>数据块在磁盘上的总大小，包含头部与载荷。</para></summary>
    /// <value>Measured in bytes and compared against the header size declared in the file header during validation.
    /// <para>以字节计，校验时与文件头部声明的头部大小进行比较。</para></value>
    public uint TotalSize { get; init; }

    /// <summary>
    /// Parses a ChunkHeader from a little-endian byte sequence.
    /// <para>从小端字节序列解析 ChunkHeader。</para>
    /// </summary>
    /// <param name="data">Byte sequence holding at least <see cref="SparseFormat.ChunkHeaderSize"/> little-endian header bytes.
    /// <para>至少包含 <see cref="SparseFormat.ChunkHeaderSize"/> 个小端头部字节的序列。</para></param>
    /// <returns>The header fields exactly as encoded; type and size consistency are not validated.
    /// <para>按编码原样解析的头部字段；不校验类型与大小一致性。</para></returns>
    /// <exception cref="ArgumentException">The sequence is shorter than <see cref="SparseFormat.ChunkHeaderSize"/> bytes.
    /// <para>字节序列少于 <see cref="SparseFormat.ChunkHeaderSize"/> 字节。</para></exception>
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
    /// <param name="span">Destination of at least <see cref="SparseFormat.ChunkHeaderSize"/> bytes; trailing bytes beyond the header are left untouched.
    /// <para>至少 <see cref="SparseFormat.ChunkHeaderSize"/> 字节的目标区域；头部之外的后续字节保持不变。</para></param>
    /// <exception cref="ArgumentException">The span is shorter than <see cref="SparseFormat.ChunkHeaderSize"/> bytes.
    /// <para>目标区域少于 <see cref="SparseFormat.ChunkHeaderSize"/> 字节。</para></exception>
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
    /// <returns>A freshly allocated array of exactly <see cref="SparseFormat.ChunkHeaderSize"/> little-endian bytes.
    /// <para>新分配的、恰好 <see cref="SparseFormat.ChunkHeaderSize"/> 字节的小端数组。</para></returns>
    public byte[] ToBytes()
    {
        var data = new byte[SparseFormat.ChunkHeaderSize];
        WriteTo(data);
        return data;
    }

    /// <summary>
    /// Validates whether the chunk header has a recognized type and consistent size fields.
    /// <para>验证数据块头部是否具有可识别的类型与一致的大小字段。</para>
    /// </summary>
    /// <param name="chunkHeaderSize">Effective per-chunk header size declared in the file header, including extended bytes.
    /// <para>文件头部声明的每块有效头部大小，包含扩展字节。</para></param>
    /// <param name="blockSize">Logical block size used to expand the chunk's block count.
    /// <para>用于展开数据块块数的逻辑块大小。</para></param>
    /// <returns><see langword="true"/> when the type is one of the four standard identifiers and <see cref="TotalSize"/> matches the header size plus the payload the type requires; expected sizes are computed in 64-bit arithmetic. CRC32 chunks may carry a block count of 0; all other types require a positive <see cref="ChunkSize"/>.
    /// <para>当类型为四种标准标识之一且 <see cref="TotalSize"/> 等于头部大小加上该类型所需载荷时返回 <see langword="true"/>；期望大小使用 64 位算术计算。CRC32 块的块数可为 0；其他类型均要求正的 <see cref="ChunkSize"/>。</para></returns>
    public bool IsValid(ushort chunkHeaderSize, uint blockSize)
    {
        if (TotalSize < chunkHeaderSize)
        {
            return false;
        }

        return ChunkType switch
        {
            (ushort)Models.ChunkType.Raw => ChunkSize > 0 && TotalSize == chunkHeaderSize + ((long)ChunkSize * blockSize),
            (ushort)Models.ChunkType.Fill => ChunkSize > 0 && TotalSize == chunkHeaderSize + 4,
            (ushort)Models.ChunkType.DontCare => ChunkSize > 0 && TotalSize == chunkHeaderSize,
            (ushort)Models.ChunkType.Crc32 => TotalSize == chunkHeaderSize + 4,
            _ => false,
        };
    }
}