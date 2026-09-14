using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4;
using UotanStudio.Firmware.BoundedContexts.FileSystem.F2fs;

namespace UotanStudio.Firmware.SharedKernel;

/// <summary>
/// Generates Android-style filesystem metadata files (<c>fs_config</c>, <c>file_contexts</c>, <c>partition_info</c>)
/// from parser inode data after extraction.
/// <para>从解析器 inode 数据生成 Android 风格的文件系统元数据文件（<c>fs_config</c>、<c>file_contexts</c>、<c>partition_info</c>）。</para>
/// </summary>
public static class FileSystemMetadataGenerator
{
    private const string DefaultSelinuxLabel = "u:object_r:system_file:s0";

    /// <summary>
    /// Generates all three metadata files for an EXT4 extraction.
    /// <para>为 EXT4 解包生成全部三个元数据文件。</para>
    /// </summary>
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
    public static void GenerateFromDirectory(string metadataDirectory, string partitionName, string extractionDirectory)
    {
        ArgumentNullException.ThrowIfNull(extractionDirectory);
        Directory.CreateDirectory(metadataDirectory);
        var paths = new List<string> { "." };
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
    public static string GetMetadataFileName(string partitionName, string suffix)
        => $"{partitionName}_{suffix}";

    /// <summary>
    /// Detects the partition name from a source filename (strips extension).
    /// <para>从源文件名检测分区名（去除扩展名）。</para>
    /// </summary>
    public static string DetectPartitionName(string sourceFileName)
    {
        var name = Path.GetFileNameWithoutExtension(sourceFileName);
        // Strip .raw extension added by sparse extraction
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
            // Normalize path: remove leading "./", ensure root is "/"
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
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, content, Encoding.UTF8);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch { /* best-effort cleanup */ }
        }
    }
}