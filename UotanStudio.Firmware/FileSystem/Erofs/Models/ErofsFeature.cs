namespace UotanStudio.Firmware.FileSystem.Erofs.Models;

/// <summary>
/// Logical EROFS feature identifiers named after the upstream erofs-utils feature list. Several
/// identifiers share one physical superblock bit, exactly as upstream does: <see cref="CompressionConfigs"/>
/// and <see cref="BigPcluster"/> both live at incompat bit 0x00000002, <see cref="DeviceTable"/> and
/// <see cref="CompressionHead2"/> both at 0x00000008, and <see cref="Fragments"/> and <see cref="Dedupe"/>
/// both at 0x00000020. The identifiers are distinct flags so a caller can name either face of an alias;
/// the on-disk word cannot tell them apart.
/// <para>以上游 erofs-utils 特性名单命名的 EROFS 逻辑特性标识。多个标识共用一个物理超级块位，与上游一致：
/// <see cref="CompressionConfigs"/> 与 <see cref="BigPcluster"/> 同为 incompat 位 0x00000002，
/// <see cref="DeviceTable"/> 与 <see cref="CompressionHead2"/> 同为 0x00000008，
/// <see cref="Fragments"/> 与 <see cref="Dedupe"/> 同为 0x00000020。标识使用互不相同的位以便调用方指名
/// 别名的任一面；磁盘字本身无法区分它们。</para>
/// </summary>
[Flags]
public enum ErofsFeature : uint
{
    /// <summary>No feature.<para>无特性。</para></summary>
    None = 0,
    /// <summary>Incompat 0x00000001; compressed pclusters are right-aligned inside their blocks with zero
    /// padding in front. Accepted by Linux 5.4 and later.<para>incompat 0x00000001：压缩 pcluster 在块内右对齐、
    /// 前部为零填充。Linux 5.4 及以后接受。</para></summary>
    Lz4ZeroPadding = 1 << 0,
    /// <summary>Incompat 0x00000002; the superblock is followed by compression configuration records.
    /// Linux 5.13 and later.<para>incompat 0x00000002：超级块之后紧跟压缩配置记录。Linux 5.13 及以后。</para></summary>
    CompressionConfigs = 1 << 1,
    /// <summary>Incompat 0x00000002; a pcluster may span more than one filesystem block. Linux 5.13 and
    /// later.<para>incompat 0x00000002：单个 pcluster 可跨越多个文件系统块。Linux 5.13 及以后。</para></summary>
    BigPcluster = 1 << 2,
    /// <summary>Incompat 0x00000004; files are composed of fixed-size chunks held in extra devices. Linux 5.15
    /// and later. Read by the parser and never emitted.<para>incompat 0x00000004：文件由存放在额外设备中的定长
    /// 块组成。Linux 5.15 及以后。仅解析器读取，写出侧不生成。</para></summary>
    ChunkedFile = 1 << 3,
    /// <summary>Incompat 0x00000008; an extra-device table follows the superblock extension slots. Linux 5.16 and
    /// later.<para>incompat 0x00000008：超级块扩展槽之后是额外设备表。Linux 5.16 及以后。</para></summary>
    DeviceTable = 1 << 4,
    /// <summary>Incompat 0x00000008; the second compressed-inode head format shares this bit with the device
    /// table.<para>incompat 0x00000008：第二种压缩 inode 头部与设备表共用该位。</para></summary>
    CompressionHead2 = 1 << 5,
    /// <summary>Incompat 0x00000010; the final compressed cluster is stored inline with its indexes. Linux 5.17
    /// and later.<para>incompat 0x00000010：最后一个压缩簇与索引同块内联存放。Linux 5.17 及以后。</para></summary>
    Ztailpacking = 1 << 6,
    /// <summary>Incompat 0x00000020; sub-block tails live in the packed inode. Linux 6.1 and
    /// later.<para>incompat 0x00000020：不足一块的尾部存放在打包 inode 中。Linux 6.1 及以后。</para></summary>
    Fragments = 1 << 7,
    /// <summary>Incompat 0x00000020; identical payloads share physical storage; shares the fragments
    /// bit.<para>incompat 0x00000020：相同负载共享物理存储，与 fragments 共用该位。</para></summary>
    Dedupe = 1 << 8,
    /// <summary>Incompat 0x00000040; an extended-attribute prefix table is present. Linux 6.4 and later. Read by
    /// the parser and never emitted.<para>incompat 0x00000040：存在扩展属性前缀表。Linux 6.4 及以后。仅解析器读取，
    /// 写出侧不生成。</para></summary>
    XattrPrefixes = 1 << 9,
    /// <summary>Incompat 0x00000080; 48-bit block counts and root inode fields are in use. Linux 6.15 and later.
    /// Read by the parser and never emitted.<para>incompat 0x00000080：使用 48 位块数与根 inode 字段。Linux 6.15
    /// 及以后。仅解析器读取，写出侧不生成。</para></summary>
    FortyEightBit = 1 << 10,
    /// <summary>Incompat 0x00000100; compression configuration and part of the metadata live in a
    /// metabox.<para>incompat 0x00000100：压缩配置与部分元数据存放在 metabox 中。</para></summary>
    Metabox = 1 << 11,
    /// <summary>Compat 0x00000001; the superblock carries a CRC32c checksum.<para>compat 0x00000001：超级块带有
    /// CRC32c 校验和。</para></summary>
    SuperblockChecksum = 1 << 12,
    /// <summary>Compat 0x00000002; inode timestamps are present.<para>compat 0x00000002：存在 inode 时间戳。</para></summary>
    Mtime = 1 << 13,
    /// <summary>Compat 0x00000004; the extended-attribute filter table is present.<para>compat 0x00000004：存在
    /// 扩展属性过滤表。</para></summary>
    XattrFilter = 1 << 14,
    /// <summary>Compat 0x00000008; shared extended attributes live in the metabox. Read by the parser and never
    /// emitted.<para>compat 0x00000008：共享扩展属性位于 metabox。仅解析器读取，写出侧不生成。</para></summary>
    SharedEaInMetabox = 1 << 15,
    /// <summary>Compat 0x00000010; xattr prefixes are stored without compression. Read by the parser and never
    /// emitted.<para>compat 0x00000010：xattr 前缀不经压缩直接存放。仅解析器读取，写出侧不生成。</para></summary>
    PlainXattrPrefixes = 1 << 16,
    /// <summary>Compat 0x00000020; inode-shared extended attributes are in use. Read by the parser and never
    /// emitted.<para>compat 0x00000020：使用 inode 间共享的扩展属性。仅解析器读取，写出侧不生成。</para></summary>
    SharedInodeXattrs = 1 << 17,
}

/// <summary>
/// Reports whether the managed writer can produce the on-disk structures a feature stands for. A
/// read-only feature is decoded from a source image but cannot be re-emitted, so declaring it for a
/// build is an error rather than a silent downgrade.
/// <para>标识受管写入方能否产出该特性对应的磁盘结构。只读特性能从源镜像解码但无法重新写出，因此把
/// 它声明给一次构建属于错误，而不是静默降级。</para>
/// </summary>
public enum ErofsFeatureCapability
{
    /// <summary>The writer emits the structures behind the bit.<para>写入方会输出该位背后的结构。</para></summary>
    Emittable,
    /// <summary>Only the parser reads the bit; the writer cannot produce it.<para>仅解析器读取该位，写入方无法产出。</para></summary>
    ReadOnlyForEmit,
}

/// <summary>
/// Selects between inheriting a value, forcing it off and forcing it on, for feature dimensions that
/// have no superblock bit of their own.
/// <para>用于没有自有超级块位的特性维度，在继承、强制关闭与强制开启之间选择。</para>
/// </summary>
public enum ErofsTriStateFeature
{
    /// <summary>Derive the value from the remaining options, matching an image built without explicit
    /// declarations.<para>由其余选项推导，与未显式声明特性的镜像构建行为一致。</para></summary>
    Auto,
    /// <summary>Never use the feature.<para>绝不使用该特性。</para></summary>
    Disabled,
    /// <summary>Always use the feature where the layout allows it.<para>在布局允许时始终使用该特性。</para></summary>
    Enabled,
}

/// <summary>
/// Declares the EROFS superblock feature words for one build. The words are the on-disk
/// <c>s_feature_compat</c> and <c>s_feature_incompat</c> masks; the logical identifiers of
/// <see cref="ErofsFeature"/> resolve to them through the upstream feature table. A set created from
/// <see cref="Auto"/> leaves both words to the writer's own derivation, while an explicit set is the
/// authority the writer must not exceed for incompat bits.
/// <para>声明一次构建使用的 EROFS 超级块特性字。两个字就是磁盘上的 <c>s_feature_compat</c> 与
/// <c>s_feature_incompat</c> 掩码；<see cref="ErofsFeature"/> 的逻辑标识经上游特性表解析到这些位。由
/// <see cref="Auto"/> 创建的集合把两个字留给写入方自行推导，而显式集合则是写入方不得超出源的 incompat 位权威。</para>
/// </summary>
/// <param name="IsAuto">Keeps both words derived when <see langword="true"/>.
/// <para>为 <see langword="true"/> 时两个字均由写入方推导。</para></param>
/// <param name="CompatBits">Raw <c>s_feature_compat</c> mask; empty while <paramref name="IsAuto"/>.
/// <para>原始 <c>s_feature_compat</c> 掩码；<paramref name="IsAuto"/> 期间为空。</para></param>
/// <param name="IncompatBits">Raw <c>s_feature_incompat</c> mask; empty while <paramref name="IsAuto"/>.
/// <para>原始 <c>s_feature_incompat</c> 掩码；<paramref name="IsAuto"/> 期间为空。</para></param>
public readonly record struct ErofsFeatureSet(bool IsAuto, uint CompatBits, uint IncompatBits)
{
    /// <summary>The derived declaration: both feature words follow the writer's own layout
    /// computation.<para>推导式声明：两个特性字都跟随写入方自己的布局计算。</para></summary>
    /// <value>A set whose <see cref="IsAuto"/> is <see langword="true"/> and whose words are
    /// empty.<para><see cref="IsAuto"/> 为 <see langword="true"/> 且两个字为空的集合。</para></value>
    public static ErofsFeatureSet Auto => new(true, 0, 0);

    /// <summary>Reports whether one logical feature is present in the declared words.<para>判断声明字中是否存在某个逻辑特性。</para></summary>
    /// <param name="feature">Feature identifier to test; alias names test the same physical
    /// bit.<para>待测特性标识；别名标识测试同一物理位。</para></param>
    /// <returns><see langword="true"/> when the feature's physical bit is set in its word.
    /// <para>该特性的物理位在其所属字中已置位时为 <see langword="true"/>。</para></returns>
    public bool Contains(ErofsFeature feature)
    {
        var bit = ErofsFeatureTable.BitOf(feature);
        return ((ErofsFeatureTable.IsCompat(feature) ? CompatBits : IncompatBits) & bit) != 0;
    }

    /// <summary>Returns the declaration with one feature's physical bit set.<para>返回置位某个特性物理位后的声明。</para></summary>
    /// <param name="feature">Feature to add; a read-only feature is still only a word change here, and
    /// validation rejects it for a build.<para>待加入的特性；只读特性在本方法中也仅改变字，构建校验会拒绝它。</para></param>
    /// <returns>An explicit set carrying the added bit. <para>携带新增位的显式集合。</para></returns>
    public ErofsFeatureSet With(ErofsFeature feature)
    {
        var bit = ErofsFeatureTable.BitOf(feature);
        return ErofsFeatureTable.IsCompat(feature)
            ? new ErofsFeatureSet(false, CompatBits | bit, IncompatBits)
            : new ErofsFeatureSet(false, CompatBits, IncompatBits | bit);
    }

    /// <summary>Returns the declaration with one feature's physical bit cleared.<para>返回清除某个特性物理位后的声明。</para></summary>
    /// <param name="feature">Feature to clear; clearing either name of an alias removes the shared
    /// bit.<para>待清除的特性；清除别名的任一名都会移除共用位。</para></param>
    /// <returns>An explicit set without the bit. <para>不含该位的显式集合。</para></returns>
    public ErofsFeatureSet Without(ErofsFeature feature)
    {
        var bit = ErofsFeatureTable.BitOf(feature);
        return ErofsFeatureTable.IsCompat(feature)
            ? new ErofsFeatureSet(false, CompatBits & ~bit, IncompatBits)
            : new ErofsFeatureSet(false, CompatBits, IncompatBits & ~bit);
    }

    /// <summary>Builds an explicit declaration from raw superblock words.<para>由原始超级块字构造显式声明。</para></summary>
    /// <param name="compatBits">The <c>s_feature_compat</c> mask as it appears on disk.
    /// <para>磁盘上出现的 <c>s_feature_compat</c> 掩码。</para></param>
    /// <param name="incompatBits">The <c>s_feature_incompat</c> mask as it appears on disk.
    /// <para>磁盘上出现的 <c>s_feature_incompat</c> 掩码。</para></param>
    /// <returns>An explicit set with both words preserved.
    /// <para>保留两个字的显式集合。</para></returns>
    public static ErofsFeatureSet FromWords(uint compatBits, uint incompatBits)
        => new(false, compatBits, incompatBits);

    /// <summary>Major component of the oldest Linux release that can mount every declared incompat
    /// feature.<para>可挂载全部已声明 incompat 特性的最老 Linux 主版本号。</para></summary>
    /// <value>Zero when no incompat feature is declared or the declaration is derived; otherwise the
    /// highest upstream kernel version among the declared incompat bits.
    /// <para>未声明 incompat 特性或声明为推导时为零；否则为已声明 incompat 位中最高的上游内核版本。</para></value>
    public int MinimumKernelMajor => ErofsFeatureTable.MinimumKernelMajor(CompatBits, IncompatBits);

    /// <summary>Minor component of the oldest Linux release that can mount every declared incompat
    /// feature.<para>可挂载全部已声明 incompat 特性的最老 Linux 次版本号。</para></summary>
    /// <value>Zero when no incompat feature is declared or the declaration is derived; otherwise the minor
    /// component paired with <see cref="MinimumKernelMajor"/>.
    /// <para>未声明 incompat 特性或声明为推导时为零；否则为与 <see cref="MinimumKernelMajor"/> 配对的次版本。</para></value>
    public int MinimumKernelMinor => ErofsFeatureTable.MinimumKernelMinor(CompatBits, IncompatBits);

    /// <summary>Formats the declared words as the upstream feature names the parser reports.
    /// <para>把声明字格式化成解析器使用的上游特性名。</para></summary>
    /// <returns>Comma-joined upstream names, or <c>"auto"</c> while the declaration is
    /// derived.<para>以上游名以逗号连接；推导式声明时返回 <c>"auto"</c>。</para></returns>
    public override string ToString()
        => IsAuto ? "auto" : ErofsFeatureTable.Describe(CompatBits, IncompatBits);
}