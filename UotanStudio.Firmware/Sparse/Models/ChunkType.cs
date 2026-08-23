namespace UotanStudio.Firmware.Sparse;

/// <summary>
/// Represents the type of a sparse chunk.
/// <para>表示稀疏数据块的类型。</para>
/// </summary>
public enum ChunkType : ushort
{
    /// <summary>
    /// Raw data — the chunk payload contains the actual block bytes.
    /// <para>原始数据 — 数据块载荷包含实际的块字节数据。</para>
    /// </summary>
    Raw = 0xCAC1,

    /// <summary>
    /// Fill data — the chunk payload is a single 4-byte pattern repeated across blocks.
    /// <para>填充数据 — 数据块载荷是一个 4 字节模式，在所有块中重复。</para>
    /// </summary>
    Fill = 0xCAC2,

    /// <summary>
    /// "Don't care" data — the chunk has no payload; these blocks are skipped.
    /// <para>"不关心"数据 — 数据块无载荷；这些块将被跳过。</para>
    /// </summary>
    DontCare = 0xCAC3,

    /// <summary>
    /// CRC32 checksum — the chunk payload contains a 4-byte CRC32 value.
    /// <para>CRC32 校验和 — 数据块载荷包含一个 4 字节的 CRC32 值。</para>
    /// </summary>
    Crc32 = 0xCAC4
}