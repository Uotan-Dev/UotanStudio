using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using UotanStudio.Firmware.Core.IO;
using UotanStudio.Firmware.FileSystem.Erofs.Parser;
using UotanStudio.Firmware.FileSystem.Ext4;
using UotanStudio.Firmware.FileSystem.F2fs;

namespace UotanStudio.Firmware.FileSystem.Metadata;

/// <summary>
/// Generates Android-style filesystem metadata files (<c>fs_config</c>, <c>file_contexts</c>, <c>partition_info</c>)
/// from parser inode data after extraction.
/// <para>从解析器 inode 数据生成 Android 风格的文件系统元数据文件（<c>fs_config</c>、<c>file_contexts</c>、<c>partition_info</c>）。</para>
/// </summary>
internal static class FileSystemMetadataGenerator
{
    private const string DefaultSelinuxLabel = "u:object_r:system_file:s0";

    /// <summary>
    /// Generates all three metadata files for an EXT4 extraction.
    /// <para>为 EXT4 解包生成全部三个元数据文件。</para>
    /// </summary>
    /// <param name="metadataDirectory">Directory the files are written to; created when missing.
    /// <para>写入目标的目录；不存在时自动创建。</para></param>
    /// <param name="partitionName">Prefix applied to all three generated file names.
    /// <para>作为三个生成文件名前缀的分区名。</para></param>
    /// <param name="parser">Opened EXT4 parser supplying entries (root included), ownership, modes and xattrs.
    /// <para>已打开的 EXT4 解析器，提供条目（含根目录）、属主、模式与扩展属性。</para></param>
    /// <param name="superblockConfig">Optional JSON object whose members are written as
    /// <c>partition.key=value</c> lines; <see langword="null"/> emits only the default
    /// <c>partition_name</c> line.
    /// <para>可选 JSON 对象，其成员以 <c>分区.key=value</c> 行写入；为 <see langword="null"/> 时仅输出默认的
    /// <c>partition_name</c> 行。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="parser"/> is <see langword="null"/>.
    /// <para><paramref name="parser"/> 为 <see langword="null"/>。</para></exception>
    public static void GenerateForExt4(string metadataDirectory, string partitionName, Ext4Parser parser, JsonObject? superblockConfig = null)
    {
        ArgumentNullException.ThrowIfNull(parser);
        Directory.CreateDirectory(metadataDirectory);
        var entries = parser.ListEntriesWithRoot();
        WriteFsConfig(metadataDirectory, partitionName, entries.Select(e => (
            e.Path,
            e.Uid,
            e.Gid,
            e.Mode,
            e.Kind == Ext4Parser.Ext4EntryKind.Directory)));
        WriteFileContextsFromXattrs(metadataDirectory, partitionName, entries.Select(e => (
            e.Path,
            e.Xattrs)));
        WritePartitionInfo(metadataDirectory, partitionName, superblockConfig);
    }

    /// <summary>
    /// Generates all three metadata files for an EROFS extraction.
    /// <para>为 EROFS 解包生成全部三个元数据文件。</para>
    /// </summary>
    /// <param name="metadataDirectory">Directory the files are written to; created when missing.
    /// <para>写入目标的目录；不存在时自动创建。</para></param>
    /// <param name="partitionName">Prefix applied to all three generated file names.
    /// <para>作为三个生成文件名前缀的分区名。</para></param>
    /// <param name="parser">Opened EROFS parser supplying the enumerated entries, ownership, modes and xattrs.
    /// <para>已打开的 EROFS 解析器，提供枚举出的条目、属主、模式与扩展属性。</para></param>
    /// <param name="superblockConfig">Optional JSON object whose members are written as
    /// <c>partition.key=value</c> lines; <see langword="null"/> emits only the default
    /// <c>partition_name</c> line.
    /// <para>可选 JSON 对象，其成员以 <c>分区.key=value</c> 行写入；为 <see langword="null"/> 时仅输出默认的
    /// <c>partition_name</c> 行。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="parser"/> is <see langword="null"/>.
    /// <para><paramref name="parser"/> 为 <see langword="null"/>。</para></exception>
    public static void GenerateForErofs(string metadataDirectory, string partitionName, ErofsParser parser, JsonObject? superblockConfig = null)
    {
        ArgumentNullException.ThrowIfNull(parser);
        Directory.CreateDirectory(metadataDirectory);
        var entries = parser.EnumerateEntries().ToList();
        WriteFsConfig(metadataDirectory, partitionName, entries.Select(e => (
            e.Path,
            e.Uid,
            e.Gid,
            e.Mode,
            e.IsDirectory)));
        WriteFileContextsFromErofsXattrs(metadataDirectory, partitionName, entries);
        WritePartitionInfo(metadataDirectory, partitionName, superblockConfig);
    }

    /// <summary>
    /// Generates all three metadata files for an F2FS extraction.
    /// <para>为 F2FS 解包生成全部三个元数据文件。</para>
    /// </summary>
    /// <param name="metadataDirectory">Directory the files are written to; created when missing.
    /// <para>写入目标的目录；不存在时自动创建。</para></param>
    /// <param name="partitionName">Prefix applied to all three generated file names.
    /// <para>作为三个生成文件名前缀的分区名。</para></param>
    /// <param name="parser">Opened F2FS parser supplying entries (root included), ownership, modes and xattrs.
    /// <para>已打开的 F2FS 解析器，提供条目（含根目录）、属主、模式与扩展属性。</para></param>
    /// <param name="superblockConfig">Optional JSON object whose members are written as
    /// <c>partition.key=value</c> lines; <see langword="null"/> emits only the default
    /// <c>partition_name</c> line.
    /// <para>可选 JSON 对象，其成员以 <c>分区.key=value</c> 行写入；为 <see langword="null"/> 时仅输出默认的
    /// <c>partition_name</c> 行。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="parser"/> is <see langword="null"/>.
    /// <para><paramref name="parser"/> 为 <see langword="null"/>。</para></exception>
    public static void GenerateForF2fs(string metadataDirectory, string partitionName, F2fsParser parser, JsonObject? superblockConfig = null)
    {
        ArgumentNullException.ThrowIfNull(parser);
        Directory.CreateDirectory(metadataDirectory);
        var entries = parser.ListEntriesWithRoot();
        WriteFsConfig(metadataDirectory, partitionName, entries.Select(e => (
            e.Path,
            e.Uid,
            e.Gid,
            e.Mode,
            e.Kind == F2fsParser.F2fsEntryKind.Directory)));
        WriteFileContextsFromXattrs(metadataDirectory, partitionName, entries.Select(e => (e.Path, e.Xattrs)));
        WritePartitionInfo(metadataDirectory, partitionName, superblockConfig);
    }

    /// <summary>
    /// Generates metadata files using the extraction directory on disk (fallback when parser is unavailable).
    /// <para>使用磁盘上的解包目录生成元数据文件（解析器不可用时的回退方案）。</para>
    /// </summary>
    /// <param name="metadataDirectory">Directory the files are written to; created when missing.
    /// <para>写入目标的目录；不存在时自动创建。</para></param>
    /// <param name="partitionName">Prefix applied to all three generated file names.
    /// <para>作为三个生成文件名前缀的分区名。</para></param>
    /// <param name="extractionDirectory">Tree enumerated recursively; every path gets uid/gid/mode
    /// <c>0</c>, which the writer expands to <c>0755</c> for directories and <c>0644</c> for files,
    /// and the default SELinux label. A missing directory yields only the root entry.
    /// <para>递归枚举的目录树；每个条目的 uid/gid/mode 记为 <c>0</c>，写入器将其展开为目录 <c>0755</c>、
    /// 文件 <c>0644</c>，并使用默认 SELinux 标签。目录不存在时仅生成根条目。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="extractionDirectory"/> is <see langword="null"/>.
    /// <para><paramref name="extractionDirectory"/> 为 <see langword="null"/>。</para></exception>
    public static void GenerateFromDirectory(string metadataDirectory, string partitionName, string extractionDirectory)
    {
        ArgumentNullException.ThrowIfNull(extractionDirectory);
        Directory.CreateDirectory(metadataDirectory);
        List<string> paths = ["."];
        if (Directory.Exists(extractionDirectory))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(extractionDirectory, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(extractionDirectory, entry).Replace('\\', '/');
                paths.Add(rel);
            }
        }

        WriteFsConfig(metadataDirectory, partitionName, paths.Select(p => (
            p,
            (uint)0,
            (uint)0,
            (ushort)0,
            Directory.Exists(Path.Combine(extractionDirectory, p)))));
        WriteFileContextsWithDefaults(metadataDirectory, partitionName, paths);
        WritePartitionInfo(metadataDirectory, partitionName, null);
    }

    /// <summary>
    /// Returns the prefixed filename for a metadata file.
    /// <para>返回带分区名前缀的元数据文件名。</para>
    /// </summary>
    /// <param name="partitionName">Partition prefix placed before the underscore.
    /// <para>置于下划线左侧的分区名前缀。</para></param>
    /// <param name="suffix">Metadata kind, e.g. <c>fs_config</c>, <c>file_contexts</c> or <c>info</c>;
    /// an empty value yields the bare <c>partition_</c> prefix used for sidecar lookups.
    /// <para>元数据类别，如 <c>fs_config</c>、<c>file_contexts</c> 或 <c>info</c>；为空时返回用于附属文件
    /// 查找的 <c>分区_</c> 前缀。</para></param>
    /// <returns><c>{partitionName}_{suffix}</c>. <para>形如 <c>{分区名}_{后缀}</c> 的文件名。</para></returns>
    public static string GetMetadataFileName(string partitionName, string suffix)
        => $"{partitionName}_{suffix}";

    /// <summary>
    /// Detects the partition name from a source filename (strips extension).
    /// <para>从源文件名检测分区名（去除扩展名）。</para>
    /// </summary>
    /// <param name="sourceFileName">Path or file name of the source image.
    /// <para>源镜像的路径或文件名。</para></param>
    /// <returns>The file name without its extension, with a further trailing <c>.raw</c> stripped
    /// case-insensitively; <c>"unknown"</c> when nothing is left.
    /// <para>去除扩展名后的文件名，还会不区分大小写地剥离紧随其后的 <c>.raw</c>；若结果为空则返回
    /// <c>"unknown"</c>。</para></returns>
    public static string DetectPartitionName(string sourceFileName)
    {
        var name = Path.GetFileNameWithoutExtension(sourceFileName);
        if (name.EndsWith(".raw", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        return string.IsNullOrWhiteSpace(name) ? "unknown" : name;
    }

    private static void WriteFsConfig(string metadataDirectory, string partitionName,
        IEnumerable<(string Path, uint Uid, uint Gid, ushort Mode, bool IsDirectory)> entries)
    {
        var fileName = GetMetadataFileName(partitionName, "fs_config");
        var path = Path.Combine(metadataDirectory, fileName);
        var sb = new StringBuilder();
        foreach (var (entryPath, uid, gid, mode, isDir) in entries)
        {
            var normalized = NormalizePath(entryPath);
            var permBits = mode != 0 ? (mode & 0xFFF) : (isDir ? 0x1ED : 0x1A4);
            var capabilities = 0;
            sb.Append(normalized);
            sb.Append(' ');
            sb.Append(uid.ToString(CultureInfo.InvariantCulture));
            sb.Append(' ');
            sb.Append(gid.ToString(CultureInfo.InvariantCulture));
            sb.Append(' ');
            sb.Append(permBits.ToString(CultureInfo.InvariantCulture));
            sb.Append(' ');
            sb.AppendLine(capabilities.ToString(CultureInfo.InvariantCulture));
        }

        WriteAtomic(path, sb.ToString());
    }

    private static void WriteFileContextsFromXattrs(string metadataDirectory, string partitionName,
        IEnumerable<(string Path, IReadOnlyDictionary<string, byte[]>? Xattrs)> entries)
    {
        var fileName = GetMetadataFileName(partitionName, "file_contexts");
        var path = Path.Combine(metadataDirectory, fileName);
        var sb = new StringBuilder();
        foreach (var (entryPath, xattrs) in entries)
        {
            var normalized = NormalizePath(entryPath);
            string? label = null;
            if (xattrs is not null && xattrs.TryGetValue("security.selinux", out var selinuxValue))
            {
                var decoded = Encoding.UTF8.GetString(selinuxValue).TrimEnd('\0');
                if (!string.IsNullOrWhiteSpace(decoded))
                    label = decoded;
            }

            if (label is not null)
            {
                sb.Append(normalized);
                sb.Append("    ");
                sb.AppendLine(label);
            }
        }

        WriteAtomic(path, sb.ToString());
    }

    private static void WriteFileContextsFromErofsXattrs(string metadataDirectory, string partitionName,
        IReadOnlyList<ErofsFileEntry> entries)
    {
        var fileName = GetMetadataFileName(partitionName, "file_contexts");
        var path = Path.Combine(metadataDirectory, fileName);
        var sb = new StringBuilder();
        foreach (var entry in entries)
        {
            var normalized = NormalizePath(entry.Path);
            string? label = null;
            if (entry.Xattrs is not null)
            {
                var selinux = entry.Xattrs.FirstOrDefault(x => x.Name == "security.selinux");
                if (selinux?.Name is not null)
                {
                    var decoded = Encoding.UTF8.GetString(selinux.Value).TrimEnd('\0');
                    if (!string.IsNullOrWhiteSpace(decoded))
                        label = decoded;
                }
            }

            if (label is not null)
            {
                sb.Append(normalized);
                sb.Append("    ");
                sb.AppendLine(label);
            }
        }

        WriteAtomic(path, sb.ToString());
    }

    private static void WriteFileContextsWithDefaults(string metadataDirectory, string partitionName,
        IEnumerable<string> paths)
    {
        var fileName = GetMetadataFileName(partitionName, "file_contexts");
        var path = Path.Combine(metadataDirectory, fileName);
        var sb = new StringBuilder();
        foreach (var entryPath in paths)
        {
            var normalized = NormalizePath(entryPath);
            sb.Append(normalized);
            sb.Append("    ");
            sb.AppendLine(DefaultSelinuxLabel);
        }

        WriteAtomic(path, sb.ToString());
    }

    private static void WritePartitionInfo(string metadataDirectory, string partitionName, JsonObject? superblockConfig)
    {
        var fileName = GetMetadataFileName(partitionName, "info");
        var path = Path.Combine(metadataDirectory, fileName);
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"# Partition: {partitionName}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"# Generated by UotanStudio");
        sb.AppendLine();
        if (superblockConfig is not null)
        {
            foreach (var (key, value) in superblockConfig)
            {
                var strValue = value?.ToString() ?? string.Empty;
                sb.AppendLine(CultureInfo.InvariantCulture, $"{partitionName}.{key}={strValue}");
            }
        }
        else
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{partitionName}.partition_name={partitionName}");
        }

        WriteAtomic(path, sb.ToString());
    }

    private static string NormalizePath(string path)
    {
        if (path is "." or "")
            return "/";
        var normalized = path.Replace('\\', '/');
        if (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];
        return "/" + normalized;
    }

    private static void WriteAtomic(string path, string content)
    {
        var temporary = TempFileHelper.CreateAdjacentPath(path);
        try
        {
            File.WriteAllText(temporary, content, Encoding.UTF8);
            AtomicPublish.Publish(temporary, path);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch { }
        }
    }
}