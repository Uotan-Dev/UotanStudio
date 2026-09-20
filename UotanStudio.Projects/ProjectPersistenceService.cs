namespace UotanStudio.Services.Projects;

/// <summary>
/// Image entry data transfer object, carrying the image's project-relative path and metadata.
/// <para>镜像条目数据传输对象，承载镜像在项目内的相对路径与元信息。</para>
/// </summary>
public sealed class ImageEntryDto
{
    /// <summary>
    /// Image path relative to the project root directory.
    /// <para>相对工程根目录的镜像路径。</para>
    /// </summary>
    public string RelativePath { get; set; } = string.Empty;

    /// <summary>
    /// Image file size in bytes.
    /// <para>镜像文件大小（字节）。</para>
    /// </summary>
    public long SizeBytes { get; set; }

    /// <summary>
    /// Image format identifier (boot/payload/cpio/oppo/sparse/raw/unknown etc.).
    /// <para>镜像格式标识（boot/payload/cpio/oppo/sparse/raw/unknown 等）。</para>
    /// </summary>
    public string Format { get; set; } = string.Empty;
}

/// <summary>
/// Packing settings data transfer object, mapping one-to-one with the packing settings panel fields.
/// <para>打包设置数据传输对象，与打包设置面板的字段一一对应。</para>
/// </summary>
public sealed class ProjectSettingsDto
{
    /// <summary>
    /// Image type ("EXT4" or "EROFS").
    /// <para>镜像类型（"EXT4" 或 "EROFS"）。</para>
    /// </summary>
    public string ImageType { get; set; } = "EROFS";

    /// <summary>
    /// Image format ("RAW" or "SPARSE").
    /// <para>镜像格式（"RAW" 或 "SPARSE"）。</para>
    /// </summary>
    public string ImageFormat { get; set; } = "RAW";

    /// <summary>
    /// Whether EXT4 is read-write (<c>false</c> means read-only).
    /// <para>EXT4 是否可读写（RO 时为 <c>false</c>）。</para>
    /// </summary>
    public bool Ext4ReadWrite { get; set; } = true;

    /// <summary>
    /// Whether size calculation is automatic (otherwise the original size is used).
    /// <para>大小计算是否自动（否则使用原大小）。</para>
    /// </summary>
    public bool SizeAuto { get; set; } = true;

    /// <summary>
    /// Whether EXT4 is compressed.
    /// <para>EXT4 是否压缩。</para>
    /// </summary>
    public bool Ext4Compress { get; set; } = true;

    /// <summary>
    /// EROFS compression algorithm: "lz4", "lz4hc", "lzma", "deflate" or "zstd". The default is the
    /// HC search because <see cref="ErofsCompLevel"/> only feeds that encoder; fast lz4 ignores the
    /// level. Legacy "lz4"/"lz4hc" values remain valid.
    /// <para>EROFS 压缩算法："lz4"、"lz4hc"、"lzma"、"deflate" 或 "zstd"。默认为 HC 搜索，因为
    /// <see cref="ErofsCompLevel"/> 只作用于该编码器；快速 lz4 会忽略等级。旧的 "lz4"/"lz4hc" 值仍然有效。</para>
    /// </summary>
    public string ErofsAlgorithm { get; set; } = "lz4hc";

    /// <summary>
    /// EROFS compression level.
    /// <para>EROFS 压缩等级。</para>
    /// </summary>
    public int ErofsCompLevel { get; set; } = 8;

    /// <summary>
    /// SUPER partition size (string).
    /// <para>SUPER 分区大小（字符串）。</para>
    /// </summary>
    public string SuperSize { get; set; } = "9126805504";

    /// <summary>
    /// SUPER partition cluster name.
    /// <para>SUPER 分区簇名。</para>
    /// </summary>
    public string SuperName { get; set; } = "qti_dynamic_partitions";

    /// <summary>
    /// SUPER format ("RAW" or "SPARSE").
    /// <para>SUPER 格式（"RAW" 或 "SPARSE"）。</para>
    /// </summary>
    public string SuperFormat { get; set; } = "RAW";

    /// <summary>
    /// BR compression level.
    /// <para>BR 压缩等级。</para>
    /// </summary>
    public int BrCompLevel { get; set; } = 3;

    /// <summary>
    /// UTC timestamp (string).
    /// <para>UTC 时间戳（字符串）。</para>
    /// </summary>
    public string UtcTime { get; set; } = "1230768000";

    /// <summary>
    /// DAT segment count (string).
    /// <para>DAT 分段数量（字符串）。</para>
    /// </summary>
    public string DatSegments { get; set; } = "15";
}