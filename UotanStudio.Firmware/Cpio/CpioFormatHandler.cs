using System.Text;
using UotanStudio.Firmware.Common;
using UotanStudio.Firmware.Cpio.Extraction;
using UotanStudio.Firmware.Cpio.Formats.Cpio;
using UotanStudio.Firmware.Cpio.Model;

namespace UotanStudio.Firmware.Cpio;

/// <summary>
/// Format handler for CPIO and TAR archives.
/// <para>CPIO 与 TAR 归档的格式处理器。</para>
/// </summary>
public sealed class CpioFormatHandler : IFormatHandler
{
    private readonly CpioArchive _archive = new();

    /// <summary>
    /// Gets the human-readable format name.
    /// <para>获取人类可读的格式名称。</para>
    /// </summary>
    public string FormatName => "CPIO/TAR Archive";

    /// <summary>
    /// Gets the typical file extensions (without dot, lowercase).
    /// <para>获取典型文件扩展名（不带点，小写）。</para>
    /// </summary>
    public IReadOnlyList<string> FileExtensions { get; } = ["cpio", "tar"];

    /// <summary>
    /// Determines whether this handler can process the given file.
    /// Detection is based on the .cpio/.tar extension or magic bytes:
    /// '070701' (newc), '070702' (crc), '070707' (odc/binary) and 'ustar' at offset 257.
    /// <para>判断该处理器能否处理指定文件；依据 .cpio/.tar 扩展名或魔数（'070701' newc、'070702' crc、'070707' odc/二进制，以及偏移 257 处的 'ustar'）进行判断。</para>
    /// </summary>
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

            // POSIX ustar marker at offset 257.
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

                return FirmwareOpResult.Ok(
                    $"{summaries.Count} entries in '{filePath}'.",
                    [.. summaries.Select(s => s.Name)],
                    metadata);
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
    public Task<FirmwareOpResult> UnpackAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var extractOptions = new ExtractOptions { Overwrite = options?.Overwrite ?? true };
                using (var fs = File.OpenRead(filePath))
                {
                    _archive.ExtractToDirectory(fs, outputDirectory, extractOptions);
                }

                var entries = new List<string>();
                using (var listing = File.OpenRead(filePath))
                {
                    foreach (var summary in _archive.ListEntries(listing))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        entries.Add(summary.Name);
                    }
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

                var entries = EnumerateDirectoryEntries(inputPath);
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

    private static List<ArchiveEntry> EnumerateDirectoryEntries(string sourceRoot)
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
                result.Add(BuildDirectoryEntry(directoryPath, rootFull));
            }

            foreach (var filePath in Directory.EnumerateFiles(current))
            {
                if (IsSymlink(filePath))
                {
                    result.Add(BuildSymlinkEntry(filePath, rootFull));
                    continue;
                }

                result.Add(BuildRegularFileEntry(filePath, rootFull));
            }
        }

        return result;
    }

    private static ArchiveEntry BuildDirectoryEntry(string fullPath, string sourceRoot)
    {
        var permissions = GetUnixPermissions(fullPath, fallback: 0x1ED);
        return new ArchiveEntry
        {
            Name = ToArchiveRelativePath(fullPath, sourceRoot),
            LinkName = null,
            Data = [],
            Metadata = new ArchiveEntryMetadata
            {
                Mode = CpioFieldParser.ToMode(CpioFileType.Directory, permissions),
                LinkCount = 1,
                ModificationTimeUnixSeconds = new DateTimeOffset(Directory.GetLastWriteTimeUtc(fullPath)).ToUnixTimeSeconds(),
                FileType = CpioFileType.Directory,
                UnixPermissions = permissions,
            },
        };
    }

    private static ArchiveEntry BuildRegularFileEntry(string fullPath, string sourceRoot)
    {
        var permissions = GetUnixPermissions(fullPath, fallback: 0x1A4);
        var info = new FileInfo(fullPath);
        return new ArchiveEntry
        {
            Name = ToArchiveRelativePath(fullPath, sourceRoot),
            LinkName = null,
            Data = File.ReadAllBytes(fullPath),
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