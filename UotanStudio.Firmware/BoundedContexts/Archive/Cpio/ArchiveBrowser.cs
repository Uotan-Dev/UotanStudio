using UotanStudio.Firmware.BoundedContexts.Archive.Cpio.Extraction;
using UotanStudio.Firmware.BoundedContexts.Archive.Cpio.Model;
using UotanStudio.Firmware.CrossCutting.Compress;
using SharpCompress.Archives;
using SharpCompress.Readers;

namespace UotanStudio.Firmware.BoundedContexts.Archive.Cpio;

/// <summary>Lists and extracts archives or compressed files without materializing payloads in memory.</summary>
public sealed class ArchiveBrowser : IDisposable
{
    private readonly FileStream _source;
    private readonly ArchiveFormat? _archiveFormat;
    private readonly IArchive? _sharpArchive;
    private readonly string _memberName;

    /// <summary>Maximum decompressed scratch-file size; the file is deleted when the browser closes.</summary>
    public const long MaximumExpandedBytes = 64L * 1024 * 1024 * 1024;

    /// <summary>A normalized member with no retained payload data.</summary>
    public sealed record Member(string Path, long Size, CpioFileType Kind);

    /// <summary>The detected container and optional compression format.</summary>
    public string FormatName { get; }

    /// <summary>Validated members in archive order.</summary>
    public IReadOnlyList<Member> Members { get; }

    private ArchiveBrowser(FileStream source, ArchiveFormat? archiveFormat, IArchive? sharpArchive,
        string memberName, string formatName, CancellationToken cancellationToken, long maximumMemberBytes)
    {
        _source = source;
        _archiveFormat = archiveFormat;
        _sharpArchive = sharpArchive;
        _memberName = memberName;
        FormatName = formatName;
        var members = new List<Member>();
        var nameCharacters = 0L;
        var expandedBytes = 0L;
        foreach (var entry in ReadEntries(cancellationToken))
        {
            if (entry.Name.Length == 0) continue;
            if (entry.DataLength > maximumMemberBytes)
                throw new InvalidDataException($"Archive member '{entry.Name}' exceeds the browsing limit.");
            try { expandedBytes = checked(expandedBytes + entry.DataLength); }
            catch (OverflowException) { throw new InvalidDataException("Archive contents exceed the browsing limit."); }
            if (expandedBytes > maximumMemberBytes)
                throw new InvalidDataException("Archive contents exceed the browsing limit.");
            nameCharacters += entry.Name.Length;
            if (members.Count >= 100_000 || nameCharacters > 8 * 1024 * 1024)
                throw new InvalidDataException("Archive directory exceeds the browsing limit.");
            members.Add(new(entry.Name, entry.DataLength, entry.Metadata.FileType));
        }
        Members = members;
    }

    /// <summary>Recognizes archive extensions and compression extensions supported by the backend.</summary>
    public static bool SupportsPath(string path) => Path.GetExtension(path).ToLowerInvariant() is ".cpio" or ".tar"
        or ".zip" or ".rar" or ".7z" or ".arj" or ".ace" or ".arc"
        or ".lzw" or ".z"
        || CompressionForPath(path) != CompressionFormat.None;

    private static CompressionFormat CompressionForPath(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".tgz" => CompressionFormat.Gzip,
        ".txz" => CompressionFormat.Xz,
        ".tbz" or ".tbz2" => CompressionFormat.Bzip2,
        ".tlz" => CompressionFormat.Lzma,
        ".tzst" => CompressionFormat.Zstd,
        _ => CompressionFormats.FromExtension(path)
    };

    /// <summary>Opens an archive, spooling at most one compression layer to a bounded temporary file.</summary>
    public static ArchiveBrowser Open(string path, CancellationToken cancellationToken = default,
        long maximumExpandedBytes = MaximumExpandedBytes)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            var compression = CompressionService.Detect(source);
            if (compression == CompressionFormat.None) compression = CompressionForPath(path);
            var memberName = Path.GetFileName(path);
            if (compression != CompressionFormat.None)
            {
                var scratch = new FileStream(Path.Combine(Path.GetTempPath(), $"uotan-browser-{Guid.NewGuid():N}.tmp"),
                    FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.DeleteOnClose);
                try { CompressionService.Decompress(source, scratch, compression, maximumExpandedBytes, cancellationToken); }
                catch { scratch.Dispose(); throw; }
                source.Dispose();
                source = scratch;
                source.Position = 0;
                memberName = Path.GetExtension(path).ToLowerInvariant() is ".tgz" or ".txz" or ".tbz" or ".tbz2" or ".tlz" or ".tzst"
                    ? Path.GetFileNameWithoutExtension(path) + ".tar" : Path.GetFileNameWithoutExtension(path);
                if (string.IsNullOrWhiteSpace(memberName)) memberName = "content";
            }

            // Common SharpCompress archives (ZIP, RAR, 7z, ARC, ARJ, ACE and LZW) are kept
            // open so individual entries can be streamed on demand instead of buffering them.
            IArchive? sharpArchive = null;
            if (IsSharpArchivePath(memberName))
            {
                source.Position = 0;
                try { sharpArchive = ArchiveFactory.OpenArchive(source, new ReaderOptions { LeaveStreamOpen = true }); }
                catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or ArgumentException)
                { throw new InvalidDataException("The archive is malformed or unsupported.", ex); }
                try
                {
                    return new(source, archiveFormat: null, sharpArchive, memberName,
                        sharpArchive.Type.ToString().ToUpperInvariant(), cancellationToken, maximumExpandedBytes);
                }
                catch
                {
                    sharpArchive.Dispose();
                    throw;
                }
            }

            ArchiveFormat? format = null;
            try { format = new CpioArchive().DetectFormat(source); }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
            {
                if (Path.GetExtension(memberName).Equals(".tar", StringComparison.OrdinalIgnoreCase))
                    format = ArchiveFormat.Tar;
                else if (compression == CompressionFormat.None || Path.GetExtension(memberName).Equals(".cpio", StringComparison.OrdinalIgnoreCase))
                    throw;
            }
            var container = format is ArchiveFormat.Tar or ArchiveFormat.Ustar ? "TAR" : format is not null ? "CPIO" : "File";
            return new(source, format, sharpArchive: null, memberName,
                compression == CompressionFormat.None ? container : $"{container} ({compression})",
                cancellationToken, maximumExpandedBytes);
        }
        catch { source.Dispose(); throw; }
    }

    /// <summary>Extracts the selected member or directory subtree using the archive backend's safe writer.</summary>
    public void Extract(string relativePath, string destination, CancellationToken cancellationToken = default)
    {
        var selected = Normalize(relativePath);
        var entries = ReadEntries(cancellationToken).Where(entry => entry.Name.Length > 0 &&
            (selected.Length == 0 || entry.Name == selected || entry.Name.StartsWith(selected + "/", StringComparison.Ordinal)));
        new CpioExtractor().ExtractToDirectory(entries, destination, new ExtractOptions
        {
            RestoreModificationTime = _archiveFormat is not null,
            RestorePermissions = _archiveFormat is not null
        });
    }

    private IEnumerable<ArchiveEntry> ReadEntries(CancellationToken cancellationToken)
    {
        if (_sharpArchive is not null)
        {
            foreach (var entry in _sharpArchive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(entry.Key)) continue;
                var name = Normalize(entry.Key);
                if (name.Length == 0) continue;
                var isDirectory = entry.IsDirectory;
                yield return new ArchiveEntry
                {
                    Name = name,
                    Data = [],
                    DataStreamFactory = isDirectory ? null : entry.OpenEntryStream,
                    DeclaredDataLength = isDirectory ? 0 : entry.Size,
                    Metadata = new ArchiveEntryMetadata
                    {
                        FileType = isDirectory ? CpioFileType.Directory : CpioFileType.Regular,
                        Mode = isDirectory ? 0x41EDu : 0x81A4u,
                        ModificationTimeUnixSeconds = entry.LastModifiedTime is { } modified
                            ? new DateTimeOffset(modified.ToUniversalTime()).ToUnixTimeSeconds() : 0
                    }
                };
            }
            yield break;
        }

        _source.Position = 0;
        if (_archiveFormat is null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ArchiveEntry { Name = Normalize(_memberName), Data = [], DataStream = _source,
                DeclaredDataLength = _source.Length, Metadata = new() { FileType = CpioFileType.Regular } };
            yield break;
        }
        foreach (var entry in new CpioArchive().ReadEntriesStreaming(_source, _archiveFormat))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ArchiveEntry { Name = Normalize(entry.Name), Metadata = entry.Metadata, Data = entry.Data,
                DataStream = entry.DataStream, DataStreamFactory = entry.DataStreamFactory,
                DeclaredDataLength = entry.DataLength, LinkName = entry.LinkName };
        }
    }

    private static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith("/", StringComparison.Ordinal)
            || path.StartsWith("\\", StringComparison.Ordinal)
            || Path.IsPathFullyQualified(path))
            throw new InvalidDataException($"Unsafe archive member path: '{path}'.");
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(part => part == ".." || part.Contains(':') || part.Contains('\0')))
            throw new InvalidDataException($"Unsafe archive member path: '{path}'.");
        return string.Join('/', parts.Where(part => part != "."));
    }

    /// <summary>Releases the source and deletes any decompressed scratch file.</summary>
    public void Dispose()
    {
        _sharpArchive?.Dispose();
        _source.Dispose();
    }

    private static bool IsSharpArchivePath(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() is
            ".zip" or ".rar" or ".7z" or ".arj" or ".ace" or ".arc"
            or ".lzw" or ".z";
    }
}
