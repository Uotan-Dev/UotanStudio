using System.Globalization;
using System.Text.Json;
using UotanStudio.Firmware.SharedKernel;
using UotanStudio.Firmware.SharedKernel.Repacking;

namespace UotanStudio.Firmware.BoundedContexts.FileSystem.Metadata;

/// <summary>
/// One parsed <c>fs_config</c> entry: POSIX identity and permission bits for an image path.
/// <para>解析后的 <c>fs_config</c> 条目：镜像路径的 POSIX 身份与权限位。</para>
/// </summary>
public readonly record struct FsConfigEntry(uint Uid, uint Gid, ushort Mode);

/// <summary>
/// Parses Android-style <c>fs_config</c> and <c>file_contexts</c> files into path-keyed lookups
/// consumed by the filesystem builders to restore ownership, permissions and SELinux labels.
/// <para>将 Android 风格的 <c>fs_config</c> 与 <c>file_contexts</c> 解析为按路径索引的查找表，
/// 供文件系统构建器还原属主、权限与 SELinux 标签。</para>
/// </summary>
public static class FileSystemMetadataOverrides
{
    private const string SelinuxXattrName = "security.selinux";

    /// <summary>
    /// Loads an <c>fs_config</c> file into a lookup keyed by the absolute image path
    /// (leading "/" stripped, invariant-case on Windows platforms is preserved as-is).
    /// Returns an empty dictionary when <paramref name="path"/> is null, empty or unreadable.
    /// <para>加载 <c>fs_config</c> 文件为以镜像路径为键的查找表（去除前导 "/"）。
    /// <paramref name="path"/> 为 null、空或不可读时返回空字典。</para>
    /// </summary>
    public static IReadOnlyDictionary<string, FsConfigEntry> LoadFsConfig(string? path)
    {
        var result = new Dictionary<string, FsConfigEntry>(StringComparer.Ordinal);
        foreach (var line in ReadLines(path))
        {
            // Format: path uid gid mode capabilities  (capabilities optional/ignored — always 0 here)
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 4) continue;
            var imagePath = NormalizeImagePath(parts[0]);
            if (!uint.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var uid)) continue;
            if (!uint.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var gid)) continue;
            // Mode may be decimal or octal (0-prefixed); fs_config from this tool writes decimal.
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
    public static IReadOnlyDictionary<string, string> LoadFileContexts(string? path)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in ReadLines(path))
        {
            // Format: path<whitespace>label  (label itself never contains whitespace)
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
    public static bool TryGetFsConfig(IReadOnlyDictionary<string, FsConfigEntry> table, string relativePath, out FsConfigEntry entry)
        => table.TryGetValue(NormalizeRelativePath(relativePath), out entry);

    /// <summary>
    /// Loads a <c>partition_info</c> file (<c>partition.key=value</c> lines) into a lookup keyed by
    /// the key with the partition prefix stripped. Comment and blank lines are skipped.
    /// Returns an empty dictionary when <paramref name="path"/> is null, empty or unreadable.
    /// <para>加载 <c>partition_info</c> 文件（<c>partition.key=value</c> 行）为以去除分区前缀后的
    /// key 为键的查找表。跳过注释与空行。<paramref name="path"/> 为 null、空或不可读时返回空字典。</para>
    /// </summary>
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
            // Strip the "<partition>." prefix if present.
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
    public static bool TryGetLabel(IReadOnlyDictionary<string, string> table, string relativePath, out string label)
        => table.TryGetValue(NormalizeRelativePath(relativePath), out label!);

    /// <summary>Builds the <c>security.selinux</c> xattr value (UTF-8, NUL-terminated) for a label.
    /// <para>构造 <c>security.selinux</c> xattr 值（UTF-8、NUL 结尾）。</para></summary>
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
        // Octal with 0 prefix (0755), otherwise decimal (493).
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