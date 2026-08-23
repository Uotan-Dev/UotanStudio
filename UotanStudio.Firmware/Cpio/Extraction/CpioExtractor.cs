using System.Text;
using UotanStudio.Firmware.Cpio.Internal;
using UotanStudio.Firmware.Cpio.Model;

namespace UotanStudio.Firmware.Cpio.Extraction;

/// <summary>
/// Extracts normalized archive entries to the file system.
/// <para>将规范化的归档条目解包到文件系统。</para>
/// </summary>
public sealed class CpioExtractor
{
    /// <summary>
    /// Extracts archive entries to a destination directory.
    /// <para>将归档条目解包到目标目录。</para>
    /// </summary>
    /// <param name="entries">Archive entries to extract.<para>待解包的归档条目。</para></param>
    /// <param name="destinationDirectory">Target directory path.<para>目标目录路径。</para></param>
    /// <param name="options">Optional extraction behavior options.<para>可选的解包行为选项。</para></param>
    public void ExtractToDirectory(IEnumerable<ArchiveEntry> entries, string destinationDirectory, ExtractOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(destinationDirectory);

        var opts = options ?? new ExtractOptions();
        Directory.CreateDirectory(destinationDirectory);
        var renameMap = NormalizeRenameMap(opts.RenameMap);
        var includePatterns = ResolveIncludePatterns(opts);

        var inodeMap = new Dictionary<(ulong Inode, uint DevMajor, uint DevMinor), string>();
        foreach (var entry in entries)
        {
            var transformedEntry = TransformEntry(entry, renameMap);
            if (!ShouldInclude(transformedEntry.Name, includePatterns, opts.ExcludePatterns, opts.NonMatching))
            {
                continue;
            }

            var targetPath = BuildSafePath(destinationDirectory, transformedEntry.Name, opts);
            EnsureParentDirectory(targetPath, opts.MakeDirectories);

            switch (transformedEntry.Metadata.FileType)
            {
                case CpioFileType.Directory:
                    Directory.CreateDirectory(targetPath);
                    break;
                case CpioFileType.SymbolicLink:
                    ExtractSymbolicLink(transformedEntry, targetPath, opts);
                    break;
                case CpioFileType.HardLink:
                    ExtractHardLink(transformedEntry, targetPath, inodeMap, opts);
                    break;
                case CpioFileType.Fifo:
                case CpioFileType.CharacterDevice:
                case CpioFileType.BlockDevice:
                    ExtractSpecialFile(targetPath, opts);
                    break;
                case CpioFileType.Unknown:
                    break;
                case CpioFileType.Regular:
                    break;
                case CpioFileType.Socket:
                    break;
                default:
                    ExtractRegularLikeFile(transformedEntry, targetPath, opts);
                    RegisterInodeMapping(transformedEntry, targetPath, inodeMap, opts.DeviceIndependentHardlinks);
                    break;
            }

            if (opts.RestoreModificationTime)
            {
                TryRestoreMtime(targetPath, transformedEntry.Metadata.ModificationTimeUnixSeconds);
            }

            ApplyMetadataBestEffort(targetPath, transformedEntry, opts);
        }
    }

    private static void ExtractRegularLikeFile(ArchiveEntry entry, string targetPath, ExtractOptions options)
    {
        if (File.Exists(targetPath))
        {
            if (!options.Overwrite)
            {
                return;
            }

            File.Delete(targetPath);
        }

        File.WriteAllBytes(targetPath, entry.Data);
    }

    private static void ExtractSymbolicLink(ArchiveEntry entry, string targetPath, ExtractOptions options)
    {
        var linkTarget = entry.LinkName ?? Encoding.UTF8.GetString(entry.Data);
        if (File.Exists(targetPath) || Directory.Exists(targetPath))
        {
            if (!options.Overwrite)
            {
                return;
            }

            TryDeleteFileSystemNode(targetPath);
        }

        try
        {
            File.CreateSymbolicLink(targetPath, linkTarget);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Best effort: fall back to a regular file holding the link target text.
            File.WriteAllBytes(targetPath, Encoding.UTF8.GetBytes(linkTarget));
        }
    }

    private static void ExtractHardLink(
        ArchiveEntry entry,
        string targetPath,
        Dictionary<(ulong Inode, uint DevMajor, uint DevMinor), string> inodeMap,
        ExtractOptions options)
    {
        if (File.Exists(targetPath) || Directory.Exists(targetPath))
        {
            if (!options.Overwrite)
            {
                return;
            }

            TryDeleteFileSystemNode(targetPath);
        }

        if (entry.LinkName is { Length: > 0 })
        {
            var parent = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(parent))
            {
                var linkTargetPath = BuildSafePath(parent, entry.LinkName, options);
                if (File.Exists(linkTargetPath) || Directory.Exists(linkTargetPath))
                {
                    // Managed .NET has no portable hard-link API; fall back to a content copy.
                    File.Copy(linkTargetPath, targetPath, overwrite: true);
                    return;
                }
            }
        }

        var key = GetInodeKey(entry, options.DeviceIndependentHardlinks);
        if (inodeMap.TryGetValue(key, out var existingPath))
        {
            // Managed .NET has no portable hard-link API; fall back to a content copy.
            File.Copy(existingPath, targetPath, overwrite: true);
            return;
        }

        // No link source available: fall back to a regular file copy.
        ExtractRegularLikeFile(entry, targetPath, options);
        RegisterInodeMapping(entry, targetPath, inodeMap, options.DeviceIndependentHardlinks);
    }

    private static void ExtractSpecialFile(string targetPath, ExtractOptions options)
    {
        if (File.Exists(targetPath) || Directory.Exists(targetPath))
        {
            if (!options.Overwrite)
            {
                return;
            }

            TryDeleteFileSystemNode(targetPath);
        }

        // Special files cannot be created portably; fall back to an empty regular file.
        File.WriteAllBytes(targetPath, []);
    }

    private static void ApplyMetadataBestEffort(string targetPath, ArchiveEntry entry, ExtractOptions options)
    {
        if (options.RestorePermissions && entry.Metadata.FileType != CpioFileType.SymbolicLink)
        {
            TrySetPermissions(targetPath, entry.Metadata.UnixPermissions);
        }

        if (options.RestoreOwnership && !OperatingSystem.IsWindows())
        {
            // Best-effort ownership restoration is intentionally skipped; it usually
            // requires elevated privileges and has no portable managed API.
        }
    }

    private static void TrySetPermissions(string path, int permissions)
    {
        if (OperatingSystem.IsWindows())
        {
            // Unix file modes are not supported on Windows.
            return;
        }

        try
        {
            var mode = (UnixFileMode)(permissions & 0x0FFF);
            if (Directory.Exists(path))
            {
                File.SetUnixFileMode(path, mode);
            }
            else if (File.Exists(path))
            {
                File.SetUnixFileMode(path, mode);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Best effort only.
        }
    }

    private static (ulong Inode, uint DevMajor, uint DevMinor) GetInodeKey(ArchiveEntry entry, bool deviceIndependent)
    {
        return deviceIndependent
            ? (entry.Metadata.Inode, 0, 0)
            : (entry.Metadata.Inode, entry.Metadata.DeviceMajor, entry.Metadata.DeviceMinor);
    }

    private static void RegisterInodeMapping(
        ArchiveEntry entry,
        string path,
        Dictionary<(ulong Inode, uint DevMajor, uint DevMinor), string> map,
        bool deviceIndependent)
    {
        if (entry.Metadata.Inode == 0)
        {
            return;
        }

        map[GetInodeKey(entry, deviceIndependent)] = path;
    }

    private static string BuildSafePath(string destinationDirectory, string entryName, ExtractOptions options)
    {
        var normalized = NormalizeEntryPath(entryName, options);
        var relative = normalized.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var combined = Path.GetFullPath(Path.Combine(destinationDirectory, relative));
        var root = Path.GetFullPath(destinationDirectory);
        return !IsPathWithinRoot(combined, root)
            ? throw new InvalidDataException($"Entry '{entryName}' escapes destination directory.")
            : combined;
    }

    private static bool IsPathWithinRoot(string candidatePath, string rootPath)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var normalizedRoot = rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var rootWithSeparator = normalizedRoot + Path.DirectorySeparatorChar;

        return string.Equals(candidatePath, normalizedRoot, comparison)
               || candidatePath.StartsWith(rootWithSeparator, comparison);
    }

    private static void EnsureParentDirectory(string path, bool makeDirectories)
    {
        var parent = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(parent) || Directory.Exists(parent))
        {
            return;
        }

        if (!makeDirectories)
        {
            throw new DirectoryNotFoundException($"Parent directory '{parent}' does not exist. Enable MakeDirectories to create it.");
        }

        Directory.CreateDirectory(parent);
    }

    private static string NormalizeEntryPath(string entryName, ExtractOptions options)
    {
        var normalizedInput = entryName.Replace('\\', '/');
        if (!IsAbsoluteArchivePath(entryName, normalizedInput))
        {
            return PathPatternMatcher.NormalizeArchivePath(entryName);
        }

        if (options.AbsolutePathPolicy == AbsolutePathPolicy.Reject)
        {
            throw new InvalidDataException($"Entry '{entryName}' is absolute and AbsolutePathPolicy is Reject.");
        }

        var stripped = entryName;
        if (Path.IsPathRooted(stripped))
        {
            var root = Path.GetPathRoot(stripped);
            if (!string.IsNullOrEmpty(root) && stripped.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                stripped = stripped[root.Length..];
            }
        }

        stripped = stripped.Replace('\\', '/').TrimStart('/');
        return stripped.Length == 0
            ? throw new InvalidDataException($"Entry '{entryName}' resolves to an empty relative path after stripping root.")
            : PathPatternMatcher.NormalizeArchivePath(stripped);
    }

    private static bool IsAbsoluteArchivePath(string rawPath, string normalizedPath)
    {
        return Path.IsPathRooted(rawPath)
               || normalizedPath.StartsWith("/", StringComparison.Ordinal)
               || (normalizedPath.Length >= 2
                   && IsAsciiLetter(normalizedPath[0])
                   && normalizedPath[1] == ':');
    }

    private static bool IsAsciiLetter(char c) => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z');

    private static void TryDeleteFileSystemNode(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
            return;
        }

        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static void TryRestoreMtime(string path, long unixSeconds)
    {
        if (unixSeconds <= 0)
        {
            return;
        }

        var timestamp = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        if (Directory.Exists(path))
        {
            Directory.SetLastWriteTimeUtc(path, timestamp.UtcDateTime);
            return;
        }

        if (File.Exists(path))
        {
            File.SetLastWriteTimeUtc(path, timestamp.UtcDateTime);
        }
    }

    private static ArchiveEntry TransformEntry(ArchiveEntry entry, IReadOnlyDictionary<string, string> renameMap)
    {
        return new ArchiveEntry
        {
            Name = ApplyRename(entry.Name, renameMap),
            LinkName = entry.LinkName is null ? null : ApplyRename(entry.LinkName, renameMap),
            Data = entry.Data,
            Metadata = entry.Metadata,
        };
    }

    private static string ApplyRename(string path, IReadOnlyDictionary<string, string> renameMap)
    {
        var normalizedPath = PathPatternMatcher.NormalizeArchivePath(path);
        return renameMap.TryGetValue(normalizedPath, out var renamed) ? renamed : path.Replace('\\', '/');
    }

    private static IReadOnlyDictionary<string, string> NormalizeRenameMap(IReadOnlyDictionary<string, string> renameMap)
    {
        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in renameMap)
        {
            normalized[PathPatternMatcher.NormalizeArchivePath(key)] = PathPatternMatcher.NormalizeArchivePath(value);
        }

        return normalized;
    }

    private static IReadOnlyList<string> ResolveIncludePatterns(ExtractOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.PatternFilePath) || !File.Exists(options.PatternFilePath))
        {
            return options.IncludePatterns;
        }

        var merged = new List<string>(options.IncludePatterns);
        foreach (var line in File.ReadLines(options.PatternFilePath))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                merged.Add(trimmed);
            }
        }

        return merged;
    }

    private static bool ShouldInclude(
        string entryName,
        IReadOnlyList<string> includePatterns,
        IReadOnlyList<string> excludePatterns,
        bool nonMatching)
    {
        if (includePatterns.Count > 0)
        {
            var matched = PathPatternMatcher.MatchesAny(entryName, includePatterns);
            if (nonMatching ? matched : !matched)
            {
                return false;
            }
        }

        return excludePatterns.Count == 0 || !PathPatternMatcher.MatchesAny(entryName, excludePatterns);
    }
}