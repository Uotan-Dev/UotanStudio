namespace UotanStudio.Projects;

/// <summary>
/// Controls how an EXT4 output capacity is selected.
/// <para>控制如何选择 EXT4 输出容量。</para>
/// </summary>
public enum Ext4CapacityPolicy
{
    /// <summary>
    /// Keep the source image capacity; the builder receives no target size.
    /// <para>保留源镜像容量；构建器不会收到目标大小。</para>
    /// </summary>
    Inherit,
    /// <summary>
    /// Use the exact byte capacity stored in <see cref="ProjectSettingsDto.Ext4TargetSizeBytes"/>.
    /// <para>使用 <see cref="ProjectSettingsDto.Ext4TargetSizeBytes"/> 中存储的精确字节容量。</para>
    /// </summary>
    Explicit,
    /// <summary>
    /// Recompute the smallest capacity that fits the rebuilt content.
    /// <para>重新计算可容纳重建内容的最小容量。</para>
    /// </summary>
    Minimize,
}

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
    /// <value>
    /// The stored relative path; <c>string.Empty</c> means no path was recorded.
    /// <para>存储的相对路径；<c>string.Empty</c> 表示未记录路径。</para>
    /// </value>
    public string RelativePath { get; set; } = string.Empty;

    /// <summary>
    /// Image file size in bytes.
    /// <para>镜像文件大小（字节）。</para>
    /// </summary>
    /// <value>Byte count carried by the entry as persisted with the project.
    /// <para>随工程持久化的条目所承载的字节数。</para></value>
    public long SizeBytes { get; set; }

    /// <summary>
    /// Image format identifier (boot/payload/cpio/oppo/sparse/raw/unknown etc.).
    /// <para>镜像格式标识（boot/payload/cpio/oppo/sparse/raw/unknown 等）。</para>
    /// </summary>
    /// <value>
    /// Format token persisted with the entry; the empty string means no format was recognized.
    /// <para>与条目一起持久化的格式标识；空字符串表示未识别出格式。</para>
    /// </value>
    public string Format { get; set; } = string.Empty;
}

/// <summary>
/// Packing settings data transfer object, mapping one-to-one with the packing settings panel fields.
/// <para>打包设置数据传输对象，与打包设置面板的字段一一对应。</para>
/// </summary>
public sealed class ProjectSettingsDto
{
    /// <summary>
    /// Image type ("EXT4", "EROFS" or "F2FS").
    /// <para>镜像类型（"EXT4" 或 "EROFS"）。</para>
    /// </summary>
    /// <value>The selected filesystem type; defaults to "EROFS".
    /// <para>所选文件系统类型；默认为 "EROFS"。</para></value>
    public string ImageType { get; set; } = "EROFS";

    /// <summary>
    /// Image format ("RAW" or "SPARSE").
    /// <para>镜像格式（"RAW" 或 "SPARSE"）。</para>
    /// </summary>
    /// <value>The selected container format; defaults to "RAW".
    /// <para>所选容器格式；默认为 "RAW"。</para></value>
    public string ImageFormat { get; set; } = "RAW";

    /// <summary>
    /// Whether EXT4 is read-write (<c>false</c> means read-only).
    /// <para>EXT4 是否可读写（RO 时为 <c>false</c>）。</para>
    /// </summary>
    /// <value>
    /// <see langword="true"/> builds a writable image; defaults to <see langword="true"/>.
    /// <para><see langword="true"/> 表示构建可写镜像；默认为 <see langword="true"/>。</para>
    /// </value>
    public bool Ext4ReadWrite { get; set; } = true;

    /// <summary>
    /// Whether size calculation is automatic (otherwise the original size is used).
    /// <para>大小计算是否自动（否则使用原大小）。</para>
    /// </summary>
    /// <value>
    /// <see langword="true"/> lets the builder compute the size; defaults to <see langword="true"/>.
    /// <para><see langword="true"/> 表示由构建器计算大小；默认为 <see langword="true"/>。</para>
    /// </value>
    public bool SizeAuto { get; set; } = true;

    /// <summary>
    /// Whether EXT4 is compressed.
    /// <para>EXT4 是否压缩。</para>
    /// </summary>
    /// <value>
    /// <see langword="true"/> marks the image as compressed, <see langword="false"/> as uncompressed,
    /// and <see langword="null"/> stores no selection.
    /// <para><see langword="true"/> 标记镜像为压缩，<see langword="false"/> 为未压缩，
    /// <see langword="null"/> 表示未存储选择。</para>
    /// </value>
    public bool? Ext4Compress { get; set; }

    /// <summary>
    /// Explicit EXT4 capacity policy. Missing values are migrated from <see cref="Ext4Compress"/>.
    /// <para>显式 EXT4 容量策略。缺失值由 <see cref="Ext4Compress"/> 迁移得到。</para>
    /// </summary>
    /// <value>
    /// The stored policy, or <see langword="null"/> when the project saved only a compression flag.
    /// <para>存储的策略；当工程仅保存了压缩标志时为 <see langword="null"/>。</para>
    /// </value>
    public Ext4CapacityPolicy? Ext4CapacityPolicy { get; set; }

    /// <summary>
    /// Exact EXT4 capacity when <see cref="Ext4CapacityPolicy"/> is <see cref="Ext4CapacityPolicy.Explicit"/>.
    /// <para>当 <see cref="Ext4CapacityPolicy"/> 为 <see cref="Ext4CapacityPolicy.Explicit"/> 时的精确 EXT4 容量。</para>
    /// </summary>
    /// <value>
    /// Byte size applied in explicit mode; <see langword="null"/> under the other policies.
    /// <para>显式模式下应用的字节大小；其余策略下为 <see langword="null"/>。</para>
    /// </value>
    public long? Ext4TargetSizeBytes { get; set; }

    /// <summary>
    /// Explicit EXT4 readonly selection. An absent value defers to the read/write field.
    /// <para>显式的 EXT4 只读选择。缺失值以读写字段 <see cref="Ext4ReadWrite"/> 为准。</para>
    /// </summary>
    /// <value>
    /// <see langword="true"/> mounts the image read-only; <see langword="null"/> defers to <see cref="Ext4ReadWrite"/>.
    /// <para><see langword="true"/> 表示镜像只读挂载；<see langword="null"/> 时以 <see cref="Ext4ReadWrite"/> 为准。</para>
    /// </value>
    public bool? Ext4ReadOnly { get; set; }

    /// <summary>
    /// EXT4 data block sharing mode.
    /// <para>EXT4 数据块共享模式。</para>
    /// </summary>
    /// <value>
    /// An enum name parsed case-insensitively; <see langword="null"/> selects the independent mode.
    /// <para>按大小写不敏感解析的枚举名；<see langword="null"/> 表示独立模式。</para>
    /// </value>
    public string? Ext4BlockSharingMode { get; set; }

    /// <summary>
    /// Explicit Android Sparse container output selection.
    /// <para>显式的 Android Sparse 容器输出选择。</para>
    /// </summary>
    /// <value>
    /// <see langword="true"/> requests sparse output; <see langword="null"/> falls back to <see cref="ImageFormat"/>.
    /// <para><see langword="true"/> 表示请求稀疏输出；<see langword="null"/> 时回退到 <see cref="ImageFormat"/>。</para>
    /// </value>
    public bool? SparseOutput { get; set; }

    /// <summary>
    /// EROFS compression algorithm: "lz4", "lz4hc", "lzma", "deflate" or "zstd". The default is the
    /// HC search because <see cref="ErofsCompLevel"/> only feeds that encoder; fast lz4 ignores the
    /// level. Legacy "lz4"/"lz4hc" values remain valid.
    /// <para>EROFS 压缩算法："lz4"、"lz4hc"、"lzma"、"deflate" 或 "zstd"。默认为 HC 搜索，因为
    /// <see cref="ErofsCompLevel"/> 只作用于该编码器；快速 lz4 会忽略等级。旧的 "lz4"/"lz4hc" 值仍然有效。</para>
    /// </summary>
    /// <value>
    /// The algorithm token; an empty or missing value restores the HC search default.
    /// <para>算法标识；空值或缺失时恢复 HC 搜索默认。</para>
    /// </value>
    public string ErofsAlgorithm { get; set; } = "lz4hc";

    /// <summary>
    /// EROFS compression level.
    /// <para>EROFS 压缩等级。</para>
    /// </summary>
    /// <value>The level handed to the HC encoder; defaults to <c>8</c>.
    /// <para>传给 HC 编码器的等级；默认为 <c>8</c>。</para></value>
    public int ErofsCompLevel { get; set; } = 8;

    /// <summary>
    /// EROFS feature declaration edit script, using the feature names the superblock feature table carries: an edit
    /// sets a bit, a <c>!</c>-prefixed edit clears it, and <c>auto</c> keeps both
    /// superblock words derived. The script edits the feature words recorded from the preserved source image,
    /// so an empty value inherits them unchanged.
    /// <para>EROFS 特性声明编辑脚本，使用超级块特性表中的特性名：普通记号置位，加 <c>!</c>
    /// 前缀清除，<c>auto</c> 让两个超级块字保持推导。脚本编辑的是自保留源镜像记录下来的特性字，因此空值表示
    /// 原样继承。</para>
    /// </summary>
    /// <value>
    /// The edit script applied over the inherited declaration; <see langword="null"/> stores no declaration
    /// edit. Tokens outside the feature table fail the build instead of being dropped silently.
    /// <para>应用在被继承声明之上的编辑脚本；<see langword="null"/> 表示未存储声明编辑。超出特性表的记号
    /// 会让构建失败，而不是被静默丢弃。</para>
    /// </value>
    public string? ErofsFeatureOverrides { get; set; }

    /// <summary>
    /// Explicit F2FS compression selection. A missing value inherits source metadata when rebuilding,
    /// or the format default for a new directory.
    /// <para>显式的 F2FS 压缩选择。缺失值在重建时继承源元数据，新建目录时使用格式默认。</para>
    /// </summary>
    /// <value>
    /// <see langword="true"/> forces compression on and <see langword="false"/> forces it off;
    /// <see langword="null"/> stores no selection.
    /// <para><see langword="true"/> 强制启用压缩，<see langword="false"/> 强制关闭；<see langword="null"/> 表示未存储选择。</para>
    /// </value>
    public bool? F2fsCompressionEnabled { get; set; }

    /// <summary>
    /// Explicit F2FS compression algorithm ("Lz4", "Zstd" or "Lzo").
    /// <para>显式的 F2FS 压缩算法（"Lz4"、"Zstd" 或 "Lzo"）。</para>
    /// </summary>
    /// <value>
    /// An algorithm enum name; <see langword="null"/> when no choice was stored.
    /// <para>算法枚举名称；未存储选择时为 <see langword="null"/>。</para>
    /// </value>
    public string? F2fsCompressionAlgorithm { get; set; }

    /// <summary>
    /// Explicit F2FS compression level.
    /// <para>显式的 F2FS 压缩等级。</para>
    /// </summary>
    /// <value>
    /// The level used together with the compression selection; <see langword="null"/> when unset.
    /// <para>与压缩选择一同使用的等级；未设置时为 <see langword="null"/>。</para>
    /// </value>
    public int? F2fsCompressionLevel { get; set; }

    /// <summary>
    /// Opt-in native Android F2FS profile (ro, project_quota, extra_attr).
    /// <para>可选启用的原生 Android F2FS 配置（ro、project_quota、extra_attr）。</para>
    /// </summary>
    /// <value>
    /// <see langword="true"/> enables the profile; <see langword="null"/> stores no selection.
    /// <para><see langword="true"/> 启用该配置；<see langword="null"/> 表示未存储选择。</para>
    /// </value>
    public bool? F2fsAndroidNativeProfile { get; set; }

    /// <summary>
    /// Fixed raw-image capacity in bytes for the native Android profile.
    /// <para>原生 Android 配置下固定的 RAW 镜像容量（字节）。</para>
    /// </summary>
    /// <value>
    /// The byte capacity the profile builds at; <see langword="null"/> when unset.
    /// <para>该配置构建时使用的字节容量；未设置时为 <see langword="null"/>。</para>
    /// </value>
    public long? F2fsAndroidImageSizeBytes { get; set; }

    /// <summary>
    /// WSL distribution containing f2fs-tools on Windows.
    /// <para>Windows 上包含 f2fs-tools 的 WSL 发行版。</para>
    /// </summary>
    /// <value>
    /// The distribution name used to run f2fs-tools; <see langword="null"/> when unset.
    /// <para>用于运行 f2fs-tools 的发行版名称；未设置时为 <see langword="null"/>。</para>
    /// </value>
    public string? F2fsNativeWslDistribution { get; set; }

    /// <summary>
    /// SUPER partition size (string).
    /// <para>SUPER 分区大小（字符串）。</para>
    /// </summary>
    /// <value>Byte-size string handed to the SUPER build steps; defaults to <c>"9126805504"</c>.
    /// <para>传给 SUPER 构建步骤的字节大小字符串；默认为 <c>"9126805504"</c>。</para></value>
    public string SuperSize { get; set; } = "9126805504";

    /// <summary>
    /// SUPER partition cluster name.
    /// <para>SUPER 分区簇名。</para>
    /// </summary>
    /// <value>The dynamic-partition group name written into the super metadata.
    /// <para>写入 super 元数据的动态分区组名。</para></value>
    public string SuperName { get; set; } = "qti_dynamic_partitions";

    /// <summary>
    /// SUPER format ("RAW" or "SPARSE").
    /// <para>SUPER 格式（"RAW" 或 "SPARSE"）。</para>
    /// </summary>
    /// <value>The container format of the produced super image; defaults to "RAW".
    /// <para>所生成 super 镜像的容器格式；默认为 "RAW"。</para></value>
    public string SuperFormat { get; set; } = "RAW";

    /// <summary>
    /// BR compression level.
    /// <para>BR 压缩等级。</para>
    /// </summary>
    /// <value>The Brotli level applied when producing <c>.new.dat.br</c> files; defaults to <c>3</c>.
    /// <para>生成 <c>.new.dat.br</c> 文件时使用的 Brotli 等级；默认为 <c>3</c>。</para></value>
    public int BrCompLevel { get; set; } = 3;

    /// <summary>
    /// UTC timestamp (string).
    /// <para>UTC 时间戳（字符串）。</para>
    /// </summary>
    /// <value>Unix-seconds string fixed into generated image metadata; defaults to <c>"1230768000"</c>.
    /// <para>固化到生成镜像元数据中的 Unix 秒字符串；默认为 <c>"1230768000"</c>。</para></value>
    public string UtcTime { get; set; } = "1230768000";

    /// <summary>
    /// DAT segment count (string).
    /// <para>DAT 分段数量（字符串）。</para>
    /// </summary>
    /// <value>Segment count applied when splitting DAT output; defaults to <c>"15"</c>.
    /// <para>拆分 DAT 输出时使用的分段数量；默认为 <c>"15"</c>。</para></value>
    public string DatSegments { get; set; } = "15";
}