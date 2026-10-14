namespace UotanStudio.Firmware.FileSystem.Metadata;

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
    /// <summary>Gets the EXT4 casefold filename encoding ID, when enabled in the source. <para>获取 EXT4 大小写折叠文件名编码 ID（源中启用时）。</para></summary>
    public ushort? Ext4Encoding { get; set; }
    /// <summary>Gets the EXT4 casefold encoding flags. <para>获取 EXT4 大小写折叠编码标志。</para></summary>
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
    /// <summary>
    /// Gets the recorded all-fragments advise: whether a file shorter than one filesystem block is stored whole
    /// in the packed fragment inode on top of the tail fragments that <see cref="Fragments"/> licenses. It is
    /// the shape <c>mkfs.erofs -E all-fragments</c> selects, and it only has an effect while
    /// <see cref="Fragments"/> is enabled.
    /// <para>获取记录的 all-fragments advise：在 <see cref="Fragments"/> 许可的尾部碎片之外，是否把不足一个
    /// 文件系统块的文件整份存放在 packed fragment inode 里。这正是 <c>mkfs.erofs -E all-fragments</c> 选用的
    /// 形态，且只在 <see cref="Fragments"/> 开启时生效。</para>
    /// </summary>
    /// <value>The recorded flag, or <see langword="null"/> when the snapshot carries no entry for it; a null
    /// value is read together with <see cref="Fragments"/>, because a profile without this dimension stores
    /// whole sub-block files in the packed inode whenever <see cref="Fragments"/> is enabled.
    /// <para>记录的值，或档案未携带该项时的 <see langword="null"/>；空值与 <see cref="Fragments"/> 一并读取，
    /// 因为不携带本维度的档案在 <see cref="Fragments"/> 开启时同样把整份不足一块的文件存入 packed inode。</para></value>
    public bool? AllFilesAsFragments { get; set; }
    /// <summary>Gets whether fragment deduplication is enabled.<para>获取是否启用碎片去重。</para></summary>
    public bool Dedup { get; set; }
    /// <summary>
    /// Gets the resolved <c>ErofsDedupeMode</c> member name for the dedupe dimension observed in the source
    /// image, so a rebuild keeps comparing what the source compared instead of falling back to the whole-file
    /// default. The name is stored as text so the document stays locale-free.
    /// <para>获取源镜像中观测到的去重维度解析后的 <c>ErofsDedupeMode</c> 成员名，使重建继续比较源所比较的
    /// 对象，而不是回落到整文件默认值。名称以文本保存，使文档不依赖语言环境。</para>
    /// </summary>
    /// <value><c>WholeFile</c> or <c>Pcluster</c>, or <see langword="null"/> when the snapshot predates this
    /// field; a null value is restored as <c>WholeFile</c>, which is the repository's existing behavior.
    /// <para>取值为 <c>WholeFile</c> 或 <c>Pcluster</c>；快照早于该字段时为 <see langword="null"/>，
    /// 此时还原为 <c>WholeFile</c>，即仓库既有行为。</para></value>
    public string? DedupeMode { get; set; }
    /// <summary>Gets whether the metabox feature is enabled.<para>获取是否启用 metabox 特性。</para></summary>
    public bool Metabox { get; set; }
    /// <summary>
    /// Gets whether the rebuilt image has to carry a metabox container even when the planned layout selects no
    /// resident inode, so a declaration holding <c>EROFS_FEATURE_INCOMPAT_METABOX</c> always has the superblock
    /// slot and <c>s_metabox_nid</c> behind it.
    /// <para>获取重建镜像是否必须在规划的布局未选出常驻 inode 时仍建立 metabox 容器，使携带
    /// <c>EROFS_FEATURE_INCOMPAT_METABOX</c> 的声明总是有超级块槽与 <c>s_metabox_nid</c> 在其背后。</para>
    /// </summary>
    /// <value>The recorded requirement, which has no effect while <see cref="Metabox"/> is off, or
    /// <see langword="null"/> when the snapshot carries no entry for it; a null value is restored as no
    /// requirement.
    /// <para>记录的要求，在 <see cref="Metabox"/> 关闭时不起作用，或档案未携带该项时的
    /// <see langword="null"/>；空值还原为无该要求。</para></value>
    public bool? MetaboxRequired { get; set; }
    /// <summary>Gets the number of extra devices declared by the source.<para>获取源声明的额外设备数量。</para></summary>
    public int ExtraDeviceCount { get; set; }
    /// <summary>Records the source device table; replayed file data is placed on the primary image. <para>记录源设备表；重放的文件数据放置在主镜像上。</para></summary>
    public List<ErofsRebuildDevice> SourceDevices { get; set; } = [];
    /// <summary>Gets whether the superblock checksum feature is written.<para>获取是否写出超级块校验和特性。</para></summary>
    public bool SuperblockChecksum { get; set; } = true;
    /// <summary>Gets whether the COMPAT_MTIME feature is declared.<para>获取是否声明 COMPAT_MTIME 特性。</para></summary>
    public bool MtimeFeature { get; set; }
    /// <summary>Gets whether the COMPAT_XATTRFILTER feature is declared.<para>获取是否声明 COMPAT_XATTRFILTER 特性。</para></summary>
    public bool XattrFilterFeature { get; set; }
    /// <summary>Records whether the source placed shared xattrs in the metabox; the writer may use inline xattrs. <para>记录源是否把共享 xattr 放在 metabox 中；写入器可改用内联 xattr。</para></summary>
    public bool SourceSharedEaInMetabox { get; set; }
    /// <summary>
    /// Gets whether the source image wrote per-inode xattr name filters, so a rebuild recomputes them
    /// against the names it emits instead of losing the dimension.
    /// <para>获取源镜像是否写入了逐 inode 的 xattr 名称过滤器，使重建按自己发出的名称重新计算，而不是
    /// 丢掉该维度。</para>
    /// </summary>
    public bool? XattrNameFilter { get; set; }
    /// <summary>
    /// Gets the long-prefix name the source image recorded content fingerprints under, so a rebuild
    /// re-registers the same prefix and its <c>ishare_xattr_prefix_id</c> resolves again.
    /// <para>获取源镜像记录内容指纹所用的长前缀名称，使重建重新注册同一前缀并让
    /// <c>ishare_xattr_prefix_id</c> 再次可解析。</para>
    /// </summary>
    public string? IshareXattrPrefix { get; set; }
    /// <summary>
    /// Gets whether the source image shared repeated xattr sets, so a rebuild re-runs the sharing pass
    /// rather than leaving every set inline.
    /// <para>获取源镜像是否共享了重复的 xattr 集合，使重建重跑共享化遍历而不是让每个集合都内联。</para>
    /// </summary>
    public bool? IshareXattrs { get; set; }
    /// <summary>Records whether the source metabox container was compressed; output geometry may differ. <para>记录源 metabox 容器是否被压缩；输出几何信息可能不同。</para></summary>
    public bool SourceMetaboxCompressed { get; set; }
    /// <summary>
    /// Gets the resolved <c>ErofsCompressionAlgorithm</c> member name of the algorithm the source image
    /// encoded its metabox container with. The container is an ordinary compressed inode, so the value is
    /// recovered from the container's own map header rather than from the primary compression declaration; a
    /// rebuild that re-encodes the container has to select the same codec for the recorded payload to stay
    /// reproducible. The name is stored as text so the document stays locale-free.
    /// <para>获取源镜像编码其 metabox 容器所用算法解析后的 <c>ErofsCompressionAlgorithm</c> 成员名。容器本质是
    /// 普通压缩 inode，因此该值取自容器自身的映射头而非主压缩声明；重新编码容器的重建必须选用同一编解码器，
    /// 记录的负载才保持可复现。名称以文本保存，使文档不依赖语言环境。</para>
    /// </summary>
    /// <value><c>Lz4</c>, <c>Lzma</c>, <c>Deflate</c> or <c>Zstd</c>, or <see langword="null"/> when the source
    /// metabox container carried no compression or when the snapshot predates this field; a null value leaves
    /// the codec to the writer's own resolution.
    /// <para>取值为 <c>Lz4</c>、<c>Lzma</c>、<c>Deflate</c> 或 <c>Zstd</c>；源 metabox 容器未压缩或快照早于该
    /// 字段时为 <see langword="null"/>，此时编解码器由写入方自行解析。</para></value>
    public string? SourceMetaboxAlgorithm { get; set; }
    /// <summary>
    /// Gets the resolved <c>ErofsDataLayout</c> member name of the storage layout the source image recorded
    /// for its metabox container inode, so a rebuild can tell an inline or flat container from a compressed one
    /// instead of inferring the shape from the compression flag alone.
    /// <para>获取源镜像为 metabox 容器 inode 记录的存储布局解析后的 <c>ErofsDataLayout</c> 成员名，使重建能区分
    /// 内联、平坦与压缩容器，而不是只凭压缩标志推断其形态。</para>
    /// </summary>
    /// <value><c>Flat</c>, <c>Compressed</c>, <c>Inline</c>, <c>CompactIndexes</c> or <c>ChunkBased</c>, or
    /// <see langword="null"/> when the source declares no metabox container or when the snapshot predates this
    /// field. The value is recorded so the container shape survives the document; no writer-side option
    /// consumes it yet, and the compression flag beside it stays the dimension a rebuild replays.
    /// <para>取值为 <c>Flat</c>、<c>Compressed</c>、<c>Inline</c>、<c>CompactIndexes</c> 或 <c>ChunkBased</c>；
    /// 源未声明 metabox 容器或快照早于该字段时为 <see langword="null"/>。记录该值是为了让容器形态随文档留存；
    /// 目前尚无写入侧选项消费它，重建重放的仍是与之并列的压缩标志。</para></value>
    public string? SourceMetaboxDataLayout { get; set; }
    /// <summary>
    /// Gets the start block of the source image's metadata area (<c>s_meta_blkaddr</c>), so a rebuild places
    /// its inode table in the same separate zone the source used instead of silently returning the metadata
    /// to the head of the image.
    /// <para>获取源镜像元数据区的起始块（<c>s_meta_blkaddr</c>），使重建把 inode 表放回源所用的同一独立
    /// 区块，而不是静默把元数据退回镜像头部之后。</para>
    /// </summary>
    /// <value>The recorded block number, or <see langword="null"/> when the snapshot predates this field;
    /// a null value is restored as no metadata zone, which is the repository's existing behavior.
    /// <para>记录的块号；快照早于该字段时为 <see langword="null"/>，此时还原为无元数据区，即仓库既有行为。</para></value>
    public uint? MetaBlkAddr { get; set; }
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
    /// 使较旧内核（5.4 / 5.10）仍可挂载。</para>
    /// </summary>
    public bool LegacyCompatibility { get; set; }
    /// <summary>
    /// Gets the raw <c>s_feature_compat</c> word read from the verified source superblock, so a rebuild
    /// declares the source's compat bits instead of the writer's own derivation.
    /// <para>获取从已校验源超级块读出的原始 <c>s_feature_compat</c> 字，使重建声明源镜像的 compat 位而不是
    /// 写入方自行推导的位。</para>
    /// </summary>
    /// <value>The on-disk compat mask, or <see langword="null"/> when the snapshot predates this field; a
    /// null value leaves the compat word to the derived declaration.
    /// <para>磁盘上的 compat 掩码；快照早于该字段时为 <see langword="null"/>，此时 compat 字回退到推导式
    /// 声明。</para></value>
    public uint? FeatureCompat { get; set; }
    /// <summary>
    /// Gets the raw <c>s_feature_incompat</c> word read from the verified source superblock. Together with
    /// <see cref="FeatureCompat"/> it is the authority the rebuilt image has to declare verbatim.
    /// <para>获取从已校验源超级块读出的原始 <c>s_feature_incompat</c> 字，与 <see cref="FeatureCompat"/>
    /// 共同构成重建镜像必须逐字声明的权威。</para>
    /// </summary>
    /// <value>The on-disk incompat mask with the bits the extraction could not honour already removed, or
    /// <see langword="null"/> when the snapshot predates this field; a null value leaves the incompat word
    /// to the derived declaration.
    /// <para>磁盘上的 incompat 掩码，其中解包阶段无法兑现的位已先行移除；快照早于该字段时为
    /// <see langword="null"/>，此时 incompat 字回退到推导式声明。</para></value>
    public uint? FeatureIncompat { get; set; }
    /// <summary>
    /// Gets the resolved <c>ErofsTriStateFeature</c> member name for the dot-omitted inode advise dimension
    /// observed in the source image. The name is stored as text so the document stays locale-free.
    /// <para>获取源镜像中观测到的 dot-omitted inode advise 维度解析后的 <c>ErofsTriStateFeature</c>
    /// 成员名。名称以文本保存，使文档不依赖语言环境。</para>
    /// </summary>
    /// <value><c>Auto</c>, <c>Disabled</c> or <c>Enabled</c>, or <see langword="null"/> when the snapshot
    /// predates this field; a null value leaves the dimension to the writer's own resolution.
    /// <para>取值为 <c>Auto</c>、<c>Disabled</c> 或 <c>Enabled</c>；快照早于该字段时为
    /// <see langword="null"/>，此时该维度由写入方自行解析。</para></value>
    public string? DotOmitted { get; set; }
    /// <summary>
    /// Gets whether the source image targeted a kernel that defines the nlink-one / dot-omitted advise bit.
    /// The bit only exists on newer kernels, so the source being observed is the only durable evidence that the
    /// target defines it; without this dimension a rebuild would silently drop the bit from every inode.
    /// <para>获取源镜像是否面向定义了 nlink-one / dot-omitted advise 位的内核。该位只存在于较新内核上，
    /// 因此"观测到源镜像"是目标定义该位的唯一持久证据；缺少该维度时，重建会在每个 inode 上静默丢弃该位。</para>
    /// </summary>
    /// <value>The recorded statement about the target, or <see langword="null"/> when the snapshot predates
    /// this field; a null value is restored as unsupported.
    /// <para>记录的目标陈述；快照早于该字段时为 <see langword="null"/>，此时还原为不支持。</para></value>
    public bool? NlinkOneAdviseSupported { get; set; }
    /// <summary>
    /// Gets the incompat bits the extraction removed from the source declaration: either the layout policy
    /// demanded a bit the source never declared, or the writer cannot re-emit the structures behind the
    /// declared bit.
    /// <para>获取解包阶段从源声明中移除的 incompat 位：或布局策略要求源镜像从未声明的位，或写入方无法重新
    /// 输出该位背后的结构。</para>
    /// </summary>
    /// <value>Raw <c>s_feature_incompat</c> mask of the removed bits, or <see langword="null"/> when the
    /// snapshot predates this field. The declaration in <see cref="FeatureIncompat"/> already excludes
    /// these bits, so the value records why the inherited layout was narrowed.
    /// <para>被移除位的原始 <c>s_feature_incompat</c> 掩码；快照早于该字段时为 <see langword="null"/>。
    /// <see cref="FeatureIncompat"/> 中的声明已排除这些位，本值只记录继承布局被收窄的原因。</para></value>
    public uint? DroppedIncompatBits { get; set; }
    /// <summary>Gets the declared compression configuration records.<para>获取已声明的压缩配置记录。</para></summary>
    public List<ErofsRebuildCodec> Codecs { get; set; } = [];
    /// <summary>Gets the per-inode source storage strategy.<para>获取逐 inode 源存储策略。</para></summary>
    public List<ErofsRebuildInode> Inodes { get; set; } = [];
    /// <summary>Gets the source image's long xattr name prefix table, replayed so xattr names that needed a
    /// long prefix encode against the same table instead of registering new records. It is empty when the
    /// source image declared no long prefixes, or when the snapshot predates this field.
    /// <para>获取源镜像的长 xattr 名称前缀表；重放它使原本依赖长前缀的 xattr 名称按同一张表编码，而不是注册
    /// 新记录。源镜像未声明长前缀、或快照早于该字段时为空表。</para></summary>
    public List<ErofsRebuildXattrPrefix> XattrPrefixes { get; set; } = [];
    /// <summary>
    /// Gets the bounded per-codec compression candidate estimates sampled from the live source parser at extraction. Empty when the rebuild optimizer was disabled, when the source declared no compression configuration record, or when the snapshot predates this field.
    /// <para>获取解包时从在线源解析器采样的、按编解码器区分的有界压缩候选估计。重建优化器被关闭、源未声明压缩配置记录、或快照早于该字段时为空。</para>
    /// </summary>
    public List<ErofsRebuildCompressionEstimate> CompressionEstimates { get; set; } = [];
    /// <summary>
    /// Gets the authoritative source filesystem size in bytes recorded at extraction, from the superblock's 64-bit block count times the block size. The 32-bit block count truncates large images, so the rebuild size search compares against this value.
    /// <para>获取解包时记录的源文件系统权威字节大小，由超级块 64 位块数乘块大小得出。32 位块数会截断大镜像，因此重建的大小搜索与此值比对。</para>
    /// </summary>
    public long? SourceFilesystemBytes { get; set; }
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
    /// <summary>Gets the original extent cluster size when the rebuild uses a different geometry. <para>当重建使用不同几何信息时，获取原始的 extent 簇大小。</para></summary>
    public int? SourceExtentClusterSize { get; set; }
    /// <summary>Gets the original extent record width in bytes. <para>获取原始 extent 记录的字节宽度。</para></summary>
    public int? SourceExtentRecordSize { get; set; }
    /// <summary>
    /// Gets the resolved <c>ErofsFragmentShape</c> member name for the packed-inode placement the source inode
    /// used. The name is stored as text so the document stays locale-free.
    /// <para>获取源 inode 使用的 packed inode 存放形态解析后的 <c>ErofsFragmentShape</c> 成员名。名称以文本
    /// 保存，使文档不依赖语言环境。</para>
    /// </summary>
    /// <value><c>None</c>, <c>WholeFile</c> or <c>Tail</c>, or <see langword="null"/> when the snapshot carries
    /// no entry for it; a null value is read as <c>None</c>.
    /// <para>取值为 <c>None</c>、<c>WholeFile</c> 或 <c>Tail</c>，或档案未携带该项时的
    /// <see langword="null"/>；空值按 <c>None</c> 读取。</para></value>
    public string? FragmentShape { get; set; }
    /// <summary>
    /// Gets the <c>ErofsCompressionAlgorithm</c> member name of the secondary algorithm that the source inode's
    /// HEAD2 index rows name through the high nibble of <c>h_algorithmtype</c>. The name is stored as text so
    /// the document stays locale-free.
    /// <para>获取源 inode 的 HEAD2 索引行经 <c>h_algorithmtype</c> 高四位指名的第二算法的
    /// <c>ErofsCompressionAlgorithm</c> 成员名。名称以文本保存，使文档不依赖语言环境。</para>
    /// </summary>
    /// <value>The secondary algorithm name, or <see langword="null"/> when the source emitted no HEAD2 index
    /// rows or the snapshot carries no entry for it; a null value is restored as no secondary algorithm.
    /// <para>第二算法名称，或源未发出 HEAD2 索引行、档案未携带该项时的 <see langword="null"/>；空值还原为无
    /// 第二算法。</para></value>
    public string? SecondaryAlgorithm { get; set; }
    /// <summary>
    /// Gets the log2 of the chunk size in filesystem blocks for a chunk-based source inode, or zero when the source file was not chunk-based. It replays h_clusterbits of a chunk-based inode, which stores the chunk size where other layouts store a logical cluster size.
    /// <para>获取基于分块的源 inode 的分块大小（以文件系统块为单位的 log2），源文件非分块时为 0。它重放分块 inode 的 h_clusterbits——该字段存放分块大小，而其他布局存放逻辑簇大小。</para>
    /// </summary>
    /// <value>The chunk-size log2, or <c>0</c> for a file that was not chunk-based.
    /// <para>chunk 大小的 log2，或非 chunk-based 文件的 <c>0</c>。</para></value>
    public int ChunkBits { get; set; }
    /// <summary>
    /// Gets the attribute sets the source image recorded for this inode, as name/value pairs. They are carried
    /// as resolved entries rather than as the raw inline body because a body may reference a slot in the source
    /// image's shared xattr area, which a rebuild does not reproduce; the resolved list is what lets the
    /// rebuild re-encode the same set, and re-share it when sharing is requested.
    /// <para>获取源镜像为该 inode 记录的属性集，以名称/值对表示。它们以已解析条目而非原始内联体的形式携带，
    /// 因为内联体可能引用源镜像共享 xattr 区中的槽位，而重建不会复现该区域；正是已解析列表让重建能重新编码
    /// 同一集合，并在请求共享时再次共享它。</para>
    /// </summary>
    /// <value>The recorded attributes in on-disk order, or <see langword="null"/> when the source inode carried
    /// none or the snapshot predates this field.
    /// <para>按磁盘顺序记录的属性；源 inode 未携带或快照早于该字段时为 <see langword="null"/>。</para></value>
    public List<ErofsRebuildXattr>? Xattrs { get; set; }
}

/// <summary>
/// One extended attribute of a source inode: the on-disk name index that selects its prefix and the resolved
/// name and value bytes. The index is stored so a name that carries a long prefix re-encodes against the same
/// prefix record instead of being re-qualified.
/// <para>源 inode 的一条扩展属性：选择其前缀的磁盘名称索引，以及已解析的名称与值字节。保存该索引使携带长前缀
/// 的名称按同一前缀记录重新编码，而不是被重新限定。</para>
/// </summary>
internal sealed class ErofsRebuildXattr
{
    /// <summary>Gets the on-disk <c>e_name_index</c> byte, defaulting to the <c>user.</c> prefix.
    /// <para>获取磁盘 <c>e_name_index</c> 字节，默认为 <c>user.</c> 前缀。</para></summary>
    /// <value>A default prefix index (1..6), or a long-prefix index with the <c>0x80</c> flag set.
    /// <para>默认前缀索引（1..6），或置有 <c>0x80</c> 标志的长前缀索引。</para></value>
    public byte NameIndex { get; set; } = 1;

    /// <summary>Gets the resolved attribute name. <para>获取已解析的属性名称。</para></summary>
    /// <value>Never null; the full name including its prefix, e.g. <c>user.caps</c>.
    /// <para>永不为 null；含前缀的完整名称，例如 <c>user.caps</c>。</para></value>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets the attribute value bytes as a base64 string, because JSON carries no raw bytes.
    /// <para>获取属性值的 base64 字符串，因为 JSON 不携带原始字节。</para></summary>
    /// <value>Standard base64 of the value; an empty string for a zero-length value.
    /// <para>值的标准 base64；零长度值为空字符串。</para></value>
    public string Value { get; set; } = string.Empty;
}

/// <summary>
/// One long-prefix record of the source EROFS xattr prefix table: the built-in short prefix the record
/// extends and the infix bytes that follow it. The pair is what the on-disk table stores, so replaying it
/// lets xattr names encode against the same table instead of registering new records.
/// <para>源 EROFS xattr 前缀表中的一条长前缀记录：记录所扩展的内置短前缀，以及其后的 infix 字节。
/// 该对正是磁盘表所存内容，因此重放它能让 xattr 名称按同一张表编码而不是注册新记录。</para>
/// </summary>
internal sealed class ErofsRebuildXattrPrefix
{
    /// <summary>Gets the built-in short prefix index the record extends, or zero for the internal hidden
    /// namespace. <para>获取记录所扩展的内置短前缀索引，内部隐藏命名空间为 0。</para></summary>
    /// <value>A default prefix index, or <c>0</c> when the record carries the whole prefix itself.
    /// <para>默认前缀索引；记录自带完整前缀时为 <c>0</c>。</para></value>
    public byte BaseIndex { get; set; }

    /// <summary>Gets the infix text sitting between the base prefix and the stored short name.
    /// <para>获取位于基准前缀与磁盘短名之间的 infix 文本。</para></summary>
    /// <value>The infix, or an empty string when the record introduces a whole prefix of its own.
    /// <para>infix 文本；记录自行引入完整前缀时为空字符串。</para></value>
    public string Infix { get; set; } = string.Empty;
}

/// <summary>
/// Bounded compression estimate for one EROFS codec, sampled at extraction from encoded clusters of the
/// largest compressed inodes. It carries the counters the rebuild search replays, never a claim about the
/// encoder that produced the source image, and the sampled cluster count is capped per codec.
/// <para>单个 EROFS 编解码的有界压缩估算，解包时从最大压缩 inode 的已编码簇采样得到。它只携带重建搜索
/// 需要重放的计数，绝不宣称源镜像由哪个编码器产生，且每种编解码的采样簇数量都有上限。</para>
/// </summary>
internal sealed class ErofsRebuildCompressionEstimate
{
    /// <summary>Gets the codec algorithm name as the <c>ErofsCompressionAlgorithm</c> member name; an
    /// unknown name is ignored by the rebuild search rather than throwing.
    /// <para>获取以 <c>ErofsCompressionAlgorithm</c> 成员名表示的编解码算法名；无法识别的名称由重建搜索
    /// 忽略而不抛出。</para></summary>
    public string Algorithm { get; set; } = "";
    /// <summary>Gets the number of source clusters sampled for this codec. <para>获取该编解码采样的源簇数量。</para></summary>
    public int Samples { get; set; }
    /// <summary>Gets the decoded sample volume in bytes. <para>获取已解码样本的字节总量。</para></summary>
    public long SampleBytes { get; set; }
    /// <summary>Gets the candidate encoders in the order the parser enumerated them; the rebuild search
    /// replays the inferred best, the smallest and the last entry.
    /// <para>获取解析器枚举顺序的候选编码器；重建搜索会重放推断最优项、最小项与末项。</para></summary>
    public List<ErofsRebuildCompressionCandidate> Candidates { get; set; } = [];
}

/// <summary>One candidate encoder of an EROFS compression estimate, recorded as raw numbers so the report
/// needs no locale. <para>EROFS 压缩估算的一个候选编码器，仅记录原始数值，使报告无需依赖语言环境。</para></summary>
internal sealed class ErofsRebuildCompressionCandidate
{
    /// <summary>Gets the candidate compression level. <para>获取候选压缩级别。</para></summary>
    public int Level { get; set; }
    /// <summary>Gets whether the candidate used the LZ4HC encoder. <para>获取候选是否使用 LZ4HC 编码器。</para></summary>
    public bool HighCompression { get; set; }
    /// <summary>Gets the accumulated re-encoded size in bytes across the samples.
    /// <para>获取跨样本的累计重编码字节数。</para></summary>
    public long Bytes { get; set; }
    /// <summary>Gets the accumulated relative size deviation against the source clusters.
    /// <para>获取相对源簇的累计体积偏差比例。</para></summary>
    public double Error { get; set; }
    /// <summary>Gets the number of samples whose re-encoding was byte-identical to the source cluster.
    /// <para>获取重编码后与源簇逐字节一致的样本数。</para></summary>
    public int ExactMatches { get; set; }
}