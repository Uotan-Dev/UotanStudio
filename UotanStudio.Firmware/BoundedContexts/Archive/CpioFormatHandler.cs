using System.Text;
using UotanStudio.Firmware.BoundedContexts.Archive.Extraction;
using UotanStudio.Firmware.BoundedContexts.Archive.Formats.Cpio;
using UotanStudio.Firmware.BoundedContexts.Archive.Models;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Metadata;
using UotanStudio.Firmware.SharedKernel;
using UotanStudio.Firmware.SharedKernel.Repacking;

namespace UotanStudio.Firmware.BoundedContexts.Archive;

/// <summary>
/// Format handler for CPIO and TAR archives used as firmware root filesystem images.
/// <para>用作固件根文件系统镜像的 CPIO 与 TAR 归档格式处理器。</para>
/// </summary>
public sealed class CpioFormatHandler : IFormatHandler, IRepackMetadataProvider
{
    void IRepackMetadataProvider.ConfigureRepackMetadata(RepackMetadata metadata, string sourcePath, string outputDirectory)
    {
        metadata.CanPackDirectory = true;
        metadata.PackingNotes = " The directory packer writes newc CPIO.";
    }

    private readonly CpioArchive _archive = new();

    /// <summary>
    /// Display name of the handled format family.
    /// <para>所处理格式家族的显示名称。</para>
    /// </summary>
    /// <value>
    /// The fixed string <c>"CPIO/TAR Archive"</c>.
    /// <para>固定字符串 <c>"CPIO/TAR Archive"</c>。</para>
    /// </value>
    public string FormatName => "CPIO/TAR Archive";

    /// <summary>
    /// File extensions this handler advertises for archive images.
    /// <para>该处理器为归档镜像声明的文件扩展名。</para>
    /// </summary>
    /// <value>
    /// <c>cpio</c> and <c>tar</c>; an extension alone never claims a match, content probing in <see cref="CanHandle"/> decides.
    /// <para>依次为 <c>cpio</c>、<c>tar</c>；仅凭扩展名不构成匹配，由 <see cref="CanHandle"/> 的内容探测决定。</para>
    /// </value>
    public IReadOnlyList<string> FileExtensions { get; } = ["cpio", "tar"];

    /// <summary>
    /// Determines whether this handler can process the given file.
    /// <para>判断该处理器能否处理指定文件；依据 .cpio/.tar 扩展名或魔数（'070701' newc、'070702' crc、'070707' odc/二进制，以及偏移 257 处的 'ustar'）进行判断。</para>
    /// </summary>
    /// <param name="filePath">
    /// Path to probe; a missing or blank path never matches, and read errors are treated as a non-match.
    /// <para>待探测的路径；不存在或为空的路径均不匹配，读取错误同样视为不匹配。</para>
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the extension is <c>.cpio</c>/<c>.tar</c> or the leading bytes carry a known CPIO or ustar magic.
    /// <para>当扩展名为 <c>.cpio</c>/<c>.tar</c>，或起始字节携带已知 CPIO 或 ustar 魔数时返回 <see langword="true"/>。</para>
    /// </returns>
    public bool CanHandle(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }

        var extension = Path.GetExtension(filePath);
        if (extension.Equals(".cpio", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".tar", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            using var fs = File.OpenRead(filePath);
            Span<byte> header = stackalloc byte[512];
            var read = fs.Read(header);
            if (read < 6)
            {
                return false;
            }

            var ascii = Encoding.ASCII.GetString(header[..6]);
            if (ascii is "070701" or "070702" or "070707")
            {
                return true;
            }

            if (read >= 2)
            {
                var magic16 = (ushort)((header[0] << 8) | header[1]);
                var swapped = (ushort)((header[1] << 8) | header[0]);
                if (magic16 == 0x71C7 || swapped == 0x71C7)
                {
                    return true;
                }
            }


            if (read >= 262)
            {
                var marker = Encoding.ASCII.GetString(header[257..262]);
                if (marker == "ustar")
                {
                    return true;
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    /// <summary>
    /// Reads format metadata without unpacking.
    /// <para>读取格式元数据（不解包）。</para>
    /// </summary>
    /// <param name="filePath">
    /// Archive file to enumerate; parsing runs on the thread pool.
    /// <para>要枚举的归档文件；解析在线程池上执行。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancel token checked before the run and between listed entries.
    /// <para>在运行前及各条目列举之间检查的取消令牌。</para>
    /// </param>
    /// <returns>
    /// An <see cref="FirmwareOpResult"/> with the detected format, entry count, names and sizes on success, or a failure result when the archive cannot be listed; parse errors are captured into the result.
    /// <para>成功时返回包含检测格式、条目数量、名称与大小的 <see cref="FirmwareOpResult"/>；归档无法列举时返回失败结果；解析错误被捕获进结果。</para>
    /// </returns>
    /// <exception cref="OperationCanceledException">
    /// The token was cancelled.
    /// <para>令牌已被取消。</para>
    /// </exception>
    public Task<FirmwareOpResult> ReadMetadataAsync(string filePath, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var summaries = new List<ArchiveEntrySummary>();
                using (var fs = File.OpenRead(filePath))
                {
                    foreach (var summary in _archive.ListEntries(fs))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        summaries.Add(summary);
                    }
                }

                var metadata = new Dictionary<string, string>
                {
                    ["Format"] = DetectFormatName(filePath),
                    ["EntryCount"] = summaries.Count.ToString(),
                };

                var components = summaries
                    .Select(s => FirmwareComponent.Of(s.Name, s.DataLength))
                    .ToList();

                return FirmwareOpResult.Ok(
                    $"{summaries.Count} entries in '{filePath}'.",
                    [.. summaries.Select(s => s.Name)],
                    metadata,
                    components);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return FirmwareOpResult.Fail($"Failed to read metadata from '{filePath}': {ex.Message}");
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Unpacks the firmware file to the output directory.
    /// <para>将固件文件解包到输出目录。</para>
    /// </summary>
    /// <param name="filePath">
    /// CPIO or TAR archive to extract; the archive itself is not modified.
    /// <para>要解包的 CPIO 或 TAR 归档；归档本身不会被修改。</para>
    /// </param>
    /// <param name="outputDirectory">
    /// Destination directory for the extracted entries.
    /// <para>解出条目的目标目录。</para>
    /// </param>
    /// <param name="options">
    /// Optional extraction settings; only its overwrite flag is consulted here, defaulting to overwrite enabled.
    /// <para>可选解包设置；此处仅参考其覆盖标志，默认允许覆盖。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancel token checked before extraction and honored by the underlying task scheduling.
    /// <para>在解包前检查并被底层任务调度遵守的取消令牌。</para>
    /// </param>
    /// <returns>
    /// An <see cref="FirmwareOpResult"/> listing the extracted entry names on success, or a failure result when extraction throws; only <see cref="OperationCanceledException"/> escapes.
    /// <para>成功时返回列出已解出条目名的 <see cref="FirmwareOpResult"/>；解包抛出异常时返回失败结果；仅 <see cref="OperationCanceledException"/> 会向外抛出。</para>
    /// </returns>
    /// <exception cref="OperationCanceledException">
    /// The token was cancelled.
    /// <para>令牌已被取消。</para>
    /// </exception>
    public Task<FirmwareOpResult> UnpackAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
        => UotanStudio.Firmware.SharedKernel.Repacking.RepackMetadata.UnpackAsync(this, filePath, outputDirectory, options,
            cancellationToken, () => UnpackCoreAsync(filePath, outputDirectory, options, cancellationToken));

    private Task<FirmwareOpResult> UnpackCoreAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var extractOptions = new ExtractOptions { Overwrite = options?.Overwrite ?? true };
                IReadOnlyList<string> entries;
                using (var fs = File.OpenRead(filePath))
                {
                    _archive.ExtractToDirectory(fs, outputDirectory, extractOptions, null, out entries);
                }

                return FirmwareOpResult.Ok(
                    $"Extracted {entries.Count} entries from '{filePath}' to '{outputDirectory}'.",
                    entries);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return FirmwareOpResult.Fail($"Failed to unpack '{filePath}': {ex.Message}");
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Packs a directory into a newc cpio archive.
    /// <para>将目录打包为 newc cpio 归档。</para>
    /// </summary>
    /// <param name="inputPath">
    /// Directory tree to enumerate; symlinked entries are stored as symbolic links and repack metadata files are skipped.
    /// <para>要枚举的目录树；符号链接条目以符号链接形式存储，重打包元数据文件被跳过。</para>
    /// </param>
    /// <param name="outputPath">
    /// Destination archive file, created (or truncated) directly rather than atomically.
    /// <para>目标归档文件，直接创建（或截断）而非原子提交。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancel token checked before enumeration and before writing; the write loop itself is synchronous.
    /// <para>在枚举前与写入前检查的取消令牌；写入循环本身为同步执行。</para>
    /// </param>
    /// <returns>
    /// An <see cref="FirmwareOpResult"/> listing the packed entry names on success, or a failure result when the input is not a directory or a source file changes during packing; only <see cref="OperationCanceledException"/> escapes.
    /// <para>成功时返回列出已打包条目名的 <see cref="FirmwareOpResult"/>；输入不是目录或打包期间源文件变化时返回失败结果；仅 <see cref="OperationCanceledException"/> 会向外抛出。</para>
    /// </returns>
    /// <exception cref="OperationCanceledException">
    /// The token was cancelled.
    /// <para>令牌已被取消。</para>
    /// </exception>
    public Task<FirmwareOpResult> PackAsync(string inputPath, string outputPath, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!Directory.Exists(inputPath))
                {
                    return FirmwareOpResult.Fail($"Input path is not a directory: '{inputPath}'.");
                }

                var fsConfig = FileSystemMetadataOverrides.LoadFsConfig(
                    RepackMetadata.DiscoverMetadataFile(inputPath, "_fs_config"));
                var entries = EnumerateDirectoryEntries(inputPath, fsConfig);
                cancellationToken.ThrowIfCancellationRequested();

                using (var fs = File.Create(outputPath))
                {
                    _archive.SaveAll(fs, entries, ArchiveFormat.NewAscii);
                }

                return FirmwareOpResult.Ok(
                    $"Packed {entries.Count} entries from '{inputPath}' into '{outputPath}' (newc).",
                    [.. entries.Select(e => e.Name)]);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return FirmwareOpResult.Fail($"Failed to pack '{inputPath}': {ex.Message}");
            }
        }, cancellationToken);
    }

    private string DetectFormatName(string filePath)
    {
        using var fs = File.OpenRead(filePath);
        return _archive.DetectFormat(fs).ToString();
    }

    private static List<ArchiveEntry> EnumerateDirectoryEntries(string sourceRoot, IReadOnlyDictionary<string, FsConfigEntry> fsConfig)
    {
        var rootFull = Path.GetFullPath(sourceRoot);
        var result = new List<ArchiveEntry>();
        var pending = new Stack<string>();
        pending.Push(rootFull);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var directoryPath in Directory.EnumerateDirectories(current))
            {
                if (IsSymlink(directoryPath))
                {
                    result.Add(BuildSymlinkEntry(directoryPath, rootFull));
                    continue;
                }

                pending.Push(directoryPath);
                result.Add(BuildDirectoryEntry(directoryPath, rootFull, fsConfig));
            }

            foreach (var filePath in Directory.EnumerateFiles(current))
            {
                if (RepackMetadata.IsMetadataFile(filePath)) continue;
                if (IsSymlink(filePath))
                {
                    result.Add(BuildSymlinkEntry(filePath, rootFull));
                    continue;
                }

                result.Add(BuildRegularFileEntry(filePath, rootFull, fsConfig));
            }
        }

        return result;
    }

    private static ArchiveEntry BuildDirectoryEntry(string fullPath, string sourceRoot, IReadOnlyDictionary<string, FsConfigEntry> fsConfig)
    {
        var permissions = GetUnixPermissions(fullPath, fallback: 0x1ED);
        uint uid = 0, gid = 0;
        if (FileSystemMetadataOverrides.TryGetFsConfig(fsConfig, ToArchiveRelativePath(fullPath, sourceRoot), out var entry))
        {
            uid = entry.Uid;
            gid = entry.Gid;
            permissions = entry.Mode & 0xFFF;
        }

        return new ArchiveEntry
        {
            Name = ToArchiveRelativePath(fullPath, sourceRoot),
            LinkName = null,
            Data = [],
            Metadata = new ArchiveEntryMetadata
            {
                Mode = CpioFieldParser.ToMode(CpioFileType.Directory, permissions),
                UserId = uid,
                GroupId = gid,
                LinkCount = 1,
                ModificationTimeUnixSeconds = new DateTimeOffset(Directory.GetLastWriteTimeUtc(fullPath)).ToUnixTimeSeconds(),
                FileType = CpioFileType.Directory,
                UnixPermissions = permissions,
            },
        };
    }

    private static ArchiveEntry BuildRegularFileEntry(string fullPath, string sourceRoot, IReadOnlyDictionary<string, FsConfigEntry> fsConfig)
    {
        var permissions = GetUnixPermissions(fullPath, fallback: 0x1A4);
        uint uid = 0, gid = 0;
        if (FileSystemMetadataOverrides.TryGetFsConfig(fsConfig, ToArchiveRelativePath(fullPath, sourceRoot), out var entry))
        {
            uid = entry.Uid;
            gid = entry.Gid;
            permissions = entry.Mode & 0xFFF;
        }

        var info = new FileInfo(fullPath);
        var expectedLength = info.Length;
        var expectedWriteTime = info.LastWriteTimeUtc;
        var expectedCreationTime = info.CreationTimeUtc;


        Stream OpenValidatedSource()
        {
            var before = new FileInfo(fullPath);
            ValidateSourceIdentity(before, expectedLength, expectedWriteTime, expectedCreationTime);
            var source = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
            try
            {
                if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException($"Source file '{fullPath}' became a symbolic link or reparse point.");
                var after = new FileInfo(fullPath);
                ValidateSourceIdentity(after, expectedLength, expectedWriteTime, expectedCreationTime);
                if (source.Length != expectedLength)
                    throw new InvalidDataException($"Source file '{fullPath}' changed after enumeration.");
                return source;
            }
            catch
            {
                source.Dispose();
                throw;
            }
        }

        return new ArchiveEntry
        {
            Name = ToArchiveRelativePath(fullPath, sourceRoot),
            LinkName = null,
            Data = [],
            DeclaredDataLength = expectedLength,
            DataStreamFactory = OpenValidatedSource,
            Metadata = new ArchiveEntryMetadata
            {
                Mode = CpioFieldParser.ToMode(CpioFileType.Regular, permissions),
                LinkCount = 1,
                ModificationTimeUnixSeconds = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds(),
                FileType = CpioFileType.Regular,
                UnixPermissions = permissions,
            },
        };
    }

    private static ArchiveEntry BuildSymlinkEntry(string fullPath, string sourceRoot)
    {
        FileSystemInfo info = Directory.Exists(fullPath) ? new DirectoryInfo(fullPath) : new FileInfo(fullPath);
        var linkTarget = info.LinkTarget ?? string.Empty;
        return new ArchiveEntry
        {
            Name = ToArchiveRelativePath(fullPath, sourceRoot),
            LinkName = linkTarget,
            Data = [],
            Metadata = new ArchiveEntryMetadata
            {
                Mode = 0xA000U | 0x1FFU,
                LinkCount = 1,
                ModificationTimeUnixSeconds = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds(),
                FileType = CpioFileType.SymbolicLink,
                UnixPermissions = 0x1FF,
            },
        };
    }

    private static void ValidateSourceIdentity(FileInfo info, long length, DateTime writeTimeUtc, DateTime creationTimeUtc)
    {
        info.Refresh();
        if (!info.Exists || info.Length != length || info.LastWriteTimeUtc != writeTimeUtc || info.CreationTimeUtc != creationTimeUtc
            || (info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"Source file '{info.FullName}' changed after enumeration.");
        }
    }

    private static bool IsSymlink(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static int GetUnixPermissions(string path, int fallback)
    {
        if (OperatingSystem.IsWindows())
        {
            return fallback;
        }

        try
        {
            return (int)File.GetUnixFileMode(path) & 0x0FFF;
        }
        catch
        {
            return fallback;
        }
    }

    private static string ToArchiveRelativePath(string fullPath, string sourceRoot) => Path.GetRelativePath(sourceRoot, fullPath).Replace('\\', '/');
}