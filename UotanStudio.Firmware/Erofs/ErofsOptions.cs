namespace UotanStudio.Firmware.Erofs;

/// <summary>
/// erofs 打包选项。
/// <para>EROFS packing options.</para>
/// </summary>
public sealed class ErofsOptions
{
    /// <summary>
    /// 块大小（字节，须为不小于 512 的 2 的幂；默认 4096）。
    /// <para>Block size in bytes (a power of two of at least 512; default 4096).</para>
    /// </summary>
    public int BlockSize { get; set; } = 4096;

    /// <summary>
    /// 是否使用 LZ4 压缩（pcluster 边界对齐块大小）。
    /// <para>Whether to use LZ4 compression (pcluster boundaries aligned to the block size).</para>
    /// </summary>
    public bool Compress { get; set; } = true;

    /// <summary>
    /// 压缩级别 1-9，映射到 LZ4 快速（1-4）/高压缩（5-9）模式。
    /// <para>Compression level 1-9, mapped to LZ4 fast (1-4) / high-compression (5-9) modes.</para>
    /// </summary>
    public int CompressionLevel { get; set; } = 1;

    /// <summary>
    /// 卷名（写入超级块 s_volume_name，截断或 NUL 填充至 16 字节）。
    /// <para>Volume name (written to the superblock s_volume_name, truncated or NUL padded to 16
    /// bytes).</para>
    /// </summary>
    public string VolumeName { get; set; } = "erofs";

    /// <summary>
    /// 校验并规整选项。
    /// <para>Validates and normalizes the options.</para>
    /// </summary>
    internal void Validate()
    {
        if (BlockSize < 512 || (BlockSize & (BlockSize - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(BlockSize),
                "BlockSize must be a power of two of at least 512 bytes.");
        }

        CompressionLevel = Math.Clamp(CompressionLevel, 1, 9);
        if (string.IsNullOrEmpty(VolumeName))
        {
            VolumeName = "erofs";
        }
    }
}