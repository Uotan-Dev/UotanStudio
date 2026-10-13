using System.Globalization;
using UotanStudio.Firmware.Core.Repacking;
using UotanStudio.Firmware.FileSystem.Rebuilding;

namespace UotanStudio.Firmware.FileSystem.Metadata;

/// <summary>
/// One parsed <c>fs_config</c> entry: POSIX identity and permission bits for an image path.
/// <para>解析后的 <c>fs_config</c> 条目：镜像路径的 POSIX 身份与权限位。</para>
/// </summary>
/// <param name="Uid">Owner user identifier parsed as an invariant decimal.
/// <para>以不变文化十进制解析的所有者用户 ID。</para></param>
/// <param name="Gid">Owner group identifier parsed as an invariant decimal.
/// <para>以不变文化十进制解析的所有者组 ID。</para></param>
/// <param name="Mode">Permission bits read as octal digits, with or without a leading <c>0</c>; a token
/// holding <c>8</c>, <c>9</c> or any non-digit is not a valid mode.
/// <para>权限位按八进制数字读取，可带前导 <c>0</c>；含 <c>8</c>、<c>9</c> 或非数字的记号不是合法模式。</para></param>
public readonly record struct FsConfigEntry(uint Uid, uint Gid, ushort Mode);

/// <summary>
/// Parses Android-style <c>fs_config</c> and <c>file_contexts</c> files into path-keyed lookups
/// consumed by the filesystem builders to restore ownership, permissions and SELinux labels.
/// <para>将 Android 风格的 <c>fs_config</c> 与 <c>file_contexts</c> 解析为按路径索引的查找表，
/// 供文件系统构建器还原属主、权限与 SELinux 标签。</para>
/// </summary>
internal static class FileSystemMetadataOverrides
{
    private const string SelinuxXattrName = "security.selinux";

    /// <summary>
    /// Loads an <c>fs_config</c> file into a lookup keyed by the absolute image path
    /// (leading "/" stripped, invariant-case on Windows platforms is preserved as-is).
    /// Returns an empty dictionary when <paramref name="path"/> is null, empty or unreadable.
    /// <para>加载 <c>fs_config</c> 文件为以镜像路径为键的查找表（去除前导 "/"）。
    /// <paramref name="path"/> 为 null、空或不可读时返回空字典。</para>
    /// </summary>
    /// <param name="path">File path; <see langword="null"/>, whitespace, a missing file or a
    /// reparse point yields an empty table. Lines need at least four space-separated tokens, and lines whose
    /// uid or gid is not decimal or whose mode is not an octal token are skipped; a repeated path overwrites
    /// the earlier entry.
    /// <para>文件路径；为 <see langword="null"/>、仅空白、文件不存在或为重解析点时返回空表。
    /// 每行至少需要四个以空格分隔的记号；uid 或 gid 非十进制、模式非八进制记号的行会被跳过；
    /// 重复路径覆盖先前条目。</para></param>
    /// <returns>Ordinal path-keyed entries with leading and trailing <c>/</c> stripped from keys.
    /// <para>以序号方式比较路径的查找表，键已去除首尾 <c>/</c>。</para></returns>
    /// <exception cref="InvalidDataException">The file is larger than 64 MiB.
    /// <para>文件超过 64 MiB。</para></exception>
    public static IReadOnlyDictionary<string, FsConfigEntry> LoadFsConfig(string? path)
    {
        var result = new Dictionary<string, FsConfigEntry>(StringComparer.Ordinal);
        foreach (var line in ReadLines(path))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 4) continue;
            var imagePath = NormalizeImagePath(parts[0]);
            if (!uint.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var uid)) continue;
            if (!uint.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var gid)) continue;
            if (!FsConfigModes.TryParse(parts[3], out var mode)) continue;
            result[imagePath] = new FsConfigEntry(uid, gid, mode);
        }

        return result;
    }

    /// <summary>
    /// Loads a <c>file_contexts</c> file into a lookup keyed by the absolute image path.
    /// Returns an empty dictionary when <paramref name="path"/> is null, empty or unreadable.
    /// <para>加载 <c>file_contexts</c> 文件为以镜像路径为键的查找表。
    /// <paramref name="path"/> 为 null、空或不可读时返回空字典。</para>
    /// </summary>
    /// <param name="path">File path; <see langword="null"/>, whitespace, a missing file or a
    /// reparse point yields an empty table. Lines need at least two whitespace-separated tokens
    /// and the last token is taken as the label; a repeated path overwrites the earlier entry.
    /// <para>文件路径；为 <see langword="null"/>、仅空白、文件不存在或为重解析点时返回空表。
    /// 每行至少需要两个以空白分隔的记号，最后一个记号作为标签；重复路径覆盖先前条目。</para></param>
    /// <returns>Ordinal path-keyed SELinux labels with leading and trailing <c>/</c> stripped from keys.
    /// <para>以序号方式比较路径的 SELinux 标签查找表，键已去除首尾 <c>/</c>。</para></returns>
    /// <exception cref="InvalidDataException">The file is larger than 64 MiB.
    /// <para>文件超过 64 MiB。</para></exception>
    public static IReadOnlyDictionary<string, string> LoadFileContexts(string? path)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in ReadLines(path))
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 2) continue;
            var imagePath = NormalizeImagePath(parts[0]);
            result[imagePath] = parts[^1];
        }

        return result;
    }

    /// <summary>
    /// Looks up a normalized relative path; also accepts the root ("/").
    /// <para>按规整后的相对路径查找；根路径（"/"）同样可查。</para>
    /// </summary>
    /// <param name="table">Table produced by <see cref="LoadFsConfig"/>.
    /// <para>由 <see cref="LoadFsConfig"/> 生成的查找表。</para></param>
    /// <param name="relativePath">Path in either separator style; backslashes become slashes,
    /// surrounding slashes are trimmed, and <c>"."</c> maps to the root key.
    /// <para>任意分隔符风格的路径；反斜杠转为正斜杠，去除首尾斜杠，<c>"."</c> 映射到根键。</para></param>
    /// <param name="entry">The matched entry when <see langword="true"/>; default-initialized when
    /// <see langword="false"/>.
    /// <para>返回 <see langword="true"/> 时为命中的条目；返回 <see langword="false"/> 时为默认值。</para></param>
    /// <returns><see langword="true"/> when the normalized path exists in <paramref name="table"/>.
    /// <para>规整后的路径存在于 <paramref name="table"/> 中时返回 <see langword="true"/>。</para></returns>
    public static bool TryGetFsConfig(IReadOnlyDictionary<string, FsConfigEntry> table, string relativePath, out FsConfigEntry entry)
        => table.TryGetValue(NormalizeRelativePath(relativePath), out entry);

    /// <summary>
    /// Loads a partition_info file into a lookup keyed by the prefix-stripped key.
    /// <para>把 partition_info 文件加载为以去除分区前缀后的键为键的查找表。</para>
    /// </summary>
    /// <param name="path">File path; <see langword="null"/>, whitespace, a missing file or a
    /// reparse point yields an empty table. Lines must contain an <c>=</c> after a non-empty key;
    /// a repeated key overwrites the earlier value.
    /// <para>文件路径；为 <see langword="null"/>、仅空白、文件不存在或为重解析点时返回空表。
    /// 每行须在非空键之后包含 <c>=</c>；重复键覆盖先前值。</para></param>
    /// <returns>Ordinal lookup keyed by each line's key with the text up to the first <c>.</c>
    /// (the partition prefix) removed, values trimmed.
    /// <para>序号比较的查找表：键去除首个 <c>.</c>（含）及其左侧的分区前缀，值已去除首尾空白。</para></returns>
    /// <exception cref="InvalidDataException">The file is larger than 64 MiB.
    /// <para>文件超过 64 MiB。</para></exception>
    public static IReadOnlyDictionary<string, string> LoadPartitionInfo(string? path)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in ReadLines(path))
        {
            if (line.StartsWith('#')) continue;
            var separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0) continue;
            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            var dot = key.IndexOf('.', StringComparison.Ordinal);
            if (dot >= 0) key = key[(dot + 1)..];
            if (key.Length > 0) result[key] = value;
        }

        return result;
    }

    /// <summary>
    /// Looks up the SELinux label for a normalized relative path.
    /// <para>按规整后的相对路径查找 SELinux 标签。</para>
    /// </summary>
    /// <param name="table">Table produced by <see cref="LoadFileContexts"/>.
    /// <para>由 <see cref="LoadFileContexts"/> 生成的查找表。</para></param>
    /// <param name="relativePath">Path in either separator style; backslashes become slashes,
    /// surrounding slashes are trimmed, and <c>"."</c> maps to the root key.
    /// <para>任意分隔符风格的路径；反斜杠转为正斜杠，去除首尾斜杠，<c>"."</c> 映射到根键。</para></param>
    /// <param name="label">The matched label when <see langword="true"/>; <see langword="null"/>
    /// when <see langword="false"/>.
    /// <para>返回 <see langword="true"/> 时为命中标签；返回 <see langword="false"/> 时为 <see langword="null"/>。</para></param>
    /// <returns><see langword="true"/> when the normalized path exists in <paramref name="table"/>.
    /// <para>规整后的路径存在于 <paramref name="table"/> 中时返回 <see langword="true"/>。</para></returns>
    public static bool TryGetLabel(IReadOnlyDictionary<string, string> table, string relativePath, out string label)
        => table.TryGetValue(NormalizeRelativePath(relativePath), out label!);

    /// <summary>Builds the <c>security.selinux</c> xattr value (UTF-8, NUL-terminated) for a label.
    /// <para>构造 <c>security.selinux</c> xattr 值（UTF-8、NUL 结尾）。</para></summary>
    /// <param name="label">SELinux context string to encode. <para>待编码的 SELinux 上下文字符串。</para></param>
    /// <returns>The UTF-8 bytes of <paramref name="label"/> followed by one terminating <c>0</c> byte.
    /// <para><paramref name="label"/> 的 UTF-8 字节，末尾追加一个终止 <c>0</c> 字节。</para></returns>
    public static byte[] ToSelinuxXattrValue(string label)
    {
        var value = new byte[System.Text.Encoding.UTF8.GetByteCount(label) + 1];
        System.Text.Encoding.UTF8.GetBytes(label, value);
        return value;
    }

    private static string NormalizeImagePath(string imagePath)
        => imagePath.TrimStart('/').TrimEnd('/');

    private static string NormalizeRelativePath(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/').TrimStart('/').TrimEnd('/');
        return normalized == "." ? string.Empty : normalized;
    }

    private static IEnumerable<string> ReadLines(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) yield break;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) yield break;
        using var input = File.OpenRead(path);
        if (input.Length > 64 * 1024 * 1024) throw new InvalidDataException("Metadata overrides file exceeds 64 MiB.");
        using var reader = new StreamReader(input);
        while (reader.ReadLine() is { } line)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
            yield return trimmed;
        }
    }

    /// <summary>
    /// Loads the optional preservation payload carried by an already parsed metadata instance.
    /// Returns <c>null</c> when the format recorded none or the tree fails validation.
    /// <para>从已解析的元数据实例加载可选的保留负载。格式未记录或 JSON 树校验失败时返回 <c>null</c>。</para>
    /// </summary>
    /// <param name="metadata">The parsed metadata instance. <para>已解析的元数据实例。</para></param>
    /// <returns>The typed payload, or <see langword="null"/> when absent or invalid.
    /// <para>类型化负载；不存在或非法时返回 <see langword="null"/>。</para></returns>
    internal static FileSystemPreservationData? TryLoadDocument(RepackMetadata? metadata)
        => FileSystemPreservation.ReadPayload(metadata);

    /// <summary>
    /// Loads the filesystem override tables from the structured preservation payload carried by the metadata
    /// file. That payload is the only pack-time input: the flat <c>fs_config</c>, <c>file_contexts</c> and
    /// <c>info</c> files beside the metadata are extraction-time exports for external tools and this path never
    /// reads them back, so a pack cannot be steered by a sidecar the same extraction wrote.
    /// <para>从元数据文件携带的结构化保留负载加载文件系统覆盖表。该负载是打包期唯一的输入：元数据旁的扁平
    /// <c>fs_config</c>、<c>file_contexts</c> 与 <c>info</c> 只是供外部工具消费的解包期导出物，本路径绝不回读，
    /// 因此打包不可能被同一次解包写出的附属文件左右。</para>
    /// </summary>
    /// <param name="metadata">The parsed metadata instance. <para>已解析的元数据实例。</para></param>
    /// <returns>The tables and rebuild parameters recorded in the payload.
    /// <para>负载中记录的各表与重建参数。</para></returns>
    /// <exception cref="InvalidDataException">The metadata records no structured payload, or the payload fails
    /// validation; there is no flat-sidecar fallback.
    /// <para>元数据未记录结构化负载，或负载校验失败；不存在扁平附属文件回退。</para></exception>
    internal static (
        IReadOnlyDictionary<string, FsConfigEntry> FsConfig,
        IReadOnlyDictionary<string, string> FileContexts,
        IReadOnlyDictionary<string, string> PartitionInfo,
        RebuildParameters? Rebuild) LoadTables(RepackMetadata metadata)
    {
        var document = TryLoadDocument(metadata)
            ?? throw new InvalidDataException("Filesystem structured rebuild metadata is missing or invalid.");
        return (document.FsConfig, document.FileContexts, document.PartitionInfo, document.Rebuild);
    }
}