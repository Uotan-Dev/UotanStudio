using System.Buffers.Binary;
using System.IO;
using K4os.Compression.LZ4;

namespace UotanStudio.Firmware.Erofs;

/// <summary>
/// erofs 压缩辅助：pcluster 索引结构与 LZ4 编解码（K4os.Compression.LZ4）。
/// <para>EROFS compression helpers: pcluster index structures and LZ4 codec
/// (K4os.Compression.LZ4).</para>
/// </summary>
internal static class ErofsCompression
{
    /// <summary>每个 pcluster 索引条目的大小（8 字节）。Size of one pcluster index entry (8 bytes).</summary>
    public const int PclusterIndexSize = 8;

    /// <summary>索引条目标志：pcluster 以原始（未压缩）方式存储。Index flag: pcluster stored plain (uncompressed).</summary>
    public const byte FlagPlain = 0x01;

    /// <summary>
    /// 计算文件所需 pcluster 数量。pcluster 与逻辑簇同大小（默认等于块大小 4096），
    /// 每个 pcluster 占据一个物理块，pcluster 边界对齐 4KB。
    /// <para>Computes the number of pclusters required for a file. A pcluster has the same size as a
    /// logical cluster (defaulting to the 4096-byte block size); each pcluster occupies one physical
    /// block, so pcluster boundaries are 4KB-aligned.</para>
    /// </summary>
    public static int PclusterCount(ulong size, int clusterSize)
    {
        if (size == 0)
        {
            return 0;
        }

        // Avoid size + clusterSize - 1 overflowing, and require the result to fit an int
        // so callers can bound the index table.
        // 避免 size + clusterSize - 1 溢出；结果须能放入 int，以便调用方约束索引表。
        var count = (size - 1) / (ulong)clusterSize + 1;
        if (count > int.MaxValue)
        {
            throw new InvalidDataException($"Pcluster count {count} exceeds the supported limit of {int.MaxValue}.");
        }

        return (int)count;
    }

    /// <summary>
    /// 将 1-9 的压缩级别映射为 K4os LZ4Level（1-4 为快速模式，5-9 为高压缩模式）。
    /// <para>Maps a 1-9 compression level to a K4os LZ4Level (1-4 fast, 5-9 high compression).</para>
    /// </summary>
    public static LZ4Level ToLz4Level(int compressionLevel)
        => (LZ4Level)Math.Clamp(compressionLevel, 0, (int)LZ4Level.L12_MAX);

    /// <summary>
    /// 压缩单个逻辑簇，返回压缩字节数。
    /// <para>Compresses a single logical cluster, returning the compressed byte count.</para>
    /// </summary>
    public static int Compress(
        byte[] source,
        int sourceIndex,
        int sourceLength,
        byte[] target,
        int targetIndex,
        int targetLength,
        int compressionLevel)
        => LZ4Codec.Encode(source, sourceIndex, sourceLength, target, targetIndex, targetLength, ToLz4Level(compressionLevel));

    /// <summary>
    /// 解压单个 pcluster，返回解压字节数。
    /// <para>Decompresses a single pcluster, returning the decompressed byte count.</para>
    /// </summary>
    public static int Decompress(byte[] source, int sourceIndex, int sourceLength, byte[] target, int targetIndex, int targetLength)
        => LZ4Codec.Decode(source, sourceIndex, sourceLength, target, targetIndex, targetLength);

    /// <summary>
    /// 计算压缩所需的最大输出字节数。
    /// <para>Computes the maximum output size for a given input length.</para>
    /// </summary>
    public static int MaximumOutputSize(int length) => LZ4Codec.MaximumOutputSize(length);
}

/// <summary>
/// pcluster 索引条目（8 字节）：物理块号 + 存储长度 + 标志，构建器按压缩块表顺序写入，
/// 解析器在 inode 之后读取该表。
/// <para>Pcluster index entry (8 bytes): physical block number + stored length + flags. The builder
/// writes the compressed-block table in order right after the inode; the parser reads it from there.</para>
/// </summary>
internal readonly struct PclusterIndex
{
    /// <summary>条目大小。Entry size.</summary>
    public const int Size = 8;

    /// <summary>pcluster 数据起始块号（绝对）。Start block number of the pcluster data.</summary>
    public uint BlockAddress { get; }

    /// <summary>存储数据长度（压缩或原始）。Stored data length (compressed or plain).</summary>
    public ushort CompressedLength { get; }

    /// <summary>标志字节。Flags byte.</summary>
    public byte Flags { get; }

    /// <summary>是否以原始方式存储。Whether stored plain (uncompressed).</summary>
    public bool IsPlain => (Flags & ErofsCompression.FlagPlain) != 0;

    /// <summary>
    /// 创建索引条目。
    /// <para>Creates an index entry.</para>
    /// </summary>
    public PclusterIndex(uint blockAddress, ushort compressedLength, byte flags)
    {
        BlockAddress = blockAddress;
        CompressedLength = compressedLength;
        Flags = flags;
    }

    /// <summary>
    /// 从字节解析索引条目。
    /// <para>Parses an index entry from bytes.</para>
    /// </summary>
    public static PclusterIndex FromBytes(ReadOnlySpan<byte> data)
    {
        if (data.Length < Size)
        {
            throw new FormatException("Pcluster index region too short.");
        }

        return new PclusterIndex(
            BinaryPrimitives.ReadUInt32LittleEndian(data),
            BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(4)),
            data[6]);
    }

    /// <summary>
    /// 将索引条目写入缓冲区（长度至少 8 字节）。
    /// <para>Writes the index entry into a buffer of at least 8 bytes.</para>
    /// </summary>
    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < Size)
        {
            throw new ArgumentException("Pcluster index destination buffer is too small.", nameof(destination));
        }

        destination.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(destination, BlockAddress);
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(4), CompressedLength);
        destination[6] = Flags;
        destination[7] = 0;
    }
}