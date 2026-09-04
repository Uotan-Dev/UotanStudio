namespace UotanStudio.Firmware.F2fs;

/// <summary>
/// f2fs 构建选项。
/// <para>F2FS packing options.</para>
/// </summary>
public sealed class F2fsOptions
{
    /// <summary>
    /// 文件系统块大小（字节）。f2fs 磁盘布局固定以 4096 字节节点页为基准（F2FS_BLKSIZE），
    /// 因此仅支持 4096；默认 4096。
    /// <para>Filesystem block size in bytes. F2FS on-disk structures are fixed to 4096-byte node
    /// pages (F2FS_BLKSIZE), so only 4096 is supported; default 4096.</para>
    /// </summary>
    public int BlockSize { get; init; } = 4096;

    /// <summary>
    /// 段大小（字节）。f2fs 段必须包含 512 个 4K 块（SIT valid_map 为 64 字节 = 512 位），
    /// 因此仅支持 2 MB；默认 2 MB。
    /// <para>Segment size in bytes. An F2FS segment must hold 512 4K blocks (the SIT valid_map is
    /// 64 bytes = 512 bits), so only 2 MB is supported; default 2 MB.</para>
    /// </summary>
    public long SegmentSize { get; init; } = 2L * 1024 * 1024;

    /// <summary>
    /// 最小总段数（0 表示按需自动计算）。提供的值小于自动计算值时按自动值处理；
    /// 用于固定镜像大小。
    /// <para>Minimum total segment count (0 = computed automatically from the source data). A value
    /// below the automatic count is ignored; useful for fixing the image size.</para>
    /// </summary>
    public int SegmentCount { get; init; }

    /// <summary>
    /// 卷名（写入超级块 s_volume_name，截断或 NUL 填充至 512 字符）。
    /// <para>Volume name (written to the superblock s_volume_name, truncated or NUL padded to 512
    /// characters).</para>
    /// </summary>
    public string VolumeName { get; init; } = "f2fs";

    /// <summary>
    /// 启用稀疏：全零文件块以空洞（块号 0）写入而非占用物理块；读回时补零。
    /// <para>When enabled, all-zero file blocks are emitted as holes (block address 0) instead of
    /// allocated data blocks; they read back as zeros.</para>
    /// </summary>
    public bool EnableSparse { get; init; } = true;

    /// <summary>
    /// 卷 UUID（写入超级块 s_uuid）。null 时自动生成随机 UUID。
    /// <para>Volume UUID (written to the superblock s_uuid). When null, a random UUID is generated.</para>
    /// </summary>
    public Guid? Uuid { get; init; }

    /// <summary>
    /// 启用压缩：尺寸大于一个簇（&gt; 64 KB，即至少跨越 2 个簇）的常规文件以 64 KB 簇为单位
    /// 压缩数据块；完整簇仅当压缩后占用的物理块更少时才存储压缩版本，不值得压缩的簇与
    /// 末尾部分簇按普通块存储，压缩文件内支持压缩簇与普通簇混合。
    /// <para>Enables compression: regular files larger than one cluster (&gt; 64 KB, i.e. spanning at
    /// least two clusters) compress their data in 64 KB cluster units; a full cluster is stored
    /// compressed only when it then uses fewer physical blocks, while incompressible clusters and
    /// the partial trailing cluster are stored as ordinary blocks. Compressed files may mix
    /// compressed and ordinary clusters.</para>
    /// </summary>
    public bool EnableCompression { get; init; }

    /// <summary>
    /// 压缩算法（默认 LZ4）。
    /// <para>Compression algorithm (default LZ4).</para>
    /// </summary>
    public CompressionAlgorithm CompressionAlgorithm { get; init; } = CompressionAlgorithm.Lz4;

    /// <summary>
    /// 压缩级别（0 = 算法默认值；LZ4 支持 0/1/3/5/9，ZSTD 支持 1–22）。
    /// <para>Compression level (0 = algorithm default; LZ4 supports 0/1/3/5/9, ZSTD 1–22).</para>
    /// </summary>
    public int CompressionLevel { get; init; }

    /// <summary>
    /// 校验并规整选项。
    /// <para>Validates and normalizes the options.</para>
    /// </summary>
    internal void Validate()
    {
        if (BlockSize != 4096)
        {
            throw new ArgumentOutOfRangeException(nameof(BlockSize), "F2FS block size must be 4096 bytes.");
        }

        if (SegmentSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(SegmentSize), "Segment size must be positive.");
        }

        if (SegmentSize / BlockSize != 512)
        {
            throw new ArgumentOutOfRangeException(
                nameof(SegmentSize),
                "F2FS segment size must yield exactly 512 blocks (2 MB for 4096-byte blocks).");
        }

        if (SegmentCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(SegmentCount), "Segment count must not be negative.");
        }

        if (CompressionLevel < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(CompressionLevel), "Compression level must not be negative.");
        }
    }
}