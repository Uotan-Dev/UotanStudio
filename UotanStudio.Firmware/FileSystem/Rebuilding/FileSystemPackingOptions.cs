using System.Globalization;
using UotanStudio.Firmware.Core.Repacking;
using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Firmware.FileSystem.Erofs.Models;
using UotanStudio.Firmware.FileSystem.Metadata;

namespace UotanStudio.Firmware.FileSystem.Rebuilding;

/// <summary>Resolves filesystem packing options from the captured metadata context.
/// <para>从已采集的元数据上下文解析文件系统打包选项。</para></summary>
internal static class FileSystemPackingOptions
{
    /// <summary>Builds the option map a filesystem packer consumes for an extraction or staging directory.
    /// Stored packing options come first, application overrides merge above them, and the structured
    /// <c>metadata.json</c> payload contributes its partition keys. The <c>_fs_config</c>,
    /// <c>_file_contexts</c> and <c>_info</c> sidecars written beside an extraction metadata file are
    /// extraction-time exports and are never published as option paths here; an external flat input reaches a
    /// builder only as an explicit <c>FsConfigPath</c> or <c>FileContextsPath</c> override on a directory build.
    /// <para>构造文件系统打包器针对解包目录或暂存目录使用的选项映射。先取记录的打包选项，再让应用层覆盖合并到
    /// 其上，最后由结构化 <c>metadata.json</c> 负载贡献其分区键。解包元数据旁的 <c>_fs_config</c>、
    /// <c>_file_contexts</c> 与 <c>_info</c> 附属文件属于解包期导出物，绝不在此作为选项路径发布；外部扁平输入
    /// 只能以显式的 <c>FsConfigPath</c> 或 <c>FileContextsPath</c> 覆盖形式进入目录构建的构建器。</para></summary>
    /// <param name="directory">Extraction or staging directory whose metadata is consulted.
    /// <para>要参考其元数据的解包目录或暂存目录。</para></param>
    /// <param name="formatName">Format name reported by the handler performing the pack.
    /// <para>执行打包的处理器所报告的格式名。</para></param>
    /// <returns>The option map; empty when no metadata exists or the recorded format belongs to neither this
    /// format nor a filesystem container.
    /// <para>选项映射；无元数据，或记录的格式既非本格式也非文件系统容器时为空。</para></returns>
    internal static IReadOnlyDictionary<string, string> OptionsFor(string directory, string formatName)
    {
        var metadata = RepackMetadata.Read(directory);
        if (metadata is null
            || (metadata.FormatName != formatName && metadata.FormatName is not ("EXT4" or "EROFS" or "F2FS")))
        {
            return new Dictionary<string, string>();
        }
        var options = new Dictionary<string, string>(metadata.PackingOptions, StringComparer.Ordinal);
        if (RepackMetadata.PackingOverrides is { } overrides)
        {
            foreach (var pair in overrides)
            {
                options[pair.Key] = pair.Value;
            }
        }
        var document = FileSystemMetadataOverrides.TryLoadDocument(metadata);
        if (document is { PartitionInfo.Count: > 0 })
        {
            foreach (var (key, value) in document.PartitionInfo)
            {
                if (!options.ContainsKey(key))
                {
                    options[key] = value;
                }
            }
        }

        return options;
    }

    /// <summary>
    /// Reads the structured override tables carried by the <c>metadata.json</c> preservation payload of
    /// <paramref name="directory"/>. A pack that builds from the host tree uses these tables instead of any flat
    /// sidecar beside the metadata file, so attributes recorded at extraction are the only attribute input a pack
    /// consumes.
    /// <para>读取 <paramref name="directory"/> 的 <c>metadata.json</c> 保留负载所承载的结构化覆盖表。基于宿主目录树
    /// 构建的打包流程使用这些表，而不是元数据文件旁的任何扁平附属文件，因此解包时记录的属性是打包唯一消费的属性输入。</para>
    /// </summary>
    /// <param name="directory">Extraction or staging directory whose metadata is consulted.
    /// <para>要参考其元数据的解包目录或暂存目录。</para></param>
    /// <returns>The payload tables, or <see langword="null"/> when the directory carries no structured payload.
    /// <para>负载中的各表；目录不携带结构化负载时返回 <see langword="null"/>。</para></returns>
    internal static PreservedTables? TryReadPreservedTables(string directory)
    {
        _ = FileSystemMetadataOverrides.TryLoadDocument(RepackMetadata.Read(directory));
        return null;
    }

    /// <summary>
    /// The POSIX attribute and SELinux label tables taken from a structured preservation payload.
    /// <para>取自结构化保留负载的 POSIX 属性表与 SELinux 标签表。</para>
    /// </summary>
    /// <param name="FsConfig">Ownership and permission table keyed by image path.
    /// <para>以镜像路径为键的属主与权限表。</para></param>
    /// <param name="FileContexts">SELinux label table keyed by image path.
    /// <para>以镜像路径为键的 SELinux 标签表。</para></param>
    internal readonly record struct PreservedTables(
        IReadOnlyDictionary<string, FsConfigEntry> FsConfig,
        IReadOnlyDictionary<string, string> FileContexts);

    internal static int IntegerOption(IReadOnlyDictionary<string, string> options, string key, int fallback)
        => options.TryGetValue(key, out var value)
            ? int.Parse(value, CultureInfo.InvariantCulture) : fallback;

    /// <summary>Gets an option as a nullable path: empty values (sidecar file absent) become null.
    /// <para>以可空路径形式获取选项：空值（附属文件不存在）转换为 <c>null</c>。</para></summary>
    internal static string? NullIfEmpty(IReadOnlyDictionary<string, string> options, string key)
        => options.TryGetValue(key, out var value) && value.Length > 0 ? value : null;

    /// <summary>
    /// Applies the <c>ErofsFeatures</c>, <c>ErofsDotOmitted</c>, <c>ErofsFragments</c>,
    /// <c>ErofsMetabox</c>, <c>ErofsExcludePath</c>, <c>ErofsExcludeRegex</c>,
    /// <c>ErofsUseExtentIndexes</c>, <c>ErofsChunkSize</c>, <c>ErofsBlobDeviceIds</c>,
    /// <c>ErofsInodeFormat</c>, <c>ErofsChunkIndexKind</c>, <c>ErofsCompressHints</c> and
    /// <c>ErofsDedupeMode</c> packing keys to builder
    /// options. The feature edit script starts from
    /// <paramref name="inheritedWords"/> when the caller carries an inherited declaration and
    /// from the writer's own implied words otherwise; a key that is absent leaves the declaration untouched, so a
    /// build without these keys stays byte-identical to one that never had them. A token outside the upstream
    /// feature table or outside the layout-mode vocabulary fails the build rather than dropping the edit silently.
    /// The exclusion, hint and device-index keys hold one entry per line rather than a comma-separated list,
    /// because a regular expression or a path may contain a comma.
    /// <para>把 <c>ErofsFeatures</c>、<c>ErofsDotOmitted</c>、<c>ErofsFragments</c>、<c>ErofsMetabox</c>、
    /// <c>ErofsExcludePath</c>、<c>ErofsExcludeRegex</c>、<c>ErofsUseExtentIndexes</c>、<c>ErofsChunkSize</c>、
    /// <c>ErofsBlobDeviceIds</c>、<c>ErofsInodeFormat</c>、<c>ErofsChunkIndexKind</c>、<c>ErofsCompressHints</c>
    /// 与 <c>ErofsDedupeMode</c> 打包键应用到构建选项。调用方携带继承声明时以
    /// <paramref name="inheritedWords"/> 作为编辑基线，否则以写入方自行推导的隐含字作为基线；缺失的键不会改动
    /// 声明，因此未带这些键的构建与从未有过这些键时逐字节一致。超出上游特性表或布局模式词表的记号会使构建
    /// 失败，而不是静默丢弃该次编辑。排除键、提示键与设备下标键每行一个条目而非逗号分隔，因为正则表达式或
    /// 路径可能包含逗号。</para>
    /// </summary>
    /// <param name="options">Merged packing option map. <para>已合并的打包选项映射。</para></param>
    /// <param name="erofsOptions">Builder options receiving the declaration.
    /// <para>接收该声明的构建选项。</para></param>
    /// <param name="inheritedWords">Inherited superblock feature words, or <see langword="null"/> to edit from the
    /// implied baseline. <para>继承的超级块特性字；为 <see langword="null"/> 时以隐含基线为编辑起点。</para></param>
    /// <exception cref="FileSystemUnsupportedFeatureException">The edit script names a token the upstream feature
    /// table does not carry, or the dot-omitted value is neither a tri-state name nor a boolean.
    /// <para>编辑脚本包含上游特性表未收录的记号，或 dot-omitted 值既不是三态名也不是布尔值。</para></exception>
    internal static void ApplyErofsFeatureKeys(IReadOnlyDictionary<string, string> options, ErofsOptions erofsOptions,
        (uint Compat, uint Incompat)? inheritedWords)
    {
        if (options.TryGetValue("ErofsFeatures", out var edits))
        {
            var baseline = inheritedWords ?? ImpliedFeatureWords(erofsOptions);
            if (!ErofsFeatureTable.TryApplyEdits(edits, baseline.Compat, baseline.Incompat, out var compat,
                    out var incompat, out var error))
            {
                throw new FileSystemUnsupportedFeatureException(
                    $"EROFS feature declaration rejected the unknown token '{error}'.");
            }

            erofsOptions.Features = ErofsFeatureSet.FromWords(compat, incompat);
        }

        if (options.TryGetValue("ErofsDotOmitted", out var dotOmitted))
        {
            erofsOptions.DotOmitted = ParseErofsTriState(dotOmitted, "ErofsDotOmitted");
        }

        if (options.TryGetValue("ErofsFragments", out var fragments))
        {
            var fragmentMode = NormalizeLayoutToken(fragments);
            (erofsOptions.Fragments, erofsOptions.AllFilesAsFragments) = fragmentMode switch
            {
                "off" => (false, false),
                "tails" => (true, false),
                "all-small" => (true, true),
                "fragdedupe" => (true, true),
                _ => throw new FileSystemUnsupportedFeatureException(
                    $"EROFS fragment mode '{fragments}' is none of 'off', 'tails', 'all-small' or 'fragdedupe'."),
            };
            if (fragmentMode == "fragdedupe")
            {
                erofsOptions.Dedup = true;
            }
        }

        if (options.TryGetValue("ErofsMetabox", out var metabox))
        {
            var metaboxMode = NormalizeLayoutToken(metabox);
            if (metaboxMode is not ("off" or "auto" or "forced"))
            {
                throw new FileSystemUnsupportedFeatureException(
                    $"EROFS metabox mode '{metabox}' is none of 'off', 'auto' or 'forced'.");
            }

            erofsOptions.Metabox = metaboxMode is "auto" or "forced";
            erofsOptions.MetaboxRequired = metaboxMode == "forced";
        }

        if (options.TryGetValue("ErofsExcludePath", out var excludePaths))
        {
            erofsOptions.ExcludePaths = SplitExclusionEntries(excludePaths);
        }

        if (options.TryGetValue("ErofsExcludeRegex", out var excludeRegexes))
        {
            erofsOptions.ExcludeRegexes = SplitExclusionEntries(excludeRegexes);
        }

        if (options.TryGetValue("ErofsUseExtentIndexes", out var useExtentIndexes))
        {
            erofsOptions.UseExtentIndexes = ParseErofsTriState(useExtentIndexes, "ErofsUseExtentIndexes");
        }

        if (options.TryGetValue("ErofsChunkSize", out var chunkSize))
        {
            if (!int.TryParse(chunkSize, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedChunkSize))
            {
                throw new FileSystemUnsupportedFeatureException(
                    $"Unsupported EROFS ErofsChunkSize value '{chunkSize}'; expected an integer byte count.");
            }
            erofsOptions.ChunkSize = parsedChunkSize;
        }

        if (options.TryGetValue("ErofsBlobDeviceIds", out var blobDeviceIds))
        {
            erofsOptions.BlobDeviceIds = ParseErofsDeviceIds(blobDeviceIds, "ErofsBlobDeviceIds");
        }

        if (options.TryGetValue("ErofsInodeFormat", out var inodeFormat))
        {
            erofsOptions.InodeFormat = ParseErofsEnum<ErofsInodeFormat>(inodeFormat, "ErofsInodeFormat");
        }

        if (options.TryGetValue("ErofsChunkIndexKind", out var chunkIndexKind))
        {
            erofsOptions.ChunkIndexKind = ParseErofsEnum<ErofsChunkIndexKind>(chunkIndexKind, "ErofsChunkIndexKind");
        }

        if (options.TryGetValue("ErofsCompressHints", out var compressHints))
        {
            erofsOptions.CompressHints = SplitExclusionEntries(compressHints);
        }

        if (options.TryGetValue("ErofsDedupeMode", out var dedupeMode))
        {
            erofsOptions.DedupeMode = ParseErofsEnum<ErofsDedupeMode>(dedupeMode, "ErofsDedupeMode");
        }
    }

    /// <summary>
    /// Splits an exclusion packing key into entries, one per line. A comma separator cannot be used here the
    /// way <c>ErofsFeatures</c> uses one, because a regular expression may legitimately contain a comma and
    /// even a <c>/</c>-separated path, so a comma-separated list would silently cut a pattern in half.
    /// <para>按行拆分排除类打包键，每行一个条目。此处不能照搬 <c>ErofsFeatures</c> 的逗号分隔：正则表达式
    /// 合法地可能包含逗号、甚至包含以 <c>/</c> 分隔的路径，逗号分隔会把模式静默截断。</para>
    /// </summary>
    /// <param name="value">Raw key text; entries are separated by LF or CRLF and surrounding whitespace of each
    /// entry is trimmed. <para>原始键文本；条目以 LF 或 CRLF 分隔，每条目首尾空白被裁剪。</para></param>
    /// <returns>The non-empty entries, or <see langword="null"/> when the key held no entry so the builder keeps
    /// its own default. <para>非空条目；键内没有任何条目时返回 <see langword="null"/>，使构建器保持自身默认值。</para></returns>
    private static IReadOnlyList<string>? SplitExclusionEntries(string value)
    {
        var entries = value
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
        return entries.Length == 0 ? null : entries;
    }

    /// <summary>
    /// Trims and lower-cases a layout-mode packing token so these keys accept the spelling an
    /// <c>erofs-utils</c> command line would carry.
    /// <para>裁剪并小写化布局模式打包记号，使这些键接受与 <c>erofs-utils</c> 命令行一致的写法。</para>
    /// </summary>
    /// <param name="value">Raw token text. <para>原始记号文本。</para></param>
    /// <returns>The normalized token. <para>规范化后的记号。</para></returns>
    private static string NormalizeLayoutToken(string value)
        => value.Trim().ToLowerInvariant();

    /// <summary>
    /// Derives the declaration baseline the writer computes for these options when no words are inherited, so a
    /// packing key edits the declaration the image would otherwise carry. Both words come from the single
    /// derivation the builder itself uses; layout-implied bits stay out of the baseline because the builder
    /// reconciles them against the planned layout and reports them instead.
    /// <para>推导未继承特性字时写入方对本组选项算出的声明基线，使打包键是在镜像原本将携带的声明上做编辑。
    /// 两个字都取自打包器自身使用的同一份推导；布局隐含位不进入基线：打包器会按实际规划的布局与声明对账
    /// 并另行报告结果。</para>
    /// </summary>
    /// <param name="options">Builder options whose implied declaration is requested.
    /// <para>需要其隐含声明的构建选项。</para></param>
    /// <returns>The implied compat and incompat words. <para>推导出的 compat 与 incompat 字。</para></returns>
    private static (uint Compat, uint Incompat) ImpliedFeatureWords(ErofsOptions options)
        => (options.DerivedCompatBits(), options.DerivedIncompatBaseline());

    /// <summary>
    /// Reads a dot-omitted declaration: an empty or <c>auto</c> value keeps the derived behaviour, a tri-state name
    /// is taken verbatim and a boolean-style value forces the feature off or on.
    /// <para>读取 dot-omitted 声明：空值或 <c>auto</c> 保持推导行为，三态名称按字面采纳，布尔风格取值则强制
    /// 关闭或开启该特性。</para>
    /// </summary>
    /// <param name="value">Raw option text. <para>原始选项文本。</para></param>
    /// <param name="key">Packing key the value came from, used in the failure message.
    /// <para>该值来自的打包键，用于失败信息。</para></param>
    /// <returns>The tri-state selection. <para>三态选择结果。</para></returns>
    private static ErofsTriStateFeature ParseErofsTriState(string? value, string key)
    {
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return ErofsTriStateFeature.Auto;
        }

        if (Enum.TryParse<ErofsTriStateFeature>(value, ignoreCase: true, out var state) && Enum.IsDefined(state))
        {
            return state;
        }

        if (IsTrueOption(value))
        {
            return ErofsTriStateFeature.Enabled;
        }

        if (string.Equals(value, "0", StringComparison.Ordinal)
            || string.Equals(value, "false", StringComparison.OrdinalIgnoreCase))
        {
            return ErofsTriStateFeature.Disabled;
        }

        throw new FileSystemUnsupportedFeatureException(
            $"Unsupported EROFS {key} declaration '{value}'.");
    }

    /// <summary>
    /// Parses an enum-valued packing key, accepting the declared member names case-insensitively and
    /// rejecting anything else instead of falling back to the default.
    /// <para>解析枚举型打包键：不区分大小写地接受已声明的成员名，其他取值直接报错而不是回落到默认值。</para>
    /// </summary>
    /// <param name="value">Raw option text. <para>原始选项文本。</para></param>
    /// <param name="key">Packing key the value came from. <para>该值来自的打包键。</para></param>
    /// <returns>The parsed member. <para>解析得到的成员。</para></returns>
    /// <exception cref="FileSystemUnsupportedFeatureException">The text names no declared member.
    /// <para>文本没有指名任何已声明的成员。</para></exception>
    private static TEnum ParseErofsEnum<TEnum>(string value, string key) where TEnum : struct, Enum
    {
        if (Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed))
        {
            return parsed;
        }

        throw new FileSystemUnsupportedFeatureException(
            $"Unsupported EROFS {key} value '{value}'; expected one of " +
            $"{string.Join(", ", Enum.GetNames<TEnum>())}.");
    }

    /// <summary>
    /// Parses a line-separated packing key into the non-negative device indices it names.
    /// <para>把按行分隔的打包键解析为它指名的非负设备下标。</para>
    /// </summary>
    /// <param name="value">Raw option text, one index per line. <para>原始选项文本，每行一个下标。</para></param>
    /// <param name="key">Packing key the value came from. <para>该值来自的打包键。</para></param>
    /// <returns>The indices, or <see langword="null"/> when the key held no entry.
    /// <para>下标列表；键内没有任何条目时返回 <see langword="null"/>。</para></returns>
    /// <exception cref="FileSystemUnsupportedFeatureException">An entry is not a non-negative integer.
    /// <para>某个条目不是非负整数。</para></exception>
    private static IReadOnlyList<int>? ParseErofsDeviceIds(string value, string key)
    {
        var entries = SplitExclusionEntries(value);
        if (entries is null)
        {
            return null;
        }

        var ids = new int[entries.Count];
        for (var i = 0; i < entries.Count; i++)
        {
            if (!int.TryParse(entries[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || id < 0)
            {
                throw new FileSystemUnsupportedFeatureException(
                    $"Unsupported EROFS {key} entry '{entries[i]}'; expected a non-negative device index.");
            }
            ids[i] = id;
        }

        return ids;
    }

    /// <summary>
    /// Matches the boolean option spellings the rebuilding pipeline already accepts for flags.
    /// <para>与重建流水线已接受的布尔选项写法保持一致。</para>
    /// </summary>
    /// <param name="value">Raw option text. <para>原始选项文本。</para></param>
    /// <returns><see langword="true"/> for <c>1</c> or a value <see cref="bool"/> parses as
    /// <see langword="true"/>. <para>取值为 <c>1</c>，或 <see cref="bool"/> 能解析为 <see langword="true"/>
    /// 时返回 <see langword="true"/>。</para></returns>
    private static bool IsTrueOption(string? value)
        => value is "1" || bool.TryParse(value, out var enabled) && enabled;
}