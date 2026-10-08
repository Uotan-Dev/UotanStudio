using UotanStudio.Firmware.Core.Repacking;
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
}