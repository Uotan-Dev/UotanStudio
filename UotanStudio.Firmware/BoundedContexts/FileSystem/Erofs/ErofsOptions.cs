using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Compression;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Models;
namespace UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs;

public sealed class ErofsOptions
{
    public int BlockSize { get; set; } = 4096;

    public bool Compress { get; set; } = true;

    public ErofsCompressionAlgorithm Algorithm { get; set; } = ErofsCompressionAlgorithm.Lz4;

    public int CompressionLevel { get; set; } = 1;

    /// <summary>The maximum physical compressed cluster (pcluster) size in bytes; zero selects the default.
    /// <para>最大物理压缩簇（pcluster）字节数；0 表示使用默认值。</para>
    /// This caps how many bytes a pcluster's compressed stream may occupy. Keeping LZ4's output close
    /// to a source image is instead governed by how much *input* one pcluster covers: a byte-granular
    /// (dense) planner fills each physical block from arbitrarily placed pclusters, while a planner
    /// that restarts every pcluster on a logical cluster boundary wastes the remainder of each block.
    /// <para>该值只限制 pcluster 压缩流的字节数。让 LZ4 输出接近源镜像的关键其实是单个 pcluster 覆盖
    /// 多少输入：按字节粒度（dense）的规划器会用任意起点的 pcluster 填满每个物理块，而每个 pcluster
    /// 都在逻辑簇边界重启的规划器会浪费每块剩余空间。</para>
    /// </summary>
    public int MaxPclusterSize { get; set; }

    /// <summary>MicroLZMA dictionary size in bytes, as declared in the compression configuration.</summary>
    public int DictionarySize { get; set; } = 8 << 20;

    /// <summary>DEFLATE or Zstandard window bits; zero selects the codec default.</summary>
    public int WindowBits { get; set; }

    /// <summary>LZ4 maximum match distance; zero denotes the upstream 65535-byte default.</summary>
    public ushort Lz4MaxDistance { get; set; }

    /// <summary>
    /// Selects the LZ4HC block encoder (bounded search over match candidates) instead of the fast
    /// single-hash matcher for <see cref="ErofsCompressionAlgorithm.Lz4"/> payloads.
    /// <para>为 <see cref="ErofsCompressionAlgorithm.Lz4"/> 数据选择 LZ4HC 块编码器（在候选匹配上做
    /// 有界搜索），而不是快速的单哈希匹配器。</para>
    /// EROFS stores no encoder identity: the on-disk codec is LZ4 either way, and the kernel only sees
    /// the resulting block, so this flag trades build time for density exactly like upstream's
    /// <c>-zlz4</c> versus <c>-zlz4hc</c>. It does not change the declared algorithm.
    /// <see cref="CompressionLevel"/> selects the HC search depth and is clamped to 1-9, so the
    /// optimal-parse levels 10-12 are not reachable through these options.
    /// <para>EROFS 不记录编码器身份：磁盘上的编解码器始终是 LZ4，内核只见最终数据块，因此该开关与上游
    /// <c>-zlz4</c> 与 <c>-zlz4hc</c> 的取舍一样，只用构建时间换取压缩率，不改变声明的算法。
    /// <see cref="CompressionLevel"/> 决定 HC 搜索深度并被限制在 1-9，因此最优分析级（10-12）无法通过
    /// 本选项到达。</para>
    /// </summary>
    public bool Lz4HighCompression { get; set; }

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
    /// size (so each pcluster's stream occupies one physical block), and full-size (non-compacted)
    /// indexes, because compacted indexes and big pclusters were introduced together in Linux 5.13.
    /// Pclusters may still start and end at byte-granular offsets: both index formats store
    /// di_clusterofs, so an older kernel can address them.
    /// <para>开启时强制 legacy 布局：仅 LZ4、<see cref="MaxPclusterSize"/> 等于块大小（每个 pcluster 的
    /// 压缩流只占一个物理块）、使用完整（非紧凑）索引——紧凑索引与 big pcluster 均在 Linux 5.13 才
    /// 引入。pcluster 仍可按字节粒度起讫：两种索引格式都保存 di_clusterofs，旧内核可以寻址。</para>
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
    /// Optional descriptors for the extra devices referenced by the device table. One entry per
    /// <see cref="ExtraDeviceCount"/> device (device ids 1..N). The builder writes each tag, block
    /// count and unified address into the on-disk device table; file data still lands on device 0,
    /// so this declares the layout rather than placing blobs on the secondary images.
    /// <para>可选的额外设备描述符，对应设备表引用的设备（id 1..N），每项对应一个
    /// <see cref="ExtraDeviceCount"/>。打包器把每个设备的 tag、块数与统一地址写入磁盘设备表；
    /// 文件数据仍落在设备 0，因此本选项声明的是布局，而非把数据块放到二级设备上。</para>
    /// </summary>
    public IReadOnlyList<ErofsDeviceSpec>? ExtraDeviceImages { get; set; }

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
    internal IReadOnlyDictionary<string, UotanStudio.Firmware.BoundedContexts.FileSystem.Metadata.FsConfigEntry>? FsConfig { get; set; }
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
        // Legacy (pre-5.13) images keep every compressed pcluster inside one physical block and drop
        // compacted indexes, whose on-disk support only landed together with big pclusters.
        // legacy（pre-5.13）镜像把每个压缩 pcluster 限制在一个物理块内，并关闭与 big pcluster 同时
        // 引入的紧凑索引。
        if (LegacyCompatibility)
        {
            // Legacy (pre-5.13) EROFS has no encoding other than LZ4 with a fixed distance; declaring a
            // modern codec here would either be silently dropped or emit a kernel-rejected superblock.
            // Refuse explicitly so callers surface the conflict to the user instead of coercing.
            // legacy（pre-5.13）erofs 仅支持固定距离的 LZ4；在此声明现代编解码器要么被静默丢弃，要么
            // 产出内核拒绝的超级块。显式报错，让调用方向用户暴露冲突而不是内部降型。
            if (Algorithm != ErofsCompressionAlgorithm.Lz4)
                throw new NotSupportedException(
                    $"EROFS legacy (pre-5.13) layout supports only LZ4; {Algorithm} requires the modern big-pcluster feature.");
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
            // recompresses edited nodes with modern semantics and emits big pclusters, fragments or an
            // inline tail that the older decoder cannot use.
            // 逐节点规划使用逐 codec 的选项对象（node.CompressionOptions），它们也必须继承 legacy
            // 约束；否则保留重建会用现代语义重压编辑过的节点，产出旧解码器无法使用的 big pcluster、
            // fragment 或内联尾部。
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

        if (string.IsNullOrEmpty(VolumeName) && UotanStudio.Firmware.BoundedContexts.FileSystem.Rebuilding.FileSystemEditPlan.Current is null)
        {
            VolumeName = "erofs";
        }

        if (ExtraDeviceCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ExtraDeviceCount), ExtraDeviceCount,
                "ExtraDeviceCount must be non-negative.");
        }

        // Multi-device output requires an explicit descriptor for every declared extra device; the
        // builder has no way to infer a device's tag or size, and writing an all-zero device table
        // would silently produce an image whose extra devices are unreadable. Rebuilds preserve only
        // the extra-device count, never the backing device contents, so a rebuild profile that declares
        // extra devices is rejected here exactly as before.
        // 多设备输出要求为每个声明的额外设备提供显式描述符；打包器无法推断设备的 tag 或大小，
        // 写全零设备表会静默产出额外设备不可读的镜像。重建仅保留额外设备数量、从不保留其内容，
        // 因此声明了额外设备的重建档案仍会在此被拒绝，与之前一致。
        if (ExtraDeviceCount > 0)
        {
            if (ExtraDeviceImages is null || ExtraDeviceImages.Count != ExtraDeviceCount)
                throw new NotSupportedException(
                    "EROFS multi-device output requires one ExtraDeviceImages descriptor per declared device.");
            for (var i = 0; i < ExtraDeviceImages.Count; i++)
            {
                var spec = ExtraDeviceImages[i];
                if (string.IsNullOrEmpty(spec.Tag) || System.Text.Encoding.UTF8.GetByteCount(spec.Tag) >= ErofsDeviceSlot.TagSize)
                    throw new ArgumentOutOfRangeException(nameof(ExtraDeviceImages),
                        $"Extra device {i} needs a non-empty tag shorter than {ErofsDeviceSlot.TagSize} bytes.");
                if (spec.SizeBytes <= 0 || spec.SizeBytes % BlockSize != 0)
                    throw new ArgumentOutOfRangeException(nameof(ExtraDeviceImages),
                        $"Extra device {i} size must be a positive, block-aligned byte count.");
            }
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

/// <summary>
/// Declares one extra device for the EROFS device table: a NUL-safe tag and the device size in bytes
/// (block-aligned). The device table records the tag and the block count derived from <paramref name="SizeBytes"/>.
/// <para>声明设备表中的一个额外设备：以 NUL 结尾的 tag 与以字节表示、块对齐的设备大小。
/// 设备表记录该 tag 以及由 <paramref name="SizeBytes"/> 推导出的块数。</para>
/// </summary>
public sealed record ErofsDeviceSpec(string Tag, long SizeBytes);