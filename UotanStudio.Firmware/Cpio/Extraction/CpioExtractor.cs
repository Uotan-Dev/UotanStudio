using System.Text;
using UotanStudio.Firmware.Cpio.Internal;
using UotanStudio.Firmware.Cpio.Model;
using UotanStudio.Firmware.Common;

namespace UotanStudio.Firmware.Cpio.Extraction;

public sealed class CpioExtractor
{
    /// <summary>
    /// Extracts archive entries to a destination directory.
    /// <para>将归档条目解包到目标目录。</para>
    /// </summary>
    public void ExtractToDirectory(IEnumerable<ArchiveEntry> entries, string destinationDirectory, ExtractOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(destinationDirectory);

        var opts = options ?? new ExtractOptions();
        var destinationRoot = Path.GetFullPath(destinationDirectory);
        var volumeRoot = Path.GetPathRoot(destinationRoot)
            ?? throw new InvalidDataException($"Destination '{destinationDirectory}' has no filesystem root.");

        EnsureNoLinkAncestors(volumeRoot, destinationRoot);
        Directory.CreateDirectory(destinationRoot);
        EnsureNoLinkAncestors(volumeRoot, destinationRoot);
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

            var targetPath = BuildSafePath(destinationRoot, transformedEntry.Name, opts);
            EnsureNoLinkAncestors(destinationRoot, targetPath);
            EnsureParentDirectory(targetPath, opts.MakeDirectories);
            EnsureNoLinkAncestors(destinationRoot, targetPath);

            switch (transformedEntry.Metadata.FileType)
            {
                case CpioFileType.Directory:
                    Directory.CreateDirectory(targetPath);
                    break;
                case CpioFileType.SymbolicLink:
                    ExtractSymbolicLink(transformedEntry, targetPath, opts);
                    break;
                case CpioFileType.HardLink:
                    ExtractHardLink(transformedEntry, destinationRoot, targetPath, inodeMap, opts);
                    break;
                case CpioFileType.Fifo:
                case CpioFileType.CharacterDevice:
                case CpioFileType.BlockDevice:
                    ExtractSpecialFile(targetPath, opts);
                    break;
                case CpioFileType.Unknown:
                case CpioFileType.Regular:
                case CpioFileType.Socket:
                default:
                    ExtractRegularLikeFile(transformedEntry, targetPath, opts);
                    RegisterInodeMapping(transformedEntry, targetPath, inodeMap, opts.DeviceIndependentHardlinks);
                    break;
            }

            if (opts.RestoreModificationTime && transformedEntry.Metadata.FileType != CpioFileType.SymbolicLink)
            {

                TryRestoreMtime(targetPath, transformedEntry.Metadata.ModificationTimeUnixSeconds);
            }

            ApplyMetadataBestEffort(targetPath, transformedEntry, opts);
        }
    }

    private static void ExtractRegularLikeFile(ArchiveEntry entry, string targetPath, ExtractOptions options)
    {
        if (File.Exists(targetPath) && !options.Overwrite) return;
        FileExtraction.WriteAtomic(targetPath, output =>
        {
            if (entry.DataStream is not null || entry.DataStreamFactory is not null)
            {
                using var opened = entry.DataStream is null
                    ? entry.DataStreamFactory?.Invoke() ?? throw new InvalidOperationException($"Entry '{entry.Name}' stream factory returned null.")
                    : null;
                var source = entry.DataStream ?? opened!;
                if (source.CanSeek) source.Position = 0;
                CopyExactly(source, output, entry.DataLength, entry.Name);
                return;
            }
            ValidateInMemoryLength(entry);
            output.Write(entry.Data);
        }, options.Overwrite);
    }

    private static void ExtractSymbolicLink(ArchiveEntry entry, string targetPath, ExtractOptions options)
    {


        string? payloadTarget = null;
        if (entry.Data.Length > 0 || entry.DataStream is not null || entry.DataStreamFactory is not null)
        {
            payloadTarget = GetLinkText(entry);
            if (entry.LinkName is not null && !string.Equals(entry.LinkName, payloadTarget, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Symbolic link '{entry.Name}' target does not match its payload.");
            }
        }
        var linkTarget = entry.LinkName ?? payloadTarget ?? string.Empty;
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

            File.WriteAllBytes(targetPath, Encoding.UTF8.GetBytes(linkTarget));
        }
    }

    private static void ExtractHardLink(
        ArchiveEntry entry,
        string destinationRoot,
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
            var linkTargetPath = BuildSafePath(destinationRoot, entry.LinkName, options);
            EnsureNoLinkAncestors(destinationRoot, linkTargetPath);
            if (File.Exists(linkTargetPath) || Directory.Exists(linkTargetPath))
            {

                File.Copy(linkTargetPath, targetPath, overwrite: true);
                return;
            }
        }

        var key = GetInodeKey(entry, options.DeviceIndependentHardlinks);
        if (inodeMap.TryGetValue(key, out var existingPath))
        {

            File.Copy(existingPath, targetPath, overwrite: true);
            return;
        }


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


        }
    }

    private static void TrySetPermissions(string path, int permissions)
    {
        if (OperatingSystem.IsWindows())
        {

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

    private static void EnsureNoLinkAncestors(string root, string target)
    {
        var current = Path.GetFullPath(root);
        CheckExistingNodeIsNotLink(current);
        var relative = Path.GetRelativePath(current, Path.GetFullPath(target));
        foreach (var component in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            CheckExistingNodeIsNotLink(current);
            if (!File.Exists(current) && !Directory.Exists(current) && !NodeLooksLikeLink(current)) break;
        }
    }

    private static void CheckExistingNodeIsNotLink(string path)
    {
        if (NodeLooksLikeLink(path))
        {
            throw new InvalidDataException($"Extraction path '{path}' traverses a symbolic link or reparse point.");
        }
    }

    private static bool NodeLooksLikeLink(string path)
    {


        foreach (FileSystemInfo info in new FileSystemInfo[] { new FileInfo(path), new DirectoryInfo(path) })
        {
            try
            {
                if (info.LinkTarget is not null) return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { }

            try
            {


                var attributes = info.Attributes;
                if (attributes == (FileAttributes)(-1)) continue;
                if ((attributes & FileAttributes.ReparsePoint) != 0) return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { }
        }

        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { return false; }
    }

    private static void ValidateInMemoryLength(ArchiveEntry entry)
    {
        if (entry.DeclaredDataLength is { } declared && declared != entry.Data.LongLength)
        {
            throw new InvalidDataException($"Entry '{entry.Name}' declared length {declared} does not match its data length {entry.Data.LongLength}.");
        }
    }

    private static void CopyExactly(Stream source, Stream destination, long length, string entryName)
    {
        var buffer = new byte[81920];
        var remaining = length;
        while (remaining > 0)
        {
            var read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read == 0) throw new EndOfStreamException($"Entry '{entryName}' ended before its declared length.");
            destination.Write(buffer, 0, read);
            remaining -= read;
        }
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
            DataStream = entry.DataStream,
            DataStreamFactory = entry.DataStreamFactory,
            DeclaredDataLength = entry.DeclaredDataLength,
            Metadata = entry.Metadata,
        };
    }

    /// <summary>
    /// Resolves the symlink target text from ArchiveEntry.Data or, when the payload is streamed, from ArchiveEntry.DataStream.
    /// <para>从 ArchiveEntry.Data 或（负载为流式时）从 ArchiveEntry.DataStream 解析链接目标文本。</para>
    /// </summary>
    private static string GetLinkText(ArchiveEntry entry)
    {
        if (entry.Data.Length > 0)
        {
            ValidateInMemoryLength(entry);
            return Encoding.UTF8.GetString(entry.Data);
        }

        if (entry.DataStream is not null || entry.DataStreamFactory is not null)
        {
            using var ms = new MemoryStream();
            using var opened = entry.DataStream is null
                ? entry.DataStreamFactory?.Invoke() ?? throw new InvalidOperationException($"Entry '{entry.Name}' stream factory returned null.")
                : null;
            var source = entry.DataStream ?? opened!;
            if (source.CanSeek)
            {
                source.Position = 0;
            }
            CopyExactly(source, ms, entry.DataLength, entry.Name);
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        return string.Empty;
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