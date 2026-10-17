using UotanStudio.Firmware.FileSystem.Erofs.Models;
using UnifiedErofsInode = UotanStudio.Firmware.FileSystem.Erofs.Models.Inodes.ErofsInode;
namespace UotanStudio.Firmware.FileSystem.Erofs;

/// <summary>
/// Builder options controlling the layout, compression, metadata and device tables of a generated EROFS image.
/// <para>控制生成的 EROFS 镜像之布局、压缩、元数据与设备表的打包选项。</para>
/// </summary>
public sealed class ErofsOptions
{
    /// <summary>Logical block size of the image in bytes. <para>镜像的逻辑块大小（字节）。</para></summary>
    /// <value>Default 4096; validation requires a power of two between 512 and 65536.
    /// <para>默认 4096；校验要求为 512 到 65536 之间的 2 的幂。</para></value>
    public int BlockSize { get; set; } = 4096;

    /// <summary>Whether file data is stored compressed. <para>文件数据是否压缩存储。</para></summary>
    /// <value>Default <see langword="true"/>; <see langword="false"/> keeps files in plain layouts and
    /// skips the compression configuration. <para>默认 <see langword="true"/>；为 <see langword="false"/>
    /// 时文件按明文布局存放并跳过压缩配置。</para></value>
    public bool Compress { get; set; } = true;

    /// <summary>Compression codec declared for the build. <para>本次打包声明的压缩编解码器。</para></summary>
    /// <value>Default <see cref="ErofsCompressionAlgorithm.Lz4"/>; validation rejects values outside the
    /// enum, and <see cref="LegacyCompatibility"/> requires LZ4.
    /// <para>默认 <see cref="ErofsCompressionAlgorithm.Lz4"/>；校验拒绝枚举之外的值，且
    /// <see cref="LegacyCompatibility"/> 要求只能为 LZ4。</para></value>
    public ErofsCompressionAlgorithm Algorithm { get; set; } = ErofsCompressionAlgorithm.Lz4;

    /// <summary>Requested compression effort. <para>请求的压缩强度。</para></summary>
    /// <value>Clamped to 1..9 by validation; with <see cref="Lz4HighCompression"/> it selects the
    /// LZ4HC search depth. <para>校验时钳制到 1..9；配合 <see cref="Lz4HighCompression"/> 时决定
    /// LZ4HC 的搜索深度。</para></value>
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
    /// <value>0 selects the builder default, which is <see cref="BlockSize"/> (one logical block per
    /// pcluster); validation requires a block-aligned value
    /// between <see cref="BlockSize"/> and <c>ErofsCompression.MaxPclusterSize</c>, and
    /// <see cref="LegacyCompatibility"/> pins it to the block size.
    /// <para>0 表示使用打包器默认值，即 <see cref="BlockSize"/>（每个 pcluster 一个逻辑块）；校验要求其为
    /// <see cref="BlockSize"/> 到
    /// <c>ErofsCompression.MaxPclusterSize</c> 之间且按块对齐的数值，且
    /// <see cref="LegacyCompatibility"/> 会将其钉死为块大小。</para></value>
    public int MaxPclusterSize { get; set; }

    /// <summary>Largest pcluster the EROFS compressed format can name, and the ceiling
    /// <see cref="MaxPclusterSize"/> accepts. A caller that wants the densest modern layout asks for this.
    /// <para>EROFS 压缩格式能够表明的最大 pcluster，也是 <see cref="MaxPclusterSize"/> 接受的上限。
    /// 需要最紧密的现代布局的调用方就索取该值。</para>
    /// </summary>
    public const int MaximumPclusterSize = ErofsCompression.MaxPclusterSize;

    /// <summary>MicroLZMA dictionary size in bytes, as declared in the compression configuration.
    /// <para>压缩配置中声明的 MicroLZMA 字典大小（字节）。</para></summary>
    /// <value>Default 8 MiB; validation requires 4096 through 8 MiB. Only the LZMA codec encodes it.
    /// <para>默认 8 MiB；校验要求 4096 到 8 MiB。仅 LZMA 编解码器会编码该值。</para></value>
    public int DictionarySize { get; set; } = 8 << 20;

    /// <summary>DEFLATE or Zstandard window bits; zero selects the codec default.
    /// <para>DEFLATE 或 Zstandard 的窗口位数；0 表示使用编解码器默认值。</para></summary>
    /// <value>Defaults to 15 (DEFLATE) or 20 (Zstandard) when zero; validation checks 8..15 for
    /// DEFLATE and 10..20 for Zstandard. Other algorithms leave the value unchecked.
    /// <para>为 0 时取默认值：DEFLATE 为 15，Zstandard 为 20；校验对 DEFLATE 检查 8..15、
    /// 对 Zstandard 检查 10..20。其他算法不检查该值。</para></value>
    public int WindowBits { get; set; }

    /// <summary>LZ4 maximum match distance; zero denotes the 65535-byte default.
    /// <para>LZ4 最大匹配距离；0 表示默认的 65535 字节。</para></summary>
    /// <value>When set, the LZ4 planner caps each pcluster's match distance and input span to this
    /// distance. <para>设置后，LZ4 规划器把每个 pcluster 的匹配距离与输入跨度限制到该距离。</para></value>
    public ushort Lz4MaxDistance { get; set; }

    /// <summary>
    /// Selects the LZ4HC block encoder (bounded search over match candidates) instead of the fast
    /// single-hash matcher for <see cref="ErofsCompressionAlgorithm.Lz4"/> payloads.
    /// <para>为 <see cref="ErofsCompressionAlgorithm.Lz4"/> 数据选择 LZ4HC 块编码器（在候选匹配上做
    /// 有界搜索），而不是快速的单哈希匹配器。</para>
    /// EROFS stores no encoder identity: the on-disk codec is LZ4 either way, and a reader only sees
    /// the resulting block, so this flag trades build time for density without changing the declared
    /// algorithm. <see cref="CompressionLevel"/> selects the HC search depth and is clamped to 1-9, so the
    /// optimal-parse levels 10-12 are not reachable through these options.
    /// <para>EROFS 不记录编码器身份：磁盘上的编解码器始终是 LZ4，读取方只见最终数据块，因此该开关只用构建时间
    /// 换取压缩率，不改变声明的算法。<see cref="CompressionLevel"/> 决定 HC 搜索深度并被限制在 1-9，
    /// 因此最优分析级（10-12）无法通过本选项到达。</para>
    /// </summary>
    /// <value>Off by default; when on, LZ4 payloads go through the LZ4HC encoder and
    /// <see cref="CompressionLevel"/> selects its search depth.
    /// <para>默认关闭；开启后 LZ4 数据经 LZ4HC 编码器处理，由 <see cref="CompressionLevel"/>
    /// 决定其搜索深度。</para></value>
    public bool Lz4HighCompression { get; set; }

    /// <summary>Optional source epoch and nanoseconds used by compact inodes.
    /// <para>紧凑 inode 使用的可选源纪元时间戳与纳秒。</para></summary>
    /// <value><see langword="null"/> writes the current UTC wall-clock seconds at build time;
    /// otherwise the supplied Unix epoch seconds. <para>为 <see langword="null"/> 时写入构建时刻的
    /// UTC Unix 秒；否则写入给定的纪元秒。</para></value>
    public ulong? BuildTime { get; set; }

    /// <summary>Nanoseconds associated with the filesystem epoch.
    /// <para>与文件系统纪元时间戳关联的纳秒。</para></summary>
    /// <value>Validation requires below 1 000 000 000. <para>校验要求小于 1 000 000 000。</para></value>
    public uint BuildTimeNanoseconds { get; set; }

    /// <summary>Build time offset from the epoch in the current EROFS superblock layout.
    /// <para>按当前 EROFS 超级块布局记录的相对纪元构建时间偏移。</para></summary>
    /// <value>Written to the superblock build-time field; during rebuilds it is seeded from the
    /// source superblock. <para>写入超级块的构建时间字段；重建时以源超级块的值作种子。</para></value>
    public uint BuildTimeOffset { get; set; }

    /// <summary>Whether to write the superblock checksum feature.
    /// <para>是否写入超级块校验和特性。</para></summary>
    /// <value>On by default; the parser re-computes CRC32c over the superblock window and rejects a
    /// mismatch. <para>默认开启；解析器会对超级块窗口重新计算 CRC32c，不匹配即拒绝。</para></value>
    public bool SuperblockChecksum { get; set; } = true;

    internal Dictionary<ulong, UnifiedErofsInode>? SourceInodes { get; set; }
    internal Dictionary<string, UnifiedErofsInode>? SourceInodesByPath { get; set; }
    internal Dictionary<ErofsCompressionAlgorithm, ErofsOptions>? SourceCompressionOptions { get; set; }
    internal bool SourceMtimeFeature { get; set; }
    internal bool SourceXattrFilterFeature { get; set; }
    internal bool SourceSharedEaInMetabox { get; set; }
    internal bool SourceMetaboxCompressed { get; set; }
    /// <summary>
    /// Compression algorithm the source image's metabox container was encoded with, recovered from the
    /// algorithm nibble of the container inode's own map header. The container is an ordinary compressed
    /// inode, so its codec is recorded beside its index the same way a regular file's is; nothing else in
    /// the image repeats it, which is why a preserved rebuild has to read it from the container itself.
    /// <para>源镜像 metabox 容器所用的压缩算法，取自容器 inode 自身映射头的算法半字节。容器就是普通压缩
    /// inode，其编解码器与常规文件一样记录在索引旁；镜像中再无第二处重复该信息，因此保留式重建必须从容器
    /// 自身读取它。</para>
    /// </summary>
    /// <value>The recovered algorithm, or <see langword="null"/> when the source carried no metabox container
    /// or stored it verbatim. <para>还原出的算法；源没有 metabox 容器或以明文存放容器时为
    /// <see langword="null"/>。</para></value>
    internal ErofsCompressionAlgorithm? SourceMetaboxAlgorithm { get; set; }
    /// <summary>
    /// Whether this build encodes the metabox container's body instead of storing it verbatim.
    /// <para>本次构建是否对 metabox 容器本体编码，而不是按明文存放。</para>
    /// </summary>
    /// <value><see langword="true"/> only when the build is on the preserved-rebuild path
    /// (<see cref="SourceInodes"/> carries records), <see cref="Metabox"/> is on, <see cref="Compress"/> is on,
    /// the source recorded a container algorithm, and that algorithm is one this build can re-encode — that is,
    /// it has a configuration record behind it (the same criterion
    /// <c>ErofsRebuildMetadata.CanReencodeWithRecords</c> applies to preserved inodes), or, when the profile
    /// restored no records at all, it is exactly <see cref="Algorithm"/>. The preserved-path requirement is
    /// what keeps this property and the metabox guard in <c>VerifyPreservedStorageStrategies</c> covering the
    /// same builds: the container codec is a source-replay input, so a fresh directory build has nothing to
    /// replay and always stores the container verbatim. The codec half of the test is what keeps the promise
    /// honest: the superblock bitmap is built from the encodable algorithms and a reader consumes the records
    /// in ascending bit order, so a container encoded with a codec that carries no record would name an
    /// algorithm the image cannot declare. With those conditions unmet the caller must keep refusing the
    /// rebuild rather than emit a container whose declared form it cannot back.
    /// <para>仅当构建处于保留式重建路径（<see cref="SourceInodes"/> 携带记录）、<see cref="Metabox"/> 与
    /// <see cref="Compress"/> 均开启、源记录了容器算法、且该算法是本次构建能重新编码的算法时为
    /// <see langword="true"/>——即它背后存在配置记录（与 <c>ErofsRebuildMetadata.CanReencodeWithRecords</c>
    /// 对保留 inode 使用的判据相同），或在档案完全未还原记录时恰好等于 <see cref="Algorithm"/>。要求处于保留
    /// 路径正是让本属性与 <c>VerifyPreservedStorageStrategies</c> 中的 metabox 守卫覆盖同一批构建的条件：容器
    /// 编解码器是源重放输入，因此全新目录构建没有可重放的内容，始终按明文存放容器。判据中关于编解码器的部分正是
    /// 承诺成立的前提：超级块位图由可编码算法集合构成，读取方按位升序消费记录，因此用没有配置记录的编解码器编码
    /// 容器会让镜像指名一个它无法声明的算法。上述条件不满足时调用方必须继续拒绝重建，而不是写出声明无法支撑其
    /// 形态的容器。</para></value>
    internal bool MetaboxContainerCompressed
        => SourceInodes is { Count: > 0 }
            && Metabox
            && Compress
            && SourceMetaboxAlgorithm is { } algorithm
            && (SourceCompressionOptions is { } records
                ? records.ContainsKey(algorithm)
                : algorithm == Algorithm);
    /// <summary>
    /// Start block of the source image's metadata area as recorded in <c>s_meta_blkaddr</c>. A nonzero value
    /// means the source kept its inode records in a metadata zone of their own instead of at the head of the
    /// image; a preserved rebuild has to place the same zone
    /// again, otherwise every inode number would silently resolve to a different byte offset.
    /// <para>源镜像元数据区的起始块，取自 <c>s_meta_blkaddr</c>。非零表示源把 inode 记录放在独立的元数据区
    /// 而不是镜像头部之后；保留式重建必须重新放置同一区块，
    /// 否则每个 inode 编号都会静默解析到不同的字节偏移。</para>
    /// </summary>
    /// <value>The recorded block number, or zero when the source kept its metadata at the image head.
    /// <para>记录的块号；源把元数据留在镜像头部之后时为零。</para></value>
    internal uint SourceMetaBlkAddr { get; set; }
    /// <summary>
    /// Slot count of the source image's extended superblock area, as recorded in <c>s_sb_extslots</c>. The
    /// compression configuration records do not follow the fixed 128-byte superblock; they follow that many
    /// extra 16-byte slots, so a rebuild that replays the records has to replay the slot count or every reader
    /// looks for them at the wrong offset.
    /// <para>源镜像扩展超级块区的槽数，取自 <c>s_sb_extslots</c>。压缩配置记录并不紧跟固定的 128 字节超级块，
    /// 而是跟在同样数量的额外 16 字节槽位之后，因此重放这些记录的重建必须一并重放槽数，否则每个读取方都会
    /// 到错误偏移处查找它们。</para>
    /// </summary>
    /// <value>The captured slot count, or <see langword="null"/> when the options carry no captured source
    /// geometry and the writer keeps its own default.
    /// <para>已采集的槽数；选项未携带已采集的源几何时为 <see langword="null"/>，此时写入方保持自身默认值。</para></value>
    internal byte? SourceSbExtSlots { get; set; }
    /// <summary>
    /// Directory block size shift of the source image, as recorded in <c>dirblkbits</c>. A nonzero value means
    /// the source packed directory data in units larger than one filesystem block, so a rebuild that emitted one
    /// filesystem block per directory block would change the geometry readers derive from this field.
    /// <para>源镜像的目录块大小位移，取自 <c>dirblkbits</c>。非零表示源以大于一个文件系统块的单位打包目录数据，
    /// 因此仍按每目录块一个文件系统块重建会改变读取方由该字段推导出的几何。</para>
    /// </summary>
    /// <value>The captured shift, or <see langword="null"/> when the options carry no captured source geometry
    /// and the writer keeps its own default.
    /// <para>已采集的位移；选项未携带已采集的源几何时为 <see langword="null"/>，此时写入方保持自身默认值。</para></value>
    internal byte? SourceDirBlkBits { get; set; }
    /// <summary>
    /// Long-prefix records the preserved source image carried, replayed so xattr names that needed them
    /// encode against the same table instead of registering new records. Null or empty when the source
    /// defined no long prefixes.
    /// <para>保留式源镜像携带的长前缀记录；重放它们使原本依赖这些记录的 xattr 名称按同一张表编码，
    /// 而不是注册新记录。源未定义长前缀时为 null 或空表。</para>
    /// </summary>
    internal IReadOnlyList<(byte BaseIndex, string Infix)>? SourceXattrPrefixes { get; set; }

    /// <summary>
    /// Packs repeated extended-attribute sets into one shared area that inodes reference by id, the layout
    /// the <c>ishare_xattrs</c> compat bit advertises. Only repeated sets are worth sharing, so nodes whose
    /// xattrs are identical collapse onto one slot and each of their ibodies then carries just the
    /// 12-byte header plus that slot id.
    /// <para>把重复的扩展属性集合打包进一个由各 inode 按 id 引用的共享区，即 <c>ishare_xattrs</c> compat 位
    /// 所声明的布局。只有重复集合才值得共享，因此 xattr 相同的节点合并到同一槽位，它们的 ibody 随后只携带
    /// 12 字节头与该槽位 id。</para>
    /// </summary>
    /// <value><see langword="true"/> when identical xattr sets are shared; the default is
    /// <see langword="false"/>, which leaves every inode's xattrs inline.
    /// <para>共享相同 xattr 集合时为 <see langword="true"/>；默认 <see langword="false"/>，此时每个 inode 的
    /// xattr 保持内联。</para></value>
    public bool IshareXattrs { get; set; }

    /// <summary>
    /// Long-prefix name under which each file inode records a SHA-256 fingerprint of its content. Setting it registers the name as a long prefix, publishes ishare_xattrs with the prefix id naming that record, and gives every regular file an attribute whose value is "sha256:" followed by the 32-byte digest.
    /// <para>各文件 inode 记录其内容 SHA-256 指纹所用的长前缀名。设置它会把该名称注册为长前缀、发布 ishare_xattrs 并以前缀 id 指向该记录，同时为每个常规文件写入名为该文本、值为 "sha256:" 加 32 字节摘要的属性。</para>
    /// </summary>
    /// <value>The attribute name, or <see langword="null"/> to record no fingerprints.
    /// <para>属性名；为 <see langword="null"/> 时不记录指纹。</para></value>
    public string? IshareXattrPrefix { get; set; }

    /// <summary>
    /// Long extended-attribute name prefixes compressed out of repeated xattr names. Each entry registers a long prefix, so a name carrying it stores only its remainder; unlike <see cref="IshareXattrPrefix"/> this records no fingerprints and publishes no ishare bit.
    /// <para>用于压缩重复 xattr 名称的长属性名前缀。每项注册一个长前缀，带该前缀的名称只存储其后缀部分；与 <see cref="IshareXattrPrefix"/> 不同，此处不记录 inode 指纹，也不发布 ishare 位。</para>
    /// </summary>
    /// <value><see langword="null"/> or an empty list compresses nothing. Each entry must be non-whitespace
    /// and at most 255 bytes, and the list holds at most 128 entries, each limit mirroring the on-disk
    /// prefix table. <para>为 <see langword="null"/> 或空列表时不压缩任何名称。每项须非空白且不超过 255
    /// 字节，列表至多 128 项；这些上限与磁盘上的前缀表一致。</para></value>
    public IReadOnlyList<string>? XattrNamePrefixes { get; set; }

    /// <summary>
    /// Image-relative paths to omit from the built image. An entry excludes that exact path and, when it names a directory, every entry beneath it; matching uses the /-separated image path. Excluded entries are dropped during collection, before ownership and xattr tables are applied.
    /// <para>从构建镜像中排除的镜像相对路径。某项排除该路径本身，若其指向目录则包含其下所有条目；匹配基于以 / 分隔的镜像路径。被排除条目在收集阶段即被丢弃，早于所有权与 xattr 表的施加。</para>
    /// </summary>
    /// <value><see langword="null"/> or empty excludes nothing. An empty or whitespace entry fails validation
    /// rather than silently matching everything. <para>为 <see langword="null"/> 或空时不排除任何内容。空或仅空白的
    /// 条目会校验失败，而不会静默匹配一切。</para></value>
    public IReadOnlyList<string>? ExcludePaths { get; set; }

    /// <summary>
    /// Regular expressions matched against image-relative paths to omit from the built image; a match at any position excludes the entry. An invalid pattern fails validation of the whole options set instead of being ignored, so a typo cannot silently widen the image.
    /// <para>用于从构建镜像中排除条目的正则表达式，匹配镜像相对路径，任意位置命中即排除该条目。无效模式会导致整套选项校验失败而非被忽略，拼写错误不会静默放大镜像。</para>
    /// </summary>
    /// <value><see langword="null"/> or empty excludes nothing.
    /// <para>为 <see langword="null"/> 或空时不排除任何内容。</para></value>
    public IReadOnlyList<string>? ExcludeRegexes { get; set; }

    /// <summary>
    /// Forces this uid on every inode after the scanned and fs_config-derived ownership.
    /// <para>在扫描与 fs_config 推导出的属主之后，把该 uid 强制应用到每个 inode。</para>
    /// </summary>
    /// <value><see langword="null"/> keeps each inode's own uid.
    /// <para>为 <see langword="null"/> 时保留各 inode 自身的 uid。</para></value>
    public uint? ForceUid { get; set; }

    /// <summary>
    /// Forces this gid on every inode after the scanned and fs_config-derived ownership.
    /// <para>在扫描与 fs_config 推导出的属主之后，把该 gid 强制应用到每个 inode。</para>
    /// </summary>
    /// <value><see langword="null"/> keeps each inode's own gid.
    /// <para>为 <see langword="null"/> 时保留各 inode 自身的 gid。</para></value>
    public uint? ForceGid { get; set; }

    /// <summary>
    /// Rewrites every inode to uid 0 and gid 0, which is what makes a tree extracted by a non-root user
    /// reproducible.
    /// <para>把每个 inode 重写为 uid 0 与 gid 0，这正是让非 root 用户解出的目录树可复现的手段。</para>
    /// </summary>
    /// <value>Off by default. A set <see cref="ForceUid"/> or <see cref="ForceGid"/> is applied after this
    /// switch, so an explicit single-id override still wins.
    /// <para>默认关闭。已设置的 <see cref="ForceUid"/> 或 <see cref="ForceGid"/> 在本开关之后应用，因此显式的
    /// 单一 id 覆盖仍然优先。</para></value>
    public bool AllRoot { get; set; }

    /// <summary>
    /// Adds this offset to every inode's uid after the other ownership steps.
    /// <para>在其他属主步骤之后给每个 inode 的 uid 加上该偏移。</para>
    /// </summary>
    /// <value>Zero leaves every uid as computed. A negative offset is applied with wraparound onto
    /// <see cref="uint"/> bounds so the stored 32-bit field is always in range.
    /// <para>为零时不改动任何 uid。负偏移按 <see cref="uint"/> 边界回绕应用，使落盘的 32 位字段始终在范围内。</para></value>
    public long UidOffset { get; set; }

    /// <summary>
    /// Adds this offset to every inode's gid after the other ownership steps.
    /// <para>在其他属主步骤之后给每个 inode 的 gid 加上该偏移。</para>
    /// </summary>
    /// <value>Zero leaves every gid as computed. A negative offset is applied with wraparound onto
    /// <see cref="uint"/> bounds so the stored 32-bit field is always in range.
    /// <para>为零时不改动任何 gid。负偏移按 <see cref="uint"/> 边界回绕应用，使落盘的 32 位字段始终在范围内。</para></value>
    public long GidOffset { get; set; }

    /// <summary>
    /// Writes the per-inode xattr name filter into <c>h_name_filter</c>. Each inode's header carries a
    /// 32-bit word in which a raised bit means the hashed attribute name is definitely absent, so lookups
    /// can be skipped; the compat <c>xattr_filter</c> bit is published only when at least one inode's word
    /// is non-zero.
    /// <para>把逐 inode 的 xattr 名称过滤器写入 <c>h_name_filter</c>。每个 inode 头部携带一个 32 位字，
    /// 其中置起的位表示被哈希的属性名一定不存在，据此可跳过查找；仅当至少一个 inode 的字非零时才发布
    /// compat <c>xattr_filter</c> 位。</para>
    /// </summary>
    /// <value><see langword="true"/> to compute and write the filter; the default is
    /// <see langword="false"/>, which leaves every header word zero.
    /// <para>计算并写出过滤器时为 <see langword="true"/>；默认 <see langword="false"/>，此时所有头部字保持
    /// 为零。</para></value>
    public bool XattrNameFilter { get; set; }

    internal IReadOnlyList<ErofsSourceDevice>? SourceDeviceTable { get; set; }
    internal byte[]? SourceVolumeNameBytes { get; set; }
    /// <summary>
    /// Token observed throughout image construction, including node collection, compression planning and data writes.
    /// <para>贯穿镜像构建全程（节点收集、压缩规划与数据写出）监视的取消令牌。</para>
    /// </summary>
    /// <value>The default (none) token makes a build non-cancellable; cancellation surfaces as
    /// <see cref="OperationCanceledException"/> and the destination file is not replaced.
    /// <para>默认（无）令牌使构建不可取消；取消时抛出 <see cref="OperationCanceledException"/>，目标文件不会被替换。</para></value>
    public CancellationToken CancellationToken { get; set; }

    internal bool OptimizeCompression { get; set; }

    /// <summary>Whether compressed files use the compacted (bitfield) index format.
    /// <para>压缩文件是否使用紧凑（位域）索引格式。</para></summary>
    /// <value>On by default; requires logical clusters of at most 16384 bytes when <see cref="Compress"/>,
    /// and <see cref="LegacyCompatibility"/> forces it off.
    /// <para>默认开启；<see cref="Compress"/> 时要求逻辑簇不超过 16384 字节，且
    /// <see cref="LegacyCompatibility"/> 会强制关闭。</para></value>
    public bool UseCompactedIndexes { get; set; } = true;

    /// <summary>Whether compacted indexes use the 2-byte middle section as well, the
    /// <c>4B+2B+4B</c> form that sets <c>Z_EROFS_ADVISE_COMPACTED_2B</c>.
    /// <para>紧凑索引是否同时使用 2 字节中段，即置位 <c>Z_EROFS_ADVISE_COMPACTED_2B</c> 的
    /// <c>4B+2B+4B</c> 形态。</para></summary>
    /// <value>Off by default; requires <see cref="UseCompactedIndexes"/> and logical clusters of at most 4096
    /// bytes. The compacted 2-byte form packs 16 entries of 14 bits per 32-byte unit, so a cluster offset has
    /// only two bits and a larger logical cluster cannot be encoded.
    /// <para>默认关闭；需要 <see cref="UseCompactedIndexes"/> 且逻辑簇不超过 4096 字节。两字节紧凑形态每个
    /// 32 字节单元打包 16 条、每条 14 位，簇内偏移因此只有两位，更大的逻辑簇无法编码。</para></value>
    public bool UseCompacted2BIndexes { get; set; }

    /// <summary>Whether compressed inodes store their index as the variable-size <c>z_erofs_extent</c> array
    /// that <c>Z_EROFS_ADVISE_EXTENTS</c> licenses.
    /// <para>压缩 inode 是否把索引存成 <c>Z_EROFS_ADVISE_EXTENTS</c> 许可的可变长度 <c>z_erofs_extent</c> 数组。</para></summary>
    /// <value><see cref="ErofsTriStateFeature.Auto"/> follows a preserved source inode's recorded
    /// shape, <c>Enabled</c> forces the extent form, and <c>Disabled</c> forbids it. Upstream only writes it
    /// when the image declares <c>48bit</c> and the inode has no inline data, so an enabled request without
    /// that declaration fails instead of degrading silently.
    /// <para><see cref="ErofsTriStateFeature.Auto"/> 跟随保留源 inode 记录的形态，<c>Enabled</c> 强制
    /// extent 形态，<c>Disabled</c> 禁止。extent 形态只在镜像声明 <c>48bit</c> 且 inode 无 inline data 时
    /// 写出，因此启用请求若缺该声明会直接报错而不是静默降级。</para></value>
    public ErofsTriStateFeature UseExtentIndexes { get; set; }

    /// <summary>Chunk size in bytes for chunk-based output.
    /// <para>chunk-based 输出的 chunk 大小（字节）。</para></summary>
    /// <value>Zero (the default) keeps the layout derived from the source inode's recorded chunk bits; a
    /// positive value must be a power of two no smaller than <see cref="BlockSize"/>.
    /// <para>零（默认）保持由源 inode 记录的 chunk 位数推导的布局；正值必须是不小于
    /// <see cref="BlockSize"/> 的 2 的幂。</para></value>
    public int ChunkSize { get; set; }

    /// <summary>Zero-based indices of the filesystem's extra devices that receive chunk data.
    /// <para>接收 chunk 数据的额外设备的下标（从 0 起）。</para></summary>
    /// <value><see langword="null"/> or an empty list keeps every chunk on the primary image. Each entry must
    /// index a declared <see cref="ExtraDeviceCount"/> slot.
    /// <para>为 <see langword="null"/> 或空列表时所有 chunk 都放在主镜像上；每项必须指向已声明的
    /// <see cref="ExtraDeviceCount"/> 槽位。</para></value>
    public IReadOnlyList<int>? BlobDeviceIds { get; set; }

    /// <summary>Inode record width to force.
    /// <para>强制使用的 inode 记录宽度。</para></summary>
    /// <value><see cref="ErofsInodeFormat.Auto"/> keeps the builder's existing size-driven choice.
    /// <para><see cref="ErofsInodeFormat.Auto"/> 保持打包器既有的按尺寸选择行为。</para></value>
    public ErofsInodeFormat InodeFormat { get; set; }

    /// <summary>Chunk index layout to force.
    /// <para>强制使用的 chunk 索引布局。</para></summary>
    /// <value><see cref="ErofsChunkIndexKind.Auto"/> keeps the existing address-range-driven choice.
    /// <para><see cref="ErofsChunkIndexKind.Auto"/> 保持既有的按地址范围选择行为。</para></value>
    public ErofsChunkIndexKind ChunkIndexKind { get; set; }

    /// <summary>Per-path compression hints.
    /// <para>按路径的压缩提示。</para></summary>
    /// <value><see langword="null"/> or an empty list applies the global algorithm everywhere. Each entry is
    /// one rule line; matching uses the hints-file glob semantics, not a regular
    /// expression. <para>为 <see langword="null"/> 或空列表时所有路径都用全局算法；每项为一行规则，按提示文件的
    /// glob 语义匹配，不使用正则。</para></value>
    public IReadOnlyList<string>? CompressHints { get; set; }

    /// <summary>Selects what the dedupe pass compares.
    /// <para>选择去重阶段比较的对象。</para></summary>
    /// <value>Defaults to <see cref="ErofsDedupeMode.WholeFile"/>, which keeps the repository's existing
    /// whole-file content hash behavior gated by <see cref="Dedup"/>; <c>Pcluster</c> instead reuses encoded
    /// pclusters on its own and without requiring <see cref="Dedup"/>.
    /// <para>默认 <see cref="ErofsDedupeMode.WholeFile"/>，保持仓库既有的、由 <see cref="Dedup"/> 门控的整文件
    /// 内容哈希行为；<c>Pcluster</c> 改为复用编码后的 pcluster，独立生效且不要求
    /// <see cref="Dedup"/>。</para></value>
    public ErofsDedupeMode DedupeMode { get; set; }

    /// <summary>
    /// Produces a legacy (pre-5.13) EROFS image without the big-pcluster / COMPR_CFGS incompatible
    /// feature, so the output stays mountable by older implementations.
    /// <para>生成 legacy（pre-5.13）erofs 镜像，不声明 big-pcluster / COMPR_CFGS 不兼容特性，
    /// 使输出可被较旧实现挂载。</para>
    /// Enabling it forces the legacy layout: LZ4 only, <see cref="MaxPclusterSize"/> equal to the block
    /// size (so each pcluster's stream occupies one physical block), and full-size (non-compacted)
    /// indexes, matching the pre-5.13 layout those features arrived with.
    /// Pclusters may still start and end at byte-granular offsets: both index formats store
    /// di_clusterofs, so an older implementation can address them.
    /// <para>开启时强制 legacy 布局：仅 LZ4、<see cref="MaxPclusterSize"/> 等于块大小（每个 pcluster 的
    /// 压缩流只占一个物理块）、使用完整（非紧凑）索引——与 5.13 之前的布局一致。
    /// pcluster 仍可按字节粒度起讫：两种索引格式都保存 di_clusterofs，旧实现可以寻址。</para>
    /// </summary>
    /// <value>Off by default; when on, validation rejects any algorithm but LZ4 and pins
    /// <see cref="MaxPclusterSize"/>, index format, tail packing, fragments, dedup and metabox to the
    /// legacy layout. It also seeds the declaration: the legacy layout keeps <c>big_pcluster</c> out of
    /// <see cref="Features"/> and turns <see cref="DotOmitted"/> off.
    /// <para>默认关闭；开启后校验只接受 LZ4，并把 <see cref="MaxPclusterSize"/>、索引格式、尾部打包、
    /// fragment、去重与 metabox 钉死为 legacy 布局。它同时为声明提供种子：legacy 布局不把
    /// <c>big_pcluster</c> 写入 <see cref="Features"/>，并关闭 <see cref="DotOmitted"/>。</para></value>
    public bool LegacyCompatibility { get; set; }

    /// <summary>
    /// Per-bit declaration channel for the on-disk <c>s_feature_compat</c> and <c>s_feature_incompat</c>
    /// words, so the builder never has to read <see cref="LegacyCompatibility"/> to decide feature bits.
    /// <para>磁盘 <c>s_feature_compat</c> 与 <c>s_feature_incompat</c> 字的逐位声明通道，使打包器无需
    /// 读取 <see cref="LegacyCompatibility"/> 即可判定特性位。</para>
    /// A derived (<see cref="ErofsFeatureSet.Auto"/>) set keeps both words computed from the layout
    /// options unless an inherited source profile is present; an explicit set is authoritative, and
    /// validation resolves it into this property together with the layout-independent rules.
    /// <para><see cref="ErofsFeatureSet.Auto"/> 的推导式集合会让两个字继续跟随布局选项，除非存在继承的
    /// 源档案；显式集合具备权威，校验会把它连同与布局无关的规则一并解析后写回本属性。</para>
    /// </summary>
    /// <value>Defaults to <see cref="ErofsFeatureSet.Auto"/>. After validation an authoritative
    /// declaration carries the resolved words, while a purely derived one stays
    /// <see cref="ErofsFeatureSet.Auto"/>.
    /// <para>默认 <see cref="ErofsFeatureSet.Auto"/>。校验后具备权威的声明携带解析完成的特性字，
    /// 纯推导式声明则保持 <see cref="ErofsFeatureSet.Auto"/>。</para></value>
    public ErofsFeatureSet Features { get; set; } = ErofsFeatureSet.Auto;

    /// <summary>
    /// Selects the dot-omitted dimension, the
    /// <see cref="UnifiedErofsInode.AdviseBitNlinkOneOrDotOmitted"/> inode advise bit. It has no superblock
    /// feature bit of its own, so it is declared beside <see cref="Features"/> rather than inside it.
    /// <para>选择省略点项维度，即
    /// <see cref="UnifiedErofsInode.AdviseBitNlinkOneOrDotOmitted"/> inode advise 位。该维度没有自有的超级块
    /// 特性位，因此与 <see cref="Features"/> 并列声明而非包含其中。</para>
    /// </summary>
    /// <value>Defaults to <see cref="ErofsTriStateFeature.Auto"/>; validation resolves a modern
    /// declaration to <see cref="ErofsTriStateFeature.Enabled"/> and a legacy one to
    /// <see cref="ErofsTriStateFeature.Disabled"/>.
    /// <para>默认 <see cref="ErofsTriStateFeature.Auto"/>；校验把现代声明解析为
    /// <see cref="ErofsTriStateFeature.Enabled"/>，把 legacy 声明解析为
    /// <see cref="ErofsTriStateFeature.Disabled"/>。</para></value>
    public ErofsTriStateFeature DotOmitted { get; set; } = ErofsTriStateFeature.Auto;

    /// <summary>
    /// Declares that the target defines the nlink-one / dot-omitted advise bit
    /// (<c>EROFS_I_NLINK_1_BIT</c> / <c>EROFS_I_DOT_OMITTED_BIT</c>). The bit lies outside the advise mask
    /// most targets accept, so an inode carrying it could mount but enumerate nothing; this stays off
    /// unless the caller knows its target genuinely defines the bit.
    /// <para>声明目标定义了 nlink-one / 省略点项 advise 位（<c>EROFS_I_NLINK_1_BIT</c> /
    /// <c>EROFS_I_DOT_OMITTED_BIT</c>）。该位位于多数目标可接受的 advise 掩码之外，携带它的 inode 可能能挂载
    /// 却枚举不出内容，因此除非调用方确定目标确实定义该位，本项保持关闭。</para>
    /// </summary>
    /// <value>Off by default. Turning it on is a statement about the target, not a request: the bit is
    /// still suppressed when the layout is legacy, and still suppressed when
    /// <see cref="DotOmitted"/> resolves to <see cref="ErofsTriStateFeature.Disabled"/>.
    /// <para>默认关闭。开启它是对目标的陈述而非请求：legacy 布局下该位仍被抑制，
    /// <see cref="DotOmitted"/> 解析为 <see cref="ErofsTriStateFeature.Disabled"/> 时也仍被抑制。</para></value>
    public bool NlinkOneAdviseSupported { get; set; }

    /// <summary>
    /// Set by validation once an explicit declaration or an inherited source profile has resolved both
    /// feature words. While it is clear, the builder derives the words from the layout options.
    /// <para>当显式声明或继承的源档案解析出两个特性字后由校验置位。其为 false 时打包器仍按布局选项推导
    /// 这两个字。</para>
    /// </summary>
    /// <value><see langword="true"/> when <see cref="Features"/> is authoritative for the build.
    /// <para><see cref="Features"/> 对本次构建具备权威时为 <see langword="true"/>。</para></value>
    internal bool FeaturesAuthoritative { get; set; }

    /// <summary>
    /// Compat bits the builder removes from the resolved declaration because the planned layout does not
    /// produce their structures; recorded for the caller to report.
    /// <para>打包器因规划的布局不会产出对应结构而从解析后的声明中移除的 compat 位，记录下来供调用方报告。</para>
    /// </summary>
    /// <value>Raw <c>s_feature_compat</c> mask of the dropped bits; empty until the builder
    /// reconciles the declaration. <para>被移除位的原始 <c>s_feature_compat</c> 掩码；打包器完成对账前为空。</para></value>
    internal uint DroppedCompatBits { get; set; }

    /// <summary>
    /// Incompat bits the builder adds to the resolved declaration because the planned layout requires
    /// them; recorded for the caller to report.
    /// <para>打包器因规划的布局必须使用而添加到解析后声明中的 incompat 位，记录下来供调用方报告。</para>
    /// </summary>
    /// <value>Raw <c>s_feature_incompat</c> mask of the added bits; empty until the builder
    /// reconciles the declaration. <para>新增位的原始 <c>s_feature_incompat</c> 掩码；打包器完成对账前为空。</para></value>
    internal uint AddedIncompatBits { get; set; }

    /// <summary>
    /// Incompat bits the source profile dropped while it was read back: a layout policy demanded a bit the
    /// source never declared, or the writer cannot re-emit the structures behind a declared bit, so the bit
    /// is absent from <see cref="Features"/> as well.
    /// <para>源档案在读回时移除的 incompat 位：布局策略要求源镜像从未声明的位，或写入方无法重新输出已声明位
    /// 背后的结构，因此这些位在 <see cref="Features"/> 中同样不存在。</para>
    /// </summary>
    /// <value>Raw <c>s_feature_incompat</c> mask of the dropped bits; empty unless a preserved source
    /// profile narrowed the inherited declaration.
    /// <para>被移除位的原始 <c>s_feature_incompat</c> 掩码；只有被保留的源档案收窄继承声明时才非空。</para></value>
    internal uint DroppedIncompatBits { get; set; }

    /// <summary>Incompat bits the legacy (pre-5.13) preset cannot keep in the declared word.
    /// <para>legacy（pre-5.13）预设无法保留在声明字中的 incompat 位。</para></summary>
    private const uint LegacyClearedIncompatBits = ErofsSuperblock.IncompatBigPcluster
        | ErofsSuperblock.IncompatZtailpacking | ErofsSuperblock.IncompatFragments
        | ErofsSuperblock.IncompatMetabox;

    /// <summary>Optional filesystem UUID; extracted images retain their original identity.
    /// <para>可选的文件系统 UUID；解包镜像可保留其原始标识。</para></summary>
    /// <value>Only a 16-byte array is written; <see langword="null"/> or another length makes the
    /// builder generate a fresh GUID. <para>仅 16 字节数组会被写入；为 <see langword="null"/>
    /// 或其他长度时由打包器生成新的 GUID。</para></value>
    public byte[]? Uuid { get; set; }

    /// <summary>Volume label written to the superblock. <para>写入超级块的卷标签。</para></summary>
    /// <value>Default <c>"erofs"</c>; an empty value is refilled with <c>"erofs"</c> during validation
    /// unless a filesystem rebuild plan is active. <para>默认 <c>"erofs"</c>；校验时空值会被重填为
    /// <c>"erofs"</c>，除非当前存在文件系统重建计划。</para></value>
    public string VolumeName { get; set; } = "erofs";

    /// <summary>Extended attributes injected per in-image path during the build.
    /// <para>打包时按镜像内路径注入的扩展属性。</para></summary>
    /// <value>Keys are '/'-separated image paths and each list is encoded as inline xattrs;
    /// <see langword="null"/> or an empty dictionary adds nothing.
    /// <para>键为 '/' 分隔的镜像路径，每项列表被编码为 inline xattr；为 <see langword="null"/>
    /// 或空字典时不添加任何内容。</para></value>
    public IReadOnlyDictionary<string, IReadOnlyList<ErofsXattrEntry>>? FileXattrs { get; set; }

    /// <summary>Whether sub-block file tails may be stored inline next to their indexes.
    /// <para>是否允许把不足一块的文件尾部内联存放在其索引旁。</para></summary>
    /// <value>On by default; validation forces it off when <see cref="Fragments"/> or
    /// <see cref="Metabox"/> is set, and rebuilds with source inodes ignore it.
    /// <para>默认开启；<see cref="Fragments"/> 或 <see cref="Metabox"/> 置位时校验强制关闭，
    /// 携带源 inode 的重建会忽略它。</para></value>
    public bool TailPacking { get; set; } = true;

    /// <summary>Stores the final compressed cluster beside its indexes when it fits in a metadata block.
    /// <para>当最后一个压缩簇能放入元数据块时，将其存放在索引旁。</para></summary>
    /// <value>Off by default; <see cref="LegacyCompatibility"/> forces it off during validation.
    /// <para>默认关闭；校验时 <see cref="LegacyCompatibility"/> 会强制关闭。</para></value>
    public bool CompressedTailPacking { get; set; }

    /// <summary>Stores file data that does not fill a whole logical cluster in the packed fragment inode
    /// instead of a dedicated block: tails of larger files first, and whole sub-block files when
    /// <see cref="AllFilesAsFragments"/> is set too. This is the on-disk layout that
    /// <c>EROFS_FEATURE_INCOMPAT_FRAGMENTS</c> licenses.
    /// <para>把填不满一个逻辑簇的数据存放在 packed fragment inode 里而不是专用块：先是较大文件的尾部，
    /// 若在 <see cref="AllFilesAsFragments"/> 也置位时还包括整文件不足一块的小文件。这正是
    /// <c>EROFS_FEATURE_INCOMPAT_FRAGMENTS</c> 所许可的磁盘布局。</para></summary>
    /// <value>Off by default; enabling it forces <see cref="TailPacking"/> off, and
    /// <see cref="LegacyCompatibility"/> clears it during validation.
    /// <para>默认关闭；开启会迫使 <see cref="TailPacking"/> 关闭，且校验时
    /// <see cref="LegacyCompatibility"/> 会将其清零。</para></value>
    public bool Fragments { get; set; }

    /// <summary>Allows a file shorter than one filesystem block to be stored whole in the packed fragment
    /// inode. Requires <see cref="Fragments"/>.
    /// <para>允许不足一个文件系统块的文件整份存放在 packed fragment inode 里。需要 <see cref="Fragments"/>
    /// 同时开启。</para></summary>
    /// <value>Off by default; validation ignores it while <see cref="Fragments"/> is off, and
    /// <see cref="LegacyCompatibility"/> clears it.
    /// <para>默认关闭；<see cref="Fragments"/> 关闭时校验忽略它，且
    /// <see cref="LegacyCompatibility"/> 会将其清零。</para></value>
    public bool AllFilesAsFragments { get; set; }

    /// <summary>Shares physical storage between files with identical content.
    /// <para>让内容相同的文件共享同一份物理数据。</para></summary>
    /// <value>Off by default; when on, data blocks and fragments are reused by content hash while
    /// <see cref="DedupeMode"/> is <see cref="ErofsDedupeMode.WholeFile"/>, and
    /// <see cref="LegacyCompatibility"/> clears it during validation. The whole-file pass is skipped when
    /// <see cref="DedupeMode"/> selects the encoded-pcluster comparison, because the two modes compare
    /// different objects and running both would make the reused set depend on the pass order.
    /// <para>默认关闭；开启后当 <see cref="DedupeMode"/> 为 <see cref="ErofsDedupeMode.WholeFile"/> 时按内容
    /// 哈希复用数据块与 fragment，且校验时 <see cref="LegacyCompatibility"/> 会将其清零。当
    /// <see cref="DedupeMode"/> 选择编码后 pcluster 比较时跳过整文件处理，因为两种模式比较的对象不同，
    /// 同时运行会让复用集合取决于处理顺序。</para></value>
    public bool Dedup { get; set; }

    /// <summary>Number of bytes the encoded-pcluster reuse kept out of the image.
    /// <para>编码后 pcluster 复用为镜像省下的字节数。</para></summary>
    /// <value><see langword="null"/> when no pcluster was reused, including for every whole-file build;
    /// otherwise the summed length of the reused frames. It is a build statistic and is never stored on
    /// disk. <para>没有 pcluster 被复用（包括所有整文件构建）时为 <see langword="null"/>；否则为被复用帧的
    /// 长度之和。它是构建统计量，绝不落盘。</para></value>
    internal long? SavedByDeduplication { get; set; }

    private readonly System.Collections.Concurrent.ConcurrentQueue<string> buildWarnings = new();

    /// <summary>
    /// Build-time warnings the builder recorded instead of silently discarding a rule the caller supplied.
    /// <para>打包器记录的构建期警告：调用方给出的规则被丢弃时不会静默，而是记录于此。</para>
    /// </summary>
    /// <value>Empty until a build records a warning, such as a <c>--compress-hints</c> entry whose pattern the
    /// engine rejects. It is a build statistic and is never stored on disk.
    /// <para>构建记录警告前为空，例如 <c>--compress-hints</c> 条目中引擎拒绝的模式。它是构建统计量，
    /// 绝不落盘。</para></value>
    internal IReadOnlyCollection<string> BuildWarnings => buildWarnings;

    /// <summary>Records one build-time warning for the caller to report.
    /// <para>记录一条构建期警告供调用方上报。</para>
    /// </summary>
    /// <param name="warning">Warning text. <para>警告文本。</para></param>
    internal void AddBuildWarning(string warning) => buildWarnings.Enqueue(warning);

    /// <summary>
    /// Produces the per-node options a compression hint selected: a value copy of this instance with the
    /// hinted pcluster size and codec, re-validated against the layout invariants so an impossible hint
    /// fails the build instead of silently emitting the global shape. The payload pass runs in parallel, so a
    /// hit must never mutate this instance or a shared per-codec record.
    /// <para>压缩提示选中的逐节点选项：本实例的按值副本，携带提示的 pcluster 大小与编解码器，并针对布局
    /// 不变量重新校验，使不可能的提示让构建失败而不是静默写出全局形态。负载遍历并行执行，因此命中绝不能修改
    /// 本实例或共享的逐编解码器记录。</para>
    /// </summary>
    /// <param name="maxPclusterSize">Pcluster size the hint selected, in bytes.
    /// <para>提示选定的 pcluster 字节数。</para></param>
    /// <param name="algorithm">Codec the hint selected. <para>提示选定的编解码器。</para></param>
    /// <returns>A fresh instance the builder may hand to the compressor for that one node.
    /// <para>打包器可交给压缩器用于该单个节点的新实例。</para></returns>
    /// <exception cref="FileSystemUnsupportedFeatureException">The hint cannot be expressed by the layout the
    /// build already declared. <para>该提示无法由本次构建已声明的布局表达。</para></exception>
    internal ErofsOptions WithCompressHint(int maxPclusterSize, ErofsCompressionAlgorithm algorithm)
    {
        var derived = new ErofsOptions
        {
            BlockSize = BlockSize,
            Compress = Compress,
            Algorithm = algorithm,
            CompressionLevel = CompressionLevel,
            MaxPclusterSize = maxPclusterSize,
            DictionarySize = DictionarySize,
            WindowBits = WindowBits,
            Lz4MaxDistance = Lz4MaxDistance,
            Lz4HighCompression = Lz4HighCompression,
            UseCompactedIndexes = UseCompactedIndexes,
            UseCompacted2BIndexes = UseCompacted2BIndexes,
            CompressedTailPacking = CompressedTailPacking,
            OptimizeCompression = OptimizeCompression,
            LegacyCompatibility = LegacyCompatibility,
            Features = Features,
            FeaturesAuthoritative = FeaturesAuthoritative,
            DotOmitted = DotOmitted,
            CancellationToken = CancellationToken,
        };

        if (derived.LegacyCompatibility)
        {
            if (algorithm != ErofsCompressionAlgorithm.Lz4)
            {
                throw new FileSystemUnsupportedFeatureException(
                    "EROFS compress hints cannot select a codec other than LZ4 for a legacy (pre-5.13) layout.");
            }

            if (maxPclusterSize != derived.BlockSize)
            {
                throw new FileSystemUnsupportedFeatureException(
                    "EROFS compress hints cannot change the pcluster size for a legacy (pre-5.13) layout, which "
                    + $"pins it to the {derived.BlockSize}-byte block size.");
            }
        }

        if (maxPclusterSize < derived.BlockSize || maxPclusterSize > MaximumPclusterSize
            || maxPclusterSize % derived.BlockSize != 0)
        {
            throw new FileSystemUnsupportedFeatureException(
                $"EROFS compress hints selected a {maxPclusterSize}-byte pcluster size, which is not a "
                + $"block-aligned value between {derived.BlockSize} and {MaximumPclusterSize}.");
        }

        if (derived.Compress && derived.UseCompactedIndexes && derived.BlockSize > 16384)
        {
            throw new FileSystemUnsupportedFeatureException(
                "Compacted EROFS indexes require logical clusters no larger than 16384 bytes.");
        }

        if (derived.UseCompacted2BIndexes && (!derived.Compress || !derived.UseCompactedIndexes))
        {
            throw new FileSystemUnsupportedFeatureException(
                "EROFS compacted 2-byte indexes require compacted indexes on compressed files.");
        }

        if (derived.UseCompacted2BIndexes && derived.BlockSize > 4096)
        {
            throw new FileSystemUnsupportedFeatureException(
                "EROFS compacted 2-byte indexes require logical clusters no larger than 4096 bytes.");
        }

        derived.CompressionLevel = ErofsCompressionLevel.Validate(algorithm, derived.CompressionLevel);
        return derived;
    }

    /// <summary>Places the compression configuration and part of the metadata in a metabox.
    /// <para>将压缩配置与部分元数据放入 metabox。</para></summary>
    /// <value>Off by default; enabling it forces <see cref="TailPacking"/> off, and
    /// <see cref="LegacyCompatibility"/> clears it during validation.
    /// <para>默认关闭；开启会迫使 <see cref="TailPacking"/> 关闭，且校验时
    /// <see cref="LegacyCompatibility"/> 会将其清零。</para></value>
    public bool Metabox { get; set; }

    /// <summary>Requires the metabox container to exist even when the planned layout selects no resident
    /// inode, so an image whose declaration carries <c>EROFS_FEATURE_INCOMPAT_METABOX</c> always has the
    /// superblock slot and <c>s_metabox_nid</c> behind the bit. The packing key <c>ErofsMetabox=forced</c>
    /// sets it; a value of <c>auto</c> leaves it clear.
    /// <para>要求即使规划的布局没选出任何常驻 inode 也建立 metabox 容器，使声明携带
    /// <c>EROFS_FEATURE_INCOMPAT_METABOX</c> 的镜像总是在该位背后有超级块槽与
    /// <c>s_metabox_nid</c>。打包键 <c>ErofsMetabox=forced</c> 置位，取值 <c>auto</c> 时保持关闭。</para>
    /// </summary>
    /// <value><see langword="false"/> unless the caller asked for a guaranteed container; it has no
    /// effect while <see cref="Metabox"/> is off.
    /// <para>调用方未要求必建容器时为 <see langword="false"/>；<see cref="Metabox"/> 关闭时它不起作用。</para></value>
    public bool MetaboxRequired { get; set; }

    /// <summary>Number of extra devices declared in the on-disk device table.
    /// <para>磁盘设备表中声明的额外设备数量。</para></summary>
    /// <value>Default <c>0</c>; must be non-negative, and a positive count requires the same number
    /// of <see cref="ExtraDeviceImages"/> descriptors.
    /// <para>默认 <c>0</c>；必须非负，且正数时要求数量一致的 <see cref="ExtraDeviceImages"/>
    /// 描述符。</para></value>
    public int ExtraDeviceCount { get; set; }

    /// <summary>
    /// Optional descriptors for the extra devices referenced by the device table, one per <see cref="ExtraDeviceCount"/> device. A declared device carries data only when <see cref="BlobDeviceIds"/> selects it.
    /// <para>设备表引用的额外设备描述符，每个 <see cref="ExtraDeviceCount"/> 设备一项。已声明的设备只有被 <see cref="BlobDeviceIds"/> 选中时才承载数据。</para>
    /// </summary>
    /// <value>One descriptor per declared device (ids 1..<see cref="ExtraDeviceCount"/>); validation
    /// requires a non-empty UTF-8 tag shorter than the slot field and a positive block-aligned
    /// <c>SizeBytes</c>. <para>每个声明的设备（id 1..<see cref="ExtraDeviceCount"/>）一个描述符；
    /// 校验要求 tag 非空、UTF-8 字节数短于槽字段，且 <c>SizeBytes</c> 为正并按块对齐。</para></value>
    public IReadOnlyList<ErofsDeviceSpec>? ExtraDeviceImages { get; set; }

    /// <summary>
    /// Optional path to an Android-style <c>fs_config</c> file (<c>path uid gid mode capabilities</c>)
    /// applied over the source tree; matching entries override the scanned uid/gid/mode.
    /// <para>可选的 Android 风格 <c>fs_config</c> 文件路径（<c>path uid gid mode capabilities</c>），
    /// 覆盖源树扫描结果：命中的条目以文件中的 uid/gid/mode 为准。</para>
    /// </summary>
    /// <value><see langword="null"/> reads nothing from disk; a preloaded override table is still
    /// applied and takes precedence over this path.
    /// <para>为 <see langword="null"/> 时不从磁盘读取；预加载的覆盖表仍会生效并优先于本路径。</para></value>
    public string? FsConfigPath { get; set; }

    /// <summary>
    /// Optional path to an Android-style <c>file_contexts</c> file (<c>path label</c>) applied over
    /// the source tree; matching entries are stored as <c>security.selinux</c> xattrs.
    /// <para>可选的 Android 风格 <c>file_contexts</c> 文件路径（<c>path label</c>），
    /// 命中的条目以 <c>security.selinux</c> xattr 写入镜像。</para>
    /// </summary>
    /// <value><see langword="null"/> reads nothing from disk; a preloaded override table is still
    /// applied and takes precedence over this path.
    /// <para>为 <see langword="null"/> 时不从磁盘读取；预加载的覆盖表仍会生效并优先于本路径。</para></value>
    public string? FileContextsPath { get; set; }

    /// <summary>Preloaded override tables; when set they take precedence over the path-based sources above.</summary>
    internal IReadOnlyDictionary<string, Metadata.FsConfigEntry>? FsConfig { get; set; }
    internal IReadOnlyDictionary<string, string>? FileContexts { get; set; }

    /// <summary>
    /// Validates and normalizes the options, resolving the declared feature words and the dot-omitted
    /// dimension once every layout rule that feeds them has run.
    /// <para>校验并规整选项，在所有为二者提供输入的布局规则执行完毕后，解析声明的特性字与 dot-omitted 维度。</para>
    /// </summary>
    internal void Validate()
    {
        if (BlockSize is < 512 or > 65536 || (BlockSize & (BlockSize - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(BlockSize),
                "BlockSize must be a power of two between 512 and 65536 bytes.");
        }

        CompressionLevel = ErofsCompressionLevel.Validate(Algorithm, CompressionLevel);

        if (MaxPclusterSize == 0) MaxPclusterSize = BlockSize;
        if (LegacyCompatibility)
        {
            if (Algorithm != ErofsCompressionAlgorithm.Lz4)
                throw new NotSupportedException(
                    $"EROFS legacy (pre-5.13) layout supports only LZ4; {Algorithm} requires the modern big-pcluster feature.");
            MaxPclusterSize = BlockSize;
            UseCompactedIndexes = false;
            UseCompacted2BIndexes = false;
            CompressedTailPacking = false;
            Fragments = false;
            AllFilesAsFragments = false;
            Dedup = false;
            Metabox = false;
            MetaboxRequired = false;
            InodeFormat = ErofsInodeFormat.Auto;
            ChunkIndexKind = ErofsChunkIndexKind.Auto;
            if (SourceCompressionOptions is { } codecOptions)
            {
                foreach (var codec in codecOptions.Values)
                {
                    codec.LegacyCompatibility = true;
                    codec.MaxPclusterSize = codec.BlockSize > 0 ? codec.BlockSize : BlockSize;
                    codec.UseCompactedIndexes = false;
                    codec.UseCompacted2BIndexes = false;
                    codec.CompressedTailPacking = false;
                    codec.Fragments = false;
                    codec.AllFilesAsFragments = false;
                    codec.Dedup = false;
                    codec.Metabox = false;
                    codec.MetaboxRequired = false;
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

        if (UseCompacted2BIndexes && (!Compress || !UseCompactedIndexes))
            throw new NotSupportedException(
                "EROFS compacted 2-byte indexes require compacted indexes on compressed files.");
        if (UseCompacted2BIndexes && BlockSize > 4096)
            throw new NotSupportedException(
                "EROFS compacted 2-byte indexes require logical clusters no larger than 4096 bytes.");

        if (string.IsNullOrEmpty(VolumeName) && Rebuilding.FileSystemEditPlan.Current is null)
        {
            VolumeName = "erofs";
        }

        if (ExtraDeviceCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ExtraDeviceCount), ExtraDeviceCount,
                "ExtraDeviceCount must be non-negative.");
        }

        if (ExtraDeviceCount > 0)
        {
            if (ExtraDeviceImages is null || ExtraDeviceImages.Count != ExtraDeviceCount)
                throw new NotSupportedException(
                    "EROFS multi-device output requires one ExtraDeviceImages descriptor per declared device.");
            for (var i = 0; i < ExtraDeviceImages.Count; i++)
            {
                var spec = ExtraDeviceImages[i];
                if (!HasWritableDeviceTag(spec))
                    throw new ArgumentOutOfRangeException(nameof(ExtraDeviceImages),
                        $"Extra device {i} needs a non-empty tag the {ErofsDeviceSlot.TagSize}-byte slot can hold: "
                        + "raw bytes of at most that width, or UTF-8 text shorter than it.");
                if (spec.SizeBytes <= 0 || spec.SizeBytes % BlockSize != 0)
                    throw new ArgumentOutOfRangeException(nameof(ExtraDeviceImages),
                        $"Extra device {i} size must be a positive, block-aligned byte count.");
            }
        }

        if (XattrNamePrefixes is { Count: > 0 } namePrefixes)
        {
            if (namePrefixes.Count > 128)
                throw new ArgumentOutOfRangeException(nameof(XattrNamePrefixes),
                    $"Xattr name prefixes exceed 128 entries, the most a prefix table id can name.");

            for (var i = 0; i < namePrefixes.Count; i++)
            {
                var prefix = namePrefixes[i];
                if (string.IsNullOrWhiteSpace(prefix))
                    throw new ArgumentOutOfRangeException(nameof(XattrNamePrefixes),
                        $"Xattr name prefix {i} must not be empty or whitespace.");
                if (System.Text.Encoding.UTF8.GetByteCount(prefix) > 255)
                    throw new ArgumentOutOfRangeException(nameof(XattrNamePrefixes),
                        $"Xattr name prefix {i} exceeds 255 bytes, the longest a prefix table record holds.");
            }
        }

        if (Fragments && !Features.IsAuto && Features.Contains(ErofsFeature.Fragments))
        {
            AllFilesAsFragments = true;
        }

        if (Metabox)
        {
            TailPacking = false;
        }

        ResolveFeatureDeclaration();
    }

    /// <summary>
    /// Checks that a device descriptor carries a tag the 64-byte slot can hold. Raw bytes decide it when the
    /// descriptor has them, because those are what the writer emits and the field may hold a digest that has no
    /// text form at all; the UTF-8 text decides it otherwise, and has to stay below the field width so the slot
    /// keeps its NUL terminator.
    /// <para>检查设备描述符是否携带 64 字节槽位能容纳的 tag。描述符携带原始字节时以原始字节判定，因为写入方
    /// 输出的就是它们，且该字段可能存放根本没有文本形式的摘要；否则以 UTF-8 文本判定，其长度必须小于字段宽度，
    /// 使槽位保留 NUL 终止符。</para>
    /// </summary>
    /// <param name="spec">Descriptor whose tag is checked.
    /// <para>待检查 tag 的描述符。</para></param>
    /// <returns><see langword="true"/> when the writer can fill the tag slot from the descriptor.
    /// <para>写入方能从该描述符填满 tag 槽位时为 <see langword="true"/>。</para></returns>
    private static bool HasWritableDeviceTag(ErofsDeviceSpec spec)
    {
        if (spec.TagBytes is { Length: > 0 } rawBytes)
        {
            return rawBytes.Length <= ErofsDeviceSlot.TagSize;
        }

        return !string.IsNullOrEmpty(spec.Tag)
            && System.Text.Encoding.UTF8.GetByteCount(spec.Tag) < ErofsDeviceSlot.TagSize;
    }

    /// <summary>
    /// Resolves <see cref="Features"/> into authoritative superblock words when the caller declared them
    /// or an inherited source profile makes the declaration authoritative, rejects declarations the
    /// writer or the layout cannot honor, resolves <see cref="DotOmitted"/> and copies the outcome into
    /// every codec record so a preserved rebuild honors it.
    /// <para>当调用方显式声明特性字，或继承的源档案使声明具备权威时，把 <see cref="Features"/> 解析为权威的
    /// 超级块字，拒绝写入方或布局无法兑现的声明，解析 <see cref="DotOmitted"/>，并把结果复制到每个编解码器
    /// 记录，使保留式重建同样遵循该声明。</para>
    /// Layout-implied bits (device table, fragments/dedupe, ztailpacking and metabox) stay out of the
    /// derived baseline because the builder reconciles them against the actual planned layout and records
    /// the outcome in <see cref="AddedIncompatBits"/> and <see cref="DroppedCompatBits"/>.
    /// <para>布局隐含位（设备表、fragments/dedupe、ztailpacking 与 metabox）不写入推导基线：打包器会按实际
    /// 规划的布局与声明对账，并把结果记录到 <see cref="AddedIncompatBits"/> 与 <see cref="DroppedCompatBits"/>。</para>
    /// The inherited profile is the one a preserved rebuild seeds, recognized here by its source inode
    /// records or its per-codec options, because those are the fields only a parsed source image fills.
    /// <para>继承的档案即保留式重建所种下的档案，此处以源 inode 记录或逐编解码器选项识别，因为只有解析源镜像
    /// 才会填充这些字段。</para>
    /// </summary>
    private void ResolveFeatureDeclaration()
    {
        var declared = !Features.IsAuto;
        var profileInherited = SourceInodes is { Count: > 0 } || SourceCompressionOptions is { Count: > 0 };
        var compatBits = declared ? Features.CompatBits : DerivedCompatBits();
        var incompatBits = declared ? Features.IncompatBits : DerivedIncompatBaseline();

        RejectLegacyIncompatConflict(incompatBits);
        if (LegacyCompatibility)
        {
            incompatBits &= ~LegacyClearedIncompatBits;
        }

        ResolveDotOmitted();
        FeaturesAuthoritative = declared || profileInherited;
        if (FeaturesAuthoritative)
        {
            Features = ErofsFeatureSet.FromWords(compatBits, incompatBits);
        }

        if (SourceCompressionOptions is { } codecs)
        {
            foreach (var codec in codecs.Values)
            {
                codec.Features = Features;
                codec.FeaturesAuthoritative = FeaturesAuthoritative;
                codec.DotOmitted = DotOmitted;
            }
        }
    }

    /// <summary>
    /// Derives the compat word from the options that own a compat bit today.
    /// <para>由今天确实持有 compat 位的各选项推导出 compat 字。</para>
    /// </summary>
    /// <returns>The derived <c>s_feature_compat</c> mask.
    /// <para>推导出的 <c>s_feature_compat</c> 掩码。</para></returns>
    internal uint DerivedCompatBits()
        => (SuperblockChecksum ? ErofsSuperblock.CompatSbChecksum : 0u)
            | (SourceMtimeFeature ? ErofsSuperblock.CompatMtime : 0u)
            | (SourceXattrFilterFeature ? ErofsSuperblock.CompatXattrFilter : 0u);

    /// <summary>
    /// Derives the incompat baseline a preset seeds: the compression padding bit plus, only when the
    /// layout actually needs it, the shared bit that licenses both pclusters spanning several blocks and
    /// the compression configuration records. A single-block pcluster limit with LZ4 needs neither.
    /// <para>推导预设所种下的 incompat 基线：压缩零填充位，加上仅在布局确实需要时的共享位（该位同时
    /// 许可跨多块的 pcluster 与压缩配置记录）。单块 pcluster 上限配 LZ4 两者都不需要。</para>
    /// </summary>
    /// <returns>The derived <c>s_feature_incompat</c> baseline.
    /// <para>推导出的 <c>s_feature_incompat</c> 基线。</para></returns>
    internal uint DerivedIncompatBaseline()
    {
        if (!Compress)
        {
            return 0u;
        }

        return ErofsSuperblock.IncompatLz4_0Padding
            | (ModernCompressionLayoutRequested ? ErofsSuperblock.IncompatBigPcluster : 0u);
    }

    /// <summary>
    /// Reports whether this build needs incompat bit 0x00000002, the single bit that licenses both
    /// multi-block pclusters and the compression configuration records. The bit is what turns
    /// <c>s_available_compr_algs</c> into an algorithm bitmap with configuration records following the
    /// superblock, so a build that declares it writes those records. Only the legacy preset and the
    /// compression switch decide the answer: a non-legacy compressed build always publishes the bit,
    /// because deriving it from the algorithm instead would make the superblock indistinguishable from a
    /// genuine legacy image that leaves the bit clear, and the parser could not tell which layout to replay.
    /// <para>判断本次构建是否需要 incompat 位 0x00000002——即同时许可多块 pcluster 与压缩配置记录的唯一位。
    /// 该位让 <c>s_available_compr_algs</c> 成为算法位图并在超级块后紧跟配置记录，因此声明它的构建必须写出
    /// 这些记录。结论只由 legacy 预设与压缩开关决定：非 legacy 的压缩构建一律发布该位，因为若改由算法推导，
    /// 得到的超级块与真正清掉该位的 legacy 镜像无法区分，解析器也就无法判断该重放哪种布局。</para>
    /// </summary>
    /// <returns><see langword="true"/> when bit 0x00000002 must be published.
    /// <para>必须发布 0x00000002 位时为 <see langword="true"/>。</para></returns>
    internal bool ModernCompressionLayoutRequested
        => Compress && !LegacyCompatibility;

    /// <summary>
    /// Reports whether this build may lay an uncompressed pcluster out with the interlaced byte rotation
    /// named by <c>Z_EROFS_ADVISE_INTERLACED_PCLUSTER</c>. The condition mirrors
    /// <c>lib/compress.c</c> in erofs-utils, which raises the advise when fragments are enabled, the dedupe
    /// dimension is not forced to whole-file comparison, and the build does not lay compressed data out
    /// unaligned.
    /// <para>判断本次构建是否可以按 <c>Z_EROFS_ADVISE_INTERLACED_PCLUSTER</c> 所指的字节旋转来存放未压缩
    /// pcluster。该条件与 erofs-utils 的 <c>lib/compress.c</c> 一致：启用 fragments、去重维度未强制为整文件
    /// 比较、且本次构建不以非对齐方式存放压缩数据时置起该 advise。</para>
    /// </summary>
    /// <value>
    /// <see langword="true"/> when every clause holds. A build without compression never rotates: the advise
    /// describes the layout of a compressed inode's plain pcluster, so it is only meaningful while the
    /// compression layout is in use.
    /// <para>每个子句都成立时为 <see langword="true"/>。未启用压缩的构建绝不旋转：该 advise 描述的是压缩 inode 的
    /// 原样 pcluster 布局，因此只在压缩布局生效时才有意义。</para></value>
    /// <remarks>
    /// The dedupe clause cannot distinguish the upstream <c>EROFS_DEDUPE_FORCE_ON</c> value from the two
    /// dimensions this repository models, because <see cref="ErofsDedupeMode"/> carries only whole-file and
    /// encoded-pcluster comparison and an explicitly requested whole-file dedupe is what
    /// <c>EROFS_DEDUPE_FORCE_ON</c> names. The unaligned clause is the one that matters in practice: a build
    /// that declares 48-bit block counts, or that pins the compressed extent size away from the block size,
    /// rotates nothing, exactly as <c>lib/compress.c</c> declines to set the advise for that layout.
    /// <para>去重子句无法把上游的 <c>EROFS_DEDUPE_FORCE_ON</c> 与本仓库建模的两个维度区分开，因为
    /// <see cref="ErofsDedupeMode"/> 只携带整文件比较与编码后 pcluster 比较，而显式请求的整文件去重正是
    /// <c>EROFS_DEDUPE_FORCE_ON</c> 所指之物。真正起作用的是非对齐子句：声明 48 位块数、或把压缩 extent 大小
    /// 钉到块大小之外的构建不会旋转任何内容，正如 <c>lib/compress.c</c> 拒绝为该布局置起该 advise。</para>
    /// </remarks>
    internal bool InterlacedPlainPclusters
        => Compress
           && Fragments
           && !(Dedup && DedupeMode == ErofsDedupeMode.WholeFile)
           && !CompressedDataLaysOutUnaligned;

    /// <summary>
    /// Reports whether this build would lay compressed data out without block alignment, which is the
    /// condition that makes the interlaced layout unavailable: it is raised by a declared 48-bit layout and
    /// by a compressed extent size that is not a whole number of blocks.
    /// <para>判断本次构建是否会以非块对齐的方式存放压缩数据，而这一条件正是交错布局不可用的原因：声明 48 位布局、
    /// 以及压缩 extent 大小不是整数个块，都会置起它。</para>
    /// </summary>
    /// <value><see langword="true"/> when either the 48-bit feature is declared or
    /// <see cref="MaxPclusterSize"/> is set to a value that is not a multiple of <see cref="BlockSize"/>.
    /// <para>声明了 48 位特性，或 <see cref="MaxPclusterSize"/> 被设为 <see cref="BlockSize"/> 的非整数倍时
    /// 为 <see langword="true"/>。</para></value>
    internal bool CompressedDataLaysOutUnaligned
        => Features.Contains(ErofsFeature.FortyEightBit)
           || (MaxPclusterSize > 0 && MaxPclusterSize % BlockSize != 0);

    /// <summary>
    /// Rejects a declaration that keeps the bit shared by <c>big_pcluster</c> and <c>compr_cfgs</c> while
    /// the legacy preset is on.
    /// <para>在 legacy 预设开启时拒绝仍保留 <c>big_pcluster</c> 与 <c>compr_cfgs</c> 共用位的声明。</para>
    /// </summary>
    /// <param name="incompatBits">Resolved incompat word to inspect.
    /// <para>待检查的已解析 incompat 字。</para></param>
    /// <exception cref="NotSupportedException">The declaration and the legacy preset contradict each
    /// other. <para>声明与 legacy 预设互相矛盾。</para></exception>
    private void RejectLegacyIncompatConflict(uint incompatBits)
    {
        if (!LegacyCompatibility || (incompatBits & ErofsSuperblock.IncompatBigPcluster) == 0)
        {
            return;
        }

        throw new NotSupportedException(
            "EROFS legacy (pre-5.13) compatibility cannot declare big_pcluster or compr_cfgs; the shared "
            + $"incompat bit 0x{ErofsSuperblock.IncompatBigPcluster:x8} requires at least Linux "
            + $"{ErofsFeatureTable.DescribeKernel(ErofsFeature.BigPcluster)}.");
    }

    /// <summary>
    /// Resolves the dot-omitted inode advise dimension, which has no superblock bit and therefore is kept
    /// outside <see cref="Features"/>.
    /// <para>解析 dot-omitted 的 inode advise 维度；它没有超级块位，因此保持在 <see cref="Features"/> 之外。</para>
    /// </summary>
    /// <exception cref="NotSupportedException">The caller forced the dimension on while the legacy preset
    /// forbids the post-5.13 advise bit. <para>调用方强制开启该维度，而 legacy 预设禁止这个 post-5.13 advise 位。</para></exception>
    private void ResolveDotOmitted()
    {
        if (DotOmitted == ErofsTriStateFeature.Enabled && LegacyCompatibility)
        {
            throw new NotSupportedException(
                "EROFS legacy (pre-5.13) compatibility cannot enable dot-omitted: pre-5.13 kernels read the "
                + "inode advise bit as nlink-1.");
        }

        if (DotOmitted == ErofsTriStateFeature.Auto)
        {
            DotOmitted = LegacyCompatibility ? ErofsTriStateFeature.Disabled : ErofsTriStateFeature.Enabled;
        }
    }
}

/// <summary>
/// The storage shape a source inode used for the part of its payload that does not fill a logical cluster.
/// A preserved rebuild replays the same shape instead of re-deriving it from the file size, because the
/// on-disk format distinguishes these cases, so the shape is kept instead of re-derived.
/// <para>源 inode 对填不满一个逻辑簇的那段负载所采用的存放形态。保留式重建按同一形态重放，而不是从文件
/// 大小重新推导，因为磁盘格式区分这几种情形。</para>
/// </summary>
internal enum ErofsFragmentShape
{
    /// <summary>The payload is stored in its own blocks or pclusters, with nothing in the packed inode.
    /// <para>负载存放在自己的块或 pcluster 里，packed inode 中没有任何内容。</para></summary>
    None = 0,

    /// <summary>The whole file lives in the packed inode, the shape <c>-E all-fragments</c> produces for
    /// sub-block files and what <c>h_clusterbits</c> bit 7 marks.
    /// <para>整个文件存放在 packed inode 里，即 <c>-E all-fragments</c> 为不足一块文件产生的形态，
    /// 由 <c>h_clusterbits</c> 的第 7 位标记。</para></summary>
    WholeFile = 1,

    /// <summary>Whole clusters sit in the file's own blocks while the final partial cluster lives in the
    /// packed inode, the tail fragment <c>-E fragments</c> produces.
    /// <para>完整簇存放在文件自身的块里，最后不足一簇的部分存放在 packed inode 里，即
    /// <c>-E fragments</c> 产生的尾部 fragment。</para></summary>
    Tail = 2,
}

/// <summary>
/// Declares one extra device for the EROFS device table: a tag, given either as text or as the raw slot bytes,
/// and the device size in bytes (block-aligned). The device table records the tag and the block count derived
/// from <paramref name="SizeBytes"/>.
/// <para>声明设备表中的一个额外设备：以文本或原始槽位字节给出的 tag，以及以字节表示、块对齐的设备大小。
/// 设备表记录该 tag 以及由 <paramref name="SizeBytes"/> 推导出的块数。</para>
/// </summary>
/// <param name="Tag">
/// Device tag text; validation requires it non-empty with a UTF-8 length below the on-disk tag field, unless
/// <see cref="TagBytes"/> carries the raw bytes the writer emits instead.
/// <para>设备 tag 文本；除非由 <see cref="TagBytes"/> 携带写入方实际输出的原始字节，否则校验要求非空且 UTF-8
/// 字节数小于磁盘 tag 字段宽度。</para>
/// </param>
/// <param name="SizeBytes">
/// Device size in bytes; validation requires a positive, block-aligned count.
/// <para>设备大小（字节）；校验要求为正数且按块对齐。</para>
/// </param>
public sealed record ErofsDeviceSpec(string Tag, long SizeBytes)
{
    /// <summary>
    /// Raw tag bytes the device slot holds; authoritative over <see cref="Tag"/> when present. The on-disk tag
    /// field is a fixed 64-byte array that upstream fills with a digest for blob-backed devices, so those bytes
    /// cannot always be represented as text and are carried verbatim instead.
    /// <para>设备槽携带的原始 tag 字节；存在时优先于 <see cref="Tag"/>。磁盘 tag 字段是固定 64 字节数组，上游
    /// 会为 blob 支撑的设备填入摘要，因此这些字节并不总能表示为文本，改为按原样携带。</para>
    /// </summary>
    /// <value>The authoritative raw bytes, or <see langword="null"/> when the descriptor was authored from text
    /// only and the writer has to fall back to <see cref="Tag"/>.
    /// <para>权威原始字节；描述符仅由文本构造时为 <see langword="null"/>，此时写入方回退到 <see cref="Tag"/>。</para></value>
    internal byte[]? TagBytes { get; init; }
}

internal sealed record ErofsSourceDevice(byte[] Tag, ulong Blocks, ulong UniAddr);