using UotanStudio.Firmware.FileSystem.Erofs.Models;
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
    /// <value>0 selects the builder default (<c>ErofsCompression.MaxPclusterSize</c>); validation
    /// requires a block-aligned value between <see cref="BlockSize"/> and that default, and
    /// <see cref="LegacyCompatibility"/> pins it to the block size.
    /// <para>0 表示使用打包器默认值（<c>ErofsCompression.MaxPclusterSize</c>）；校验要求其为
    /// <see cref="BlockSize"/> 到该默认值之间且按块对齐的数值，且 <see cref="LegacyCompatibility"/>
    /// 会将其钉死为块大小。</para></value>
    public int MaxPclusterSize { get; set; }

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

    /// <summary>LZ4 maximum match distance; zero denotes the upstream 65535-byte default.
    /// <para>LZ4 最大匹配距离；0 表示上游默认的 65535 字节。</para></summary>
    /// <value>When set, the LZ4 planner caps each pcluster's match distance and input span to this
    /// distance. <para>设置后，LZ4 规划器把每个 pcluster 的匹配距离与输入跨度限制到该距离。</para></value>
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

    /// <summary>Whether to write the upstream superblock checksum feature.
    /// <para>是否写入上游的超级块校验和特性。</para></summary>
    /// <value>On by default; the parser re-computes CRC32c over the superblock window and rejects a
    /// mismatch. <para>默认开启；解析器会对超级块窗口重新计算 CRC32c，不匹配即拒绝。</para></value>
    public bool SuperblockChecksum { get; set; } = true;

    internal Dictionary<ulong, ErofsSourceInode>? SourceInodes { get; set; }
    internal Dictionary<string, ErofsSourceInode>? SourceInodesByPath { get; set; }
    internal Dictionary<ErofsCompressionAlgorithm, ErofsOptions>? SourceCompressionOptions { get; set; }
    internal bool SourceMtimeFeature { get; set; }
    internal bool SourceXattrFilterFeature { get; set; }
    internal bool SourceSharedEaInMetabox { get; set; }
    internal bool SourceMetaboxCompressed { get; set; }
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
    /// Selects the upstream <c>-E dot-omitted</c> dimension, the
    /// <see cref="ErofsInode.AdviseBitNlinkOneOrDotOmitted"/> inode advise bit. It has no superblock
    /// feature bit of its own, so it is declared beside <see cref="Features"/> rather than inside it.
    /// <para>选择上游 <c>-E dot-omitted</c> 维度，即
    /// <see cref="ErofsInode.AdviseBitNlinkOneOrDotOmitted"/> inode advise 位。该维度没有自有的超级块
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

    /// <summary>Stores file tails smaller than one block as packed fragments.
    /// <para>把不足一块的文件尾部作为打包 fragment 存放。</para></summary>
    /// <value>Off by default; enabling it forces <see cref="TailPacking"/> off, and
    /// <see cref="LegacyCompatibility"/> clears it during validation.
    /// <para>默认关闭；开启会迫使 <see cref="TailPacking"/> 关闭，且校验时
    /// <see cref="LegacyCompatibility"/> 会将其清零。</para></value>
    public bool Fragments { get; set; }

    /// <summary>Shares physical storage between files with identical content.
    /// <para>让内容相同的文件共享同一份物理数据。</para></summary>
    /// <value>Off by default; when on, data blocks and fragments are reused by content hash, and
    /// <see cref="LegacyCompatibility"/> clears it during validation.
    /// <para>默认关闭；开启后按内容哈希复用数据块与 fragment，且校验时
    /// <see cref="LegacyCompatibility"/> 会将其清零。</para></value>
    public bool Dedup { get; set; }

    /// <summary>Places the compression configuration and part of the metadata in a metabox.
    /// <para>将压缩配置与部分元数据放入 metabox。</para></summary>
    /// <value>Off by default; enabling it forces <see cref="TailPacking"/> off, and
    /// <see cref="LegacyCompatibility"/> clears it during validation.
    /// <para>默认关闭；开启会迫使 <see cref="TailPacking"/> 关闭，且校验时
    /// <see cref="LegacyCompatibility"/> 会将其清零。</para></value>
    public bool Metabox { get; set; }

    /// <summary>Number of extra devices declared in the on-disk device table.
    /// <para>磁盘设备表中声明的额外设备数量。</para></summary>
    /// <value>Default <c>0</c>; must be non-negative, and a positive count requires the same number
    /// of <see cref="ExtraDeviceImages"/> descriptors.
    /// <para>默认 <c>0</c>；必须非负，且正数时要求数量一致的 <see cref="ExtraDeviceImages"/>
    /// 描述符。</para></value>
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
    internal IReadOnlyDictionary<string, UotanStudio.Firmware.FileSystem.Metadata.FsConfigEntry>? FsConfig { get; set; }
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

        CompressionLevel = Math.Clamp(CompressionLevel, 1, 9);
        if (!Enum.IsDefined(Algorithm))
        {
            throw new ArgumentOutOfRangeException(nameof(Algorithm), Algorithm,
                "Unknown EROFS compression algorithm.");
        }

        if (MaxPclusterSize == 0) MaxPclusterSize = ErofsCompression.MaxPclusterSize;
        if (LegacyCompatibility)
        {
            if (Algorithm != ErofsCompressionAlgorithm.Lz4)
                throw new NotSupportedException(
                    $"EROFS legacy (pre-5.13) layout supports only LZ4; {Algorithm} requires the modern big-pcluster feature.");
            MaxPclusterSize = BlockSize;
            UseCompactedIndexes = false;
            CompressedTailPacking = false;
            Fragments = false;
            Dedup = false;
            Metabox = false;
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

        if (string.IsNullOrEmpty(VolumeName) && UotanStudio.Firmware.FileSystem.Rebuilding.FileSystemEditPlan.Current is null)
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

        ResolveFeatureDeclaration();
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

        RejectReadOnlyIncompat(incompatBits);
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
    private uint DerivedCompatBits()
        => (SuperblockChecksum ? ErofsSuperblock.CompatSbChecksum : 0u)
            | (SourceMtimeFeature ? ErofsSuperblock.CompatMtime : 0u)
            | (SourceXattrFilterFeature ? ErofsSuperblock.CompatXattrFilter : 0u);

    /// <summary>
    /// Derives the incompat baseline a preset seeds: the compression padding bit plus big pclusters for a
    /// modern compressed layout, and nothing when data is stored plain.
    /// <para>推导预设所种下的 incompat 基线：现代压缩布局取压缩零填充位加 big pcluster，明文存储时不取任何位。</para>
    /// </summary>
    /// <returns>The derived <c>s_feature_incompat</c> baseline.
    /// <para>推导出的 <c>s_feature_incompat</c> 基线。</para></returns>
    private uint DerivedIncompatBaseline()
    {
        if (!Compress)
        {
            return 0u;
        }

        return ErofsSuperblock.IncompatLz4_0Padding
            | (LegacyCompatibility ? 0u : ErofsSuperblock.IncompatBigPcluster);
    }

    /// <summary>
    /// Rejects a declaration asking for an incompat bit the writer can only parse, naming the upstream
    /// feature and its oldest mounting kernel.
    /// <para>拒绝要求写入方仅能解析的 incompat 位的声明，并给出上游特性名与其最老可挂载内核。</para>
    /// </summary>
    /// <param name="incompatBits">Resolved incompat word to inspect.
    /// <para>待检查的已解析 incompat 字。</para></param>
    /// <exception cref="ArgumentOutOfRangeException">The declaration asks for a read-only incompat
    /// feature. <para>声明要求只读的 incompat 特性。</para></exception>
    private void RejectReadOnlyIncompat(uint incompatBits)
    {
        foreach (var row in ErofsFeatureTable.Entries)
        {
            if (row.IsCompat || row.Capability != ErofsFeatureCapability.ReadOnlyForEmit
                || (incompatBits & row.Bit) == 0)
            {
                continue;
            }

            throw new ArgumentOutOfRangeException(nameof(Features),
                $"EROFS feature '{ErofsFeatureTable.NameOf(row.Feature)}' (oldest kernel "
                + $"{ErofsFeatureTable.DescribeKernel(row.Feature)}) is read-only for the writer and cannot be "
                + "declared for a build; declared read-only incompat features: "
                + $"{ErofsFeatureTable.ReadOnlyIncompatNames(incompatBits)}.");
        }
    }

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

internal sealed record ErofsSourceInode(ulong Mtime, uint MtimeNanoseconds, bool Extended,
    bool Compressed, ErofsCompressionAlgorithm Algorithm, bool CompactIndexes, bool InlineData, int LogicalClusterSize,
    string? Path = null, int? SourceExtentClusterSize = null, int? SourceExtentRecordSize = null,
    bool NlinkOneOrDotOmitted = false);

/// <summary>
/// Declares one extra device for the EROFS device table: a NUL-safe tag and the device size in bytes
/// (block-aligned). The device table records the tag and the block count derived from <paramref name="SizeBytes"/>.
/// <para>声明设备表中的一个额外设备：以 NUL 结尾的 tag 与以字节表示、块对齐的设备大小。
/// 设备表记录该 tag 以及由 <paramref name="SizeBytes"/> 推导出的块数。</para>
/// </summary>
/// <param name="Tag">
/// Device tag text; validation requires it non-empty with UTF-8 length below the on-disk tag field.
/// <para>设备 tag 文本；校验要求非空且 UTF-8 字节数小于磁盘 tag 字段宽度。</para>
/// </param>
/// <param name="SizeBytes">
/// Device size in bytes; validation requires a positive, block-aligned count.
/// <para>设备大小（字节）；校验要求为正数且按块对齐。</para>
/// </param>
public sealed record ErofsDeviceSpec(string Tag, long SizeBytes);

internal sealed record ErofsSourceDevice(byte[] Tag, ulong Blocks, ulong UniAddr);