using System.Buffers.Binary;

namespace UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse.Models;

public readonly struct SparseHeader
{
    public uint Magic { get; init; }

    public ushort MajorVersion { get; init; }

    public ushort MinorVersion { get; init; }

    public ushort FileHeaderSize { get; init; }

    public ushort ChunkHeaderSize { get; init; }

    public uint BlockSize { get; init; }

    public uint TotalBlocks { get; init; }

    public uint TotalChunks { get; init; }

    public uint ImageChecksum { get; init; }

    /// <summary>
    /// Creates a default SparseHeader with standard magic, version, and header sizes.
    /// <para>使用标准魔术数字、版本号和头部大小创建默认的 SparseHeader。</para>
    /// </summary>
    public static SparseHeader CreateDefault(uint blockSize, uint totalBlocks)
    {
        return new SparseHeader
        {
            Magic = SparseFormat.SparseHeaderMagic,
            MajorVersion = 1,
            MinorVersion = 0,
            FileHeaderSize = SparseFormat.SparseHeaderSize,
            ChunkHeaderSize = SparseFormat.ChunkHeaderSize,
            BlockSize = blockSize,
            TotalBlocks = totalBlocks,
            TotalChunks = 0,
            ImageChecksum = 0
        };
    }

    /// <summary>
    /// Parses a SparseHeader from a little-endian byte sequence.
    /// <para>从小端字节序列解析 SparseHeader。</para>
    /// </summary>
    public static SparseHeader FromBytes(ReadOnlySpan<byte> data)
    {
        return data.Length < SparseFormat.SparseHeaderSize
            ? throw new ArgumentException("Data length is insufficient to build SparseHeader")
            : new SparseHeader
            {
                Magic = BinaryPrimitives.ReadUInt32LittleEndian(data),
                MajorVersion = BinaryPrimitives.ReadUInt16LittleEndian(data[4..]),
                MinorVersion = BinaryPrimitives.ReadUInt16LittleEndian(data[6..]),
                FileHeaderSize = BinaryPrimitives.ReadUInt16LittleEndian(data[8..]),
                ChunkHeaderSize = BinaryPrimitives.ReadUInt16LittleEndian(data[10..]),
                BlockSize = BinaryPrimitives.ReadUInt32LittleEndian(data[12..]),
                TotalBlocks = BinaryPrimitives.ReadUInt32LittleEndian(data[16..]),
                TotalChunks = BinaryPrimitives.ReadUInt32LittleEndian(data[20..]),
                ImageChecksum = BinaryPrimitives.ReadUInt32LittleEndian(data[24..])
            };
    }

    /// <summary>
    /// Writes the SparseHeader to a little-endian byte sequence.
    /// <para>将 SparseHeader 写入小端字节序列。</para>
    /// </summary>
    public void WriteTo(Span<byte> span)
    {
        if (span.Length < SparseFormat.SparseHeaderSize)
        {
            throw new ArgumentException("Span length is insufficient to write SparseHeader");
        }

        BinaryPrimitives.WriteUInt32LittleEndian(span, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], MajorVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(span[6..], MinorVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(span[8..], FileHeaderSize);
        BinaryPrimitives.WriteUInt16LittleEndian(span[10..], ChunkHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(span[12..], BlockSize);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], TotalBlocks);
        BinaryPrimitives.WriteUInt32LittleEndian(span[20..], TotalChunks);
        BinaryPrimitives.WriteUInt32LittleEndian(span[24..], ImageChecksum);
    }

    /// <summary>
    /// Converts the SparseHeader to a new byte array.
    /// <para>将 SparseHeader 转换为新的字节数组。</para>
    /// </summary>
    public byte[] ToBytes()
    {
        var data = new byte[SparseFormat.SparseHeaderSize];
        WriteTo(data);
        return data;
    }

    /// <summary>
    /// Validates whether the sparse header contains correct magic, version, and size fields.
    /// <para>验证稀疏头部是否包含正确的魔术数字、版本号和大小字段。</para>
    /// </summary>
    public bool IsValid()
    {
        return Magic == SparseFormat.SparseHeaderMagic &&
               MajorVersion == 1 &&
               FileHeaderSize >= SparseFormat.SparseHeaderSize &&
               ChunkHeaderSize >= SparseFormat.ChunkHeaderSize &&
               BlockSize > 0 && BlockSize % 4 == 0;
    }
}