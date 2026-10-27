namespace UotanStudio.Firmware.FileSystem.Erofs.Models;

/// <summary>
/// Single source of truth mapping each <see cref="ErofsFeature"/> to its on-disk word, bit, feature
/// name, oldest mounting kernel and writer capability. The name is the one the superblock summary prints,
/// and the table is the single bit-to-name mapping shared by the parser and the writer. Names that
/// alias one bit share that bit's kernel floor, while capability belongs to the name: a bit the writer can
/// lay out through one name marks the other name <see cref="ErofsFeatureCapability.Contextual"/>, because
/// whether that face is writable depends on fields the feature word does not carry.
/// <para>把每个 <see cref="ErofsFeature"/> 映射到磁盘字、位、特性名、最老可挂载内核与写入方能力的唯一真值表。
/// 特性名即超级块摘要打印的名称，该表是解析器与写入方共用的单一位到名称映射。共用一位的名称共享该位的内核下限，
/// 而能力归属于名称：写入方能经某个名称落盘的位，
/// 会把另一个名称标为 <see cref="ErofsFeatureCapability.Contextual"/>，因为该面能否写出取决于特性字未携带的字段。</para>
/// </summary>
internal static class ErofsFeatureTable
{
    /// <summary>Oldest kernel that mounts a bitmap carrying MicroLZMA once <c>s_available_compr_algs</c>
    /// raises the incompat floor.
    /// <para>依据 <c>s_available_compr_algs</c> 抬高下限后，携带 MicroLZMA 的位图所能挂载的最老内核。</para></summary>
    private const int LzmaKernelFloor = 516;

    /// <summary>Oldest kernel that mounts a bitmap carrying DEFLATE.<para>携带 DEFLATE 的位图所能挂载的最老内核。</para></summary>
    private const int DeflateKernelFloor = 606;

    /// <summary>Oldest kernel that mounts a bitmap carrying Zstandard.<para>携带 Zstandard 的位图所能挂载的最老内核。</para></summary>
    private const int ZstdKernelFloor = 610;

    /// <summary>One row of the feature table.<para>特性表中的一行。</para></summary>
    /// <param name="Feature">Logical identifier.<para>逻辑标识。</para></param>
    /// <param name="IsCompat">Whether the word is <c>s_feature_compat</c>.<para>该位属于 <c>s_feature_compat</c> 时为真。</para></param>
    /// <param name="Bit">Physical bit value.<para>物理位值。</para></param>
    /// <param name="Name">Feature name as printed in the superblock summary.<para>超级块摘要中输出的特性名。</para></param>
    /// <param name="Kernel">Oldest mounting kernel, encoded as major * 100 + minor.<para>最老可挂载内核，编码为 major * 100 + minor。</para></param>
    /// <param name="Capability">Whether the managed writer can emit the structures this name stands for, given
    /// the bit it shares.<para>结合该名称所共用的位，受管写入方能否输出该名称所指的结构。</para></param>
    internal sealed record Descriptor(ErofsFeature Feature, bool IsCompat, uint Bit, string Name, int Kernel,
        ErofsFeatureCapability Capability);

    private static readonly Descriptor[] Descriptors =
    [
        new(ErofsFeature.Lz4ZeroPadding, false, ErofsSuperblock.IncompatLz4_0Padding, "lz4_0padding", 504,
            ErofsFeatureCapability.Emittable),
        new(ErofsFeature.CompressionConfigs, false, ErofsSuperblock.IncompatCompressCfgs, "compr_cfgs", 513,
            ErofsFeatureCapability.Emittable),
        new(ErofsFeature.BigPcluster, false, ErofsSuperblock.IncompatBigPcluster, "big_pcluster", 513,
            ErofsFeatureCapability.Emittable),
        new(ErofsFeature.ChunkedFile, false, ErofsSuperblock.IncompatChunkedFile, "chunked_file", 515,
            ErofsFeatureCapability.Emittable),
        new(ErofsFeature.DeviceTable, false, ErofsSuperblock.IncompatDeviceTable, "device_table", 516,
            ErofsFeatureCapability.Emittable),
        new(ErofsFeature.CompressionHead2, false, ErofsSuperblock.IncompatCompressHead2, "compr_head2", 516,
            ErofsFeatureCapability.Contextual),
        new(ErofsFeature.Ztailpacking, false, ErofsSuperblock.IncompatZtailpacking, "ztailpacking", 517,
            ErofsFeatureCapability.Emittable),
        new(ErofsFeature.Fragments, false, ErofsSuperblock.IncompatFragments, "fragments", 601,
            ErofsFeatureCapability.Emittable),
        new(ErofsFeature.Dedupe, false, ErofsSuperblock.IncompatDedupe, "dedupe", 601,
            ErofsFeatureCapability.Emittable),
        new(ErofsFeature.XattrPrefixes, false, ErofsSuperblock.IncompatXattrPrefixes, "xattr_prefixes", 604,
            ErofsFeatureCapability.Emittable),
        new(ErofsFeature.FortyEightBit, false, ErofsSuperblock.Incompat48Bit, "48bit", 615,
            ErofsFeatureCapability.Emittable),
        new(ErofsFeature.Metabox, false, ErofsSuperblock.IncompatMetabox, "metabox", 617,
            ErofsFeatureCapability.Emittable),
        new(ErofsFeature.SuperblockChecksum, true, ErofsSuperblock.CompatSbChecksum, "sb_csum", 0,
            ErofsFeatureCapability.Emittable),
        new(ErofsFeature.Mtime, true, ErofsSuperblock.CompatMtime, "mtime", 0,
            ErofsFeatureCapability.Emittable),
        new(ErofsFeature.XattrFilter, true, ErofsSuperblock.CompatXattrFilter, "xattr_filter", 0,
            ErofsFeatureCapability.Emittable),
        new(ErofsFeature.SharedEaInMetabox, true, ErofsSuperblock.CompatSharedEaInMetabox, "shared_ea_in_metabox", 0,
            ErofsFeatureCapability.Contextual),
        new(ErofsFeature.PlainXattrPrefixes, true, ErofsSuperblock.CompatPlainXattrPfx, "plain_xattr_pfx", 0,
            ErofsFeatureCapability.Contextual),
        new(ErofsFeature.SharedInodeXattrs, true, ErofsSuperblock.CompatIshareXattrs, "ishare_xattrs", 0,
            ErofsFeatureCapability.Emittable),
    ];

    /// <summary>Every row, in the bit order of its feature word.<para>按其所属特性字的位顺序排列的全部行。</para></summary>
    internal static IReadOnlyList<Descriptor> Entries => Descriptors;

    /// <summary>Physical bit of a logical identifier.<para>逻辑标识的物理位。</para></summary>
    /// <param name="feature">Identifier to resolve.<para>待解析的标识。</para></param>
    /// <returns>The bit value stored in its word. <para>所属字中存放的位值。</para></returns>
    internal static uint BitOf(ErofsFeature feature)
        => Row(feature).Bit;

    /// <summary>Reports whether a logical identifier lives in the compat word.<para>判断逻辑标识是否属于 compat 字。</para></summary>
    /// <param name="feature">Identifier to test.<para>待测标识。</para></param>
    /// <returns><see langword="true"/> for a compat feature. <para>compat 特性时为 <see langword="true"/>。</para></returns>
    internal static bool IsCompat(ErofsFeature feature)
        => Row(feature).IsCompat;

    /// <summary>Every name in the table sharing one physical bit in one feature word.<para>列出某个特性字内共用同一物理位的全部名称。</para></summary>
    /// <param name="bit">Physical bit value as it appears in its word.<para>磁盘上该字中的物理位值。</para></param>
    /// <param name="isCompat">Word to search: <see langword="true"/> selects <c>s_feature_compat</c> and
    /// <see langword="false"/> selects <c>s_feature_incompat</c>, since one value appears in both
    /// words.<para>待检索的字：<see langword="true"/> 选 <c>s_feature_compat</c>，<see langword="false"/> 选
    /// <c>s_feature_incompat</c>，因为同一数值在两个字中都会出现。</para></param>
    /// <returns>The names in table order, so an alias pair reads as <c>compr_cfgs/big_pcluster</c> once joined,
    /// or an empty list when no row carries that bit in the selected word.
    /// <para>按表序的名称，因此别名对连接后即 <c>compr_cfgs/big_pcluster</c>；选定字中没有行携带该位时返回空表。</para></returns>
    internal static IReadOnlyList<string> AliasNames(uint bit, bool isCompat)
        => [.. Descriptors
            .Where(row => row.IsCompat == isCompat && row.Bit == bit)
            .Select(row => row.Name)];

    /// <summary>Names one physical incompat bit under the layout context that produced it.<para>按产出该位的布局上下文为物理 incompat 位命名。</para></summary>
    /// <param name="bit">Physical bit as it appears in <c>s_feature_incompat</c>.<para>磁盘 <c>s_feature_incompat</c>
    /// 中的物理位。</para></param>
    /// <param name="extraDeviceCount">Extra-device slots the image or build plan carries, read from
    /// <c>s_extra_devices</c>. It is the context that tells the device table and the second compressed-inode
    /// head format apart, because both name bit 0x00000008 and only the device table has on-disk structures the
    /// writer lays out.<para>镜像或构建计划携带的额外设备槽位数，取自 <c>s_extra_devices</c>。正是该上下文区分设备表与
    /// 第二种压缩 inode 头部：二者同指 0x00000008 位，而只有设备表拥有写入方能够落盘的磁盘结构。</para></param>
    /// <returns><c>device_table</c> for bit 0x00000008 with a positive <paramref name="extraDeviceCount"/> and
    /// <c>compr_head2</c> for that bit without any extra device; the joined alias names for the pairs sharing
    /// 0x00000002 and 0x00000020; the single name of every other known bit; and the hexadecimal value
    /// for a bit the table does not carry.<para>0x00000008 位在 <paramref name="extraDeviceCount"/> 为正时返回
    /// <c>device_table</c>，无额外设备时返回 <c>compr_head2</c>；0x00000002 与 0x00000020 的别名对返回连接后的名称；
    /// 其余已知位返回其唯一名称；表中未收录的位返回十六进制值。</para></returns>
    internal static string NameForIncompatBit(uint bit, int extraDeviceCount)
    {
        if (bit == ErofsSuperblock.IncompatDeviceTable)
        {
            return extraDeviceCount > 0
                ? NameOf(ErofsFeature.DeviceTable)
                : NameOf(ErofsFeature.CompressionHead2);
        }

        var aliases = AliasNames(bit, false);
        return aliases.Count == 0
            ? $"0x{bit:x8}"
            : string.Join("/", aliases);
    }

    /// <summary>Name of a logical identifier as the superblock summary prints it.<para>逻辑标识在超级块摘要中打印的名称。</para></summary>
    /// <param name="feature">Identifier to name.<para>待命名的标识。</para></param>
    /// <returns>The name the superblock summary prints. <para>超级块摘要打印的名称。</para></returns>
    internal static string NameOf(ErofsFeature feature)
        => Row(feature).Name;

    /// <summary>Kernel version text for one feature, formatted as a major.minor release.<para>单个特性的内核版本，格式为主版本.次版本。</para></summary>
    /// <param name="feature">Identifier to describe.<para>待描述的身份。</para></param>
    /// <returns>A release such as <c>5.13</c>, or <c>any</c> for a compat feature with no kernel
    /// floor.<para>形如 <c>5.13</c> 的发布号；无内核下限的 compat 特性返回 <c>any</c>。</para></returns>
    internal static string DescribeKernel(ErofsFeature feature)
    {
        var kernel = Row(feature).Kernel;
        return kernel == 0 ? "any" : $"{kernel / 100}.{kernel % 100}";
    }

    /// <summary>Oldest mounting kernel across declared words, as a major component.<para>声明字中最老可挂载内核的主版本。</para></summary>
    /// <param name="compatBits">Declared compat word.<para>已声明的 compat 字。</para></param>
    /// <param name="incompatBits">Declared incompat word.<para>已声明的 incompat 字。</para></param>
    /// <returns>The major version, or zero when no incompat floor applies.
    /// <para>主版本号；无 incompat 下限时为零。</para></returns>
    internal static int MinimumKernelMajor(uint compatBits, uint incompatBits)
        => MinimumKernel(incompatBits, 0, false) / 100;

    /// <summary>Oldest mounting kernel across declared words, as a minor component.<para>声明字中最老可挂载内核的次版本。</para></summary>
    /// <param name="compatBits">Declared compat word.<para>已声明的 compat 字。</para></param>
    /// <param name="incompatBits">Declared incompat word.<para>已声明的 incompat 字。</para></param>
    /// <returns>The minor version, or zero when no incompat floor applies.
    /// <para>次版本号；无 incompat 下限时为零。</para></returns>
    internal static int MinimumKernelMinor(uint compatBits, uint incompatBits)
        => MinimumKernel(incompatBits, 0, false) % 100;

    /// <summary>Oldest mounting kernel of a decoded superblock, as a major component.<para>解码超级块的最老可挂载内核主版本。</para></summary>
    /// <param name="compatBits">Decoded compat word. Compat rows carry no kernel floor, so the word cannot
    /// raise the estimate.<para>解码得到的 compat 字。compat 行没有内核下限，因此该字不会抬高估计值。</para></param>
    /// <param name="incompatBits">Decoded incompat word.<para>解码得到的 incompat 字。</para></param>
    /// <param name="availableComprAlgs">The <c>s_available_compr_algs</c> bitmap. Each named algorithm raises
    /// the floor: MicroLZMA to 5.16, DEFLATE to 6.6 and Zstandard to 6.10.<para>
    /// <c>s_available_compr_algs</c> 位图。每个指名的算法各自抬高下限：MicroLZMA 至 5.16、DEFLATE 至 6.6、Zstandard
    /// 至 6.10。</para></param>
    /// <param name="hasCompressionConfigs">Whether compression configuration records follow the superblock. The
    /// same two bytes hold the legacy LZ4 maximum distance while no records exist, so the bitmap raises no floor
    /// unless it names records.<para>超级块之后是否紧跟压缩配置记录。无记录时同样两字节存放的是 legacy LZ4 最大距离，
    /// 因此该位图在不指向记录时不抬高下限。</para></param>
    /// <returns>The major version, or zero when neither the incompat word nor the algorithm bitmap applies a
    /// floor.<para>主版本号；incompat 字与算法位图都无下限时为零。</para></returns>
    internal static int MinimumKernelMajor(uint compatBits, uint incompatBits, ushort availableComprAlgs,
        bool hasCompressionConfigs)
        => MinimumKernel(incompatBits, availableComprAlgs, hasCompressionConfigs) / 100;

    /// <summary>Oldest mounting kernel of a decoded superblock, as a minor component.<para>解码超级块的最老可挂载内核次版本。</para></summary>
    /// <param name="compatBits">Decoded compat word, which carries no kernel
    /// floor.<para>解码得到的 compat 字，不含内核下限。</para></param>
    /// <param name="incompatBits">Decoded incompat word.<para>解码得到的 incompat 字。</para></param>
    /// <param name="availableComprAlgs">The <c>s_available_compr_algs</c> bitmap read as an algorithm set only
    /// when <paramref name="hasCompressionConfigs"/> holds.<para>仅当 <paramref name="hasCompressionConfigs"/>
    /// 成立时按算法集合读取的 <c>s_available_compr_algs</c> 位图。</para></param>
    /// <param name="hasCompressionConfigs">Whether compression configuration records follow the
    /// superblock.<para>超级块之后是否紧跟压缩配置记录。</para></param>
    /// <returns>The minor version, or zero when neither the incompat word nor the algorithm bitmap applies a
    /// floor.<para>次版本号；incompat 字与算法位图都无下限时为零。</para></returns>
    internal static int MinimumKernelMinor(uint compatBits, uint incompatBits, ushort availableComprAlgs,
        bool hasCompressionConfigs)
        => MinimumKernel(incompatBits, availableComprAlgs, hasCompressionConfigs) % 100;

    /// <summary>Formats declared words as table names, printing the names that alias one shared bit together.
    /// <para>把声明字格式化为特性名，共用位的别名一并输出。</para></summary>
    /// <param name="compatBits">Declared compat word.<para>已声明的 compat 字。</para></param>
    /// <param name="incompatBits">Declared incompat word.<para>已声明的 incompat 字。</para></param>
    /// <returns>Comma-joined names, or <c>"none"</c> when both words are empty.
    /// <para>以逗号连接的特性名；两字皆空时返回 <c>"none"</c>。</para></returns>
    internal static string Describe(uint compatBits, uint incompatBits)
        => JoinWords(DescribeWord(incompatBits, false, null), DescribeWord(compatBits, true, null));

    /// <summary>Formats declared words as table names, resolving the bit shared by the device table and the
    /// second compressed-inode head format through the layout context instead of printing both names.
    /// <para>把声明字格式化为特性名，并用布局上下文解析设备表与第二种压缩 inode 头部共用的位，而不是同时输出两名。</para></summary>
    /// <param name="compatBits">Declared compat word.<para>已声明的 compat 字。</para></param>
    /// <param name="incompatBits">Declared incompat word.<para>已声明的 incompat 字。</para></param>
    /// <param name="extraDeviceCount">Extra-device slot count read from <c>s_extra_devices</c>, which names the
    /// shared 0x00000008 bit.<para>取自 <c>s_extra_devices</c> 的额外设备槽位数，用于命名共用的 0x00000008 位。</para></param>
    /// <returns>Comma-joined names, or <c>"none"</c> when both words are empty.
    /// <para>以逗号连接的特性名；两字皆空时返回 <c>"none"</c>。</para></returns>
    internal static string Describe(uint compatBits, uint incompatBits, int extraDeviceCount)
        => JoinWords(DescribeWord(incompatBits, false, extraDeviceCount),
            DescribeWord(compatBits, true, null));

    /// <summary>Applies a feature edit script to a baseline declaration, dropping the notes about bits that
    /// carry more than one name.
    /// <para>把特性编辑序列应用到基线声明，并省略关于携带多个名称的位的提示。</para></summary>
    /// <param name="text">Comma-separated tokens; a bare name sets a bit, a leading <c>!</c> clears one,
    /// and <c>auto</c> returns the derived declaration. Matching is case-insensitive against the table
    /// names.
    /// <para>以逗号分隔的记号：裸名置位，前导 <c>!</c> 清位，<c>auto</c> 回到推导式声明；匹配对特性名忽略大小写。</para></param>
    /// <param name="baseCompat">Compat word to start from.<para>起始的 compat 字。</para></param>
    /// <param name="baseIncompat">Incompat word to start from.<para>起始的 incompat 字。</para></param>
    /// <param name="compat">Resulting compat word.<para>结果的 compat 字。</para></param>
    /// <param name="incompat">Resulting incompat word.<para>结果的 incompat 字。</para></param>
    /// <param name="error">Rejected token text, or <see langword="null"/> on success.
    /// <para>被拒绝的记号文本；成功时为 <see langword="null"/>。</para></param>
    /// <returns><see langword="true"/> when every token resolved and the script applied.
    /// <para>所有记号均解析并成功应用时为 <see langword="true"/>。</para></returns>
    internal static bool TryApplyEdits(string? text, uint baseCompat, uint baseIncompat, out uint compat,
        out uint incompat, out string? error)
        => TryApplyEdits(text, baseCompat, baseIncompat, out compat, out incompat, out error, out _);

    /// <summary>Applies a feature edit script to a baseline declaration and reports the aliases each edited bit
    /// carries. Edits address physical bits, so a token naming one alias edits every name sharing that
    /// bit, and <paramref name="aliasNotice"/> lists all of them.
    /// <para>把特性编辑序列应用到基线声明，并报告每个被编辑位携带的别名。编辑针对物理位进行，因此指名某个别名的
    /// 记号会同时改动共用该位的全部名称，<paramref name="aliasNotice"/> 会列出全部名称。</para></summary>
    /// <param name="text">Comma-separated tokens; a bare name sets a bit, a leading <c>!</c> clears one,
    /// and <c>auto</c> returns the derived declaration. Matching is case-insensitive against the table
    /// names.
    /// <para>以逗号分隔的记号：裸名置位，前导 <c>!</c> 清位，<c>auto</c> 回到推导式声明；匹配对特性名忽略大小写。</para></param>
    /// <param name="baseCompat">Compat word to start from.<para>起始的 compat 字。</para></param>
    /// <param name="baseIncompat">Incompat word to start from.<para>起始的 incompat 字。</para></param>
    /// <param name="compat">Resulting compat word.<para>结果的 compat 字。</para></param>
    /// <param name="incompat">Resulting incompat word.<para>结果的 incompat 字。</para></param>
    /// <param name="error">Rejected token text, or <see langword="null"/> on success.
    /// <para>被拒绝的记号文本；成功时为 <see langword="null"/>。</para></param>
    /// <param name="aliasNotice">Semicolon-joined notes naming every bit an edited token shares with other
    /// names in the table, one note per edited bit, or <see langword="null"/> when no edited bit is aliased. An incompat word
    /// alone cannot tell which face of a shared bit the declaration means, so the full alias list is what tells the
    /// operator that clearing <c>dedupe</c> also clears <c>fragments</c> and that clearing <c>compr_head2</c> also
    /// clears <c>device_table</c>.
    /// <para>以分号连接的提示，为被编辑记号共用的每个位列出其全部名称，每个被编辑位一条；当没有编辑到共用位时为
    /// <see langword="null"/>。仅凭 incompat 字无法判断声明指的是共用位的哪一面，因此完整别名列表才能告知操作者：清除
    /// <c>dedupe</c> 会一并清除 <c>fragments</c>，清除 <c>compr_head2</c> 也会一并清除 <c>device_table</c>。</para></param>
    /// <returns><see langword="true"/> when every token resolved and the script applied.
    /// <para>所有记号均解析并成功应用时为 <see langword="true"/>。</para></returns>
    internal static bool TryApplyEdits(string? text, uint baseCompat, uint baseIncompat, out uint compat,
        out uint incompat, out string? error, out string? aliasNotice)
    {
        compat = baseCompat;
        incompat = baseIncompat;
        error = null;
        aliasNotice = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        var notices = new List<string>();
        var noted = new HashSet<(bool IsCompat, uint Bit)>();
        foreach (var raw in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var token = raw;
            var clear = token.StartsWith('!');
            if (clear)
            {
                token = token[1..].Trim();
            }

            if (token.Length == 0)
            {
                error = raw;
                return false;
            }

            if (string.Equals(token, "auto", StringComparison.OrdinalIgnoreCase) && !clear)
            {
                compat = baseCompat;
                incompat = baseIncompat;
                continue;
            }

            var row = Descriptors.FirstOrDefault(candidate => string.Equals(candidate.Name, token,
                StringComparison.OrdinalIgnoreCase));
            if (row is null)
            {
                error = raw;
                return false;
            }

            if (row.IsCompat)
            {
                compat = clear ? compat & ~row.Bit : compat | row.Bit;
            }
            else
            {
                incompat = clear ? incompat & ~row.Bit : incompat | row.Bit;
            }

            var aliases = AliasNames(row.Bit, row.IsCompat);
            if (aliases.Count > 1 && noted.Add((row.IsCompat, row.Bit)))
            {
                var word = row.IsCompat ? "compat" : "incompat";
                var effect = clear ? "cleared" : "declared";
                notices.Add($"EROFS token '{raw}' edits {word} bit 0x{row.Bit:x8}; every name sharing that bit "
                    + $"({string.Join("/", aliases)}) is {effect} with it");
            }
        }

        aliasNotice = notices.Count == 0 ? null : string.Join("; ", notices);
        return true;
    }

    private static Descriptor Row(ErofsFeature feature)
        => Descriptors.First(row => row.Feature == feature);

    private static int MinimumKernel(uint incompatBits, ushort availableComprAlgs, bool hasCompressionConfigs)
    {
        var oldest = 0;
        foreach (var row in Descriptors)
        {
            if (row.IsCompat || (incompatBits & row.Bit) == 0)
            {
                continue;
            }

            if (row.Kernel > oldest)
            {
                oldest = row.Kernel;
            }
        }

        if (!hasCompressionConfigs)
        {
            return oldest;
        }

        return Math.Max(oldest, AlgorithmKernelFloor(availableComprAlgs));
    }

    private static int AlgorithmKernelFloor(ushort availableComprAlgs)
    {
        var oldest = 0;
        if ((availableComprAlgs & (1 << (int)ErofsAlgorithm.Lzma)) != 0)
        {
            oldest = Math.Max(oldest, LzmaKernelFloor);
        }

        if ((availableComprAlgs & (1 << (int)ErofsAlgorithm.Deflate)) != 0)
        {
            oldest = Math.Max(oldest, DeflateKernelFloor);
        }

        if ((availableComprAlgs & (1 << (int)ErofsAlgorithm.Zstd)) != 0)
        {
            oldest = Math.Max(oldest, ZstdKernelFloor);
        }

        return oldest;
    }

    private static string JoinWords(string incompatNames, string compatNames)
    {
        if (incompatNames.Length == 0)
        {
            return compatNames.Length == 0 ? "none" : compatNames;
        }

        return compatNames.Length == 0 ? incompatNames : $"{incompatNames},{compatNames}";
    }

    private static string DescribeWord(uint word, bool isCompat, int? extraDeviceCount)
    {
        var names = new List<string>();
        var named = new HashSet<uint>();
        foreach (var row in Descriptors)
        {
            if (row.IsCompat != isCompat || (word & row.Bit) == 0 || !named.Add(row.Bit))
            {
                continue;
            }

            names.Add(extraDeviceCount is { } count
                ? NameForIncompatBit(row.Bit, count)
                : string.Join("/", AliasNames(row.Bit, isCompat)));
        }

        return string.Join(",", names);
    }
}