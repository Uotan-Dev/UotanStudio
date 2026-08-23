using System.Buffers.Binary;

namespace UotanStudio.Firmware.Sparse;

/// <summary>
/// Represents the structure of a sparse file header.
/// <para>表示稀疏文件头部的结构。</para>
/// </summary>
public readonly struct SparseHeader
{
    /// <summary>
    /// The magic number that identifies this as a sparse image.
    /// <para>标识此文件为稀疏镜像的魔术数字。</para>
    /// </summary>
    public uint Magic { get; init; }

    /// <summary>
    /// The major version number of the sparse format.
    /// <para>稀疏格式的主版本号。</para>
    /// </summary>
    public ushort MajorVersion { get; init; }

    /// <summary>
    /// The minor version number of the sparse format.
    /// <para>稀疏格式的次版本号。</para>
    /// </summary>
    public ushort MinorVersion { get; init; }

    /// <summary>
    /// The size of the file header in bytes.
    /// <para>文件头部的字节大小。</para>
    /// </summary>
    public ushort FileHeaderSize { get; init; }

    /// <summary>
    /// The size of each chunk header in bytes.
    /// <para>每个数据块头部的字节大小。</para>
    /// </summary>
    public ushort ChunkHeaderSize { get; init; }

    /// <summary>
    /// The block size in bytes. Every chunk's data length is a multiple of this value.
    /// <para>块的字节大小。每个数据块的数据长度为此值的整数倍。</para>
    /// </summary>
    public uint BlockSize { get; init; }

    /// <summary>
    /// The total number of blocks in the unsparsified image.
    /// <para>未稀疏化镜像中的总块数。</para>
    /// </summary>
    public uint TotalBlocks { get; init; }

    /// <summary>
    /// The total number of chunks in the sparse image.
    /// <para>稀疏镜像中的总数据块数。</para>
    /// </summary>
    public uint TotalChunks { get; init; }

    /// <summary>
    /// The image checksum (CRC32 of all chunk checksums).
    /// <para>镜像校验和（所有数据块校验和的 CRC32）。</para>
    /// </summary>
    public uint ImageChecksum { get; init; }

    /// <summary>
    /// Creates a default <see cref="SparseHeader"/> with standard magic, version, and header sizes.
    /// <para>使用标准魔术数字、版本号和头部大小创建默认的 SparseHeader。</para>
    /// </summary>
    /// <param name="blockSize">Block size in bytes. <para>块的字节大小。</para></param>
    /// <param name="totalBlocks">Total number of blocks in the image. <para>镜像中的总块数。</para></param>
    /// <returns>A new <see cref="SparseHeader"/> with default values. <para>具有默认值的新 SparseHeader。</para></returns>
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
    /// Parses a <see cref="SparseHeader"/> from a little-endian byte sequence.
    /// <para>从小端字节序列解析 SparseHeader。</para>
    /// </summary>
    /// <param name="data">A read-only byte sequence containing at least <see cref="SparseFormat.SparseHeaderSize"/> bytes.
    /// <para>包含至少 SparseHeaderSize 字节的只读字节序列。</para></param>
    /// <returns>A parsed instance of <see cref="SparseHeader"/>. <para>解析后的 SparseHeader 实例。</para></returns>
    /// <exception cref="ArgumentException">Thrown when the data length is insufficient.
    /// <para>当数据长度不足时抛出。</para></exception>
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
    /// Writes the <see cref="SparseHeader"/> to a little-endian byte sequence.
    /// <para>将 SparseHeader 写入小端字节序列。</para>
    /// </summary>
    /// <param name="span">The target byte span, must be at least <see cref="SparseFormat.SparseHeaderSize"/> bytes.
    /// <para>目标字节跨度，必须至少为 SparseHeaderSize 字节。</para></param>
    /// <exception cref="ArgumentException">Thrown when the span length is insufficient.
    /// <para>当跨度长度不足时抛出。</para></exception>
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
    /// Converts the <see cref="SparseHeader"/> to a new byte array.
    /// <para>将 SparseHeader 转换为新的字节数组。</para>
    /// </summary>
    /// <returns>A byte array containing the serialized header data. <para>包含序列化头部数据的字节数组。</para></returns>
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
    /// <returns>True if the header is valid; otherwise false. <para>如果头部有效则返回 true；否则返回 false。</para></returns>
    public bool IsValid()
    {
        return Magic == SparseFormat.SparseHeaderMagic &&
               MajorVersion == 1 &&
               FileHeaderSize >= SparseFormat.SparseHeaderSize &&
               ChunkHeaderSize >= SparseFormat.ChunkHeaderSize &&
               BlockSize > 0 && BlockSize % 4 == 0;
    }
}