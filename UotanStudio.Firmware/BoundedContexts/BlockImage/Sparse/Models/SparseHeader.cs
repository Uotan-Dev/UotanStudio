using System.Buffers.Binary;

namespace UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse.Models;

/// <summary>
/// Immutable 28-byte base header of an Android sparse image, parsed and written in little-endian form.
/// <para>Android 稀疏镜像的不可变 28 字节基础头部，以小端形式解析和写入。</para>
/// </summary>
public readonly struct SparseHeader
{
    /// <summary>Format magic identifying the file as a sparse image.
    /// <para>标识文件为稀疏镜像的格式魔数。</para></summary>
    /// <value>The raw 32-bit magic as stored in the file; compare with <see cref="SparseFormat.SparseHeaderMagic"/> to recognise a sparse image.
    /// <para>文件中存储的原始 32 位魔数；与 <see cref="SparseFormat.SparseHeaderMagic"/> 比较可识别稀疏镜像。</para></value>
    public uint Magic { get; init; }

    /// <summary>Major format version accepted by this reader.
    /// <para>当前读取器接受的主版本号。</para></summary>
    /// <value>Stored value; <see cref="IsValid"/> only accepts major version 1.
    /// <para>存储值；<see cref="IsValid"/> 仅接受主版本 1。</para></value>
    public ushort MajorVersion { get; init; }

    /// <summary>Minor format version recorded by the producing tool.
    /// <para>生成工具记录的次版本号。</para></summary>
    /// <value>Stored value; this implementation does not constrain the minor version.
    /// <para>存储值；此实现不约束次版本号。</para></value>
    public ushort MinorVersion { get; init; }

    /// <summary>Declared total size of the file header, including any extended header bytes.
    /// <para>声明的文件头部总大小，包含扩展头部字节。</para></summary>
    /// <value>Must be at least <see cref="SparseFormat.SparseHeaderSize"/> for the header to be valid; readers skip the extra bytes.
    /// <para>头部有效时至少为 <see cref="SparseFormat.SparseHeaderSize"/>；读取器会跳过多余字节。</para></value>
    public ushort FileHeaderSize { get; init; }

    /// <summary>Declared total size of each chunk header, including any extended chunk-header bytes.
    /// <para>声明的每个数据块头部总大小，包含扩展数据块头部字节。</para></summary>
    /// <value>Must be at least <see cref="SparseFormat.ChunkHeaderSize"/> for the header to be valid; the extra bytes are skipped per chunk.
    /// <para>头部有效时至少为 <see cref="SparseFormat.ChunkHeaderSize"/>；每块的多余字节会被跳过。</para></value>
    public ushort ChunkHeaderSize { get; init; }

    /// <summary>Logical block size the image is laid out in.
    /// <para>镜像布局所使用的逻辑块大小。</para></summary>
    /// <value>Valid headers require a positive size that is a multiple of 4.
    /// <para>有效头部要求为正的 4 的倍数。</para></value>
    public uint BlockSize { get; init; }

    /// <summary>Total number of logical blocks the expanded image covers.
    /// <para>展开后镜像覆盖的逻辑块总数。</para></summary>
    /// <value>The logical block count; multiplying by <see cref="BlockSize"/> gives the expanded image size in bytes.
    /// <para>逻辑块数量；乘以 <see cref="BlockSize"/> 即得展开镜像的字节大小。</para></value>
    public uint TotalBlocks { get; init; }

    /// <summary>Number of chunk records that follow the file header.
    /// <para>文件头部之后的数据块记录数量。</para></summary>
    /// <value>0 describes an image with no chunks; writers recompute it from the emitted chunk list.
    /// <para>0 表示没有数据块的镜像；写入器会依据实际写出的数据块列表重新计算。</para></value>
    public uint TotalChunks { get; init; }

    /// <summary>Checksum of the fully expanded image recorded by the producing tool.
    /// <para>生成工具记录的完整展开镜像校验和。</para></summary>
    /// <value>CRC32 of the expanded bytes; 0 means absent, and validators only compare it when non-zero.
    /// <para>展开字节的 CRC32；0 表示缺失，校验方仅在非 0 时进行比较。</para></value>
    public uint ImageChecksum { get; init; }

    /// <summary>
    /// Creates a default SparseHeader with standard magic, version, and header sizes.
    /// <para>使用标准魔术数字、版本号和头部大小创建默认的 SparseHeader。</para>
    /// </summary>
    /// <param name="blockSize">Logical block size recorded in the header; not validated here.
    /// <para>记入头部的逻辑块大小；此处不做校验。</para></param>
    /// <param name="totalBlocks">Total logical block count of the expanded image.
    /// <para>展开后镜像的逻辑块总数。</para></param>
    /// <returns>A header with magic, version 1.0, standard sizes, zero chunks and zero checksum.
    /// <para>返回包含标准魔数、版本 1.0、标准大小、零数据块数与零校验和的头部。</para></returns>
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
    /// <param name="data">Byte sequence holding at least <see cref="SparseFormat.SparseHeaderSize"/> little-endian header bytes.
    /// <para>至少包含 <see cref="SparseFormat.SparseHeaderSize"/> 个小端头部字节的序列。</para></param>
    /// <returns>The header fields exactly as encoded; no magic, version or geometry validation is applied.
    /// <para>按编码原样解析的头部字段；不校验魔数、版本或几何信息。</para></returns>
    /// <exception cref="ArgumentException">The sequence is shorter than <see cref="SparseFormat.SparseHeaderSize"/> bytes.
    /// <para>字节序列少于 <see cref="SparseFormat.SparseHeaderSize"/> 字节。</para></exception>
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
    /// <param name="span">Destination of at least <see cref="SparseFormat.SparseHeaderSize"/> bytes; trailing bytes beyond the header are left untouched.
    /// <para>至少 <see cref="SparseFormat.SparseHeaderSize"/> 字节的目标区域；头部之外的后续字节保持不变。</para></param>
    /// <exception cref="ArgumentException">The span is shorter than <see cref="SparseFormat.SparseHeaderSize"/> bytes.
    /// <para>目标区域少于 <see cref="SparseFormat.SparseHeaderSize"/> 字节。</para></exception>
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
    /// <returns>A freshly allocated array of exactly <see cref="SparseFormat.SparseHeaderSize"/> little-endian bytes.
    /// <para>新分配的、恰好 <see cref="SparseFormat.SparseHeaderSize"/> 字节的小端数组。</para></returns>
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
    /// <returns><see langword="true"/> when the magic matches, the major version is 1, both header sizes meet their minimums and the block size is a positive multiple of 4; otherwise <see langword="false"/>. Field bounds are never cross-checked against the chunk stream here.
    /// <para>当魔数匹配、主版本为 1、两个头部大小均达到下限且块大小为正的 4 的倍数时返回 <see langword="true"/>；否则返回 <see langword="false"/>。此处不将字段边界与数据块流交叉校验。</para></returns>
    public bool IsValid()
    {
        return Magic == SparseFormat.SparseHeaderMagic &&
               MajorVersion == 1 &&
               FileHeaderSize >= SparseFormat.SparseHeaderSize &&
               ChunkHeaderSize >= SparseFormat.ChunkHeaderSize &&
               BlockSize > 0 && BlockSize % 4 == 0;
    }
}