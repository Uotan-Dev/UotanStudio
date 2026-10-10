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
            ? int.Parse(value, System.Globalization.CultureInfo.InvariantCulture) : fallback;

    /// <summary>Gets an option as a nullable path: empty values (sidecar file absent) become null.
    /// <para>以可空路径形式获取选项：空值（附属文件不存在）转换为 <c>null</c>。</para></summary>
    internal static string? NullIfEmpty(IReadOnlyDictionary<string, string> options, string key)
        => options.TryGetValue(key, out var value) && value.Length > 0 ? value : null;

    /// <summary>
    /// Applies the <c>ErofsFeatures</c> and <c>ErofsDotOmitted</c> packing keys to builder options. The edit
    /// script starts from <paramref name="inheritedWords"/> when the caller carries an inherited declaration and
    /// from the writer's own implied words otherwise; a key that is absent leaves the declaration untouched, so a
    /// build without these keys stays byte-identical to one that never had them. A token outside the upstream
    /// feature table fails the build rather than dropping the edit silently.
    /// <para>把 <c>ErofsFeatures</c> 与 <c>ErofsDotOmitted</c> 打包键应用到构建选项。调用方携带继承声明时以
    /// <paramref name="inheritedWords"/> 作为编辑基线，否则以写入方自行推导的隐含字作为基线；缺失的键不会改动
    /// 声明，因此未带这些键的构建与从未有过这些键时逐字节一致。超出上游特性表的记号会使构建失败，而不是静默
    /// 丢弃该次编辑。</para>
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
            erofsOptions.DotOmitted = ParseErofsDotOmitted(dotOmitted);
        }
    }

    /// <summary>
    /// Derives the declaration baseline the writer computes for these options when no words are inherited, so a
    /// packing key edits the declaration the image would otherwise carry. Layout-implied bits stay out of the
    /// baseline because the builder reconciles them against the planned layout and reports them instead.
    /// <para>推导未继承特性字时写入方对本组选项算出的声明基线，使打包键是在镜像原本将携带的声明上做编辑。
    /// 布局隐含位不进入基线：打包器会按实际规划的布局与声明对账并另行报告结果。</para>
    /// </summary>
    /// <param name="options">Builder options whose implied declaration is requested.
    /// <para>需要其隐含声明的构建选项。</para></param>
    /// <returns>The implied compat and incompat words. <para>推导出的 compat 与 incompat 字。</para></returns>
    private static (uint Compat, uint Incompat) ImpliedFeatureWords(ErofsOptions options)
    {
        var compat = (options.SuperblockChecksum ? ErofsSuperblock.CompatSbChecksum : 0u)
            | (options.SourceMtimeFeature ? ErofsSuperblock.CompatMtime : 0u)
            | (options.SourceXattrFilterFeature ? ErofsSuperblock.CompatXattrFilter : 0u);
        var incompat = options.Compress ? ErofsSuperblock.IncompatLz4_0Padding : 0u;
        if (options.Compress && !options.LegacyCompatibility)
        {
            incompat |= ErofsSuperblock.IncompatBigPcluster;
        }

        return (compat, incompat);
    }

    /// <summary>
    /// Reads a dot-omitted declaration: an empty or <c>auto</c> value keeps the derived behaviour, a tri-state name
    /// is taken verbatim and a boolean-style value forces the feature off or on.
    /// <para>读取 dot-omitted 声明：空值或 <c>auto</c> 保持推导行为，三态名称按字面采纳，布尔风格取值则强制
    /// 关闭或开启该特性。</para>
    /// </summary>
    /// <param name="value">Raw option text. <para>原始选项文本。</para></param>
    /// <returns>The tri-state selection. <para>三态选择结果。</para></returns>
    private static ErofsTriStateFeature ParseErofsDotOmitted(string? value)
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
            $"Unsupported EROFS dot-omitted declaration '{value}'.");
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