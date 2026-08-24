namespace UotanStudio.Firmware.Sparse;

/// <summary>
/// Defines constants for the Android sparse file format.
/// <para>定义 Android 稀疏文件格式的常量。</para>
/// </summary>
public static class SparseFormat
{
    /// <summary>
    /// Magic number for the sparse file header.
    /// <para>稀疏文件头部的魔术数字。</para>
    /// </summary>
    public const uint SparseHeaderMagic = 0xed26ff3a;

    /// <summary>
    /// Size of the sparse file header in bytes.
    /// <para>稀疏文件头部的字节大小。</para>
    /// </summary>
    public const ushort SparseHeaderSize = 28;

    /// <summary>
    /// Size of the chunk header in bytes.
    /// <para>数据块头部的字节大小。</para>
    /// </summary>
    public const ushort ChunkHeaderSize = 12;

    /// <summary>
    /// Maximum chunk payload size in bytes (64 MB).
    /// <para>数据块载荷的最大字节大小（64 MB）。</para>
    /// </summary>
    public const uint MaxChunkDataSize = 64 * 1024 * 1024;
}