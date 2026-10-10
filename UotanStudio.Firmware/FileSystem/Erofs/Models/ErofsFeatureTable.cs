namespace UotanStudio.Firmware.FileSystem.Erofs.Models;

/// <summary>
/// Single source of truth mapping each <see cref="ErofsFeature"/> to its on-disk word, bit, upstream
/// name, oldest mounting kernel and writer capability, mirroring the erofs-utils feature list. Capability
/// belongs to the physical bit, so every name aliasing one bit shares it.
/// <para>把每个 <see cref="ErofsFeature"/> 映射到磁盘字、位、上游名称、最老可挂载内核与写入方能力的唯一真值表，
/// 与 erofs-utils 的特性名单一致。能力归属于物理位，因此共用一位的每个名称也共用该能力。</para>
/// </summary>
internal static class ErofsFeatureTable
{
    /// <summary>One row of the upstream feature list.<para>上游特性表中的一行。</para></summary>
    /// <param name="Feature">Logical identifier.<para>逻辑标识。</para></param>
    /// <param name="IsCompat">Whether the word is <c>s_feature_compat</c>.<para>该位属于 <c>s_feature_compat</c> 时为真。</para></param>
    /// <param name="Bit">Physical bit value.<para>物理位值。</para></param>
    /// <param name="Name">Upstream feature name as erofs-utils prints it.<para>erofs-utils 输出的上游特性名。</para></param>
    /// <param name="Kernel">Oldest mounting kernel, encoded as major * 100 + minor.<para>最老可挂载内核，编码为 major * 100 + minor。</para></param>
    /// <param name="Capability">Whether the managed writer can emit the structures behind the bit.<para>受管写入方能否输出该位背后的结构。</para></param>
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
            ErofsFeatureCapability.ReadOnlyForEmit),
        new(ErofsFeature.DeviceTable, false, ErofsSuperblock.IncompatDeviceTable, "device_table", 516,
            ErofsFeatureCapability.Emittable),
        new(ErofsFeature.CompressionHead2, false, ErofsSuperblock.IncompatCompressHead2, "compr_head2", 516,
            ErofsFeatureCapability.Emittable),
        new(ErofsFeature.Ztailpacking, false, ErofsSuperblock.IncompatZtailpacking, "ztailpacking", 517,
            ErofsFeatureCapability.Emittable),
        new(ErofsFeature.Fragments, false, ErofsSuperblock.IncompatFragments, "fragments", 601,
            ErofsFeatureCapability.Emittable),
        new(ErofsFeature.Dedupe, false, ErofsSuperblock.IncompatDedupe, "dedupe", 601,
            ErofsFeatureCapability.Emittable),
        new(ErofsFeature.XattrPrefixes, false, ErofsSuperblock.IncompatXattrPrefixes, "xattr_prefixes", 604,
            ErofsFeatureCapability.ReadOnlyForEmit),
        new(ErofsFeature.FortyEightBit, false, ErofsSuperblock.Incompat48Bit, "48bit", 615,
            ErofsFeatureCapability.ReadOnlyForEmit),
        new(ErofsFeature.Metabox, false, ErofsSuperblock.IncompatMetabox, "metabox", 617,
            ErofsFeatureCapability.Emittable),
        new(ErofsFeature.SuperblockChecksum, true, ErofsSuperblock.CompatSbChecksum, "sb_csum", 0,
            ErofsFeatureCapability.Emittable),
        new(ErofsFeature.Mtime, true, ErofsSuperblock.CompatMtime, "mtime", 0,
            ErofsFeatureCapability.Emittable),
        new(ErofsFeature.XattrFilter, true, ErofsSuperblock.CompatXattrFilter, "xattr_filter", 0,
            ErofsFeatureCapability.Emittable),
        new(ErofsFeature.SharedEaInMetabox, true, ErofsSuperblock.CompatSharedEaInMetabox, "shared_ea_in_metabox", 0,
            ErofsFeatureCapability.ReadOnlyForEmit),
        new(ErofsFeature.PlainXattrPrefixes, true, ErofsSuperblock.CompatPlainXattrPfx, "plain_xattr_pfx", 0,
            ErofsFeatureCapability.ReadOnlyForEmit),
        new(ErofsFeature.SharedInodeXattrs, true, ErofsSuperblock.CompatIshareXattrs, "ishare_xattrs", 0,
            ErofsFeatureCapability.ReadOnlyForEmit),
    ];

    /// <summary>Every row, in upstream bit order.<para>按上游位顺序排列的全部行。</para></summary>
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

    /// <summary>Reports whether the writer can emit the structures behind a bit.<para>判断写入方能否输出该位背后的结构。</para></summary>
    /// <param name="feature">Identifier to test.<para>待测标识。</para></param>
    /// <returns>The capability shared by every name aliasing the feature's physical
    /// bit.<para>共用该物理位的每个名称共享的能力。</para></returns>
    internal static ErofsFeatureCapability CapabilityOf(ErofsFeature feature)
        => Row(feature).Capability;

    /// <summary>Upstream name of a logical identifier.<para>逻辑标识的上游名称。</para></summary>
    /// <param name="feature">Identifier to name.<para>待命名的标识。</para></param>
    /// <returns>The name as erofs-utils prints it. <para>erofs-utils 输出的名称。</para></returns>
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
        => MinimumKernel(compatBits, incompatBits) / 100;

    /// <summary>Oldest mounting kernel across declared words, as a minor component.<para>声明字中最老可挂载内核的次版本。</para></summary>
    /// <param name="compatBits">Declared compat word.<para>已声明的 compat 字。</para></param>
    /// <param name="incompatBits">Declared incompat word.<para>已声明的 incompat 字。</para></param>
    /// <returns>The minor version, or zero when no incompat floor applies.
    /// <para>次版本号；无 incompat 下限时为零。</para></returns>
    internal static int MinimumKernelMinor(uint compatBits, uint incompatBits)
        => MinimumKernel(compatBits, incompatBits) % 100;

    /// <summary>Formats declared words as upstream names, aliasing names for shared bits printed together.
    /// <para>把声明字格式化为上游名称，共用位的别名名一并输出。</para></summary>
    /// <param name="compatBits">Declared compat word.<para>已声明的 compat 字。</para></param>
    /// <param name="incompatBits">Declared incompat word.<para>已声明的 incompat 字。</para></param>
    /// <returns>Comma-joined names, or <c>"none"</c> when both words are empty.
    /// <para>以逗号连接的上游名；两字皆空时返回 <c>"none"</c>。</para></returns>
    internal static string Describe(uint compatBits, uint incompatBits)
    {
        var names = new List<string>();
        if (incompatBits != 0)
        {
            names.Add(Label(incompatBits, ErofsSuperblock.IncompatLz4_0Padding, "lz4_0padding"));
            names.Add(Label(incompatBits, ErofsSuperblock.IncompatBigPcluster, "compr_cfgs/big_pcluster"));
            names.Add(Label(incompatBits, ErofsSuperblock.IncompatChunkedFile, "chunked_file"));
            names.Add(Label(incompatBits, ErofsSuperblock.IncompatDeviceTable, "device_table/compr_head2"));
            names.Add(Label(incompatBits, ErofsSuperblock.IncompatZtailpacking, "ztailpacking"));
            names.Add(Label(incompatBits, ErofsSuperblock.IncompatFragments, "fragments/dedupe"));
            names.Add(Label(incompatBits, ErofsSuperblock.IncompatXattrPrefixes, "xattr_prefixes"));
            names.Add(Label(incompatBits, ErofsSuperblock.Incompat48Bit, "48bit"));
            names.Add(Label(incompatBits, ErofsSuperblock.IncompatMetabox, "metabox"));
        }
        if (compatBits != 0)
        {
            names.Add(Label(compatBits, ErofsSuperblock.CompatSbChecksum, "sb_csum"));
            names.Add(Label(compatBits, ErofsSuperblock.CompatMtime, "mtime"));
            names.Add(Label(compatBits, ErofsSuperblock.CompatXattrFilter, "xattr_filter"));
            names.Add(Label(compatBits, ErofsSuperblock.CompatSharedEaInMetabox, "shared_ea_in_metabox"));
            names.Add(Label(compatBits, ErofsSuperblock.CompatPlainXattrPfx, "plain_xattr_pfx"));
            names.Add(Label(compatBits, ErofsSuperblock.CompatIshareXattrs, "ishare_xattrs"));
        }
        var present = names.Where(name => name.Length > 0).ToArray();
        return present.Length == 0 ? "none" : string.Join(",", present);
    }

    /// <summary>Names the incompat bits a writer cannot re-emit.<para>列出写入方无法重新输出的 incompat 位名称。</para></summary>
    /// <param name="incompatBits">Incompat word to inspect.<para>待检查的 incompat 字。</para></param>
    /// <returns>Comma-joined upstream names, or an empty string when every declared bit is
    /// emittable.<para>以逗号连接的上游名；所有声明位均可写出时为空串。</para></returns>
    internal static string ReadOnlyIncompatNames(uint incompatBits)
        => string.Join(",", Descriptors
            .Where(row => !row.IsCompat && row.Capability == ErofsFeatureCapability.ReadOnlyForEmit
                && (incompatBits & row.Bit) != 0)
            .Select(row => row.Name));

    /// <summary>Applies an upstream-style feature edit script to a baseline declaration.<para>把上游风格的特性编辑序列应用到基线声明。</para></summary>
    /// <param name="text">Comma-separated tokens; a bare name sets a bit, a leading <c>!</c> clears one,
    /// and <c>auto</c> returns the derived declaration. Matching is case-insensitive against the upstream
    /// names.
    /// <para>以逗号分隔的记号：裸名置位，前导 <c>!</c> 清位，<c>auto</c> 回到推导式声明；匹配对上游名忽略大小写。</para></param>
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
    {
        compat = baseCompat;
        incompat = baseIncompat;
        error = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        foreach (var raw in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var token = raw;
            var clear = token.StartsWith('!');
            if (clear) token = token[1..].Trim();
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
        }

        return true;
    }

    private static Descriptor Row(ErofsFeature feature)
        => Descriptors.First(row => row.Feature == feature);

    private static int MinimumKernel(uint compatBits, uint incompatBits)
    {
        var oldest = 0;
        foreach (var row in Descriptors)
        {
            if (row.IsCompat || (incompatBits & row.Bit) == 0) continue;
            if (row.Kernel > oldest) oldest = row.Kernel;
        }

        return oldest;
    }

    private static string Label(uint word, uint bit, string name)
        => (word & bit) == 0 ? string.Empty : name;
}