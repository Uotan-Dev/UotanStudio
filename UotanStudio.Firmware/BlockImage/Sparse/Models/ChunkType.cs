namespace UotanStudio.Firmware.BlockImage.Sparse.Models;

/// <summary>
/// AOSP-standard sparse chunk type identifiers stored in the 16-bit chunk header type field.
/// <para>存于 16 位数据块头部类型字段中的 AOSP 标准稀疏数据块类型标识。</para>
/// </summary>
public enum ChunkType : ushort
{
    /// <summary>Chunk carrying literal block data of <c>ChunkSize * BlockSize</c> bytes. <para>携带 <c>ChunkSize * BlockSize</c> 字节实际块数据的块。</para></summary>
    Raw = 0xCAC1,

    /// <summary>Chunk repeating a stored little-endian 4-byte pattern across its blocks. <para>在其覆盖的块中重复存储的小端 4 字节模式的块。</para></summary>
    Fill = 0xCAC2,

    /// <summary>Chunk whose blocks expand to zeros without storing payload. <para>其块在不存储载荷的情况下展开为零的块。</para></summary>
    DontCare = 0xCAC3,

    /// <summary>Trailing chunk storing the CRC32 checksum of all preceding expanded data. <para>存储此前全部展开数据 CRC32 校验和的尾部块。</para></summary>
    Crc32 = 0xCAC4
}