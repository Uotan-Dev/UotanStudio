using System.Globalization;
using System.Text.Json;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.Core.Repacking;

namespace UotanStudio.Firmware.FileSystem.Metadata;

/// <summary>
/// One parsed <c>fs_config</c> entry: POSIX identity and permission bits for an image path.
/// <para>解析后的 <c>fs_config</c> 条目：镜像路径的 POSIX 身份与权限位。</para>
/// </summary>
/// <param name="Uid">Owner user identifier parsed as an invariant decimal.
/// <para>以不变文化十进制解析的所有者用户 ID。</para></param>
/// <param name="Gid">Owner group identifier parsed as an invariant decimal.
/// <para>以不变文化十进制解析的所有者组 ID。</para></param>
/// <param name="Mode">Permission bits, accepted either as a decimal value or as a leading-zero
/// octal string of digits <c>0</c>-<c>7</c>.
/// <para>权限位；接受十进制值，或以 <c>0</c> 开头、仅含 <c>0</c>-<c>7</c> 数字的八进制字符串。</para></param>
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
    /// reparse point yields an empty table. Lines need at least four space-separated tokens and
    /// lines with unparsable numeric fields are skipped; a repeated path overwrites the earlier entry.
    /// <para>文件路径；为 <see langword="null"/>、仅空白、文件不存在或为重解析点时返回空表。
    /// 每行至少需要四个以空格分隔的记号，数值字段无法解析的行会被跳过；重复路径覆盖先前条目。</para></param>
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
            if (!TryParseMode(parts[3], out var mode)) continue;
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
    /// Loads a <c>partition_info</c> file (<c>partition.key=value</c> lines) into a lookup keyed by
    /// the key with the partition prefix stripped. Comment and blank lines are skipped.
    /// Returns an empty dictionary when <paramref name="path"/> is null, empty or unreadable.
    /// <para>加载 <c>partition_info</c> 文件（<c>partition.key=value</c> 行）为以去除分区前缀后的
    /// key 为键的查找表。跳过注释与空行。<paramref name="path"/> 为 null、空或不可读时返回空字典。</para>
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

    private static bool TryParseMode(string text, out ushort mode)
    {
        if (text.Length > 1 && text[0] == '0' && text.All(static c => c is >= '0' and <= '7'))
        {
            mode = Convert.ToUInt16(text, 8);
            return true;
        }

        return ushort.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out mode);
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

    private const string MetadataFolderName = ".filesystem";
    private const string MetadataFileName = "metadata.json";

    /// <summary>
    /// Loads an optional <c>metadata.json</c> document from the preservation directory
    /// (<c>.filesystem/&lt;id&gt;/metadata.json</c>). Returns <c>null</c> when absent, the id is
    /// invalid, or the document fails validation.
    /// <para>从保留目录（<c>.filesystem/&lt;id&gt;/metadata.json</c>）加载可选的 <c>metadata.json</c> 文档。
    /// 不存在、id 非法或文档校验失败时返回 <c>null</c>。</para>
    /// </summary>
    internal static FilesystemMetadataDocument? TryLoadDocument(string metadataDirectory, string sourceFileName, string? preservationId)
    {
        if (string.IsNullOrWhiteSpace(metadataDirectory) || string.IsNullOrWhiteSpace(sourceFileName)
            || preservationId is not { Length: 32 } || !Guid.TryParseExact(preservationId, "N", out _))
            return null;
        var path = FileExtraction.ResolvePath(metadataDirectory, MetadataFolderName + "/" + preservationId + "/" + MetadataFileName);
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return null;
        using var input = File.OpenRead(path);
        FilesystemMetadataDocument? document;
        try
        {
            document = JsonSerializer.Deserialize(input, FilesystemMetadataJsonContext.Default.FilesystemMetadataDocument);
        }
        catch (JsonException)
        {
            return null;
        }

        if (document is null || document.Version != 1 || document.FileContexts is null
            || document.FsConfig is null || document.PartitionInfo is null)
            return null;
        return document;
    }

    /// <summary>
    /// Builds a rich document from the on-disk Android-style flat sidecars, for persisting as
    /// <c>metadata.json</c> when no original image is retained.
    /// <para>从磁盘上的 Android 风格扁平附属文件构建富文档，用于在未保留原镜像时持久化为 <c>metadata.json</c>。</para>
    /// </summary>
    internal static FilesystemMetadataDocument BuildDocument(string metadataDirectory, string sourceFileName, RebuildParameters? rebuild = null)
    {
        var partition = RepackMetadata.DetectPartitionName(sourceFileName);
        var prefix = FileSystemMetadataGenerator.GetMetadataFileName(partition, "");
        return new FilesystemMetadataDocument
        {
            Partition = partition,
            FileContexts = new Dictionary<string, string>(
                LoadFileContexts(Path.Combine(metadataDirectory, prefix + "file_contexts")), StringComparer.Ordinal),
            FsConfig = new Dictionary<string, FsConfigEntry>(
                LoadFsConfig(Path.Combine(metadataDirectory, prefix + "fs_config")), StringComparer.Ordinal),
            PartitionInfo = new Dictionary<string, string>(
                LoadPartitionInfo(Path.Combine(metadataDirectory, prefix + "info")), StringComparer.Ordinal),
            Rebuild = rebuild
        };
    }

    /// <summary>
    /// Loads the filesystem override tables, preferring the structured <c>metadata.json</c> and
    /// falling back to parsing the flat sidecars when it is absent.
    /// <para>加载文件系统覆盖表：优先使用结构化的 <c>metadata.json</c>，不存在时回退解析扁平附属文件。</para>
    /// </summary>
    internal static (
        IReadOnlyDictionary<string, FsConfigEntry> FsConfig,
        IReadOnlyDictionary<string, string> FileContexts,
        IReadOnlyDictionary<string, string> PartitionInfo,
        RebuildParameters? Rebuild) LoadTables(string metadataDirectory, string sourceFileName, string? preservationId,
            bool requireStructured = false)
    {
        var document = TryLoadDocument(metadataDirectory, sourceFileName, preservationId);
        if (document is null)
        {
            if (requireStructured)
            {
                throw new InvalidDataException("Filesystem structured rebuild metadata is missing or invalid.");
            }
            var partition = RepackMetadata.DetectPartitionName(sourceFileName);
            var prefix = FileSystemMetadataGenerator.GetMetadataFileName(partition, "");
            return (
                LoadFsConfig(Path.Combine(metadataDirectory, prefix + "fs_config")),
                LoadFileContexts(Path.Combine(metadataDirectory, prefix + "file_contexts")),
                LoadPartitionInfo(Path.Combine(metadataDirectory, prefix + "info")),
                null);
        }

        return (document.FsConfig, document.FileContexts, document.PartitionInfo, document.Rebuild);
    }
}