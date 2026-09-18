namespace UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs;

public sealed class ErofsOptions
{
    public int BlockSize { get; set; } = 4096;

    public bool Compress { get; set; } = true;

    public ErofsCompressionAlgorithm Algorithm { get; set; } = ErofsCompressionAlgorithm.Lz4;

    public int CompressionLevel { get; set; } = 1;

    /// <summary>The maximum physical compressed cluster (pcluster) size in bytes; zero selects the default.
    /// <para>最大物理压缩簇（pcluster）字节数；0 表示使用默认值。</para>
    /// A single pcluster may cover several logical clusters; a larger pcluster lets LZ4 use its full
    /// 64 KiB history window across block boundaries, which is what keeps the output close to the
    /// source image. Collapsing this to one filesystem block recompresses each 4 KiB independently and
    /// noticeably inflates the image.
    /// <para>一个 pcluster 可覆盖多个逻辑簇；加大的 pcluster 让 LZ4 能跨块使用完整 64 KiB 历史窗口，
    /// 使输出接近源镜像。若把该值收缩为一个文件系统块，则会独立重压每个 4 KiB 而显著膨胀镜像。</para>
    /// </summary>
    public int MaxPclusterSize { get; set; }

    /// <summary>MicroLZMA dictionary size in bytes, as declared in the compression configuration.</summary>
    public int DictionarySize { get; set; } = 8 << 20;

    /// <summary>DEFLATE or Zstandard window bits; zero selects the codec default.</summary>
    public int WindowBits { get; set; }

    /// <summary>LZ4 maximum match distance; zero denotes the upstream 65535-byte default.</summary>
    public ushort Lz4MaxDistance { get; set; }

    /// <summary>Optional source epoch and nanoseconds used by compact inodes.</summary>
    public ulong? BuildTime { get; set; }

    /// <summary>Nanoseconds associated with the filesystem epoch.</summary>
    public uint BuildTimeNanoseconds { get; set; }

    /// <summary>Build time offset from the epoch in the current EROFS superblock layout.</summary>
    public uint BuildTimeOffset { get; set; }

    /// <summary>Whether to write the upstream superblock checksum feature.</summary>
    public bool SuperblockChecksum { get; set; } = true;

    internal Dictionary<ulong, ErofsSourceInode>? SourceInodes { get; set; }
    internal Dictionary<string, ErofsSourceInode>? SourceInodesByPath { get; set; }
    internal Dictionary<ErofsCompressionAlgorithm, ErofsOptions>? SourceCompressionOptions { get; set; }
    internal bool SourceMtimeFeature { get; set; }
    internal bool SourceXattrFilterFeature { get; set; }
    internal byte[]? SourceVolumeNameBytes { get; set; }
    internal CancellationToken CancellationToken { get; set; }
    internal bool OptimizeCompression { get; set; }

    public bool UseCompactedIndexes { get; set; } = true;

    /// <summary>
    /// Produces a legacy (pre-5.13) EROFS image without the big-pcluster / COMPR_CFGS incompatible
    /// feature, making the output mountable by older kernels (Linux 5.4 and 5.10).
    /// <para>生成 legacy（pre-5.13）erofs 镜像，不声明 big-pcluster / COMPR_CFGS 不兼容特性，
    /// 使输出可被较旧内核（Linux 5.4 与 5.10）挂载。</para>
    /// Enabling it forces the legacy layout: LZ4 only, <see cref="MaxPclusterSize"/> equal to the block
    /// size (so every pcluster spans a single logical cluster), and full-size (non-compacted) indexes,
    /// because compacted indexes and big pclusters were introduced together in Linux 5.13.
    /// <para>开启时强制 legacy 布局：仅 LZ4、<see cref="MaxPclusterSize"/> 等于块大小（每个 pcluster
    /// 仅覆盖一个逻辑簇）、使用完整（非紧凑）索引——紧凑索引与 big pcluster 均在 Linux 5.13 才引入。</para>
    /// </summary>
    public bool LegacyCompatibility { get; set; }

    /// <summary>Optional filesystem UUID; extracted images retain their original identity.</summary>
    public byte[]? Uuid { get; set; }

    public string VolumeName { get; set; } = "erofs";

    public IReadOnlyDictionary<string, IReadOnlyList<ErofsXattrEntry>>? FileXattrs { get; set; }

    public bool TailPacking { get; set; } = true;

    /// <summary>Stores the final compressed cluster beside its indexes when it fits in a metadata block.</summary>
    public bool CompressedTailPacking { get; set; }

    public bool Fragments { get; set; }

    public bool Dedup { get; set; }

    public bool Metabox { get; set; }

    public int ExtraDeviceCount { get; set; }

    /// <summary>
    /// Optional path to an Android-style <c>fs_config</c> file (<c>path uid gid mode capabilities</c>)
    /// applied over the source tree; matching entries override the scanned uid/gid/mode.
    /// <para>可选的 Android 风格 <c>fs_config</c> 文件路径（<c>path uid gid mode capabilities</c>），
    /// 覆盖源树扫描结果：命中的条目以文件中的 uid/gid/mode 为准。</para>
    /// </summary>
    public string? FsConfigPath { get; set; }

    /// <summary>
    /// Optional path to an Android-style <c>file_contexts</c> file (<c>path label</c>) applied over
    /// the source tree; matching entries are stored as <c>security.selinux</c> xattrs.
    /// <para>可选的 Android 风格 <c>file_contexts</c> 文件路径（<c>path label</c>），
    /// 命中的条目以 <c>security.selinux</c> xattr 写入镜像。</para>
    /// </summary>
    public string? FileContextsPath { get; set; }

    /// <summary>Preloaded override tables; when set they take precedence over the path-based sources above.</summary>
    internal IReadOnlyDictionary<string, SharedKernel.FsConfigEntry>? FsConfig { get; set; }
    internal IReadOnlyDictionary<string, string>? FileContexts { get; set; }

    /// <summary>
    /// Validates and normalizes the options.
    /// <para>校验并规整选项。</para>
    /// </summary>
    internal void Validate()
    {
        if (BlockSize is < 512 or > 65536 || (BlockSize & (BlockSize - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(BlockSize),
                "BlockSize must be a power of two between 512 and 65536 bytes.");
        }

        CompressionLevel = Math.Clamp(CompressionLevel, 1, 9);
        if (!Enum.IsDefined(Algorithm))
        {
            throw new ArgumentOutOfRangeException(nameof(Algorithm), Algorithm,
                "Unknown EROFS compression algorithm.");
        }

        if (MaxPclusterSize == 0) MaxPclusterSize = ErofsCompression.MaxPclusterSize;
        // Legacy (pre-5.13) images restrict compressed pclusters to a single logical cluster and
        // drop compacted indexes, whose on-disk support only landed together with big pclusters.
        // legacy（pre-5.13）镜像把压缩 pcluster 限制为单个逻辑簇，并关闭与 big pcluster 同时引入
        // 的紧凑索引。
        if (LegacyCompatibility)
        {
            Algorithm = ErofsCompressionAlgorithm.Lz4;
            MaxPclusterSize = BlockSize;
            UseCompactedIndexes = false;
            // Linux 5.4 only understands the LZ4_0_PADDING incompatible bit; fragments,
            // ztailpacking, dedupe and metabox are newer incompatible features that would
            // make the image unmountable even with the big-pcluster bit cleared.
            // Linux 5.4 只认识 LZ4_0_PADDING 这一个 incompat 位；fragments、ztailpacking、
            // dedupe 和 metabox 都是更新的 incompat 特性，即使清掉 big-pcluster 位也会导致挂载失败。
            CompressedTailPacking = false;
            Fragments = false;
            Dedup = false;
            Metabox = false;
            // Per-node planning runs on the codec-specific option objects (node.CompressionOptions),
            // so they must inherit the legacy constraints too; otherwise a preserved rebuild
            // recompresses edited nodes with modern semantics and merges the unaligned tail into a
            // compressed pcluster, which pre-big-pcluster kernels reject with
            // "failed to decompress ... out[...]".
            // 逐节点规划使用逐 codec 的选项对象（node.CompressionOptions），它们也必须继承 legacy
            // 约束；否则保留重建会用现代语义重压编辑过的节点，把非整簇尾部并入压缩 pcluster，
            // 被 pre-big-pcluster 内核以 "failed to decompress ... out[...]" 拒绝。
            if (SourceCompressionOptions is { } codecOptions)
            {
                foreach (var codec in codecOptions.Values)
                {
                    codec.LegacyCompatibility = true;
                    codec.MaxPclusterSize = codec.BlockSize > 0 ? codec.BlockSize : BlockSize;
                    codec.UseCompactedIndexes = false;
                    codec.CompressedTailPacking = false;
                    codec.Fragments = false;
                    codec.Dedup = false;
                    codec.Metabox = false;
                }
            }
        }

        if (MaxPclusterSize < BlockSize || MaxPclusterSize > ErofsCompression.MaxPclusterSize || MaxPclusterSize % BlockSize != 0)
            throw new ArgumentOutOfRangeException(nameof(MaxPclusterSize));
        if (DictionarySize is < 4096 or > 8 << 20)
            throw new ArgumentOutOfRangeException(nameof(DictionarySize));
        if (BuildTimeNanoseconds >= 1_000_000_000) throw new ArgumentOutOfRangeException(nameof(BuildTimeNanoseconds));
        if (Algorithm == ErofsCompressionAlgorithm.Deflate && WindowBits != 0 && WindowBits is < 8 or > 15)
            throw new ArgumentOutOfRangeException(nameof(WindowBits));
        if (Algorithm == ErofsCompressionAlgorithm.Zstd && WindowBits != 0 && WindowBits is < 10 or > 20)
            throw new ArgumentOutOfRangeException(nameof(WindowBits));
        if (Compress && UseCompactedIndexes && BlockSize > 16384)
            throw new NotSupportedException("Compacted EROFS indexes require logical clusters no larger than 16384 bytes.");

        if (string.IsNullOrEmpty(VolumeName) && SharedKernel.FileSystemEditPlan.Current is null)
        {
            VolumeName = "erofs";
        }

        if (ExtraDeviceCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ExtraDeviceCount), ExtraDeviceCount,
                "ExtraDeviceCount must be non-negative.");
        }

        // The builder has no input path for backing device images; writing an all-zero device
        // table would silently produce an image whose extra devices are unreadable. Refuse
        // explicitly until multi-device output is actually implemented.
        // 打包器没有外部设备镜像的输入路径；写全零设备表会静默产出额外设备不可读的镜像。
        // 在真正实现多设备输出前显式拒绝。
        if (ExtraDeviceCount > 0)
        {
            throw new NotSupportedException(
                "EROFS multi-device output is not implemented: ExtraDeviceCount must be 0.");
        }


        if (Fragments)
        {
            TailPacking = false;
        }


        if (Metabox)
        {
            TailPacking = false;
        }
    }
}

internal sealed record ErofsSourceInode(ulong Mtime, uint MtimeNanoseconds, bool Extended,
    bool Compressed, ErofsCompressionAlgorithm Algorithm, bool CompactIndexes, bool InlineData, int LogicalClusterSize,
    string? Path = null);