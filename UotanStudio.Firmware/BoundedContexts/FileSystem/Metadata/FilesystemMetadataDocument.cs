using System.Text.Json.Serialization;
using UotanStudio.Firmware.SharedKernel;

namespace UotanStudio.Firmware.BoundedContexts.FileSystem.Metadata;

/// <summary>
/// Machine-readable mirror of the Android-style filesystem sidecars, persisted beside
/// <c>manifest.json</c> so repacking prefers structured tables over fragile flat-text parsing.
/// <para>Android 风格文件系统附属元数据的机器可读镜像，与 <c>manifest.json</c> 同目录存放，
/// 使重打包优先使用结构化表而非脆弱的扁平文本解析。</para>
/// </summary>
internal sealed class FilesystemMetadataDocument
{
    /// <summary>Gets the schema version.<para>获取模式版本。</para></summary>
    public int Version { get; set; } = 1;
    /// <summary>Gets the partition name.<para>获取分区名。</para></summary>
    public string Partition { get; set; } = "";
    /// <summary>Gets the SELinux label table keyed by image path.<para>获取以镜像路径为键的 SELinux 标签表。</para></summary>
    public Dictionary<string, string> FileContexts { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Gets the POSIX attribute table keyed by image path.<para>获取以镜像路径为键的 POSIX 属性表。</para></summary>
    public Dictionary<string, FsConfigEntry> FsConfig { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Gets the partition key-value table (superblock/format keys).<para>获取分区键值表（superblock/格式键）。</para></summary>
    public Dictionary<string, string> PartitionInfo { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Gets the source rebuild parameters captured at extraction.<para>获取解包时采集的源重建参数。</para></summary>
    public RebuildParameters? Rebuild { get; set; }
}

/// <summary>
/// Parameters captured from the source image at extraction so a tree can be rebuilt without retaining
/// the original image. Physical layout is regenerated; identity is inherited where the format allows.
/// <para>解包时从源镜像采集的参数，使无需保留原镜像即可重建目录树。物理布局重新生成；
/// 在格式允许处继承镜像身份。</para>
/// </summary>
internal sealed class RebuildParameters
{
    /// <summary>Gets the source format name (<c>EXT4</c>/<c>EROFS</c>/<c>F2FS</c>).<para>获取源格式名（EXT4/EROFS/F2FS）。</para></summary>
    public string? Format { get; set; }
    /// <summary>Gets the inner filesystem format for sparse images.<para>获取稀疏镜像的内部文件系统格式。</para></summary>
    public string? InnerFormat { get; set; }
    /// <summary>Gets the filesystem block size in bytes.<para>获取文件系统块大小（字节）。</para></summary>
    public int? BlockSize { get; set; }
    /// <summary>Gets the sparse-image container block size in bytes.<para>获取稀疏镜像容器块大小（字节）。</para></summary>
    public int? SparseBlockSize { get; set; }
    /// <summary>Gets the inode size in bytes (EXT4).<para>获取 inode 大小（字节，EXT4）。</para></summary>
    public int? InodeSize { get; set; }
    /// <summary>Gets the EXT4 casefold filename encoding ID, when enabled in the source.</summary>
    public ushort? Ext4Encoding { get; set; }
    /// <summary>Gets the EXT4 casefold encoding flags.</summary>
    public ushort Ext4EncodingFlags { get; set; }
    /// <summary>Gets the captured EXT4 read-only policy.<para>获取捕获的 EXT4 只读策略。</para></summary>
    public bool? Ext4ReadOnly { get; set; }
    /// <summary>Gets whether the source EXT4 shares data blocks.<para>获取源 EXT4 是否共享数据块。</para></summary>
    public bool? Ext4SharedBlocks { get; set; }
    /// <summary>Gets the raw filesystem UUID bytes (serialized as base64).<para>获取原始文件系统 UUID 字节（以 base64 序列化）。</para></summary>
    public byte[]? Uuid { get; set; }
    /// <summary>Gets the volume name.<para>获取卷名。</para></summary>
    public string? VolumeName { get; set; }
    /// <summary>Gets the segment size in bytes (F2FS).<para>获取段大小（字节，F2FS）。</para></summary>
    public long? SegmentSize { get; set; }

    public bool? F2fsCompressionEnabled { get; set; }

    public string? F2fsCompressionAlgorithm { get; set; }
    /// <summary>Gets the original image length in bytes, when known.<para>获取原始镜像字节长度（已知时）。</para></summary>
    public long? SourceLength { get; set; }
    /// <summary>Gets the source filesystem's own byte size (block count times block size), when known.
    /// A partition image is usually padded beyond its filesystem, so this is the size a rebuild has to
    /// be compared against; <see cref="SourceLength"/> includes that padding.
    /// <para>获取源文件系统自身的字节数（块数 × 块大小，已知时）。分区镜像通常会被填充到超出其文件系统的
    /// 长度，因此重建体积应与本字段比较；<see cref="SourceLength"/> 含该填充。</para></summary>
    public long? SourceFilesystemBytes { get; set; }
    /// <summary>Gets the verified EROFS rebuild profile.<para>获取经校验的 EROFS 重建档案。</para></summary>
    public ErofsRebuildParameters? Erofs { get; set; }
}

/// <summary>
/// EROFS rebuild inputs captured from the verified source image: superblock features,
/// compression configuration records and the per-inode storage strategy. Physical layout is
/// regenerated, but these fields keep the rebuilt image semantically consistent with the source.
/// <para>从已校验源镜像采集的 EROFS 重建输入：超级块特性、压缩配置记录与逐 inode 存储策略。
/// 物理布局会重新生成，但这些字段保证重建镜像与源镜像语义一致。</para>
/// </summary>
internal sealed class ErofsRebuildParameters
{
    /// <summary>Gets the filesystem block size in bytes.<para>获取文件系统块大小（字节）。</para></summary>
    public int BlockSize { get; set; } = 4096;
    /// <summary>Gets whether compressed data blocks are emitted.<para>获取是否输出压缩数据块。</para></summary>
    public bool Compress { get; set; }
    /// <summary>Gets the primary compression algorithm used for new inodes.<para>获取新 inode 使用的主压缩算法。</para></summary>
    public string Algorithm { get; set; } = "Lz4";
    /// <summary>Gets the primary encoder level.<para>获取主编码级别。</para></summary>
    public int CompressionLevel { get; set; } = 1;
    /// <summary>Gets whether uncompressed tail data may be stored inline.<para>获取未压缩尾部数据是否可内联存储。</para></summary>
    public bool TailPacking { get; set; }
    /// <summary>Gets whether the final compressed cluster may be stored beside its indexes.<para>获取最后一个压缩簇是否可与其索引同块存储。</para></summary>
    public bool CompressedTailPacking { get; set; }
    /// <summary>Gets whether compact indexes are preferred.<para>获取是否优先使用紧凑索引。</para></summary>
    public bool UseCompactedIndexes { get; set; } = true;
    /// <summary>Gets whether whole-file fragments are enabled.<para>获取是否启用整文件碎片。</para></summary>
    public bool Fragments { get; set; }
    /// <summary>Gets whether fragment deduplication is enabled.<para>获取是否启用碎片去重。</para></summary>
    public bool Dedup { get; set; }
    /// <summary>Gets whether the metabox feature is enabled.<para>获取是否启用 metabox 特性。</para></summary>
    public bool Metabox { get; set; }
    /// <summary>Gets the number of extra devices declared by the source.<para>获取源声明的额外设备数量。</para></summary>
    public int ExtraDeviceCount { get; set; }
    /// <summary>Records the source device table; replayed file data is placed on the primary image.</summary>
    public List<ErofsRebuildDevice> SourceDevices { get; set; } = [];
    /// <summary>Gets whether the superblock checksum feature is written.<para>获取是否写出超级块校验和特性。</para></summary>
    public bool SuperblockChecksum { get; set; } = true;
    /// <summary>Gets whether the COMPAT_MTIME feature is declared.<para>获取是否声明 COMPAT_MTIME 特性。</para></summary>
    public bool MtimeFeature { get; set; }
    /// <summary>Gets whether the COMPAT_XATTRFILTER feature is declared.<para>获取是否声明 COMPAT_XATTRFILTER 特性。</para></summary>
    public bool XattrFilterFeature { get; set; }
    /// <summary>Records whether the source placed shared xattrs in the metabox; the writer may use inline xattrs.</summary>
    public bool SourceSharedEaInMetabox { get; set; }
    /// <summary>Records whether the source metabox container was compressed; output geometry may differ.</summary>
    public bool SourceMetaboxCompressed { get; set; }
    /// <summary>Gets the superblock epoch in seconds.<para>获取超级块 epoch（秒）。</para></summary>
    public ulong? BuildTime { get; set; }
    /// <summary>Gets the superblock fixed nanosecond component.<para>获取超级块固定纳秒分量。</para></summary>
    public uint BuildTimeNanoseconds { get; set; }
    /// <summary>Gets the superblock build-time offset from the epoch.<para>获取超级块相对 epoch 的构建时间偏移。</para></summary>
    public uint BuildTimeOffset { get; set; }
    /// <summary>Gets the raw 16-byte volume label field.<para>获取原始 16 字节卷标字段。</para></summary>
    public byte[]? VolumeNameBytes { get; set; }
    /// <summary>Gets the primary MicroLZMA dictionary size.<para>获取主 MicroLZMA 字典大小。</para></summary>
    public int DictionarySize { get; set; }
    /// <summary>Gets the primary DEFLATE/Zstandard window bits.<para>获取主 DEFLATE/Zstandard 窗口位数。</para></summary>
    public int WindowBits { get; set; }
    /// <summary>Gets the primary LZ4 maximum match distance.<para>获取主 LZ4 最大匹配距离。</para></summary>
    public int Lz4MaxDistance { get; set; }
    /// <summary>Gets the primary maximum physical cluster size.<para>获取主最大物理簇大小。</para></summary>
    public int MaxPclusterSize { get; set; }
    /// <summary>
    /// Gets whether the source image used the legacy (pre-5.13) layout, i.e. no big pclusters and
    /// no compacted indexes, so older (5.4 / 5.10) kernels can still mount it.
    /// <para>获取源镜像是否采用 legacy（pre-5.13）布局，即无 big pcluster 与紧凑索引，
    /// 使较旧内核（5.4 / 5.10）仍能挂载。</para>
    /// </summary>
    public bool LegacyCompatibility { get; set; }
    /// <summary>Gets the declared compression configuration records.<para>获取已声明的压缩配置记录。</para></summary>
    public List<ErofsRebuildCodec> Codecs { get; set; } = [];
    /// <summary>Gets the per-inode source storage strategy.<para>获取逐 inode 源存储策略。</para></summary>
    public List<ErofsRebuildInode> Inodes { get; set; } = [];
}

/// <summary>One EROFS compression configuration record captured from the source image.<para>从源镜像采集的一条 EROFS 压缩配置记录。</para></summary>
internal sealed class ErofsRebuildCodec
{
    /// <summary>Gets the codec algorithm name.<para>获取编解码算法名称。</para></summary>
    public string Algorithm { get; set; } = "";
    /// <summary>Gets the codec block size.<para>获取编解码块大小。</para></summary>
    public int BlockSize { get; set; }
    /// <summary>Gets the maximum physical cluster size.<para>获取最大物理簇大小。</para></summary>
    public int MaxPclusterSize { get; set; }
    /// <summary>Gets the MicroLZMA dictionary size.<para>获取 MicroLZMA 字典大小。</para></summary>
    public int DictionarySize { get; set; }
    /// <summary>Gets the DEFLATE/Zstandard window bits.<para>获取 DEFLATE/Zstandard 窗口位数。</para></summary>
    public int WindowBits { get; set; }
    /// <summary>Gets the LZ4 maximum match distance.<para>获取 LZ4 最大匹配距离。</para></summary>
    public int Lz4MaxDistance { get; set; }
    /// <summary>Gets the encoder level.<para>获取编码级别。</para></summary>
    public int CompressionLevel { get; set; } = 1;
}

internal sealed class ErofsRebuildDevice
{
    public byte[] Tag { get; set; } = [];
    public ulong Blocks { get; set; }
    public ulong UniAddr { get; set; }
}

/// <summary>The source storage strategy of one EROFS inode.<para>一个 EROFS inode 的源存储策略。</para></summary>
internal sealed class ErofsRebuildInode
{
    /// <summary>Gets the source node id.<para>获取源节点号。</para></summary>
    public ulong Nid { get; set; }
    /// <summary>Gets the image-absolute path of the inode, when known. The path is the stable
    /// cross-session key; the nid drifts when files are added or removed.
    /// <para>获取 inode 的镜像内绝对路径（已知时）。路径是跨会话的稳定主键；nid 会随文件增删漂移。</para></summary>
    public string? Path { get; set; }
    /// <summary>Gets the modification time in seconds.<para>获取修改时间（秒）。</para></summary>
    public ulong MTime { get; set; }
    /// <summary>Gets the modification time nanosecond component.<para>获取修改时间纳秒分量。</para></summary>
    public uint MTimeNanoseconds { get; set; }
    /// <summary>Gets whether the source used the extended inode layout.<para>获取源是否使用扩展 inode 布局。</para></summary>
    public bool Extended { get; set; }
    /// <summary>Gets whether the source inode held compressed data.<para>获取源 inode 是否包含压缩数据。</para></summary>
    public bool Compressed { get; set; }
    /// <summary>Gets the codec algorithm name.<para>获取编解码算法名称。</para></summary>
    public string Algorithm { get; set; } = "Lz4";
    /// <summary>Gets whether the source used compact indexes.<para>获取源是否使用紧凑索引。</para></summary>
    public bool CompactIndexes { get; set; }
    /// <summary>Gets whether the source stored tail data inline.<para>获取源是否内联存储尾部数据。</para></summary>
    public bool InlineData { get; set; }
    /// <summary>Gets the logical cluster size in bytes.<para>获取逻辑簇大小（字节）。</para></summary>
    public int LogicalClusterSize { get; set; }
    /// <summary>Gets the original extent cluster size when rebuild uses a different geometry.</summary>
    public int? SourceExtentClusterSize { get; set; }
    /// <summary>Gets the original extent record width in bytes.</summary>
    public int? SourceExtentRecordSize { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(FilesystemMetadataDocument))]
[JsonSerializable(typeof(RebuildParameters))]
[JsonSerializable(typeof(ErofsRebuildParameters))]
internal partial class FilesystemMetadataJsonContext : JsonSerializerContext;