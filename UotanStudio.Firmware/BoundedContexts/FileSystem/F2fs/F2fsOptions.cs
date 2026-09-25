using UotanStudio.Firmware.BoundedContexts.FileSystem.F2fs.Compression;
namespace UotanStudio.Firmware.BoundedContexts.FileSystem.F2fs;

/// <summary>
/// Options controlling how a directory tree is built into an F2FS image.
/// <para>控制目录树如何构建为 F2FS 镜像的选项。</para>
/// </summary>
public sealed class F2fsOptions
{
    /// <summary>Use native f2fs-tools for Android ro, project_quota and extra_attr features.
    /// <para>针对 Android ro、project_quota 与 extra_attr 特性使用原生 f2fs-tools。</para></summary>
    /// <value><see langword="true"/> to route the build through native tools (WSL on Windows);
    /// requires <see cref="AndroidImageSizeBytes"/> and a zero <see cref="CompressionLevel"/>.
    /// <para><see langword="true"/> 表示经由原生工具构建（Windows 上通过 WSL）；
    /// 要求提供 <see cref="AndroidImageSizeBytes"/> 且 <see cref="CompressionLevel"/> 为 0。</para></value>
    public bool AndroidNativeProfile { get; init; }

    /// <summary>Required fixed raw-image capacity for the Android native profile.
    /// <para>Android 原生配置所要求的固定原始镜像容量。</para></summary>
    /// <value>Capacity in bytes; <see langword="null"/> outside the native profile, where the builder
    /// sizes the image from the content instead.
    /// <para>字节为单位的容量；非原生配置时为 <see langword="null"/>，此时由构建器按内容定容。</para></value>
    public long? AndroidImageSizeBytes { get; init; }

    /// <summary>Explicit WSL distribution when native tools are run from Windows.
    /// <para>在 Windows 上运行原生工具时显式指定的 WSL 发行版。</para></summary>
    /// <value>Distribution name passed to <c>wsl.exe</c>; <see langword="null"/> leaves the default
    /// distribution, and native tool runs on Windows require an explicit one.
    /// <para>传给 <c>wsl.exe</c> 的发行版名；为 <see langword="null"/> 时使用默认发行版，而 Windows 上的原生工具运行要求显式指定。</para></value>
    public string? NativeWslDistribution { get; init; }

    /// <summary>
    /// F2FS block size in bytes.
    /// <para>F2FS 块大小（字节）。</para>
    /// </summary>
    /// <value>Defaults to <c>4096</c>; validation rejects any other value.
    /// <para>默认 <c>4096</c>；校验时拒绝其他值。</para></value>
    public int BlockSize { get; init; } = 4096;

    /// <summary>
    /// F2FS segment size in bytes.
    /// <para>F2FS 段大小（字节）。</para>
    /// </summary>
    /// <value>Defaults to 2 MiB; must yield exactly 512 blocks for the configured block size.
    /// <para>默认 2 MiB；按所配置块大小必须恰好折合 512 个块。</para></value>
    public long SegmentSize { get; init; } = 2L * 1024 * 1024;

    /// <summary>
    /// Requested total segment count for the image.
    /// <para>请求的镜像总段数。</para>
    /// </summary>
    /// <value><c>0</c> (default) lets the builder size the segment table from the content; a positive
    /// value enlarges the image to at least that many segments, and a negative value fails validation.
    /// <para><c>0</c>（默认）由构建器按内容确定段表；正值将镜像扩展到至少该段数，负值校验失败。</para></value>
    public int SegmentCount { get; init; }

    /// <summary>
    /// Volume label written to the superblock.
    /// <para>写入超级块的卷标。</para>
    /// </summary>
    /// <value>Defaults to <c>"f2fs"</c>.
    /// <para>默认 <c>"f2fs"</c>。</para></value>
    public string VolumeName { get; init; } = "f2fs";

    /// <summary>
    /// Whether zero-filled data blocks are detected and left unallocated (sparse holes).
    /// <para>是否检测全零数据块并将其保留为未分配（稀疏洞）。</para>
    /// </summary>
    /// <value><see langword="true"/> by default; when <see langword="false"/> every data block is allocated.
    /// <para>默认 <see langword="true"/>；为 <see langword="false"/> 时分配所有数据块。</para></value>
    public bool EnableSparse { get; init; } = true;

    /// <summary>
    /// Filesystem UUID written to the superblock.
    /// <para>写入超级块的文件系统 UUID。</para>
    /// </summary>
    /// <value><see langword="null"/> (default) generates a fresh random GUID at build time.
    /// <para>为 <see langword="null"/>（默认）时在构建时生成新的随机 GUID。</para></value>
    public Guid? Uuid { get; init; }

    /// <summary>
    /// Whether cluster compression is requested for regular file data.
    /// <para>是否对常规文件数据请求簇压缩。</para>
    /// </summary>
    /// <value><see langword="false"/> by default; when enabled, files larger than one cluster are
    /// compressed cluster by cluster and only stored compressed where the payload shrinks.
    /// <para>默认 <see langword="false"/>；启用后，大于一簇的文件按簇压缩，仅在载荷确实变小时以压缩形式存储。</para></value>
    public bool EnableCompression { get; init; }

    /// <summary>
    /// Codec used for cluster compression.
    /// <para>簇压缩使用的编解码器。</para>
    /// </summary>
    /// <value>Defaults to <see cref="CompressionAlgorithm.Lz4"/>; ignored when <see cref="EnableCompression"/> is <see langword="false"/>.
    /// <para>默认 <see cref="CompressionAlgorithm.Lz4"/>；<see cref="EnableCompression"/> 为 <see langword="false"/> 时忽略。</para></value>
    public CompressionAlgorithm CompressionAlgorithm { get; init; } = CompressionAlgorithm.Lz4;

    /// <summary>
    /// Codec-specific compression level hint.
    /// <para>编解码器相关的压缩级别提示。</para>
    /// </summary>
    /// <value><c>0</c> (default) lets each codec pick its own level; negative values fail validation
    /// and the native Android profile requires <c>0</c>.
    /// <para><c>0</c>（默认）由各编解码器自选级别；负值校验失败，Android 原生配置要求 <c>0</c>。</para></value>
    public int CompressionLevel { get; init; }

    /// <summary>
    /// Token observed throughout image construction.
    /// <para>贯穿整个镜像构建过程监视的取消令牌。</para>
    /// </summary>
    /// <value>The default (none) token makes a build non-cancellable; cancellation surfaces as
    /// <see cref="OperationCanceledException"/> with no partially published output.
    /// <para>默认（无）令牌使构建不可取消；取消时抛出 <see cref="OperationCanceledException"/>，且不会发布部分输出。</para></value>
    public CancellationToken CancellationToken { get; init; }

    /// <summary>
    /// Optional path to an Android-style <c>fs_config</c> file (<c>path uid gid mode capabilities</c>)
    /// applied over the source tree; matching entries override the scanned uid/gid/mode.
    /// <para>可选的 Android 风格 <c>fs_config</c> 文件路径（<c>path uid gid mode capabilities</c>），
    /// 覆盖源树扫描结果：命中的条目以文件中的 uid/gid/mode 为准。</para>
    /// </summary>
    /// <value>The file path, or <see langword="null"/> when no owner or mode overrides are applied.
    /// <para>文件路径；不应用任何属主或模式覆盖时为 <see langword="null"/>。</para></value>
    public string? FsConfigPath { get; init; }

    /// <summary>
    /// Optional path to an Android-style <c>file_contexts</c> file (<c>path label</c>) applied over
    /// the source tree; matching entries are stored as <c>security.selinux</c> extra attributes.
    /// <para>可选的 Android 风格 <c>file_contexts</c> 文件路径（<c>path label</c>），
    /// 命中的条目以 <c>security.selinux</c> 扩展属性写入镜像。</para>
    /// </summary>
    /// <value>The file path, or <see langword="null"/> when no SELinux labels are applied.
    /// <para>文件路径；不应用任何 SELinux 标签时为 <see langword="null"/>。</para></value>
    public string? FileContextsPath { get; init; }

    /// <summary>Binary Linux xattrs keyed by image-relative path; root uses an empty path.
    /// <para>以镜像内相对路径为键的二进制 Linux 扩展属性；根目录使用空路径。</para></summary>
    /// <value>Path-keyed attribute maps encoded into each inode's xattr area; <see langword="null"/>
    /// (default) supplies no binary overrides.
    /// <para>按路径索引的属性表，编码进各 inode 的 xattr 区域；为 <see langword="null"/>（默认）时不提供二进制覆盖。</para></value>
    public IReadOnlyDictionary<string, Dictionary<string, byte[]>>? FileXattrs { get; init; }

    /// <summary>Preloaded override tables; when set they take precedence over the path-based sources above.</summary>
    internal IReadOnlyDictionary<string, UotanStudio.Firmware.BoundedContexts.FileSystem.Metadata.FsConfigEntry>? FsConfig { get; set; }
    internal IReadOnlyDictionary<string, string>? FileContexts { get; set; }

    /// <summary>
    /// Validates and normalizes the options.
    /// <para>校验并规整选项。</para>
    /// </summary>
    internal void Validate()
    {
        if (AndroidNativeProfile && AndroidImageSizeBytes is null)
        {
            throw new ArgumentException("Android F2FS profile requires an explicit image capacity.");
        }
        if (AndroidNativeProfile && CompressionLevel != 0)
        {
            throw new ArgumentException("Native sload.f2fs does not accept a compression level.");
        }
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