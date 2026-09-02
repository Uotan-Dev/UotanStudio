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
    /// 是否使用压缩（pcluster 边界对齐块大小）。压缩算法见 <see cref="Algorithm"/>。
    /// <para>Whether to use compression (pcluster boundaries aligned to the block size). The
    /// algorithm is selected via <see cref="Algorithm"/>.</para>
    /// </summary>
    public bool Compress { get; set; } = true;

    /// <summary>
    /// 压缩算法（默认 LZ4）。<see cref="Compress"/> 关闭时该值不影响输出（仅写入
    /// s_available_compr_algs 为 0）。
    /// <para>Compression algorithm (LZ4 by default). When <see cref="Compress"/> is off this value
    /// does not affect the output (s_available_compr_algs is written as 0).</para>
    /// </summary>
    public ErofsCompressionAlgorithm Algorithm { get; set; } = ErofsCompressionAlgorithm.Lz4;

    /// <summary>
    /// 压缩级别 1-9：LZ4 映射为快速（1-4）/高压缩（5-9）模式；LZMA/DEFLATE/ZSTD 映射为
    /// 各自的码率级别。
    /// <para>Compression level 1-9: mapped to LZ4 fast (1-4) / high-compression (5-9) modes, or to
    /// the rate level of the selected codec (LZMA/DEFLATE/ZSTD).</para>
    /// </summary>
    public int CompressionLevel { get; set; } = 1;

    /// <summary>
    /// 是否对压缩文件使用紧凑（amortized）索引写端而非 legacy full 8B 索引。
    /// 默认 false（legacy full，与 mkfs 旧布局一致且已被广泛验证）；开启后压缩文件
    /// 使用 COMPRESSED_COMPACT datalayout 与 4B amortized 位打包索引。
    /// <para>Whether compressed files use the compacted (amortized) index writer instead of the
    /// legacy full 8-byte index. Defaults to false (legacy full, the well-validated mkfs-era
    /// layout); when enabled, compressed files use the COMPRESSED_COMPACT datalayout with 4-byte
    /// amortized bit-packed indexes.</para>
    /// </summary>
    public bool UseCompactedIndexes { get; set; }

    /// <summary>
    /// 卷名（写入超级块 s_volume_name，截断或 NUL 填充至 16 字节）。
    /// <para>Volume name (written to the superblock s_volume_name, truncated or NUL padded to 16
    /// bytes).</para>
    /// </summary>
    public string VolumeName { get; set; } = "erofs";

    /// <summary>
    /// 可选的按文件 xattr 注入：键为镜像内相对路径（'/' 分隔，如 "system/etc/hosts"），
    /// 值为该文件应写入的 xattr 条目列表。条目名称为完整名称（如 "security.selinux"）。
    /// 构建器把这些 xattr 编码为 inline ibody 并设置 inode 的 xattr 字段；未列出的文件不带
    /// xattr。为 null 时所有文件均不带 xattr（与旧行为一致）。
    /// <para>Optional per-file xattr injection: keys are image-relative paths ('/' separated,
    /// e.g. "system/etc/hosts"), values are the xattr entries to write for that file. Entry names
    /// are full names (e.g. "security.selinux"). The builder encodes them as an inline ibody and
    /// sets the inode's xattr fields; files not listed carry no xattr. Null means no file carries
    /// any xattr (the legacy behavior).</para>
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<ErofsXattrEntry>>? FileXattrs { get; set; }

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
        if (!Enum.IsDefined(Algorithm))
        {
            throw new ArgumentOutOfRangeException(nameof(Algorithm), Algorithm,
                "Unknown EROFS compression algorithm.");
        }

        if (string.IsNullOrEmpty(VolumeName))
        {
            VolumeName = "erofs";
        }
    }
}