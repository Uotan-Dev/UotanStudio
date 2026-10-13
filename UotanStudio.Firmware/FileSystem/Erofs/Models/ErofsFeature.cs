namespace UotanStudio.Firmware.FileSystem.Erofs.Models;

/// <summary>
/// Logical EROFS feature identifiers named after the format's feature list. Several identifiers share one physical superblock bit, so they are distinct flags a caller can use to name either face of an alias; the on-disk word cannot tell them apart.
/// <para>按格式特性表命名的 EROFS 逻辑特性标识。多个标识共用同一个超级块物理位，因此它们是彼此独立的标志，调用方可据此指名别名的任一面；磁盘上的字无法区分它们。</para>
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
    /// <summary>Incompat 0x00000004; files are composed of fixed-size chunks addressed by a chunk index table
    /// that follows the inode record. Linux 5.15 and later.<para>incompat 0x00000004：文件由定长块组成，
    /// 由紧随 inode 记录之后的 chunk 索引表寻址。Linux 5.15 及以后。</para></summary>
    ChunkedFile = 1 << 3,
    /// <summary>Incompat 0x00000008; an extra-device table follows the superblock extension slots. Linux 5.16 and
    /// later. This is the face of the shared bit the writer lays out, and it is licensed only by a layout that
    /// carries extra-device slots.<para>incompat 0x00000008：超级块扩展槽之后是额外设备表。Linux 5.16 及以后。
    /// 这是共用位可由写入方落盘的一面，且只有携带额外设备槽位的布局才会置位。</para></summary>
    DeviceTable = 1 << 4,
    /// <summary>Incompat 0x00000008; the second compressed-inode head format shares this bit with the device
    /// table. The writer emits HEAD2 index rows for it when the source inode named a secondary algorithm through
    /// <c>h_algorithmtype</c>; erofs-utils keeps no setter for this face and pins the HEAD2 algorithm nibble to
    /// zero, so a build that declares the bit without extra-device slots and without any HEAD2 row cannot honour
    /// it.<para>incompat 0x00000008：第二种压缩 inode 头部格式与设备表共用该位。当源 inode 经
    /// <c>h_algorithmtype</c> 指名了第二算法时，写入方为该面发出 HEAD2 索引行；erofs-utils 未为该面提供置位
    /// 接口，并把 HEAD2 算法半字节固定为零，因此既无额外设备槽又没有任何 HEAD2 行的构建无法兑现该位。</para></summary>
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
    /// <summary>Incompat 0x00000040; an extended-attribute prefix table is present. Linux 6.4 and later.
    /// <para>incompat 0x00000040：存在扩展属性前缀表。Linux 6.4 及以后。</para></summary>
    XattrPrefixes = 1 << 9,
    /// <summary>Incompat 0x00000080; 48-bit block counts and root inode fields are in use. Linux 6.15
    /// and later.<para>incompat 0x00000080：使用 48 位块数与根 inode 字段。Linux 6.15
    /// 及以后。</para></summary>
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
    /// <summary>Compat 0x00000008; shared extended attributes live in the metabox. The writer sets this bit only
    /// when a metabox container holds the shared extended-attribute area.<para>compat 0x00000008：共享扩展属性位于
    /// metabox。仅当 metabox 容器承载共享扩展属性区时写入方才置该位。</para></summary>
    SharedEaInMetabox = 1 << 15,
    /// <summary>Compat 0x00000010; xattr prefixes are stored without compression.<para>compat 0x00000010：xattr
    /// 前缀不经压缩直接存放。</para></summary>
    PlainXattrPrefixes = 1 << 16,
    /// <summary>Compat 0x00000020; inode-shared extended attributes are in use.<para>compat 0x00000020：使用
    /// inode 间共享的扩展属性。</para></summary>
    SharedInodeXattrs = 1 << 17,
}

/// <summary>
/// Reports whether the managed writer can produce the on-disk structures a feature stands for. Every name in the upstream table is emittable; a contextual name is one whose bit is honoured only when the layout carries the structures behind it.
/// <para>报告托管写入器能否产出某特性所代表的磁盘结构。上游表中每个名称都可写出；上下文名称指仅在布局具备相应结构时才生效的位。</para>
/// </summary>
public enum ErofsFeatureCapability
{
    /// <summary>The writer emits the structures behind the bit.<para>写入方会输出该位背后的结构。</para></summary>
    Emittable,
    /// <summary>Whether the writer can produce this face of the bit depends on context the feature word does
    /// not carry, such as the remaining fields of the layout or the shapes collected from a source image, so
    /// the caller makes the judgement and the table neither offers the bit as unconditionally writable nor
    /// refuses it.<para>该位的这一面能否写出，取决于特性字本身不携带的上下文，例如布局的其余字段或从源镜像采集到的形状；
    /// 判定由调用方结合该上下文完成，真值表既不把该位当作无条件可写，也不拒绝它。</para></summary>
    Contextual = 1,
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
/// Declares the EROFS superblock feature words for one build. The words are the on-disk s_feature_compat and s_feature_incompat masks; a set created from an image leaves both to the writer's derivation, while an explicit set is the authority the writer must not exceed for incompat bits.
/// <para>声明某次构建的 EROFS 超级块特性字，即磁盘上的 s_feature_compat 与 s_feature_incompat 掩码；由镜像创建的集合把两个字留给写入器自行推导，显式集合则是写入器不得超越 incompat 位的权威。</para>
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
    /// validation rejects it for a build. A contextual feature sets the shared bit, and whether the resulting
    /// declaration is writable is judged against the layout context.<para>待加入的特性；只读特性在本方法中也仅改变字，
    /// 构建校验会拒绝它。上下文相关的特性置位的是共用位，所得声明能否写出需按布局上下文判定。</para></param>
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
    /// highest upstream kernel version among the declared incompat bits. A declaration carries no
    /// <c>s_available_compr_algs</c> word, so the per-algorithm floors erofs-utils adds from it are outside
    /// this value; a decoded superblock reports those floors together with its feature words.
    /// <para>未声明 incompat 特性或声明为推导时为零；否则为已声明 incompat 位中最高的上游内核版本。声明不携带
    /// <c>s_available_compr_algs</c> 字，因此 erofs-utils 依据该字追加的逐算法下限不在本值之内；解码出的超级块会
    /// 把这些下限与特性字一并报告。</para></value>
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