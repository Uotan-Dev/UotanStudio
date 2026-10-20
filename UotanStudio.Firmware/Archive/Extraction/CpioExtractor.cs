using System.Collections.Concurrent;
using System.Text;
using UotanStudio.Firmware.Archive.Internal;
using UotanStudio.Firmware.Archive.Models;
using UotanStudio.Firmware.Core;

namespace UotanStudio.Firmware.Archive.Extraction;

/// <summary>
/// Writes archive entries to a destination directory with path-traversal, symbolic-link ancestry
/// and absolute-path guards, dispatching each entry by its recorded file type.
/// <para>将归档条目写入目标目录，附带路径逃逸、符号链接祖先与绝对路径防护，
/// 并按条目记录的文件类型分派处理。</para>
/// </summary>
public sealed class CpioExtractor
{
    /// <summary>Extracts archive entries to a destination directory.
    /// <para>将归档条目解包到目标目录。</para></summary>
    /// <param name="entries">Entries consumed in enumeration order; payloads come from in-memory data, <see cref="ArchiveEntry.DataStream"/> or <see cref="ArchiveEntry.DataStreamFactory"/>. Streams opened from a factory are disposed by this call; a supplied <see cref="ArchiveEntry.DataStream"/> stays open and owned by the caller.
    /// <para>按枚举顺序消费的条目；负载来自内存数据、<see cref="ArchiveEntry.DataStream"/> 或 <see cref="ArchiveEntry.DataStreamFactory"/>。由工厂打开的流在本次调用中释放； 外部提供的 <see cref="ArchiveEntry.DataStream"/> 保持打开，所有权归调用方。</para></param>
    /// <param name="destinationDirectory">Directory resolved to a full path and created when missing; every write is confined beneath it.
    /// <para>解析为完整路径、缺失时创建的目标目录；所有写入都被约束在其内部。</para></param>
    /// <param name="options">Filtering, overwrite and restore behaviour; <see langword="null"/> uses the <see cref="ExtractOptions"/> defaults.
    /// <para>过滤、覆盖与还原行为选项；为 <see langword="null"/> 时使用 <see cref="ExtractOptions"/> 默认值。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="entries"/> or <paramref name="destinationDirectory"/> is <see langword="null"/>.
    /// <para><paramref name="entries"/> 或 <paramref name="destinationDirectory"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArchiveCorruptedException">An entry path escapes the destination, declares a negative or mismatched length, or is absolute under <see cref="AbsolutePathPolicy.Reject"/>.
    /// <para>条目路径逃出目标目录、声明长度为负或不匹配，或在 <see cref="AbsolutePathPolicy.Reject"/> 策略下为绝对路径。</para></exception>
    /// <exception cref="InvalidDataException">The destination has no filesystem root, or an extraction path traverses a symbolic link or reparse point.
    /// <para>目标目录没有文件系统根，或解包路径穿过符号链接或重解析点。</para></exception>
    /// <exception cref="EndOfStreamException">A payload stream ends before its declared length.
    /// <para>负载流在达到声明长度前提前结束。</para></exception>
    /// <exception cref="DirectoryNotFoundException">A parent directory is missing and <see cref="ExtractOptions.MakeDirectories"/> is <see langword="false"/>.
    /// <para>父目录不存在且 <see cref="ExtractOptions.MakeDirectories"/> 为 <see langword="false"/>。</para></exception>
    public void ExtractToDirectory(IEnumerable<ArchiveEntry> entries, string destinationDirectory, ExtractOptions? options = null)
        => ExtractToDirectory(entries, destinationDirectory, options, entryProgress: null, totalEntries: 0);

    /// <summary>
    /// Extracts entries and reports a throttled per-entry progress position against a caller-known total.
    /// <para>解包条目，并按调用方已知的总量上报经节流的逐条目进度。</para>
    /// </summary>
    internal void ExtractToDirectory(IEnumerable<ArchiveEntry> entries, string destinationDirectory,
        ExtractOptions? options, IProgress<(int Processed, int Total)>? entryProgress, int totalEntries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(destinationDirectory);

        var opts = options ?? new ExtractOptions();
        opts.CancellationToken.ThrowIfCancellationRequested();
        var destinationRoot = Path.GetFullPath(destinationDirectory);
        if (Path.GetPathRoot(destinationRoot) is null)
        {
            throw new InvalidDataException($"Destination '{destinationDirectory}' has no filesystem root.");
        }

        EnsureDestinationRootIsNotLinked(destinationRoot);
        Directory.CreateDirectory(destinationRoot);
        EnsureDestinationRootIsNotLinked(destinationRoot);
        var renameMap = NormalizeRenameMap(opts.RenameMap);
        var includePatterns = ResolveIncludePatterns(opts);
        var ancestors = new AncestorCache(destinationRoot);
        var progress = entryProgress is null || totalEntries <= 0
            ? null
            : new ProgressThrottle(totalEntries, new EntryCountProgress(entryProgress), opts.CancellationToken);

        var inodeMap = new Dictionary<(ulong Inode, uint DevMajor, uint DevMinor), string>();
        var inMemory = new List<PlannedEntry>();
        var processed = 0;
        foreach (var entry in entries)
        {
            opts.CancellationToken.ThrowIfCancellationRequested();
            processed++;
            progress?.Report((processed, totalEntries));
            var transformedEntry = TransformEntry(entry, renameMap);
            if (!ShouldInclude(transformedEntry.Name, includePatterns, opts.ExcludePatterns, opts.NonMatching))
            {
                continue;
            }

            var targetPath = BuildSafePath(destinationRoot, transformedEntry.Name, opts);

            // A payload reached through DataStream or DataStreamFactory is a view over the reader's own
            // source stream, which the reader advances as it enumerates, so it has to be consumed right
            // here while the stream still sits on this entry. Only entries whose bytes are already in
            // memory can be deferred to the parallel pass.
            ancestors.EnsureAncestors(targetPath, checkLeaf: false);
            EnsureParentDirectory(targetPath, opts.MakeDirectories);
            ancestors.EnsureAncestors(targetPath, checkLeaf: true);
            var item = new PlannedEntry(transformedEntry, targetPath);

            if (IsMaterializedInMemory(item.Entry))
            {
                if (item.Entry.Metadata.FileType is CpioFileType.Directory or CpioFileType.HardLink)
                {
                    ApplyOrderedEntry(item, destinationRoot, inodeMap, opts, ancestors);
                }
                else
                {
                    inMemory.Add(item);
                }

                continue;
            }

            if (item.Entry.Metadata.FileType is CpioFileType.Directory or CpioFileType.HardLink)
            {
                ApplyOrderedEntry(item, destinationRoot, inodeMap, opts, ancestors);
                continue;
            }

            WriteIndependentEntry(item, opts, ancestors);
            RegisterPlannedInode(item, inodeMap, opts.DeviceIndependentHardlinks);
        }

        if (inMemory.Count == 0)
        {
            progress?.Complete();
            return;
        }

        // Deferred entries may live beneath a directory entry that is itself deferred, so every
        // deferred entry makes sure its own parent exists before the parallel pass writes anything.
        foreach (var item in inMemory)
        {
            opts.CancellationToken.ThrowIfCancellationRequested();
            EnsureParentDirectory(item.TargetPath, opts.MakeDirectories);
        }

        var workers = FirmwareParallelism.Workers;
        if (workers > 1 && inMemory.Count > 1)
        {
            ExtractInMemoryEntriesInParallel(inMemory, opts, workers, ancestors);
            foreach (var item in inMemory)
            {
                RegisterPlannedInode(item, inodeMap, opts.DeviceIndependentHardlinks);
            }

            progress?.Complete();
            return;
        }

        foreach (var item in inMemory)
        {
            opts.CancellationToken.ThrowIfCancellationRequested();
            WriteIndependentEntry(item, opts, ancestors);
            RegisterPlannedInode(item, inodeMap, opts.DeviceIndependentHardlinks);
        }

        progress?.Complete();
    }

    /// <summary>
    /// Reports whether an entry already carries its payload bytes, so writing it needs no source access.
    /// <para>报告条目是否已携带其负载字节，从而写出时无需再访问来源。</para>
    /// </summary>
    /// <param name="entry">The archive entry to classify. <para>待分类的归档条目。</para></param>
    /// <returns>
    /// <see langword="true"/> when neither <see cref="ArchiveEntry.DataStream"/> nor
    /// <see cref="ArchiveEntry.DataStreamFactory"/> is set, which means <see cref="ArchiveEntry.Data"/> holds the
    /// bytes and no source stream has to be re-read; otherwise <see langword="false"/>.
    /// <para>当 <see cref="ArchiveEntry.DataStream"/> 与 <see cref="ArchiveEntry.DataStreamFactory"/> 均未设置时为
    /// <see langword="true"/>，此时 <see cref="ArchiveEntry.Data"/> 即负载字节、无需重读来源流；否则为
    /// <see langword="false"/>。</para>
    /// </returns>
    private static bool IsMaterializedInMemory(ArchiveEntry entry)
        => entry.DataStream is null && entry.DataStreamFactory is null;

    /// <summary>
    /// Writes one entry whose payload is already in memory, with bounded parallelism, and registers its
    /// inode afterwards in archive order so the hardlink mapping stays deterministic.
    /// <para>以有界并行度写出负载已在内存中的条目，并在之后按归档顺序登记其 inode，使硬链接映射保持确定性。</para>
    /// </summary>
    /// <param name="planned">Entries whose bytes are already in memory, in archive order.
    /// <para>负载字节已在内存中的条目，按归档顺序排列。</para></param>
    /// <param name="opts">The resolved extraction options. <para>已解析的提取选项。</para></param>
    /// <param name="workers">Resolved worker count taken from
    /// <see cref="FirmwareParallelism.Workers"/>.
    /// <para>取自 <see cref="FirmwareParallelism.Workers"/> 的已解析工作者数量。</para></param>
    /// <param name="ancestors">Shared ancestry cache that records the paths already validated against symbolic links.
    /// <para>共享的祖先缓存，记录已通过符号链接校验的路径。</para></param>
    private static void ExtractInMemoryEntriesInParallel(List<PlannedEntry> planned, ExtractOptions opts,
        int workers, AncestorCache ancestors)
    {
        Parallel.For(
            0, planned.Count,
            new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = opts.CancellationToken },
            slot =>
            {
                opts.CancellationToken.ThrowIfCancellationRequested();
                WriteIndependentEntry(planned[slot], opts, ancestors);
            });
    }

    /// <summary>
    /// Writes one entry whose payload is already in memory to its validated path, applying the same
    /// mtime and metadata restoration the serial extractor performs.
    /// <para>把负载已在内存中的单个条目写出到已校验的目标路径，并施加与串行解包器相同的 mtime 与元数据还原。</para>
    /// </summary>
    /// <param name="item">The planned entry. <para>已规划的条目。</para></param>
    /// <param name="opts">The resolved extraction options. <para>已解析的提取选项。</para></param>
    /// <param name="ancestors">Shared ancestry cache that records the paths already validated against symbolic links.
    /// <para>共享的祖先缓存，记录已通过符号链接校验的路径。</para></param>
    private static void WriteIndependentEntry(PlannedEntry item, ExtractOptions opts, AncestorCache ancestors)
    {
        var entry = item.Entry;
        switch (entry.Metadata.FileType)
        {
            case CpioFileType.SymbolicLink:
                ExtractSymbolicLink(entry, item.TargetPath, opts, ancestors);
                break;
            case CpioFileType.Fifo:
            case CpioFileType.CharacterDevice:
            case CpioFileType.BlockDevice:
                ExtractSpecialFile(item.TargetPath, opts, ancestors);
                break;
            default:
                ExtractRegularLikeFile(entry, item.TargetPath, opts);
                break;
        }

        if (opts.RestoreModificationTime && entry.Metadata.FileType != CpioFileType.SymbolicLink)
        {
            TryRestoreMtime(item.TargetPath, entry.Metadata.ModificationTimeUnixSeconds);
        }

        ApplyMetadataBestEffort(item.TargetPath, entry, opts);
    }

    /// <summary>
    /// Writes a directory or hard link in archive order, because the order is part of the archive's meaning.
    /// <para>按归档顺序写出目录或硬链接，因为其顺序本身属于归档语义的一部分。</para>
    /// </summary>
    /// <param name="item">The planned entry. <para>已规划的条目。</para></param>
    /// <param name="destinationRoot">Resolved destination root used for hardlink target resolution.
    /// <para>已解析的用于硬链接目标解析的目标根目录。</para></param>
    /// <param name="inodeMap">Inode registry filled in archive order.
    /// <para>按归档顺序填充的 inode 登记表。</para></param>
    /// <param name="opts">The resolved extraction options. <para>已解析的提取选项。</para></param>
    /// <param name="ancestors">Shared ancestry cache that records the paths already validated against symbolic links.
    /// <para>共享的祖先缓存，记录已通过符号链接校验的路径。</para></param>
    private static void ApplyOrderedEntry(
        PlannedEntry item,
        string destinationRoot,
        Dictionary<(ulong Inode, uint DevMajor, uint DevMinor), string> inodeMap,
        ExtractOptions opts,
        AncestorCache ancestors)
    {
        var entry = item.Entry;
        switch (entry.Metadata.FileType)
        {
            case CpioFileType.Directory:
                Directory.CreateDirectory(item.TargetPath);
                break;
            case CpioFileType.HardLink:
                ExtractHardLink(entry, destinationRoot, item.TargetPath, inodeMap, opts, ancestors);
                break;
            default:
                throw new InvalidOperationException("Only directories and hard links are order dependent.");
        }

        if (opts.RestoreModificationTime && entry.Metadata.FileType != CpioFileType.SymbolicLink)
        {
            TryRestoreMtime(item.TargetPath, entry.Metadata.ModificationTimeUnixSeconds);
        }

        ApplyMetadataBestEffort(item.TargetPath, entry, opts);
    }

    /// <summary>
    /// Records the inode of a written entry so a later hard link can copy from it.
    /// <para>登记已写出条目的 inode，使后续硬链接可以从中复制。</para>
    /// </summary>
    /// <param name="item">The planned entry that was written. <para>已写出的已规划条目。</para></param>
    /// <param name="inodeMap">Inode registry to fill. <para>待填充的 inode 登记表。</para></param>
    /// <param name="deviceIndependent">Whether the device number is part of the inode key.
    /// <para>设备号是否属于 inode 键的一部分。</para></param>
    private static void RegisterPlannedInode(
        PlannedEntry item,
        Dictionary<(ulong Inode, uint DevMajor, uint DevMinor), string> inodeMap,
        bool deviceIndependent)
    {
        var entry = item.Entry;
        if (entry.Metadata.Inode == 0)
        {
            return;
        }

        var key = deviceIndependent
            ? (entry.Metadata.Inode, 0, 0)
            : (entry.Metadata.Inode, entry.Metadata.DeviceMajor, entry.Metadata.DeviceMinor);
        inodeMap[key] = item.TargetPath;
    }

    /// <summary>
    /// One planned archive entry together with the already validated destination path computed for it.
    /// <para>单个已规划的归档条目，连同为其计算并校验过的目标路径。</para>
    /// </summary>
    private sealed class PlannedEntry
    {
        internal PlannedEntry(ArchiveEntry entry, string targetPath)
        {
            Entry = entry;
            TargetPath = targetPath;
        }

        internal ArchiveEntry Entry { get; }

        internal string TargetPath { get; }
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
                CopyExactly(source, output, entry.DataLength, entry.Name, options.CancellationToken);
                return;
            }
            ValidateInMemoryLength(entry);
            options.CancellationToken.ThrowIfCancellationRequested();
            output.Write(entry.Data);
        }, options.Overwrite, options.CancellationToken);
    }

    private static void ExtractSymbolicLink(ArchiveEntry entry, string targetPath, ExtractOptions options,
        AncestorCache ancestors)
    {


        string? payloadTarget = null;
        if (entry.Data.Length > 0 || entry.DataStream is not null || entry.DataStreamFactory is not null)
        {
            payloadTarget = GetLinkText(entry, options.CancellationToken);
            if (entry.LinkName is not null && !string.Equals(entry.LinkName, payloadTarget, StringComparison.Ordinal))
            {
                throw new ArchiveCorruptedException($"Symbolic link '{entry.Name}' target does not match its payload.");
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

        ancestors.Invalidate(targetPath);
    }

    private static void ExtractHardLink(
        ArchiveEntry entry,
        string destinationRoot,
        string targetPath,
        Dictionary<(ulong Inode, uint DevMajor, uint DevMinor), string> inodeMap,
        ExtractOptions options,
        AncestorCache ancestors)
    {
        if (File.Exists(targetPath) || Directory.Exists(targetPath))
        {
            if (!options.Overwrite)
            {
                return;
            }

            TryDeleteFileSystemNode(targetPath);
            ancestors.Invalidate(targetPath);
        }

        if (entry.LinkName is { Length: > 0 })
        {
            var linkTargetPath = BuildSafePath(destinationRoot, entry.LinkName, options);
            ancestors.EnsureAncestors(linkTargetPath, checkLeaf: true);
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

    private static void ExtractSpecialFile(string targetPath, ExtractOptions options, AncestorCache ancestors)
    {
        if (File.Exists(targetPath) || Directory.Exists(targetPath))
        {
            if (!options.Overwrite)
            {
                return;
            }

            TryDeleteFileSystemNode(targetPath);
            ancestors.Invalidate(targetPath);
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
            ? throw new ArchiveCorruptedException($"Entry '{entryName}' escapes destination directory.")
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

    /// <summary>
    /// Validates that the destination root is not reached through a symbolic link. The check starts at the deepest
    /// existing non-link directory, so a system-level link above the destination root (macOS resolves <c>/var</c> to
    /// <c>/private/var</c>) is not treated as a violation while a link at or below the destination root still is.
    /// <para>
    /// 校验目标根目录不是经由符号链接抵达的。检查从已存在的最深非链接目录开始，因此目标根之上的系统级链接
    /// （macOS 上 <c>/var</c> 指向 <c>/private/var</c>）不会误伤，而目标根本身或其下方的链接仍会被拒绝。
    /// </para>
    /// </summary>
    private static void EnsureDestinationRootIsNotLinked(string destinationRoot)
    {
        EnsureNoLinkAncestors(DeepestExistingNonLinkDirectory(destinationRoot), destinationRoot);
    }

    /// <summary>
    /// Returns the deepest directory that exists and is not itself a symbolic link, starting at <paramref name="path"/>.
    /// </summary>
    private static string DeepestExistingNonLinkDirectory(string path)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            if (!Directory.Exists(current) || NodeLooksLikeLink(current))
            {
                continue;
            }
            return current;
        }
        return Path.GetPathRoot(path) ?? path;
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
            throw new ArchiveCorruptedException($"Entry '{entry.Name}' declared length {declared} does not match its data length {entry.Data.LongLength}.");
        }
    }

    private static void CopyExactly(Stream source, Stream destination, long length, string entryName,
        CancellationToken cancellationToken = default)
    {
        const int chunkSize = 81920;
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(chunkSize);
        try
        {
            var remaining = length;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = source.Read(buffer, 0, (int)Math.Min(chunkSize, remaining));
                cancellationToken.ThrowIfCancellationRequested();
                if (read == 0) throw new EndOfStreamException($"Entry '{entryName}' ended before its declared length.");
                destination.Write(buffer, 0, read);
                remaining -= read;
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
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
            throw new ArchiveCorruptedException($"Entry '{entryName}' is absolute and AbsolutePathPolicy is Reject.");
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
            ? throw new ArchiveCorruptedException($"Entry '{entryName}' resolves to an empty relative path after stripping root.")
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
    private static string GetLinkText(ArchiveEntry entry, CancellationToken cancellationToken)
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
            CopyExactly(source, ms, entry.DataLength, entry.Name, cancellationToken);
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

    private sealed class AncestorCache
    {
        private readonly string _root;
        private readonly StringComparison _comparison;
        private readonly ConcurrentDictionary<string, bool> _verified;

        public AncestorCache(string destinationRoot)
        {
            _root = Path.GetFullPath(destinationRoot);
            _comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            _verified = new ConcurrentDictionary<string, bool>(
                _comparison == StringComparison.OrdinalIgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            CheckExistingNodeIsNotLink(_root);
            _verified[_root] = true;
        }

        public void EnsureAncestors(string targetPath, bool checkLeaf)
        {
            var relative = Path.GetRelativePath(_root, Path.GetFullPath(targetPath));
            if (relative.Length == 0 || relative == ".")
            {
                return;
            }

            var components = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
            var count = checkLeaf ? components.Length : components.Length - 1;
            var current = _root;
            for (var i = 0; i < count; i++)
            {
                current = Path.Combine(current, components[i]);
                if (_verified.ContainsKey(current))
                {
                    continue;
                }

                CheckExistingNodeIsNotLink(current);
                if (!File.Exists(current) && !Directory.Exists(current) && !NodeLooksLikeLink(current))
                {
                    break;
                }

                _verified[current] = true;
            }
        }

        public void Invalidate(string path)
        {
            var full = Path.GetFullPath(path);
            var prefix = full + Path.DirectorySeparatorChar;
            foreach (var key in _verified.Keys)
            {
                if (key.Equals(full, _comparison) || key.StartsWith(prefix, _comparison))
                {
                    _verified.TryRemove(key, out _);
                }
            }
        }
    }
}